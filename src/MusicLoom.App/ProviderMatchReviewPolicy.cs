using MusicLoom.Core.Scanning;
using MusicLoom.Core.Synchronization;
using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

/// <summary>
/// A provider auto-match is not a human confirmation. Just like NetEase,
/// an unconfirmed match requires review only when its metadata is suspect.
/// The persisted Confirmed flag remains explicit user intent, not a guessed
/// approval; old databases are reclassified without changing any rows.
/// </summary>
internal static class ProviderMatchReviewPolicy
{
    internal static bool NeedsReview(PlanEntry? local, StoredProviderMatch? match)
    {
        if (match?.IsMatched != true || match.Confirmed)
            return false;

        CloudLyricsCandidate candidate = match.Candidate!;
        if (local?.Metadata is null
            || string.IsNullOrWhiteSpace(candidate.Title)
            || string.IsNullOrWhiteSpace(candidate.Artist))
            return true;

        // Reuse exactly the NetEase title/artist normalization/compatibility
        // logic, including Chinese normalization and artist credit subsets.
        string recordedArtist = string.IsNullOrWhiteSpace(local.Metadata.Artist)
            ? local.Metadata.AlbumArtist ?? string.Empty
            : local.Metadata.Artist;
        TrackSyncWarning? mismatch = SyncExecutor.DetectMetadataWarning(
            local.SourcePath, local.Metadata.Title, recordedArtist,
            candidate.Title, candidate.Artist);
        if (mismatch is not null)
            return true;

        // A different release (e.g. Live/Remix vs studio) can carry
        // matching titles and artists but different lyrics. Only compare
        // when both records actually provide an album.
        return !string.IsNullOrWhiteSpace(local.Metadata.Album)
            && !string.IsNullOrWhiteSpace(candidate.Album)
            && !SyncExecutor.MetadataValuesEquivalent(
                local.Metadata.Album, candidate.Album);
    }

    internal static bool AnyNeedsReview(
        PlanEntry local,
        IReadOnlyDictionary<CloudLyricsSource, StoredProviderMatch>? matches) =>
        matches is not null && matches.Values.Any(match =>
            NeedsReview(local, match));

    internal static bool IsReady(
        PlanEntry local, StoredProviderMatch? match) =>
        match?.IsMatched == true && !NeedsReview(local, match);
}
