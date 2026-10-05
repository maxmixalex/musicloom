using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MusicLoom.AudioFingerprint;

public static class NeteaseFingerprint
{
    public const int SampleRate = 8_000;
    public const int DurationSeconds = 3;
    public const int RequiredSampleCount = SampleRate * DurationSeconds;

    private const int WindowSize = 2_048;
    private const int HopSize = 160;
    private const int LowBin = 25;   // floor(100 Hz / (8000 / 2048))
    private const int HighBin = 1_024;
    private const int BandBins = HighBin - LowBin;
    private const int MinimumFrameCount = 10;
    private const float MinimumMagnitude = 1.11920929e-6f;
    private const int FinalFrequencyRadius = 30;
    private const int FinalTimeRadius = 8;
    private const int AverageFrequencyRadius = 10;
    private const int AverageTimeRadius = 5;
    private const double AverageThreshold = 1.0;
    private const string Version = "hyai_1.2.0_client_1.0.0";
    private static readonly byte[] AesKey = Encoding.ASCII.GetBytes("4B97221F27F02907");
    private static readonly float[] HammingWindow = BuildHammingWindow();
    private static readonly int[] BitReversedIndices = BuildBitReversedIndices();

    public static FingerprintResult Generate(ReadOnlySpan<float> samples)
    {
        if (samples.Length != RequiredSampleCount)
        {
            throw new ArgumentException(
                $"Netease fingerprinting requires exactly {RequiredSampleCount} mono float32 samples at {SampleRate} Hz.",
                nameof(samples));
        }

        FingerprintPeak[] peaks = ExtractPeaks(samples);
        byte[] raw = BuildRawFingerprint(DurationSeconds, peaks);
        byte[] compressed = ZlibStore(raw);
        byte[] encrypted = EncryptAes128EcbPkcs7(compressed);
        return new FingerprintResult(Convert.ToBase64String(encrypted), peaks);
    }

    public static FingerprintPeak[] ExtractPeaks(ReadOnlySpan<float> samples)
    {
        if (samples.Length < WindowSize)
        {
            return [];
        }

        int frameCount = ((samples.Length - WindowSize) / HopSize) + 1;
        if (frameCount < MinimumFrameCount)
        {
            return [];
        }

        float[] matrix = BuildFeatureMatrix(samples, frameCount);
        List<InternalPeak> candidates = [];

        for (int frequency = 0; frequency < BandBins; frequency++)
        {
            for (int time = 0; time < frameCount; time++)
            {
                if (!HasGreaterInNeighborhood(matrix, frameCount, frequency, time, 1, 1))
                {
                    candidates.Add(new InternalPeak(frequency, time, matrix[Index(frequency, time, frameCount)]));
                }
            }
        }

        candidates = FilterByLocalAverage(candidates, matrix, frameCount);
        candidates.RemoveAll(peak => HasGreaterInNeighborhood(
            matrix,
            frameCount,
            peak.FrequencyBin,
            peak.TimeFrame,
            FinalFrequencyRadius,
            FinalTimeRadius));

        FingerprintPeak[] result = new FingerprintPeak[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            InternalPeak peak = candidates[i];
            result[i] = new FingerprintPeak(peak.FrequencyBin + LowBin, peak.TimeFrame, peak.Amplitude);
        }

        Array.Sort(result, static (left, right) =>
        {
            int timeComparison = left.TimeFrame.CompareTo(right.TimeFrame);
            return timeComparison != 0 ? timeComparison : left.FrequencyBin.CompareTo(right.FrequencyBin);
        });
        return result;
    }

    private static float[] BuildFeatureMatrix(ReadOnlySpan<float> samples, int frameCount)
    {
        float[] matrix = new float[BandBins * frameCount];
        double[] real = new double[WindowSize];
        double[] imaginary = new double[WindowSize];
        double sum = 0;

        for (int frame = 0; frame < frameCount; frame++)
        {
            int start = frame * HopSize;
            Array.Clear(real);
            Array.Clear(imaginary);

            for (int i = 0; i < WindowSize; i++)
            {
                int target = BitReversedIndices[i];
                real[target] = samples[start + i] * HammingWindow[i];
            }

            FftInPlace(real, imaginary);

            for (int frequency = 0; frequency < BandBins; frequency++)
            {
                int bin = LowBin + frequency;
                float power = (float)((real[bin] * real[bin]) + (imaginary[bin] * imaginary[bin]));
                float magnitude = MathF.Sqrt(power);
                float clamped = magnitude >= MinimumMagnitude ? magnitude : MinimumMagnitude;
                float value = WasmLogF(clamped);
                matrix[Index(frequency, frame, frameCount)] = value;
                sum += value;
            }
        }

        float mean = (float)(sum / matrix.Length);
        for (int i = 0; i < matrix.Length; i++)
        {
            matrix[i] -= mean;
        }

        return matrix;
    }

