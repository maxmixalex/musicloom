using System.Globalization;
using Microsoft.Data.Sqlite;
using MusicLoom.Core.State;

namespace MusicLoom.App;

internal sealed class SqliteStateStore : StateStore
{
    private const int DatabaseSchemaVersion = 2;
    private readonly string? _legacyJsonPath;

    public SqliteStateStore(
        string databasePath,
        string? legacyJsonPath = null)
        : base(databasePath)
    {
        _legacyJsonPath = legacyJsonPath;
    }

    public override async Task<StateDocument> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        RecoveryWarning = null;
        await InitializeAsync(
            cancellationToken);

        if (!await HasStateAsync(
                cancellationToken)
            && !string.IsNullOrWhiteSpace(
                _legacyJsonPath)
            && File.Exists(
                _legacyJsonPath))
        {
            StateStore legacy =
                new(_legacyJsonPath);

            StateDocument imported =
                await legacy.LoadAsync(
                    cancellationToken);

            await SaveAsync(
                imported,
                cancellationToken);

            RecoveryWarning =
                "Migrated legacy JSON synchronization state into the local database.";

            TryDelete(
                _legacyJsonPath);
            TryDelete(
                _legacyJsonPath + ".bak");

            return imported;
        }

        await using SqliteConnection connection =
            new(BuildConnectionString());
        await connection.OpenAsync(
            cancellationToken);

        string recipe =
            await ReadMetaAsync(
                connection,
                "recipe_version",
                cancellationToken)
            ?? SyncRecipe.CurrentVersion;

        StateDocument state =
            new()
            {
                RecipeVersion = recipe,
            };

        await using SqliteCommand command =
            connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                source_path,
                source_size,
                source_last_write_utc_ticks,
                quick_hash,
                recipe_version,
                destination_path,
                destination_size,
                destination_last_write_utc_ticks,
                recognition,
                recognition_checked_at,
                netease_song_id,
                netease_track_name,
                netease_track_artist,
                netease_track_album,
                lyrics,
                lyrics_checked_at,
                lyrics_fingerprint,
                metadata_captured,
                metadata_schema_version,
                title_captured,
                title,
                artist,
                album_artist,
                album,
                has_embedded_art,
                processed_at,
                netease_cover_url,
                netease_match_suppressed,
                netease_match_confirmed
            FROM sync_tracks;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        while (await reader.ReadAsync(
            cancellationToken))
        {
            string sourcePath =
                reader.GetString(0);

            TrackState track =
                new()
                {
                    SourcePath =
                        sourcePath,
                    SourceSize =
                        reader.GetInt64(1),
                    SourceLastWriteUtcTicks =
                        reader.GetInt64(2),
                    QuickHash =
                        ulong.TryParse(
                            reader.GetString(3),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out ulong quickHash)
                            ? quickHash
                            : 0,
                    RecipeVersion =
                        reader.GetString(4),
                    DestinationPath =
                        ReadString(
                            reader,
                            5),
                    DestinationSize =
                        ReadInt64(
                            reader,
                            6),
                    DestinationLastWriteUtcTicks =
                        ReadInt64(
                            reader,
                            7),
                    Recognition =
                        (RecognitionState)
                            reader.GetInt32(8),
                    RecognitionCheckedAt =
                        ReadDateTimeOffset(
                            reader,
                            9),
                    NeteaseSongId =
                        ReadInt64(
                            reader,
                            10),
                    NeteaseTrackName =
                        ReadString(
                            reader,
                            11),
                    NeteaseTrackArtist =
                        ReadString(
                            reader,
                            12),
                    NeteaseTrackAlbum =
                        ReadString(
                            reader,
                            13),
                    Lyrics =
                        (LyricsState)
                            reader.GetInt32(14),
                    LyricsCheckedAt =
                        ReadDateTimeOffset(
                            reader,
                            15),
                    LyricsFingerprint =
                        ReadString(
                            reader,
                            16),
                    MetadataCaptured =
                        reader.GetInt32(17)
                            != 0,
                    MetadataSchemaVersion =
                        reader.GetInt32(18),
                    TitleCaptured =
                        reader.GetInt32(19)
                            != 0,
                    Title =
                        ReadString(
                            reader,
                            20),
                    Artist =
                        ReadString(
                            reader,
                            21),
                    AlbumArtist =
                        ReadString(
                            reader,
                            22),
                    Album =
                        ReadString(
                            reader,
                            23),
                    HasEmbeddedArt =
                        reader.GetInt32(24)
                            != 0,
                    ProcessedAt =
                        ReadDateTimeOffset(
                            reader,
                            25)
                        ?? DateTimeOffset.MinValue,
                    NeteaseCoverUrl =
                        ReadString(
                            reader,
                            26),
                    NeteaseMatchSuppressed =
                        reader.GetInt32(27)
                            != 0,
                    NeteaseMatchConfirmed =
                        reader.GetInt32(28)
                            != 0,
                };

            state.Tracks[sourcePath] =
                track;
        }

