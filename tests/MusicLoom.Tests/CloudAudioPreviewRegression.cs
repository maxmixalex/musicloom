using System.Net;
using System.Text.Json;
using MusicLoom.LyricsProviders;

internal static class CloudAudioPreviewRegression
{
    private const string QqMid = "003lghpv0jfFXG";
    private const string QqMediaMid = "001mediaM1D1abc";

    public static void Run()
    {
        string guest = CloudLyricsClient.BuildQqGuestPreviewRequest(
            QqMid, QqMediaMid, "126548448");
        using (JsonDocument doc = JsonDocument.Parse(guest))
        {
            JsonElement root = doc.RootElement;
            JsonElement comm = root.GetProperty("comm");
            JsonElement request = root.GetProperty("req_0");
            JsonElement param = request.GetProperty("param");
            if (comm.GetProperty("uin").GetString() != "0"
                || request.GetProperty("module").GetString()
                    != "vkey.GetVkeyServer"
                || request.GetProperty("method").GetString()
                    != "CgiGetVkey"
                || param.GetProperty("filename").GetArrayLength() != 4
                || param.GetProperty("songmid").GetArrayLength() != 4
                || param.GetProperty("songmid")[0].GetString() != QqMid
                || !param.GetProperty("filename")[0].GetString()!
                    .Contains(QqMediaMid, StringComparison.Ordinal)
                || param.GetProperty("filename")[2].GetString() !=
                    "M500" + QqMid + ".mp3")
            {
                throw new InvalidOperationException(
                    "QQ guest vkey request omitted the real song MID, " +
                    "media MID, or quality fallbacks.");
            }
        }

        string response = """
        {"req_0":{"code":0,"data":{
          "sip":["https://dl.stream.qqmusic.qq.com/"],
          "midurlinfo":[
            {"filename":"C400003lghpv0jfFXG.m4a",
             "purl":"C400003lghpv0jfFXG.m4a?token=abc"},
            {"filename":"M500003lghpv0jfFXG.mp3",
             "purl":"M500003lghpv0jfFXG.mp3?token=xyz"}]}}}
        """;
        Uri? qq = CloudLyricsClient.ParseQqGuestPreviewResponse(response);
        if (qq?.AbsoluteUri != "https://dl.stream.qqmusic.qq.com/" +
            "M500003lghpv0jfFXG.mp3?token=xyz")
        {
            throw new InvalidOperationException(
                "QQ vkey parsing did not prefer a granted MP3 stream.");
        }

        const string qqEmpty = """
        {"req_0":{"code":0,"data":{"sip":["https://ws.stream.qqmusic.qq.com/"],
        "midurlinfo":[{"filename":"M500003lghpv0jfFXG.mp3",
        "purl":"","result":104003}]}}}
        """;
        if (CloudLyricsClient.ParseQqGuestPreviewResponse(qqEmpty)
            is not null)
        {
            throw new InvalidOperationException(
                "QQ membership/copyright denial produced a fake stream.");
        }

        const string qqUnsafe = """
        {"req_0":{"code":0,"data":{
          "sip":["https://evil.invalid/"],
          "midurlinfo":[{"filename":"M500003lghpv0jfFXG.mp3",
           "purl":"https://evil.invalid/malware.mp3"}]}}}
        """;
        if (CloudLyricsClient.ParseQqGuestPreviewResponse(qqUnsafe)
            is not null)
        {
            throw new InvalidOperationException(
                "QQ untrusted stream URL escaped the provider allowlist.");
        }

        const string kugouJsonp = """
        musicloomPreview({"status":1,"err_code":0,"data":{
          "hash":"A4ED13E2A4ED13E2A4ED13E2A4ED13E2",
          "play_url":"https://fs.w.kugou.com/guest/sample.mp3"}})
        """;
        Uri? kg = CloudLyricsClient.ParseKugouGuestPreviewResponse(kugouJsonp);
        if (kg?.AbsoluteUri !=
            "https://fs.w.kugou.com/guest/sample.mp3")
        {
            throw new InvalidOperationException(
                "KuGou play/getdata JSONP response did not yield stream.");
        }

        Uri? mobile = CloudLyricsClient.ParseKugouGuestPreviewResponse(
            """{"status":1,"errcode":0,"url":"https://fs.open.kugou.com/a.mp3"}""");
        if (mobile?.Host != "fs.open.kugou.com")
        {
            throw new InvalidOperationException(
                "KuGou mobile play-info response was not parsed.");
        }

        Uri? kgBackup = CloudLyricsClient.ParseKugouGuestPreviewResponse(
            """{"status":1,"data":{
            "play_url":"https://untrusted.invalid/f.mp3",
            "play_backup_url":"https://fs.w.kugou.com/guest/backup.mp3"}}""");
        if (kgBackup?.AbsoluteUri
            != "https://fs.w.kugou.com/guest/backup.mp3")
        {
            throw new InvalidOperationException(
                "KuGou valid backup stream was hidden by a bad primary URL.");
        }

        if (CloudLyricsClient.ParseKugouGuestPreviewResponse(
            """{"status":1,"data":{"play_url":""}}""") is not null
            || CloudLyricsClient.ParseKugouGuestPreviewResponse(
            """{"status":0,"data":{"play_url":"https://fs.w.kugou.com/a.mp3"}}""")
                is not null)
        {
            throw new InvalidOperationException(
                "KuGou restricted/empty streams were improperly accepted.");
        }

        foreach (string unsafeUrl in new[]
        {
            "file:///C:/Users/test/song.mp3",
            "http://localhost:5000/song.mp3",
            "https://evil.invalid/a.mp3",
            "https://fs.w.kugou.com.evil.invalid/song.mp3",
            "https://fs.w.kugou.com:4443/song.mp3",
            "ftp://fs.w.kugou.com/song.mp3",
        })
        {
            if (CloudLyricsClient.NormalizeStreamUrl(
                    unsafeUrl, CloudLyricsSource.KugouMusic) is not null)
            {
                throw new InvalidOperationException(
                    "Untrusted provider stream accepted: " + unsafeUrl);
            }
        }

        using (MockHandler fake = new())
        using (HttpClient http = new(fake))
        using (CloudLyricsClient client = new(http))
        {
            CloudLyricsCandidate qqSong = new(
                CloudLyricsSource.QqMusic, "123", "Sample", "Singer",
                "Album", 180_000, SongMid: QqMid, MediaMid: QqMediaMid);
            CloudAudioPreviewResult granted = client.GetAudioPreviewAsync(
                qqSong).GetAwaiter().GetResult();
            if (!granted.CanPlay
                || granted.StreamUrl?.Host != "ws.stream.qqmusic.qq.com"
                || !fake.GotQqGuestRequest
                || fake.GotAuthorization)
            {
                throw new InvalidOperationException(
                    "QQ guest media resolution was not wired end-to-end.");
            }

            CloudLyricsCandidate kugouSong = new(
                CloudLyricsSource.KugouMusic, "12", "Sample", "Singer",
                "Album", 180_000,
                Hash: "A4ED13E2A4ED13E2A4ED13E2A4ED13E2",
                AlbumId: "129");
            CloudAudioPreviewResult grantedKg = client.GetAudioPreviewAsync(
                kugouSong).GetAwaiter().GetResult();
            if (!grantedKg.CanPlay
                || grantedKg.StreamUrl?.Host != "fs.open.kugou.com"
                || !fake.GotKugouWebRequest || !fake.GotKugouMobileFallback)
            {
                throw new InvalidOperationException(
                    "KuGou did not fall back to public mobile play info.");
            }

            CloudAudioPreviewResult invalid = client.GetAudioPreviewAsync(
                kugouSong with { Hash = "NOT_A_REAL_HASH" })
                .GetAwaiter().GetResult();
            if (invalid.CanPlay
                || invalid.Status != CloudAudioPreviewStatus.InvalidCandidate)
            {
                throw new InvalidOperationException(
                    "Invalid KuGou hash was accepted for playback.");
            }
        }
    }

