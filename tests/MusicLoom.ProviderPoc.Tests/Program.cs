using System.Net;
using System.Xml;
using System.Text;
using MusicLoom.Core.Lyrics;
using MusicLoom.ProviderPoc;

// Public, independently generated MRC vector from Lyrimuse's own regression
// fixture (synthetic test text, NOT a commercial lyric).
const string Cipher = "31c1161be5ad108cd8f1bfa976753ccfdfd4aa7b97188cbd5a7322a000333da1450b36bc4312671a92eb81bf0e4f65e76c1e794348ec171093d0f8ead4e5a833ce520af3e26961d0b65482da18f5da530f612f25ff1508f19d4135f0123b1350f2188d0f3fed36648591dca314cba5e18e0c6c01228bacd1540e5cfa30badac6486bdb0cd5592b7cc792a3ce085eb6312dc371b310204eb1b34f63ca16677911676190ae50844901700370217fb91abb0bfa5f3c783c43fc2e50f562fb655c06eea2b6be832ba9b6dfed68e89eff328d7ac23e5864b743b2468b43e49293dd60ea36e5aebae83985e8a05e6ed7161c00";
const string Plain = "[ti:测试]\n[0,0]测(0,0)试(0,0)\n[1000,900]你(1000,300)好(1300,300)世(1600,150)界(1750,150)\n[2500,600]Hel(2500,300)lo(2800,300)\n";

int passed = 0;
foreach ((string name, Func<Task> check) in new (string, Func<Task>)[]
{
    ("Migu MRC synthetic ciphertext decrypts independently", MiguCipher),
    ("Migu lyric metadata, credits and member lines remain untouched", MiguPreservesSource),
    ("Migu search, distinct identities, detail, lyrics and authorized audio", MiguFullFlow),
    ("Migu rejects untrusted hosts and never substitutes another provider", MiguSecurity),
    ("Apple TTML retains word times, Japanese text and credit lines", AppleTtml),
    ("Apple search/detail/private TTML and preview require scoped authorization", AppleFullFlow),
    ("Spotify catalog, color-lyrics and explicit Premium device command", SpotifyFullFlow),
    ("Spotify authorization and lack of direct full-audio URL are explicit", SpotifySecurity),
})
{
    try
    {
        await check();
        Console.WriteLine("PASS " + name);
        passed++;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("FAIL " + name + " -- " + error);
    }
}
Console.WriteLine($"{passed}/8 PoC checks passed");
return passed == 8 ? 0 : 1;

static void Check(bool value, string why)
{
    if (!value) throw new InvalidOperationException(why);
}

static Task MiguCipher()
{
    string decrypted = MiguMrcCodec.Decrypt(Cipher);
    Check(decrypted == Plain, "Migu signed-64 MRC cipher vector must reproduce UTF16LE original");
    LrcDocument doc = MiguMrcCodec.Parse(decrypted);
    Check(doc.Lines.Count == 3 && doc.Lines[0].Text == "测试"
        && doc.Lines[0].Timestamp == TimeSpan.Zero
        && doc.Lines[1].Text == "你好世界" && doc.Lines[1].Words?.Count == 4
        && doc.Lines[1].Words![2].Start == TimeSpan.FromMilliseconds(1600)
        && doc.Lines[2].Text == "Hello",
        "Migu word starts are ABSOLUTE milliseconds and first title line survives");
    Check(doc.Metadata["ti"] == "测试",
        "Migu TT metadata must survive as metadata");
    bool bad = false;
    try { MiguMrcCodec.Decrypt("not-hex"); }
    catch (InvalidDataException) { bad = true; }
    Check(bad, "Malformed MRC must not decrypt");
    return Task.CompletedTask;
}

