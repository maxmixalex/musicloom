namespace MusicLoom.Core.Lyrics;

/// <summary>Durable ownership of generated sidecar LRCs, independent
/// of track recipe state and provider-match refreshes.</summary>
public interface IManagedLyricsOutputRegistry
{
    Task<bool> IsKnownOutputAsync(
        string sourcePath, string destinationAudioPath,
        string normalizedFingerprint,
        CancellationToken cancellationToken = default);

    Task RecordWrittenAsync(
        string sourcePath, string destinationAudioPath,
        string normalizedFingerprint,
        CancellationToken cancellationToken = default);
}

public enum SidecarLyricsConflictKind
{
    ReplacementBlocked,
    RemovalBlocked,
}

/// <summary>Typed conflict used for localized user-facing errors.</summary>
public sealed class SidecarLyricsConflictException : IOException
{
    public SidecarLyricsConflictKind Kind { get; }

    public SidecarLyricsConflictException(SidecarLyricsConflictKind kind)
        : base(kind switch
        {
            SidecarLyricsConflictKind.ReplacementBlocked =>
                "Existing sidecar lyrics cannot be replaced automatically because MusicLoom cannot verify ownership or the file was edited.",
            _ =>
                "The destination LRC differs from the last MusicLoom-managed version, so it cannot be removed automatically.",
        })
    {
        Kind = kind;
    }
}
