using System.Net.Http.Headers;
using System.Text.Json;
using MusicLoom.Core.Lyrics;

namespace MusicLoom.ProviderPoc;

/// <summary>
/// Official catalog search/details, community-observed AMP TTML request.
/// Auth strings are supplied by the caller and NEVER persisted by this PoC.
/// Full subscriber playback is MusicKit-only, not a freely downloadable URL.
/// </summary>
public sealed class AppleMusicProviderPoc : IExternalMusicProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string? _developerToken;
    private readonly string? _webBearer;
    private readonly string? _mediaUserToken;
    private readonly string _storefront;

    public ExternalProvider Provider => ExternalProvider.AppleMusic;
    public ProviderCapabilities Capabilities => new(
        true, true, true, false, false, true,
        PlaybackDisposition.AuthorizedSdkRequired);

    public AppleMusicProviderPoc(
        string? developerToken = null, string? webBearer = null,
        string? mediaUserToken = null, string storefront = "us",
        HttpClient? http = null)
    {
        if (storefront.Length != 2
            || !storefront.All(static c => char.IsAsciiLetter(c)))
            throw new ArgumentException("Storefront must be a two-letter country code.", nameof(storefront));
        _storefront = storefront.ToLowerInvariant();
        _developerToken = NormalizeToken(developerToken);
        _webBearer = NormalizeToken(webBearer);
        _mediaUserToken = mediaUserToken?.Trim();
        _ownsHttp = http is null;
        _http = http ?? ProviderProtocol.CreateHttpClient();
    }

    public async Task<IReadOnlyList<ProviderTrack>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        string path = "catalog/" + _storefront + "/search?term="
            + Uri.EscapeDataString(query.Trim()) + "&types=songs&limit=20";
        using HttpRequestMessage request = CatalogRequest(path);
        using JsonDocument json = await ProviderProtocol.JsonAsync(_http, request, ct);
        JsonElement songs = ProviderProtocol.Path(
            json.RootElement, "results", "songs", "data");
        return ProviderProtocol.Array(songs)
            .Select(ParseTrack).Where(static t => t is not null)
            .Select(static t => t!).ToArray();
    }

    public async Task<ProviderTrack?> GetDetailsAsync(
        ProviderTrack track, CancellationToken ct = default)
    {
        ProviderProtocol.RequireSameProvider(track, Provider);
        using HttpRequestMessage request = CatalogRequest(
            "catalog/" + _storefront + "/songs/" + Uri.EscapeDataString(track.Id));
        using JsonDocument json = await ProviderProtocol.JsonAsync(_http, request, ct);
        ProviderTrack? detail = ParseTrack(ProviderProtocol.First(
            ProviderProtocol.Get(json.RootElement, "data")));
        return detail ?? track;
    }

    public async Task<ProviderLyricBundle?> GetLyricsAsync(
        ProviderTrack track, CancellationToken ct = default)
    {
        ProviderProtocol.RequireSameProvider(track, Provider);
        if (string.IsNullOrEmpty(_webBearer) || string.IsNullOrEmpty(_mediaUserToken))
            throw new ProviderAuthorizationException(
                "Apple Music private TTML requires the user's web Bearer and media-user-token.");
        string path = "catalog/" + _storefront + "/songs/"
            + Uri.EscapeDataString(track.Id)
            + "/syllable-lyrics?extend=ttmlLocalizations";
        using HttpRequestMessage request = AmpRequest(path);
        using JsonDocument json = await ProviderProtocol.JsonAsync(_http, request, ct);
        JsonElement attributes = ProviderProtocol.Path(
            ProviderProtocol.First(ProviderProtocol.Get(json.RootElement, "data")),
            "attributes");
        string ttml = ProviderProtocol.Text(attributes, "ttml");
        Dictionary<string, string> locales = [];
        JsonElement localized = ProviderProtocol.Get(attributes, "ttmlLocalizations");
        if (localized.ValueKind == JsonValueKind.String)
            locales["unspecified"] = localized.GetString() ?? "";
        else if (localized.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in localized.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    locales[p.Name] = p.Value.GetString() ?? "";
            }
        }
        if (ttml.Length == 0)
            ttml = locales.Values.FirstOrDefault(static value => value.Length > 0) ?? "";
        if (ttml.Length == 0) return null;
        // Localization variants are evidence, NOT guessed translations/romaji.
        // They are returned as raw TTML keyed by the actual provider locale.
        return new ProviderLyricBundle(AppleTtmlCodec.Parse(ttml), ttml,
            Localizations: locales);
    }

    public Task<ProviderPlayback> ResolvePlaybackAsync(
        ProviderTrack track, CancellationToken ct = default)
    {
        ProviderProtocol.RequireSameProvider(track, Provider);
        ct.ThrowIfCancellationRequested();
        // Provider-declared previews are separate from full MusicKit playback.
        if (Uri.TryCreate(track.PreviewUrl, UriKind.Absolute, out Uri? preview)
            && preview.Scheme == Uri.UriSchemeHttps)
            return Task.FromResult(new ProviderPlayback(
                PlaybackDisposition.AuthorizedPreviewUrl,
                "The Apple Music catalog provided a preview. Full tracks require user-authorized MusicKit.", preview));
        return Task.FromResult(new ProviderPlayback(
            PlaybackDisposition.AuthorizedSdkRequired,
            "A subscriber must authorize MusicKit playback; the REST API does not supply an unrestricted audio stream."));
    }

    public static ProviderTrack? ParseTrack(JsonElement resource)
    {
        string id = ProviderProtocol.Text(resource, "id");
        if (id.Length == 0) return null;
        JsonElement info = ProviderProtocol.Get(resource, "attributes");
        string cover = ProviderProtocol.Text(ProviderProtocol.Get(info, "artwork"), "url");
        cover = cover.Replace("{w}", "600", StringComparison.Ordinal)
            .Replace("{h}", "600", StringComparison.Ordinal);
        string preview = ProviderProtocol.Text(
            ProviderProtocol.First(ProviderProtocol.Get(info, "previews")), "url");
        return new ProviderTrack(ExternalProvider.AppleMusic, id,
            ProviderProtocol.Text(info, "name"),
            ProviderProtocol.Text(info, "artistName"),
            ProviderProtocol.Text(info, "albumName"),
            ProviderProtocol.Number(info, "durationInMillis"),
            cover, ProviderProtocol.Text(info, "url"),
            PreviewUrl: preview);
    }

    private HttpRequestMessage CatalogRequest(string path)
    {
        bool official = !string.IsNullOrEmpty(_developerToken);
        string token = official ? _developerToken!
            : _webBearer ?? throw new ProviderAuthorizationException(
                "Apple catalog access requires a developer token or user-provided web Bearer.");
        string host = official ? "api.music.apple.com" : "amp-api.music.apple.com";
        return MakeRequest(host, path, token,
            official ? null : _mediaUserToken);
    }

    private HttpRequestMessage AmpRequest(string path) =>
        MakeRequest("amp-api.music.apple.com", path, _webBearer!, _mediaUserToken);

    private static HttpRequestMessage MakeRequest(
        string host, string path, string token, string? userToken)
    {
        HttpRequestMessage request = new(HttpMethod.Get,
            "https://" + host + "/v1/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        if (host.StartsWith("amp-api.", StringComparison.Ordinal))
        {
            request.Headers.Referrer = new Uri("https://music.apple.com/");
            request.Headers.TryAddWithoutValidation("Origin", "https://music.apple.com");
            if (!string.IsNullOrWhiteSpace(userToken))
                request.Headers.TryAddWithoutValidation("media-user-token", userToken);
        }
        return request;
    }

    private static string? NormalizeToken(string? token)
    {
        token = token?.Trim();
        return token?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? token[7..].Trim() : token;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