static Task MiguPreservesSource()
{
    const string raw = "[00:01.00]歌曲名 星光\n"
        + "[00:02.00]歌手名 某歌手\n"
        + "[00:03.00]乐队成员：鼓手某某\n"
        + "[00:04.00]作词：某人\n"
        + "[00:05.00]作曲：某人\n"
        + "[00:06.00]不要抹掉我\n";
    LrcDocument doc = LrcDocument.Parse(raw);
    Check(doc.Lines.Count == 6 && doc.Lines[0].Text == "歌曲名 星光"
        && doc.Lines[1].Text == "歌手名 某歌手"
        && doc.Lines[2].Text == "乐队成员：鼓手某某"
        && doc.Lines[3].Text == "作词：某人"
        && doc.ToStandardLrcString().Contains("歌曲名 星光"),
        "Do NOT apply heuristic deletion of title/artist/band member/credits");
    LrcDocument mrc = MiguMrcCodec.Parse("[0,0]歌曲名(0,0)星光(0,0)\n"
        + "[100,500]乐队成员(100,250)某某(350,250)\n");
    Check(mrc.Lines.Count == 2
        && mrc.Lines[0].Text == "歌曲名星光"
        && mrc.Lines[1].Text == "乐队成员某某",
        "Even [0,0] MRC metadata-style lyric rows MUST be retained");
    return Task.CompletedTask;
}

static async Task MiguFullFlow()
{
    using FakeHttp handler = new(req =>
    {
        Uri u = req.RequestUri!;
        if (u.Host == "pd.musicapp.migu.cn") return Ok("""
            {"code":"000000","songResultData":{"result":[{
              "name":"星光","contentId":"600929000001520013",
              "copyrightId":"copyright-88","songId":"song-9",
              "singers":[{"name":"歌手甲"},{"name":"歌手乙"}],
              "albums":[{"name":"发行专辑","id":"album-8"}],
              "lyricUrl":"https://d.musicapp.migu.cn/o.lrc",
              "trcUrl":"https://d.musicapp.migu.cn/t.trc",
              "mrcurl":"https://d.musicapp.migu.cn/w.mrc",
              "imgItems":[{"img":"https://d.musicapp.migu.cn/cover.jpg","imgSizeType":"03"}]
            }]}}
            """);
        if (u.AbsolutePath.Contains("resourceinfo.do", StringComparison.Ordinal))
        {
            Check(u.Query.Contains("resourceId=600929000001520013", StringComparison.Ordinal)
                && !u.Query.Contains("copyrightId=", StringComparison.Ordinal),
                "contentId must be passed ONLY as resourceId");
            return Ok("""
                {"code":"000000","resource":[{
                  "contentId":"600929000001520013","name":"星光",
                  "lrcUrl":"https://d.musicapp.migu.cn/o.lrc",
                  "trcUrl":"https://d.musicapp.migu.cn/t.trc",
                  "mrcUrl":"https://d.musicapp.migu.cn/w.mrc",
                  "listenUrl":"https://d.musicapp.migu.cn/listen.mp3"
                }]}
                """);
        }
        if (u.AbsolutePath == "/o.lrc")
            return Ok("[00:01.000]歌曲名 星光\n[00:02.000]歌手名 某某\n[00:03.000]乐队成员：鼓手某某\n");
        if (u.AbsolutePath == "/t.trc")
            return Ok("[00:01.000]星光译名\n[00:02.000]中文译文\n");
        if (u.AbsolutePath == "/w.mrc") return Ok(Cipher);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });
    using HttpClient http = new(handler);
    using MiguProviderPoc poc = new(http);
    var hits = await poc.SearchAsync("星光");
    Check(hits.Count == 1 && hits[0].ContentId == "600929000001520013"
        && hits[0].CopyrightId == "copyright-88"
        && hits[0].SongId == "song-9"
        && hits[0].Artist == "歌手甲/歌手乙"
        && hits[0].ArtworkUrl!.EndsWith("cover.jpg"),
        "Migu search needs independent IDs, artists, artwork and lyrics URLs");
    var info = await poc.GetDetailsAsync(hits[0]);
    Check(info!.StreamUrl == "https://d.musicapp.migu.cn/listen.mp3",
        "Provider details should retain an explicitly granted stream URL");
    var lyrics = await poc.GetLyricsAsync(info);
    Check(lyrics is not null && lyrics.Original.Lines.Count == 3
        && lyrics.RawOriginal.Contains("歌曲名 星光")
        && lyrics.RawOriginal.Contains("乐队成员")
        && lyrics.Translation?.Lines.Count == 2
        && lyrics.WordTimed?.Lines.Count == 3
        && lyrics.WordTimed!.Lines[1].Words?.Count == 4,
        "Migu original/translation/word-timing channels must coexist without filtering");
    var playback = await poc.ResolvePlaybackAsync(hits[0]);
    Check(playback.Disposition == PlaybackDisposition.AuthorizedAudioUrl
        && playback.AuthorizedUri?.Host == "d.musicapp.migu.cn",
        "Actual provider-granted HTTPS stream must be returned as such");
}