    private sealed class MockHandler : HttpMessageHandler
    {
        public bool GotQqGuestRequest { get; private set; }
        public bool GotKugouWebRequest { get; private set; }
        public bool GotKugouMobileFallback { get; private set; }
        public bool GotAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            GotAuthorization |= request.Headers.Authorization is not null
                || request.Headers.Contains("Cookie");
            Uri target = request.RequestUri
                ?? throw new InvalidOperationException("No request URL");
            if (target.Host == "u.y.qq.com")
            {
                string body = await request.Content!.ReadAsStringAsync(ct);
                using JsonDocument json = JsonDocument.Parse(body);
                GotQqGuestRequest = json.RootElement
                    .GetProperty("req_0")
                    .GetProperty("method").GetString() == "CgiGetVkey";
                return Ok("""
                    {"req_0":{"code":0,"data":{
                       "sip":["https://ws.stream.qqmusic.qq.com/"],
                       "midurlinfo":[
                          {"filename":"C400003lghpv0jfFXG.m4a",
                           "purl":"C400003lghpv0jfFXG.m4a?vkey=public"}]
                    }}}
                    """);
            }

            if (target.Host == "wwwapi.kugou.com")
            {
                GotKugouWebRequest = target.Query.Contains(
                    "album_id=129", StringComparison.Ordinal);
                return Ok("""
                    musicloomPreview({"status":1,"data":{"play_url":""}})
                    """);
            }

            if (target.Host == "m.kugou.com")
            {
                GotKugouMobileFallback = true;
                return Ok("""
                    {"status":1,"errcode":0,
                     "url":"https://fs.open.kugou.com/guest/sample.mp3"}
                    """);
            }

            throw new InvalidOperationException(
                "Unexpected provider request: " + target);
        }

        private static HttpResponseMessage Ok(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            };
    }
}
