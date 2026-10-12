using MusicLoom.Core.Lyrics;

internal static class KaraokeTimingRegression
{
    public static void Run()
    {
        const string lrc =
            "[00:01.000]<00:01.050>Hel<00:01.450>lo<00:01.900>\n"
            + "[00:03.000]<00:03.000>世<00:03.300>界<00:03.800>\n";
        LrcDocument original = LrcDocument.Parse(lrc);
        LrcDocument translation = LrcDocument.Parse(
            "[00:01.120]你好\n[00:03.100]world");
        LrcDocument merged = LrcDocument.Merge(original, translation);
        IReadOnlyList<LrcPresentationLine> rendered =
            LrcPresentation.Project(merged);

        if (rendered.Count != 2
            || rendered[0].Original != "Hello"
            || rendered[0].Translation != "你好"
            || rendered[0].OriginalWords is not { Count: 2 }
            || rendered[1].OriginalWords is not { Count: 2 })
        {
            throw new InvalidOperationException(
                "Translated LRC presentation discarded original QRC/KRC words.");
        }

        LrcWord first = rendered[0].OriginalWords![0];
        AssertNear(0, LrcKaraokeTiming.Fraction(first,
            TimeSpan.FromMilliseconds(1000)));
        AssertNear(0, LrcKaraokeTiming.Fraction(first,
            TimeSpan.FromMilliseconds(1050)));
        AssertNear(0.5, LrcKaraokeTiming.Fraction(first,
            TimeSpan.FromMilliseconds(1250)));
        AssertNear(1, LrcKaraokeTiming.Fraction(first,
            TimeSpan.FromMilliseconds(1450)));
        AssertNear(1, LrcKaraokeTiming.Fraction(first,
            TimeSpan.FromMilliseconds(1900)));

        // Seeking back always recomputes from the real position.
        AssertNear(0.25, LrcKaraokeTiming.Fraction(first,
            TimeSpan.FromMilliseconds(1150)));

        LrcWord gap = new(
            TimeSpan.FromMilliseconds(3000),
            TimeSpan.FromMilliseconds(3200),
            "word");
        AssertNear(1, LrcKaraokeTiming.Fraction(gap,
            TimeSpan.FromMilliseconds(4000)));
        AssertNear(0, LrcKaraokeTiming.Fraction(gap,
            TimeSpan.FromMilliseconds(2000)));

        LrcWord instant = new(TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(2), "!");
        AssertNear(0, LrcKaraokeTiming.Fraction(instant,
            TimeSpan.FromMilliseconds(1999)));
        AssertNear(1, LrcKaraokeTiming.Fraction(instant,
            TimeSpan.FromSeconds(2)));

        LrcDocument withoutWords = LrcDocument.Parse(
            "[00:00.500]Plain line\n[00:01.000]No words");
        if (LrcPresentation.Project(withoutWords)
            .Any(static line => line.OriginalWords is not null))
        {
            throw new InvalidOperationException(
                "Ordinary LRC must not fabricate karaoke word timing.");
        }

        // Inline translations cannot be assigned unrelated original
        // word segments just because they share a timestamp.
        LrcDocument mixed = new();
        mixed.Lines.Add(new LrcLine(
            TimeSpan.FromSeconds(1), "Hello / 你好",
            [new LrcWord(TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(2), "Hello / 你好")]));
        if (LrcPresentation.Project(mixed)[0].OriginalWords is not null)
        {
            throw new InvalidOperationException(
                "Unsafe inline text split inherited incorrect word timings.");
        }

        LrcDocument shifted = LrcDocument.Shift(merged, 250);
        if (LrcPresentation.Project(shifted)[0].OriginalWords![0].Start
            != TimeSpan.FromMilliseconds(1300))
        {
            throw new InvalidOperationException(
                "Sync output offset did not move word playback times.");
        }
    }

    private static void AssertNear(double expected, double actual)
    {
        if (Math.Abs(expected - actual) > 0.000_001)
        {
            throw new InvalidOperationException(
                $"Wrong karaoke progress: expected {expected}, got {actual}.");
        }
    }
}
