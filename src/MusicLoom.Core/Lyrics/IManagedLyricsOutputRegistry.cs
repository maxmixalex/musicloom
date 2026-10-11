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
    ModifiedExisting,
    ModifiedRemoval,
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
            SidecarLyricsConflictKind.ModifiedExisting =>
                "The destination LRC has been changed since MusicLoom last wrote it. It cannot be replaced without reviewing those edits.",
            SidecarLyricsConflictKind.ModifiedRemoval =>
                "The destination LRC differs from MusicLoom's last generated version and cannot be deleted automatically.",
            _ =>
                "The destination LRC cannot be verified as MusicLoom output, so it cannot be removed automatically.",
        })
    {
        Kind = kind;
    }
}
