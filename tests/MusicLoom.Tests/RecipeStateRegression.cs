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
        await NegativeEnrichmentTtlAsync();
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
                QuickHash = QuickFileHash.Compute(sourcePath),
                DestinationPath = destinationPath,
                RecipeVersion = SyncRecipe.For(oldSettings),
                MetadataCaptured = true,
            MetadataSchemaVersion = TrackMetadata.CurrentSchemaVersion,
                TitleCaptured = true,
                Artist = metadata.Artist,
                AlbumArtist = metadata.AlbumArtist,
                Album = metadata.Album,
                HasEmbeddedArt = metadata.HasEmbeddedArt,
                ProcessedAt = DateTimeOffset.UtcNow,
            };

            CountingRecipeMetadataReader reader = new();
            LibraryPlan plan = await new LibraryScanner(reader)
                .BuildPlanAsync(newSettings, state);

            // Retiring an old disabled NetEase-fetch toggle does not
            // invalidate offline audio that was already synchronized.
            Equal(0, plan.ModifiedCount);
            Equal(1, plan.UnchangedCount);
            Equal(0, reader.ReadCount);
            Equal(false, plan.Entries[0].NeedsAudioCopy);

            // An actual output-format change still schedules work, without
            // rereading metadata or recopying unchanged audio.
            MusicLoomSettings sidecarSettings = newSettings with
            {
                Organizer = newSettings.Organizer with
                {
                    Lyrics = newSettings.Organizer.Lyrics with
                    {
                        StorageMode = LyricsStorageModes.Sidecar,
                    },
                },
            };
            LibraryPlan changed = await new LibraryScanner(reader)
                .BuildPlanAsync(sidecarSettings, state);
            Equal(1, changed.ModifiedCount);
            Equal(0, reader.ReadCount);
            Equal("Processing recipe changed.", changed.Entries[0].Detail ?? string.Empty);
            Equal(false, changed.Entries[0].NeedsAudioCopy);
            Equal("Album Artist", changed.Entries[0].Metadata?.AlbumArtist ?? string.Empty);
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
                QuickHash = QuickFileHash.Compute(sourcePath),
                DestinationPath = oldDestinationPath,
                RecipeVersion = SyncRecipe.For(oldSettings),
                MetadataCaptured = true,
            MetadataSchemaVersion = TrackMetadata.CurrentSchemaVersion,
                TitleCaptured = true,
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

    private static async Task NegativeEnrichmentTtlAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "musicloom-negative-ttl-" + Guid.NewGuid().ToString("N"));
        string sourceRoot = Path.Combine(root, "source");
        string destinationRoot = Path.Combine(root, "destination");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(destinationRoot);

        try
        {
            string sourcePath = Path.Combine(sourceRoot, "track.mp3");
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);

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
                        Enabled = true,
                        OverrideExisting = false,
                    },
                },
            };

            string destinationPath = Path.Combine(
                destinationRoot,
                "Album Artist",
                "Album",
                "track.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);

            FileInfo source = new(sourcePath);
            DateTimeOffset now = new(
                2026,
                10,
                5,
                20,
                0,
                0,
                TimeSpan.Zero);

            TrackState BaseState() => new()
            {
                SourcePath = sourcePath,
                SourceSize = source.Length,
                SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks,
                QuickHash = QuickFileHash.Compute(sourcePath),
                DestinationPath = destinationPath,
                RecipeVersion = SyncRecipe.For(settings),
                MetadataCaptured = true,
            MetadataSchemaVersion = TrackMetadata.CurrentSchemaVersion,
                TitleCaptured = true,
                Artist = "Artist",
                AlbumArtist = "Album Artist",
                Album = "Album",
                HasEmbeddedArt = false,
                ProcessedAt = now,
            };

            CountingRecipeMetadataReader reader = new();

            StateDocument freshNoMatch = new();
            freshNoMatch.Tracks[sourcePath] = BaseState() with
            {
                Recognition = RecognitionState.NoMatch,
                RecognitionCheckedAt = now - TimeSpan.FromHours(23),
            };

            LibraryPlan freshNoMatchPlan = await new LibraryScanner(reader)
                .BuildPlanAsync(
                    settings,
                    freshNoMatch,
                    now: now);
            Equal(1, freshNoMatchPlan.UnchangedCount);
            Equal(0, reader.ReadCount);

            StateDocument staleNoMatch = new();
            staleNoMatch.Tracks[sourcePath] = BaseState() with
            {
                Recognition = RecognitionState.NoMatch,
                RecognitionCheckedAt = now - TimeSpan.FromHours(25),
            };

            LibraryPlan staleNoMatchPlan = await new LibraryScanner(reader)
                .BuildPlanAsync(
                    settings,
                    staleNoMatch,
                    now: now);
            // Online recognition is an explicit action, not a sync reason.
            Equal(1, staleNoMatchPlan.UnchangedCount);
            Equal(false, staleNoMatchPlan.Entries[0].NeedsAudioCopy);
            Equal(0, reader.ReadCount);

            StateDocument freshNoLyrics = new();
            freshNoLyrics.Tracks[sourcePath] = BaseState() with
            {
                Recognition = RecognitionState.Matched,
                RecognitionCheckedAt = now - TimeSpan.FromDays(30),
                NeteaseSongId = 123,
                Lyrics = LyricsState.NoLyrics,
                LyricsCheckedAt = now - TimeSpan.FromHours(23),
            };

            LibraryPlan freshNoLyricsPlan = await new LibraryScanner(reader)
                .BuildPlanAsync(
                    settings,
                    freshNoLyrics,
                    now: now);
            Equal(1, freshNoLyricsPlan.UnchangedCount);
            Equal(0, reader.ReadCount);

            StateDocument staleNoLyrics = new();
            staleNoLyrics.Tracks[sourcePath] = BaseState() with
            {
                Recognition = RecognitionState.Matched,
                RecognitionCheckedAt = now - TimeSpan.FromDays(30),
                NeteaseSongId = 123,
                Lyrics = LyricsState.NoLyrics,
                LyricsCheckedAt = now - TimeSpan.FromHours(25),
            };

            LibraryPlan staleNoLyricsPlan = await new LibraryScanner(reader)
                .BuildPlanAsync(
                    settings,
                    staleNoLyrics,
                    now: now);
            // Cache expiry alone does not invalidate already written output.
            Equal(1, staleNoLyricsPlan.UnchangedCount);
            Equal(false, staleNoLyricsPlan.Entries[0].NeedsAudioCopy);
            Equal(0, reader.ReadCount);

            StateDocument matchedNeedsLyrics = new();
            matchedNeedsLyrics.Tracks[sourcePath] = BaseState() with
            {
                NeteaseSongId = 123,
                Recognition = RecognitionState.Matched,
                Lyrics = LyricsState.Unknown,
            };
            LibraryPlan matchedPending = await new LibraryScanner(reader)
                .BuildPlanAsync(settings, matchedNeedsLyrics, now: now);
            Equal(1, matchedPending.ModifiedCount);
            Equal(false, matchedPending.Entries[0].NeedsAudioCopy);

            StateDocument written = new();
            written.Tracks[sourcePath] = BaseState() with
            {
                Recognition = RecognitionState.Matched,
                RecognitionCheckedAt = now - TimeSpan.FromDays(365),
                NeteaseSongId = 123,
                Lyrics = LyricsState.Written,
                LyricsCheckedAt = now - TimeSpan.FromDays(365),
            };

            LibraryPlan writtenPlan = await new LibraryScanner(reader)
                .BuildPlanAsync(
                    settings,
                    written,
                    now: now);
            Equal(1, writtenPlan.UnchangedCount);
            Equal(0, reader.ReadCount);
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
