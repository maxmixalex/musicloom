using System.Net;
using System.Net.Http;
using System.Text;
using MusicLoom.App;
using MusicLoom.Core.Lyrics;
using MusicLoom.LyricsProviders;
using MusicLoom.ProviderPoc;

internal static class ExternalProviderIntegrationRegression
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        Assert((int)CloudLyricsSource.QqMusic == 0
            && (int)CloudLyricsSource.KugouMusic == 1
            && (int)CloudLyricsSource.MiguMusic == 2
            && (int)CloudLyricsSource.AppleMusic == 3
            && (int)CloudLyricsSource.Spotify == 4,
            "The existing two provider DB identifiers changed.");

        await TestPersistenceAsync();
        await TestMiguAsync();
        await TestAppleSpotifyAsync();
        await TestSpotifyLocalTokenFallbackAsync();
        TestCanonicalMerge();
    }

    private static async Task TestPersistenceAsync()
    {
        string db = Path.Combine(Path.GetTempPath(),
            "musicloom-six-providers-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            const string path = @"C:\Source\track.flac";
            // Re-create the PRE-2026-10-17 schema to prove on-disk
            // migration, not just creation of a clean new database.
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                "Data Source=" + db))
            {
                await connection.OpenAsync();
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE provider_matches (
                      source_path TEXT NOT NULL COLLATE NOCASE,
                      provider INTEGER NOT NULL,
                      suppressed INTEGER NOT NULL DEFAULT 0,
                      confirmed INTEGER NOT NULL DEFAULT 0,
                      track_id TEXT, song_mid TEXT, media_mid TEXT, file_hash TEXT,
                      album_id TEXT, title TEXT, artist TEXT, album TEXT,
                      duration_ms INTEGER, cover_url TEXT,
                      updated_at TEXT NOT NULL,
                      PRIMARY KEY (source_path, provider)
                    );
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            SqliteProviderMatchStore store = new(db);
            CloudLyricsCandidate migu = new(
                CloudLyricsSource.MiguMusic, "content-123", "歌曲名 宇宙",
                "歌手名 某人", "专辑", 120000,
                CoverUrl: "https://d.musicapp.migu.cn/a.jpg",
                ContentId: "content-123", CopyrightId: "copyright-123",
                ProviderSongId: "song-123",
                LrcUrl: "https://d.musicapp.migu.cn/original.lrc",
                TrcUrl: "https://d.musicapp.migu.cn/translated.trc",
                MrcUrl: "https://d.musicapp.migu.cn/words.mrc",
                StreamUrl: "https://d.musicapp.migu.cn/audio.mp3");
            CloudLyricsCandidate apple = new(
                CloudLyricsSource.AppleMusic, "123456", "Apple song",
                "Artist", "Album", 150000,
                CoverUrl: "https://is1-ssl.mzstatic.com/artwork.jpg",
                PreviewUrl: "https://audio-ssl.itunes.apple.com/preview.m4a",
                ExternalUrl: "https://music.apple.com/us/album/song/123456");
            CloudLyricsCandidate spotify = new(
                CloudLyricsSource.Spotify, "4iV5W9uYEdYUVa79Axb7Rh",
                "Spotify Song", "Singer", "Album", 150000,
                ExternalUrl: "https://open.spotify.com/track/4iV5W9uYEdYUVa79Axb7Rh");
            await store.SaveMatchAsync(path, migu, true);
            await store.SaveMatchAsync(path, apple, false);
            await store.SaveMatchAsync(path, spotify, false);
            var snapshot = await store.LoadSnapshotAsync();
            Assert(snapshot[path].Count == 3
                && snapshot[path][CloudLyricsSource.MiguMusic].Candidate == migu
                && snapshot[path][CloudLyricsSource.AppleMusic].Candidate == apple
                && snapshot[path][CloudLyricsSource.Spotify].Candidate == spotify,
                "Migration or provider metadata round-trip lost identities/resources.");
            await store.SuppressAsync(path, CloudLyricsSource.AppleMusic);
            Assert((await store.GetAsync(path, CloudLyricsSource.AppleMusic))
                    is { Suppressed: true, Candidate: null }
                && (await store.GetAsync(path, CloudLyricsSource.MiguMusic))
                    ?.Candidate == migu
                && (await store.GetAsync(path, CloudLyricsSource.Spotify))
                    ?.Candidate == spotify,
                "Explicit Apple no-match must not destroy other provider matches.");
            SqliteProviderMatchStore reopened = new(db);
            Assert((await reopened.GetAsync(path, CloudLyricsSource.AppleMusic))
                    ?.Suppressed == true,
                "Explicit new-provider empty state did not survive reopening.");
        }
        finally
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(db + suffix); } catch (IOException) { }
            }
        }
    }

    private static async Task TestMiguAsync()
    {
        using HttpClient http = new(new FakeHandler(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/search_all.do", StringComparison.Ordinal))
                return Json("""
                    {"code":"000000","songResultData":{"result":[{
                      "name":"Sample","contentId":"cid","copyrightId":"copy",
                      "songId":"sid","singers":[{"name":"Singer"}],
                      "albums":[{"id":"al","name":"Album"}],
                      "lyricUrl":"https://d.musicapp.migu.cn/lrc",
                      "trcUrl":"https://d.musicapp.migu.cn/trc",
                      "imgItems":[{"img":"https://d.musicapp.migu.cn/cover.jpg",
                                   "imgSizeType":"03"}]
                    }]}}
                    """);
            if (path == "/lrc")
                return Json("[00:00.000]歌曲名 宇宙\n[00:01.000]歌手名 乐队\n[00:02.000]乐队成员 某人");
            if (path == "/trc")
                return Json("[00:00.000]Universe\n[00:01.000]Band");
            if (path.EndsWith("/resourceinfo.do", StringComparison.Ordinal))
                return Json("""
                    {"code":"000000","resource":[{
                      "contentId":"cid","copyrightId":"copy",
                      "lrcUrl":"https://d.musicapp.migu.cn/lrc",
                      "trcUrl":"https://d.musicapp.migu.cn/trc"}]}
                    """);
            if (path.EndsWith("/listen/v1.0", StringComparison.Ordinal))
                return Json("""
                    {"code":"000000","data":{"url":"https://freetyst.nf.migu.cn/authorized.mp3"}}
                    """);
            throw new InvalidOperationException("Unexpected Migu request " + path);
        }));
        using CloudLyricsClient client = new(http);
        IReadOnlyList<CloudLyricsCandidate> songs =
            await client.SearchAsync(CloudLyricsSource.MiguMusic, "Sample");
        Assert(songs.Count == 1 && songs[0].Id == "cid"
            && songs[0].ContentId == "cid"
            && songs[0].CopyrightId == "copy"
            && songs[0].ProviderSongId == "sid"
            && songs[0].CoverUrl == "https://d.musicapp.migu.cn/cover.jpg",
            "Migu search lost IDs or cover mapping.");
        LrcDocument? lyrics = await client.GetLyricsAsync(songs[0]);
        Assert(lyrics is { Lines.Count: >= 3 }
            && lyrics.Lines.Any(line => line.Text == "歌曲名 宇宙")
            && lyrics.Lines.Any(line => line.Text == "歌手名 乐队")
            && lyrics.Lines.Any(line => line.Text == "乐队成员 某人")
            && lyrics.Lines.Any(line => line.Text == "Universe"),
            "Migu experimental bridge stripped names/members or translations.");
        CloudAudioPreviewResult play =
            await client.GetAudioPreviewAsync(songs[0]);
        Assert(play.CanPlay
            && play.StreamUrl?.Host == "freetyst.nf.migu.cn",
            "Migu must only allow the direct provider-granted media URL.");
    }

    private static async Task TestAppleSpotifyAsync()
    {
        var old = new Dictionary<string, string?>();
        foreach (string key in new[]
        {
            "MUSICLOOM_APPLE_DEVELOPER_TOKEN",
            "MUSICLOOM_APPLE_WEB_BEARER",
            "MUSICLOOM_APPLE_MEDIA_USER_TOKEN",
            "MUSICLOOM_SPOTIFY_OAUTH_TOKEN",
            "MUSICLOOM_SPOTIFY_LYRICS_BEARER",
        }) old[key] = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable("MUSICLOOM_APPLE_DEVELOPER_TOKEN", "dev");
            Environment.SetEnvironmentVariable("MUSICLOOM_APPLE_WEB_BEARER", "web");
            Environment.SetEnvironmentVariable("MUSICLOOM_APPLE_MEDIA_USER_TOKEN", "user");
            Environment.SetEnvironmentVariable("MUSICLOOM_SPOTIFY_OAUTH_TOKEN", "oauth");
            Environment.SetEnvironmentVariable("MUSICLOOM_SPOTIFY_LYRICS_BEARER", "lyrics");
            using HttpClient http = new(new FakeHandler(req =>
            {
                string path = req.RequestUri!.AbsolutePath;
                if (req.RequestUri.Host == "api.music.apple.com")
                    return Json("""
                        {"results":{"songs":{"data":[{
                          "id":"12345","attributes":{"name":"Apple track",
                          "artistName":"Artist","albumName":"Album",
                          "artwork":{"url":"https://is1-ssl.mzstatic.com/image/{w}x{h}"},
                          "previews":[{"url":"https://audio-ssl.itunes.apple.com/preview.m4a"}]}}
                        ]}}}
                        """);
                if (req.RequestUri.Host == "amp-api.music.apple.com")
                    return Json("""
                        {"data":[{"attributes":{"ttml":
                          "<tt><body><div><p begin=\"1s\" end=\"2s\">歌曲名 远方</p></div></body></tt>"}}]}
                        """);
                if (req.RequestUri.Host == "api.spotify.com")
                    return Json("""
                        {"tracks":{"items":[{
                          "id":"4iV5W9uYEdYUVa79Axb7Rh","name":"Spotify track",
                          "artists":[{"name":"A"}],"album":{"name":"Album",
                          "images":[{"url":"https://i.scdn.co/image/test"}]}}
                        ]}}
                        """);
                if (path.Contains("/color-lyrics/v2/", StringComparison.Ordinal))
                    return Json("""
                        {"lyrics":{"syncType":"LINE_SYNCED","lines":[
                          {"startTimeMs":"1000","words":"歌手名：歌词"},
                          {"startTimeMs":"2000","words":"乐队成员：谁"}]}}
                        """);
                throw new InvalidOperationException("Unexpected provider request " + path);
            }));
            using CloudLyricsClient client = new(http);
            CloudLyricsCandidate apple = (await client.SearchAsync(
                CloudLyricsSource.AppleMusic, "Apple track")).Single();
            Assert(apple.CoverUrl!.Contains("600x600")
                && apple.PreviewUrl!.StartsWith("https://audio-ssl.itunes.apple.com"),
                "Apple catalog fields missing.");
            LrcDocument? original = await client.GetLyricsAsync(apple);
            Assert(original?.Lines.Single().Text == "歌曲名 远方",
                "Apple TTML header-like lyric was deleted or corrupted.");
            CloudAudioPreviewResult applePreview =
                await client.GetAudioPreviewAsync(apple);
            Assert(applePreview.CanPlay
                && applePreview.StreamUrl!.Host == "audio-ssl.itunes.apple.com",
                "Apple full DRM playback must not be substituted for a preview.");
            CloudLyricsCandidate spotify = (await client.SearchAsync(
                CloudLyricsSource.Spotify, "Spotify track")).Single();
            Assert(spotify.Id == "4iV5W9uYEdYUVa79Axb7Rh"
                && spotify.CoverUrl!.StartsWith("https://i.scdn.co"),
                "Spotify catalog data missing.");
            LrcDocument? spotifyLyrics = await client.GetLyricsAsync(spotify);
            Assert(spotifyLyrics?.Lines.Count == 2
                && spotifyLyrics.Lines[0].Text == "歌手名：歌词"
                && spotifyLyrics.Lines[1].Text == "乐队成员：谁",
                "Spotify lyric metadata text must remain unchanged.");
            CloudAudioPreviewResult spotifyPreview =
                await client.GetAudioPreviewAsync(spotify);
            Assert(!spotifyPreview.CanPlay,
                "Spotify Connect must not be fabricated as MediaPlayer audio.");
        }
        finally
        {
            foreach ((string key, string? value) in old)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static async Task TestSpotifyLocalTokenFallbackAsync()
    {
        // The WinUI Spotify provider should work from ONE authenticated
        // local go-librespot session. No second manually pasted OAuth token
        // is necessary for catalog/lyric lookup when the session exposes it.
        string? oauth = Environment.GetEnvironmentVariable(
            "MUSICLOOM_SPOTIFY_OAUTH_TOKEN");
        string? lyrics = Environment.GetEnvironmentVariable(
            "MUSICLOOM_SPOTIFY_LYRICS_BEARER");
        string? url = Environment.GetEnvironmentVariable(
            "MUSICLOOM_GO_LIBRESPOT_URL");
        try
        {
            Environment.SetEnvironmentVariable("MUSICLOOM_SPOTIFY_OAUTH_TOKEN", null);
            Environment.SetEnvironmentVariable("MUSICLOOM_SPOTIFY_LYRICS_BEARER", null);
            Environment.SetEnvironmentVariable("MUSICLOOM_GO_LIBRESPOT_URL",
                "http://127.0.0.1:3678");
            int tokenRequests = 0;
            using HttpClient http = new(new FakeHandler(req =>
            {
                Uri u = req.RequestUri!;
                if (u.Host == "127.0.0.1" && u.AbsolutePath == "/token")
                {
                    Assert(req.Method == HttpMethod.Post,
                        "go-librespot session token requires local POST.");
                    tokenRequests++;
                    return Json("""{"token":"ephemeral-local-user-token"}""");
                }
                if (u.Host == "api.spotify.com")
                {
                    Assert(req.Headers.Authorization?.Parameter
                        == "ephemeral-local-user-token",
                        "Spotify catalog must use the local authenticated session.");
                    return Json("""
                        {"tracks":{"items":[{
                           "id":"4iV5W9uYEdYUVa79Axb7Rh",
                           "name":"Track","artists":[{"name":"Artist"}],
                           "album":{"name":"Album"}}]}}
                        """);
                }
                if (u.Host == "spclient.wg.spotify.com")
                {
                    Assert(req.Headers.Authorization?.Parameter
                        == "ephemeral-local-user-token",
                        "Spotify lyrics must use its own authorized token.");
                    return Json("""
                        {"lyrics":{"syncType":"LINE_SYNCED",
                          "lines":[{"startTimeMs":"1000",
                                    "words":"乐队成员：保留原文"}]}}
                        """);
                }
                throw new InvalidOperationException("Unexpected Spotify fallback URL.");
            }));
            using CloudLyricsClient client = new(http);
            CloudLyricsCandidate match = (await client.SearchAsync(
                CloudLyricsSource.Spotify, "Artist Track")).Single();
            Assert(match.Id == "4iV5W9uYEdYUVa79Axb7Rh",
                "Session token fallback could not search Spotify.");
            LrcDocument? lyric = await client.GetLyricsAsync(match);
            Assert(lyric?.Lines.Single().Text == "乐队成员：保留原文"
                && tokenRequests == 2,
                "Local-session Spotify lyrics lost text or used no session.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "MUSICLOOM_SPOTIFY_OAUTH_TOKEN", oauth);
            Environment.SetEnvironmentVariable(
                "MUSICLOOM_SPOTIFY_LYRICS_BEARER", lyrics);
            Environment.SetEnvironmentVariable(
                "MUSICLOOM_GO_LIBRESPOT_URL", url);
        }
    }

    private static void TestCanonicalMerge()
    {
        LrcDocument original = LrcDocument.Parse(
            "[00:00.000]歌曲名 流星\n[00:01.000]乐队成员：某人");
        LrcDocument timing = MiguMrcCodec.Parse(
            "[0,0]歌曲名(0,0)流星(0,0)\n"
            + "[1000,900]乐队成员(1000,450)：某人(1450,450)\n");
        LrcDocument result = CloudLyricsClient.MergeExternalLyrics(
            new ProviderLyricBundle(original, original.ToString(),
                WordTimed: timing));
        Assert(result.Lines.Any(x => x.Text == "歌曲名 流星")
            && result.Lines.Any(x => x.Text == "乐队成员：某人")
            && result.Lines.Any(x => x.Text == "歌曲名流星")
            && result.Lines.Any(x => x.Words is { Count: > 0 }),
            "Both original and distinct word-timed source lines must survive.");
    }

    private static HttpResponseMessage Json(string text) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(text, Encoding.UTF8, "application/json"),
        };

    private static void Assert(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }
}
