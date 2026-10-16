using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MusicLoom.Core.Lyrics;

namespace MusicLoom.ProviderPoc;

/// <summary>
/// Official OAuth catalog and Spotify Connect control, plus isolated private
/// color-lyrics experiments. No fake MediaPlayer URL or borrowed sessions.
/// </summary>
public sealed class SpotifyProviderPoc : IExternalMusicProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string? _oauth;
    private readonly string? _privateLyricsBearer;
    private readonly string _market;

    public ExternalProvider Provider => ExternalProvider.Spotify;
    public ProviderCapabilities Capabilities => new(
        true, true, true, false, false, false,
        PlaybackDisposition.AuthorizedDeviceCommand);

    public SpotifyProviderPoc(string? oauthToken = null,
        string? privateLyricsBearer = null, string market = "US",
        HttpClient? http = null)
    {
        if (market.Length != 2 || !market.All(static c => char.IsAsciiLetter(c)))
            throw new ArgumentException("Market must be an ISO country code.", nameof(market));
        _market = market.ToUpperInvariant();
        _oauth = Normalize(oauthToken);
        _privateLyricsBearer = Normalize(privateLyricsBearer);
        _ownsHttp = http is null;
        _http = http ?? ProviderProtocol.CreateHttpClient();
    }

    public async Task<IReadOnlyList<ProviderTrack>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        using HttpRequestMessage request = OfficialRequest(HttpMethod.Get,
            "search?q=" + Uri.EscapeDataString(query.Trim())
                + "&type=track&limit=20&market=" + _market);
        using JsonDocument json = await ProviderProtocol.JsonAsync(_http, request, ct);
        return ProviderProtocol.Array(ProviderProtocol.Path(
                json.RootElement, "tracks", "items"))
            .Select(ParseTrack).Where(static x => x is not null)
            .Select(static x => x!).ToArray();
    }

    public async Task<ProviderTrack?> GetDetailsAsync(
        ProviderTrack track, CancellationToken ct = default)
    {
        ProviderProtocol.RequireSameProvider(track, Provider);
        using HttpRequestMessage request = OfficialRequest(HttpMethod.Get,
            "tracks/" + Uri.EscapeDataString(track.Id) + "?market=" + _market);
        using JsonDocument json = await ProviderProtocol.JsonAsync(_http, request, ct);
        ProviderTrack? details = ParseTrack(json.RootElement);
        return details is null ? track : details with
        {
            ArtworkUrl = string.IsNullOrWhiteSpace(details.ArtworkUrl)
                ? track.ArtworkUrl : details.ArtworkUrl,
            ExternalUrl = string.IsNullOrWhiteSpace(details.ExternalUrl)
                ? track.ExternalUrl : details.ExternalUrl,
        };
    }

    public async Task<ProviderLyricBundle?> GetLyricsAsync(
        ProviderTrack track, CancellationToken ct = default)
    {
        ProviderProtocol.RequireSameProvider(track, Provider);
        if (string.IsNullOrWhiteSpace(_privateLyricsBearer))
            throw new ProviderAuthorizationException(
                "Spotify private lyrics require a separate user-supplied web-player session token.");
        string url = "https://spclient.wg.spotify.com/color-lyrics/v2/track/"
            + Uri.EscapeDataString(track.Id) + "?format=json&market=from_token";
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _privateLyricsBearer);
        using JsonDocument json = await ProviderProtocol.JsonAsync(_http, request, ct);
        return ParseColorLyrics(json.RootElement);
    }

    public Task<ProviderPlayback> ResolvePlaybackAsync(
        ProviderTrack track, CancellationToken ct = default)
    {
        ProviderProtocol.RequireSameProvider(track, Provider);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_oauth))
            return Task.FromResult(new ProviderPlayback(
                PlaybackDisposition.ExternalPlayerRequired,
                "Spotify requires user OAuth and a Premium playback device; no direct MP3 stream is exposed."));
        return Task.FromResult(new ProviderPlayback(
            PlaybackDisposition.AuthorizedDeviceCommand,
            "Call PlayOnDeviceAsync with authorized user-modify-playback-state scope and Spotify Premium; 204 only acknowledges the command."));
    }

    /// <summary>
    /// Sends an explicit, user-authorized Spotify Connect command; a 204
    /// response is NOT proof that audible playback started on a device.
    /// </summary>
    public async Task<bool> PlayOnDeviceAsync(
        ProviderTrack track, string? deviceId = null,
        CancellationToken ct = default)
    {
        ProviderProtocol.RequireSameProvider(track, Provider);
        if (track.Id.Length != 22 || !track.Id.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("Invalid Spotify track id.", nameof(track));
        string path = "me/player/play";
        if (!string.IsNullOrWhiteSpace(deviceId))
            path += "?device_id=" + Uri.EscapeDataString(deviceId);
        using HttpRequestMessage request = OfficialRequest(HttpMethod.Put, path);
        request.Content = new StringContent(
            "{\"uris\":[\"spotify:track:" + track.Id + "\"]}",
            Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return response.StatusCode == System.Net.HttpStatusCode.NoContent;
    }

    public static ProviderLyricBundle? ParseColorLyrics(JsonElement root)
    {
        JsonElement lyrics = ProviderProtocol.Get(root, "lyrics");
        string kind = ProviderProtocol.Text(lyrics, "syncType");
        if (kind.Length == 0) return null;
        LrcDocument document = new();
        StringBuilder source = new();
        foreach (JsonElement line in ProviderProtocol.Array(
            ProviderProtocol.Get(lyrics, "lines")))
        {
            string phrase = ProviderProtocol.Text(line, "words");
            string rawMs = ProviderProtocol.Text(line, "startTimeMs");
            source.Append(rawMs).Append('\t').Append(phrase).Append('\n');
            if (kind != "LINE_SYNCED"
                || !long.TryParse(rawMs, out long position)
                || position < 0 || position > TimeSpan.MaxValue.TotalMilliseconds)
                continue;
            // Keep headings, credits, ♪, and blank markers unchanged.
            document.Lines.Add(new LrcLine(TimeSpan.FromMilliseconds(position), phrase));
        }
        return new ProviderLyricBundle(document, source.ToString());
    }

    public static ProviderTrack? ParseTrack(JsonElement item)
    {
        string id = ProviderProtocol.Text(item, "id");
        if (id.Length == 0) return null;
        JsonElement album = ProviderProtocol.Get(item, "album");
        string artist = string.Join(" / ",
            ProviderProtocol.Array(ProviderProtocol.Get(item, "artists"))
            .Select(static a => ProviderProtocol.Text(a, "name"))
            .Where(static a => a.Length > 0));
        return new ProviderTrack(ExternalProvider.Spotify, id,
            ProviderProtocol.Text(item, "name"), artist,
            ProviderProtocol.Text(album, "name"),
            ProviderProtocol.Number(item, "duration_ms"),
            ProviderProtocol.Text(ProviderProtocol.First(
                ProviderProtocol.Get(album, "images")), "url"),
            ProviderProtocol.Text(ProviderProtocol.Get(item, "external_urls"), "spotify"),
            PreviewUrl: ProviderProtocol.Text(item, "preview_url"));
    }

    private HttpRequestMessage OfficialRequest(HttpMethod method, string path)
    {
        if (string.IsNullOrWhiteSpace(_oauth))
            throw new ProviderAuthorizationException(
                "Spotify catalog/playback requires an explicit user OAuth access token.");
        HttpRequestMessage request = new(method,
            "https://api.spotify.com/v1/" + path);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _oauth);
        return request;
    }

    private static string? Normalize(string? token)
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
