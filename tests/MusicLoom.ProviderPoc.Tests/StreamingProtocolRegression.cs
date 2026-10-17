using System.Net;
using System.Text;
using MusicLoom.ProviderPoc;

internal static class StreamingProtocolRegression
{
    public static async Task AppleEnhancedHls()
    {
        int calls = 0;
        using HttpClient http = new(new Handler(req =>
        {
            ++calls;
            Uri url = req.RequestUri!;
            if (url.Host == "amp-api.music.apple.com")
            {
                Must(url.AbsolutePath == "/v1/catalog/us/songs/12345"
                    && url.Query.Contains("extend=extendedAssetUrls")
                    && req.Headers.Authorization?.Parameter == "personal-web-token",
                    "Private Apple full-stream metadata must use song-specific AMP URL and personal Bearer.");
                return Json("""
                    {"data":[{"attributes":{"extendedAssetUrls":{
                      "enhancedHls":"https://aod.itunes.apple.com/itunes-assets/HLS/full-master.m3u8?token=short"
                    }}}]}
                    """);
            }
            if (url.Host == "aod.itunes.apple.com")
            {
                Must(url.AbsolutePath.EndsWith("/full-master.m3u8"),
                    "HLS GET targets only the provider-returned full-song playlist.");
                return Json("""
                    #EXTM3U
                    #EXT-X-VERSION:7
                    #EXT-X-STREAM-INF:BANDWIDTH=256000,CODECS="mp4a.40.2"
                    audio/aac.m3u8
                    #EXT-X-STREAM-INF:BANDWIDTH=1200000,CODECS="alac"
                    audio/alac.m3u8
                    """);
            }
            throw new InvalidOperationException("Unexpected Apple request.");
        }));
        using AppleMusicProviderPoc apple = new(
            webBearer: "personal-web-token", http: http);
        ProviderTrack track = new(ExternalProvider.AppleMusic, "12345",
            "Full song", "Artist", "Album");
        Uri? hls = await apple.ResolveEnhancedHlsAsync(track);
        Must(hls is not null && hls.Host == "aod.itunes.apple.com"
            && hls.AbsolutePath.EndsWith("full-master.m3u8"),
            "Apple enhancedHls is an actual complete-song playlist locator.");
        AppleHlsManifest? playlist = await apple.InspectEnhancedHlsAsync(track);
        Must(playlist is { VariantCount: 2, MediaPlaylistEncrypted: null }
            && playlist.Codecs.Count == 2
            && playlist.Codecs.Contains("alac")
            && playlist.Codecs.Contains("mp4a.40.2")
            && calls == 3,
            "Apple HLS metadata and master manifest codecs must be parsed, without claiming decryption.");
        AppleHlsManifest protectedPlaylist = AppleMusicProviderPoc.ParsePlaylist(
            new Uri("https://aod.itunes.apple.com/hls/segment.m3u8"),
            "#EXTM3U\n#EXT-X-TARGETDURATION:8\n"
            + "#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://protected\"\n"
            + "#EXTINF:8,\nsegment.m4s\n");
        Must(protectedPlaylist.MediaPlaylistEncrypted == true
            && protectedPlaylist.VariantCount == 0,
            "Protected Apple HLS media playlist must be flagged, not treated as a raw MP3.");
        Must(!AppleMusicProviderPoc.IsTrustedAppleMediaUrl(
                "https://aod.itunes.apple.com.attacker.invalid/evil.m3u8", out _)
            && !AppleMusicProviderPoc.IsTrustedAppleMediaUrl(
                "http://aod.itunes.apple.com/evil.m3u8", out _),
            "HLS manifest URL cannot be arbitrary, downgraded or attacker-controlled.");
        using AppleMusicProviderPoc anonymous = new(http: http);
        bool authRequired = false;
        try { await anonymous.ResolveEnhancedHlsAsync(track); }
        catch (ProviderAuthorizationException) { authRequired = true; }
        Must(authRequired, "No borrowed session should silently grant Apple HLS access.");
    }

    public static async Task SpotifyLocalAudio()
    {
        const string id = "4iV5W9uYEdYUVa79Axb7Rh";
        int plays = 0, paused = 0, resumed = 0, seeked = 0;
        using HttpClient http = new(new Handler(req =>
        {
            Uri url = req.RequestUri!;
            Must(url.Host == "127.0.0.1" && url.Port == 3678,
                "Never send playback instructions to an external host.");
            switch (url.AbsolutePath)
            {
                case "/":
                    Must(req.Method == HttpMethod.Get, "Check readiness using GET.");
                    return Json("""{"playback_ready":true}""");
                case "/player/play":
                    plays++;
                    Must(req.Method == HttpMethod.Post
                        && req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()
                            == """{"uri":"spotify:track:4iV5W9uYEdYUVa79Axb7Rh"}""",
                        "Local Spotify player should receive a correct track URI.");
                    return Json("""{"ok":true}""");
                case "/status":
                    return Json("""
                        {"track":{"uri":"spotify:track:4iV5W9uYEdYUVa79Axb7Rh",
                          "position":12345,"duration":185000},
                         "stopped":false,"paused":false,"buffering":false}
                        """);
                case "/player/pause":
                    paused++; return Json("{}");
                case "/player/resume":
                    resumed++; return Json("{}");
                case "/player/seek":
                    seeked++;
                    Must(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult()
                            == """{"position":42000}""",
                        "Seek position must be absolute milliseconds.");
                    return Json("{}");
                default:
                    throw new InvalidOperationException("Unexpected loopback path.");
            }
        }));
        using SpotifyLocalStreamBridge local =
            new("http://127.0.0.1:3678", http);
        Must(await local.IsReadyAsync(), "Spotify local decoded player can report readiness.");
        Must(await local.PlayTrackAsync(id), "Spotify local playback command must be dispatched.");
        SpotifyLocalPlayerStatus? status = await local.GetStatusAsync();
        Must(status is { IsPlaying: true, PositionMilliseconds: 12345,
                          DurationMilliseconds: 185000 }
            && status.TrackUri == "spotify:track:" + id,
            "Spotify sidecar should report actual streaming track identity/position.");
        await local.PauseAsync();
        await local.ResumeAsync();
        await local.SeekAsync(42000);
        Must(plays == 1 && paused == 1 && resumed == 1 && seeked == 1,
            "Local audio player is controllable without using the Spotify official Web API.");

        foreach (string target in new[]
        {
            "http://spotify.com:3678", "http://192.168.1.10:3678",
            "https://127.0.0.1:3678", "http://127.0.0.1:3678/base/",
            "http://127.0.0.1:3678/?token=x"
        })
        {
            bool blocked = false;
            try { using SpotifyLocalStreamBridge ignored = new(target, http); }
            catch (ArgumentException) { blocked = true; }
            Must(blocked, "Non-local or suspicious Spotify control endpoint must be rejected: " + target);
        }
        bool invalid = false;
        try { await local.PlayTrackAsync("bad-id"); }
        catch (ArgumentException) { invalid = true; }
        Must(invalid && plays == 1, "Invalid Spotify track IDs must not cause playback.");

        using HttpClient deniedHttp = new(new Handler(req =>
            req.RequestUri!.AbsolutePath == "/" ? Json("""{"playback_ready":false}""")
                : throw new InvalidOperationException("Not-ready daemon must not receive play.")));
        using SpotifyLocalStreamBridge denied = new(http: deniedHttp);
        Must(!await denied.PlayTrackAsync(id),
            "A sidecar without an authenticated playback session must not claim success.");
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private static void Must(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> next)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(next(request));
        }
    }
}
