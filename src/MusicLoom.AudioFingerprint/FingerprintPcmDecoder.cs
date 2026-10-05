namespace MusicLoom.AudioFingerprint;

public interface IFingerprintPcmDecoder
{
    Task<float[]?> DecodeAsync(
        string audioFilePath,
        CancellationToken cancellationToken = default);
}

public sealed class FingerprintWindowCollector
{
    public const float SilenceThreshold = 0.01f;
    public const int ProbeSampleCount = 192;

    private readonly float[] _output = new float[NeteaseFingerprint.RequiredSampleCount];
    private int _count;

    public bool HasStarted { get; private set; }
    public int Count => _count;
    public bool IsComplete => _count >= _output.Length;

    public bool Push(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty || IsComplete)
        {
            return IsComplete;
        }

        if (!HasStarted)
        {
            if (CalculateRms(samples) < SilenceThreshold)
            {
                return false;
            }

            HasStarted = true;
        }

        int copyCount = Math.Min(samples.Length, _output.Length - _count);
        samples[..copyCount].CopyTo(_output.AsSpan(_count));
        _count += copyCount;
        return IsComplete;
    }

    public float[]? Complete() => HasStarted ? _output : null;

    public static float CalculateRms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0f;
        }

        float sum = 0f;
        foreach (float sample in samples)
        {
            sum += sample * sample;
        }

        return MathF.Sqrt(sum / samples.Length);
    }
}