    private static void FftInPlace(double[] real, double[] imaginary)
    {
        for (int length = 2; length <= WindowSize; length *= 2)
        {
            double angle = -2.0 * Math.PI / length;
            double stepReal = Math.Cos(angle);
            double stepImaginary = Math.Sin(angle);
            int halfLength = length / 2;

            for (int blockStart = 0; blockStart < WindowSize; blockStart += length)
            {
                double twiddleReal = 1.0;
                double twiddleImaginary = 0.0;

                for (int offset = 0; offset < halfLength; offset++)
                {
                    int evenIndex = blockStart + offset;
                    int oddIndex = evenIndex + halfLength;
                    double oddReal = (real[oddIndex] * twiddleReal) - (imaginary[oddIndex] * twiddleImaginary);
                    double oddImaginary = (real[oddIndex] * twiddleImaginary) + (imaginary[oddIndex] * twiddleReal);
                    double evenReal = real[evenIndex];
                    double evenImaginary = imaginary[evenIndex];

                    real[evenIndex] = evenReal + oddReal;
                    imaginary[evenIndex] = evenImaginary + oddImaginary;
                    real[oddIndex] = evenReal - oddReal;
                    imaginary[oddIndex] = evenImaginary - oddImaginary;

                    double nextTwiddleReal = (twiddleReal * stepReal) - (twiddleImaginary * stepImaginary);
                    twiddleImaginary = (twiddleReal * stepImaginary) + (twiddleImaginary * stepReal);
                    twiddleReal = nextTwiddleReal;
                }
            }
        }
    }

    private static List<InternalPeak> FilterByLocalAverage(
        List<InternalPeak> peaks,
        float[] matrix,
        int frameCount)
    {
        List<InternalPeak> kept = new(peaks.Count);
        foreach (InternalPeak peak in peaks)
        {
            float amplitude = matrix[Index(peak.FrequencyBin, peak.TimeFrame, frameCount)];
            if (amplitude <= 0f)
            {
                continue;
            }

            int frequencyStart = Math.Max(0, peak.FrequencyBin - AverageFrequencyRadius);
            int frequencyEnd = Math.Min(BandBins, peak.FrequencyBin + AverageFrequencyRadius + 1);
            int timeStart = Math.Max(0, peak.TimeFrame - AverageTimeRadius);
            int timeEnd = Math.Min(frameCount, peak.TimeFrame + AverageTimeRadius + 1);

            double sum = 0;
            for (int frequency = frequencyStart; frequency < frequencyEnd; frequency++)
            {
                for (int time = timeStart; time < timeEnd; time++)
                {
                    sum += matrix[Index(frequency, time, frameCount)];
                }
            }

            int count = (frequencyEnd - frequencyStart) * (timeEnd - timeStart);
            double average = sum / count;
            if (average > 2.0 || amplitude - average > AverageThreshold)
            {
                kept.Add(peak);
            }
        }

        return kept;
    }

