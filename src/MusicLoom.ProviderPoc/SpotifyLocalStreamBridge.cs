using System.Net;
using System.Text;
using System.Text.Json;

namespace MusicLoom.ProviderPoc;

/// <summary>
/// HTTP control for an INDEPENDENT local go-librespot player, which itself
/// authenticates, obtains Spotify CDN audio, decodes it, and outputs PCM
/// through Windows WASAPI. No Spotify Web API and no audio URL fabrication.
/// Does not copy community source or package the GPL-licensed sidecar.
/// </summary>
public sealed class SpotifyLocalStreamBridge : IDisposable
{
    private readonly Uri _base;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <param name="serviceBase">Must be HTTP loopback; never send local
    /// daemon commands to a public internet server.</param>
    public SpotifyLocalStreamBridge(
        string serviceBase = "http://127.0.0.1:3678",
        HttpClient? http = null)
    {
        if (!Uri.TryCreate(serviceBase, UriKind.Absolute, out Uri? baseUri)
            || !baseUri.IsLoopback
            || baseUri.Scheme != Uri.UriSchemeHttp
            || baseUri.UserInfo.Length > 0
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment)
            || baseUri.AbsolutePath != "/")
            throw new ArgumentException(
                "Spotify local media service must use a plain HTTP loopback URL.",
                nameof(serviceBase));
        _base = baseUri;
        _ownsHttp = http is null;
        _http = http ?? ProviderProtocol.CreateHttpClient();
    }

    private Uri Route(string segment) => new(_base, segment.TrimStart('/'));

    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, Route("/"));
        using JsonDocument response = await ProviderProtocol.JsonAsync(
            _http, request, ct);
        JsonElement ready = ProviderProtocol.Get(response.RootElement,
            "playback_ready");
        return ready.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    /// go-librespot receives /player/play and DECODES Spotify audio on this
    /// PC. A successful HTTP status is just receipt; follow /status to
    /// verify active playback and track identity.
    /// </summary>
    public async Task<bool> PlayTrackAsync(
        string spotifyTrackId, CancellationToken ct = default)
    {
        ValidateId(spotifyTrackId);
        if (!await IsReadyAsync(ct)) return false;
        string body = "{\"uri\":\"spotify:track:" + spotifyTrackId + "\"}";
        using HttpRequestMessage request = new(HttpMethod.Post,
            Route("/player/play"))
        {
            Content = new StringContent(body, Encoding.UTF8,
                "application/json"),
        };
        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return response.IsSuccessStatusCode;
    }

    public async Task<SpotifyLocalPlayerStatus?> GetStatusAsync(
        CancellationToken ct = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get,
            Route("/status"));
        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        byte[] payload = await response.Content.ReadAsByteArrayAsync(ct);
        if (payload.Length > 128 * 1024)
            throw new InvalidDataException("Spotify player status is oversized.");
        using JsonDocument json = JsonDocument.Parse(payload);
        JsonElement root = json.RootElement;
        JsonElement track = ProviderProtocol.Get(root, "track");
        return new SpotifyLocalPlayerStatus(
            ProviderProtocol.Text(track, "uri"),
            ProviderProtocol.Get(root, "stopped").ValueKind == JsonValueKind.True,
            ProviderProtocol.Get(root, "paused").ValueKind == JsonValueKind.True,
            ProviderProtocol.Get(root, "buffering").ValueKind == JsonValueKind.True,
            ProviderProtocol.Number(track, "position"),
            ProviderProtocol.Number(track, "duration"));
    }

    public async Task PauseAsync(CancellationToken ct = default) =>
        await SendCommandAsync("/player/pause", null, ct);

    public async Task ResumeAsync(CancellationToken ct = default) =>
        await SendCommandAsync("/player/resume", null, ct);

    public async Task SeekAsync(int absoluteMilliseconds,
        CancellationToken ct = default)
    {
        if (absoluteMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(absoluteMilliseconds));
        await SendCommandAsync("/player/seek",
            "{\"position\":" + absoluteMilliseconds + "}", ct);
    }

    private async Task SendCommandAsync(
        string route, string? json, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, Route(route));
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8,
                "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private static void ValidateId(string id)
    {
        if (id.Length != 22 || !id.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException(
                "A Spotify track ID has exactly 22 base62 characters.",
                nameof(id));
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

public sealed record SpotifyLocalPlayerStatus(
    string TrackUri, bool Stopped, bool Paused, bool Buffering,
    int PositionMilliseconds, int DurationMilliseconds = 0)
{
    public bool IsPlaying => !Stopped && !Paused && !Buffering;
}
