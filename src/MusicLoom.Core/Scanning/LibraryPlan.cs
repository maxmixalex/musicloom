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
    string? Detail = null);

public sealed record ScanProgress(
    int Processed,
    int Total,
    string CurrentPath);

public sealed record LibraryPlan(IReadOnlyList<PlanEntry> Entries)
{
    public int TotalCount => Entries.Count;
    public int UnchangedCount => Entries.Count(static entry => entry.Status == PlanEntryStatus.Unchanged);
    public int NewCount => Entries.Count(static entry => entry.Status == PlanEntryStatus.New);
    public int ModifiedCount => Entries.Count(static entry => entry.Status == PlanEntryStatus.Modified);
    public int ErrorCount => Entries.Count(static entry => entry.Status == PlanEntryStatus.Error);
    public int PendingCount => NewCount + ModifiedCount;
}
