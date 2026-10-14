using System.Text;
using System.Net;
using System.Net.Http;
using MusicLoom.LyricsProviders;

internal static class CloudLyricsProviderRegression
{
    public static void Run()
    {
        const string qq = """
        {"music.search.SearchCgiService":{"code":0,"data":{"body":{
           "song":{"list":[{"mid":"mid-123","title":"テスト",
           "singer":[{"name":"Artist"}],"album":{"name":"Album"},
           "interval":182}]}}}}}
        """;
        var qqTracks = CloudLyricsClient.ParseQqSearch(qq);
        if (qqTracks.Count != 1 || qqTracks[0].Id != "mid-123"
            || qqTracks[0].DurationMilliseconds != 182_000
            || qqTracks[0].Artist != "Artist")
        {
            throw new InvalidOperationException("QQ Music parser lost song fields.");
        }

        const string kugou = """
        {"data":{"info":[{"album_audio_id":1234,
        "hash":"FILEHASH","songname":"Song",
        "singername":"Singer","album_name":"Release",
        "duration":230}]}}
        """;
        var kgTracks = CloudLyricsClient.ParseKugouSearch(kugou);
        if (kgTracks.Count != 1 || kgTracks[0].Hash != "FILEHASH"
            || kgTracks[0].Id != "1234"
            || kgTracks[0].DurationMilliseconds != 230_000)
        {
            throw new InvalidOperationException("KuGou parser lost search fields.");
        }

        // QQ Lite album MID and Web flattened MID must both populate
        // the actual album cover rather than leaving an empty candidate tile.
        const string qqCoverJson = """
        {"music.search.SearchCgiService":{"data":{"body":{
            "item_song":[
                {"id":11,"mid":"001qSongMid", "title":"One",
                 "singer":[{"name":"A"}],
                 "album":{"name":"Release","pmid":"002fRO0N4FftzY"},
                 "interval":123},
                {"id":12,"mid":"002qSongMid","title":"Two",
                 "singer":[{"name":"A"}],"albumname":"Second",
                 "albumMid":"003fRO0N4FftzX","interval":125}
            ]}}}}
        """;
        var qqCovers = CloudLyricsClient.ParseQqSearch(qqCoverJson);
        if (qqCovers.Count != 2
            || qqCovers[0].CoverUrl !=
                "https://y.gtimg.cn/music/photo_new/T002R300x300M000002fRO0N4FftzY.jpg"
            || qqCovers[1].CoverUrl !=
                "https://y.gtimg.cn/music/photo_new/T002R300x300M000003fRO0N4FftzX.jpg")
        {
            throw new InvalidOperationException(
                "QQ album cover URLs were lost by a Lite/Web search variant.");
        }

        const string kugouArtwork = """
        {"data":{"lists":[
          {"FileHash":"ABCD1234ABCD1234ABCD1234ABCD1234","ID":42,
           "SongName":"Sample","SingerName":"Artist","AlbumName":"Release",
           "AlbumID":2345,
           "imgUrl":"http://imge.kugou.com/stdmusic/{size}/cover.jpg",
           "Duration":187},
          {"FileHash":"BBCC1234BBCC1234BBCC1234BBCC1234","ID":43,
           "SongName":"Second","SingerName":"Artist","AlbumName":"Other",
           "album_id":0,"AlbumID":2346,
           "Duration":192}
        ]}}
        """;
        var kugouCovers = CloudLyricsClient.ParseKugouSearch(kugouArtwork);
        if (kugouCovers.Count != 2
            || kugouCovers[0].CoverUrl !=
                "https://imge.kugou.com/stdmusic/300/cover.jpg"
            || kugouCovers[0].AlbumId != "2345"
            || kugouCovers[1].AlbumId != "2346"
            || kugouCovers[1].CoverUrl is not null)
        {
            throw new InvalidOperationException(
                "KuGou artwork URL normalization or album ID fallback failed.");
        }

        const string albumPayload = """
        {"status":1,"data":{"albumname":"Release",
         "imgurl":"http://imge.kugou.com/stdmusic/{size}/dated.jpg"}}
        """;
        if (CloudLyricsClient.ParseKugouAlbumArtwork(albumPayload)
            != "https://imge.kugou.com/stdmusic/300/dated.jpg")
        {
            throw new InvalidOperationException(
                "KuGou album detail did not decode an actual cover.");
        }

        if (CloudLyricsClient.ParseQqAlbumArtwork(
            """{"req_0":{"data":{"basicInfo":{"albumMId":"002fRO0N4FftzY"}}}}""")
                is not string qqDetail
            || !qqDetail.EndsWith("002fRO0N4FftzY.jpg",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "QQ album ID detail fallback could not resolve a MID.");
        }

        if (CloudLyricsClient.NormalizeArtworkUrl(
                "https://untrusted.invalid/cover.jpg") is not null)
        {
            throw new InvalidOperationException(
                "Non-provider album artwork hosts must be rejected.");
        }

        // Exercise a real asynchronous provider metadata request with a
        // deterministic local transport; network is not needed for CI.
        using (ArtworkHandler handler = new(albumPayload))
        using (HttpClient client = new(handler))
        using (CloudLyricsClient provider = new(client))
        {
            string? resolved = provider.ResolveArtworkAsync(
                kugouCovers[1]).GetAwaiter().GetResult();
            if (resolved !=
                    "https://imge.kugou.com/stdmusic/300/dated.jpg"
                || handler.LastRequest?.Host != "mobilecdn.kugou.com"
                || handler.LastRequest?.Query.Contains("albumid=2346",
                    StringComparison.Ordinal) != true)
            {
                throw new InvalidOperationException(
                    "Missing KuGou album art was not fetched by album ID.");
            }
        }

        const string candidates =
            """{"candidates":[{"id":"129","accesskey":"secret"}]}""";
        if (CloudLyricsClient.ParseKugouLyricsCandidate(candidates)
            != ("129", "secret"))
        {
            throw new InvalidOperationException(
                "KuGou lyric candidate could not be resolved.");
        }

        string base64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("[00:01.000]Original"));
        string translation = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("[00:01.100]译文"));
        string qqLyrics =
            "{\"lyric\":\"" + base64 + "\",\"trans\":\"" + translation + "\"}";
        var merged = CloudLyricsClient.ParseQqLyrics(qqLyrics);
        if (merged is null || merged.Lines.Count != 2
            || !merged.Lines.Any(l => l.Text == "译文"))
        {
            throw new InvalidOperationException(
                "QQ translated lyrics were not merged.");
        }

        var kgLyrics = CloudLyricsClient.ParseKugouLyrics(
            "{\"content\":\"" + base64 + "\"}");
        if (kgLyrics?.Lines.Count != 1 || kgLyrics.Lines[0].Text != "Original")
        {
            throw new InvalidOperationException("KuGou lyric base64 was not parsed.");
        }
    }
    private sealed class ArtworkHandler(string payload) : HttpMessageHandler
    {
        public Uri? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload),
            });
        }
    }

}
