namespace MusicLoom.Core.Lyrics;

/// <summary>
/// Reconciles independently fetched romaji rows with original song lines.
/// Word-level readings become timed ruby atoms; a plain romaji row is
/// retained as a line-level reading, never mistaken for translated lyrics.
/// Matching is one-to-one and bounded by timestamp proximity.
/// </summary>
public static class LrcRomanization
{
    public static void Attach(
        LrcDocument merged,
        LrcDocument original,
        LrcDocument romanization)
    {
        ArgumentNullException.ThrowIfNull(merged);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(romanization);

        LrcLine[] originals = original.Lines
            .Where(static line => !string.IsNullOrWhiteSpace(line.Text)
                && !LrcDocument.LooksLikeCreditLine(line.Text))
            .ToArray();
        HashSet<int> used = [];

        foreach (LrcLine reading in romanization.Lines)
        {
            if (string.IsNullOrWhiteSpace(reading.Text)
                || LrcDocument.LooksLikeCreditLine(reading.Text))
                continue;

            int index = -1;
            TimeSpan best = TimeSpan.FromMilliseconds(601);
            for (int i = 0; i < originals.Length; i++)
            {
                if (used.Contains(i)) continue;
                TimeSpan delta =
                    (originals[i].Timestamp - reading.Timestamp).Duration();
                if (delta < best)
                {
                    best = delta;
                    index = i;
                }
            }
            if (index < 0) continue;
            used.Add(index);
            LrcLine anchor = originals[index];
            int at = merged.Lines.FindIndex(line =>
                line.Timestamp == anchor.Timestamp
                && string.Equals(line.Text, anchor.Text, StringComparison.Ordinal));
            if (at < 0) continue;

            IReadOnlyList<LrcWord>? annotated = null;
            if (anchor.Words is { Count: > 0 } words
                && reading.Words is { Count: > 0 } phonemes)
            {
                annotated = RubyWordAlignment.Attach(words, phonemes);
            }

            if (annotated is not null)
            {
                merged.Lines[at] = merged.Lines[at] with
                {
                    Words = annotated,
                    Romanization = null,
                };
            }
            else
            {
                // A plain timed LRC romaji row cannot safely be mapped
                // to a Japanese character or mora by equal indexes.
                merged.Lines[at] = merged.Lines[at] with
                {
                    Romanization = reading.Text.Trim(),
                };
            }
        }
    }
}