        if (!string.IsNullOrWhiteSpace(
                _legacyJsonPath))
        {
            TryDelete(
                _legacyJsonPath);
            TryDelete(
                _legacyJsonPath + ".bak");
        }

        return state;
    }

    public override async Task SaveAsync(
        StateDocument state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(
            state);

        await InitializeAsync(
            cancellationToken);

        await using SqliteConnection connection =
            new(BuildConnectionString());
        await connection.OpenAsync(
            cancellationToken);

        using SqliteTransaction transaction =
            connection.BeginTransaction();

        try
        {
            await using (
                SqliteCommand clear =
                    connection.CreateCommand())
            {
                clear.Transaction =
                    transaction;
                clear.CommandText =
                    "DELETE FROM sync_tracks;";
                await clear.ExecuteNonQueryAsync(
                    cancellationToken);
            }

            await using SqliteCommand insert =
                connection.CreateCommand();
            insert.Transaction =
                transaction;
            insert.CommandText =
                """
                INSERT INTO sync_tracks (
                    source_path,
                    source_size,
                    source_last_write_utc_ticks,
                    quick_hash,
                    recipe_version,
                    destination_path,
                    destination_size,
                    destination_last_write_utc_ticks,
                    recognition,
                    recognition_checked_at,
                    netease_song_id,
                    netease_track_name,
                    netease_track_artist,
                    netease_track_album,
                    lyrics,
                    lyrics_checked_at,
                    lyrics_fingerprint,
                    metadata_captured,
                    metadata_schema_version,
                    title_captured,
                    title,
                    artist,
                    album_artist,
                    album,
                    has_embedded_art,
                    processed_at,
                    netease_cover_url,
                    netease_match_suppressed,
                    netease_match_confirmed
                ) VALUES (
                    $source,
                    $sourceSize,
                    $sourceWrite,
                    $quickHash,
                    $recipe,
                    $destination,
                    $destinationSize,
                    $destinationWrite,
                    $recognition,
                    $recognitionChecked,
                    $neteaseId,
                    $neteaseName,
                    $neteaseArtist,
                    $neteaseAlbum,
                    $lyrics,
                    $lyricsChecked,
                    $lyricsFingerprint,
                    $metadataCaptured,
                    $metadataSchema,
                    $titleCaptured,
                    $title,
                    $artist,
                    $albumArtist,
                    $album,
                    $embeddedArt,
                    $processedAt,
                    $neteaseCoverUrl,
                    $neteaseSuppressed,
                    $neteaseConfirmed
                );
                """;

            foreach (TrackState track
                in state.Tracks.Values)
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                insert.Parameters.Clear();
                insert.Parameters.AddWithValue(
                    "$source",
                    track.SourcePath);
                insert.Parameters.AddWithValue(
                    "$sourceSize",
                    track.SourceSize);
                insert.Parameters.AddWithValue(
                    "$sourceWrite",
                    track.SourceLastWriteUtcTicks);
                insert.Parameters.AddWithValue(
                    "$quickHash",
                    track.QuickHash.ToString(
                        CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue(
                    "$recipe",
                    track.RecipeVersion);
                insert.Parameters.AddWithValue(
                    "$destination",
                    DbValue(
                        track.DestinationPath));
                insert.Parameters.AddWithValue(
                    "$destinationSize",
                    DbValue(
                        track.DestinationSize));
                insert.Parameters.AddWithValue(
                    "$destinationWrite",
                    DbValue(
                        track.DestinationLastWriteUtcTicks));
                insert.Parameters.AddWithValue(
                    "$recognition",
                    (int)track.Recognition);
                insert.Parameters.AddWithValue(
                    "$recognitionChecked",
                    DbValue(
                        track.RecognitionCheckedAt));
                insert.Parameters.AddWithValue(
                    "$neteaseId",
                    DbValue(
                        track.NeteaseSongId));
                insert.Parameters.AddWithValue(
                    "$neteaseName",
                    DbValue(
                        track.NeteaseTrackName));
                insert.Parameters.AddWithValue(
                    "$neteaseArtist",
                    DbValue(
                        track.NeteaseTrackArtist));
                insert.Parameters.AddWithValue(
                    "$neteaseAlbum",
                    DbValue(
                        track.NeteaseTrackAlbum));
                insert.Parameters.AddWithValue(
                    "$lyrics",
                    (int)track.Lyrics);
                insert.Parameters.AddWithValue(
                    "$lyricsChecked",
                    DbValue(
                        track.LyricsCheckedAt));
                insert.Parameters.AddWithValue(
                    "$lyricsFingerprint",
                    DbValue(
                        track.LyricsFingerprint));
                insert.Parameters.AddWithValue(
                    "$metadataCaptured",
                    track.MetadataCaptured
                        ? 1
                        : 0);
                insert.Parameters.AddWithValue(
                    "$metadataSchema",
                    track.MetadataSchemaVersion);
                insert.Parameters.AddWithValue(
                    "$titleCaptured",
                    track.TitleCaptured
                        ? 1
                        : 0);
                insert.Parameters.AddWithValue(
                    "$title",
                    DbValue(
                        track.Title));
                insert.Parameters.AddWithValue(
                    "$artist",
                    DbValue(
                        track.Artist));
                insert.Parameters.AddWithValue(
                    "$albumArtist",
                    DbValue(
                        track.AlbumArtist));
                insert.Parameters.AddWithValue(
                    "$album",
                    DbValue(
                        track.Album));
                insert.Parameters.AddWithValue(
                    "$embeddedArt",
                    track.HasEmbeddedArt
                        ? 1
                        : 0);
                insert.Parameters.AddWithValue(
                    "$processedAt",
                    track.ProcessedAt
                        .ToString(
                            "O",
                            CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue(
                    "$neteaseCoverUrl",
                    DbValue(
                        track.NeteaseCoverUrl));
                insert.Parameters.AddWithValue(
                    "$neteaseSuppressed",
                    track.NeteaseMatchSuppressed
                        ? 1
                        : 0);
                insert.Parameters.AddWithValue(
                    "$neteaseConfirmed",
                    track.NeteaseMatchConfirmed
                        ? 1
                        : 0);

                await insert.ExecuteNonQueryAsync(
                    cancellationToken);
            }

            await WriteMetaAsync(
                connection,
                transaction,
                "state_schema_version",
                DatabaseSchemaVersion.ToString(
                    CultureInfo.InvariantCulture),
                cancellationToken);
            await WriteMetaAsync(
                connection,
                transaction,
                "recipe_version",
                state.RecipeVersion,
                cancellationToken);

            transaction.Commit();
            RecoveryWarning = null;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private async Task InitializeAsync(
        CancellationToken cancellationToken)
    {
        string? directory =
            System.IO.Path
                .GetDirectoryName(
                    Path);

        if (!string.IsNullOrWhiteSpace(
                directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        await using SqliteConnection connection =
            new(BuildConnectionString());
        await connection.OpenAsync(
            cancellationToken);

        await using SqliteCommand command =
            connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS sync_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sync_tracks (
                source_path TEXT PRIMARY KEY,
                source_size INTEGER NOT NULL,
                source_last_write_utc_ticks INTEGER NOT NULL,
                quick_hash TEXT NOT NULL,
                recipe_version TEXT NOT NULL,
                destination_path TEXT NULL,
                destination_size INTEGER NULL,
                destination_last_write_utc_ticks INTEGER NULL,
                recognition INTEGER NOT NULL,
                recognition_checked_at TEXT NULL,
                netease_song_id INTEGER NULL,
                netease_track_name TEXT NULL,
                netease_track_artist TEXT NULL,
                netease_track_album TEXT NULL,
                lyrics INTEGER NOT NULL,
                lyrics_checked_at TEXT NULL,
                lyrics_fingerprint TEXT NULL,
                metadata_captured INTEGER NOT NULL,
                metadata_schema_version INTEGER NOT NULL,
                title_captured INTEGER NOT NULL,
                title TEXT NULL,
                artist TEXT NULL,
                album_artist TEXT NULL,
                album TEXT NULL,
                has_embedded_art INTEGER NOT NULL,
                processed_at TEXT NOT NULL,
                netease_cover_url TEXT NULL,
                netease_match_suppressed INTEGER NOT NULL DEFAULT 0,
                netease_match_confirmed INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_sync_tracks_netease
                ON sync_tracks(netease_song_id);
            """;

        await command.ExecuteNonQueryAsync(
            cancellationToken);

        // CREATE TABLE IF NOT EXISTS does not evolve an existing v1 table.
        // Add v2 columns lazily so installed databases keep their state.
        await EnsureColumnAsync(
            connection,
            "netease_cover_url",
            "TEXT NULL",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "netease_match_suppressed",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);
        await EnsureColumnAsync(
            connection,
            "netease_match_confirmed",
            "INTEGER NOT NULL DEFAULT 0",
            cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string columnName,
        string definition,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand inspect =
            connection.CreateCommand();
        inspect.CommandText =
            "PRAGMA table_info(sync_tracks);";

        await using SqliteDataReader reader =
            await inspect.ExecuteReaderAsync(
                cancellationToken);

        bool exists = false;
        while (await reader.ReadAsync(
            cancellationToken))
        {
            if (string.Equals(
                    reader.GetString(1),
                    columnName,
                    StringComparison.OrdinalIgnoreCase))
            {
                exists = true;
                break;
            }
        }

        await reader.DisposeAsync();

        if (exists)
        {
            return;
        }

        await using SqliteCommand alter =
            connection.CreateCommand();
        alter.CommandText =
            $"ALTER TABLE sync_tracks ADD COLUMN {columnName} {definition};";
        await alter.ExecuteNonQueryAsync(
            cancellationToken);
    }

    private async Task<bool> HasStateAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            new(BuildConnectionString());
        await connection.OpenAsync(
            cancellationToken);

        string? value =
            await ReadMetaAsync(
                connection,
                "state_schema_version",
                cancellationToken);

        return value is not null;
    }

    private string BuildConnectionString()
    {
        SqliteConnectionStringBuilder builder =
            new()
            {
                DataSource = Path,
                Mode =
                    SqliteOpenMode
                        .ReadWriteCreate,
                Cache =
                    SqliteCacheMode.Shared,
            };

        return builder.ToString();
    }

    private static async Task<string?>
        ReadMetaAsync(
            SqliteConnection connection,
            string key,
            CancellationToken cancellationToken)
    {
        await using SqliteCommand command =
            connection.CreateCommand();
        command.CommandText =
            """
            SELECT value
            FROM sync_meta
            WHERE key = $key;
            """;
        command.Parameters.AddWithValue(
            "$key",
            key);

        object? value =
            await command.ExecuteScalarAsync(
                cancellationToken);

        return value as string;
    }

    private static async Task WriteMetaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command =
            connection.CreateCommand();
        command.Transaction =
            transaction;
        command.CommandText =
            """
            INSERT INTO sync_meta(key, value)
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

    private static string? ReadString(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : reader.GetString(
                ordinal);

    private static long? ReadInt64(
        SqliteDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : reader.GetInt64(
                ordinal);

    private static DateTimeOffset?
        ReadDateTimeOffset(
            SqliteDataReader reader,
            int ordinal)
    {
        string? value =
            ReadString(
                reader,
                ordinal);

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset result)
                ? result
                : null;
    }

    private static object DbValue(
        object? value) =>
        value switch
        {
            null => DBNull.Value,
            DateTimeOffset timestamp =>
                timestamp.ToString(
                    "O",
                    CultureInfo.InvariantCulture),
            _ => value,
        };

    private static void TryDelete(
        string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A successful database migration must not be rolled back
            // merely because an obsolete JSON file could not be removed.
        }
    }
}