static async Task MiguSecurity()
{
    Check(!Uri.TryCreate("this-is-not-a-url", UriKind.Absolute, out _),
        "Fixture sanity");
    var malicious = new ProviderTrack(ExternalProvider.Migu, "123", "t", "a", "b",
        LrcUrl: "https://d.musicapp.migu.cn.evil.invalid/song.lrc");
    using MiguProviderPoc poc = new(new HttpClient(new FakeHttp(_ => Ok("[]"))));
    bool blocked = false;
    try { await poc.GetLyricsAsync(malicious); }
    catch (InvalidDataException) { blocked = true; }
    Check(blocked, "Host suffix must not accept evil domain lookalikes");
    var stream = await poc.ResolvePlaybackAsync(malicious);
    Check(stream.Disposition == PlaybackDisposition.Unavailable,
        "No stream URL is not an anonymous listening grant");
    bool wrongProvider = false;
    try
    {
        await poc.GetDetailsAsync(new ProviderTrack(
            ExternalProvider.AppleMusic, "1", "X", "A", "B"));
    }
    catch (ArgumentException) { wrongProvider = true; }
    Check(wrongProvider, "A track from another service must be rejected");
}

static Task AppleTtml()
{
    const string ttml = """
        <tt xmlns="http://www.w3.org/ns/ttml">
          <body><div>
            <p begin="0.25s" end="1.00s"><span begin="0.25s" end="0.55s">君</span><span begin="0.55s" end="1.00s">が</span></p>
            <p begin="00:01.000" end="00:02.000">楽曲名：歌声</p>
            <p begin="00:02.000" end="00:03.000">歌手名：劇中の人物</p>
            <p begin="00:03.000" end="00:04.000">乐队成员：某人</p>
          </div></body>
        </tt>
        """;
    LrcDocument doc = AppleTtmlCodec.Parse(ttml);
    Check(doc.Lines.Count == 4
        && doc.Lines[0].Text == "君が"
        && doc.Lines[0].Words?.Count == 2
        && doc.Lines[0].Words![0].End == TimeSpan.FromMilliseconds(550)
        && doc.Lines[1].Text == "楽曲名：歌声"
        && doc.Lines[2].Text == "歌手名：劇中の人物"
        && doc.Lines[3].Text == "乐队成员：某人",
        "Apple TTML must retain original lyrics and real syllable clocks");
    bool xxERejected = false;
    try { AppleTtmlCodec.Parse("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><tt>&e;</tt>"); }
    catch (XmlException) { xxERejected = true; }
    Check(xxERejected, "Untrusted TTML DTD must be disabled");
    Check(AppleTtmlCodec.TryParseClock("1:02.500", out TimeSpan clock)
        && clock == TimeSpan.FromMilliseconds(62500),
        "TTML minute:second clocks must be supported");
    return Task.CompletedTask;
}

