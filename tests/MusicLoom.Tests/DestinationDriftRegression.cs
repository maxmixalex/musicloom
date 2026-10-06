using MusicLoom.Core.Configuration;
using MusicLoom.Core.Enrichment;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Planning;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Core.Synchronization;

internal static class DestinationDriftRegression
{
    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "musicloom-drift-" + Guid.NewGuid().ToString("N"));
        string sourceRoot = Path.Combine(root, "source");
        string destinationRoot = Path.Combine(root, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);

        try
        {
            string sourcePath = Path.Combine(sourceRoot, "track.mp3");
            await File.WriteAllBytesAsync(
                sourcePath,
                [1, 2, 3, 4, 5, 6]);

            TrackMetadata metadata = new(
                "Artist",
                "Album Artist",
                "Album",
                HasEmbeddedArt: false);

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
                        Enabled = false,
                    },
                },
            };

            MusicFile musicFile = new(
                sourcePath,
                sourceRoot,
                metadata.Artist,
                metadata.AlbumArtist,
                metadata.Album,
                metadata.HasEmbeddedArt);
            string destinationPath =
                LibraryLayout.GetDestinationPath(
                    musicFile,
                    destinationRoot);

            Directory.CreateDirectory(
                Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
            File.SetLastWriteTimeUtc(
                destinationPath,
                File.GetLastWriteTimeUtc(sourcePath));

            FileInfo source = new(sourcePath);
            FileInfo destination = new(destinationPath);

            StateDocument state = new();
            state.Tracks[sourcePath] = new TrackState
            {
                SourcePath = sourcePath,
                SourceSize = source.Length,
                SourceLastWriteUtcTicks =
                    source.LastWriteTimeUtc.Ticks,
                QuickHash = QuickFileHash.Compute(sourcePath),
                DestinationPath = destinationPath,
                DestinationSize = destination.Length,
                DestinationLastWriteUtcTicks =
                    destination.LastWriteTimeUtc.Ticks,
                RecipeVersion = SyncRecipe.For(settings),
                MetadataCaptured = true,
            TitleCaptured = true,
                TitleCaptured = true,
                Artist = metadata.Artist,
                AlbumArtist = metadata.AlbumArtist,
                Album = metadata.Album,
                HasEmbeddedArt = metadata.HasEmbeddedArt,
                ProcessedAt = DateTimeOffset.UtcNow,
            };

            CountingReader initialReader = new();
            LibraryPlan initial = await new LibraryScanner(
                initialReader).BuildPlanAsync(settings, state);

            Equal(1, initial.UnchangedCount);
            Equal(0, initial.PendingTrackCount);
            Equal(0, initialReader.ReadCount);

            // External target mutation: source is untouched.
            await File.WriteAllBytesAsync(
                destinationPath,
                [9, 9, 9, 9, 9, 9, 9, 9]);

            CountingReader driftReader = new();
            LibraryPlan drift = await new LibraryScanner(
                driftReader).BuildPlanAsync(settings, state);

            Equal(1, drift.ModifiedCount);
            Equal(0, driftReader.ReadCount);
            Equal(true, drift.Entries[0].NeedsAudioCopy);
            Equal(
                "Destination file changed.",
                drift.Entries[0].Detail ?? string.Empty);

            StateStore store = new(
                Path.Combine(root, ".musicloom", "state.json"));
            SyncExecutor executor = new(
                new LocalLibraryMaterializer(),
                new NeverRecognizer(),
                new NeverLyricsProvider(),
                new NeverLyricsWriter());

            SyncExecutionSummary summary =
                await executor.ExecuteAsync(
                    settings,
                    drift,
                    state,
                    store);

            Equal(1, summary.CompletedTracks);
            Equal(1, summary.Local.CopiedTracks);

            StateDocument completed = await store.LoadAsync();
            TrackState completedTrack = completed.Tracks[sourcePath];
            FileInfo completedDestination =
                new(destinationPath);

            Equal(
                completedDestination.Length,
                completedTrack.DestinationSize ?? -1);
            Equal(
                completedDestination.LastWriteTimeUtc.Ticks,
                completedTrack.DestinationLastWriteUtcTicks ?? -1);

            CountingReader finalReader = new();
            LibraryPlan final = await new LibraryScanner(
                finalReader).BuildPlanAsync(
                    settings,
                    completed);

            Equal(1, final.UnchangedCount);
            Equal(0, final.PendingTrackCount);
            Equal(0, finalReader.ReadCount);

            // Legacy states without a destination signature migrate lazily:
            // no forced recopy of an otherwise valid library.
            StateDocument legacy = completed with
            {
                Tracks = new Dictionary<string, TrackState>(
                    completed.Tracks,
                    StringComparer.OrdinalIgnoreCase),
            };
            legacy.Tracks[sourcePath] = completedTrack with
            {
                DestinationSize = null,
                DestinationLastWriteUtcTicks = null,
            };

            CountingReader legacyReader = new();
            LibraryPlan legacyPlan = await new LibraryScanner(
                legacyReader).BuildPlanAsync(
                    settings,
                    legacy);

            Equal(1, legacyPlan.UnchangedCount);
            Equal(0, legacyPlan.PendingTrackCount);
            Equal(0, legacyReader.ReadCount);

            // Clock rollback / a future state timestamp must not extend a
            // negative provider result beyond the intended 24-hour TTL.
            MusicLoomSettings lyricsSettings = settings with
            {
                Organizer = new OrganizerSettings
                {
                    Lyrics = new LyricsSettings
                    {
                        Enabled = true,
                        OverrideExisting = false,
                    },
                },
            };
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TrackState futureNegative = completedTrack with
            {
                RecipeVersion = SyncRecipe.For(lyricsSettings),
                Recognition = RecognitionState.NoMatch,
                RecognitionCheckedAt = now.AddHours(1),
                NeteaseSongId = null,
                Lyrics = LyricsState.Unknown,
                LyricsCheckedAt = null,
            };

            Equal(
                false,
                StateStore.IsEnrichmentFresh(
                    futureNegative,
                    lyricsSettings,
                    now));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Equal<T>(T expected, T actual)
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

    private sealed class CountingReader : ITrackMetadataReader
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

    private sealed class NeverRecognizer : ITrackRecognizer
    {
        public Task<TrackRecognitionResult> RecognizeAsync(
            string audioFilePath,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Lyrics-disabled drift repair must not recognize audio.");
        }
    }

    private sealed class NeverLyricsProvider : ITrackLyricsProvider
    {
        public Task<LyricsLookupResult> GetLyricsAsync(
            long trackId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Lyrics-disabled drift repair must not fetch lyrics.");
        }
    }

    private sealed class NeverLyricsWriter : ITrackLyricsWriter
    {
        public ValueTask<LyricsProbeResult> ProbeAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Lyrics-disabled drift repair must not probe lyrics.");
        }

        public ValueTask<LyricsWriteResult> WriteAsync(
            string path,
            LrcDocument lyrics,
            bool overrideExisting,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Lyrics-disabled drift repair must not write lyrics.");
        }
    }
}
