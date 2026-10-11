using System.Runtime.Versioning;
using NAudio.Dsp;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLayer.NAudioSupport;
using NLayer;

namespace MusicLoom.Audio.Windows;

/// <summary>
/// Spectral content from the actual audio file at its playback timeline.
/// Frames encode measured 50 ms windows; the UI never animates a fake sine
/// or treats the horizontal axis as the song's progress bar.
/// </summary>
public sealed class ReactiveAudioFrames
{
    public const int BandCount = 24;
    public const double FrameDurationSeconds = 0.05;

    public ReactiveAudioFrames(float[][] frames)
    {
        Frames = frames;
    }

    public float[][] Frames { get; }

    public float[] At(TimeSpan position)
    {
        if (Frames.Length == 0)
        {
            return new float[BandCount];
        }

        double time = position.TotalSeconds;
        if (!double.IsFinite(time) || time < 0)
        {
            time = 0;
        }

        double cursor = Math.Min(
            Frames.Length - 1,
            time / FrameDurationSeconds);
        int low = (int)cursor;
        int high = Math.Min(Frames.Length - 1, low + 1);
        float weight = (float)(cursor - low);
        float[] output = new float[BandCount];

        for (int band = 0; band < output.Length; band++)
        {
            float left = Frames[low][band];
            float right = Frames[high][band];
            float interpolated = left + (right - left) * weight;
            output[band] = float.IsFinite(interpolated)
                ? Math.Clamp(interpolated, 0f, 1f)
                : 0f;
        }

        return output;
    }
}

[SupportedOSPlatform("windows")]
public sealed class ReactiveAudioAnalyzer
{
    private const int SampleRate = 16_000;
    private const int HopSamples = 800; // 20 measured frames per second.
    private const int FftSize = 1024;
    private const int FftOrder = 10;
    private const int MaximumFrames = 20 * 60 * 60; // Stop after one hour.

    public Task<ReactiveAudioFrames?> AnalyzeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(
            () => AnalyzeCore(path, cancellationToken),
            cancellationToken);
    }

    private static ReactiveAudioFrames? AnalyzeCore(
        string path,
        CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            using ManagedMpegStream decoder = new(path, StereoMode.DownmixToMono);
            return ReadFrames(decoder.ToSampleProvider(), cancellationToken);
        }

        if (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            using VorbisWaveReader decoder = new(path);
            return ReadFrames((ISampleProvider)decoder, cancellationToken);
        }

        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            using WaveFileReader decoder = new(path);
            return ReadFrames(decoder.ToSampleProvider(), cancellationToken);
        }

        MediaFoundationReader.MediaFoundationReaderSettings settings = new()
        {
            RequestFloatOutput = true,
            SingleReaderObject = true,
        };
        using MediaFoundationReader reader = new(path, settings);
        return ReadFrames(reader.ToSampleProvider(), cancellationToken);
    }

    private static ReactiveAudioFrames? ReadFrames(
        ISampleProvider source,
        CancellationToken cancellationToken)
    {
        if (source.WaveFormat.Channels < 1
            || source.WaveFormat.SampleRate <= 0)
        {
            return null;
        }

        ISampleProvider mono = source.WaveFormat.Channels == 1
            ? source
            : new DownmixToMonoSampleProvider(source);
        ISampleProvider resampled = mono.WaveFormat.SampleRate == SampleRate
            ? mono
            : new WdlResamplingSampleProvider(mono, SampleRate);

        List<float[]> spectralPower = [];
        List<float> loudness = [];
        float[] samples = new float[HopSamples];
        Complex[] fft = new Complex[FftSize];
        double[] powerByBand = new double[ReactiveAudioFrames.BandCount];
        int[] binCounts = new int[ReactiveAudioFrames.BandCount];

        for (int k = 1; k < FftSize / 2; k++)
        {
            double freq = (double)k * SampleRate / FftSize;
            if (freq < 70 || freq > 7500)
            {
                continue;
            }

            int band = (int)Math.Clamp(
                Math.Log(freq / 70) / Math.Log(7500.0 / 70)
                    * ReactiveAudioFrames.BandCount,
                0, ReactiveAudioFrames.BandCount - 1);
            binCounts[band]++;
        }

        while (spectralPower.Count < MaximumFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int filled = 0;
            while (filled < HopSamples)
            {
                int read = resampled.Read(samples.AsSpan(filled));
                if (read <= 0)
                {
                    break;
                }

                filled += read;
            }

            if (filled == 0)
            {
                break;
            }

            Array.Clear(fft);
            Array.Clear(powerByBand);
            double rmsSum = 0;
            for (int index = 0; index < filled; index++)
            {
                float sample = float.IsFinite(samples[index])
                    ? Math.Clamp(samples[index], -1f, 1f)
                    : 0;
                rmsSum += (double)sample * sample;
                double hann = 0.5 * (1 - Math.Cos(
                    2 * Math.PI * index / (HopSamples - 1)));
                fft[index].X = (float)(sample * hann);
                fft[index].Y = 0;
            }

            FastFourierTransform.FFT(true, FftOrder, fft);
            for (int k = 1; k < FftSize / 2; k++)
            {
                double freq = (double)k * SampleRate / FftSize;
                if (freq < 70 || freq > 7500)
                {
                    continue;
                }

                int band = (int)Math.Clamp(
                    Math.Log(freq / 70) / Math.Log(7500.0 / 70)
                        * ReactiveAudioFrames.BandCount,
                    0, ReactiveAudioFrames.BandCount - 1);
                double real = fft[k].X;
                double imag = fft[k].Y;
                powerByBand[band] += real * real + imag * imag;
            }

            float[] energies = new float[ReactiveAudioFrames.BandCount];
            for (int band = 0; band < energies.Length; band++)
            {
                energies[band] = binCounts[band] == 0
                    ? 0
                    : (float)Math.Sqrt(
                        powerByBand[band] / binCounts[band]);
            }

            spectralPower.Add(energies);
            loudness.Add((float)Math.Sqrt(rmsSum / filled));
            if (filled < HopSamples)
            {
                break;
            }
        }

        if (spectralPower.Count == 0)
        {
            return null;
        }

        // The global loudness reference preserves song dynamics rather than
        // normalizing every window to full height (which would look fake).
        float[] sorted = loudness.ToArray();
        Array.Sort(sorted);
        float reference = sorted[(int)(sorted.Length * 0.95)];
        reference = float.IsFinite(reference)
            ? Math.Max(reference, 0.0001f)
            : 0.0001f;

        float[][] finalFrames = new float[spectralPower.Count][];
        for (int frame = 0; frame < spectralPower.Count; frame++)
        {
            float[] energies = spectralPower[frame];
            float maxEnergy = Math.Max(0.00001f, energies.Max());
            float rms = Math.Clamp(
                loudness[frame] / reference,
                0f, 1f);
            float[] bars = new float[ReactiveAudioFrames.BandCount];
            for (int band = 0; band < bars.Length; band++)
            {
                float relative = Math.Clamp(
                    energies[band] / maxEnergy,
                    0f, 1f);
                // Even a prominent frequency shrinks when the recording
                // gets quiet. Gentle compression keeps the line readable.
                bars[band] = Math.Clamp(
                    MathF.Pow(relative, 0.65f) * rms,
                    0f, 1f);
            }

            finalFrames[frame] = bars;
        }

        return new ReactiveAudioFrames(finalFrames);
    }
}
