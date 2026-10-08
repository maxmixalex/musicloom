using System.Security.Cryptography;
using System.Text;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Enrichment;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using OpenccNetLib;

namespace MusicLoom.Core.Synchronization;

public sealed record TrackSyncError(
    string SourcePath,
    string Stage,
    string Message);

public sealed record TrackSyncWarning(
    string SourcePath,
    string? RecordedTitle,
    string? MatchedTitle,
    string? RecordedArtist,
    string? MatchedArtist,
    bool TitleMismatch,
    bool ArtistMismatch);

public sealed record SyncExecutionSummary(
    LocalMaterializeSummary Local,
    int CompletedTracks,
    int ExistingLyrics,
    int WrittenLyrics,
    int NoMatch,
    int NoLyrics,
    int UnsupportedLyrics,
    IReadOnlyList<TrackSyncWarning> Warnings,
    IReadOnlyList<TrackSyncError> Errors,
    IReadOnlyList<string> CompletedSourcePaths);

public sealed record SyncExecutionProgress(
    int Processed,
    int Total,
    string SourcePath,
    string Stage,
    long? NeteaseSongId = null,
    string? NeteaseTrackName = null,
    string? NeteaseTrackArtist = null,
    string? NeteaseTrackAlbum = null,
    TrackSyncWarning? Warning = null,
    string? NeteaseCoverUrl = null);

public sealed class SyncExecutor
{
    private static readonly object
        ChineseConverterLock = new();
    private static readonly Opencc
        SimplifiedChineseConverter =
            new(
                OpenccConfig.T2S,
                isFrozen: true);

    private readonly LocalLibraryMaterializer _materializer;
    private readonly ITrackRecognizer _recognizer;
    private readonly ITrackLyricsProvider _lyricsProvider;
    private readonly ITrackLyricsWriter _lyricsWriter;
    private readonly int _checkpointInterval;

    public SyncExecutor(
        LocalLibraryMaterializer materializer,
        ITrackRecognizer recognizer,
        ITrackLyricsProvider lyricsProvider,
        ITrackLyricsWriter lyricsWriter,
        int checkpointInterval = 25)
    {
        if (checkpointInterval <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(checkpointInterval),
                "Checkpoint interval must be positive.");
        }

