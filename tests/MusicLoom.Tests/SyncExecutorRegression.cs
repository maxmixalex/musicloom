using MusicLoom.Core.Configuration;
using MusicLoom.Core.Enrichment;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Planning;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Core.Synchronization;

internal static class SyncExecutorRegression
{
    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        await LyricsDisabledUsesZeroNetworkAsync();
        await ExistingLyricsUsesZeroNetworkAsync();
        await OverrideExistingSkipsProbeAsync();
        await UnsupportedLyricsUsesZeroProbeAsync();
        await NoMatchIsNonFatalAsync();
        await LyricsOnlyRefreshAvoidsOtherWritesAsync();
        await LyricsOnlyRefreshProtectsUserSidecarAsync();
        await CachedLyricsAvoidsSecondNetworkFetchAsync();
        await CanonicalEmbeddedLyricsNeverUseRemoteAsync();
        await CanonicalSidecarLyricsWriteWithoutRemoteAsync();
        await FetchSuccessSurvivesOutputFailureAsync();
        await NoLyricsCacheProtectsUnmanagedOutputAsync();
        MatchWarningHandlesChineseAndArtistSubsets();
        ProgressTrackerRejectsStaleStages();
        await WarningGatesLyricsUntilConfirmationAsync();
        await SuppressedMatchSkipsRecognitionAsync();
        await LyricsWritePersistsEnrichmentStateAsync();
        await RetryableFailureKeepsWarmMetadataAndAvoidsRecopyAsync();
        await PeriodicCheckpointPersistsBeforeFinalSaveAsync();
        await RetryableErrorCheckpointsImmediatelyAsync();
        await CancellationPersistsPartialStateAsync();
        await PathMigrationCleansUnchangedOldDestinationAsync();
        await PathMigrationPreservesDriftedOldDestinationAsync();
    }

    private static async Task LyricsDisabledUsesZeroNetworkAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: false,
            overrideExisting: false);

        FakeRecognizer recognizer = new(TrackRecognitionResult.Matched(42));
        FakeLyricsProvider lyricsProvider = new(
            LyricsLookupResult.Found(LrcDocument.Parse("[00:01.000]hello")));
        FakeLyricsWriter writer = new(LyricsProbeStatus.Missing);

        SyncExecutionSummary summary = await fixture.ExecuteAsync(
            recognizer,
            lyricsProvider,
            writer);

        Equal(1, summary.CompletedTracks);
        Equal(0, recognizer.CallCount);
        Equal(0, lyricsProvider.CallCount);
        Equal(0, writer.ProbeCalls);
        Equal(0, writer.WriteCalls);

        TrackState state = await fixture.LoadTrackStateAsync();
        Equal(
            SyncRecipe.For(fixture.Settings),
            state.RecipeVersion);
    }

    private static async Task ExistingLyricsUsesZeroNetworkAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: true,
            overrideExisting: false);

        FakeRecognizer recognizer = new(TrackRecognitionResult.Matched(42));
        FakeLyricsProvider lyricsProvider = new(
            LyricsLookupResult.Found(LrcDocument.Parse("[00:01.000]hello")));
        FakeLyricsWriter writer = new(LyricsProbeStatus.Present);

        SyncExecutionSummary summary = await fixture.ExecuteAsync(
            recognizer,
            lyricsProvider,
            writer);

        Equal(1, summary.ExistingLyrics);
        Equal(0, recognizer.CallCount);
        Equal(0, lyricsProvider.CallCount);
        Equal(1, writer.ProbeCalls);
        Equal(0, writer.WriteCalls);

        TrackState state = await fixture.LoadTrackStateAsync();
        Equal(LyricsState.Existing, state.Lyrics);
        Equal(
            SyncRecipe.For(fixture.Settings),
            state.RecipeVersion);
    }

    private static async Task OverrideExistingSkipsProbeAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: true,
            overrideExisting: true);

        FakeRecognizer recognizer = new(
            TrackRecognitionResult.Matched(42));
        FakeLyricsProvider lyricsProvider = new(
            LyricsLookupResult.Found(
                LrcDocument.Parse("[00:01.000]hello")));
        FakeLyricsWriter writer = new(
            LyricsProbeStatus.Present);

        SyncExecutionSummary summary =
            await fixture.ExecuteAsync(
                recognizer,
                lyricsProvider,
                writer);

        Equal(0, summary.Errors.Count);
        Equal(0, writer.ProbeCalls);
        Equal(1, writer.WriteCalls);
        Equal(1, recognizer.CallCount);
        Equal(1, lyricsProvider.CallCount);
    }

    private static async Task UnsupportedLyricsUsesZeroProbeAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: true,
            overrideExisting: true,
            extension: ".wma");

        FakeRecognizer recognizer =
            new(TrackRecognitionResult.Matched(42));
        FakeLyricsProvider lyricsProvider = new(
            LyricsLookupResult.Found(
                LrcDocument.Parse("[00:01.000]hello")));
        FakeLyricsWriter writer = new(
            LyricsProbeStatus.UnsupportedFormat,
            LyricsWriteStatus.UnsupportedFormat);

        SyncExecutionSummary summary =
            await fixture.ExecuteAsync(
                recognizer,
                lyricsProvider,
                writer);

        Equal(1, summary.UnsupportedLyrics);
        Equal(1, recognizer.CallCount);
        Equal(1, lyricsProvider.CallCount);
        Equal(0, writer.ProbeCalls);
        Equal(1, writer.WriteCalls);

        TrackState state =
            await fixture.LoadTrackStateAsync();
        Equal(
            LyricsState.Unsupported,
            state.Lyrics);
        Equal(
            SyncRecipe.For(fixture.Settings),
            state.RecipeVersion);
    }

    private static async Task NoMatchIsNonFatalAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: true,
            overrideExisting: true);

        FakeRecognizer recognizer = new(TrackRecognitionResult.NoMatch());
        FakeLyricsProvider lyricsProvider = new(
            LyricsLookupResult.NoLyrics());
        FakeLyricsWriter writer = new(LyricsProbeStatus.Missing);

        SyncExecutionSummary summary = await fixture.ExecuteAsync(
            recognizer,
            lyricsProvider,
            writer);

        Equal(1, summary.NoMatch);
        Equal(1, summary.CompletedSourcePaths.Count);
        Equal(fixture.SourcePath, summary.CompletedSourcePaths[0]);
        Equal(0, summary.Errors.Count);
        Equal(1, recognizer.CallCount);
        Equal(0, lyricsProvider.CallCount);
        Equal(0, writer.WriteCalls);

        TrackState state = await fixture.LoadTrackStateAsync();
        Equal(RecognitionState.NoMatch, state.Recognition);
        Equal<long?>(null, state.NeteaseSongId);
        Equal(
            SyncRecipe.For(fixture.Settings),
            state.RecipeVersion);
    }

    private static async Task LyricsOnlyRefreshAvoidsOtherWritesAsync()
    {
        await using Fixture fixture =
            await Fixture.CreateAsync(
                lyricsEnabled: true,
                overrideExisting: false);

        Directory.CreateDirectory(
            Path.GetDirectoryName(fixture.DestinationPath)!);
        await File.WriteAllBytesAsync(
            fixture.DestinationPath,
            [9, 8, 7]);

        PlanEntry entry = fixture.Plan.Entries[0] with
        {
            Status = PlanEntryStatus.Modified,
            NeedsAudioCopy = false,
        };
        string cover = Path.Combine(fixture.Root, "unused-cover.jpg");
        string playlist = Path.Combine(fixture.Root, "unused-playlist.txt");

        LibraryPlan selected = new(
            [entry],
            covers:
            [
                new CoverPlanEntry(
                    fixture.SourcePath,
                    cover,
                    NeedsWrite: true),
            ],
            playlists:
            [
                new PlaylistPlanEntry(
                    fixture.SourcePath,
                    playlist,
                    "should not appear",
                    NeedsWrite: true),
            ]);

        FakeRecognizer recognizer =
            new(TrackRecognitionResult.Matched(42));
        FakeLyricsProvider provider =
            new(LyricsLookupResult.Found(
                LrcDocument.Parse("[00:01.000]hello")));
        FakeLyricsWriter writer =
            new(LyricsProbeStatus.Present);

        SyncExecutionSummary summary = await new SyncExecutor(
            new LocalLibraryMaterializer(),
            recognizer,
            provider,
            writer).ExecuteAsync(
                fixture.Settings,
                selected,
                fixture.State,
                fixture.StateStore,
                lyricsOnly: true);

        Equal(1, summary.CompletedTracks);
        Equal(1, summary.CompletedSourcePaths.Count);
        Equal(0, summary.Local.CopiedTracks);
        Equal(0, summary.Local.CopiedCovers);
        Equal(0, summary.Local.WrittenPlaylists);
        Equal(0, writer.ProbeCalls);
        Equal(1, writer.WriteCalls);
        True(!File.Exists(cover));
        True(!File.Exists(playlist));
        True((await File.ReadAllBytesAsync(
            fixture.DestinationPath))
            .SequenceEqual(new byte[] { 9, 8, 7 }));
    }

    private static async Task LyricsOnlyRefreshProtectsUserSidecarAsync()
    {
        await using Fixture fixture =
            await Fixture.CreateAsync(
                lyricsEnabled: true,
                overrideExisting: false);

        Directory.CreateDirectory(
            Path.GetDirectoryName(fixture.DestinationPath)!);
        await File.WriteAllBytesAsync(
            fixture.DestinationPath,
            [9, 8, 7]);

        string sidecar =
            SidecarLrcLyricsWriter.GetSidecarPath(
                fixture.DestinationPath);
        await File.WriteAllTextAsync(
            sidecar,
            "[00:01.000]User-edited lyric");

        PlanEntry entry = fixture.Plan.Entries[0] with
        {
            Status = PlanEntryStatus.Modified,
            NeedsAudioCopy = false,
        };

        SyncExecutionSummary summary = await new SyncExecutor(
            new LocalLibraryMaterializer(),
            new FakeRecognizer(
                TrackRecognitionResult.Matched(42)),
            new FakeLyricsProvider(
                LyricsLookupResult.Found(
                    LrcDocument.Parse(
                        "[00:01.000]New lyric"))),
            new SidecarLrcLyricsWriter()).ExecuteAsync(
                fixture.Settings,
                new LibraryPlan([entry]),
                fixture.State,
                fixture.StateStore,
                lyricsOnly: true);

        Equal(1, summary.Errors.Count);
        Equal(0, summary.CompletedTracks);
        Equal(
            "[00:01.000]User-edited lyric",
            await File.ReadAllTextAsync(sidecar));
    }

    private static async Task CachedLyricsAvoidsSecondNetworkFetchAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: true,
            overrideExisting: true);

        InMemoryLyricsCache lyricsCache = new();
        await lyricsCache.SaveAsync(
            fixture.SourcePath, 42,
            LrcDocument.Parse("[00:01.000]canonical"));

        FakeLyricsProvider provider = new(
            LyricsLookupResult.NoLyrics());
        FakeLyricsWriter writer = new(
            LyricsProbeStatus.Missing);

        SyncExecutionSummary summary = await new SyncExecutor(
            new LocalLibraryMaterializer(),
            new FakeRecognizer(
                TrackRecognitionResult.Matched(42)),
            provider,
            writer,
            lyricsCache: lyricsCache).ExecuteAsync(
                fixture.Settings,
                fixture.Plan,
                fixture.State,
                fixture.StateStore);

        Equal(1, summary.WrittenLyrics);
        Equal(0, provider.CallCount);
        Equal(1, writer.WriteCalls);
    }

    private static async Task CanonicalEmbeddedLyricsNeverUseRemoteAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: true,
            overrideExisting: true);

        InMemoryLibraryLyricsCache library = new();
        await library.SaveLibraryAsync(new LibraryLyricsRecord(
            fixture.SourcePath,
            LibraryLyricsOrigin.Embedded,
            "[00:01.000]Embedded",
            42,
            "source-file",
            DateTimeOffset.UtcNow));

        FakeRecognizer recognizer =
            new(TrackRecognitionResult.Matched(42));
        FakeLyricsProvider provider =
            new(LyricsLookupResult.NoLyrics());
        FakeLyricsWriter writer = new(
            LyricsProbeStatus.Missing);

        SyncExecutionSummary result = await new SyncExecutor(
            new LocalLibraryMaterializer(),
            recognizer,
            provider,
            writer,
            libraryLyricsCache: library).ExecuteAsync(
                fixture.Settings,
                fixture.Plan,
                fixture.State,
                fixture.StateStore);

        Equal(1, result.CompletedTracks);
        Equal(1, result.ExistingLyrics);
        Equal(0, recognizer.CallCount);
        Equal(0, provider.CallCount);
        Equal(0, writer.WriteCalls);
    }

    private static async Task CanonicalSidecarLyricsWriteWithoutRemoteAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: true,
            overrideExisting: true);

        InMemoryLibraryLyricsCache library = new();
        await library.SaveLibraryAsync(new LibraryLyricsRecord(
            fixture.SourcePath,
            LibraryLyricsOrigin.Sidecar,
            "[00:01.000]Local source sidecar",
            42,
            "source-sidecar",
            DateTimeOffset.UtcNow));

        FakeRecognizer recognizer =
            new(TrackRecognitionResult.Matched(42));
        FakeLyricsProvider provider =
            new(LyricsLookupResult.NoLyrics());
        FakeLyricsWriter writer = new(
            LyricsProbeStatus.Missing,
            LyricsWriteStatus.Written);

        SyncExecutionSummary result = await new SyncExecutor(
            new LocalLibraryMaterializer(),
            recognizer,
            provider,
            writer,
            libraryLyricsCache: library).ExecuteAsync(
                fixture.Settings,
                fixture.Plan,
                fixture.State,
                fixture.StateStore);

        Equal(1, result.CompletedTracks);
        Equal(1, result.WrittenLyrics);
        Equal(0, recognizer.CallCount);
        Equal(0, provider.CallCount);
        Equal(1, writer.WriteCalls);
    }

    private static async Task FetchSuccessSurvivesOutputFailureAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: true,
            overrideExisting: true);

        InMemoryLyricsCache lyricsCache = new();
        FakeLyricsProvider provider = new(
            LyricsLookupResult.Found(
                LrcDocument.Parse("[00:01.000]saved before output")));

        SyncExecutionSummary summary = await new SyncExecutor(
            new LocalLibraryMaterializer(),
            new FakeRecognizer(
                TrackRecognitionResult.Matched(42)),
            provider,
            new ThrowingLyricsWriter(),
            lyricsCache: lyricsCache).ExecuteAsync(
                fixture.Settings,
                fixture.Plan,
                fixture.State,
                fixture.StateStore);

        Equal(1, summary.Errors.Count);
        Equal(0, summary.CompletedTracks);
        Equal(1, provider.CallCount);

        CachedTrackLyrics cached = await lyricsCache.GetAsync(
            fixture.SourcePath,
            42) ?? throw new InvalidOperationException(
                "A write failure must not lose the fetched canonical lyrics.");

        Equal(
            "[00:01.000]saved before output",
            cached.Lrc ?? string.Empty);
        True(await lyricsCache.GetAsync(fixture.SourcePath, 99) is null);
    }

    private static async Task NoLyricsCacheProtectsUnmanagedOutputAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: true,
            overrideExisting: false);

        Directory.CreateDirectory(
            Path.GetDirectoryName(fixture.DestinationPath)!);
        await File.WriteAllBytesAsync(
            fixture.DestinationPath,
            [9, 8, 7]);

        string sidecar =
            SidecarLrcLyricsWriter.GetSidecarPath(
                fixture.DestinationPath);
        await File.WriteAllTextAsync(
            sidecar, "[00:01.000]Do not delete this manual lyric");

        InMemoryLyricsCache lyricsCache = new();
        SyncExecutionSummary result = await new SyncExecutor(
            new LocalLibraryMaterializer(),
            new FakeRecognizer(
                TrackRecognitionResult.Matched(42)),
            new FakeLyricsProvider(
                LyricsLookupResult.NoLyrics()),
            new SidecarLrcLyricsWriter(),
            lyricsCache: lyricsCache).ExecuteAsync(
                fixture.Settings,
                new LibraryPlan([
                    fixture.Plan.Entries[0] with
                    {
                        Status = PlanEntryStatus.Modified,
                        NeedsAudioCopy = false,
                    },
                ]),
                fixture.State,
                fixture.StateStore,
                lyricsOnly: true);

        Equal(1, result.Errors.Count);
        Equal(0, result.CompletedTracks);
        Equal(
            "[00:01.000]Do not delete this manual lyric",
            await File.ReadAllTextAsync(sidecar));

        CachedTrackLyrics cached = await lyricsCache.GetAsync(
            fixture.SourcePath, 42)
            ?? throw new InvalidOperationException(
                "A confirmed no-lyrics response must be cached.");
        True(!cached.HasLyrics);

        TrackState state = await fixture.LoadTrackStateAsync();
        Equal(string.Empty, state.RecipeVersion);
    }

    private static void MatchWarningHandlesChineseAndArtistSubsets()
    {
        PlanEntry entry =
            new(
                "track.mp3",
                "destination.mp3",
                PlanEntryStatus.Unchanged,
                new TrackMetadata(
                    Artist: "周杰倫;方文山",
                    AlbumArtist: "周杰倫;方文山",
                    Album: "Local Album",
                    HasEmbeddedArt: false,
                    Title: "髮如雪"));

        TrackState compatible =
            new()
            {
                SourcePath = entry.SourcePath,
                NeteaseSongId = 42,
                NeteaseTrackName = "发如雪",
                NeteaseTrackArtist = "周杰伦",
                NeteaseTrackAlbum = "Different Album",
            };

        Equal<TrackSyncWarning?>(
            null,
            SyncExecutor.DetectMatchWarning(
                entry,
                compatible));

        TrackState mismatching =
            compatible with
            {
                NeteaseTrackName =
                    "Different Title",
                NeteaseTrackArtist =
                    "Different Artist",
            };

        TrackSyncWarning warning =
            SyncExecutor.DetectMatchWarning(
                entry,
                mismatching)
            ?? throw new InvalidOperationException(
                "Expected a metadata mismatch warning.");

        True(warning.TitleMismatch);
        True(warning.ArtistMismatch);

        Equal<TrackSyncWarning?>(
            null,
            SyncExecutor.DetectMatchWarning(
                entry,
                mismatching with
                {
                    NeteaseMatchConfirmed = true,
                }));
    }

    private static void ProgressTrackerRejectsStaleStages()
    {
        SyncProgressTracker tracker = new();
        TrackSyncWarning warning =
            new(
                "warning.flac",
                "Local",
                "Remote",
                null,
                null,
                true,
                false);

        True(tracker.ShouldApply(
            new SyncExecutionProgress(
                0,
                2,
                "warning.flac",
                "recognize")));
        True(tracker.ShouldApply(
            new SyncExecutionProgress(
                0,
                2,
                "warning.flac",
                "match",
                Warning: warning)));

        // The warning is terminal for the song until manual review.
        True(!tracker.ShouldApply(
            new SyncExecutionProgress(
                0,
                2,
                "warning.flac",
                "copy-audio")));

        // Persistence may fail after the warning was reported.
        True(tracker.ShouldApply(
            new SyncExecutionProgress(
                1,
                2,
                "warning.flac",
                "failed")));
        True(!tracker.ShouldApply(
            new SyncExecutionProgress(
                1,
                2,
                "warning.flac",
                "complete")));

        True(tracker.ShouldApply(
            new SyncExecutionProgress(
                2,
                2,
                "success.flac",
                "complete")));
        True(!tracker.ShouldApply(
            new SyncExecutionProgress(
                1,
                2,
                "success.flac",
                "fetch-lyrics")));
    }

    private static async Task WarningGatesLyricsUntilConfirmationAsync()
    {
        await using Fixture fixture =
            await Fixture.CreateAsync(
                lyricsEnabled: true,
                overrideExisting: true);

        FakeRecognizer recognizer =
            new(
                TrackRecognitionResult.Matched(
                    42,
                    "Remote title",
                    "Different Artist",
                    "Remote Album",
                    "https://example.test/cover.jpg"));
        FakeLyricsProvider lyricsProvider =
            new(
                LyricsLookupResult.Found(
                    LrcDocument.Parse(
                        "[00:01.000]hello")));
        FakeLyricsWriter writer =
            new(
                LyricsProbeStatus.Missing,
                LyricsWriteStatus.Written);

        SyncExecutionSummary summary =
            await fixture.ExecuteAsync(
                recognizer,
                lyricsProvider,
                writer);

        Equal(1, summary.Warnings.Count);
        Equal(0, summary.CompletedTracks);
        Equal(0, summary.CompletedSourcePaths.Count);
        Equal(0, lyricsProvider.CallCount);
        Equal(0, writer.WriteCalls);

        TrackState persisted =
            await fixture.LoadTrackStateAsync();
        Equal(42L, persisted.NeteaseSongId ?? -1);
        Equal(
            "https://example.test/cover.jpg",
            persisted.NeteaseCoverUrl
                ?? string.Empty);
        Equal(string.Empty, persisted.RecipeVersion);
    }

    private static async Task SuppressedMatchSkipsRecognitionAsync()
    {
        await using Fixture fixture =
            await Fixture.CreateAsync(
                lyricsEnabled: true,
                overrideExisting: true);

        FileInfo source =
            new(
                fixture.SourcePath);

        fixture.State.Tracks[
            fixture.SourcePath] =
            new TrackState
            {
                SourcePath =
                    fixture.SourcePath,
                SourceSize =
                    source.Length,
                SourceLastWriteUtcTicks =
                    source.LastWriteTimeUtc
                        .Ticks,
                QuickHash =
                    QuickFileHash.Compute(
                        fixture.SourcePath),
                DestinationPath =
                    fixture.DestinationPath,
                Recognition =
                    RecognitionState.NoMatch,
                RecognitionCheckedAt =
                    DateTimeOffset.UtcNow,
                NeteaseMatchSuppressed = true,
                MetadataCaptured = true,
                MetadataSchemaVersion =
                    TrackMetadata.CurrentSchemaVersion,
                TitleCaptured = true,
                Artist = "Artist",
                AlbumArtist =
                    "Album Artist",
                Album = "Album",
                ProcessedAt =
                    DateTimeOffset.UtcNow,
            };

        FakeRecognizer recognizer =
            new(
                TrackRecognitionResult.Matched(
                    123));
        FakeLyricsProvider lyricsProvider =
            new(
                LyricsLookupResult.NoLyrics());
        FakeLyricsWriter writer =
            new(
                LyricsProbeStatus.Missing);

        SyncExecutionSummary summary =
            await fixture.ExecuteAsync(
                recognizer,
                lyricsProvider,
                writer);

        Equal(1, summary.NoMatch);
        Equal(0, recognizer.CallCount);
        Equal(0, lyricsProvider.CallCount);

        TrackState persisted =
            await fixture.LoadTrackStateAsync();
        True(persisted.NeteaseMatchSuppressed);
        Equal<long?>(null, persisted.NeteaseSongId);
        Equal(
            SyncRecipe.For(
                fixture.Settings),
            persisted.RecipeVersion);
    }

    private static async Task LyricsWritePersistsEnrichmentStateAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: true,
            overrideExisting: true);

        LrcDocument lyrics = LrcDocument.Parse(
            "[ar:MusicLoom]\n[00:01.000]hello");
        FakeRecognizer recognizer = new(
            TrackRecognitionResult.Matched(123_456));
        FakeLyricsProvider lyricsProvider = new(
            LyricsLookupResult.Found(lyrics));
        FakeLyricsWriter writer = new(
            LyricsProbeStatus.Missing,
            LyricsWriteStatus.Written);

        SyncExecutionSummary summary = await fixture.ExecuteAsync(
            recognizer,
            lyricsProvider,
            writer);

        Equal(1, summary.WrittenLyrics);
        Equal(1, summary.CompletedSourcePaths.Count);
        Equal(fixture.SourcePath, summary.CompletedSourcePaths[0]);
        Equal(0, summary.Errors.Count);
        Equal(1, recognizer.CallCount);
        Equal(1, lyricsProvider.CallCount);
        Equal(0, writer.ProbeCalls);
        Equal(1, writer.WriteCalls);

        TrackState state = await fixture.LoadTrackStateAsync();
        Equal(RecognitionState.Matched, state.Recognition);
        Equal(123_456L, state.NeteaseSongId ?? -1);
        Equal(LyricsState.Written, state.Lyrics);
        True(!string.IsNullOrWhiteSpace(state.LyricsFingerprint));
        Equal(
            SyncRecipe.For(fixture.Settings),
            state.RecipeVersion);
    }

    private static async Task RetryableFailureKeepsWarmMetadataAndAvoidsRecopyAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: true,
            overrideExisting: true);

        FakeRecognizer recognizer = new(
            new InvalidOperationException("simulated network failure"));
        FakeLyricsProvider lyricsProvider = new(
            LyricsLookupResult.NoLyrics());
        FakeLyricsWriter writer = new(LyricsProbeStatus.Missing);

        SyncExecutionSummary summary = await fixture.ExecuteAsync(
            recognizer,
            lyricsProvider,
            writer);

        Equal(1, summary.Errors.Count);
        Equal(0, summary.CompletedSourcePaths.Count);
        Equal("recognize", summary.Errors[0].Stage);
        True(File.Exists(fixture.DestinationPath));

        StateDocument persisted = await fixture.StateStore.LoadAsync();
        TrackState partial = persisted.Tracks[fixture.SourcePath];
        Equal(string.Empty, partial.RecipeVersion);
        True(partial.MetadataCaptured);

        CountingMetadataReader metadataReader = new();
        LibraryPlan retryPlan = await new LibraryScanner(metadataReader)
            .BuildPlanAsync(fixture.Settings, persisted);

        Equal(0, metadataReader.ReadCount);
        Equal(1, retryPlan.ModifiedCount);
        Equal(false, retryPlan.Entries[0].NeedsAudioCopy);
        Equal("Processing recipe changed.", retryPlan.Entries[0].Detail ?? string.Empty);
    }

    private static async Task PeriodicCheckpointPersistsBeforeFinalSaveAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: false,
            overrideExisting: false);

        FakeRecognizer recognizer = new(TrackRecognitionResult.Matched(42));
        FakeLyricsProvider lyricsProvider = new(
            LyricsLookupResult.NoLyrics());
        FakeLyricsWriter writer = new(LyricsProbeStatus.Missing);
        CountingStateStore stateStore = new(fixture.StateStore.Path);

        SyncExecutor executor = new(
            new LocalLibraryMaterializer(),
            recognizer,
            lyricsProvider,
            writer,
            checkpointInterval: 1);

        SyncExecutionSummary summary = await executor.ExecuteAsync(
            fixture.Settings,
            fixture.Plan,
            fixture.State,
            stateStore);

        Equal(1, summary.CompletedTracks);
        Equal(2, stateStore.SaveCount);
    }

    private static async Task RetryableErrorCheckpointsImmediatelyAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: true,
            overrideExisting: true);

        FakeRecognizer recognizer = new(
            new InvalidOperationException("simulated network failure"));
        FakeLyricsProvider lyricsProvider = new(
            LyricsLookupResult.NoLyrics());
        FakeLyricsWriter writer = new(LyricsProbeStatus.Missing);
        CountingStateStore stateStore = new(fixture.StateStore.Path);

        SyncExecutor executor = new(
            new LocalLibraryMaterializer(),
            recognizer,
            lyricsProvider,
            writer);

        SyncExecutionSummary summary = await executor.ExecuteAsync(
            fixture.Settings,
            fixture.Plan,
            fixture.State,
            stateStore);

        Equal(1, summary.Errors.Count);
        // One immediate error checkpoint plus the final snapshot.
        Equal(2, stateStore.SaveCount);
    }

    private static async Task CancellationPersistsPartialStateAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: true,
            overrideExisting: true);

        FakeRecognizer recognizer = new(
            new OperationCanceledException("simulated user cancellation"));
        FakeLyricsProvider lyricsProvider = new(
            LyricsLookupResult.NoLyrics());
        FakeLyricsWriter writer = new(LyricsProbeStatus.Missing);

        try
        {
            await fixture.ExecuteAsync(
                recognizer,
                lyricsProvider,
                writer);
            throw new InvalidOperationException(
                "Expected sync cancellation was not propagated.");
        }
        catch (OperationCanceledException)
        {
        }

        StateDocument persisted = await fixture.StateStore.LoadAsync();
        TrackState partial = persisted.Tracks[fixture.SourcePath];
        Equal(string.Empty, partial.RecipeVersion);
        True(partial.MetadataCaptured);
        True(File.Exists(fixture.DestinationPath));

        CountingMetadataReader metadataReader = new();
        LibraryPlan retryPlan = await new LibraryScanner(metadataReader)
            .BuildPlanAsync(fixture.Settings, persisted);

        Equal(0, metadataReader.ReadCount);
        Equal(1, retryPlan.ModifiedCount);
        Equal(false, retryPlan.Entries[0].NeedsAudioCopy);
    }

    private static async Task
        PathMigrationCleansUnchangedOldDestinationAsync()
    {
        await RunPathMigrationCleanupAsync(
            mutateOldDestination: false,
            expectOldDestination: false);
    }

    private static async Task
        PathMigrationPreservesDriftedOldDestinationAsync()
    {
        await RunPathMigrationCleanupAsync(
            mutateOldDestination: true,
            expectOldDestination: true);
    }

    private static async Task RunPathMigrationCleanupAsync(
        bool mutateOldDestination,
        bool expectOldDestination)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "musicloom-path-migration-"
            + Guid.NewGuid().ToString("N"));
        string sourceRoot =
            Path.Combine(root, "source");
        string destinationRoot =
            Path.Combine(root, "destination");

        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);

        try
        {
            string sourcePath =
                Path.Combine(
                    sourceRoot,
                    "track.mp3");
            await File.WriteAllBytesAsync(
                sourcePath,
                [1, 2, 3, 4, 5, 6]);

            FileInfo source =
                new(sourcePath);

            string oldDestination =
                Path.Combine(
                    destinationRoot,
                    "Artist A Artist B",
                    "Album",
                    "track.mp3");
            string newDestination =
                Path.Combine(
                    destinationRoot,
                    "Artist A;Artist B",
                    "Album",
                    "track.mp3");

            Directory.CreateDirectory(
                Path.GetDirectoryName(
                    oldDestination)!);
            File.Copy(
                sourcePath,
                oldDestination);
            File.SetLastWriteTimeUtc(
                oldDestination,
                source.LastWriteTimeUtc);

            FileInfo oldFile =
                new(oldDestination);

            TrackState previous =
                new()
                {
                    SourcePath = sourcePath,
                    SourceSize = source.Length,
                    SourceLastWriteUtcTicks =
                        source.LastWriteTimeUtc.Ticks,
                    QuickHash =
                        QuickFileHash.Compute(
                            sourcePath),
                    RecipeVersion = "legacy",
                    DestinationPath =
                        oldDestination,
                    DestinationSize =
                        oldFile.Length,
                    DestinationLastWriteUtcTicks =
                        oldFile.LastWriteTimeUtc.Ticks,
                    MetadataCaptured = true,
                    MetadataSchemaVersion =
                        TrackMetadata.CurrentSchemaVersion,
                    TitleCaptured = true,
                    Title = "Track",
                    AlbumArtist =
                        "Artist A;Artist B",
                    Album = "Album",
                    ProcessedAt =
                        DateTimeOffset.UtcNow,
                };

            if (mutateOldDestination)
            {
                await File.AppendAllTextAsync(
                    oldDestination,
                    "user edit");
            }

            MusicLoomSettings settings =
                new()
                {
                    Path = new PathSettings
                    {
                        SourceDirectory =
                            sourceRoot,
                        DestinationDirectory =
                            destinationRoot,
                    },
                    Organizer =
                        new OrganizerSettings
                        {
                            Lyrics =
                                new LyricsSettings
                                {
                                    Enabled = false,
                                },
                        },
                };

            TrackMetadata metadata =
                new(
                    Artist: null,
                    AlbumArtist:
                        "Artist A;Artist B",
                    Album: "Album",
                    HasEmbeddedArt: false,
                    Title: "Track");

            LibraryPlan plan =
                new(
                [
                    new PlanEntry(
                        sourcePath,
                        newDestination,
                        PlanEntryStatus.Modified,
                        metadata,
                        "Destination path changed.",
                        NeedsAudioCopy: true),
                ]);

            StateDocument state =
                new();
            state.Tracks[sourcePath] =
                previous;

            StateStore store =
                new(
                    Path.Combine(
                        root,
                        "state.json"));

            SyncExecutor executor =
                new(
                    new LocalLibraryMaterializer(),
                    new FakeRecognizer(
                        TrackRecognitionResult
                            .NoMatch()),
                    new FakeLyricsProvider(
                        LyricsLookupResult
                            .NoLyrics()),
                    new FakeLyricsWriter(
                        LyricsProbeStatus.Missing));

            SyncExecutionSummary summary =
                await executor.ExecuteAsync(
                    settings,
                    plan,
                    state,
                    store);

            Equal(0, summary.Errors.Count);
            True(File.Exists(
                newDestination));
            Equal(
                expectOldDestination,
                File.Exists(
                    oldDestination));

            if (!expectOldDestination)
            {
                True(!Directory.Exists(
                    Path.Combine(
                        destinationRoot,
                        "Artist A Artist B")));
            }
        }
        finally
        {
            Directory.Delete(
                root,
                recursive: true);
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"expected '{expected}', got '{actual}'");
        }
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("condition was false");
        }
    }

    private sealed class FakeRecognizer : ITrackRecognizer
    {
        private readonly TrackRecognitionResult? _result;
        private readonly Exception? _exception;

        public FakeRecognizer(TrackRecognitionResult result)
        {
            _result = result;
        }

        public FakeRecognizer(Exception exception)
        {
            _exception = exception;
        }

        public int CallCount { get; private set; }

        public Task<TrackRecognitionResult> RecognizeAsync(
            string audioFilePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;

            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(
                _result
                ?? throw new InvalidOperationException(
                    "Fake recognizer has no result."));
        }
    }

    private sealed class FakeLyricsProvider : ITrackLyricsProvider
    {
        private readonly LyricsLookupResult _result;

        public FakeLyricsProvider(LyricsLookupResult result)
        {
            _result = result;
        }

        public int CallCount { get; private set; }

        public Task<LyricsLookupResult> GetLyricsAsync(
            long trackId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FakeLyricsWriter : ITrackLyricsWriter
    {
        private readonly LyricsProbeStatus _probeStatus;
        private readonly LyricsWriteStatus _writeStatus;

        public FakeLyricsWriter(
            LyricsProbeStatus probeStatus,
            LyricsWriteStatus writeStatus = LyricsWriteStatus.Written)
        {
            _probeStatus = probeStatus;
            _writeStatus = writeStatus;
        }

        public int ProbeCalls { get; private set; }
        public int WriteCalls { get; private set; }

        public ValueTask<LyricsProbeResult> ProbeAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCalls++;
            return ValueTask.FromResult(
                new LyricsProbeResult(_probeStatus));
        }

        public ValueTask<LyricsWriteResult> WriteAsync(
            string path,
            LrcDocument lyrics,
            bool overrideExisting,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCalls++;
            return ValueTask.FromResult(
                new LyricsWriteResult(_writeStatus));
        }
    }

    private sealed class ThrowingLyricsWriter : ITrackLyricsWriter
    {
        public ValueTask<LyricsProbeResult> ProbeAsync(
            string path,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new LyricsProbeResult(
                LyricsProbeStatus.Missing));

        public ValueTask<LyricsWriteResult> WriteAsync(
            string path,
            LrcDocument lyrics,
            bool overrideExisting,
            CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated output write failure.");
    }

    private sealed class InMemoryLibraryLyricsCache : ILibraryLyricsCache
    {
        private readonly Dictionary<string, LibraryLyricsRecord> _rows =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<LibraryLyricsRecord?> GetLibraryAsync(
            string sourcePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _rows.TryGetValue(sourcePath, out LibraryLyricsRecord? record);
            return Task.FromResult(record);
        }

        public Task SaveLibraryAsync(
            LibraryLyricsRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _rows[record.SourcePath] = record;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryLyricsCache : ITrackLyricsCache
    {
        private readonly Dictionary<string, CachedTrackLyrics> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<CachedTrackLyrics?> GetAsync(
            string sourcePath,
            long songId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _cache.TryGetValue(sourcePath, out CachedTrackLyrics? entry);
            return Task.FromResult(
                entry?.SongId == songId ? entry : null);
        }

        public Task SaveAsync(
            string sourcePath,
            long songId,
            LrcDocument? lyrics,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _cache[sourcePath] = new CachedTrackLyrics(
                sourcePath,
                songId,
                lyrics?.ToString(),
                DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingMetadataReader : ITrackMetadataReader
    {
        public int ReadCount { get; private set; }

        public ValueTask<TrackMetadata> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(new TrackMetadata(
                "Artist",
                "Album Artist",
                "Album",
                HasEmbeddedArt: false));
        }
    }

    private sealed class CountingStateStore : StateStore
    {
        public CountingStateStore(string path)
            : base(path)
        {
        }

        public int SaveCount { get; private set; }

        public override async Task SaveAsync(
            StateDocument state,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            await base.SaveAsync(state, cancellationToken);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string root,
            MusicLoomSettings settings,
            LibraryPlan plan,
            StateDocument state,
            StateStore stateStore,
            string sourcePath,
            string destinationPath)
        {
            Root = root;
            Settings = settings;
            Plan = plan;
            State = state;
            StateStore = stateStore;
            SourcePath = sourcePath;
            DestinationPath = destinationPath;
        }

        public string Root { get; }
        public MusicLoomSettings Settings { get; }
        public LibraryPlan Plan { get; }
        public StateDocument State { get; }
        public StateStore StateStore { get; }
        public string SourcePath { get; }
        public string DestinationPath { get; }

        public static async Task<Fixture> CreateAsync(
            bool lyricsEnabled,
            bool overrideExisting,
            string extension = ".mp3")
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "musicloom-sync-" + Guid.NewGuid().ToString("N"));
            string sourceRoot = Path.Combine(root, "source");
            string destinationRoot = Path.Combine(root, "destination");
            Directory.CreateDirectory(sourceRoot);

            string sourcePath = Path.Combine(
                sourceRoot,
                "track" + extension);
            await File.WriteAllBytesAsync(
                sourcePath,
                [1, 2, 3, 4, 5, 6]);

            MusicLoomSettings settings = new()
            {
                Path = new PathSettings
                {
                    SourceDirectory = sourceRoot,
                    DestinationDirectory = destinationRoot,
                },
                Organizer = new OrganizerSettings
                {
                    Lyrics = new LyricsSettings
                    {
                        Enabled = lyricsEnabled,
                        OverrideExisting = overrideExisting,
                    },
                },
            };

            TrackMetadata metadata = new(
                "Artist",
                "Album Artist",
                "Album",
                HasEmbeddedArt: false);

            MusicFile musicFile = new(
                sourcePath,
                sourceRoot,
                metadata.Artist,
                metadata.AlbumArtist,
                metadata.Album,
                metadata.HasEmbeddedArt);

            string destinationPath = LibraryLayout.GetDestinationPath(
                musicFile,
                destinationRoot);

            LibraryPlan plan = new(
            [
                new PlanEntry(
                    sourcePath,
                    destinationPath,
                    PlanEntryStatus.New,
                    metadata,
                    "test",
                    NeedsAudioCopy: true),
            ]);

            StateStore stateStore = new(
                Path.Combine(root, ".musicloom", "state.json"));

            return new Fixture(
                root,
                settings,
                plan,
                new StateDocument(),
                stateStore,
                sourcePath,
                destinationPath);
        }

        public async Task<SyncExecutionSummary> ExecuteAsync(
            ITrackRecognizer recognizer,
            ITrackLyricsProvider lyricsProvider,
            ITrackLyricsWriter writer)
        {
            SyncExecutor executor = new(
                new LocalLibraryMaterializer(),
                recognizer,
                lyricsProvider,
                writer);

            return await executor.ExecuteAsync(
                Settings,
                Plan,
                State,
                StateStore);
        }

        public async Task<TrackState> LoadTrackStateAsync()
        {
            StateDocument loaded = await StateStore.LoadAsync();
            return loaded.Tracks[SourcePath];
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
