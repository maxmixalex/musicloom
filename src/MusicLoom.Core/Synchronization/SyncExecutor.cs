using System.Security.Cryptography;
using System.Text;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Enrichment;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;

namespace MusicLoom.Core.Synchronization;

public sealed record TrackSyncError(
    string SourcePath,
    string Stage,
    string Message);

public sealed record SyncExecutionSummary(
    LocalMaterializeSummary Local,
    int CompletedTracks,
    int ExistingLyrics,
    int WrittenLyrics,
    int NoMatch,
    int NoLyrics,
    int UnsupportedLyrics,
    IReadOnlyList<TrackSyncError> Errors);

public sealed record SyncExecutionProgress(
    int Processed,
    int Total,
    string SourcePath,
    string Stage);

public sealed class SyncExecutor
{
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

        LocalMaterializeSummary local = await _materializer.ApplyAsync(
            settings,
            plan,
            cancellationToken);

        StateDocument updatedState = state with
        {
            RecipeVersion = SyncRecipe.CurrentVersion,
            Tracks = new Dictionary<string, TrackState>(
                state.Tracks,
                StringComparer.OrdinalIgnoreCase),
        };

        List<TrackSyncError> errors = [];
        int completedTracks = 0;
        int existingLyrics = 0;
        int writtenLyrics = 0;
        int noMatch = 0;
        int noLyrics = 0;
        int unsupportedLyrics = 0;

        PlanEntry[] work = plan.Entries
            .Where(static entry =>
                entry.Status is PlanEntryStatus.New or PlanEntryStatus.Modified)
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

                stage = "inspect-lyrics";
                progress?.Report(new SyncExecutionProgress(
                    index,
                    work.Length,
                    entry.SourcePath,
                    stage));

                LyricsProbeResult probe = await _lyricsWriter.ProbeAsync(
                    entry.DestinationPath,
                    cancellationToken);

                if (probe.Status == LyricsProbeStatus.UnsupportedFormat)
                {
                    partial = partial with
                    {
                        Lyrics = LyricsState.Unsupported,
                        LyricsCheckedAt = DateTimeOffset.UtcNow,
                    };
                    unsupportedLyrics++;
                    await CompleteAsync(entry, partial, expectedRecipe, index + 1);
                    continue;
                }

                if (probe.Status == LyricsProbeStatus.Present
                    && !settings.Organizer.Lyrics.OverrideExisting)
                {
                    partial = partial with
                    {
                        Lyrics = LyricsState.Existing,
                        LyricsCheckedAt = DateTimeOffset.UtcNow,
                    };
                    existingLyrics++;
                    await CompleteAsync(entry, partial, expectedRecipe, index + 1);
                    continue;
                }

                long? songId = partial.NeteaseSongId;
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
            errors);

        async Task CompleteAsync(
            PlanEntry entry,
            TrackState trackState,
            string recipe,
            int processed)
        {
            updatedState.Tracks[entry.SourcePath] = trackState with
            {
                RecipeVersion = recipe,
                ProcessedAt = DateTimeOffset.UtcNow,
            };
            completedTracks++;

            progress?.Report(new SyncExecutionProgress(
                processed,
                work.Length,
                entry.SourcePath,
                "complete"));

            if (processed % _checkpointInterval == 0)
            {
                await stateStore.SaveAsync(
                    updatedState,
                    cancellationToken);
            }
        }
    }

    private static TrackState BuildPartialState(
        PlanEntry entry,
        StateDocument state)
    {
        FileInfo source = new(entry.SourcePath);
        TryGetState(state, entry.SourcePath, out TrackState? previous);

        bool sourceUnchanged =
            previous is not null
            && StateStore.IsSourceVersionUnchanged(previous, source);

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
            QuickHash = sourceUnchanged ? previous?.QuickHash ?? 0 : 0,
            RecipeVersion = canReuseEnrichment
                ? previous?.RecipeVersion ?? string.Empty
                : string.Empty,
            DestinationPath = entry.DestinationPath,
            Recognition = recognition,
            RecognitionCheckedAt = sourceUnchanged
                ? previous?.RecognitionCheckedAt
                : null,
            NeteaseSongId = songId,
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
        if (!StateStore.IsSourceVersionUnchanged(previous, source)
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
