using MusicLoom.Core.Configuration;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Core.Synchronization;


internal static class ParityRegression
{
    public static void Run()
    {
        DetectSameStatContentChangeAsync().GetAwaiter().GetResult();
        PreserveUnreadableMetadataAsync().GetAwaiter().GetResult();
        LyricRefreshTtl();
    }

    private static async Task DetectSameStatContentChangeAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "musicloom-hash-" + Guid.NewGuid().ToString("N"));
        string sourceRoot = Path.Combine(root, "source");
        string destinationRoot = Path.Combine(root, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);

        try
        {
            string sourcePath = Path.Combine(sourceRoot, "track.mp3");
            byte[] original = Enumerable.Repeat((byte)0x11, 96 * 1024).ToArray();
            await File.WriteAllBytesAsync(sourcePath, original);
            DateTime originalMtime = File.GetLastWriteTimeUtc(sourcePath);

            TrackMetadata metadata = new(
                Artist: "Artist",
                AlbumArtist: null,
                Album: "Album",
                HasEmbeddedArt: false);

            MusicLoomSettings settings = new()
            {
                Path = new PathSettings
                {
                    SourceDirectory = sourceRoot,
                    DestinationDirectory = destinationRoot,
                },
            };

            string destinationPath = Path.Combine(
                destinationRoot,
                "Artist",
                "Album",
                "track.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
            File.SetLastWriteTimeUtc(destinationPath, originalMtime);

            FileInfo source = new(sourcePath);
            FileInfo destination = new(destinationPath);
            StateDocument state = new();
            state.Tracks[sourcePath] = new TrackState
            {
                SourcePath = sourcePath,
                SourceSize = source.Length,
                SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks,
                QuickHash = QuickFileHash.Compute(sourcePath),
                RecipeVersion = SyncRecipe.For(settings),
                DestinationPath = destinationPath,
                DestinationSize = destination.Length,
                DestinationLastWriteUtcTicks = destination.LastWriteTimeUtc.Ticks,
                MetadataCaptured = true,
                Artist = metadata.Artist,
                AlbumArtist = metadata.AlbumArtist,
                Album = metadata.Album,
                HasEmbeddedArt = metadata.HasEmbeddedArt,
                ProcessedAt = DateTimeOffset.UtcNow,
            };

            byte[] changed = Enumerable.Repeat((byte)0x22, original.Length).ToArray();
            await File.WriteAllBytesAsync(sourcePath, changed);
            File.SetLastWriteTimeUtc(sourcePath, originalMtime);

            CountingReader reader = new(metadata);
            LibraryPlan plan = await new LibraryScanner(reader)
                .BuildPlanAsync(settings, state);

            Equal(1, plan.ModifiedCount);
            True(plan.Entries[0].NeedsAudioCopy);
            Equal(1, reader.ReadCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task PreserveUnreadableMetadataAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "musicloom-fallback-" + Guid.NewGuid().ToString("N"));
        string sourceRoot = Path.Combine(root, "source");
        string destinationRoot = Path.Combine(root, "destination");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            string sourcePath = Path.Combine(sourceRoot, "broken.mp3");
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4, 5]);

            MusicLoomSettings settings = new()
            {
                Path = new PathSettings
                {
                    SourceDirectory = sourceRoot,
                    DestinationDirectory = destinationRoot,
                },
            };

            LibraryPlan plan = await new LibraryScanner(new ThrowingReader())
                .BuildPlanAsync(settings, new StateDocument());

            Equal(0, plan.ErrorCount);
            Equal(1, plan.NewCount);
            True(plan.Entries[0].NeedsAudioCopy);
            Equal(
                Path.GetFullPath(Path.Combine(destinationRoot, "broken.mp3")),
                Path.GetFullPath(plan.Entries[0].DestinationPath!));

            LocalMaterializeSummary summary =
                await new LocalLibraryMaterializer()
                    .ApplyAsync(settings, plan);
            Equal(1, summary.CopiedTracks);
            True(File.Exists(Path.Combine(destinationRoot, "broken.mp3")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void LyricRefreshTtl()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MusicLoomSettings settings = new()
        {
            Organizer = new OrganizerSettings
            {
                Lyrics = new LyricsSettings
                {
                    Enabled = true,
                    OverrideExisting = true,
                    RefreshIntervalHours = 24,
                },
            },
        };

        TrackState state = new()
        {
            SourcePath = "track.mp3",
            Lyrics = LyricsState.Written,
            LyricsCheckedAt = now.AddHours(-23),
        };

        True(StateStore.IsEnrichmentFresh(state, settings, now));
        True(!StateStore.IsEnrichmentFresh(
            state with { LyricsCheckedAt = now.AddHours(-25) },
            settings,
            now));

        MusicLoomSettings preserveExisting = settings with
        {
            Organizer = settings.Organizer with
            {
                Lyrics = settings.Organizer.Lyrics with
                {
                    OverrideExisting = false,
                },
            },
        };
        True(StateStore.IsEnrichmentFresh(
            state with { LyricsCheckedAt = now.AddDays(-365) },
            preserveExisting,
            now));
    }

    private static void Equal<T>(T expected, T actual)
        where T : notnull
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

    private sealed class CountingReader : ITrackMetadataReader
    {
        private readonly TrackMetadata _metadata;

        public CountingReader(TrackMetadata metadata)
        {
            _metadata = metadata;
        }

        public int ReadCount { get; private set; }

        public ValueTask<TrackMetadata> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(_metadata);
        }
    }

    private sealed class ThrowingReader : ITrackMetadataReader
    {
        public ValueTask<TrackMetadata> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidDataException("simulated metadata failure");
        }
    }
}
