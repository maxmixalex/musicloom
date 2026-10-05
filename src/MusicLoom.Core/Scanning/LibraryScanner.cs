using MusicLoom.Core.Configuration;
using MusicLoom.Core.Planning;
using MusicLoom.Core.State;

namespace MusicLoom.Core.Scanning;

public sealed class LibraryScanner
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3",
        ".flac",
        ".m4a",
        ".ogg",
        ".wav",
        ".opus",
        ".wma",
    };

    private readonly ITrackMetadataReader _metadataReader;

    public LibraryScanner(ITrackMetadataReader metadataReader)
    {
        _metadataReader = metadataReader;
    }

    public async Task<LibraryPlan> BuildPlanAsync(
        MusicLoomSettings settings,
        StateDocument state,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string sourceRoot = Path.GetFullPath(settings.Path.SourceDirectory);
        string destinationRoot = Path.GetFullPath(settings.Path.DestinationDirectory);

        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"Source library does not exist: {sourceRoot}");
        }

        EnumerationOptions enumerationOptions = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        string[] sourcePaths = Directory
            .EnumerateFiles(sourceRoot, "*", enumerationOptions)
            .Where(static path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        List<PlanEntry> entries = new(sourcePaths.Length);
        for (int index = 0; index < sourcePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourcePath = sourcePaths[index];
            progress?.Report(new ScanProgress(index, sourcePaths.Length, sourcePath));

            FileInfo source = new(sourcePath);
            if (TryGetState(state, sourcePath, out TrackState? trackState)
                && trackState is not null
                && StateStore.IsSourceUnchanged(trackState, source))
            {
                if (!string.IsNullOrWhiteSpace(trackState.DestinationPath)
                    && File.Exists(trackState.DestinationPath))
                {
                    entries.Add(new PlanEntry(
                        sourcePath,
                        trackState.DestinationPath,
                        PlanEntryStatus.Unchanged));
                }
                else
                {
                    entries.Add(new PlanEntry(
                        sourcePath,
                        trackState.DestinationPath,
                        PlanEntryStatus.Modified,
                        Detail: "Destination file is missing."));
                }

                continue;
            }

            try
            {
                TrackMetadata metadata = await _metadataReader.ReadAsync(sourcePath, cancellationToken);
                MusicFile musicFile = new(
                    sourcePath,
                    Path.GetDirectoryName(sourcePath) ?? sourceRoot,
                    metadata.Artist,
                    metadata.AlbumArtist,
                    metadata.Album,
                    metadata.HasEmbeddedArt,
                    trackState?.NeteaseSongId);

                string destinationPath = LibraryLayout.GetDestinationPath(musicFile, destinationRoot);
                PlanEntryStatus status = trackState is null
                    ? PlanEntryStatus.New
                    : PlanEntryStatus.Modified;

                entries.Add(new PlanEntry(
                    sourcePath,
                    destinationPath,
                    status,
                    metadata,
                    status == PlanEntryStatus.New ? "Not present in MusicLoom state." : "Source file changed."));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                entries.Add(new PlanEntry(
                    sourcePath,
                    trackState?.DestinationPath,
                    PlanEntryStatus.Error,
                    Detail: exception.Message));
            }
        }

        progress?.Report(new ScanProgress(sourcePaths.Length, sourcePaths.Length, string.Empty));
        return new LibraryPlan(entries);
    }

    private static bool TryGetState(
        StateDocument state,
        string sourcePath,
        out TrackState? trackState)
    {
        if (state.Tracks.TryGetValue(sourcePath, out trackState))
        {
            return true;
        }

        foreach ((string key, TrackState value) in state.Tracks)
        {
            if (string.Equals(key, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                trackState = value;
                return true;
            }
        }

        trackState = null;
        return false;
    }
}
