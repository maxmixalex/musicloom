using MusicLoom.Core.Enrichment;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;

internal static class AutoMatchEligibilityRegression
{
    public static void Run()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "musicloom-automatch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "song.flac");
            File.WriteAllBytes(source, [1, 2, 3, 4]);
            FileInfo info = new(source);
            TrackMetadata metadata = new(
                "Singer", "Singer", "Album", false, "Song");
            PlanEntry entry = new(source, Path.Combine(root, "dest.flac"),
                PlanEntryStatus.Unchanged, metadata);

            Assert(AutoMatchEligibility.ShouldAttempt(entry, null));
            TrackState known = new()
            {
                SourcePath = source,
                SourceSize = info.Length,
                SourceLastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                QuickHash = QuickFileHash.Compute(source),
                Recognition = RecognitionState.NoMatch,
                RecognitionCheckedAt = DateTimeOffset.UtcNow,
            };
            Assert(!AutoMatchEligibility.ShouldAttempt(entry, known));

            // Even a same-size/same-timestamp content replacement retries.
            DateTime timestamp = File.GetLastWriteTimeUtc(source);
            File.WriteAllBytes(source, [4, 3, 2, 1]);
            File.SetLastWriteTimeUtc(source, timestamp);
            Assert(AutoMatchEligibility.ShouldAttempt(entry, known));

            known = known with
            {
                SourceSize = new FileInfo(source).Length,
                QuickHash = QuickFileHash.Compute(source),
            };
            Assert(!AutoMatchEligibility.ShouldAttempt(entry, known));
            Assert(!AutoMatchEligibility.ShouldAttempt(
                entry, known with { NeteaseMatchSuppressed = true }));
            Assert(!AutoMatchEligibility.ShouldAttempt(
                entry, known with { NeteaseSongId = 42 }));

            File.Delete(source);
            Assert(!AutoMatchEligibility.ShouldAttempt(entry, null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "Auto-match incremental eligibility assertion failed.");
        }
    }
}
