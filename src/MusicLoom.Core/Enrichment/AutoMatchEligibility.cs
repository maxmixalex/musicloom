using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;

namespace MusicLoom.Core.Enrichment;

/// <summary>
/// Durable incrementality for explicit NetEase auto-match. A failed lookup
/// is retried only if the underlying audio changed, never on every launch.
/// </summary>
public static class AutoMatchEligibility
{
    public static bool ShouldAttempt(
        PlanEntry entry,
        TrackState? saved)
    {
        if (entry.Status == PlanEntryStatus.Error
            || entry.Metadata is null
            || !File.Exists(entry.SourcePath))
        {
            return false;
        }

        if (saved is null)
        {
            return true;
        }

        if (saved.NeteaseMatchSuppressed
            || saved.NeteaseSongId.HasValue)
        {
            return false;
        }

        if (saved.Recognition != RecognitionState.NoMatch)
        {
            return true;
        }

        FileInfo source = new(entry.SourcePath);
        if (!StateStore.IsSourceStatUnchanged(saved, source)
            || saved.QuickHash == 0)
        {
            return true;
        }

        try
        {
            return !StateStore.IsSourceVersionUnchanged(
                saved, source,
                QuickFileHash.Compute(entry.SourcePath));
        }
        catch (IOException)
        {
            // Retry through the normal recognizer/error handling path.
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
