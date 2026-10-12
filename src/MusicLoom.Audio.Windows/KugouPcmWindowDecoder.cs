using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MusicLoom.AudioFingerprint;
using NAudio.Wave;

namespace MusicLoom.Audio.Windows;

/// <summary>
/// Resamples at most ten seconds of local audio into KuGou's expected
/// 8 kHz mono little-endian PCM16. Decoding is local and read-only; the
/// recognition request is a separate explicit user action.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KugouPcmWindowDecoder
{
    private const int SampleRate = 8_000;
    private const int MaxSeconds = 10;
    private const int MinSeconds = 2;

    public async Task<byte[]?> DecodeAsync(
        string audioPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ct.ThrowIfCancellationRequested();

        try
        {
            return await Task.Run(
                () => DecodeMediaFoundation(audioPath, ct),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (
            error is NotSupportedException
            or InvalidDataException
            or COMException
            or IOException)
        {
            // FFmpeg-free fallback to the existing six-format decoder.
            // It supplies a three-second window where Media Foundation
            // does not support the particular codec/container.
            float[]? samples =
                await new CompositeFingerprintPcmDecoder()
                    .DecodeAsync(audioPath, ct);
            return samples is null
                ? null
                : ToPcm16(samples);
        }
    }

    private static byte[]? DecodeMediaFoundation(
        string path, CancellationToken ct)
    {
        MediaFoundationReader.MediaFoundationReaderSettings settings = new()
        {
            RequestFloatOutput = true,
            SingleReaderObject = true,
        };

        using MediaFoundationReader reader = new(path, settings);
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(
            SampleRate, channels: 1);
        using MediaFoundationResampler converter =
            new(reader, format)
            {
                ResamplerQuality = 60,
            };

        byte[] result = new byte[SampleRate * MaxSeconds * sizeof(short)];
        byte[] buffer = new byte[1_024 * sizeof(float)];
        int written = 0;
        bool started = false;

        while (written < result.Length)
        {
            ct.ThrowIfCancellationRequested();
            int bytes = converter.Read(buffer, 0, buffer.Length);
            if (bytes == 0)
            {
                break;
            }
            if (bytes % sizeof(float) != 0)
            {
                throw new InvalidDataException(
                    "Media Foundation returned misaligned float32 PCM.");
            }

            ReadOnlySpan<float> samples =
                MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytes));
            if (!started)
            {
                // Skip only the initial silence, just like the NetEase
                // fingerprint decoder. Later quiet passages remain.
                if (FingerprintWindowCollector.CalculateRms(samples)
                    < FingerprintWindowCollector.SilenceThreshold)
                {
                    continue;
                }
                started = true;
            }

            foreach (float sample in samples)
            {
                if (written >= result.Length)
                {
                    break;
                }

                short pcm = Quantize(sample);
                BinaryPrimitives.WriteInt16LittleEndian(
                    result.AsSpan(written, sizeof(short)), pcm);
                written += sizeof(short);
            }
        }

        return written >= SampleRate * MinSeconds * sizeof(short)
            ? result.AsSpan(0, written).ToArray()
            : null;
    }

    public static byte[] ToPcm16(ReadOnlySpan<float> samples)
    {
        byte[] data = new byte[samples.Length * sizeof(short)];
        for (int index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                data.AsSpan(index * sizeof(short), sizeof(short)),
                Quantize(samples[index]));
        }
        return data;
    }

    private static short Quantize(float sample)
    {
        if (!float.IsFinite(sample))
        {
            return 0;
        }

        float normalized = Math.Clamp(sample, -1f, 1f);
        return normalized < 0
            ? (short)MathF.Round(normalized * 32768f)
            : (short)MathF.Round(normalized * 32767f);
    }
}
