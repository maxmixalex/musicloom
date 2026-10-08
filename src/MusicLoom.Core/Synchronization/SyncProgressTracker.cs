namespace MusicLoom.Core.Synchronization;

/// <summary>
/// Rejects out-of-order progress notifications for tracks whose current run
/// has already reached a terminal result. A failure is allowed to supersede
/// a prior terminal notification if persistence failed after it was reported.
/// </summary>
public sealed class SyncProgressTracker
{
    private readonly Dictionary<string, string> _terminalStages =
        new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldApply(SyncExecutionProgress progress)
    {
        if (_terminalStages.TryGetValue(
                progress.SourcePath,
                out string? terminal))
        {
            if (terminal == "failed" || progress.Stage != "failed")
            {
                return false;
            }
        }

        if (progress.Stage is "complete" or "failed"
            || (progress.Stage == "match"
                && progress.Warning is not null))
        {
            _terminalStages[progress.SourcePath] =
                progress.Stage;
        }

        return true;
    }
}
