using MusicLoom.Core.Configuration;
using MusicLoom.Core.Planning;
using Microsoft.Data.Sqlite;
using MusicLoom.Core.Scanning;

namespace MusicLoom.App;

internal static class LibraryDatabase
{
    private const int SchemaVersion = 3;

    public static async Task InitializeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        string? directory =
            Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using SqliteConnection connection =
            new(BuildConnectionString(path));
        await connection.OpenAsync(cancellationToken);

        await using SqliteCommand command =
            connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS tracks (
                source_path TEXT PRIMARY KEY,
                destination_path TEXT NULL,
                status INTEGER NOT NULL,
                title TEXT NULL,
                artist TEXT NULL,
                album_artist TEXT NULL,
                album TEXT NULL,
                has_embedded_art INTEGER NOT NULL,
                detail TEXT NULL,
                needs_audio_copy INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_tracks_status
                ON tracks(status);
            CREATE INDEX IF NOT EXISTS idx_tracks_title
                ON tracks(title);
            CREATE INDEX IF NOT EXISTS idx_tracks_artist
                ON tracks(artist);
            CREATE INDEX IF NOT EXISTS idx_tracks_album
                ON tracks(album);

            """;

        await command.ExecuteNonQueryAsync(
            cancellationToken);

        await SetMetaAsync(
            connection,
            transaction: null,
            "schema_version",
            SchemaVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
    }

    public static async Task<MusicLoomSettings?>
        LoadLegacySettingsAsync(
            string path,
            CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using SqliteConnection connection =
            new(BuildConnectionString(path));
        await connection.OpenAsync(
            cancellationToken);

        if (!await TableExistsAsync(
                connection,
                "app_settings",
                cancellationToken))
        {
            return null;
        }

        await using SqliteCommand command =
            connection.CreateCommand();
        command.CommandText =
            """
            SELECT
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
            FROM app_settings
            WHERE id = 1;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        if (!await reader.ReadAsync(
                cancellationToken))
        {
            return null;
        }

        return new MusicLoomSettings
        {
            Verbose =
                reader.GetInt32(0) != 0,
            Path =
                new PathSettings
                {
                    SourceDirectory =
                        reader.GetString(1),
                    DestinationDirectory =
                        reader.GetString(2),
                    SourcePlaylistDirectory =
                        reader.GetString(3),
                    DestinationPlaylistBase =
                        reader.GetString(4),
                },
            Organizer =
                new OrganizerSettings
                {
                    Lyrics =
                        new LyricsSettings
                        {
                            Enabled =
                                reader.GetInt32(5)
                                    != 0,
                            OverrideExisting =
                                reader.GetInt32(6)
                                    != 0,
                            RefreshIntervalHours =
                                reader.GetDouble(7),
                            StorageMode =
                                reader.GetString(8),
                        },
                },
            Logging =
                new LoggingSettings
                {
                    Level =
                        reader.GetString(9),
                    Format =
                        reader.GetString(10),
                    DateFormat =
                        reader.GetString(11),
                },
            Ui =
                new UiSettings
                {
                    Language =
                        reader.GetString(12),
                    AcrylicEnabled =
                        reader.GetInt32(13)
                            != 0,
                    AccentColor =
                        reader.GetString(14),
                    Theme =
                        reader.GetString(15),
                    AutoScrollLongTitles =
                        reader.GetInt32(16)
                            != 0,
                    LongTextMode =
                        reader.IsDBNull(17)
                            ? null
                            : reader.GetString(17),
                    LyricsDisplayMode =
                        reader.GetString(18),
                },
        };
    }



