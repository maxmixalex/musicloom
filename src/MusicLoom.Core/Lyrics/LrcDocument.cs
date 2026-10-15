using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicLoom.Core.Lyrics;

public sealed partial class LrcDocument
{
    // Increment when the NetEase original/translation merge algorithm
    // changes; existing merged cache entries must be re-fetched.
    public const int MergeAlgorithmVersion = 2;

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

            // MusicLoom's private, base64-encoded ruby extension is stored
            // ONLY in the canonical cache. Generic LRC output omits it.
            // It belongs to the immediately preceding enhanced word line.
            if (line.StartsWith("[ml-ruby:", StringComparison.Ordinal)
                && line.EndsWith(']'))
            {
                if (document.Lines.Count > 0
                    && document.Lines[^1].Words is { Count: > 0 } previous)
                {
                    try
                    {
                        byte[] encoded = Convert.FromBase64String(line[9..^1]);
                        // JsonDocument is trim/NativeAOT-safe: avoid the
                        // reflection-based JsonSerializer overloads here.
                        using var metadata = System.Text.Json.JsonDocument.Parse(encoded);
                        if (metadata.RootElement.ValueKind
                            == System.Text.Json.JsonValueKind.Array)
                        {
                            string?[] annotations = metadata.RootElement
                                .EnumerateArray()
                                .Select(static value => value.ValueKind
                                    == System.Text.Json.JsonValueKind.String
                                        ? value.GetString() : null)
                                .ToArray();
                            if (annotations.Length == previous.Count
                                && annotations.Length <= 256)
                            {
                                LrcLine last = document.Lines[^1];
                                document.Lines[^1] = last with
                                {
                                    Words = previous.Select((word, i) =>
                                        word with { Ruby = annotations[i] }).ToArray(),
                                };
                            }
                        }
                    }
                    catch (FormatException) { }
                    catch (System.Text.Json.JsonException) { }
                }
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
            (string plainText, IReadOnlyList<LrcWord>? words) =
                ParseEnhancedWords(lyricText);
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
                    plainText, words));
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

    /// <summary>Shift generated output without modifying source lyrics.</summary>
    public static LrcDocument Shift(LrcDocument source, int offsetMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(source);
        LrcDocument result = new();
        long inheritedOffset = 0;
        foreach ((string key, string value) in source.Metadata)
        {
            if (string.Equals(key, "offset",
                    StringComparison.OrdinalIgnoreCase)
                && long.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long parsedOffset))
            {
                // Normalize an existing LRC offset into timestamps exactly
                // once, rather than double-applying it in the output player.
                inheritedOffset = parsedOffset;
                continue;
            }
            result.Metadata[key] = value;
        }
        TimeSpan shift = TimeSpan.FromMilliseconds(
            (double)offsetMilliseconds + inheritedOffset);
        foreach (LrcLine line in source.Lines)
        {
            TimeSpan timestamp = line.Timestamp + shift;
            result.Lines.Add(line with
            {
                Timestamp = timestamp < TimeSpan.Zero ? TimeSpan.Zero : timestamp,
                Words = line.Words?.Select(word => word with
                {
                    Start = word.Start + shift < TimeSpan.Zero
                        ? TimeSpan.Zero : word.Start + shift,
                    End = word.End + shift < TimeSpan.Zero
                        ? TimeSpan.Zero : word.End + shift,
                }).ToArray(),
            });
        }
        return result;
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

    public override string ToString() => Serialize(includeRuby: true);

    /// <summary>
    /// Standard LRC for destination sidecars and audio tags. Internal ruby
    /// annotations do not leak into players that cannot interpret them.
    /// </summary>
    public string ToStandardLrcString() => Serialize(includeRuby: false);

    private string Serialize(bool includeRuby)
    {
        StringBuilder builder = new();
        foreach ((string key, string value) in Metadata)
            builder.Append('[').Append(key).Append(':').Append(value).Append("]\n");

        foreach (LrcLine line in Lines)
        {
            builder.Append(line).Append('\n');
            if (includeRuby && line.Words is { Count: > 0 and <= 256 } words
                && words.Any(static word => !string.IsNullOrWhiteSpace(word.Ruby)))
            {
                using MemoryStream buffer = new();
                using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
                {
                    writer.WriteStartArray();
                    foreach (LrcWord word in words)
                    {
                        if (word.Ruby is null) writer.WriteNullValue();
                        else writer.WriteStringValue(word.Ruby);
                    }
                    writer.WriteEndArray();
                    writer.Flush();
                }
                string marker = Convert.ToBase64String(buffer.ToArray());
                builder.Append("[ml-ruby:").Append(marker).Append("]\n");
            }
        }
        return builder.ToString().TrimEnd('\n');
    }

    private static (string Text, IReadOnlyList<LrcWord>? Words)
        ParseEnhancedWords(string content)
    {
        MatchCollection marks = EnhancedTimestampRegex().Matches(content);
        if (marks.Count == 0)
        {
            return (content, null);
        }

        List<LrcWord> words = [];
        // A closing time marker has no following text. The marker's
        // absolute time still supplies the preceding word's end time.
        for (int i = 0; i < marks.Count; i++)
        {
            Match current = marks[i];
            int contentStart = current.Index + current.Length;
            int contentEnd = i + 1 < marks.Count
                ? marks[i + 1].Index : content.Length;
            string text = content[contentStart..contentEnd];
            if (i == 0 && current.Index > 0)
            {
                text = content[..current.Index] + text;
            }
            if (text.Length == 0)
            {
                continue;
            }

            TimeSpan start = ParseClock(current);
            TimeSpan end = i + 1 < marks.Count
                ? ParseClock(marks[i + 1]) : start;
            words.Add(new LrcWord(start, end < start ? start : end, text));
        }

        return words.Count > 0
            ? (string.Concat(words.Select(static word => word.Text)),
                words.ToArray())
            : (EnhancedTimestampRegex().Replace(content, ""), null);
    }

    private static TimeSpan ParseClock(Match time)
    {
        int minutes = int.Parse(time.Groups[1].Value,
            CultureInfo.InvariantCulture);
        int seconds = int.Parse(time.Groups[2].Value,
            CultureInfo.InvariantCulture);
        string fraction = time.Groups[3].Value;
        int millis = int.Parse(fraction, CultureInfo.InvariantCulture)
            * (fraction.Length == 2 ? 10 : 1);
        return TimeSpan.FromMinutes(minutes)
            + TimeSpan.FromSeconds(seconds)
            + TimeSpan.FromMilliseconds(millis);
    }

    [GeneratedRegex(@"\[(\d{2,}):(\d{2})\.(\d{2,3})\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"<(\d{2,}):(\d{2})\.(\d{2,3})>", RegexOptions.CultureInvariant)]
    private static partial Regex EnhancedTimestampRegex();

    [GeneratedRegex(@"\[(ti|ar|al|by|offset):(.*?)\]", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MetadataRegex();
}

/// <summary>
/// Word timing is optional; the canonical plain line remains in Text.
/// Enhanced LRC serializes each word's absolute start and a final end tag.
/// </summary>
public sealed record LrcWord(TimeSpan Start, TimeSpan End, string Text,
    string? Ruby = null);

public sealed record LrcLine(
    TimeSpan Timestamp,
    string Text,
    IReadOnlyList<LrcWord>? Words = null)
{
    public override string ToString()
    {
        string prefix = FormatTag(Timestamp, '[', ']');
        if (Words is not { Count: > 0 })
        {
            return prefix + Text;
        }

        StringBuilder enhanced = new(prefix);
        foreach (LrcWord word in Words)
        {
            enhanced.Append(FormatTag(word.Start, '<', '>'));
            enhanced.Append(word.Text);
        }
        enhanced.Append(FormatTag(Words[^1].End, '<', '>'));
        return enhanced.ToString();
    }

    private static string FormatTag(TimeSpan time, char left, char right)
    {
        long value = Math.Max(0, (long)time.TotalMilliseconds);
        long minutes = value / 60_000;
        long seconds = value / 1_000 % 60;
        long milliseconds = value % 1_000;
        return FormattableString.Invariant(
            $"{left}{minutes:00}:{seconds:00}.{milliseconds:000}{right}");
    }
}
