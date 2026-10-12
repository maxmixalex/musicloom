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

public enum LyricsProbeStatus
{
    Missing,
    Present,
    UnsupportedFormat,
}

public sealed record LyricsProbeResult(
    LyricsProbeStatus Status,
    string? Detail = null);

public interface ITrackLyricsWriter
{
    ValueTask<LyricsProbeResult> ProbeAsync(
        string path,
        CancellationToken cancellationToken = default);

    ValueTask<LyricsWriteResult> WriteAsync(
        string path,
        LrcDocument lyrics,
        bool overrideExisting,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Removes *only* embedded lyrics from generated destination audio when
/// separate sidecar lyrics have been selected. Source audio is never passed.
/// </summary>
public interface IEmbeddedLyricsCleaner
{
    ValueTask<bool> RemoveAsync(
        string destinationPath,
        CancellationToken cancellationToken = default);
}
