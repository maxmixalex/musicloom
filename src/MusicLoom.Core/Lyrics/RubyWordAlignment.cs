namespace MusicLoom.Core.Lyrics;

/// <summary>
/// Maps syllable-level romanization to the original time-coded words.
/// Direct one-to-one pairs retain their order. When a provider splits
/// readings into more/fewer syllables, time overlap determines the base
/// word without inventing a separate scrolling translation row.
/// </summary>
public static class RubyWordAlignment
{
    public static IReadOnlyList<LrcWord>? Attach(
        IReadOnlyList<LrcWord> original, IReadOnlyList<LrcWord> readings)
    {
        if (original.Count is < 1 or > 256
            || readings.Count is < 1 or > 256)
            return null;

        if (original.Count == readings.Count)
        {
            return original.Select((word, i) => word with
            {
                Ruby = Normalize(readings[i].Text),
            }).ToArray();
        }

        string[] annotations = new string[original.Count];
        int mapped = 0;
        foreach (LrcWord reading in readings)
        {
            string? text = Normalize(reading.Text);
            if (text is null) continue;

            long readStart = reading.Start.Ticks;
            long readEnd = Math.Max(readStart, reading.End.Ticks);
            long readMid = readStart + (readEnd - readStart) / 2;
            int best = -1;
            long bestOverlap = -1;
            long bestDistance = long.MaxValue;

            for (int i = 0; i < original.Count; i++)
            {
                LrcWord baseWord = original[i];
                long start = baseWord.Start.Ticks;
                long end = Math.Max(start, baseWord.End.Ticks);
                long overlap = Math.Max(0,
                    Math.Min(end, readEnd) - Math.Max(start, readStart));
                long center = start + (end - start) / 2;
                long distance = Math.Abs(center - readMid);
                if (overlap > bestOverlap
                    || (overlap == bestOverlap && distance < bestDistance))
                {
                    best = i;
                    bestOverlap = overlap;
                    bestDistance = distance;
                }
            }

            // No overlapping timing and no close neighbouring word means
            // we cannot attach this syllable without risking bad ruby.
            if (best < 0
                || (bestOverlap == 0
                    && bestDistance > TimeSpan.FromMilliseconds(350).Ticks))
                continue;

            annotations[best] += text;
            mapped++;
        }

        if (mapped == 0 || mapped * 3 < readings.Count * 2)
            return null;

        return original.Select((word, i) => word with
        {
            Ruby = Normalize(annotations[i]),
        }).ToArray();
    }

    private static string? Normalize(string? text)
    {
        string? normalized = text?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