static async Task AppleFullFlow()
{
    int privateRequests = 0;
    using FakeHttp handler = new(req =>
    {
        Uri u = req.RequestUri!;
        Check(req.Headers.Authorization is { Scheme: "Bearer" },
            "Apple token must use Authorization header");
        if (u.Host == "api.music.apple.com")
        {
            Check(req.Headers.Authorization!.Parameter == "dev-token",
                "Catalog uses official developer token");
            if (u.AbsolutePath.EndsWith("/search"))
                return Ok("""
                    {"results":{"songs":{"data":[{
                      "id":"12345","attributes":{"name":"Song","artistName":"Artist",
                      "albumName":"Album","durationInMillis":123000,
                      "artwork":{"url":"https://is1-ssl.mzstatic.com/image/{w}x{h}.jpg"},
                      "previews":[{"url":"https://audio-ssl.itunes.apple.com/a.m4a"}]}}]}}}
                    """);
            return Ok("""
                {"data":[{"id":"12345","attributes":{"name":"Song",
                  "artistName":"Artist","albumName":"Album"}}]}
                """);
        }
        if (u.Host == "amp-api.music.apple.com")
        {
            privateRequests++;
            Check(req.Headers.Authorization!.Parameter == "web-token"
                && req.Headers.Contains("media-user-token"),
                "Private Apple lyric request requires user's web bearer and subscriber token");
            return Ok("""
                {"data":[{"attributes":{"ttml":
                  "<tt><body><div><p begin=\"1s\" end=\"2s\">歌手名：歌曲的一句</p></div></body></tt>",
                  "ttmlLocalizations":{"ja-JP":"<tt><body><div><p begin=\"1s\">歌手名：歌詞</p></div></body></tt>"}}}]}
                """);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });
    using HttpClient http = new(handler);
    using AppleMusicProviderPoc poc = new(
        developerToken:"dev-token", webBearer:"web-token",
        mediaUserToken:"media-user-token", http:http);
    var result = await poc.SearchAsync("Song");
    Check(result.Count == 1 && result[0].Id == "12345"
        && result[0].PreviewUrl!.StartsWith("https://audio-ssl.itunes.apple.com")
        && result[0].ArtworkUrl!.Contains("600x600"),
        "Apple official catalog search must keep real artwork and preview URL");
    var details = await poc.GetDetailsAsync(result[0]);
    Check(details?.Title == "Song"
        && details.PreviewUrl == result[0].PreviewUrl
        && details.ArtworkUrl == result[0].ArtworkUrl,
        "Apple catalog details must retain previously known artwork and preview fields");
    var lyrics = await poc.GetLyricsAsync(result[0]);
    Check(lyrics!.Original.Lines[0].Text == "歌手名：歌曲的一句"
        && lyrics.Localizations!.ContainsKey("ja-JP") && privateRequests == 1,
        "Apple private TTML and localization preserve all source content");
    var play = await poc.ResolvePlaybackAsync(result[0]);
    Check(play.Disposition == PlaybackDisposition.AuthorizedPreviewUrl
        && play.AuthorizedUri!.Host == "audio-ssl.itunes.apple.com",
        "Apple preview must not be called full MusicKit playback");
}

static async Task SpotifyFullFlow()
{
    const string Id = "4iV5W9uYEdYUVa79Axb7Rh";
    int controlCount = 0;
    using FakeHttp handler = new(req =>
    {
        Uri u = req.RequestUri!;
        if (u.Host == "spclient.wg.spotify.com")
        {
            Check(req.Headers.Authorization?.Parameter == "lyrics-token",
                "Private Spotify color lyrics token must be separate");
            return Ok("""
                {"lyrics":{"syncType":"LINE_SYNCED","provider":"MusixMatch",
                  "lines":[{"startTimeMs":"1000","words":"歌曲名：真歌词","syllables":[]},
                           {"startTimeMs":"1250","words":"乐队成员：某人","syllables":[]},
                           {"startTimeMs":"2000","words":"♪","syllables":[]}]}}
                """);
        }
        Check(req.Headers.Authorization?.Parameter == "oauth-token",
            "Spotify catalog or device control must use user OAuth");
        if (u.AbsolutePath == "/v1/search")
            return Ok("""
                {"tracks":{"items":[{"id":"4iV5W9uYEdYUVa79Axb7Rh",
                  "name":"Song","duration_ms":185000,
                  "artists":[{"name":"Artist"}],
                  "album":{"name":"Album","images":[{"url":"https://i.scdn.co/image/cover"}]},
                  "external_urls":{"spotify":"https://open.spotify.com/track/4iV5W9uYEdYUVa79Axb7Rh"}}]}}
                """);
        if (u.AbsolutePath.StartsWith("/v1/tracks/", StringComparison.Ordinal))
            return Ok("""
                {"id":"4iV5W9uYEdYUVa79Axb7Rh",
                 "name":"Song","duration_ms":185000,
                 "artists":[{"name":"Artist"}],"album":{"name":"Album"}}
                """);
        if (u.AbsolutePath == "/v1/me/player/play")
        {
            controlCount++;
            Check(req.Method == HttpMethod.Put
                && u.Query.Contains("device_id=DEVICE-1"),
                "Spotify playback is explicitly targeted to a user device");
            string payload = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Check(payload.Contains("spotify:track:" + Id),
                "Spotify command must play selected track, not a fake local URI");
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });
    using HttpClient http = new(handler);
    using SpotifyProviderPoc poc = new("oauth-token", "lyrics-token", http:http);
    var hits = await poc.SearchAsync("Song");
    Check(hits.Count == 1 && hits[0].Id == Id
        && hits[0].DurationMilliseconds == 185000
        && hits[0].ExternalUrl!.StartsWith("https://open.spotify.com"),
        "Spotify catalog search title/artist/album/duration/link");
    var info = await poc.GetDetailsAsync(hits[0]);
    Check(info?.Album == "Album"
        && info.ExternalUrl == hits[0].ExternalUrl
        && info.ArtworkUrl == hits[0].ArtworkUrl,
        "Spotify details must preserve search artwork and provider song link");
    var lyrics = await poc.GetLyricsAsync(hits[0]);
    Check(lyrics?.Original.Lines.Count == 3
        && lyrics.Original.Lines[0].Text == "歌曲名：真歌词"
        && lyrics.Original.Lines[1].Text == "乐队成员：某人"
        && lyrics.Original.Lines[2].Text == "♪",
        "Spotify line-synced lyrics keep every provider row including credits/music marks");
    var capability = await poc.ResolvePlaybackAsync(hits[0]);
    Check(capability.Disposition == PlaybackDisposition.AuthorizedDeviceCommand
        && capability.AuthorizedUri is null,
        "Spotify cannot return a fabricated local full-audio stream");
    Check(await poc.PlayOnDeviceAsync(hits[0], "DEVICE-1") && controlCount == 1,
        "A 204 user device control command should only acknowledge the request");
}

static async Task SpotifySecurity()
{
    using SpotifyProviderPoc spotify = new();
    bool blocked = false;
    try { await spotify.SearchAsync("No token"); }
    catch (ProviderAuthorizationException) { blocked = true; }
    Check(blocked, "Spotify catalog must not silently scrape as a guessed anonymous user");
    var sample = new ProviderTrack(ExternalProvider.Spotify, "id", "t", "a", "b");
    var playback = await spotify.ResolvePlaybackAsync(sample);
    Check(playback.Disposition == PlaybackDisposition.ExternalPlayerRequired,
        "Unauthenticated Spotify playback must explicitly need user authorization");
    bool invalid = false;
    try { await spotify.PlayOnDeviceAsync(sample); }
    catch (ArgumentException) { invalid = true; }
    Check(invalid, "Spotify playback id needs validation");
    using AppleMusicProviderPoc apple = new();
    bool appleAuth = false;
    try { await apple.GetLyricsAsync(new ProviderTrack(
        ExternalProvider.AppleMusic, "12345", "t", "a", "b")); }
    catch (ProviderAuthorizationException) { appleAuth = true; }
    Check(appleAuth, "Apple private TTML must require its own subscriber credential");
}

static HttpResponseMessage Ok(string content) => new(HttpStatusCode.OK)
{
    Content = new StringContent(content, Encoding.UTF8, "application/json"),
};

sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(responder(request));
    }
}
