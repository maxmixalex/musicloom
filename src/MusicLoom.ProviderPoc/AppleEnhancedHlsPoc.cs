using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MusicLoom.ProviderPoc;

/// <summary>
/// Describes a provider-issued complete-song HLS manifest. A master M3U8
/// is a stream locator, NOT proof that protected audio is decodable by
/// Windows MediaPlayer. Does not fetch segments, licenses or keys.
/// </summary>
public sealed record AppleHlsManifest(
    Uri PlaylistUrl,
    IReadOnlyList<string> Codecs,
    int VariantCount,
    bool? MediaPlaylistEncrypted);

public sealed partial class AppleMusicProviderPoc
{
    private static readonly string[] AppleMediaDomains =
        ["apple.com", "itunes.apple.com", "mzstatic.com"];

    /// <summary>
    /// Community-observed Apple web-player extendedAssetUrls.enhancedHls.
    /// Unlike the free catalog preview this references a full-song playlist.
    /// Authentication uses the caller's own web Bearer/session.
    /// </summary>
    public async Task<Uri?> ResolveEnhancedHlsAsync(
        ProviderTrack track, CancellationToken ct = default)
    {
        ProviderProtocol.RequireSameProvider(track, ExternalProvider.AppleMusic);
        if (string.IsNullOrWhiteSpace(_webBearer))
            throw new ProviderAuthorizationException(
                "Apple private HLS metadata needs an account-owned web Bearer token.");

        string path = "catalog/" + _storefront + "/songs/"
            + Uri.EscapeDataString(track.Id)
            + "?extend=extendedAssetUrls";
        using HttpRequestMessage request = AmpRequest(path);
        using JsonDocument response =
            await ProviderProtocol.JsonAsync(_http, request, ct);
        JsonElement attrs = ProviderProtocol.Path(
            ProviderProtocol.First(
                ProviderProtocol.Get(response.RootElement, "data")),
            "attributes");
        string raw = ProviderProtocol.Text(
            ProviderProtocol.Get(attrs, "extendedAssetUrls"),
            "enhancedHls");

        return IsTrustedAppleMediaUrl(raw, out Uri? uri) ? uri : null;
    }

    /// <summary>
    /// Inspect the HLS master/media playlist without loading protected
    /// audio segments. This is about *stream existence and formats*.
    /// </summary>
    public async Task<AppleHlsManifest?> InspectEnhancedHlsAsync(
        ProviderTrack track, CancellationToken ct = default)
    {
        Uri? uri = await ResolveEnhancedHlsAsync(track, ct);
        if (uri is null) return null;
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        byte[] payload = await ProviderProtocol.ReadBoundedAsync(
            _http, request, 256 * 1024, ct);
        return ParsePlaylist(uri, ProviderProtocol.DecodeText(payload));
    }

    public static bool IsTrustedAppleMediaUrl(string? value, out Uri? result)
    {
        result = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || uri.HostNameType != UriHostNameType.Dns)
            return false;
        if (!AppleMediaDomains.Any(domain =>
            uri.IdnHost.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (!uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            && !uri.AbsolutePath.Contains("/HLS/", StringComparison.OrdinalIgnoreCase))
            return false;
        result = uri;
        return true;
    }

    public static AppleHlsManifest ParsePlaylist(Uri manifestUrl, string payload)
    {
        ArgumentNullException.ThrowIfNull(manifestUrl);
        if (payload.Length > 256 * 1024
            || !payload.TrimStart('\uFEFF', '\r', '\n', ' ', '\t')
                .StartsWith("#EXTM3U", StringComparison.Ordinal))
            throw new InvalidDataException("Apple media response is not an HLS manifest.");

        List<string> codecs = [];
        int variants = 0;
        bool keys = false;
        bool media = false;
        foreach (string line in payload.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                variants++;
                int index = trimmed.IndexOf("CODECS=\"", StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    index += "CODECS=\"".Length;
                    int end = trimmed.IndexOf('"', index);
                    if (end > index)
                    {
                        string value = trimmed[index..end];
                        if (!codecs.Contains(value, StringComparer.Ordinal))
                            codecs.Add(value);
                    }
                }
            }
            if (trimmed.StartsWith("#EXTINF:", StringComparison.Ordinal)
                || trimmed.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
                media = true;
            if (trimmed.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal)
                || trimmed.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal))
                keys = true;
        }
        return new AppleHlsManifest(manifestUrl, codecs, variants,
            media ? keys : null);
    }
}
