using System.Text.Json;
using MusicLoom.Core.Configuration;

namespace MusicLoom.Core.State;

public class StateStore
{
    private bool _loadedPrimarySuccessfully;

    public StateStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public string BackupPath => Path + ".bak";

    public string? RecoveryWarning { get; private set; }

    public async Task<StateDocument> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        RecoveryWarning = null;
        _loadedPrimarySuccessfully = false;

        if (!File.Exists(Path))
        {
            return new StateDocument();
        }

        try
        {
            StateDocument state = await LoadDocumentAsync(
                Path,
                cancellationToken);
            _loadedPrimarySuccessfully = true;
            return state;
        }
        catch (Exception primaryException)
            when (IsRecoverableStateException(primaryException))
        {
            if (!File.Exists(BackupPath))
            {
                throw new InvalidDataException(
                    $"Could not read MusicLoom state '{Path}'. " +
                    "No usable backup is available.",
                    primaryException);
            }

            try
            {
                StateDocument backup = await LoadDocumentAsync(
                    BackupPath,
                    cancellationToken);
                RecoveryWarning =
                    $"State '{Path}' could not be read and was recovered " +
                    $"from '{BackupPath}'. The damaged primary will not " +
                    "replace the backup on the next save.";
                return backup;
            }
            catch (Exception backupException)
                when (IsRecoverableStateException(backupException))
            {
                throw new InvalidDataException(
                    $"Could not read MusicLoom state '{Path}' or backup " +
                    $"'{BackupPath}'.",
                    new AggregateException(
                        primaryException,
                        backupException));
            }
        }
    }

    public virtual async Task SaveAsync(
        StateDocument state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        string? directory =
            System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath =
            Path + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous
                | FileOptions.SequentialScan
                | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    MusicLoomJsonContext.Default.StateDocument,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            // Preserve the last primary state that this store instance
            // successfully loaded/saved. If LoadAsync recovered from .bak,
            // never overwrite that good backup with the corrupt primary.
            if (File.Exists(Path)
                && _loadedPrimarySuccessfully)
            {
                File.Copy(
                    Path,
                    BackupPath,
                    overwrite: true);
            }

            File.Move(
                temporaryPath,
                Path,
                overwrite: true);

            _loadedPrimarySuccessfully = true;
            RecoveryWarning = null;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public static bool IsSourceStatUnchanged(
        TrackState state,
        FileInfo source)
    {
        return state.SourceSize == source.Length
            && state.SourceLastWriteUtcTicks
                == source.LastWriteTimeUtc.Ticks;
    }

    public static bool IsSourceVersionUnchanged(
        TrackState state,
        FileInfo source,
        ulong quickHash)
    {
        if (!IsSourceStatUnchanged(state, source))
        {
            return false;
        }

        // State created before quick-hash validation is migrated lazily.
        return state.QuickHash == 0
            || state.QuickHash == quickHash;
    }

    public static bool HasDestinationVersion(
        TrackState state)
    {
        return state.DestinationSize.HasValue
            && state.DestinationLastWriteUtcTicks.HasValue;
    }

    public static bool IsDestinationVersionUnchanged(
        TrackState state,
        FileInfo destination)
    {
        return state.DestinationSize == destination.Length
            && state.DestinationLastWriteUtcTicks
                == destination.LastWriteTimeUtc.Ticks;
    }

    public static bool IsEnrichmentFresh(
        TrackState state,
        MusicLoomSettings settings,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Organizer.Lyrics.Enabled)
        {
            return true;
        }

        TimeSpan refreshTtl =
            GetRefreshTtl(settings.Organizer.Lyrics.RefreshIntervalHours);

        return state.Lyrics switch
        {
            LyricsState.Written =>
                !settings.Organizer.Lyrics.OverrideExisting
                || IsFresh(
                    state.LyricsCheckedAt,
                    now,
                    refreshTtl),
            LyricsState.Existing => true,
            LyricsState.Unsupported => true,
            LyricsState.NoLyrics =>
                IsFresh(
                    state.LyricsCheckedAt,
                    now,
                    refreshTtl),
            LyricsState.Unknown
                when state.Recognition
                    == RecognitionState.NoMatch =>
                IsFresh(
                    state.RecognitionCheckedAt,
                    now,
                    refreshTtl),
            _ => false,
        };
    }

    private async Task<StateDocument> LoadDocumentAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous
            | FileOptions.SequentialScan);

        StateDocument? state =
            await JsonSerializer.DeserializeAsync(
                stream,
                MusicLoomJsonContext.Default.StateDocument,
                cancellationToken);

        if (state is null || state.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported MusicLoom state file '{path}'.");
        }

        return state;
    }

    private static bool IsRecoverableStateException(
        Exception exception) =>
        exception is JsonException
        or InvalidDataException
        or IOException
        or UnauthorizedAccessException;

    private static TimeSpan GetRefreshTtl(double hours)
    {
        if (!double.IsFinite(hours) || hours <= 0)
        {
            return TimeSpan.Zero;
        }

        // Prevent invalid/overflowing config values from destabilizing scans.
        return TimeSpan.FromHours(Math.Min(hours, 24 * 365 * 10));
    }

    private static bool IsFresh(
        DateTimeOffset? checkedAt,
        DateTimeOffset now,
        TimeSpan ttl)
    {
        if (checkedAt is not DateTimeOffset timestamp)
        {
            return false;
        }

        TimeSpan age = now - timestamp;
        return age >= TimeSpan.Zero
            && age <= ttl;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A stale uniquely named temp is harmless and must never hide
            // the original save failure.
        }
    }
}
