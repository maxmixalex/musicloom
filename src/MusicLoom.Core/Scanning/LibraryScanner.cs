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

        // Index the target library once as well as the source library.
        // Target identity is still the planned path: an unrelated file
        // elsewhere in the destination cannot be mistaken for a synced track.
        Dictionary<string, FileInfo> destinationFiles =
            Directory.Exists(destinationRoot)
                ? Directory.EnumerateFiles(
                        destinationRoot, "*", enumerationOptions)
                    .Where(static path =>
                        SupportedExtensions.Contains(Path.GetExtension(path)))
                    .Select(Path.GetFullPath)
                    .ToDictionary(
                        static path => path,
                        static path => new FileInfo(path),
                        OperatingSystem.IsWindows()
                            ? StringComparer.OrdinalIgnoreCase
                            : StringComparer.Ordinal)
                : new Dictionary<string, FileInfo>(
                    OperatingSystem.IsWindows()
                        ? StringComparer.OrdinalIgnoreCase
                        : StringComparer.Ordinal);

        List<PlanEntry> entries = new(sourcePaths.Length);
        for (int index = 0; index < sourcePaths.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string sourcePath = sourcePaths[index];
            progress?.Report(new ScanProgress(
                index + 1,
                sourcePaths.Length,
                sourcePath,
                "tracks"));

            FileInfo source = new(sourcePath);
            TryGetState(state, sourcePath, out TrackState? trackState);

            bool sourceStatUnchanged =
                trackState is not null
                && StateStore.IsSourceStatUnchanged(trackState, source);
            bool sourceVersionUnchanged = false;
            bool quickHashNeedsCapture = false;

            if (trackState is not null && sourceStatUnchanged)
            {
                ulong quickHash = QuickFileHash.Compute(sourcePath);
                quickHashNeedsCapture = trackState.QuickHash == 0;
                sourceVersionUnchanged =
                    StateStore.IsSourceVersionUnchanged(
                        trackState,
                        source,
                        quickHash);
            }

            bool metadataCacheCurrent =
                trackState is
                {
                    MetadataCaptured: true,
                    TitleCaptured: true,
                }
                && trackState.MetadataSchemaVersion
                    >= TrackMetadata.CurrentSchemaVersion;

            TrackMetadata? cachedMetadata =
                metadataCacheCurrent
                    ? new TrackMetadata(
                        trackState!.Artist,
                        trackState.AlbumArtist,
                        trackState.Album,
                        trackState.HasEmbeddedArt,
                        trackState.Title)
                    : null;

            // Refresh old cache formats once on a warm scan. v2 is important
            // because it preserves multi-value artist boundaries as ';'.
            if (trackState is
                    { MetadataCaptured: true }
                && sourceVersionUnchanged
                && !metadataCacheCurrent)
            {
                try
                {
                    TrackMetadata refreshed =
                        await _metadataReader.ReadAsync(
                            sourcePath,
                            cancellationToken);

                    trackState = trackState with
                    {
                        MetadataSchemaVersion =
                            TrackMetadata.CurrentSchemaVersion,
                        TitleCaptured = true,
                        Title = refreshed.Title,
                        Artist = refreshed.Artist,
                        AlbumArtist = refreshed.AlbumArtist,
                        Album = refreshed.Album,
                        HasEmbeddedArt = refreshed.HasEmbeddedArt,
                    };
                    state.Tracks[sourcePath] = trackState;
                    cachedMetadata = refreshed;
                }
                catch
                {
                    // Keep the previous metadata usable for this scan, but do
                    // not mark the schema migrated so a later scan can retry.
                    cachedMetadata = new TrackMetadata(
                        trackState.Artist,
                        trackState.AlbumArtist,
                        trackState.Album,
                        trackState.HasEmbeddedArt,
                        trackState.Title);
                }
            }

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
                bool destinationExists =
                    destinationFiles.TryGetValue(
                        destinationPath, out FileInfo? destinationFile)
                    && destinationFile.Exists;
                bool destinationVersionKnown =
                    StateStore.HasDestinationVersion(trackState);
                bool destinationVersionMatches =
                    !destinationVersionKnown
                    || (destinationExists
                        && StateStore.IsDestinationVersionUnchanged(
                            trackState,
                            destinationFile!));
                bool recipeMatches =
                    SyncRecipe.IsCompatibleForLocalSync(
                        trackState, settings);
                bool enrichmentFresh =
                    StateStore.IsEnrichmentFresh(
                        trackState,
                        settings,
                        effectiveNow);

                if (destinationMatchesState
                    && destinationExists
                    && destinationVersionMatches
                    && recipeMatches
                    && enrichmentFresh
                    && !quickHashNeedsCapture)
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
                        : !destinationVersionMatches
                            ? "Destination file changed."
                            : !recipeMatches
                                ? "Processing recipe changed."
                                : !enrichmentFresh
                                    ? "Enrichment retry is due."
                                    : "Capturing source quick hash.";

                entries.Add(new PlanEntry(
                    sourcePath,
                    destinationPath,
                    PlanEntryStatus.Modified,
                    cachedMetadata,
                    detail,
                    NeedsAudioCopy:
                        !destinationMatchesState
                        || !destinationExists
                        || !destinationVersionMatches));
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

                // Adopt a pre-existing destination when its bytes are
                // identical to the source. This recovers a lost sync index
                // without recopying an otherwise complete local library.
                // Audio with externally edited tags/lyrics is not adopted.
                if (trackState is null
                    && destinationFiles.TryGetValue(
                        destinationPath, out FileInfo? existingDestination)
                    && await FilesHaveIdenticalContentAsync(
                        sourcePath,
                        existingDestination,
                        cancellationToken))
                {
                    state.Tracks[sourcePath] = new TrackState
                    {
                        SourcePath = sourcePath,
                        SourceSize = source.Length,
                        SourceLastWriteUtcTicks =
                            source.LastWriteTimeUtc.Ticks,
                        QuickHash = QuickFileHash.Compute(sourcePath),
                        RecipeVersion = expectedRecipe,
                        DestinationPath = destinationPath,
                        DestinationSize = existingDestination.Length,
                        DestinationLastWriteUtcTicks =
                            existingDestination.LastWriteTimeUtc.Ticks,
                        MetadataCaptured = true,
                        MetadataSchemaVersion =
                            TrackMetadata.CurrentSchemaVersion,
                        TitleCaptured = true,
                        Title = metadata.Title,
                        Artist = metadata.Artist,
                        AlbumArtist = metadata.AlbumArtist,
                        Album = metadata.Album,
                        HasEmbeddedArt = metadata.HasEmbeddedArt,
                        ProcessedAt = effectiveNow,
                    };

                    entries.Add(new PlanEntry(
                        sourcePath,
                        destinationPath,
                        PlanEntryStatus.Unchanged,
                        metadata));
                    continue;
                }

                bool destinationMatchesState =
                    trackState?.DestinationPath is not null
                    && PathsEqual(trackState.DestinationPath, destinationPath);
                bool destinationExists =
                    destinationFiles.TryGetValue(
                        destinationPath, out FileInfo? destinationFile)
                    && destinationFile.Exists;
                bool destinationVersionMatches =
                    trackState is null
                    || !sourceVersionUnchanged
                    || !StateStore.HasDestinationVersion(trackState)
                    || (destinationExists
                        && StateStore.IsDestinationVersionUnchanged(
                            trackState,
                            destinationFile!));
                bool recipeMatches =
                    trackState is not null
                    && SyncRecipe.IsCompatibleForLocalSync(
                        trackState, settings);
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
                    || !destinationExists
                    || !destinationVersionMatches;

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
                                : !destinationVersionMatches
                                    ? "Destination file changed."
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
                // Metadata problems must not prevent a readable audio file from
                // being copied. Fall back to the destination root, matching the
                // user's explicit "preserve the file first" policy.
                TrackMetadata fallbackMetadata = new(
                    Artist: null,
                    AlbumArtist: null,
                    Album: null,
                    HasEmbeddedArt: false,
                    Title: null);
                string destinationPath = GetDestinationPath(
                    sourcePath,
                    sourceRoot,
                    destinationRoot,
                    fallbackMetadata,
                    neteaseSongId: null);

                bool destinationMatchesState =
                    trackState?.DestinationPath is not null
                    && PathsEqual(
                        trackState.DestinationPath,
                        destinationPath);
                bool destinationExists =
                    destinationFiles.TryGetValue(
                        destinationPath, out FileInfo? destinationFile)
                    && destinationFile.Exists;
                bool destinationVersionMatches =
                    trackState is null
                    || !sourceVersionUnchanged
                    || !StateStore.HasDestinationVersion(trackState)
                    || (destinationExists
                        && StateStore.IsDestinationVersionUnchanged(
                            trackState,
                            destinationFile!));

                bool needsAudioCopy =
                    trackState is null
                    || !sourceVersionUnchanged
                    || !destinationMatchesState
                    || !destinationExists
                    || !destinationVersionMatches;

                entries.Add(new PlanEntry(
                    sourcePath,
                    destinationPath,
                    trackState is null
                        ? PlanEntryStatus.New
                        : PlanEntryStatus.Modified,
                    fallbackMetadata,
                    "Metadata could not be read; copying to the destination root. "
                        + exception.Message,
                    needsAudioCopy));
            }
        }

        progress?.Report(new ScanProgress(
            sourcePaths.Length,
            sourcePaths.Length,
            string.Empty,
            "ancillary"));

        AncillaryPlan ancillary = await AncillaryPlanner.BuildAsync(
            settings,
            entries,
            cancellationToken);

        progress?.Report(new ScanProgress(
            sourcePaths.Length,
            sourcePaths.Length,
            string.Empty,
            "complete"));
        return new LibraryPlan(
            entries,
            ancillary.Covers,
            ancillary.Playlists);
    }

    private static async Task<bool> FilesHaveIdenticalContentAsync(
        string sourcePath,
        FileInfo destination,
        CancellationToken cancellationToken)
    {
        try
        {
            FileInfo source = new(sourcePath);
            if (!destination.Exists
                || source.Length != destination.Length)
            {
                return false;
            }

            // Deterministic byte comparison rather than trusting timestamps
            // or a short quick hash. Stop at the first mismatching block.
            const int blockSize = 128 * 1024;
            byte[] sourceBuffer = new byte[blockSize];
            byte[] destinationBuffer = new byte[blockSize];
            await using FileStream left = new(
                sourcePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, blockSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream right = new(
                destination.FullName, FileMode.Open, FileAccess.Read,
                FileShare.Read, blockSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                int count = await left.ReadAsync(
                    sourceBuffer, cancellationToken);
                if (count == 0)
                {
                    return true;
                }

                await right.ReadExactlyAsync(
                    destinationBuffer.AsMemory(0, count),
                    cancellationToken);
                if (!sourceBuffer.AsSpan(0, count).SequenceEqual(
                        destinationBuffer.AsSpan(0, count)))
                {
                    return false;
                }
            }
        }
        catch (IOException)
        {
            // A locked or disappearing target remains pending. Never infer
            // identity from a failed read and never delete or overwrite it.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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