    public static async Task DropLegacySettingsAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return;
        }

        await using SqliteConnection connection =
            new(BuildConnectionString(path));
        await connection.OpenAsync(
            cancellationToken);

        if (!await TableExistsAsync(
                connection,
                "app_settings",
                cancellationToken))
        {
            return;
        }

        await using SqliteCommand command =
            connection.CreateCommand();
        command.CommandText =
            "DROP TABLE app_settings;";
        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
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

        object? value =
            await command.ExecuteScalarAsync(
                cancellationToken);

        return value is not null;
    }

    public static async Task SavePlanAsync(
        string path,
        string sourceDirectory,
        string destinationDirectory,
        LibraryPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        await InitializeAsync(
            path,
            cancellationToken);

        await using SqliteConnection connection =
            new(BuildConnectionString(path));
        await connection.OpenAsync(cancellationToken);

        using SqliteTransaction transaction =
            connection.BeginTransaction();

        try
        {
            await using (SqliteCommand clear =
                connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText =
                    "DELETE FROM tracks;";
                await clear.ExecuteNonQueryAsync(
                    cancellationToken);
            }

            await using SqliteCommand insert =
                connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO tracks (
                    source_path,
                    destination_path,
                    status,
                    title,
                    artist,
                    album_artist,
                    album,
                    has_embedded_art,
                    detail,
                    needs_audio_copy
                ) VALUES (
                    $source,
                    $destination,
                    $status,
                    $title,
                    $artist,
                    $albumArtist,
                    $album,
                    $embeddedArt,
                    $detail,
                    $needsAudioCopy
                );
                """;

            SqliteParameter sourceParameter =
                insert.Parameters.Add(
                    "$source",
                    SqliteType.Text);
            SqliteParameter destinationParameter =
                insert.Parameters.Add(
                    "$destination",
                    SqliteType.Text);
            SqliteParameter statusParameter =
                insert.Parameters.Add(
                    "$status",
                    SqliteType.Integer);
            SqliteParameter titleParameter =
                insert.Parameters.Add(
                    "$title",
                    SqliteType.Text);
            SqliteParameter artistParameter =
                insert.Parameters.Add(
                    "$artist",
                    SqliteType.Text);
            SqliteParameter albumArtistParameter =
                insert.Parameters.Add(
                    "$albumArtist",
                    SqliteType.Text);
            SqliteParameter albumParameter =
                insert.Parameters.Add(
                    "$album",
                    SqliteType.Text);
            SqliteParameter embeddedArtParameter =
                insert.Parameters.Add(
                    "$embeddedArt",
                    SqliteType.Integer);
            SqliteParameter detailParameter =
                insert.Parameters.Add(
                    "$detail",
                    SqliteType.Text);
            SqliteParameter needsAudioCopyParameter =
                insert.Parameters.Add(
                    "$needsAudioCopy",
                    SqliteType.Integer);

            foreach (PlanEntry entry
                in plan.Entries)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                sourceParameter.Value =
                    entry.SourcePath;
                destinationParameter.Value =
                    DbValue(entry.DestinationPath);
                statusParameter.Value =
                    (int)entry.Status;
                titleParameter.Value =
                    DbValue(entry.Metadata?.Title);
                artistParameter.Value =
                    DbValue(entry.Metadata?.Artist);
                albumArtistParameter.Value =
                    DbValue(entry.Metadata?.AlbumArtist);
                albumParameter.Value =
                    DbValue(entry.Metadata?.Album);
                embeddedArtParameter.Value =
                    entry.Metadata?.HasEmbeddedArt
                    == true
                        ? 1
                        : 0;
                detailParameter.Value =
                    DbValue(entry.Detail);
                needsAudioCopyParameter.Value =
                    entry.NeedsAudioCopy ? 1 : 0;

                await insert.ExecuteNonQueryAsync(
                    cancellationToken);
            }

            await SetMetaAsync(
                connection,
                transaction,
                "source_directory",
                Path.GetFullPath(
                    sourceDirectory),
                cancellationToken);
            await SetMetaAsync(
                connection,
                transaction,
                "destination_directory",
                Path.GetFullPath(
                    destinationDirectory),
                cancellationToken);
            await SetMetaAsync(
                connection,
                transaction,
                "captured_at",
                DateTimeOffset.UtcNow
                    .ToString("O"),
                cancellationToken);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public static async Task<LibraryPlan?>
        LoadPlanAsync(
            string path,
            string sourceDirectory,
            string destinationDirectory,
            CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await InitializeAsync(
            path,
            cancellationToken);

        await using SqliteConnection connection =
            new(BuildConnectionString(path));
        await connection.OpenAsync(cancellationToken);

        string? storedSource =
            await GetMetaAsync(
                connection,
                "source_directory",
                cancellationToken);
        string? storedDestination =
            await GetMetaAsync(
                connection,
                "destination_directory",
                cancellationToken);

        if (storedSource is null
            || storedDestination is null
            || !PathEquals(
                storedSource,
                sourceDirectory)
            || !PathEquals(
                storedDestination,
                destinationDirectory))
        {
            return null;
        }

        await using SqliteCommand command =
            connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                source_path,
                destination_path,
                status,
                title,
                artist,
                album_artist,
                album,
                has_embedded_art,
                detail,
                needs_audio_copy
            FROM tracks
            ORDER BY source_path COLLATE NOCASE;
            """;

        List<PlanEntry> entries = [];

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
            cancellationToken))
        {
            string source =
                reader.GetString(0);
            string? destination =
                reader.IsDBNull(1)
                    ? null
                    : reader.GetString(1);

            PlanEntryStatus status =
                (PlanEntryStatus)
                    reader.GetInt32(2);

            string? title =
                ReadNullableString(
                    reader,
                    3);
            string? artist =
                ReadNullableString(
                    reader,
                    4);
            string? albumArtist =
                ReadNullableString(
                    reader,
                    5);
            string? album =
                ReadNullableString(
                    reader,
                    6);

            TrackMetadata metadata = new(
                artist,
                albumArtist,
                album,
                reader.GetInt32(7) != 0,
                title);

            entries.Add(
                new PlanEntry(
                    source,
                    destination,
                    status,
                    metadata,
                    ReadNullableString(
                        reader,
                        8),
                    reader.GetInt32(9) != 0));
        }

        return new LibraryPlan(entries);
    }

    public static long GetStorageSizeBytes(
        string path)
    {
        long total = 0;

        foreach (string candidate
            in new[]
            {
                path,
                path + "-wal",
                path + "-shm",
            })
        {
            try
            {
                FileInfo file = new(candidate);
                if (file.Exists)
                {
                    total += file.Length;
                }
            }
            catch
            {
                // Size reporting is diagnostic UI only.
            }
        }

        return total;
    }

    public static async Task ClearAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        SqliteConnection.ClearAllPools();

        foreach (string candidate
            in new[]
            {
                path,
                path + "-wal",
                path + "-shm",
            })
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(candidate))
            {
                File.Delete(candidate);
            }
        }
    }

    private static async Task SetMetaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command =
            connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO meta(key, value)
            VALUES ($key, $value)
            ON CONFLICT(key)
            DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue(
            "$key",
            key);
        command.Parameters.AddWithValue(
            "$value",
            value);

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private static async Task<string?>
        GetMetaAsync(
            SqliteConnection connection,
            string key,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command =
            connection.CreateCommand();
        command.CommandText =
            "SELECT value FROM meta WHERE key = $key;";
        command.Parameters.AddWithValue(
            "$key",
            key);

        object? value =
            await command.ExecuteScalarAsync(
                cancellationToken);

        return value as string;
    }

    private static string BuildConnectionString(
        string path)
    {
        SqliteConnectionStringBuilder builder =
            new()
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            };

        return builder.ToString();
    }

    private static object DbValue(
        string? value) =>
        value is null
            ? DBNull.Value
            : value;

    private static string? ReadNullableString(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : reader.GetString(ordinal);

    private static bool PathEquals(
        string left,
        string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
