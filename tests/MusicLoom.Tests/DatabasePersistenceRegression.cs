using MusicLoom.App;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.State;

internal static class DatabasePersistenceRegression
{
    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "musicloom-db-persistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            string databasePath =
                Path.Combine(root, "musicloom.db");

            MusicLoomSettings expectedSettings =
                new()
                {
                    Path =
                        new PathSettings
                        {
                            SourceDirectory =
                                Path.Combine(root, "source"),
                            DestinationDirectory =
                                Path.Combine(root, "destination"),
                            SourcePlaylistDirectory =
                                Path.Combine(root, "playlists"),
                            DestinationPlaylistBase =
                                "/storage/emulated/0/Custom Music///",
                        },
                    Organizer =
                        new OrganizerSettings
                        {
                            Lyrics =
                                new LyricsSettings
                                {
                                    Enabled = true,
                                    OverrideExisting = true,
                                    RefreshIntervalHours = 6.5,
                                    StorageMode =
                                        LyricsStorageModes.Sidecar,
                                },
                        },
                    Ui =
                        new UiSettings
                        {
                            Language = "zh-CN",
                            Theme = UiThemeModes.Dark,
                            AcrylicEnabled = true,
                            AccentColor = "#336699",
                            LongTextMode = LongTextModes.Wrap,
                            LyricsDisplayMode =
                                LyricsDisplayModes.PreferTranslation,
                        },
                };

            await LibraryDatabase.SaveSettingsAsync(
                databasePath,
                expectedSettings);

            MusicLoomSettings actualSettings =
                await LibraryDatabase.LoadSettingsAsync(
                    databasePath)
                ?? throw new InvalidOperationException(
                    "SQLite settings were not loaded.");

            Equal(
                expectedSettings.Path.SourceDirectory,
                actualSettings.Path.SourceDirectory);
            Equal(
                expectedSettings.Path.DestinationDirectory,
                actualSettings.Path.DestinationDirectory);
            Equal(
                "/storage/emulated/0/Custom Music",
                actualSettings.Path.DestinationPlaylistBase);
            Equal(
                expectedSettings.Organizer.Lyrics.RefreshIntervalHours,
                actualSettings.Organizer.Lyrics.RefreshIntervalHours);
            Equal(
                expectedSettings.Ui.Theme,
                actualSettings.Ui.Theme);
            Equal(
                expectedSettings.Ui.LyricsDisplayMode,
                actualSettings.Ui.LyricsDisplayMode);

            string legacyStatePath =
                Path.Combine(root, "state.json");

            StateDocument legacyState = new();
            string sourcePath =
                Path.Combine(root, "source", "track.flac");

            legacyState.Tracks[sourcePath] =
                new TrackState
                {
                    SourcePath = sourcePath,
                    SourceSize = 1234,
                    SourceLastWriteUtcTicks = 5678,
                    QuickHash = 9876543210,
                    RecipeVersion = "legacy-recipe",
                    DestinationPath =
                        Path.Combine(root, "destination", "track.flac"),
                    DestinationSize = 1234,
                    DestinationLastWriteUtcTicks = 5678,
                    Recognition =
                        RecognitionState.Matched,
                    RecognitionCheckedAt =
                        new DateTimeOffset(
                            2026,
                            10,
                            7,
                            1,
                            2,
                            3,
                            TimeSpan.Zero),
                    NeteaseSongId = 123456789,
                    NeteaseTrackName = "Matched title",
                    NeteaseTrackArtist = "Matched artist",
                    NeteaseTrackAlbum = "Matched album",
                    Lyrics = LyricsState.Written,
                    LyricsCheckedAt =
                        new DateTimeOffset(
                            2026,
                            10,
                            7,
                            2,
                            3,
                            4,
                            TimeSpan.Zero),
                    LyricsFingerprint = "fingerprint",
                    MetadataCaptured = true,
                    MetadataSchemaVersion = 1,
                    TitleCaptured = true,
                    Title = "Title",
                    Artist = "Artist",
                    AlbumArtist = "Album Artist",
                    Album = "Album",
                    HasEmbeddedArt = true,
                    ProcessedAt =
                        new DateTimeOffset(
                            2026,
                            10,
                            7,
                            3,
                            4,
                            5,
                            TimeSpan.Zero),
                };

            await new StateStore(
                    legacyStatePath)
                .SaveAsync(
                    legacyState);

            True(File.Exists(
                legacyStatePath));

            SqliteStateStore sqliteStore =
                new(
                    databasePath,
                    legacyStatePath);

            StateDocument migrated =
                await sqliteStore.LoadAsync();

            True(!File.Exists(
                legacyStatePath));
            True(sqliteStore.RecoveryWarning
                ?.Contains(
                    "Migrated legacy JSON",
                    StringComparison.Ordinal)
                == true);

            TrackState migratedTrack =
                migrated.Tracks[sourcePath];

            Equal(
                123456789L,
                migratedTrack.NeteaseSongId
                    ?? 0);
            Equal(
                "Matched title",
                migratedTrack.NeteaseTrackName
                    ?? string.Empty);
            Equal(
                LyricsState.Written,
                migratedTrack.Lyrics);
            Equal(
                true,
                migratedTrack.HasEmbeddedArt);

            migrated.Tracks[sourcePath] =
                migratedTrack with
                {
                    NeteaseTrackName =
                        "Updated in SQLite",
                    Lyrics =
                        LyricsState.NoLyrics,
                };

            await sqliteStore.SaveAsync(
                migrated);

            SqliteStateStore reloadedStore =
                new(
                    databasePath,
                    legacyStatePath);

            StateDocument reloaded =
                await reloadedStore.LoadAsync();

            Equal(
                "Updated in SQLite",
                reloaded.Tracks[sourcePath]
                    .NeteaseTrackName
                ?? string.Empty);
            Equal(
                LyricsState.NoLyrics,
                reloaded.Tracks[sourcePath]
                    .Lyrics);
            True(!File.Exists(
                legacyStatePath));
            True(
                LibraryDatabase.GetStorageSizeBytes(
                    databasePath)
                > 0);

            await LibraryDatabase.ClearAsync(
                databasePath);

            Equal(
                0L,
                LibraryDatabase.GetStorageSizeBytes(
                    databasePath));
            True(!File.Exists(
                databasePath));
        }
        finally
        {
            Directory.Delete(
                root,
                recursive: true);
        }
    }

    private static void Equal<T>(
        T expected,
        T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default
            .Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"expected '{expected}', got '{actual}'");
        }
    }

    private static void True(
        bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException(
                "condition was false");
        }
    }
}