    private static bool HasGreaterInNeighborhood(
        float[] matrix,
        int frameCount,
        int frequency,
        int time,
        int frequencyRadius,
        int timeRadius)
    {
        float center = matrix[Index(frequency, time, frameCount)];
        int frequencyStart = Math.Max(0, frequency - frequencyRadius);
        int frequencyEnd = Math.Min(BandBins, frequency + frequencyRadius + 1);
        int timeStart = Math.Max(0, time - timeRadius);
        int timeEnd = Math.Min(frameCount, time + timeRadius + 1);

        for (int nearbyFrequency = frequencyStart; nearbyFrequency < frequencyEnd; nearbyFrequency++)
        {
            for (int nearbyTime = timeStart; nearbyTime < timeEnd; nearbyTime++)
            {
                if (matrix[Index(nearbyFrequency, nearbyTime, frameCount)] > center)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static byte[] BuildRawFingerprint(float durationSeconds, ReadOnlySpan<FingerprintPeak> peaks)
    {
        byte[] version = Encoding.ASCII.GetBytes(Version);
        byte[] raw = new byte[79 + (peaks.Length * 12)];
        int offset = 0;

        WriteUInt32(raw, ref offset, (uint)version.Length);
        version.CopyTo(raw, offset);
        offset += version.Length;
        offset += 8;
        WriteSingle(raw, ref offset, durationSeconds);
        Encoding.ASCII.GetBytes("FPVER").CopyTo(raw, offset);
        offset += 5;
        WriteUInt32(raw, ref offset, (uint)version.Length);
        version.CopyTo(raw, offset);
        offset += version.Length;
        Encoding.ASCII.GetBytes("Peak").CopyTo(raw, offset);
        offset += 4;
        WriteUInt32(raw, ref offset, (uint)peaks.Length);

        foreach (FingerprintPeak peak in peaks)
        {
            WriteUInt32(raw, ref offset, (uint)peak.FrequencyBin);
            WriteUInt32(raw, ref offset, (uint)peak.TimeFrame);
            WriteSingle(raw, ref offset, peak.Amplitude);
        }

        return raw;
    }

    private static byte[] ZlibStore(ReadOnlySpan<byte> input)
    {
        using MemoryStream output = new();
        using (System.IO.Compression.ZLibStream compressor = new(
            output,
            System.IO.Compression.CompressionLevel.Optimal,
            leaveOpen: true))
        {
            compressor.Write(input);
        }

        return output.ToArray();
    }

    private static byte[] EncryptAes128EcbPkcs7(byte[] input)
    {
        using Aes aes = Aes.Create();
        aes.Key = AesKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(input, 0, input.Length);
    }

    private static float WasmLogF(float value)
    {
        float twoPow25 = BitConverter.UInt32BitsToSingle(0x4c000000);
        float ln2Hi = BitConverter.UInt32BitsToSingle(0x3f317180);
        float ln2Lo = BitConverter.UInt32BitsToSingle(0x3717f7d1);
        float lg1 = BitConverter.UInt32BitsToSingle(0x3f2aaaaa);
        float lg2 = BitConverter.UInt32BitsToSingle(0x3eccce13);
        float lg3 = BitConverter.UInt32BitsToSingle(0x3e91e9ee);
        float lg4 = BitConverter.UInt32BitsToSingle(0x3e789e26);
        const uint normalizedMantissaBase = 0x3f3504f3;
        const uint roundingBias = 0x004afb0d;

        uint bits = BitConverter.SingleToUInt32Bits(value);
        int exponentAdjustment;

        if (bits < 0x00800000 || (int)bits < 0)
        {
            if ((bits & 0x7fffffff) == 0)
            {
                return -1f / (value * value);
            }

            if ((int)bits < 0)
            {
                return (value - value) / 0f;
            }

            value *= twoPow25;
            bits = BitConverter.SingleToUInt32Bits(value);
            exponentAdjustment = -152;
        }
        else
        {
            if (bits > 0x7f7fffff)
            {
                return value;
            }

            if (bits == 0x3f800000)
            {
                return 0f;
            }

            exponentAdjustment = -127;
        }

        bits = unchecked(bits + roundingBias);
        exponentAdjustment += (int)(bits >> 23);
        bits = (bits & 0x007fffff) + normalizedMantissaBase;

        float f = BitConverter.UInt32BitsToSingle(bits) - 1f;
        float s = f / (2f + f);
        float halfFSquared = (0.5f * f) * f;
        float z = s * s;
        float w = z * z;
        float t1 = w * (lg2 + (w * lg4));
        float t2 = z * (lg1 + (w * lg3));
        float remainder = t2 + t1;
        float exponent = exponentAdjustment;

        float result = s * (halfFSquared + remainder);
        result += exponent * ln2Lo;
        result -= halfFSquared;
        result += f;
        result += exponent * ln2Hi;
        return result;
    }

    private static float[] BuildHammingWindow()
    {
        float[] window = new float[WindowSize];
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = (float)(0.54 - (0.46 * Math.Cos((2.0 * Math.PI * i) / (WindowSize - 1))));
        }

        return window;
    }

    private static int[] BuildBitReversedIndices()
    {
        int[] indices = new int[WindowSize];
        for (int index = 0; index < WindowSize; index++)
        {
            int source = index;
            int reversed = 0;
            for (int bit = 0; bit < 11; bit++)
            {
                reversed = (reversed << 1) | (source & 1);
                source >>= 1;
            }

            indices[index] = reversed;
        }

        return indices;
    }

    private static int Index(int frequency, int time, int frameCount) => (frequency * frameCount) + time;

    private static void WriteUInt32(Span<byte> destination, ref int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, 4), value);
        offset += 4;
    }

    private static void WriteSingle(Span<byte> destination, ref int offset, float value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, 4), BitConverter.SingleToInt32Bits(value));
        offset += 4;
    }

    private readonly record struct InternalPeak(int FrequencyBin, int TimeFrame, float Amplitude);
}

public sealed record FingerprintResult(string Encoded, IReadOnlyList<FingerprintPeak> Peaks);

public readonly record struct FingerprintPeak(int FrequencyBin, int TimeFrame, float Amplitude);
