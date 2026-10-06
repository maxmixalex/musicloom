using MusicLoom.Core.Configuration;
using MusicLoom.Core.Enrichment;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Planning;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Core.Synchronization;

internal static class ResilienceRegression
{
    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        await StateBackupRecoversCorruptPrimaryAsync();
        await MaterializerContinuesAfterTrackFailureAsync();
        await ExecutorSkipsFailedMaterializationAsync();
    }

    private static async Task StateBackupRecoversCorruptPrimaryAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "musicloom-state-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            string statePath = Path.Combine(root, "state.json");
            StateStore store = new(statePath);

            StateDocument first = new()
            {
                RecipeVersion = "first",
            };
            StateDocument second = new()
            {
                RecipeVersion = "second",
            };
            StateDocument third = new()
            {
                RecipeVersion = "third",
            };

            await store.SaveAsync(first);
            await store.SaveAsync(second);

            True(File.Exists(store.BackupPath));

            await File.WriteAllTextAsync(
                statePath,
                "{ this is deliberately truncated");

            StateStore recoveredStore = new(statePath);
            StateDocument recovered =
                await recoveredStore.LoadAsync();

            Equal("first", recovered.RecipeVersion);
            True(!string.IsNullOrWhiteSpace(
                recoveredStore.RecoveryWarning));

            // Saving after recovery must not replace the good backup with
            // the damaged primary.
            await recoveredStore.SaveAsync(third);

            StateDocument primary =
                await new StateStore(statePath).LoadAsync();
            StateDocument backup =
                await new StateStore(
                    recoveredStore.BackupPath).LoadAsync();

            Equal("third", primary.RecipeVersion);
            Equal("first", backup.RecipeVersion);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task MaterializerContinuesAfterTrackFailureAsync()
    {
        await using Fixture fixture =
            await Fixture.CreateAsync();

        LocalMaterializeSummary summary =
            await new LocalLibraryMaterializer()
                .ApplyAsync(
                    fixture.Settings,
                    fixture.Plan);

        Equal(1, summary.CopiedTracks);
        Equal(1, summary.Errors.Count);
        Equal(
            LocalMaterializeErrorKind.Track,
            summary.Errors[0].Kind);
        Equal(
            fixture.BadSourcePath,
            summary.Errors[0].SourcePath);
        True(File.Exists(fixture.GoodDestinationPath));
        True(!File.Exists(fixture.BadDestinationPath));
    }

    private static async Task ExecutorSkipsFailedMaterializationAsync()
    {
        await using Fixture fixture =
            await Fixture.CreateAsync();

        CountingRecognizer recognizer = new();
        NeverLyricsProvider lyricsProvider = new();
        CountingLyricsWriter writer = new();

        SyncExecutor executor = new(
            new LocalLibraryMaterializer(),
            recognizer,
            lyricsProvider,
            writer,
            checkpointInterval: 1);

        StateStore store = new(
            Path.Combine(
                fixture.Root,
                ".musicloom",
                "state.json"));

        SyncExecutionSummary summary =
            await executor.ExecuteAsync(
                fixture.Settings,
                fixture.Plan,
                new StateDocument(),
                store);

        Equal(1, summary.CompletedTracks);
        Equal(1, summary.Errors.Count);
        Equal("copy-audio", summary.Errors[0].Stage);
        Equal(fixture.BadSourcePath, summary.Errors[0].SourcePath);
        Equal(1, recognizer.CallCount);
        Equal(0, writer.ProbeCalls);

        StateDocument persisted =
            await store.LoadAsync();

        True(persisted.Tracks.ContainsKey(
            fixture.GoodSourcePath));
        True(!persisted.Tracks.ContainsKey(
            fixture.BadSourcePath));

        TrackState good =
            persisted.Tracks[fixture.GoodSourcePath];
        Equal(
            SyncRecipe.For(fixture.Settings),
            good.RecipeVersion);
    }

    private static void Equal<T>(
        T expected,
        T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(
                expected,
                actual))
        {
            throw new InvalidOperationException(
                $"expected '{expected}', got '{actual}'");
        }
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException(
                "condition was false");
        }
    }

    private sealed class CountingRecognizer :
        ITrackRecognizer
    {
        public int CallCount { get; private set; }

        public Task<TrackRecognitionResult> RecognizeAsync(
            string audioFilePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(
                TrackRecognitionResult.NoMatch());
        }
    }

    private sealed class NeverLyricsProvider :
        ITrackLyricsProvider
    {
        public Task<LyricsLookupResult> GetLyricsAsync(
            long trackId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "No-match recognition must not fetch lyrics.");
        }
    }

    private sealed class CountingLyricsWriter :
        ITrackLyricsWriter
    {
        public int ProbeCalls { get; private set; }

        public ValueTask<LyricsProbeResult> ProbeAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeCalls++;
            return ValueTask.FromResult(
                new LyricsProbeResult(
                    LyricsProbeStatus.Missing));
        }

        public ValueTask<LyricsWriteResult> WriteAsync(
            string path,
            LrcDocument lyrics,
            bool overrideExisting,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "No-match recognition must not write lyrics.");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            string root,
            MusicLoomSettings settings,
            LibraryPlan plan,
            string goodSourcePath,
            string badSourcePath,
            string goodDestinationPath,
            string badDestinationPath)
        {
            Root = root;
            Settings = settings;
            Plan = plan;
            GoodSourcePath = goodSourcePath;
            BadSourcePath = badSourcePath;
            GoodDestinationPath = goodDestinationPath;
            BadDestinationPath = badDestinationPath;
        }

        public string Root { get; }
        public MusicLoomSettings Settings { get; }
        public LibraryPlan Plan { get; }
        public string GoodSourcePath { get; }
        public string BadSourcePath { get; }
        public string GoodDestinationPath { get; }
        public string BadDestinationPath { get; }

        public static async Task<Fixture> CreateAsync()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "musicloom-resilience-"
                + Guid.NewGuid().ToString("N"));
            string sourceRoot =
                Path.Combine(root, "source");
            string destinationRoot =
                Path.Combine(root, "destination");

            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(destinationRoot);

            string goodSource = Path.Combine(
                sourceRoot,
                "good.mp3");
            string badSource = Path.Combine(
                sourceRoot,
                "bad.mp3");
            await File.WriteAllBytesAsync(
                goodSource,
                [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(
                badSource,
                [5, 6, 7, 8]);

            string goodDestination = Path.Combine(
                destinationRoot,
                "Artist",
                "Album",
                "good.mp3");

            // A regular file where a directory is required reliably makes
            // only the bad materialization fail on Windows and Linux.
            string blockedParent =
                Path.Combine(destinationRoot, "blocked");
            await File.WriteAllTextAsync(
                blockedParent,
                "not a directory");
            string badDestination =
                Path.Combine(
                    blockedParent,
                    "bad.mp3");

            TrackMetadata metadata = new(
                "Artist",
                "Album Artist",
                "Album",
                HasEmbeddedArt: false);

            LibraryPlan plan = new(
                [
                    new PlanEntry(
                        badSource,
                        badDestination,
                        PlanEntryStatus.New,
                        metadata,
                        "test failure isolation",
                        NeedsAudioCopy: true),
                    new PlanEntry(
                        goodSource,
                        goodDestination,
                        PlanEntryStatus.New,
                        metadata,
                        "test success after failure",
                        NeedsAudioCopy: true),
                ],
                covers:
                [
                    new CoverPlanEntry(
                        goodSource,
                        goodDestination + ".unused-cover",
                        NeedsWrite: false),
                ]);

            MusicLoomSettings settings = new()
            {
                Path = new PathSettings
                {
                    SourceDirectory = sourceRoot,
                    DestinationDirectory =
                        destinationRoot,
                },
                Organizer = new OrganizerSettings
                {
                    Lyrics = new LyricsSettings
                    {
                        Enabled = true,
                        OverrideExisting = true,
                    },
                },
            };

            return new Fixture(
                root,
                settings,
                plan,
                goodSource,
                badSource,
                goodDestination,
                badDestination);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(
                    Root,
                    recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
