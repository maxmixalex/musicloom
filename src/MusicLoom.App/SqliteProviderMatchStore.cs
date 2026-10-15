using System.Globalization;
using Microsoft.Data.Sqlite;
using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

/// <summary>
/// A matching decision is a provider-specific record, never an alias for
/// the currently selected local song or the canonical lyric document.
/// Each provider has independent candidate metadata and an explicit No Match.
/// </summary>
internal sealed record StoredProviderMatch(
    string SourcePath,
    CloudLyricsSource Provider,
    CloudLyricsCandidate? Candidate,
    bool Suppressed,
    bool Confirmed)
{
    public bool IsMatched => !Suppressed && Candidate is not null;
}

/// <summary>
/// Durable provider match data lives in the same musicloom.db as canonical
/// lyrics, but in a separate table. A suppressed entry survives rescans
/// and prevents metadata auto-matching from resurrecting a cleared match.
/// </summary>
internal sealed class SqliteProviderMatchStore(string databasePath)
{
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        string? directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
        };
        SqliteConnection connection = new(builder.ToString());
        try
        {
            await connection.OpenAsync(ct);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA busy_timeout = 5000;
                CREATE TABLE IF NOT EXISTS provider_matches (
                    source_path TEXT NOT NULL COLLATE NOCASE,
                    provider INTEGER NOT NULL,
                    suppressed INTEGER NOT NULL DEFAULT 0,
                    confirmed INTEGER NOT NULL DEFAULT 0,
                    track_id TEXT,
                    song_mid TEXT,
                    media_mid TEXT,
                    file_hash TEXT,
                    album_id TEXT,
                    title TEXT,
                    artist TEXT,
                    album TEXT,
                    duration_ms INTEGER,
                    cover_url TEXT,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY (source_path, provider)
                );
                """;
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    // One query for an entire library facet refresh; never SELECT per track.
    public async Task<Dictionary<string, Dictionary<CloudLyricsSource, StoredProviderMatch>>>
        LoadSnapshotAsync(CancellationToken ct = default)
    {
        Dictionary<string, Dictionary<CloudLyricsSource, StoredProviderMatch>> snapshot =
            new(StringComparer.OrdinalIgnoreCase);
        await using SqliteConnection db = await OpenAsync(ct);
        await using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT source_path, provider, suppressed, confirmed,
                   track_id, song_mid, media_mid, file_hash, album_id,
                   title, artist, album, duration_ms, cover_url
              FROM provider_matches;
            """;
        await using SqliteDataReader rows = await cmd.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
        {
            string path = rows.GetString(0);
            CloudLyricsSource provider = (CloudLyricsSource)rows.GetInt32(1);
            bool suppressed = rows.GetInt32(2) != 0;
            bool confirmed = rows.GetInt32(3) != 0;
            string? Get(int index) => rows.IsDBNull(index) ? null : rows.GetString(index);
            string? id = Get(4);
            CloudLyricsCandidate? candidate =
                suppressed || string.IsNullOrWhiteSpace(id) ? null
                : new CloudLyricsCandidate(provider, id,
                    Get(9) ?? "", Get(10) ?? "", Get(11) ?? "",
                    rows.IsDBNull(12) ? 0 : rows.GetInt32(12),
                    Hash: Get(7), SongMid: Get(5),
                    CoverUrl: Get(13), AlbumId: Get(8), MediaMid: Get(6));
            if (!snapshot.TryGetValue(path, out var providerRows))
            {
                providerRows = [];
                snapshot.Add(path, providerRows);
            }
            providerRows[provider] = new StoredProviderMatch(
                path, provider, candidate, suppressed, confirmed);
        }
        return snapshot;
    }

    public async Task<StoredProviderMatch?> GetAsync(
        string sourcePath, CloudLyricsSource provider,
        CancellationToken ct = default)
    {
        await using SqliteConnection db = await OpenAsync(ct);
        await using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT suppressed, confirmed, track_id, song_mid, media_mid,
                   file_hash, album_id, title, artist, album,
                   duration_ms, cover_url
              FROM provider_matches
             WHERE source_path = $source AND provider = $provider;
            """;
        cmd.Parameters.AddWithValue("$source", sourcePath);
        cmd.Parameters.AddWithValue("$provider", (int)provider);
        await using SqliteDataReader reader =
            await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        bool suppressed = reader.GetInt32(0) != 0;
        bool confirmed = reader.GetInt32(1) != 0;
        string? Field(int index) =>
            reader.IsDBNull(index) ? null : reader.GetString(index);
        string? id = Field(2);
        CloudLyricsCandidate? candidate =
            suppressed || string.IsNullOrWhiteSpace(id)
                ? null
                : new CloudLyricsCandidate(
                    provider, id, Field(7) ?? "", Field(8) ?? "",
                    Field(9) ?? "", reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                    Hash: Field(5), SongMid: Field(3),
                    CoverUrl: Field(11), AlbumId: Field(6),
                    MediaMid: Field(4));
        return new StoredProviderMatch(
            sourcePath, provider, candidate, suppressed, confirmed);
    }

    public async Task SaveMatchAsync(
        string sourcePath, CloudLyricsCandidate candidate, bool confirmed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (string.IsNullOrWhiteSpace(sourcePath)
            || string.IsNullOrWhiteSpace(candidate.Id))
            throw new ArgumentException("Match source and provider ID are required.");

        await using SqliteConnection db = await OpenAsync(ct);
        await using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO provider_matches (
              source_path, provider, suppressed, confirmed, track_id,
              song_mid, media_mid, file_hash, album_id, title, artist,
              album, duration_ms, cover_url, updated_at
            ) VALUES (
              $source, $provider, 0, $confirmed, $id,
              $mid, $media, $hash, $albumId, $title, $artist,
              $album, $duration, $cover, $updated
            ) ON CONFLICT(source_path, provider) DO UPDATE SET
              suppressed=0, confirmed=excluded.confirmed,
              track_id=excluded.track_id, song_mid=excluded.song_mid,
              media_mid=excluded.media_mid, file_hash=excluded.file_hash,
              album_id=excluded.album_id, title=excluded.title,
              artist=excluded.artist, album=excluded.album,
              duration_ms=excluded.duration_ms,
              cover_url=excluded.cover_url, updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$source", sourcePath);
        cmd.Parameters.AddWithValue("$provider", (int)candidate.Source);
        cmd.Parameters.AddWithValue("$confirmed", confirmed ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", candidate.Id);
        static object Str(string? value) => value is null ? DBNull.Value : value;
        cmd.Parameters.AddWithValue("$mid", Str(candidate.SongMid));
        cmd.Parameters.AddWithValue("$media", Str(candidate.MediaMid));
        cmd.Parameters.AddWithValue("$hash", Str(candidate.Hash));
        cmd.Parameters.AddWithValue("$albumId", Str(candidate.AlbumId));
        cmd.Parameters.AddWithValue("$title", candidate.Title);
        cmd.Parameters.AddWithValue("$artist", candidate.Artist);
        cmd.Parameters.AddWithValue("$album", candidate.Album);
        cmd.Parameters.AddWithValue("$duration", candidate.DurationMilliseconds);
        cmd.Parameters.AddWithValue("$cover", Str(candidate.CoverUrl));
        cmd.Parameters.AddWithValue("$updated",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SuppressAsync(
        string sourcePath, CloudLyricsSource provider,
        CancellationToken ct = default)
    {
        await using SqliteConnection db = await OpenAsync(ct);
        await using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO provider_matches (
                source_path, provider, suppressed, confirmed, updated_at
            ) VALUES ($source, $provider, 1, 0, $updated)
            ON CONFLICT(source_path, provider) DO UPDATE SET
                suppressed=1, confirmed=0, track_id=NULL,
                song_mid=NULL, media_mid=NULL, file_hash=NULL,
                album_id=NULL, title=NULL, artist=NULL, album=NULL,
                duration_ms=NULL, cover_url=NULL,
                updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$source", sourcePath);
        cmd.Parameters.AddWithValue("$provider", (int)provider);
        cmd.Parameters.AddWithValue("$updated",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ConfirmAsync(
        string sourcePath, CloudLyricsSource provider,
        CancellationToken ct = default)
    {
        await using SqliteConnection db = await OpenAsync(ct);
        await using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = """
            UPDATE provider_matches SET confirmed=1, updated_at=$updated
             WHERE source_path=$source AND provider=$provider
               AND suppressed=0 AND track_id IS NOT NULL;
            """;
        cmd.Parameters.AddWithValue("$source", sourcePath);
        cmd.Parameters.AddWithValue("$provider", (int)provider);
        cmd.Parameters.AddWithValue("$updated",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
