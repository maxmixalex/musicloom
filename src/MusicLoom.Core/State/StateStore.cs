using System.Text.Json;
using MusicLoom.Core.Configuration;

namespace MusicLoom.Core.State;

public class StateStore
{
    public StateStore(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public async Task<StateDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path))
        {
            return new StateDocument();
        }

        await using FileStream stream = File.OpenRead(Path);
        StateDocument? state = await JsonSerializer.DeserializeAsync(
            stream,
            MusicLoomJsonContext.Default.StateDocument,
            cancellationToken);

        if (state is null || state.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported MusicLoom state file '{Path}'.");
        }

        return state;
    }

    public virtual async Task SaveAsync(StateDocument state, CancellationToken cancellationToken = default)
    {
        string? directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = Path + ".tmp";
        await using (FileStream stream = new(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                state,
                MusicLoomJsonContext.Default.StateDocument,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temporaryPath, Path, overwrite: true);
    }

    public static readonly TimeSpan NegativeEnrichmentTtl = TimeSpan.FromHours(24);

    public static bool IsSourceVersionUnchanged(TrackState state, FileInfo source)
    {
        return state.SourceSize == source.Length
            && state.SourceLastWriteUtcTicks == source.LastWriteTimeUtc.Ticks;
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

        return state.Lyrics switch
        {
            LyricsState.Written => true,
            LyricsState.Existing => true,
            LyricsState.Unsupported => true,
            LyricsState.NoLyrics => IsFresh(state.LyricsCheckedAt, now),
            LyricsState.Unknown when state.Recognition == RecognitionState.NoMatch =>
                IsFresh(state.RecognitionCheckedAt, now),
            _ => false,
        };
    }

    private static bool IsFresh(
        DateTimeOffset? checkedAt,
        DateTimeOffset now)
    {
        if (checkedAt is not DateTimeOffset timestamp)
        {
            return false;
        }

        TimeSpan age = now - timestamp;
        return age <= NegativeEnrichmentTtl;
    }
}
