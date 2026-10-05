using System.Text.Json;
using MusicLoom.Core.Configuration;

namespace MusicLoom.Core.State;

public sealed class StateStore
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

    public async Task SaveAsync(StateDocument state, CancellationToken cancellationToken = default)
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

    public static bool IsSourceUnchanged(TrackState state, FileInfo source)
    {
        return state.RecipeVersion == SyncRecipe.CurrentVersion
            && state.SourceSize == source.Length
            && state.SourceLastWriteUtcTicks == source.LastWriteTimeUtc.Ticks;
    }
}
