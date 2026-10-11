using System.Buffers.Binary;
using MusicLoom.Audio.Windows;
using MusicLoom.AudioFingerprint;

string path = Path.Combine(Path.GetTempPath(), $"musicloom-mf-{Guid.NewGuid():N}.wav");

try
{
    WriteTestWave(path);

    MediaFoundationFingerprintPcmDecoder decoder = new();
    float[]? samples = await decoder.DecodeAsync(path);
    if (samples is null)
    {
        throw new InvalidOperationException("Media Foundation decoder produced no non-silent samples.");
    }

    if (samples.Length != NeteaseFingerprint.RequiredSampleCount)
    {
        throw new InvalidOperationException(
            $"Expected {NeteaseFingerprint.RequiredSampleCount} samples, got {samples.Length}.");
    }

    int nonZero = samples.Count(static sample => MathF.Abs(sample) > 0.001f);
    if (nonZero < NeteaseFingerprint.SampleRate)
    {
        throw new InvalidOperationException(
            $"Decoded window contains too little non-silent audio ({nonZero} samples).");
    }

    float[]? envelope = await new TrackWaveformAnalyzer()
        .AnalyzeAsync(path);
    if (envelope is null
        || envelope.Length != TrackWaveformAnalyzer.Resolution)
    {
        throw new InvalidOperationException(
            "The playing-row waveform did not produce a complete PCM envelope.");
    }

    // The first second of the fixture is silent; the remaining four
    // seconds contain real 440 Hz audio. Synthetic looping artwork would
    // not pass this test.
    if (envelope.Take(50).Any(static value => value > 0.02f)
        || envelope.Skip(120).Max() < 0.5f)
    {
        throw new InvalidOperationException(
            "Decoded waveform does not reflect silence and loud audio.");
    }

    FingerprintResult fingerprint = NeteaseFingerprint.Generate(samples);
    if (fingerprint.Peaks.Count == 0)
    {
        throw new InvalidOperationException("Decoded 440 Hz tone produced no fingerprint peaks.");
    }

    Console.WriteLine(
        $"PASS Media Foundation fingerprint window: {nonZero} non-zero samples, {fingerprint.Peaks.Count} peaks");
    return 0;
}
finally
{
    File.Delete(path);
}

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
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28, 4), sampleRate * blockAlign);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32, 2), (short)blockAlign);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(34, 2), (short)bitsPerSample);
    "data"u8.CopyTo(data.AsSpan(36));
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40, 4), dataSize);

    int offset = 44;
    for (int frame = 0; frame < frames; frame++)
    {
        double seconds = (double)frame / sampleRate;
        short value = frame < sampleRate * silentSeconds
            ? (short)0
            : (short)Math.Round(
                Math.Sin(2.0 * Math.PI * frequency * seconds) *
                amplitude *
                short.MaxValue);

        for (int channel = 0; channel < channels; channel++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset, 2), value);
            offset += 2;
        }
    }

    File.WriteAllBytes(path, data);
}
