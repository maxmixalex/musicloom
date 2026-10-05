using System.Runtime.Versioning;
using Concentus;
using Concentus.Oggfile;
using Concentus.Structs;
using MusicLoom.AudioFingerprint;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLayer;
using NLayer.NAudioSupport;

namespace MusicLoom.Audio.Windows;

[SupportedOSPlatform("windows")]
public sealed class CompositeFingerprintPcmDecoder : IFingerprintPcmDecoder
{
    private readonly MediaFoundationFingerprintPcmDecoder _mediaFoundation = new();

    public Task<float[]?> DecodeAsync(
        string audioFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(audioFilePath);

        string extension = Path.GetExtension(audioFilePath);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            return Task.Run(
                () => DecodeMp3(audioFilePath, cancellationToken),
                cancellationToken);
        }

        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return Task.Run(
                () => DecodeWave(audioFilePath, cancellationToken),
                cancellationToken);
        }

        if (extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            return Task.Run(
                () => DecodeVorbis(audioFilePath, cancellationToken),
                cancellationToken);
        }

        if (extension.Equals(".opus", StringComparison.OrdinalIgnoreCase))
        {
            return Task.Run(
                () => DecodeOpus(audioFilePath, cancellationToken),
                cancellationToken);
        }

        // Media Foundation is reliable on the Windows corpus for FLAC,
        // AAC/M4A and WMA, and remains the fallback for future formats.
        return _mediaFoundation.DecodeAsync(audioFilePath, cancellationToken);
    }

    private static float[]? DecodeMp3(
        string path,
        CancellationToken cancellationToken)
    {
        using ManagedMpegStream reader = new(
            path,
            StereoMode.DownmixToMono);

        return Collect(
            reader.ToSampleProvider(),
            cancellationToken);
    }

    private static float[]? DecodeWave(
        string path,
        CancellationToken cancellationToken)
    {
        using WaveFileReader reader = new(path);
        return Collect(
            reader.ToSampleProvider(),
            cancellationToken);
    }

    private static float[]? DecodeVorbis(
        string path,
        CancellationToken cancellationToken)
    {
        using VorbisWaveReader reader = new(path);
        return Collect(
            (ISampleProvider)reader,
            cancellationToken);
    }

    private static float[]? DecodeOpus(
        string path,
        CancellationToken cancellationToken)
    {
        int channels = ReadOpusChannelCount(path);
        if (channels is < 1 or > 2)
        {
            throw new NotSupportedException(
                $"Opus channel count {channels} is not supported by the fingerprint decoder.");
        }

        using FileStream stream = File.OpenRead(path);
        IOpusDecoder decoder = OpusCodecFactory.CreateDecoder(
            NeteaseFingerprint.SampleRate,
            channels);
        OpusOggReadStream reader = new(decoder, stream);

        try
        {
            FingerprintWindowCollector collector = new();
            float[] block = new float[FingerprintWindowCollector.ProbeSampleCount];
            int buffered = 0;

            while (reader.HasNextPacket && !collector.IsComplete)
            {
                cancellationToken.ThrowIfCancellationRequested();

                short[]? packet = reader.DecodeNextPacket();
                if (packet is null)
                {
                    if (!string.IsNullOrWhiteSpace(reader.LastError))
                    {
                        throw new InvalidDataException(reader.LastError);
                    }

                    continue;
                }

                int frames = packet.Length / channels;
                for (int frame = 0; frame < frames; frame++)
                {
                    int offset = frame * channels;
                    int sum = 0;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        sum += packet[offset + channel];
                    }

                    block[buffered++] =
                        (sum / (float)channels) / 32768f;

                    if (buffered == block.Length)
                    {
                        collector.Push(block);
                        buffered = 0;
                        if (collector.IsComplete)
                        {
                            break;
                        }
                    }
                }
            }

            if (buffered > 0 && !collector.IsComplete)
            {
                collector.Push(block.AsSpan(0, buffered));
            }

            return collector.Complete();
        }
        finally
        {
            reader.Close();
        }
    }

    private static float[]? Collect(
        ISampleProvider source,
        CancellationToken cancellationToken)
    {
        ISampleProvider mono = source.WaveFormat.Channels == 1
            ? source
            : new DownmixToMonoSampleProvider(source);

        ISampleProvider output = mono.WaveFormat.SampleRate ==
            NeteaseFingerprint.SampleRate
                ? mono
                : new WdlResamplingSampleProvider(
                    mono,
                    NeteaseFingerprint.SampleRate);

        FingerprintWindowCollector collector = new();
        float[] block = new float[FingerprintWindowCollector.ProbeSampleCount];
        int buffered = 0;

        while (!collector.IsComplete)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int read = output.Read(block.AsSpan(buffered));
            if (read == 0)
            {
                if (buffered > 0)
                {
                    collector.Push(block.AsSpan(0, buffered));
                }

                break;
            }

            buffered += read;
            if (buffered == block.Length)
            {
                collector.Push(block);
                buffered = 0;
            }
        }

        return collector.Complete();
    }

    private static int ReadOpusChannelCount(string path)
    {
        const int ProbeLength = 64 * 1024;
        byte[] bytes = new byte[ProbeLength];

        using FileStream stream = File.OpenRead(path);
        int read = stream.Read(bytes.AsSpan());
        ReadOnlySpan<byte> data = bytes.AsSpan(0, read);
        int index = data.IndexOf("OpusHead"u8);

        if (index < 0 || index + 10 > data.Length)
        {
            throw new InvalidDataException(
                "Could not find the OpusHead packet.");
        }

        int channels = data[index + 9];
        if (channels == 0)
        {
            throw new InvalidDataException(
                "OpusHead declares zero channels.");
        }

        return channels;
    }
}

internal sealed class DownmixToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _scratch = [];

    public DownmixToMonoSampleProvider(ISampleProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _channels = source.WaveFormat.Channels;
        if (_channels < 2)
        {
            throw new ArgumentException(
                "Downmixing requires at least two source channels.",
                nameof(source));
        }

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            source.WaveFormat.SampleRate,
            1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        int required = checked(buffer.Length * _channels);
        if (_scratch.Length < required)
        {
            _scratch = new float[required];
        }

        int read = _source.Read(_scratch.AsSpan(0, required));
        int frames = read / _channels;

        for (int frame = 0; frame < frames; frame++)
        {
            int offset = frame * _channels;
            float sum = 0f;
            for (int channel = 0; channel < _channels; channel++)
            {
                sum += _scratch[offset + channel];
            }

            buffer[frame] = sum / _channels;
        }

        return frames;
    }
}
