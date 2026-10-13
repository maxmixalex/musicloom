namespace MusicLoom.Core.Lyrics;

/// <summary>
/// Deterministic, seek-safe word completion fractions derived only from
/// actual source word timestamps. This never invents a beat or a duration
/// for ordinary line-timed LRC files.
/// </summary>
public static class LrcKaraokeTiming
{
    public static double Fraction(LrcWord word, TimeSpan playbackPosition)
    {
        ArgumentNullException.ThrowIfNull(word);

        if (playbackPosition <= word.Start)
        {
            return playbackPosition == word.Start && word.End <= word.Start
                ? 1 : 0;
        }

        if (playbackPosition >= word.End)
        {
            return 1;
        }

        // A zero/negative-duration word is an instantaneous marker.
        if (word.End <= word.Start)
        {
            return 1;
        }

        return Math.Clamp(
            (playbackPosition - word.Start).Ticks
                / (double)(word.End - word.Start).Ticks,
            0, 1);
    }
}
