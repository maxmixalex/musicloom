using MusicLoom.Core.Configuration;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;

internal static class RecipeStateRegression
{
    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        await LyricsToggleUsesCachedMetadataAsync();
        await DestinationChangeUsesCachedMetadataAsync();
    }

    private static async Task LyricsToggleUsesCachedMetadataAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "musicloom-recipe-toggle-" + Guid.NewGuid().ToString("N"));
        string sourceRoot = Path.Combine(root, "source");
        string destinationRoot = Path.Combine(root, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);

        try
        {
            string sourcePath = Path.Combine(sourceRoot, "track.mp3");
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);

            MusicLoomSettings oldSettings = new()
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
                        OverrideExisting = false,
                    },
                },
            };

            MusicLoomSettings newSettings = oldSettings with
            {
                Organizer = new OrganizerSettings
                {
                    Lyrics = new LyricsSettings
                    {
                        Enabled = true,
                        OverrideExisting = true,
                    },
                },
            };

            TrackMetadata metadata = new(
                "Artist",
                "Album Artist",
                "Album",
                HasEmbeddedArt: true);

            string destinationPath = Path.Combine(
                destinationRoot,
                "Album Artist",
                "Album",
                "track.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);

            FileInfo source = new(sourcePath);
            StateDocument state = new();
            state.Tracks[sourcePath] = new TrackState
            {
                SourcePath = sourcePath,
                SourceSize = source.Length,
                SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks,
                DestinationPath = destinationPath,
                RecipeVersion = SyncRecipe.For(oldSettings),
                MetadataCaptured = true,
                Artist = metadata.Artist,
                AlbumArtist = metadata.AlbumArtist,
                Album = metadata.Album,
                HasEmbeddedArt = metadata.HasEmbeddedArt,
                ProcessedAt = DateTimeOffset.UtcNow,
            };

            CountingRecipeMetadataReader reader = new();
            LibraryPlan plan = await new LibraryScanner(reader)
                .BuildPlanAsync(newSettings, state);

            Equal(1, plan.ModifiedCount);
            Equal(0, plan.UnchangedCount);
            Equal(0, reader.ReadCount);
            Equal(1, plan.Entries.Count);
            Equal("Processing recipe changed.", plan.Entries[0].Detail ?? string.Empty);
            Equal(false, plan.Entries[0].NeedsAudioCopy);
            Equal("Album Artist", plan.Entries[0].Metadata?.AlbumArtist ?? string.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task DestinationChangeUsesCachedMetadataAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "musicloom-destination-change-" + Guid.NewGuid().ToString("N"));
        string sourceRoot = Path.Combine(root, "source");
        string oldDestinationRoot = Path.Combine(root, "destination-old");
        string newDestinationRoot = Path.Combine(root, "destination-new");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(oldDestinationRoot);

        try
        {
            string sourcePath = Path.Combine(sourceRoot, "track.mp3");
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);

            MusicLoomSettings oldSettings = new()
            {
                Path = new PathSettings
                {
                    SourceDirectory = sourceRoot,
                    DestinationDirectory = oldDestinationRoot,
                },
            };
            MusicLoomSettings newSettings = oldSettings with
            {
                Path = oldSettings.Path with
                {
                    DestinationDirectory = newDestinationRoot,
                },
            };

            string oldDestinationPath = Path.Combine(
                oldDestinationRoot,
                "Album Artist",
                "Album",
                "track.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(oldDestinationPath)!);
            File.Copy(sourcePath, oldDestinationPath);

            FileInfo source = new(sourcePath);
            StateDocument state = new();
            state.Tracks[sourcePath] = new TrackState
            {
                SourcePath = sourcePath,
                SourceSize = source.Length,
                SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks,
                DestinationPath = oldDestinationPath,
                RecipeVersion = SyncRecipe.For(oldSettings),
                MetadataCaptured = true,
                Artist = "Artist",
                AlbumArtist = "Album Artist",
                Album = "Album",
                HasEmbeddedArt = false,
                ProcessedAt = DateTimeOffset.UtcNow,
            };

            CountingRecipeMetadataReader reader = new();
            LibraryPlan plan = await new LibraryScanner(reader)
                .BuildPlanAsync(newSettings, state);

            Equal(1, plan.ModifiedCount);
            Equal(0, reader.ReadCount);
            Equal(true, plan.Entries[0].NeedsAudioCopy);
            Equal("Destination path changed.", plan.Entries[0].Detail ?? string.Empty);

            string expectedDestination = Path.Combine(
                newDestinationRoot,
                "Album Artist",
                "Album",
                "track.mp3");
            Equal(
                Path.GetFullPath(expectedDestination),
                Path.GetFullPath(plan.Entries[0].DestinationPath ?? string.Empty));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private sealed class CountingRecipeMetadataReader : ITrackMetadataReader
    {
        public int ReadCount { get; private set; }

        public ValueTask<TrackMetadata> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(new TrackMetadata(
                Artist: "Artist",
                AlbumArtist: "Album Artist",
                Album: "Album",
                HasEmbeddedArt: false));
        }
    }
}
