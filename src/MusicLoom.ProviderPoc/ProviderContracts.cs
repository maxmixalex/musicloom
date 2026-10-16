using MusicLoom.Core.Lyrics;

namespace MusicLoom.ProviderPoc;

/// <summary>
/// Offline/opt-in experiments only: deliberately NOT referenced by MusicLoom.App.
/// Tracks, identities and lyric payloads remain scoped to their provider.
/// </summary>
public enum ExternalProvider { Migu, AppleMusic, Spotify }

public enum PlaybackDisposition
{
    Unavailable,
    ExternalPlayerRequired,
    AuthorizedPreviewUrl,
    AuthorizedAudioUrl,
    AuthorizedDeviceCommand,
    AuthorizedSdkRequired,
}

public sealed record ProviderCapabilities(
    bool CanSearch, bool CanReadDetails, bool CanReadTimedLyrics,
    bool CanReadTranslations, bool CanReadRomanization,
    bool CanReadWordTiming, PlaybackDisposition Playback);

public sealed record ProviderTrack(
    ExternalProvider Provider,
    string Id,
    string Title,
    string Artist,
    string Album,
    int DurationMilliseconds = 0,
    string? ArtworkUrl = null,
    string? ExternalUrl = null,
    string? ContentId = null,
    string? CopyrightId = null,
    string? SongId = null,
    string? AlbumId = null,
    string? LrcUrl = null,
    string? TrcUrl = null,
    string? MrcUrl = null,
    string? PreviewUrl = null,
    string? StreamUrl = null);

/// <summary>
/// Source documents are preserved byte-for-byte after decoding to text.
/// No keyword-based removal of "歌曲名", "歌手名", credits, members, etc.
/// Projection into a player is independent from this immutable source data.
/// </summary>
public sealed record ProviderLyricBundle(
    LrcDocument Original,
    string RawOriginal,
    LrcDocument? Translation = null,
    string? RawTranslation = null,
    LrcDocument? WordTimed = null,
    string? RawWordTimed = null);

public sealed record ProviderPlayback(
    PlaybackDisposition Disposition, string Explanation, Uri? AuthorizedUri = null);

public interface IExternalMusicProvider
{
    ExternalProvider Provider { get; }
    ProviderCapabilities Capabilities { get; }
    Task<IReadOnlyList<ProviderTrack>> SearchAsync(
        string query, CancellationToken ct = default);
    Task<ProviderTrack?> GetDetailsAsync(
        ProviderTrack track, CancellationToken ct = default);
    Task<ProviderLyricBundle?> GetLyricsAsync(
        ProviderTrack track, CancellationToken ct = default);
    Task<ProviderPlayback> ResolvePlaybackAsync(
        ProviderTrack track, CancellationToken ct = default);
}

public sealed class ProviderAuthorizationException(string message) : Exception(message);
