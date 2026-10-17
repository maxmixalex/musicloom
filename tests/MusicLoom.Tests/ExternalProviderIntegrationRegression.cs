using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MusicLoom.App;
using MusicLoom.Core.Lyrics;
using MusicLoom.LyricsProviders;
using MusicLoom.LyricsProviders.Migu;

internal static class ExternalProviderIntegrationRegression
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        // The identifiers are historical on-disk state, not a promise that
        // the experimental Apple/Spotify adapters are active providers.
        Assert((int)CloudLyricsSource.QqMusic == 0
            && (int)CloudLyricsSource.KugouMusic == 1
            && (int)CloudLyricsSource.MiguMusic == 2
            && (int)CloudLyricsSource.AppleMusic == 3
            && (int)CloudLyricsSource.Spotify == 4,
            "Provider IDs changed; existing user matching records would be lost.");
        // Packaging is enforced by the LyricsProviders csproj dependency
        // graph and the anonymous-only dispatch assertions below.
        TestAuthenticatedProvidersNotExposed();
        TestMiguSongPageLinks();
        await TestLegacyDbAndMiguAsync();
        await TestAnonymousMiguFlowAsync();
        await TestMiguFallbackAsync();
        await TestMiguDirectGrantAsync();
        await TestSearchResultAudioGrantAsync();
        await TestMiguTimeoutFallbackAsync();
        await TestMiguOptionalLyricTimeoutAsync();
        await TestMiguDurationHydrationAsync();
        await TestMiguClientMetadataBridgeAsync();
        TestMiguDurationFormats();
        TestLyricsIntegrity();
    }

    private static void TestAuthenticatedProvidersNotExposed()
    {
        Assert(!CloudLyricsSourceNames.IsExternal(CloudLyricsSource.AppleMusic)
            && !CloudLyricsSourceNames.IsExternal(CloudLyricsSource.Spotify)
            && CloudLyricsSourceNames.IsExternal(CloudLyricsSource.MiguMusic),
            "Account-dependent providers cannot appear in the MusicLoom product.");
        using CloudLyricsClient client = new(new HttpClient(new GuardHandler(
            _ => throw new InvalidOperationException(
                "A disabled Spotify/Apple client attempted an HTTP request."))));
        foreach (CloudLyricsSource unavailable in new[]
        {
            CloudLyricsSource.AppleMusic, CloudLyricsSource.Spotify,
        })
        {
            bool searchBlocked = false;
            try
            {
                _ = client.SearchAsync(unavailable, "Song");
            }
            catch (ArgumentOutOfRangeException) { searchBlocked = true; }

            bool lyricsBlocked = false;
            try
            {
                _ = client.GetLyricsAsync(new CloudLyricsCandidate(
                    unavailable, "id", "Song", "Artist", "", 180000));
            }
            catch (ArgumentOutOfRangeException) { lyricsBlocked = true; }

            bool playbackBlocked = false;
            try
            {
                _ = client.GetAudioPreviewAsync(new CloudLyricsCandidate(
                    unavailable, "id", "Song", "Artist", "", 180000));
            }
            catch (ArgumentOutOfRangeException) { playbackBlocked = true; }

            Assert(searchBlocked && lyricsBlocked && playbackBlocked,
                "Removed account-dependent provider remains callable in release.");
        }
    }

    private static void TestMiguSongPageLinks()
    {
        CloudLyricsCandidate song = new(CloudLyricsSource.MiguMusic,
            "content-123", "Title", "Singer", "Album", 0,
            CopyrightId: "600929000001520013");
        Assert(CloudLyricsSourceNames.SongPage(song)?.AbsoluteUri
            == "https://music.migu.cn/v3/music/song/600929000001520013",
            "Migu copyrightId should resolve to the provider's own song page.");
        Assert(CloudLyricsSourceNames.SongPage(song with
            { CopyrightId = "invalid/../id" }) is null
            && CloudLyricsSourceNames.SongPage(song with
            { CopyrightId = null }) is null
            && CloudLyricsSourceNames.SongPage(song with
            { ExternalUrl = "https://migu.cn.evil.example/track",
              CopyrightId = null }) is null,
            "Do not construct links from untrusted hosts or non-copyright IDs.");
        Assert(CloudLyricsSourceNames.SongPage(song with
            { ExternalUrl = "https://music.migu.cn/v3/music/song/123456" })
            ?.AbsolutePath == "/v3/music/song/123456",
            "A verified provider song URL should take priority.");
        Assert(CloudLyricsSourceNames.SongPage(song with
            { Source = CloudLyricsSource.QqMusic }) is null,
            "Migu song links must never be used for another provider.");
    }

    private static async Task TestLegacyDbAndMiguAsync()
    {
        string file = Path.Combine(Path.GetTempPath(),
            "musicloom-anonymous-providers-" + Guid.NewGuid().ToString("N") + ".db");
        const string path = @"C:\Music\Song.flac";
        try
        {
            // Load the database layout predating the provider extension.
            // Preserve experimental match rows, but NEVER surface their
            // provider as a usable source in the current build.
            await using (SqliteConnection db = new("Data Source=" + file))
            {
                await db.OpenAsync();
                await using SqliteCommand cmd = db.CreateCommand();
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
                    INSERT INTO provider_matches(
                      source_path, provider, suppressed, confirmed, track_id,
                      title, updated_at
                    ) VALUES (
                      'C:\Music\Song.flac', 4, 0, 1, 'old-spotify-id',
                      'Legacy experimental row', '2026-10-16T00:00:00Z'
                    );
                    """;
                await cmd.ExecuteNonQueryAsync();
            }
            SqliteProviderMatchStore store = new(file);
            CloudLyricsCandidate song = new(CloudLyricsSource.MiguMusic,
                "content-123", "歌曲名 宇宙", "歌手名 某人", "专辑", 120000,
                CoverUrl: "https://d.musicapp.migu.cn/a.jpg",
                ContentId: "content-123", CopyrightId: "copyright-123",
                ProviderSongId: "song-123",
                LrcUrl: "https://d.musicapp.migu.cn/original.lrc",
                TrcUrl: "https://d.musicapp.migu.cn/translated.trc",
                MrcUrl: "https://d.musicapp.migu.cn/words.mrc");
            await store.SaveMatchAsync(path, song, confirmed: true);
            var snapshot = await store.LoadSnapshotAsync();
            Assert(snapshot[path][CloudLyricsSource.MiguMusic].Candidate == song
                && snapshot[path][CloudLyricsSource.Spotify].Candidate?.Id
                    == "old-spotify-id",
                "Migration must preserve both anonymous matches and inactive historic rows.");
            await store.SuppressAsync(path, CloudLyricsSource.MiguMusic);
            Assert((await store.GetAsync(path, CloudLyricsSource.MiguMusic))
                    is { Suppressed: true, Candidate: null }
                && (await store.GetAsync(path, CloudLyricsSource.Spotify))
                    ?.Candidate?.Id == "old-spotify-id",
                "Explicit Migu clear must not touch archived provider rows.");
            SqliteProviderMatchStore newStore = new(file);
            Assert((await newStore.GetAsync(path, CloudLyricsSource.MiguMusic))
                    ?.Suppressed == true,
                "Migu clear state is not durable across process restarts.");
        }
        finally
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(file + suffix); }
                catch (IOException) { }
            }
        }
    }

    private static async Task TestAnonymousMiguFlowAsync()
    {
        int requests = 0;
        using HttpClient http = new(new GuardHandler(req =>
        {
            Uri u = req.RequestUri!;
            Assert(u.Scheme == "https" && u.IdnHost.EndsWith(".migu.cn",
                    StringComparison.OrdinalIgnoreCase)
                && req.Headers.Authorization is null,
                "Migu must operate without account, OAuth or token.");
            requests++;
            string path = u.AbsolutePath;
            if (path.EndsWith("/search_all.do", StringComparison.Ordinal))
                return Json("""
                    {"code":"000000","songResultData":{"result":[{
                      "name":"银河","contentId":"cid","copyrightId":"copy",
                      "songId":"sid","singers":[{"name":"歌手甲"}],
                      "albums":[{"id":"al","name":"发行专辑"}],
                      "lyricUrl":"https://d.musicapp.migu.cn/original.lrc",
                      "trcUrl":"https://d.musicapp.migu.cn/translation.trc",
                      "imgItems":[{"img":"https://d.musicapp.migu.cn/cover.jpg",
                                   "imgSizeType":"03"}]
                    }]}}
                    """);
            if (path == "/original.lrc")
                return Json("[00:00.000]歌曲名 银河\n[00:01.000]歌手名 乐队\n[00:02.000]乐队成员 某人");
            if (path == "/translation.trc")
                return Json("[00:00.000]Milky Way\n[00:01.000]The band");
            if (path.EndsWith("/resourceinfo.do", StringComparison.Ordinal))
                return Json("""
                    {"code":"000000","resource":[{
                      "contentId":"cid","copyrightId":"copy",
                      "lrcUrl":"https://d.musicapp.migu.cn/original.lrc",
                      "trcUrl":"https://d.musicapp.migu.cn/translation.trc"}]}
                    """);
            if (path.EndsWith("/listen/v1.0", StringComparison.Ordinal))
            {
                Assert(u.Query.Contains("contentId=cid", StringComparison.Ordinal)
                    && u.Query.Contains("copyrightId=copy",
                        StringComparison.Ordinal),
                    "Playback must pass both real Migu identifiers.");
                return Json("""
                    {"code":"000000","data":{
                      "url":"https://freetyst.nf.migu.cn/authorized.mp3"}}
                    """);
            }
            throw new InvalidOperationException("Unexpected Migu request: " + path);
        }));
        using CloudLyricsClient client = new(http);
        IReadOnlyList<CloudLyricsCandidate> found =
            await client.SearchAsync(CloudLyricsSource.MiguMusic, "银河");
        Assert(found.Count == 1 && found[0].Id == "cid"
            && found[0].ContentId == "cid"
            && found[0].CopyrightId == "copy"
            && found[0].ProviderSongId == "sid"
            && found[0].CoverUrl == "https://d.musicapp.migu.cn/cover.jpg",
            "Migu anonymous search must map song identifiers and cover.");
        LrcDocument? lyrics = await client.GetLyricsAsync(found[0]);
        Assert(lyrics is { Lines.Count: >= 3 }
            && lyrics.Lines.Any(l => l.Text == "歌曲名 银河")
            && lyrics.Lines.Any(l => l.Text == "歌手名 乐队")
            && lyrics.Lines.Any(l => l.Text == "乐队成员 某人")
            && lyrics.Lines.Any(l => l.Text == "Milky Way"),
            "Migu metadata-shaped lyric lines must be preserved verbatim.");
        CloudAudioPreviewResult play =
            await client.GetAudioPreviewAsync(found[0]);
        Assert(play.CanPlay
            && play.StreamUrl?.Host == "freetyst.nf.migu.cn"
            && requests >= 5,
            "Migu may play only the actual guest-granted HTTPS audio stream.");
    }

    private static async Task TestMiguFallbackAsync()
    {
        int modern = 0, legacy = 0, details = 0, listens = 0;
        using HttpClient http = new(new GuardHandler(req =>
        {
            Uri uri = req.RequestUri!;
            Assert(uri.Scheme == "https"
                && uri.IdnHost.EndsWith(".migu.cn", StringComparison.OrdinalIgnoreCase)
                && req.Headers.Authorization is null,
                "All Migu requests must remain anonymous and first-party.");
            if (uri.AbsolutePath.Contains("/MIGUM3.0/v1.0/content/search_all.do",
                    StringComparison.Ordinal))
            {
                modern++;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            if (uri.AbsolutePath.Contains("/MIGUM2.0/v1.0/content/search_all.do",
                    StringComparison.Ordinal))
            {
                legacy++;
                return Json("""
                    {"code":"000000","songResultData":{"result":[{
                      "name":"回退歌曲","contentId":"cid","copyrightId":"copy",
                      "singers":[{"name":"歌手"}],"albums":[{"name":"专辑"}]
                    }]}}
                    """);
            }
            if (uri.AbsolutePath.EndsWith("/resourceinfo.do",
                    StringComparison.Ordinal))
            {
                details++;
                return new HttpResponseMessage(HttpStatusCode.BadGateway);
            }
            if (uri.AbsolutePath.EndsWith("/listen/v1.0",
                    StringComparison.Ordinal))
            {
                listens++;
                Assert(uri.Query.Contains("contentId=cid", StringComparison.Ordinal)
                    && uri.Query.Contains("copyrightId=copy", StringComparison.Ordinal),
                    "Listen grants must pass separate real content/copyright identifiers.");
                return Json("""
                    {"code":"000000","data":{
                      "url":"https://freetyst.nf.migu.cn/full.mp3",
                      "lrcUrl":"https://d.musicapp.migu.cn/guest.lrc",
                      "trcUrl":"https://d.musicapp.migu.cn/guest.trc"}}
                    """);
            }
            if (uri.AbsolutePath == "/guest.lrc")
                return Json("[00:00.000]歌曲名 回退歌曲\n[00:01.000]演奏者 某乐队");
            if (uri.AbsolutePath == "/guest.trc")
                return Json("[00:00.000]Guest Song");
            throw new InvalidOperationException("Unexpected anonymous endpoint: "
                + uri.AbsolutePath);
        }));
        using CloudLyricsClient client = new(http);
        IReadOnlyList<CloudLyricsCandidate> found =
            await client.SearchAsync(CloudLyricsSource.MiguMusic, "回退歌曲");
        Assert(found.Count == 1 && modern == 1 && legacy == 1,
            "Migu search must fall back from MIGUM3.0 to MIGUM2.0.");
        LrcDocument? lyrics = await client.GetLyricsAsync(found[0]);
        Assert(lyrics is not null
            && lyrics.Lines.Any(l => l.Text == "歌曲名 回退歌曲")
            && lyrics.Lines.Any(l => l.Text == "演奏者 某乐队")
            && lyrics.Lines.Any(l => l.Text == "Guest Song"),
            "A listen grant must recover missing timed lyrics without metadata.");
        CloudAudioPreviewResult playback = await client.GetAudioPreviewAsync(found[0]);
        Assert(playback.CanPlay
            && playback.StreamUrl?.Host == "freetyst.nf.migu.cn"
            && details >= 4 && listens == 2,
            "Guest playback must survive metadata outages with only granted URLs.");
    }

    private static async Task TestMiguDirectGrantAsync()
    {
        using HttpClient http = new(new GuardHandler(_ =>
            throw new InvalidOperationException(
                "Provider-granted stream must not require a metadata request.")));
        using CloudLyricsClient client = new(http);
        CloudLyricsCandidate candidate = new(
            CloudLyricsSource.MiguMusic, "cid", "Song", "Artist", "Album",
            180000, StreamUrl: "https://freetyst.nf.migu.cn/full.mp3");
        CloudAudioPreviewResult playback = await client.GetAudioPreviewAsync(candidate);
        Assert(playback.CanPlay
            && playback.StreamUrl?.Host == "freetyst.nf.migu.cn",
            "Direct stream was lost during unnecessary detail lookup.");
    }

    private static async Task TestSearchResultAudioGrantAsync()
    {
        int requestCount = 0;
        using HttpClient http = new(new GuardHandler(req =>
        {
            Assert(req.RequestUri?.AbsolutePath.EndsWith("/search_all.do",
                    StringComparison.Ordinal) == true,
                "Already-granted Migu audio must not issue a detail/play request.");
            requestCount++;
            return Json("""
                {"code":"000000","songResultData":{"result":[{
                  "name":"歌曲","contentId":"cid","copyrightId":"copy",
                  "playUrl":"https://freetyst.nf.migu.cn/direct.mp3",
                  "auditionUrl":"https://freetyst.nf.migu.cn/preview.mp3"
                }]}}
                """);
        }));
        using CloudLyricsClient client = new(http);
        IReadOnlyList<CloudLyricsCandidate> songs =
            await client.SearchAsync(CloudLyricsSource.MiguMusic, "歌曲");
        Assert(songs.Count == 1
            && songs[0].StreamUrl == "https://freetyst.nf.migu.cn/direct.mp3"
            && songs[0].PreviewUrl == "https://freetyst.nf.migu.cn/preview.mp3",
            "Migu search discarded its own authorized playback address.");

        CloudAudioPreviewResult play = await client.GetAudioPreviewAsync(songs[0]);
        Assert(play.CanPlay
            && play.StreamUrl?.AbsoluteUri == "https://freetyst.nf.migu.cn/direct.mp3"
            && requestCount == 1,
            "Search's full guest audio grant must play without redundant requests.");

        using JsonDocument untrusted = JsonDocument.Parse("""
            {"contentId":"cid","name":"Song",
             "playUrl":"https://migu.cn.evil.example/song.mp3",
             "auditionUrl":"http://freetyst.nf.migu.cn/preview.mp3"}
            """);
        ProviderTrack? rejected = MiguProviderClient.ParseSong(untrusted.RootElement);
        Assert(rejected is not null
            && rejected.StreamUrl is null && rejected.PreviewUrl is null,
            "Search parser must not preserve untrusted audio URLs in match records.");
    }

    private static async Task TestMiguTimeoutFallbackAsync()
    {
        int modern = 0, legacy = 0;
        using HttpClient http = new(new GuardHandler(req =>
        {
            Assert(req.Headers.Authorization is null
                && req.RequestUri?.Scheme == Uri.UriSchemeHttps,
                "Timeout retry must remain anonymous and use HTTPS.");
            if (req.RequestUri!.AbsolutePath.Contains("/MIGUM3.0/",
                    StringComparison.Ordinal))
            {
                modern++;
                throw new TaskCanceledException("Simulated HTTP timeout");
            }
            legacy++;
            return Json("""
                {"code":"000000","songResultData":{"result":[{
                  "name":"Fallback","contentId":"id","copyrightId":"copy"
                }]}}
                """);
        }));
        using CloudLyricsClient client = new(http);
        IReadOnlyList<CloudLyricsCandidate> rows =
            await client.SearchAsync(CloudLyricsSource.MiguMusic, "Fallback");
        Assert(rows.Count == 1 && modern == 1 && legacy == 1,
            "An internal HTTP timeout should retry the legacy anonymous endpoint.");

        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        bool didCancel = false;
        try
        {
            _ = await client.SearchAsync(
                CloudLyricsSource.MiguMusic, "Canceled", canceled.Token);
        }
        catch (OperationCanceledException) { didCancel = true; }
        Assert(didCancel && modern == 1 && legacy == 1,
            "Caller cancellation must not cause another provider request.");
    }

    private static async Task TestMiguOptionalLyricTimeoutAsync()
    {
        int requests = 0;
        using HttpClient http = new(new GuardHandler(request =>
        {
            requests++;
            string? path = request.RequestUri?.AbsolutePath;
            if (path == "/lyrics.lrc")
                return Json("[00:00.000]重要原文\n[00:01.000]乐队成员");
            if (path is "/lyrics.trc" or "/lyrics.mrc")
                throw new TaskCanceledException("Simulated optional lyric timeout");
            throw new InvalidOperationException("Unexpected lyric URL: " + path);
        }));
        using MiguProviderClient provider = new(http);
        ProviderTrack song = new(ExternalProvider.Migu, "id",
            "Song", "Artist", "Album",
            LrcUrl: "https://d.musicapp.migu.cn/lyrics.lrc",
            TrcUrl: "https://d.musicapp.migu.cn/lyrics.trc",
            MrcUrl: "https://d.musicapp.migu.cn/lyrics.mrc");
        ProviderLyricBundle? lyric = await provider.GetLyricsAsync(song);
        Assert(lyric is not null
            && lyric.Original.Lines.Any(x => x.Text == "重要原文")
            && lyric.Original.Lines.Any(x => x.Text == "乐队成员")
            && lyric.Translation is null && lyric.WordTimed is null
            && requests == 3,
            "Optional subtitle timeouts must not erase original timed lyrics.");
    }

    private static async Task TestMiguClientMetadataBridgeAsync()
    {
        using HttpClient http = new(new GuardHandler(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            Assert(req.Headers.Authorization is null,
                "Migu metadata bridge must not acquire account credentials.");
            if (path.EndsWith("/resourceinfo.do", StringComparison.Ordinal))
                return Json("""
                    {"code":"000000","resource":[{"duration":202}]}
                    """);
            throw new InvalidOperationException(
                "Search already has duration after Migu detail response: " + path);
        }));
        using CloudLyricsClient client = new(http);
        CloudLyricsCandidate original = new(CloudLyricsSource.MiguMusic,
            "source-cid", "Search title", "Singer", "Album", 0,
            CopyrightId: "600929000001520013",
            ContentId: "source-cid");
        CloudLyricsCandidate enriched =
            await client.EnrichMiguCandidateAsync(original);
        Assert(enriched.DurationMilliseconds == 202000
            && original.DurationMilliseconds == 0
            && enriched.Id == original.Id
            && enriched.Title == original.Title
            && enriched.CopyrightId == original.CopyrightId,
            "In-app C# provider bridge must enrich actual candidate duration, "
            + "preserving source identity and immutable search result.");
    }

    private static void TestMiguDurationFormats()
    {
        (string Payload, int Milliseconds)[] examples =
        [
            ("""{"contentId":"one","duration":245}""", 245000),
            ("""{"contentId":"two","duration":"03:37"}""", 217000),
            ("""{"contentId":"three","durationMs":184250}""", 184250),
            ("""{"contentId":"four","duration":199000}""", 199000),
            ("""{"contentId":"five","song":{"duration":"192.5"}}""", 192500),
            ("""{"contentId":"six","song":{"duration":0}}""", 0),
        ];
        foreach ((string payload, int milliseconds) in examples)
        {
            using JsonDocument json = JsonDocument.Parse(payload);
            ProviderTrack track = MiguProviderClient.ParseSong(json.RootElement)
                ?? throw new InvalidOperationException("Migu search parser lost a valid song.");
            Assert(track.DurationMilliseconds == milliseconds,
                $"Unexpected Migu duration for {track.Id}: {track.DurationMilliseconds}");
        }
    }

    private static async Task TestMiguDurationHydrationAsync()
    {
        int details = 0, info = 0, listen = 0;
        using HttpClient http = new(new GuardHandler(req =>
        {
            Assert(req.RequestUri?.Scheme == Uri.UriSchemeHttps
                && req.Headers.Authorization is null,
                "Migu duration lookup must stay HTTPS and anonymous.");
            string path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/resourceinfo.do", StringComparison.Ordinal))
            {
                details++;
                return Json("""
                    {"code":"000000","resource":[{
                      "contentId":"content-id","copyrightId":"600929000001520013",
                      "title":"Real track", "duration":0
                    }]}
                    """);
            }
            if (path.EndsWith("/song_info.do", StringComparison.Ordinal))
            {
                info++;
                Assert(req.RequestUri.Query.Contains("contentId=content-id",
                    StringComparison.Ordinal) && req.RequestUri.Query.Contains(
                        "copyrightId=600929000001520013", StringComparison.Ordinal),
                    "Migu duration must query the original provider track identifiers.");
                return Json("""
                    {"code":"000000","data":{"song":{"duration":231}}}
                    """);
            }
            if (path.EndsWith("/listen/v1.0", StringComparison.Ordinal))
            {
                listen++;
                return Json("""
                    {"code":"000000","data":{"song":{"duration":193}}}
                    """);
            }
            throw new InvalidOperationException("Unexpected duration endpoint: " + path);
        }));
        using MiguProviderClient provider = new(http);
        ProviderTrack unknown = new(ExternalProvider.Migu, "content-id",
            "Real track", "Singer", "Album",
            ContentId: "content-id", CopyrightId: "600929000001520013");
        ProviderTrack enriched = await provider.EnrichMetadataAsync(unknown);
        Assert(enriched.DurationMilliseconds == 231000
            && enriched.Id == unknown.Id
            && enriched.ContentId == unknown.ContentId
            && enriched.CopyrightId == unknown.CopyrightId
            && details == 1 && info == 1 && listen == 0,
            "Migu song_info metadata must fill absent search duration without playback request.");

        using HttpClient fallbackHttp = new(new GuardHandler(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            if (path.EndsWith("/resourceinfo.do", StringComparison.Ordinal)
                || path.EndsWith("/song_info.do", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (path.EndsWith("/listen/v1.0", StringComparison.Ordinal))
            {
                listen++;
                return Json("""
                    {"code":"000000","data":{"song":{"duration":"03:13"}}}
                    """);
            }
            throw new InvalidOperationException("Unexpected fallback endpoint: " + path);
        }));
        using MiguProviderClient fallback = new(fallbackHttp);
        ProviderTrack second = await fallback.EnrichMetadataAsync(unknown);
        Assert(second.DurationMilliseconds == 193000 && listen == 1,
            "Migu anonymous listen song duration must survive metadata failures.");
    }

    private static void TestLyricsIntegrity()
    {
        LrcDocument original = LrcDocument.Parse(
            "[00:00.000]歌曲名 流星\n[00:01.000]乐队成员：某人");
        LrcDocument timed = MiguMrcCodec.Parse(
            "[0,0]歌曲名(0,0)流星(0,0)\n"
            + "[1000,900]乐队成员(1000,450)：某人(1450,450)\n");
        LrcDocument result = CloudLyricsClient.MergeExternalLyrics(
            new ProviderLyricBundle(original, original.ToString(),
                WordTimed: timed));
        Assert(result.Lines.Any(x => x.Text == "歌曲名 流星")
            && result.Lines.Any(x => x.Text == "乐队成员：某人")
            && result.Lines.Any(x => x.Text == "歌曲名流星")
            && result.Lines.Any(x => x.Words is { Count: > 0 }),
            "Original timed lyric text and MRC source lines must not be deleted.");
    }

    private static HttpResponseMessage Json(string text) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(text, Encoding.UTF8, "application/json")
        };

    private static void Assert(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }

    private sealed class GuardHandler(Func<HttpRequestMessage,
        HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }
}
