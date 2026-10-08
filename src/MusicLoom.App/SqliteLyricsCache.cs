using System.Globalization;
using Microsoft.Data.Sqlite;
using MusicLoom.Core.Lyrics;

namespace MusicLoom.App;

/// <summary>
/// Canonical remote lyrics. This table intentionally survives SaveAsync's
/// full rewrite of sync_tracks and keeps lyrics when the destination is absent.
/// </summary>
internal sealed class SqliteLyricsCache : ITrackLyricsCache
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
            SELECT song_id, lrc, fetched_at
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
                DateTimeStyles.RoundtripKind));
    }

    public async Task SaveAsync(
        string sourcePath,
        long songId,
        LrcDocument? lyrics,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection =
            await OpenAsync(cancellationToken);

        await using SqliteCommand command =
            connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sync_lyrics (
                source_path, song_id, lrc, fetched_at
            ) VALUES (
                $source, $songId, $lrc, $fetched
            )
            ON CONFLICT(source_path) DO UPDATE SET
                song_id = excluded.song_id,
                lrc = excluded.lrc,
                fetched_at = excluded.fetched_at;
            """;

        command.Parameters.AddWithValue("$source", sourcePath);
        command.Parameters.AddWithValue("$songId", songId);
        command.Parameters.AddWithValue(
            "$lrc",
            lyrics is { Lines.Count: > 0 }
                ? lyrics.ToString()
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "$fetched",
            DateTimeOffset.UtcNow.ToString(
                "O",
                CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken);
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
                    fetched_at TEXT NOT NULL
                );
                """;
            await init.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
