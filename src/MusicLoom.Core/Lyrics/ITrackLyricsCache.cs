namespace MusicLoom.Core.Lyrics;

/// <summary>
/// Persisted canonical NetEase lyrics for a source track. A null LRC means
/// the provider was checked and returned no lyrics; it is not a cache miss.
/// The song ID prevents lyrics from one match being reused for another.
/// </summary>
public sealed record CachedTrackLyrics(
    string SourcePath,
    long SongId,
    string? Lrc,
    DateTimeOffset FetchedAt)
{
    public bool HasLyrics => !string.IsNullOrWhiteSpace(Lrc);

    public LrcDocument? ToDocument() =>
        HasLyrics ? LrcDocument.Parse(Lrc) : null;
}

/// <summary>
/// The database is the canonical store for MusicLoom-managed remote lyrics.
/// Audio tags and .lrc files are independently replaceable output replicas.
/// </summary>
public interface ITrackLyricsCache
{
    Task<CachedTrackLyrics?> GetAsync(
        string sourcePath,
        long songId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string sourcePath,
        long songId,
        LrcDocument? lyrics,
        CancellationToken cancellationToken = default);
}
