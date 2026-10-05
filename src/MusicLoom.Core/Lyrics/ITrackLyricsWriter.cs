namespace MusicLoom.Core.Lyrics;

public enum LyricsWriteStatus
{
    Written,
    SkippedExisting,
    UnsupportedFormat,
}

public sealed record LyricsWriteResult(
    LyricsWriteStatus Status,
    string? Detail = null);

public interface ITrackLyricsWriter
{
    ValueTask<LyricsWriteResult> WriteAsync(
        string path,
        LrcDocument lyrics,
        bool overrideExisting,
        CancellationToken cancellationToken = default);
}
