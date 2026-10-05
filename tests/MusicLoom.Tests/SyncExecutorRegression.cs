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
        await UnsupportedLyricsUsesZeroNetworkAsync();
        await NoMatchIsNonFatalAsync();
        await LyricsWritePersistsEnrichmentStateAsync();
        await RetryableFailureKeepsWarmMetadataAndAvoidsRecopyAsync();
        await CancellationPersistsPartialStateAsync();
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

    private static async Task UnsupportedLyricsUsesZeroNetworkAsync()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            lyricsEnabled: true,
            overrideExisting: true,
            extension: ".wma");

        FakeRecognizer recognizer = new(TrackRecognitionResult.Matched(42));
        FakeLyricsProvider lyricsProvider = new(
            LyricsLookupResult.Found(LrcDocument.Parse("[00:01.000]hello")));
        FakeLyricsWriter writer = new(LyricsProbeStatus.UnsupportedFormat);

        SyncExecutionSummary summary = await fixture.ExecuteAsync(
            recognizer,
            lyricsProvider,
            writer);

        Equal(1, summary.UnsupportedLyrics);
        Equal(0, recognizer.CallCount);
        Equal(0, lyricsProvider.CallCount);
        Equal(1, writer.ProbeCalls);
        Equal(0, writer.WriteCalls);

        TrackState state = await fixture.LoadTrackStateAsync();
        Equal(LyricsState.Unsupported, state.Lyrics);
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
        Equal(0, summary.Errors.Count);
        Equal(1, recognizer.CallCount);
        Equal(1, lyricsProvider.CallCount);
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
