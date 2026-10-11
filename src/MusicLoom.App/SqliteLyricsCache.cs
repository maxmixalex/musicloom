using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MusicLoom.Core.Lyrics;

namespace MusicLoom.App;

/// <summary>
/// Canonical remote lyrics. This table intentionally survives SaveAsync's
/// full rewrite of sync_tracks and keeps lyrics when the destination is absent.
/// </summary>
internal sealed class SqliteLyricsCache :
    ITrackLyricsCache, ILibraryLyricsCache, IManagedLyricsOutputRegistry
{
    private readonly string _databasePath;

    public SqliteLyricsCache(string databasePath)
    {
        _databasePath = databasePath;
    }

    public async Task<CachedTrackLyrics?> GetAsync(
        string sourcePath,
        long songId,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await OpenAsync(cancellationToken);

        await using SqliteCommand command =
            connection.CreateCommand();
        command.CommandText =
            """
            SELECT song_id, lrc, fetched_at, merge_version
              FROM sync_lyrics
             WHERE source_path = $source
               AND song_id = $songId;
            """;
        command.Parameters.AddWithValue("$source", sourcePath);
        command.Parameters.AddWithValue("$songId", songId);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CachedTrackLyrics(
            sourcePath,
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            DateTimeOffset.Parse(
                reader.GetString(2),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            reader.GetInt32(3));
    }

    public Task SaveAsync(
        string sourcePath,
        long songId,
        LrcDocument? lyrics,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(
            sourcePath, songId, lyrics,
            explicitSelection: false, cancellationToken);

    // Only a deliberate "Use selected" / "Update lyrics" action may
    // override locally indexed lyrics. Auto-matching never calls this.
    public Task SaveExplicitAsync(
        string sourcePath,
        long songId,
        LrcDocument? lyrics,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(
            sourcePath, songId, lyrics,
            explicitSelection: true, cancellationToken);

    private async Task SaveCoreAsync(
        string sourcePath,
        long songId,
        LrcDocument? lyrics,
        bool explicitSelection,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            await OpenAsync(cancellationToken);

        // Both tables describe one logical lyric update. Never publish
        // a new remote cache while leaving the Lyrics tab on an old
        // NetEase song or lyric document.
        using SqliteTransaction transaction =
            connection.BeginTransaction();
        string? document = lyrics is { Lines.Count: > 0 }
            ? lyrics.ToString()
            : null;
        string stamp = DateTimeOffset.UtcNow.ToString(
            "O", CultureInfo.InvariantCulture);

        try
        {
            if (explicitSelection)
            {
                // A manual match replaces the provider cache before the
                // writer can verify an older generated .lrc. Keep the
                // previous canonical provider version's fingerprint so a
                // legacy output lacking TrackState provenance is safe to
                // upgrade *only* when its actual bytes still match.
                await using SqliteCommand previous = connection.CreateCommand();
                previous.Transaction = transaction;
                previous.CommandText =
                    "SELECT lrc FROM sync_lyrics WHERE source_path = $source;";
                previous.Parameters.AddWithValue("$source", sourcePath);
                string? oldText = (await previous.ExecuteScalarAsync(
                    cancellationToken)) as string;
                if (!string.IsNullOrWhiteSpace(oldText)
                    && !string.Equals(oldText, document, StringComparison.Ordinal))
                {
                    string oldFingerprint = Fingerprint(
                        LrcDocument.Parse(oldText).ToString());
                    await using SqliteCommand history = connection.CreateCommand();
                    history.Transaction = transaction;
                    history.CommandText =
                        """
                        INSERT INTO previous_managed_lrc (source_path, fingerprint)
                        VALUES ($source, $fingerprint)
                        ON CONFLICT(source_path) DO UPDATE SET
                            fingerprint = excluded.fingerprint;
                        """;
                    history.Parameters.AddWithValue("$source", sourcePath);
                    history.Parameters.AddWithValue("$fingerprint", oldFingerprint);
                    await history.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await using (SqliteCommand remote = connection.CreateCommand())
            {
                remote.Transaction = transaction;
                remote.CommandText =
                    """
                    INSERT INTO sync_lyrics (
                        source_path, song_id, lrc, fetched_at, merge_version
                    ) VALUES (
                        $source, $songId, $lrc, $fetched, $mergeVersion
                    )
                    ON CONFLICT(source_path) DO UPDATE SET
                        song_id = excluded.song_id,
                        lrc = excluded.lrc,
                        fetched_at = excluded.fetched_at,
                        merge_version = excluded.merge_version;
                    """;
                remote.Parameters.AddWithValue("$source", sourcePath);
                remote.Parameters.AddWithValue("$songId", songId);
                remote.Parameters.AddWithValue(
                    "$lrc", document is null ? DBNull.Value : document);
                remote.Parameters.AddWithValue("$fetched", stamp);
                remote.Parameters.AddWithValue(
                    "$mergeVersion", LrcDocument.MergeAlgorithmVersion);
                await remote.ExecuteNonQueryAsync(cancellationToken);
            }

            // Passive recognition preserves local lyrics. An explicit
            // selection changes provenance to ManualNetease atomically,
            // so later scans know the user's preference is intentional.
            await using (SqliteCommand canonical =
                connection.CreateCommand())
            {
                canonical.Transaction = transaction;
                canonical.CommandText =
                    """
                    INSERT INTO library_lyrics (
                        source_path, origin, lrc, song_id,
                        source_signature, updated_at
                    ) VALUES (
                        $source, $origin, $lrc, $songId,
                        $signature, $updatedAt
                    )
                    ON CONFLICT(source_path) DO UPDATE SET
                        origin = excluded.origin,
                        lrc = excluded.lrc,
                        song_id = excluded.song_id,
                        source_signature = excluded.source_signature,
                        updated_at = excluded.updated_at
                    WHERE $explicit = 1
                       OR library_lyrics.origin NOT IN (
                           $embedded, $sidecar, $manual);
                    """;
                canonical.Parameters.AddWithValue("$source", sourcePath);
                canonical.Parameters.AddWithValue(
                    "$origin", (int)(explicitSelection
                        ? LibraryLyricsOrigin.ManualNetease
                        : LibraryLyricsOrigin.Netease));
                canonical.Parameters.AddWithValue(
                    "$lrc", document is null ? DBNull.Value : document);
                canonical.Parameters.AddWithValue("$songId", songId);
                canonical.Parameters.AddWithValue(
                    "$signature",
                    (explicitSelection ? "manual-provider:" : "provider:")
                    + stamp);
                canonical.Parameters.AddWithValue("$updatedAt", stamp);
                canonical.Parameters.AddWithValue(
                    "$embedded", (int)LibraryLyricsOrigin.Embedded);
                canonical.Parameters.AddWithValue(
                    "$sidecar", (int)LibraryLyricsOrigin.Sidecar);
                canonical.Parameters.AddWithValue(
                    "$manual", (int)LibraryLyricsOrigin.ManualNetease);
                canonical.Parameters.AddWithValue(
                    "$explicit", explicitSelection ? 1 : 0);
                await canonical.ExecuteNonQueryAsync(cancellationToken);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private static string Fingerprint(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();

    public async Task<bool> IsKnownOutputAsync(
        string sourcePath, string destinationAudioPath,
        string normalizedFingerprint,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1 FROM managed_lrc_output
                WHERE source_path = $source AND destination_path = $destination
                  AND fingerprint = $fingerprint
            ) OR EXISTS (
                SELECT 1 FROM previous_managed_lrc
                WHERE source_path = $source AND fingerprint = $fingerprint
            );
            """;
        command.Parameters.AddWithValue("$source", sourcePath);
        command.Parameters.AddWithValue(
            "$destination", Path.GetFullPath(destinationAudioPath));
        command.Parameters.AddWithValue("$fingerprint", normalizedFingerprint);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    public async Task RecordWrittenAsync(
        string sourcePath, string destinationAudioPath,
        string normalizedFingerprint,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO managed_lrc_output (
                source_path, destination_path, fingerprint, recorded_at
            ) VALUES ($source, $destination, $fingerprint, $time)
            ON CONFLICT(source_path) DO UPDATE SET
                destination_path = excluded.destination_path,
                fingerprint = excluded.fingerprint,
                recorded_at = excluded.recorded_at;
            """;
        command.Parameters.AddWithValue("$source", sourcePath);
        command.Parameters.AddWithValue(
            "$destination", Path.GetFullPath(destinationAudioPath));
        command.Parameters.AddWithValue("$fingerprint", normalizedFingerprint);
        command.Parameters.AddWithValue("$time",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LibraryLyricsRecord?> GetLibraryAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT origin, lrc, song_id, source_signature, updated_at
              FROM library_lyrics
             WHERE source_path = $source;
            """;
        command.Parameters.AddWithValue("$source", sourcePath);

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new LibraryLyricsRecord(
            sourcePath,
            (LibraryLyricsOrigin)reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.GetString(3),
            DateTimeOffset.Parse(
                reader.GetString(4),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
    }

    public async Task SaveLibraryAsync(
        LibraryLyricsRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using SqliteConnection connection =
            await OpenAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO library_lyrics (
                source_path, origin, lrc, song_id,
                source_signature, updated_at
            ) VALUES (
                $source, $origin, $lrc, $songId,
                $signature, $updatedAt
            )
            ON CONFLICT(source_path) DO UPDATE SET
                origin = excluded.origin,
                lrc = excluded.lrc,
                song_id = excluded.song_id,
                source_signature = excluded.source_signature,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$source", record.SourcePath);
        command.Parameters.AddWithValue("$origin", (int)record.Origin);
        command.Parameters.AddWithValue(
            "$lrc", record.Text is null ? DBNull.Value : record.Text);
        command.Parameters.AddWithValue(
            "$songId", record.SongId is long id ? id : DBNull.Value);
        command.Parameters.AddWithValue(
            "$signature", record.SourceSignature);
        command.Parameters.AddWithValue(
            "$updatedAt",
            record.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Dictionary<string, LibraryLyricsRecord>>
        LoadLibrarySnapshotAsync(
            CancellationToken cancellationToken = default)
    {
        Dictionary<string, LibraryLyricsRecord> entries =
            new(StringComparer.OrdinalIgnoreCase);

        await using SqliteConnection connection =
            await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source_path, origin, lrc, song_id,
                   source_signature, updated_at
              FROM library_lyrics;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string source = reader.GetString(0);
            entries[source] = new LibraryLyricsRecord(
                source,
                (LibraryLyricsOrigin)reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetString(4),
                DateTimeOffset.Parse(
                    reader.GetString(5),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind));
        }

        return entries;
    }

    public async Task<Dictionary<string, CachedTrackLyrics>>
        LoadRemoteSnapshotAsync(
            CancellationToken cancellationToken = default)
    {
        Dictionary<string, CachedTrackLyrics> entries =
            new(StringComparer.OrdinalIgnoreCase);

        await using SqliteConnection connection =
            await OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source_path, song_id, lrc, fetched_at, merge_version
              FROM sync_lyrics;
            """;

        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string source = reader.GetString(0);
            entries[source] = new CachedTrackLyrics(
                source,
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                DateTimeOffset.Parse(
                    reader.GetString(3),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                reader.GetInt32(4));
        }

        return entries;
    }

    public async Task SaveLibraryBatchAsync(
        IReadOnlyCollection<LibraryLyricsRecord> records,
        CancellationToken cancellationToken = default)
    {
        if (records.Count == 0)
        {
            return;
        }

        await using SqliteConnection connection =
            await OpenAsync(cancellationToken);
        using SqliteTransaction transaction =
            connection.BeginTransaction();

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO library_lyrics (
                source_path, origin, lrc, song_id,
                source_signature, updated_at
            ) VALUES (
                $source, $origin, $lrc, $songId,
                $signature, $updatedAt
            )
            ON CONFLICT(source_path) DO UPDATE SET
                origin = excluded.origin,
                lrc = excluded.lrc,
                song_id = excluded.song_id,
                source_signature = excluded.source_signature,
                updated_at = excluded.updated_at;
            """;

        try
        {
            foreach (LibraryLyricsRecord record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$source", record.SourcePath);
                command.Parameters.AddWithValue("$origin", (int)record.Origin);
                command.Parameters.AddWithValue(
                    "$lrc",
                    record.Text is null ? DBNull.Value : record.Text);
                command.Parameters.AddWithValue(
                    "$songId",
                    record.SongId is long id ? id : DBNull.Value);
                command.Parameters.AddWithValue(
                    "$signature",
                    record.SourceSignature);
                command.Parameters.AddWithValue(
                    "$updatedAt",
                    record.UpdatedAt.ToString(
                        "O",
                        CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private async Task<SqliteConnection> OpenAsync(
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
        };
        SqliteConnection connection =
            new(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);

            await using SqliteCommand init =
                connection.CreateCommand();
            init.CommandText =
                """
                PRAGMA busy_timeout = 5000;
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS sync_lyrics (
                    source_path TEXT PRIMARY KEY COLLATE NOCASE,
                    song_id INTEGER NOT NULL,
                    lrc TEXT NULL,
                    fetched_at TEXT NOT NULL,
                    merge_version INTEGER NOT NULL DEFAULT 1
                );

                -- Only library_lyrics is authoritative for the Lyrics tab.
                -- sync_lyrics stores a NetEase provider candidate.
                CREATE TABLE IF NOT EXISTS library_lyrics (
                    source_path TEXT PRIMARY KEY COLLATE NOCASE,
                    origin INTEGER NOT NULL,
                    lrc TEXT NULL,
                    song_id INTEGER NULL,
                    source_signature TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                -- Durable proof of MusicLoom-written LRC output: unrelated
                -- provider/metadata changes cannot erase its fingerprint.
                CREATE TABLE IF NOT EXISTS managed_lrc_output (
                    source_path TEXT PRIMARY KEY COLLATE NOCASE,
                    destination_path TEXT NOT NULL COLLATE NOCASE,
                    fingerprint TEXT NOT NULL,
                    recorded_at TEXT NOT NULL
                );

                -- Upgrade assistance for older MusicLoom sidecars where
                -- track state lost the last output fingerprint.
                CREATE TABLE IF NOT EXISTS previous_managed_lrc (
                    source_path TEXT PRIMARY KEY COLLATE NOCASE,
                    fingerprint TEXT NOT NULL
                );
                """;
            await init.ExecuteNonQueryAsync(cancellationToken);

            // Installed databases predate the merge-version marker. Keep
            // their cached lyrics and mark them as v1 for lazy re-fetch.
            bool hasMergeVersion = false;
            await using (SqliteCommand inspect = connection.CreateCommand())
            {
                inspect.CommandText = "PRAGMA table_info(sync_lyrics);";
                await using SqliteDataReader reader =
                    await inspect.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (string.Equals(
                        reader.GetString(1),
                        "merge_version",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        hasMergeVersion = true;
                        break;
                    }
                }
            }

            if (!hasMergeVersion)
            {
                await using SqliteCommand migrate =
                    connection.CreateCommand();
                migrate.CommandText =
                    "ALTER TABLE sync_lyrics " +
                    "ADD COLUMN merge_version INTEGER NOT NULL DEFAULT 1;";
                await migrate.ExecuteNonQueryAsync(cancellationToken);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
