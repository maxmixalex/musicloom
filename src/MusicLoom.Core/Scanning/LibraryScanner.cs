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
        CancellationToken cancellationToken = default,
        DateTimeOffset? now = null)
    {
        LibraryPathSafety.Validate(settings);

        string sourceRoot = Path.GetFullPath(settings.Path.SourceDirectory);
        string destinationRoot = Path.GetFullPath(settings.Path.DestinationDirectory);
        string expectedRecipe = SyncRecipe.For(settings);
        DateTimeOffset effectiveNow = now ?? DateTimeOffset.UtcNow;

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
            TryGetState(state, sourcePath, out TrackState? trackState);
            bool sourceVersionUnchanged =
                trackState is not null
                && StateStore.IsSourceVersionUnchanged(trackState, source);

            TrackMetadata? cachedMetadata = trackState is { MetadataCaptured: true }
                ? new TrackMetadata(
                    trackState.Artist,
                    trackState.AlbumArtist,
                    trackState.Album,
                    trackState.HasEmbeddedArt)
                : null;

            // Source metadata remains valid even when the output recipe changes.
            // This is the critical warm path: config changes should not force us
            // to reopen every audio file merely to rediscover artist/album data.
            if (trackState is not null
                && sourceVersionUnchanged
                && cachedMetadata is not null)
            {
                string destinationPath = GetDestinationPath(
                    sourcePath,
                    sourceRoot,
                    destinationRoot,
                    cachedMetadata,
                    trackState.NeteaseSongId);

                bool destinationMatchesState =
                    !string.IsNullOrWhiteSpace(trackState.DestinationPath)
                    && PathsEqual(trackState.DestinationPath, destinationPath);
                bool destinationExists = File.Exists(destinationPath);
                bool recipeMatches =
                    string.Equals(
                        trackState.RecipeVersion,
                        expectedRecipe,
                        StringComparison.Ordinal);
                bool enrichmentFresh =
                    StateStore.IsEnrichmentFresh(
                        trackState,
                        settings,
                        effectiveNow);

                if (destinationMatchesState
                    && destinationExists
                    && recipeMatches
                    && enrichmentFresh)
                {
                    entries.Add(new PlanEntry(
                        sourcePath,
                        destinationPath,
                        PlanEntryStatus.Unchanged,
                        cachedMetadata));
                    continue;
                }

                string detail = !destinationMatchesState
                    ? "Destination path changed."
                    : !destinationExists
                        ? "Destination file is missing."
                        : !recipeMatches
                            ? "Processing recipe changed."
                            : "Enrichment retry is due.";

                entries.Add(new PlanEntry(
                    sourcePath,
                    destinationPath,
                    PlanEntryStatus.Modified,
                    cachedMetadata,
                    detail,
                    NeedsAudioCopy:
                        !destinationMatchesState || !destinationExists));
                continue;
            }

            try
            {
                TrackMetadata metadata = await _metadataReader.ReadAsync(
                    sourcePath,
                    cancellationToken);

                string destinationPath = GetDestinationPath(
                    sourcePath,
                    sourceRoot,
                    destinationRoot,
                    metadata,
                    trackState?.NeteaseSongId);

                bool destinationMatchesState =
                    trackState?.DestinationPath is not null
                    && PathsEqual(trackState.DestinationPath, destinationPath);
                bool destinationExists = File.Exists(destinationPath);
                bool recipeMatches =
                    trackState is not null
                    && string.Equals(
                        trackState.RecipeVersion,
                        expectedRecipe,
                        StringComparison.Ordinal);
                bool enrichmentFresh =
                    trackState is not null
                    && StateStore.IsEnrichmentFresh(
                        trackState,
                        settings,
                        effectiveNow);

                bool needsAudioCopy =
                    trackState is null
                    || !sourceVersionUnchanged
                    || !destinationMatchesState
                    || !destinationExists;

                PlanEntryStatus status = trackState is null
                    ? PlanEntryStatus.New
                    : PlanEntryStatus.Modified;

                string detail = status == PlanEntryStatus.New
                    ? "Not present in MusicLoom state."
                    : !sourceVersionUnchanged
                        ? "Source file changed."
                        : !destinationMatchesState
                            ? "Destination path changed."
                            : !destinationExists
                                ? "Destination file is missing."
                                : cachedMetadata is null
                                    ? "Refreshing metadata state."
                                    : !recipeMatches
                                        ? "Processing recipe changed."
                                        : !enrichmentFresh
                                            ? "Enrichment retry is due."
                                            : "Refreshing metadata state.";

                entries.Add(new PlanEntry(
                    sourcePath,
                    destinationPath,
                    status,
                    metadata,
                    detail,
                    needsAudioCopy));
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

        AncillaryPlan ancillary = await AncillaryPlanner.BuildAsync(
            settings,
            entries,
            cancellationToken);

        progress?.Report(new ScanProgress(
            sourcePaths.Length,
            sourcePaths.Length,
            string.Empty));
        return new LibraryPlan(
            entries,
            ancillary.Covers,
            ancillary.Playlists);
    }

    private static string GetDestinationPath(
        string sourcePath,
        string sourceRoot,
        string destinationRoot,
        TrackMetadata metadata,
        long? neteaseSongId)
    {
        MusicFile musicFile = new(
            sourcePath,
            Path.GetDirectoryName(sourcePath) ?? sourceRoot,
            metadata.Artist,
            metadata.AlbumArtist,
            metadata.Album,
            metadata.HasEmbeddedArt,
            neteaseSongId);

        return LibraryLayout.GetDestinationPath(
            musicFile,
            destinationRoot);
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

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}
