using MusicLoom.Core.Lyrics;

namespace MusicLoom.Core.Enrichment;

public enum TrackRecognitionStatus
{
    Matched,
    NoMatch,
}

public sealed record TrackRecognitionResult(
    TrackRecognitionStatus Status,
    long? TrackId = null,
    string? TrackName = null,
    string? Artist = null,
    string? Album = null)
{
    public static TrackRecognitionResult Matched(
        long trackId,
        string? trackName = null,
        string? artist = null,
        string? album = null) =>
        new(
            TrackRecognitionStatus.Matched,
            trackId,
            trackName,
            artist,
            album);

    public static TrackRecognitionResult NoMatch() =>
        new(TrackRecognitionStatus.NoMatch);
}

public interface ITrackRecognizer
{
    Task<TrackRecognitionResult> RecognizeAsync(
        string audioFilePath,
        CancellationToken cancellationToken = default);
}

public enum LyricsLookupStatus
{
    Found,
    NoLyrics,
}

public sealed record LyricsLookupResult(
    LyricsLookupStatus Status,
    LrcDocument? Lyrics = null)
{
    public static LyricsLookupResult Found(LrcDocument lyrics) =>
        new(LyricsLookupStatus.Found, lyrics);

    public static LyricsLookupResult NoLyrics() =>
        new(LyricsLookupStatus.NoLyrics);
}

public interface ITrackLyricsProvider
{
    Task<LyricsLookupResult> GetLyricsAsync(
        long trackId,
        CancellationToken cancellationToken = default);
}
