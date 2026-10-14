using System.Text;

namespace MusicLoom.LyricsProviders;

/// <summary>
/// Rank QQ/KuGou song candidates with the same title, artist, album
/// priorities offered by the match dialog; never mix provider caches.
/// </summary>
public static class CloudLyricsCandidateRanker
{
    public static IReadOnlyList<CloudLyricsCandidate> Rank(
        IEnumerable<CloudLyricsCandidate> candidates,
        string? title,
        string? artist,
        string? album)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Select((song, index) =>
                (Song: song, Index: index,
                 Score: Score(song, title, artist, album)))
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Index)
            .Select(static item => item.Song)
            .ToArray();
    }

    private static double Score(
        CloudLyricsCandidate song,
        string? title,
        string? artist,
        string? album) =>
        8 * Match(title, song.Title) +
        4 * Match(artist, song.Artist) +
        2 * Match(album, song.Album);

    private static double Match(string? query, string? candidate)
    {
        string expected = Normalize(query);
        if (expected.Length == 0)
            return 0;

        string actual = Normalize(candidate);
        if (actual == expected)
            return 1;

        if (actual.Contains(expected, StringComparison.Ordinal)
            || expected.Contains(actual, StringComparison.Ordinal)
                && actual.Length > 1)
            return 0.63;

        // A candidate can have multiple participating artists.
        string[] words = expected.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return 0;
        int matches = words.Count(word =>
            actual.Contains(word, StringComparison.Ordinal));
        return 0.33 * matches / words.Length;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // FormKC folds full/half-width text while preserving CJK semantics.
        string input = value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        StringBuilder text = new(input.Length);
        bool separator = false;
        foreach (char c in input)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (separator && text.Length > 0)
                    text.Append(' ');
                separator = false;
                text.Append(c);
            }
            else
            {
                separator = true;
            }
        }
        return text.ToString();
    }
}