        _materializer = materializer;
        _recognizer = recognizer;
        _lyricsProvider = lyricsProvider;
        _lyricsWriter = lyricsWriter;
        _checkpointInterval = checkpointInterval;
    }

    public async Task<SyncExecutionSummary> ExecuteAsync(
        MusicLoomSettings settings,
        LibraryPlan plan,
        StateDocument state,
        StateStore stateStore,
        IProgress<SyncExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stateStore);

        Progress<LocalMaterializeProgress>
            materializeProgress = new(value =>
            {
                progress?.Report(
                    new SyncExecutionProgress(
                        value.Processed,
                        value.Total,
                        value.SourcePath,
                        value.Stage));
            });

        LocalMaterializeSummary local =
            await _materializer.ApplyAsync(
                settings,
                plan,
                materializeProgress,
                cancellationToken:
                    cancellationToken);

        List<TrackSyncError> errors = local.Errors
            .Select(static error => new TrackSyncError(
                error.SourcePath,
                error.Kind switch
                {
                    LocalMaterializeErrorKind.Track => "copy-audio",
                    LocalMaterializeErrorKind.Cover => "copy-cover",
                    LocalMaterializeErrorKind.Playlist => "write-playlist",
                    _ => "materialize",
                },
                error.Message))
            .ToList();
        List<TrackSyncWarning> warnings = [];

        HashSet<string> failedTrackSources = local.Errors
            .Where(static error =>
                error.Kind == LocalMaterializeErrorKind.Track)
            .Select(static error => error.SourcePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // When metadata changes the destination path (for example after fixing
        // multi-artist separators), clean up the old file only if it still
        // exactly matches the version MusicLoom previously wrote. User-edited
        // or otherwise drifted destination files are never deleted.
        string destinationRoot =
            Path.GetFullPath(
                settings.Path.DestinationDirectory);

        foreach (PlanEntry entry in plan.Entries)
        {
            if (!entry.NeedsAudioCopy
                || entry.DestinationPath is null
                || failedTrackSources.Contains(
                    entry.SourcePath)
                || !state.Tracks.TryGetValue(
                    entry.SourcePath,
                    out TrackState? previous)
                || previous.DestinationPath is null
                || PathsEqual(
                    previous.DestinationPath,
                    entry.DestinationPath)
                || !File.Exists(
                    entry.DestinationPath)
                || !File.Exists(
                    previous.DestinationPath)
                || !StateStore.HasDestinationVersion(
                    previous))
            {
                continue;
            }

            try
            {
                string previousFull =
                    Path.GetFullPath(
                        previous.DestinationPath);

                if (!IsWithinRoot(
                        destinationRoot,
                        previousFull))
                {
                    continue;
                }

                FileInfo previousFile =
                    new(previousFull);

                if (!StateStore
                    .IsDestinationVersionUnchanged(
                        previous,
                        previousFile))
                {
                    continue;
                }

                File.Delete(previousFull);
                RemoveEmptyParents(
                    Path.GetDirectoryName(
                        previousFull),
                    destinationRoot);
            }
            catch (Exception exception)
                when (exception
                    is not OperationCanceledException)
            {
                errors.Add(
                    new TrackSyncError(
                        entry.SourcePath,
                        "cleanup-old-destination",
                        exception.Message));
            }
        }

        StateDocument updatedState = state with
        {
            RecipeVersion = SyncRecipe.CurrentVersion,
            Tracks = new Dictionary<string, TrackState>(
                state.Tracks,
                StringComparer.OrdinalIgnoreCase),
        };

        int completedTracks = 0;
        List<string> completedSources = [];
        int existingLyrics = 0;
        int writtenLyrics = 0;
        int noMatch = 0;
        int noLyrics = 0;
        int unsupportedLyrics = 0;

        PlanEntry[] work = plan.Entries
            .Where(entry =>
                (entry.Status is PlanEntryStatus.New or PlanEntryStatus.Modified)
                && !failedTrackSources.Contains(entry.SourcePath))
            .ToArray();

        try
        {
            for (int index = 0; index < work.Length; index++)
            {
            cancellationToken.ThrowIfCancellationRequested();
            PlanEntry entry = work[index];

            if (entry.DestinationPath is null || entry.Metadata is null)
            {
                errors.Add(new TrackSyncError(
                    entry.SourcePath,
                    "plan",
                    "The plan entry is missing a destination path or metadata."));
                continue;
            }

            string stage = "state";
            TrackState partial = BuildPartialState(
                entry,
                updatedState);

            // Stage the partial state before any external/network operation.
            // If cancellation occurs, this is enough for the next scan to
            // reuse metadata and an already materialized destination.
            updatedState.Tracks[entry.SourcePath] = partial;

            try
            {
                string expectedRecipe = SyncRecipe.For(settings);

                if (!settings.Organizer.Lyrics.Enabled)
                {
                    await CompleteAsync(
                        entry,
                        partial,
                        expectedRecipe,
                        index + 1);
                    continue;
                }

                if (!settings.Organizer.Lyrics.OverrideExisting)
                {
                    stage = "inspect-lyrics";
                    progress?.Report(new SyncExecutionProgress(
                        index,
                        work.Length,
                        entry.SourcePath,
                        stage));

                    LyricsProbeResult probe =
                        await _lyricsWriter.ProbeAsync(
                            entry.DestinationPath,
                            cancellationToken);

                    if (probe.Status
                        == LyricsProbeStatus.UnsupportedFormat)
                    {
                        partial = partial with
                        {
                            Lyrics = LyricsState.Unsupported,
                            LyricsCheckedAt = DateTimeOffset.UtcNow,
                        };
                        unsupportedLyrics++;
                        await CompleteAsync(
                            entry,
                            partial,
                            expectedRecipe,
                            index + 1);
                        continue;
                    }

                    if (probe.Status
                        == LyricsProbeStatus.Present)
                    {
                        partial = partial with
                        {
                            Lyrics = LyricsState.Existing,
                            LyricsCheckedAt = DateTimeOffset.UtcNow,
                        };
                        existingLyrics++;
                        await CompleteAsync(
                            entry,
                            partial,
                            expectedRecipe,
                            index + 1);
                        continue;
                    }
                }

                long? songId = partial.NeteaseSongId;
                if (songId is null
                    && partial.NeteaseMatchSuppressed)
                {
                    partial = partial with
                    {
                        Recognition =
                            RecognitionState.NoMatch,
                        RecognitionCheckedAt =
                            DateTimeOffset.UtcNow,
                        Lyrics =
                            LyricsState.NoLyrics,
                        LyricsCheckedAt =
                            DateTimeOffset.UtcNow,
                    };
                    noMatch++;
                    await CompleteAsync(
                        entry,
                        partial,
                        expectedRecipe,
                        index + 1);
                    continue;
                }

                if (songId is null)
                {
                    stage = "recognize";
                    progress?.Report(new SyncExecutionProgress(
                        index,
                        work.Length,
                        entry.SourcePath,
                        stage));

                    TrackRecognitionResult recognition =
                        await _recognizer.RecognizeAsync(
                            entry.SourcePath,
                            cancellationToken);

                    partial = partial with
                    {
                        Recognition = recognition.Status ==
                            TrackRecognitionStatus.Matched
                                ? RecognitionState.Matched
                                : RecognitionState.NoMatch,
                        RecognitionCheckedAt = DateTimeOffset.UtcNow,
                        NeteaseSongId = recognition.TrackId,
                        NeteaseTrackName = recognition.TrackName,
                        NeteaseTrackArtist = recognition.Artist,
                        NeteaseTrackAlbum = recognition.Album,
                        NeteaseCoverUrl = recognition.CoverUrl,
                        NeteaseMatchSuppressed = false,
                        NeteaseMatchConfirmed = false,
                    };

                    if (recognition.Status == TrackRecognitionStatus.NoMatch
                        || recognition.TrackId is null)
                    {
                        noMatch++;
                        await CompleteAsync(entry, partial, expectedRecipe, index + 1);
                        continue;
                    }

                    songId = recognition.TrackId;
                }
                else if (partial.Recognition == RecognitionState.Unknown)
                {
                    partial = partial with
                    {
                        Recognition = RecognitionState.Matched,
                    };
                }

                TrackSyncWarning? matchWarning =
                    DetectMatchWarning(
                        entry,
                        partial);

                if (matchWarning is not null)
                {
                    warnings.Add(
                        matchWarning);
                }

                progress?.Report(
                    new SyncExecutionProgress(
                        index,
                        work.Length,
                        entry.SourcePath,
                        "match",
                        partial.NeteaseSongId,
                        partial.NeteaseTrackName,
                        partial.NeteaseTrackArtist,
                        partial.NeteaseTrackAlbum,
                        matchWarning,
                        partial.NeteaseCoverUrl));

                if (matchWarning is not null)
                {
                    // A suspicious automatic match is a review gate, not an
                    // error. Persist the recognition result but deliberately
                    // leave the recipe incomplete so manual confirmation turns
                    // the row into Pending and the next sync can continue with
                    // lyric retrieval/write without recopying audio.
                    updatedState.Tracks[
                        entry.SourcePath] =
                        partial with
                        {
                            RecipeVersion =
                                string.Empty,
                            ProcessedAt =
                                DateTimeOffset.UtcNow,
                        };

                    await stateStore.SaveAsync(
                        updatedState,
                        cancellationToken);
                    continue;
                }

                stage = "fetch-lyrics";
                progress?.Report(new SyncExecutionProgress(
                    index,
                    work.Length,
                    entry.SourcePath,
                    stage));

                LyricsLookupResult lookup =
                    await _lyricsProvider.GetLyricsAsync(
                        songId.Value,
                        cancellationToken);

                if (lookup.Status == LyricsLookupStatus.NoLyrics
                    || lookup.Lyrics is null
                    || lookup.Lyrics.Lines.Count == 0)
                {
                    partial = partial with
                    {
                        Lyrics = LyricsState.NoLyrics,
                        LyricsCheckedAt = DateTimeOffset.UtcNow,
                    };
                    noLyrics++;
                    await CompleteAsync(entry, partial, expectedRecipe, index + 1);
                    continue;
                }

                stage = "write-lyrics";
                progress?.Report(new SyncExecutionProgress(
                    index,
                    work.Length,
                    entry.SourcePath,
                    stage));

                LyricsWriteResult write = await _lyricsWriter.WriteAsync(
                    entry.DestinationPath,
                    lookup.Lyrics,
                    settings.Organizer.Lyrics.OverrideExisting,
                    cancellationToken);

                LyricsState lyricsState = write.Status switch
                {
                    LyricsWriteStatus.Written => LyricsState.Written,
                    LyricsWriteStatus.SkippedExisting => LyricsState.Existing,
                    LyricsWriteStatus.UnsupportedFormat => LyricsState.Unsupported,
                    _ => throw new InvalidOperationException(
                        $"Unknown lyric write status '{write.Status}'."),
                };

                if (lyricsState == LyricsState.Written)
                {
                    writtenLyrics++;
                }
                else if (lyricsState == LyricsState.Existing)
                {
                    existingLyrics++;
                }
                else
                {
                    unsupportedLyrics++;
                }

                partial = partial with
                {
                    Lyrics = lyricsState,
                    LyricsCheckedAt = DateTimeOffset.UtcNow,
                    LyricsFingerprint = lyricsState == LyricsState.Written
                        ? ComputeLyricsFingerprint(lookup.Lyrics)
                        : partial.LyricsFingerprint,
                };

                await CompleteAsync(entry, partial, expectedRecipe, index + 1);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Persist useful partial work (copied destination, metadata,
                // successful recognition) but never claim the current recipe
                // completed. The next scan will retry enrichment without
                // recopying an unchanged destination.
                updatedState.Tracks[entry.SourcePath] = partial with
                {
                    RecipeVersion = GetPreviousValidRecipe(
                        state,
                        entry),
                    ProcessedAt = DateTimeOffset.UtcNow,
                };

                errors.Add(new TrackSyncError(
                    entry.SourcePath,
                    stage,
                    exception.Message));

                progress?.Report(new SyncExecutionProgress(
                    index + 1,
                    work.Length,
                    entry.SourcePath,
                    "failed"));

                // Retryable errors are uncommon and may contain useful partial
                // work (for example a successful recognition before lyric fetch
                // failed). Persist immediately so a process crash does not lose it.
                await stateStore.SaveAsync(
                    updatedState,
                    cancellationToken);
            }
        }

            await stateStore.SaveAsync(updatedState, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Persist tracks that completed before cancellation so the next
            // scan can resume without rediscovering metadata or recopying
            // destinations that are already safe.
            await stateStore.SaveAsync(
                updatedState,
                CancellationToken.None);
            throw;
        }

        return new SyncExecutionSummary(
            local,
            completedTracks,
            existingLyrics,
            writtenLyrics,
            noMatch,
            noLyrics,
            unsupportedLyrics,
            warnings,
            errors,
            completedSources);

        async Task CompleteAsync(
            PlanEntry entry,
            TrackState trackState,
            string recipe,
            int processed)
        {
            TrackState completed = trackState with
            {
                RecipeVersion = recipe,
                ProcessedAt = DateTimeOffset.UtcNow,
            };

            updatedState.Tracks[entry.SourcePath] =
                CaptureDestinationVersion(
                    completed,
                    entry.DestinationPath
                    ?? throw new InvalidOperationException(
                        "Completed plan entry has no destination path."));
            completedTracks++;
            completedSources.Add(entry.SourcePath);

            progress?.Report(new SyncExecutionProgress(
                processed,
                work.Length,
                entry.SourcePath,
                "complete"));

            if (completedTracks % _checkpointInterval == 0)
            {
                await stateStore.SaveAsync(
                    updatedState,
                    cancellationToken);
            }
        }
    }

    public static TrackSyncWarning? DetectMatchWarning(
        PlanEntry entry,
        TrackState state)
    {
        ArgumentNullException.ThrowIfNull(
            entry);
        ArgumentNullException.ThrowIfNull(
            state);

        if (state.NeteaseSongId is null
            || state.NeteaseMatchSuppressed
            || state.NeteaseMatchConfirmed
            || entry.Metadata is null)
        {
            return null;
        }

        string? recordedTitle =
            entry.Metadata.Title;
        string? recordedArtist =
            entry.Metadata.AlbumArtist
            ?? entry.Metadata.Artist;

        bool titleMismatch =
            HasComparableValue(
                recordedTitle)
            && HasComparableValue(
                state.NeteaseTrackName)
            && !MetadataValuesEqual(
                recordedTitle!,
                state.NeteaseTrackName!);

        bool artistMismatch =
            HasComparableValue(
                recordedArtist)
            && HasComparableValue(
                state.NeteaseTrackArtist)
            && !ArtistsCompatible(
                recordedArtist!,
                state.NeteaseTrackArtist!);

        if (!titleMismatch
            && !artistMismatch)
        {
            return null;
        }

        return new TrackSyncWarning(
            entry.SourcePath,
            recordedTitle,
            state.NeteaseTrackName,
            recordedArtist,
            state.NeteaseTrackArtist,
            titleMismatch,
            artistMismatch);
    }

    private static bool HasComparableValue(
        string? value) =>
        !string.IsNullOrWhiteSpace(
            value);

    private static bool MetadataValuesEqual(
        string left,
        string right) =>
        string.Equals(
            NormalizeMetadataValue(
                left),
            NormalizeMetadataValue(
                right),
            StringComparison.Ordinal);

    private static bool ArtistsCompatible(
        string recorded,
        string matched)
    {
        if (MetadataValuesEqual(
                recorded,
                matched))
        {
            return true;
        }

        HashSet<string> recordedArtists =
            SplitArtists(
                recorded);
        HashSet<string> matchedArtists =
            SplitArtists(
                matched);

        if (recordedArtists.Count == 0
            || matchedArtists.Count == 0)
        {
            return false;
        }

        // NetEase frequently credits fewer performers than the local tag.
        // Treat that documented subset as compatible, but not the reverse.
        return matchedArtists.IsSubsetOf(
            recordedArtists);
    }

    private static HashSet<string> SplitArtists(
        string value)
    {
        char[] separators =
        [
            ';', '；',
            '/', '／',
            '・', '·',
            ',', '，',
            '&', '＆',
        ];

        return value
            .Split(
                separators,
                StringSplitOptions
                    .RemoveEmptyEntries
                | StringSplitOptions
                    .TrimEntries)
            .Select(
                NormalizeMetadataValue)
            .Where(
                static part =>
                    part.Length > 0)
            .ToHashSet(
                StringComparer.Ordinal);
    }

    private static string NormalizeMetadataValue(
        string value)
    {
        string normalized =
            value.Normalize(
                NormalizationForm.FormKC);

        lock (ChineseConverterLock)
        {
            normalized =
                SimplifiedChineseConverter
                    .Convert(
                        normalized);
        }

        StringBuilder builder = new();

        foreach (char character
            in normalized)
        {
            if (char.IsLetterOrDigit(
                    character))
            {
                builder.Append(
                    char.ToLowerInvariant(
                        character));
            }
        }

        return builder.ToString();
    }

    private static TrackState BuildPartialState(
        PlanEntry entry,
        StateDocument state)
    {
        FileInfo source = new(entry.SourcePath);
        TryGetState(state, entry.SourcePath, out TrackState? previous);

        ulong quickHash = QuickFileHash.Compute(entry.SourcePath);
        bool sourceUnchanged =
            previous is not null
            && StateStore.IsSourceVersionUnchanged(
                previous,
                source,
                quickHash);

        bool sameDestination =
            previous?.DestinationPath is not null
            && entry.DestinationPath is not null
            && PathsEqual(previous.DestinationPath, entry.DestinationPath);

        bool canReuseEnrichment =
            sourceUnchanged
            && sameDestination
            && !entry.NeedsAudioCopy;

        long? songId = sourceUnchanged
            ? previous?.NeteaseSongId
            : null;

        RecognitionState recognition = sourceUnchanged
            ? previous?.Recognition ?? RecognitionState.Unknown
            : RecognitionState.Unknown;

        if (songId is not null && recognition == RecognitionState.Unknown)
        {
            recognition = RecognitionState.Matched;
        }

        TrackMetadata metadata = entry.Metadata
            ?? throw new InvalidOperationException(
                "Cannot persist a plan entry without metadata.");

        return new TrackState
        {
            SourcePath = entry.SourcePath,
            SourceSize = source.Length,
            SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks,
            QuickHash = quickHash,
            RecipeVersion = canReuseEnrichment
                ? previous?.RecipeVersion ?? string.Empty
                : string.Empty,
            DestinationPath = entry.DestinationPath,
            DestinationSize = canReuseEnrichment
                ? previous?.DestinationSize
                : null,
            DestinationLastWriteUtcTicks = canReuseEnrichment
                ? previous?.DestinationLastWriteUtcTicks
                : null,
            Recognition = recognition,
            RecognitionCheckedAt = sourceUnchanged
                ? previous?.RecognitionCheckedAt
                : null,
            NeteaseSongId = songId,
            NeteaseTrackName = sourceUnchanged
                ? previous?.NeteaseTrackName
                : null,
            NeteaseTrackArtist = sourceUnchanged
                ? previous?.NeteaseTrackArtist
                : null,
            NeteaseTrackAlbum = sourceUnchanged
                ? previous?.NeteaseTrackAlbum
                : null,
            NeteaseCoverUrl = sourceUnchanged
                ? previous?.NeteaseCoverUrl
                : null,
            NeteaseMatchSuppressed =
                sourceUnchanged
                && previous?.NeteaseMatchSuppressed
                    == true,
            NeteaseMatchConfirmed =
                sourceUnchanged
                && previous?.NeteaseMatchConfirmed
                    == true,
            Lyrics = canReuseEnrichment
                ? previous?.Lyrics ?? LyricsState.Unknown
                : LyricsState.Unknown,
            LyricsCheckedAt = canReuseEnrichment
                ? previous?.LyricsCheckedAt
                : null,
            LyricsFingerprint = canReuseEnrichment
                ? previous?.LyricsFingerprint
                : null,
            MetadataCaptured = true,
            MetadataSchemaVersion =
                TrackMetadata.CurrentSchemaVersion,
            TitleCaptured = true,
            Title = metadata.Title,
            Artist = metadata.Artist,
            AlbumArtist = metadata.AlbumArtist,
            Album = metadata.Album,
            HasEmbeddedArt = metadata.HasEmbeddedArt,
            ProcessedAt = DateTimeOffset.UtcNow,
        };
    }

    private static string GetPreviousValidRecipe(
        StateDocument originalState,
        PlanEntry entry)
    {
        if (!TryGetState(
                originalState,
                entry.SourcePath,
                out TrackState? previous)
            || previous is null
            || entry.NeedsAudioCopy)
        {
            return string.Empty;
        }

        FileInfo source = new(entry.SourcePath);
        ulong quickHash = QuickFileHash.Compute(entry.SourcePath);
        if (!StateStore.IsSourceVersionUnchanged(
                previous,
                source,
                quickHash)
            || previous.DestinationPath is null
            || entry.DestinationPath is null
            || !PathsEqual(
                previous.DestinationPath,
                entry.DestinationPath))
        {
            return string.Empty;
        }

        return previous.RecipeVersion;
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
            if (string.Equals(
                    key,
                    sourcePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                trackState = value;
                return true;
            }
        }

        trackState = null;
        return false;
    }

    private static TrackState CaptureDestinationVersion(
        TrackState state,
        string destinationPath)
    {
        FileInfo destination = new(destinationPath);
        if (!destination.Exists)
        {
            throw new FileNotFoundException(
                "Cannot complete sync state because the destination file is missing.",
                destinationPath);
        }

        return state with
        {
            DestinationSize = destination.Length,
            DestinationLastWriteUtcTicks =
                destination.LastWriteTimeUtc.Ticks,
        };
    }

    private static bool IsWithinRoot(
        string root,
        string path)
    {
        string fullRoot =
            Path.GetFullPath(root)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        string fullPath =
            Path.GetFullPath(path);

        return fullPath.StartsWith(
            fullRoot,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private static void RemoveEmptyParents(
        string? directory,
        string root)
    {
        if (string.IsNullOrWhiteSpace(
                directory))
        {
            return;
        }

        string fullRoot =
            Path.GetFullPath(root)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);

        string? current =
            Path.GetFullPath(directory);

        while (!string.IsNullOrWhiteSpace(
                   current)
            && !string.Equals(
                current,
                fullRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal)
            && IsWithinRoot(
                fullRoot,
                current))
        {
            if (Directory.EnumerateFileSystemEntries(
                    current)
                .Any())
            {
                break;
            }

            Directory.Delete(current);
            current =
                Path.GetDirectoryName(
                    current);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string ComputeLyricsFingerprint(
        LrcDocument lyrics)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(lyrics.ToString());
        return Convert.ToHexString(
            SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
