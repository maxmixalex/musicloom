using System.Text;
using System.Net;
using System.Net.Http;
using MusicLoom.LyricsProviders;
using MusicLoom.Core.Lyrics;

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

        const string qqDetailTrackInfo = """
        {"req_0":{"code":0,"data":{"track_info":{"album":{
            "name":"Album","pmid":"002fRO0N4FftzY"}}}}}
        """;
        if (CloudLyricsClient.ParseQqAlbumArtwork(qqDetailTrackInfo) !=
            "https://y.gtimg.cn/music/photo_new/T002R300x300M000002fRO0N4FftzY.jpg")
            throw new InvalidOperationException(
                "QQ track detail album PMIDs do not hydrate artwork.");

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

        const string kugouUnionCover = """
        {"data":{"lists":[{
          "FileHash":"ABCD1234ABCD1234ABCD1234ABCD1234",
          "ID":13,"SongName":"Union Cover","SingerName":"A",
          "AlbumName":"Real album","AlbumID":4242,
          "trans_param":{"union_cover":
            "http://imge.kugou.com/stdmusic/{size}/from-union.jpg"}
        }]}}
        """;
        var unionCovers = CloudLyricsClient.ParseKugouSearch(kugouUnionCover);
        if (unionCovers.Count != 1
            || unionCovers[0].CoverUrl !=
                "https://imge.kugou.com/stdmusic/300/from-union.jpg")
        {
            throw new InvalidOperationException(
                "KuGou v2 trans_param.union_cover was not displayed.");
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

        // Lite and Web QQ lyric responses can carry a blank trans field
        // while translated lyrics live in tlyric. Romanization may be a
        // simple LRC rather than a separately word-timed encrypted QRC.
        string simpleRomaji = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("[00:01.000]o ri ji na ru"));
        string nested = "{\"req_0\":{\"data\":{\"lyric\":\""
            + base64 + "\",\"trans\":\"\",\"tlyric\":\""
            + translation + "\",\"roma\":\"" + simpleRomaji
            + "\"}}}";
        LrcDocument? recovered = CloudLyricsClient.ParseQqLyrics(nested);
        if (recovered is null || recovered.Lines.Count != 2
            || recovered.Lines[0].Romanization != "o ri ji na ru"
            || !recovered.Lines.Any(line => line.Text == "译文")
            || LrcPresentation.Project(recovered).Count != 1)
        {
            throw new InvalidOperationException(
                "QQ nested lyric response lost translated lyrics or line-level ruby.");
        }
        LrcDocument persisted = LrcDocument.Parse(recovered.ToString());
        if (persisted.Lines[0].Romanization != "o ri ji na ru"
            || persisted.ToStandardLrcString().Contains("ml-romaji",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "QQ romaji should survive the canonical cache without leaking into output LRC.");
        }

        // A QQ Lite QRC response can contain only the original while
        // the legacy SongMID endpoint retains romaji and translation.
        // The merged result must keep word timings and order stable.
        const string baseQrc = """
        <QrcInfos><LyricInfo><Lyric_1 LyricContent="[1000,1300]晴(1000,650)天(1650,650)"
        /></LyricInfo></QrcInfos>
        """;
        string modern = BuildQqFixture(baseQrc, "", "");
        string legacy = BuildQqFixture(baseQrc,
            "[00:01.000]Sunny day", "[00:01.000]qing tian");
        LrcDocument? modernDocument =
            CloudLyricsClient.ParseQqLyrics(modern);
        LrcDocument? legacyDocument =
            CloudLyricsClient.ParseQqLyrics(legacy);
        if (modernDocument is null || legacyDocument is null)
            throw new InvalidOperationException(
                "QQ test fixture should contain two parsed lyric records.");
        LrcDocument enriched = CloudLyricsClient.MergeQqSupplementalLyrics(
            modernDocument, legacyDocument);
        LrcPresentationLine[] enrichedLines =
            LrcPresentation.Project(enriched).ToArray();
        if (enrichedLines.Length != 1
            || enrichedLines[0].Original != "晴天"
            || enrichedLines[0].Translation != "Sunny day"
            || enrichedLines[0].Romanization != "qing tian"
            || enrichedLines[0].OriginalWords is not { Count: 2 })
        {
            throw new InvalidOperationException(
                "QQ legacy supplementation dropped the translation, romanization or word timings.");
        }

        // The QRC service strips whitespace while the legacy MID
        // endpoint may retain spaces and Japanese punctuation. They still
        // represent the SAME timestamped lyric, not different songs.
        string spacedLegacy = BuildQqFixture(
            "[00:01.080]晴 天！",
            "[00:01.080]Fine weather",
            "[00:01.080]qing tian");
        LrcDocument? spacedDocument =
            CloudLyricsClient.ParseQqLyrics(spacedLegacy);
        if (spacedDocument is null)
            throw new InvalidOperationException(
                "QQ spaced legacy fixture failed to parse.");
        LrcDocument normalizedSupplement =
            CloudLyricsClient.MergeQqSupplementalLyrics(
                modernDocument, spacedDocument);
        LrcPresentationLine[] normalizedLines =
            LrcPresentation.Project(normalizedSupplement).ToArray();
        if (normalizedLines.Length != 1
            || normalizedLines[0].Original != "晴天"
            || normalizedLines[0].Translation != "Fine weather"
            || normalizedLines[0].Romanization != "qing tian"
            || normalizedLines[0].OriginalWords is not { Count: 2 })
        {
            throw new InvalidOperationException(
                "QQ spaced/full-width-punctuation legacy lyrics were not joined to the original QRC line.");
        }

        var kgLyrics = CloudLyricsClient.ParseKugouLyrics(
            "{\"content\":\"" + base64 + "\"}");
        if (kgLyrics?.Lines.Count != 1 || kgLyrics.Lines[0].Text != "Original")
        {
            throw new InvalidOperationException("KuGou lyric base64 was not parsed.");
        }
    }
    private static string BuildQqFixture(string original,
        string translated, string romanized)
    {
        using MemoryStream buffer = new();
        using (System.Text.Json.Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("lyric", original);
            writer.WriteString("trans", translated);
            writer.WriteString("roma", romanized);
            writer.WriteEndObject();
            writer.Flush();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
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
