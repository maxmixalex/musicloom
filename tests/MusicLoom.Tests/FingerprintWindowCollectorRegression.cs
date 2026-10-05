using MusicLoom.AudioFingerprint;

internal static class FingerprintWindowCollectorRegression
{
    public static void Run()
    {
        FingerprintWindowCollector collector = new();

        float[] silent = new float[FingerprintWindowCollector.ProbeSampleCount];
        if (collector.Push(silent) || collector.HasStarted)
        {
            throw new InvalidOperationException("silent block unexpectedly started collection");
        }

        float[] belowThreshold = Enumerable
            .Repeat(FingerprintWindowCollector.SilenceThreshold * 0.9f, FingerprintWindowCollector.ProbeSampleCount)
            .ToArray();
        collector.Push(belowThreshold);
        if (collector.HasStarted)
        {
            throw new InvalidOperationException("below-threshold block unexpectedly started collection");
        }

        const float audibleLevel = 0.02f;
        float[] audibleBlock = Enumerable
            .Repeat(audibleLevel, FingerprintWindowCollector.ProbeSampleCount)
            .ToArray();
        collector.Push(audibleBlock);

        if (!collector.HasStarted || collector.Count != audibleBlock.Length)
        {
            throw new InvalidOperationException("the complete first non-silent block was not retained");
        }

        float[]? result = collector.Complete();
        if (result is null || result.Length != NeteaseFingerprint.RequiredSampleCount)
        {
            throw new InvalidOperationException("started collection must return a zero-padded 24,000-sample window");
        }

        for (int index = 0; index < audibleBlock.Length; index++)
        {
            if (result[index] != audibleLevel)
            {
                throw new InvalidOperationException("first non-silent block was not copied intact");
            }
        }

        if (result[audibleBlock.Length] != 0f)
        {
            throw new InvalidOperationException("short input was not zero-padded");
        }

        FingerprintWindowCollector allSilent = new();
        allSilent.Push(silent);
        if (allSilent.Complete() is not null)
        {
            throw new InvalidOperationException("all-silent input must match legacy no-samples behavior");
        }
    }
}
