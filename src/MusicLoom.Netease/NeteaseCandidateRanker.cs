using System.Text;

namespace MusicLoom.Netease;

public static class NeteaseCandidateRanker
{
    public static IReadOnlyList<NeteaseMatchCandidate>
        Rank(
            IEnumerable<NeteaseMatchCandidate> candidates,
            double? localDurationMs,
            string? title = null,
            string? artist = null,
            string? album = null)
    {
        ArgumentNullException.ThrowIfNull(
            candidates);

        return candidates
            .OrderBy(candidate =>
                DurationBucket(
                    localDurationMs,
                    candidate.DurationMs))
            .ThenByDescending(candidate =>
                MetadataScore(
                    candidate,
                    title,
                    artist,
                    album))
            .ThenBy(candidate =>
                DurationDistance(
                    localDurationMs,
                    candidate.DurationMs))
            .ThenByDescending(candidate =>
                candidate.Score
                    ?? double.MinValue)
            .ThenBy(candidate =>
                candidate.TrackId)
            .ToArray();
    }

    private static int DurationBucket(
        double? localDurationMs,
        long? candidateDurationMs)
    {
        if (localDurationMs is not double local
            || !double.IsFinite(local)
            || local <= 0)
        {
            return 0;
        }

        if (candidateDurationMs is not long candidate
            || candidate <= 0)
        {
            return 100;
        }

        double difference =
            Math.Abs(
                candidate - local);

        return difference switch
        {
            <= 1_500 => 0,
            <= 5_000 => 1,
            <= 15_000 => 2,
            <= 30_000 => 3,
            _ => 4
                + (int)Math.Min(
                    95,
                    difference
                    / 60_000),
        };
    }

    private static double DurationDistance(
        double? localDurationMs,
        long? candidateDurationMs)
    {
        if (localDurationMs is not double local
            || !double.IsFinite(local)
            || local <= 0)
        {
            return 0;
        }

        if (candidateDurationMs is not long candidate
            || candidate <= 0)
        {
            return double.MaxValue;
        }

        return Math.Abs(
            candidate - local);
    }

    private static int MetadataScore(
        NeteaseMatchCandidate candidate,
        string? title,
        string? artist,
        string? album)
    {
        int score = 0;

        score += SimilarityScore(
            title,
            candidate.Name)
            * 5;
        score += SimilarityScore(
            artist,
            candidate.Artist)
            * 3;
        score += SimilarityScore(
            album,
            candidate.Album);

        return score;
    }

    private static int SimilarityScore(
        string? expected,
        string? actual)
    {
        string left =
            Normalize(expected);
        string right =
            Normalize(actual);

        if (left.Length == 0
            || right.Length == 0)
        {
            return 0;
        }

        if (string.Equals(
                left,
                right,
                StringComparison.Ordinal))
        {
            return 4;
        }

        if (left.Contains(
                right,
                StringComparison.Ordinal)
            || right.Contains(
                left,
                StringComparison.Ordinal))
        {
            return 2;
        }

        string[] leftTokens =
            Tokenize(left);
        string[] rightTokens =
            Tokenize(right);

        if (leftTokens.Length == 0
            || rightTokens.Length == 0)
        {
            return 0;
        }

        int common =
            leftTokens
                .Intersect(
                    rightTokens,
                    StringComparer.Ordinal)
                .Count();

        return common > 0
            ? 1
            : 0;
    }

    private static string[] Tokenize(
        string value) =>
        value.Split(
            [' ', ';', '/', '・', '·', '-', '_'],
            StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries);

    private static string Normalize(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        StringBuilder builder = new();

        foreach (char character
            in value.Normalize(
                NormalizationForm.FormKC))
        {
            if (char.IsLetterOrDigit(
                    character))
            {
                builder.Append(
                    char.ToLowerInvariant(
                        character));
            }
            else if (char.IsWhiteSpace(
                         character)
                || character is ';'
                    or '/'
                    or '・'
                    or '·'
                    or '-'
                    or '_')
            {
                builder.Append(' ');
            }
        }

        return string.Join(
            ' ',
            builder
                .ToString()
                .Split(
                    ' ',
                    StringSplitOptions
                        .RemoveEmptyEntries
                    | StringSplitOptions
                        .TrimEntries));
    }
}
