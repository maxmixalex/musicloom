using Microsoft.Data.Sqlite;
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
            string configPath =
                Path.Combine(root, "config.toml");

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
                                    IncludeRomanization = true,
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

            await CreateLegacySettingsAsync(
                databasePath,
                expectedSettings);

            MusicLoomSettings migratedSettings =
                await LibraryDatabase
                    .LoadLegacySettingsAsync(
                        databasePath)
                ?? throw new InvalidOperationException(
                    "Legacy SQLite settings were not loaded.");

            await TomlSettingsStore.SaveAsync(
                configPath,
                migratedSettings);
            await LibraryDatabase
                .DropLegacySettingsAsync(
                    databasePath);

            True(!await TableExistsAsync(
                databasePath,
                "app_settings"));

            MusicLoomSettings actualSettings =
                await TomlSettingsStore.LoadAsync(
                    configPath);

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
            Equal(4, actualSettings.Matching.MaxConcurrency);
            True(MatchingSettings.Normalize(0) == 1);
            True(MatchingSettings.Normalize(16) == 12);
            await TomlSettingsStore.SaveAsync(configPath,
                actualSettings with { Matching = new MatchingSettings
                    { MaxConcurrency = 8 } });
            MusicLoomSettings concurrencyReloaded =
                await TomlSettingsStore.LoadAsync(configPath);
            Equal(8, concurrencyReloaded.Matching.MaxConcurrency);
            await File.WriteAllTextAsync(configPath,
                "[matching]\nmax_concurrency = 999\n");
            Equal(12, (await TomlSettingsStore.LoadAsync(
                configPath)).Matching.MaxConcurrency);
            await TomlSettingsStore.SaveAsync(configPath, actualSettings);
            True(File.Exists(
                configPath));
            True(
                (await File.ReadAllTextAsync(
                    configPath))
                .Contains(
                    "[organizer.lyrics]",
                    StringComparison.Ordinal));

            string legacyStatePath =
                Path.Combine(root, "state.json");

            StateDocument legacyState = new();
            string sourcePath =
                Path.Combine(root, "source", "track.flac");

            // Simulate the existing v3 database, whose remote merged cache
            // had no merge-algorithm version column.
            await using (SqliteConnection legacyDb =
                new($"Data Source={databasePath}"))
            {
                await legacyDb.OpenAsync();
                await using SqliteCommand createLegacy =
                    legacyDb.CreateCommand();
                createLegacy.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS sync_lyrics (
                        source_path TEXT PRIMARY KEY COLLATE NOCASE,
                        song_id INTEGER NOT NULL,
                        lrc TEXT NULL,
                        fetched_at TEXT NOT NULL
                    );
                    INSERT INTO sync_lyrics (
                        source_path, song_id, lrc, fetched_at
                    ) VALUES (
                        $source, 555, '[00:01.000]Old cached lyrics',
                        '2026-10-01T00:00:00.0000000+00:00'
                    );
                    """;
                createLegacy.Parameters.AddWithValue("$source", sourcePath);
                await createLegacy.ExecuteNonQueryAsync();
            }

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
                    NeteaseCoverUrl =
                        "https://example.test/cover.jpg",
                    NeteaseMatchConfirmed = true,
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
                "https://example.test/cover.jpg",
                migratedTrack.NeteaseCoverUrl
                    ?? string.Empty);
            Equal(
                true,
                migratedTrack.NeteaseMatchConfirmed);
            Equal(
                true,
                migratedTrack.HasEmbeddedArt);

            // Lyrics cache is independent of the sync_tracks checkpoint.
            // It also persists a verified "no lyrics" response, and a new
            // NetEase match cannot reuse the previous song's lyrics.
            SqliteLyricsCache lyricsCache =
                new(databasePath);
            MusicLoom.Core.Lyrics.CachedTrackLyrics migratedLegacy =
                await lyricsCache.GetAsync(sourcePath, 555)
                ?? throw new InvalidOperationException(
                    "Legacy merged lyrics were lost on migration.");
            Equal(1, migratedLegacy.MergeVersion);

            await lyricsCache.SaveAsync(
                sourcePath,
                123456789L,
                MusicLoom.Core.Lyrics.LrcDocument.Parse(
                    "[00:01.000]Canonical lyric"));

            MusicLoom.Core.Lyrics.CachedTrackLyrics storedLyrics =
                await lyricsCache.GetAsync(
                    sourcePath,
                    123456789L)
                ?? throw new InvalidOperationException(
                    "Lyrics cache lost the saved document.");
            Equal(
                MusicLoom.Core.Lyrics.LrcDocument.MergeAlgorithmVersion,
                storedLyrics.MergeVersion);

            Equal(
                "[00:01.000]Canonical lyric",
                storedLyrics.Lrc ?? string.Empty);

            True(await lyricsCache.GetAsync(
                sourcePath,
                987654321L) is null);

            migrated.Tracks[sourcePath] =
                migratedTrack with
                {
                    NeteaseTrackName =
                        "Updated in SQLite",
                    NeteaseSongId = null,
                    NeteaseCoverUrl = null,
                    NeteaseMatchConfirmed = false,
                    NeteaseMatchSuppressed = true,
                    Lyrics =
                        LyricsState.NoLyrics,
                };

            await sqliteStore.SaveAsync(
                migrated);

            // Saving sync_tracks rewrites every track, but must never
            // delete the independent canonical lyrics table.
            storedLyrics = await new SqliteLyricsCache(
                databasePath).GetAsync(sourcePath, 123456789L)
                ?? throw new InvalidOperationException(
                    "State checkpoint unexpectedly erased lyrics.");
            Equal(
                "[00:01.000]Canonical lyric",
                storedLyrics.Lrc ?? string.Empty);

            await lyricsCache.SaveLibraryAsync(
                new MusicLoom.Core.Lyrics.LibraryLyricsRecord(
                    sourcePath,
                    MusicLoom.Core.Lyrics.LibraryLyricsOrigin.Sidecar,
                    "[00:01.000]Verified local lyric",
                    987654321L,
                    "source:123|sidecar:456",
                    DateTimeOffset.UtcNow));

            MusicLoom.Core.Lyrics.LibraryLyricsRecord localCanonical =
                await new SqliteLyricsCache(databasePath)
                    .GetLibraryAsync(sourcePath)
                ?? throw new InvalidOperationException(
                    "Canonical lyrics were not written to SQLite.");

            Equal(
                MusicLoom.Core.Lyrics.LibraryLyricsOrigin.Sidecar,
                localCanonical.Origin);
            Equal(
                "[00:01.000]Verified local lyric",
                localCanonical.Text ?? string.Empty);

            await lyricsCache.SaveAsync(
                sourcePath,
                987654321L,
                null);
            MusicLoom.Core.Lyrics.CachedTrackLyrics noLyrics =
                await lyricsCache.GetAsync(
                    sourcePath,
                    987654321L)
                ?? throw new InvalidOperationException(
                    "A cached no-lyrics response must not be a cache miss.");
            True(!noLyrics.HasLyrics);
            True(await lyricsCache.GetAsync(
                sourcePath,
                123456789L) is null);

            SqliteStateStore reloadedStore =
                new(
                    databasePath,
                    legacyStatePath);

            StateDocument reloaded =
                await reloadedStore.LoadAsync();
            localCanonical = await new SqliteLyricsCache(databasePath)
                .GetLibraryAsync(sourcePath)
                ?? throw new InvalidOperationException(
                    "Track checkpoint erased the canonical lyrics.");
            Equal(
                "[00:01.000]Verified local lyric",
                localCanonical.Text ?? string.Empty);


            Equal(
                "Updated in SQLite",
                reloaded.Tracks[sourcePath]
                    .NeteaseTrackName
                ?? string.Empty);
            Equal(
                LyricsState.NoLyrics,
                reloaded.Tracks[sourcePath]
                    .Lyrics);
            Equal(
                true,
                reloaded.Tracks[sourcePath]
                    .NeteaseMatchSuppressed);
            Equal(
                false,
                reloaded.Tracks[sourcePath]
                    .NeteaseMatchConfirmed);
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
            True(File.Exists(
                configPath));

            MusicLoomSettings afterClear =
                await TomlSettingsStore.LoadAsync(
                    configPath);
            Equal(
                expectedSettings.Path.SourceDirectory,
                afterClear.Path.SourceDirectory);
        }
        finally
        {
            Directory.Delete(
                root,
                recursive: true);
        }
    }

    private static async Task CreateLegacySettingsAsync(
        string databasePath,
        MusicLoomSettings settings)
    {
        await LibraryDatabase.InitializeAsync(
            databasePath);

        await using SqliteConnection connection =
            new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                }.ToString());
        await connection.OpenAsync();

        await using SqliteCommand command =
            connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE app_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                verbose INTEGER NOT NULL,
                source_directory TEXT NOT NULL,
                destination_directory TEXT NOT NULL,
                source_playlist_directory TEXT NOT NULL,
                destination_playlist_base TEXT NOT NULL,
                lyrics_enabled INTEGER NOT NULL,
                lyrics_override_existing INTEGER NOT NULL,
                lyrics_refresh_interval_hours REAL NOT NULL,
                lyrics_storage_mode TEXT NOT NULL,
                logging_level TEXT NOT NULL,
                logging_format TEXT NOT NULL,
                logging_date_format TEXT NOT NULL,
                ui_language TEXT NOT NULL,
                ui_acrylic_enabled INTEGER NOT NULL,
                ui_accent_color TEXT NOT NULL,
                ui_theme TEXT NOT NULL,
                ui_auto_scroll_long_titles INTEGER NOT NULL,
                ui_long_text_mode TEXT NULL,
                ui_lyrics_display_mode TEXT NOT NULL
            );

            INSERT INTO app_settings (
                id,
                verbose,
                source_directory,
                destination_directory,
                source_playlist_directory,
                destination_playlist_base,
                lyrics_enabled,
                lyrics_override_existing,
                lyrics_refresh_interval_hours,
                lyrics_storage_mode,
                logging_level,
                logging_format,
                logging_date_format,
                ui_language,
                ui_acrylic_enabled,
                ui_accent_color,
                ui_theme,
                ui_auto_scroll_long_titles,
                ui_long_text_mode,
                ui_lyrics_display_mode
            ) VALUES (
                1,
                $verbose,
                $source,
                $destination,
                $playlistSource,
                $playlistBase,
                $lyricsEnabled,
                $lyricsOverride,
                $lyricsRefresh,
                $lyricsStorage,
                $logLevel,
                $logFormat,
                $logDate,
                $language,
                $acrylic,
                $accent,
                $theme,
                $autoScroll,
                $longText,
                $lyricsDisplay
            );
            """;
        command.Parameters.AddWithValue(
            "$verbose",
            settings.Verbose ? 1 : 0);
        command.Parameters.AddWithValue(
            "$source",
            settings.Path.SourceDirectory);
        command.Parameters.AddWithValue(
            "$destination",
            settings.Path.DestinationDirectory);
        command.Parameters.AddWithValue(
            "$playlistSource",
            settings.Path.SourcePlaylistDirectory);
        command.Parameters.AddWithValue(
            "$playlistBase",
            settings.Path.DestinationPlaylistBase);
        command.Parameters.AddWithValue(
            "$lyricsEnabled",
            settings.Organizer.Lyrics.Enabled
                ? 1
                : 0);
        command.Parameters.AddWithValue(
            "$lyricsOverride",
            settings.Organizer.Lyrics.OverrideExisting
                ? 1
                : 0);
        command.Parameters.AddWithValue(
            "$lyricsRefresh",
            settings.Organizer.Lyrics.RefreshIntervalHours);
        command.Parameters.AddWithValue(
            "$lyricsStorage",
            settings.Organizer.Lyrics.StorageMode);
        command.Parameters.AddWithValue(
            "$logLevel",
            settings.Logging.Level);
        command.Parameters.AddWithValue(
            "$logFormat",
            settings.Logging.Format);
        command.Parameters.AddWithValue(
            "$logDate",
            settings.Logging.DateFormat);
        command.Parameters.AddWithValue(
            "$language",
            settings.Ui.Language);
        command.Parameters.AddWithValue(
            "$acrylic",
            settings.Ui.AcrylicEnabled ? 1 : 0);
        command.Parameters.AddWithValue(
            "$accent",
            settings.Ui.AccentColor);
        command.Parameters.AddWithValue(
            "$theme",
            settings.Ui.Theme);
        command.Parameters.AddWithValue(
            "$autoScroll",
            settings.Ui.AutoScrollLongTitles
                ? 1
                : 0);
        command.Parameters.AddWithValue(
            "$longText",
            (object?)settings.Ui.LongTextMode
                ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$lyricsDisplay",
            settings.Ui.LyricsDisplayMode);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExistsAsync(
        string databasePath,
        string tableName)
    {
        await using SqliteConnection connection =
            new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                }.ToString());
        await connection.OpenAsync();

        await using SqliteCommand command =
            connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue(
            "$name",
            tableName);

        return await command.ExecuteScalarAsync()
            is not null;
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
