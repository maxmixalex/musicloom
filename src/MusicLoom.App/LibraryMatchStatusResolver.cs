using MusicLoom.Core.Configuration;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Core.Synchronization;

namespace MusicLoom.App;

internal sealed record LibraryMatchRowState(
    PlanEntry Entry,
    string Kind);

/// <summary>
/// Reconcile only the changed match rows against persisted sync state.
/// This does not rescan or repopulate the ListView, so existing selection
/// and explicit filters are never reapplied by a match action.
/// </summary>
internal static class LibraryMatchStatusResolver
{
    public static IReadOnlyDictionary<string, LibraryMatchRowState> Reconcile(
        IEnumerable<PlanEntry> entries,
        StateDocument state,
        MusicLoomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);

        PlanEntry[] changed = entries
            .DistinctBy(
                static entry => entry.SourcePath,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        LibraryPlan reconciled = CachedPlanReconciler.Reconcile(
            new LibraryPlan(changed), state, settings);
        return reconciled.Entries.ToDictionary(
            static entry => entry.SourcePath,
            entry => new LibraryMatchRowState(
                entry, GetKind(entry, state)),
            StringComparer.OrdinalIgnoreCase);
    }

    public static string GetKind(
        PlanEntry entry,
        StateDocument? state)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Status == PlanEntryStatus.Error)
        {
            return "error";
        }

        if (state is not null
            && state.Tracks.TryGetValue(
                entry.SourcePath, out TrackState? track)
            && SyncExecutor.DetectMatchWarning(entry, track) is not null)
        {
            return "warning";
        }

        return entry.Status == PlanEntryStatus.Unchanged
            ? "synced"
            : "pending";
    }
}
