using MusicLoom.Core.Lyrics;

internal static class LyricsOffsetRegression
{
    public static void Run()
    {
        LrcDocument canonical = LrcDocument.Parse(
            "[ar:Artist]\n[offset:50]\n[00:00.100]First\n[00:02.000]Second");
        string original = canonical.ToString();
        LrcDocument before = LrcDocument.Shift(canonical, -300);
        LrcDocument after = LrcDocument.Shift(canonical, 1500);
        if (before.Lines[0].Timestamp != TimeSpan.Zero
            || before.Lines[1].Timestamp != TimeSpan.FromMilliseconds(1750)
            || after.Lines[0].Timestamp != TimeSpan.FromMilliseconds(1650)
            || after.Lines[1].Timestamp != TimeSpan.FromMilliseconds(3550)
            || before.Metadata.ContainsKey("offset")
            || after.Metadata.ContainsKey("offset")
            || canonical.ToString() != original)
        {
            throw new InvalidOperationException("Lyric output offset corrupted source.");
        }
    }
}
