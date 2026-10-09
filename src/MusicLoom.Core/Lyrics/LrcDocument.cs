using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicLoom.Core.Lyrics;

public sealed partial class LrcDocument
{
    private static readonly TimeSpan MergeThreshold = TimeSpan.FromMilliseconds(450);

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

        // Preserve input order when the original and a translation share the
        // same timestamp. List.Sort is unstable and can swap those roles.
        LrcLine[] ordered = document.Lines
            .OrderBy(static line => line.Timestamp)
            .ToArray();
        document.Lines.Clear();
        document.Lines.AddRange(ordered);
        return document;
    }

    public static LrcDocument Merge(
        LrcDocument original,
        LrcDocument translated)
    {
        LrcDocument merged = new();
        foreach ((string key, string value)
            in original.Metadata)
        {
            merged.Metadata[key] = value;
        }

        foreach ((string key, string value)
            in translated.Metadata)
        {
            merged.Metadata[key] = value;
        }

        bool[] usedTranslations =
            new bool[translated.Lines.Count];

        int searchStart = 0;

        for (int originalIndex = 0;
            originalIndex
                < original.Lines.Count;
            originalIndex++)
        {
            LrcLine originalLine =
                original.Lines[
                    originalIndex];

            LrcLine? nextOriginal =
                original.Lines
                    .Skip(originalIndex + 1)
                    .FirstOrDefault(static line =>
                        !string.IsNullOrWhiteSpace(line.Text)
                        && !LooksLikeCreditLine(line.Text));

            merged.Lines.Add(originalLine);

            // Credits, headers and deliberately empty timing markers have
            // no translated counterpart. Matching them consumed the first
            // real translation and shifted the rest of the song.
            if (string.IsNullOrWhiteSpace(originalLine.Text)
                || LooksLikeCreditLine(originalLine.Text))
            {
                continue;
            }

            int bestIndex = -1;
            TimeSpan smallestDelta =
                MergeThreshold
                + TimeSpan.FromTicks(1);

            for (int i = searchStart;
                i < translated.Lines.Count;
                i++)
            {
                LrcLine translation =
                    translated.Lines[i];

                if (string.IsNullOrWhiteSpace(translation.Text)
                    || LooksLikeCreditLine(translation.Text))
                {
                    continue;
                }

                TimeSpan signedDelta =
                    translation.Timestamp
                    - originalLine.Timestamp;

                if (signedDelta
                    > MergeThreshold)
                {
                    break;
                }

                TimeSpan delta =
                    signedDelta.Duration();

                if (delta > MergeThreshold
                    || delta >= smallestDelta)
                {
                    continue;
                }

                if (nextOriginal is not null)
                {
                    TimeSpan nextDelta =
                        (translation.Timestamp
                            - nextOriginal.Timestamp)
                        .Duration();

                    if (nextDelta < delta)
                    {
                        continue;
                    }
                }

                bestIndex = i;
                smallestDelta = delta;
            }

            if (bestIndex < 0)
            {
                continue;
            }

            LrcLine best =
                translated.Lines[
                    bestIndex];

            // Some tlyric feeds repeat the original in altered punctuation
            // rather than providing a translation. Do not display those as
            // a second line.
            if (!SameLyricText(originalLine.Text, best.Text))
            {
                merged.Lines.Add(
                    best with
                    {
                        Timestamp = originalLine.Timestamp,
                    });
            }

            usedTranslations[bestIndex] = true;

            // Translation matching is monotonic. Once a later translation
            // has been paired, an even later original line cannot go back
            // and steal an earlier translation.
            searchStart =
                bestIndex + 1;
        }

        for (int i = 0;
            i < translated.Lines.Count;
            i++)
        {
            if (!usedTranslations[i]
                && !string.IsNullOrWhiteSpace(
                    translated.Lines[i].Text)
                && !original.Lines.Any(source =>
                    !string.IsNullOrWhiteSpace(source.Text)
                    && SameLyricText(
                        source.Text,
                        translated.Lines[i].Text)
                    && (source.Timestamp - translated.Lines[i].Timestamp)
                        .Duration() <= MergeThreshold))
            {
                merged.Lines.Add(translated.Lines[i]);
            }
        }

        // Stable ordering is required: original first, translation second.
        LrcLine[] ordered = merged.Lines
            .OrderBy(static line => line.Timestamp)
            .ToArray();
        merged.Lines.Clear();
        merged.Lines.AddRange(ordered);

        return merged;
    }

    private static bool SameLyricText(
        string left,
        string right)
    {
        static string Normalize(string value)
        {
            string normalized =
                value.Normalize(NormalizationForm.FormKC);
            return new string(normalized
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        }

        return string.Equals(
            Normalize(left),
            Normalize(right),
            StringComparison.Ordinal);
    }

    public static bool LooksLikeCreditLine(
        string text)
    {
        int asciiColon =
            text.IndexOf(':');
        int fullWidthColon =
            text.IndexOf('：');

        int colon =
            asciiColon < 0
                ? fullWidthColon
                : fullWidthColon < 0
                    ? asciiColon
                    : Math.Min(
                        asciiColon,
                        fullWidthColon);

        if (colon <= 0 || colon > 24)
        {
            return false;
        }

        string label =
            text[..colon].Trim();

        return CreditLabels.Contains(
            label);
    }

    private static readonly HashSet<string>
        CreditLabels =
            new(
                [
                    "器乐",
                    "演奏",
                    "作词",
                    "作詞",
                    "词",
                    "詞",
                    "作曲",
                    "曲",
                    "编曲",
                    "編曲",
                    "制作",
                    "制作人",
                    "演唱",
                    "和声",
                    "和聲",
                    "吉他",
                    "贝斯",
                    "貝斯",
                    "鼓",
                    "键盘",
                    "鍵盤",
                    "弦乐",
                    "弦楽",
                    "混音",
                    "母带",
                    "母帶",
                    "录音",
                    "錄音",
                    "监制",
                    "監製",
                    "lyrics",
                    "lyricist",
                    "composer",
                    "arranger",
                    "arrangement",
                    "instrumental",
                    "guitar",
                    "bass",
                    "drums",
                    "keyboard",
                    "producer",
                    "mix",
                    "mixing",
                    "mastering",
                    "recording",
                ],
                StringComparer.OrdinalIgnoreCase);

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
