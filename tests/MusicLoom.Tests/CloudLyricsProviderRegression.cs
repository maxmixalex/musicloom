using System.Text;
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
}
