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

(string Name, string File)[] fixtures =
[
    ("MP3", "sample.mp3"),
    ("FLAC", "sample_replaygain.flac"),
    ("M4A", "sample.m4a"),
    ("Ogg Vorbis", "sample.ogg"),
    ("Opus", "sample.opus"),
    ("WAV", "sample.wav"),
    ("WMA", "sample.wma"),
];

MediaFoundationFingerprintPcmDecoder decoder = new();
int failed = 0;

foreach ((string name, string file) in fixtures)
{
    string path = Path.Combine(samplesRoot, file);
    try
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Fixture '{file}' does not exist.",
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

Console.WriteLine(
    $"{fixtures.Length - failed}/{fixtures.Length} Media Foundation formats passed");
return failed == 0 ? 0 : 1;
