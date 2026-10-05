using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using MusicLoom.AudioFingerprint;
using NAudio.Wave;

namespace MusicLoom.Audio.Windows;

[SupportedOSPlatform("windows")]
public sealed class MediaFoundationFingerprintPcmDecoder : IFingerprintPcmDecoder
{
    public Task<float[]?> DecodeAsync(
        string audioFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);
        return Task.Run(
            () => DecodeCore(audioFilePath, cancellationToken),
            cancellationToken);
    }

    private static float[]? DecodeCore(
        string audioFilePath,
        CancellationToken cancellationToken)
    {
        MediaFoundationReader.MediaFoundationReaderSettings settings = new()
        {
            RequestFloatOutput = true,
            SingleReaderObject = true,
        };

        using MediaFoundationReader reader = new(audioFilePath, settings);
        WaveFormat outputFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            NeteaseFingerprint.SampleRate,
            channels: 1);
        using MediaFoundationResampler resampler = new(reader, outputFormat)
        {
            ResamplerQuality = 60,
        };

        FingerprintWindowCollector collector = new();
        byte[] buffer = new byte[FingerprintWindowCollector.ProbeSampleCount * sizeof(float)];

        while (!collector.IsComplete)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int bytesRead = resampler.Read(buffer, 0, buffer.Length);
            if (bytesRead == 0)
            {
                break;
            }

            if ((bytesRead % sizeof(float)) != 0)
            {
                throw new InvalidDataException(
                    $"Media Foundation returned {bytesRead} bytes, which is not aligned to float32 samples.");
            }

            ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(
                buffer.AsSpan(0, bytesRead));
            collector.Push(samples);
        }

        return collector.Complete();
    }
}
