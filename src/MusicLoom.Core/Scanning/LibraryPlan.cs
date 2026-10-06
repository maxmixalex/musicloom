namespace MusicLoom.Core.Scanning;

public enum PlanEntryStatus
{
    Unchanged,
    New,
    Modified,
    Error,
}

public sealed record PlanEntry(
    string SourcePath,
    string? DestinationPath,
    PlanEntryStatus Status,
    TrackMetadata? Metadata = null,
    string? Detail = null,
    bool NeedsAudioCopy = false);

public sealed record CoverPlanEntry(
    string SourcePath,
    string DestinationPath,
    bool NeedsWrite);

public sealed record PlaylistPlanEntry(
    string SourcePath,
    string DestinationPath,
    string Content,
    bool NeedsWrite);

public sealed record ScanProgress(
    int Processed,
    int Total,
    string CurrentPath,
    string Stage = "tracks");

public sealed record LibraryPlan
{
    public LibraryPlan(
        IReadOnlyList<PlanEntry> entries,
        IReadOnlyList<CoverPlanEntry>? covers = null,
        IReadOnlyList<PlaylistPlanEntry>? playlists = null)
    {
        Entries = entries;
        Covers = covers ?? Array.Empty<CoverPlanEntry>();
        Playlists = playlists ?? Array.Empty<PlaylistPlanEntry>();
    }

    public IReadOnlyList<PlanEntry> Entries { get; }
    public IReadOnlyList<CoverPlanEntry> Covers { get; }
    public IReadOnlyList<PlaylistPlanEntry> Playlists { get; }

    public int TotalCount => Entries.Count;
    public int UnchangedCount => Entries.Count(static entry => entry.Status == PlanEntryStatus.Unchanged);
    public int NewCount => Entries.Count(static entry => entry.Status == PlanEntryStatus.New);
    public int ModifiedCount => Entries.Count(static entry => entry.Status == PlanEntryStatus.Modified);
    public int ErrorCount => Entries.Count(static entry => entry.Status == PlanEntryStatus.Error);

    public int PendingTrackCount => NewCount + ModifiedCount;
    public int AudioCopyCount => Entries.Count(static entry => entry.NeedsAudioCopy);
    public int EnrichmentOnlyCount => Math.Max(0, PendingTrackCount - AudioCopyCount);

    public int CoverWriteCount => Covers.Count(static entry => entry.NeedsWrite);
    public int PlaylistWriteCount => Playlists.Count(static entry => entry.NeedsWrite);
    public int AncillaryPendingCount => CoverWriteCount + PlaylistWriteCount;

    public int PendingCount => PendingTrackCount + AncillaryPendingCount;
}
