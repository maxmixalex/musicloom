using System.Runtime.Versioning;
using NAudio.Vorbis;
using NAudio.Wave;

namespace MusicLoom.Audio.Windows;

/// <summary>
/// Decodes the actual recording into a small RMS envelope. Values represent
/// amplitude over the track timeline, not an invented moving sine or an FFT
/// spectrum. Decoding is cancellable and never runs on the WinUI thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrackWaveformAnalyzer
{
    public const int Resolution = 384;

    public Task<float[]?> AnalyzeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(
            () => AnalyzeCore(path, cancellationToken),
            cancellationToken);
    }

    private static float[]? AnalyzeCore(
        string path,
        CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            using VorbisWaveReader vorbis = new(path);
            return AnalyzeSamples(
                (ISampleProvider)vorbis,
                vorbis.TotalTime,
                cancellationToken);
        }

        try
        {
            // Windows Media Foundation handles FLAC/MP3/WAV/M4A/WMA.
            // Using the same OS codecs as the player keeps source and
            // waveform timelines aligned.
            MediaFoundationReader.MediaFoundationReaderSettings settings = new()
            {
                RequestFloatOutput = true,
                SingleReaderObject = true,
            };
            using MediaFoundationReader reader = new(path, settings);
            return AnalyzeSamples(
                reader.ToSampleProvider(),
                reader.TotalTime,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception) when (
            extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            // WAV can still be decoded when Media Foundation is unavailable.
            using WaveFileReader wave = new(path);
            return AnalyzeSamples(
                wave.ToSampleProvider(),
                wave.TotalTime,
                cancellationToken);
        }
    }

    internal static float[]? AnalyzeSamples(
        ISampleProvider samples,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (duration.TotalSeconds <= 0
            || !double.IsFinite(duration.TotalSeconds))
        {
            return null;
        }

        int channels = samples.WaveFormat.Channels;
        int sampleRate = samples.WaveFormat.SampleRate;
        long totalSamples = (long)Math.Ceiling(
            duration.TotalSeconds * sampleRate * channels);
        if (totalSamples <= 0)
        {
            return null;
        }

        double[] squares = new double[Resolution];
        long[] counts = new long[Resolution];
        float[] buffer = new float[8192];
        long processed = 0;
        int read;
        while ((read = samples.Read(buffer.AsSpan())) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int offset = 0;
            while (offset < read)
            {
                int bin = (int)Math.Min(
                    Resolution - 1,
                    (double)processed * Resolution / totalSamples);
                long next = (long)Math.Ceiling(
                    (double)(bin + 1) * totalSamples / Resolution);
                int count = (int)Math.Min(
                    read - offset, Math.Max(1L, next - processed));
                for (int i = offset; i < offset + count; i++)
                {
                    float sample = Math.Clamp(buffer[i], -1f, 1f);
                    squares[bin] += (double)sample * sample;
                }

                counts[bin] += count;
                processed += count;
                offset += count;
            }
        }

        float[] rms = new float[Resolution];
        for (int index = 0; index < rms.Length; index++)
        {
            rms[index] = counts[index] == 0
                ? 0f
                : (float)Math.Sqrt(squares[index] / counts[index]);
        }

        // Scale each recording to its own 95th-percentile level. The
        // relative dynamics are real; low-level recordings simply remain
        // visible without changing the actual ratio of their passages.
        float[] sorted = (float[])rms.Clone();
        Array.Sort(sorted);
        float reference = sorted[(int)((sorted.Length - 1) * 0.95)];
        if (reference <= 0.0001f)
        {
            return rms;
        }

        for (int index = 0; index < rms.Length; index++)
        {
            rms[index] = Math.Clamp(
                rms[index] / reference,
                0f, 1f);
        }

        return rms;
    }
}
