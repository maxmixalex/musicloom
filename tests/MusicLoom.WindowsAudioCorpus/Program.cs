using System.Buffers.Binary;
using MusicLoom.Audio.Windows;
using MusicLoom.AudioFingerprint;

if (args.Length != 1)
{
    Console.Error.WriteLine(
        "usage: MusicLoom.WindowsAudioCorpus <taglib-sharp samples directory>");
    return 2;
}

string samplesRoot = Path.GetFullPath(args[0]);
if (!Directory.Exists(samplesRoot))
{
    throw new DirectoryNotFoundException(samplesRoot);
}

string syntheticWave = Path.Combine(
    Path.GetTempPath(),
    $"musicloom-audio-corpus-{Guid.NewGuid():N}.wav");
WriteTestWave(syntheticWave);

(string Name, string Path)[] fixtures =
[
    ("MP3", Path.Combine(samplesRoot, "sample_replaygain.mp3")),
    ("FLAC", Path.Combine(samplesRoot, "sample_replaygain.flac")),
    ("M4A", Path.Combine(samplesRoot, "sample.m4a")),
    ("Ogg Vorbis", Path.Combine(samplesRoot, "sample.ogg")),
    ("Opus", Path.Combine(samplesRoot, "sample.opus")),
    ("WAV", syntheticWave),
    ("WMA", Path.Combine(samplesRoot, "sample.wma")),
];

CompositeFingerprintPcmDecoder decoder = new();
int failed = 0;

try
{
    foreach ((string name, string path) in fixtures)
    {
        try
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Fixture '{Path.GetFileName(path)}' does not exist.",
                    path);
            }

            float[]? samples = await decoder.DecodeAsync(path);
            if (samples is null)
            {
                throw new InvalidOperationException(
                    "decoder returned no non-silent fingerprint window");
            }

            if (samples.Length != NeteaseFingerprint.RequiredSampleCount)
            {
                throw new InvalidOperationException(
                    $"expected {NeteaseFingerprint.RequiredSampleCount} samples, got {samples.Length}");
            }

            int meaningful = samples.Count(
                static sample => MathF.Abs(sample) > 0.0001f);
            float peak = samples.Max(static sample => MathF.Abs(sample));

            if (meaningful == 0 || peak <= 0.0001f)
            {
                throw new InvalidOperationException(
                    "decoded fingerprint window contains no meaningful audio");
            }

            FingerprintResult fingerprint = NeteaseFingerprint.Generate(samples);
            Console.WriteLine(
                $"PASS {name,-10} samples={samples.Length} nonzero={meaningful} peak={peak:F5} fpPeaks={fingerprint.Peaks.Count}");
        }
        catch (Exception exception)
        {
            failed++;
            Console.Error.WriteLine(
                $"FAIL {name}: {exception.GetType().Name}: {exception.Message}");
        }
    }
}
finally
{
    File.Delete(syntheticWave);
}

Console.WriteLine(
    $"{fixtures.Length - failed}/{fixtures.Length} composite decoder formats passed");
return failed == 0 ? 0 : 1;

static void WriteTestWave(string path)
{
    const int sampleRate = 44_100;
    const int channels = 2;
    const int bitsPerSample = 16;
    const int silentSeconds = 1;
    const int toneSeconds = 4;
    const double frequency = 440.0;
    const double amplitude = 0.5;

    int frames = sampleRate * (silentSeconds + toneSeconds);
    int bytesPerSample = bitsPerSample / 8;
    int blockAlign = channels * bytesPerSample;
    int dataSize = frames * blockAlign;
    byte[] data = new byte[44 + dataSize];

    "RIFF"u8.CopyTo(data);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 36 + dataSize);
    "WAVE"u8.CopyTo(data.AsSpan(8));
    "fmt "u8.CopyTo(data.AsSpan(12));
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16, 4), 16);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20, 2), 1);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22, 2), (short)channels);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24, 4), sampleRate);
    BinaryPrimitives.WriteInt32LittleEndian(
        data.AsSpan(28, 4),
        sampleRate * blockAlign);
    BinaryPrimitives.WriteInt16LittleEndian(
        data.AsSpan(32, 2),
        (short)blockAlign);
    BinaryPrimitives.WriteInt16LittleEndian(
        data.AsSpan(34, 2),
        (short)bitsPerSample);
    "data"u8.CopyTo(data.AsSpan(36));
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40, 4), dataSize);

    int offset = 44;
    for (int frame = 0; frame < frames; frame++)
    {
        double seconds = (double)frame / sampleRate;
        short value = frame < sampleRate * silentSeconds
            ? (short)0
            : (short)Math.Round(
                Math.Sin(2.0 * Math.PI * frequency * seconds)
                * amplitude
                * short.MaxValue);

        for (int channel = 0; channel < channels; channel++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(
                data.AsSpan(offset, 2),
                value);
            offset += 2;
        }
    }

    File.WriteAllBytes(path, data);
}
