using MusicLoom.Core.Configuration;
using MusicLoom.Core.State;

namespace MusicLoom.Core.Scanning;

/// <summary>
/// Reconciles the startup-only library snapshot with the latest persisted
/// synchronization state. This is a display optimization, not a substitute
/// for LibraryScanner before performing any file operations.
/// </summary>
public static class CachedPlanReconciler
{
    public static LibraryPlan Reconcile(
        LibraryPlan snapshot,
        StateDocument state,
        MusicLoomSettings settings,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);

        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;

        PlanEntry[] entries = snapshot.Entries
            .Select(entry => ReconcileEntry(
                entry, state, settings, timestamp))
            .ToArray();

        return new LibraryPlan(
            entries, snapshot.Covers, snapshot.Playlists);
    }

    private static PlanEntry ReconcileEntry(
        PlanEntry entry,
        StateDocument state,
        MusicLoomSettings settings,
        DateTimeOffset now)
    {
        if (entry.Status == PlanEntryStatus.Error
            || !state.Tracks.TryGetValue(
                entry.SourcePath, out TrackState? stored)
            || !File.Exists(entry.SourcePath))
        {
            return entry;
        }

        FileInfo source = new(entry.SourcePath);
        bool sourceMatches =
            StateStore.IsSourceStatUnchanged(stored, source);

        bool destinationMatches =
            entry.DestinationPath is string target
            && stored.DestinationPath is string previous
            && string.Equals(
                Path.GetFullPath(target),
                Path.GetFullPath(previous),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);

        bool destinationExists =
            entry.DestinationPath is string existing
            && File.Exists(existing);

        bool destinationVersionMatches =
            destinationExists
            && (!StateStore.HasDestinationVersion(stored)
                || StateStore.IsDestinationVersionUnchanged(
                    stored,
                    new FileInfo(entry.DestinationPath!)));

        bool copyRequired =
            !sourceMatches
            || !destinationMatches
            || !destinationVersionMatches;

        bool complete = !copyRequired
            && SyncRecipe.IsCompatibleForLocalSync(
                stored, settings)
            && StateStore.IsEnrichmentFresh(
                stored, settings, now);

        return entry with
        {
            Status = complete
                ? PlanEntryStatus.Unchanged
                : PlanEntryStatus.Modified,
            NeedsAudioCopy = copyRequired,
            Detail = complete
                ? null
                : copyRequired
                    ? "Source or destination requires revalidation."
                    : "Enrichment requires synchronization.",
        };
    }
}
