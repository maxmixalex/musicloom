using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicLoom.Core.Lyrics;

public sealed partial class LrcDocument
{
    private static readonly TimeSpan MergeThreshold = TimeSpan.FromMilliseconds(200);

    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LrcLine> Lines { get; } = [];

    public static LrcDocument Parse(string? text)
    {
        LrcDocument document = new();
        if (string.IsNullOrWhiteSpace(text))
        {
            return document;
        }

        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            Match metadataMatch = MetadataRegex().Match(line);
            if (metadataMatch.Success)
            {
                document.Metadata[metadataMatch.Groups[1].Value.Trim()] = metadataMatch.Groups[2].Value.Trim();
                continue;
            }

            MatchCollection timestamps = TimestampRegex().Matches(line);
            if (timestamps.Count == 0)
            {
                continue;
            }

            string lyricText = TimestampRegex().Replace(line, string.Empty).Trim();
            foreach (Match timestamp in timestamps)
            {
                int minutes = int.Parse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture);
                int seconds = int.Parse(timestamp.Groups[2].Value, CultureInfo.InvariantCulture);
                string fraction = timestamp.Groups[3].Value;
                int milliseconds = fraction.Length == 3
                    ? int.Parse(fraction, CultureInfo.InvariantCulture)
                    : int.Parse(fraction, CultureInfo.InvariantCulture) * 10;

                document.Lines.Add(new LrcLine(
                    TimeSpan.FromMinutes(minutes)
                    + TimeSpan.FromSeconds(seconds)
                    + TimeSpan.FromMilliseconds(milliseconds),
                    lyricText));
            }
        }

        document.Lines.Sort(static (left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return document;
    }

    public static LrcDocument Merge(LrcDocument original, LrcDocument translated)
    {
        LrcDocument merged = new();
        foreach ((string key, string value) in original.Metadata)
        {
            merged.Metadata[key] = value;
        }

        foreach ((string key, string value) in translated.Metadata)
        {
            merged.Metadata[key] = value;
        }

        List<LrcLine> remainingTranslations = [.. translated.Lines];
        foreach (LrcLine originalLine in original.Lines)
        {
            merged.Lines.Add(originalLine);
            LrcLine? bestMatch = null;
            TimeSpan smallestDelta = MergeThreshold;

            foreach (LrcLine translation in remainingTranslations)
            {
                TimeSpan delta = (translation.Timestamp - originalLine.Timestamp).Duration();
                if (delta <= smallestDelta)
                {
                    bestMatch = translation;
                    smallestDelta = delta;
                }
            }

            if (bestMatch is not null)
            {
                merged.Lines.Add(bestMatch with { Timestamp = originalLine.Timestamp });
                remainingTranslations.Remove(bestMatch);
            }
        }

        merged.Lines.AddRange(remainingTranslations);
        merged.Lines.Sort(static (left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return merged;
    }

    public override string ToString()
    {
        StringBuilder builder = new();
        foreach ((string key, string value) in Metadata)
        {
            builder.Append('[').Append(key).Append(':').Append(value).Append("]\n");
        }

        foreach (LrcLine line in Lines)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    [GeneratedRegex(@"\[(\d{2}):(\d{2})\.(\d{2,3})\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"\[(ti|ar|al|by|offset):(.*?)\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MetadataRegex();
}

public sealed record LrcLine(TimeSpan Timestamp, string Text)
{
    public override string ToString()
    {
        long totalMilliseconds = (long)Timestamp.TotalMilliseconds;
        long minutes = totalMilliseconds / 60_000;
        long seconds = totalMilliseconds / 1_000 % 60;
        long milliseconds = totalMilliseconds % 1_000;
        return FormattableString.Invariant($"[{minutes:00}:{seconds:00}.{milliseconds:000}]{Text}");
    }
}
