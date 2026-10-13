namespace MusicLoom.Core.Lyrics;

/// <summary>
/// Canonical presentation of a persisted LRC document. A timestamp can have
/// two distinct lyric rows (original followed by translation). Playback,
/// previews, and copy-as-text must use the same projection.
/// </summary>
public sealed record LrcPresentationLine(
    TimeSpan? Timestamp,
    string Original,
    string? Translation,
    IReadOnlyList<LrcWord>? OriginalWords = null,
    IReadOnlyList<LrcWord>? TranslationWords = null);

public static class LrcPresentation
{
    public static IReadOnlyList<LrcPresentationLine> Project(
        LrcDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<LrcPresentationLine> result = [];
        foreach (IGrouping<TimeSpan, LrcLine> group
            in document.Lines.GroupBy(static line => line.Timestamp))
        {
            string[] texts = group
                .Select(static line => line.Text.Trim())
                .Where(static value => value.Length > 0)
                .ToArray();

            foreach (string credit in texts
                .Where(LrcDocument.LooksLikeCreditLine))
            {
                result.Add(new LrcPresentationLine(
                    group.Key, credit, null));
            }

            string[] lyrics = texts
                .Where(static value =>
                    !LrcDocument.LooksLikeCreditLine(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (lyrics.Length == 0)
            {
                continue;
            }

            (string original, string? translation) =
                SplitInline(lyrics[0]);

            // The canonical QRC/KRC words belong to the original line,
            // not an adjacent translation or romaji row sharing its time.
            // Preserve them only when their concatenated text really
            // equals what the lyric viewer will display.
            IReadOnlyList<LrcWord>? originalWords = group
                .Where(line => string.Equals(
                    line.Text.Trim(), lyrics[0], StringComparison.Ordinal))
                .Select(static line => line.Words)
                .FirstOrDefault(words => words is { Count: > 0 }
                    && string.Equals(
                        string.Concat(words.Select(static w => w.Text)),
                        original,
                        StringComparison.Ordinal));

            if (lyrics.Length > 1)
            {
                string[] additional = lyrics.Skip(1)
                    .Where(value => !SameText(original, value))
                    .ToArray();
                if (additional.Length > 0)
                {
                    translation = string.Join(
                        Environment.NewLine, additional);
                }
            }

            // QQ's translated QRC may have its own independently timed
            // words. Preserve them when there is exactly one matching
            // displayed translation; never assign timings to a composite
            // "romaji + translation" string or fabricated inline split.
            IReadOnlyList<LrcWord>? translationWords =
                string.IsNullOrWhiteSpace(translation)
                    ? null
                    : group
                        .Where(line => string.Equals(
                            line.Text.Trim(),
                            translation,
                            StringComparison.Ordinal))
                        .Select(static line => line.Words)
                        .FirstOrDefault(words => words is { Count: > 0 }
                            && string.Equals(
                                string.Concat(words.Select(static w => w.Text)),
                                translation,
                                StringComparison.Ordinal));

            result.Add(new LrcPresentationLine(
                group.Key,
                original,
                translation,
                originalWords,
                translationWords));
        }

        return result;
    }

    public static (string Original, string? Translation)
        SplitInline(string text)
    {
        if (text.Contains("://", StringComparison.Ordinal)
            || LrcDocument.LooksLikeCreditLine(text))
        {
            return (text, null);
        }

        int slash = text.IndexOf('/');
        if (slash <= 0
            || slash == text.Length - 1
            || slash != text.LastIndexOf('/'))
        {
            return (text, null);
        }

        string left = text[..slash].Trim();
        string right = text[(slash + 1)..].Trim();

        if (left.Length == 0 || right.Length == 0
            || SameText(left, right)
            || !ClearlyDifferentScripts(left, right))
        {
            // Slash punctuation is not a trustworthy translation delimiter.
            // Keep same-script expressions such as "and/or" or "yes / no"
            // intact rather than inventing a translated line.
            return (text, null);
        }

        return (left, right);
    }

    private static bool SameText(string a, string b)
    {
        return string.Equals(
            new string(a.Where(char.IsLetterOrDigit).ToArray()),
            new string(b.Where(char.IsLetterOrDigit).ToArray()),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool ClearlyDifferentScripts(string left, string right)
    {
        static bool Latin(string s) =>
            s.Any(ch => ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        static bool Han(string s) =>
            s.Any(ch => ch is >= '\u3400' and <= '\u9FFF');
        static bool Kana(string s) =>
            s.Any(ch => ch is >= '\u3040' and <= '\u30FF');
        static bool Hangul(string s) =>
            s.Any(ch => ch is >= '\uAC00' and <= '\uD7AF');
        static bool Cyrillic(string s) =>
            s.Any(ch => ch is >= '\u0400' and <= '\u052F');

        bool leftLatin = Latin(left);
        bool rightLatin = Latin(right);
        bool leftEastAsian = Han(left) || Kana(left) || Hangul(left);
        bool rightEastAsian = Han(right) || Kana(right) || Hangul(right);

        return (leftLatin && rightEastAsian && !leftEastAsian)
            || (rightLatin && leftEastAsian && !rightEastAsian)
            || (Cyrillic(left) && (rightLatin || rightEastAsian))
            || (Cyrillic(right) && (leftLatin || leftEastAsian))
            || (Kana(left) && Han(right) && !Kana(right))
            || (Kana(right) && Han(left) && !Kana(left));
    }
}
