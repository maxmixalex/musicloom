using System.Net;
using System.Text;
using MusicLoom.AudioFingerprint;
using MusicLoom.Netease;
using MusicLoom.Core.Lyrics;

internal static class NeteaseClientRegression
{
    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        CapturingHandler handler = new();
        using HttpClient httpClient = new(handler);
        using NeteaseClient client = new(httpClient);

        FixedDecoder decoder = new();
        long? songId = await client.MatchFileAsync(decoder, "fixture.wav");

        Equal(1, decoder.CallCount);
        Equal(123_456_789L, songId ?? -1);

        IReadOnlyList<NeteaseMatchCandidate> candidates =
            await client.MatchFileCandidatesAsync(
                decoder,
                "fixture.wav");

        Equal(2, decoder.CallCount);
        Equal(2, candidates.Count);
        Equal(123_456_789L, candidates[0].TrackId);
        Equal("First Song", candidates[0].Name);
        Equal("Artist A;Artist B", candidates[0].Artist);
        Equal("Album One", candidates[0].Album);
        Equal(987_654_321L, candidates[1].TrackId);

        Uri requestUri = handler.RequestUri
            ?? throw new InvalidOperationException("No WEAPI request was captured.");
        Equal("/weapi/music/audio/match", requestUri.AbsolutePath);
        Equal("?csrf_token=", requestUri.Query);
        Equal(HttpMethod.Post, handler.Method ?? HttpMethod.Get);

        string body = handler.Body
            ?? throw new InvalidOperationException("WEAPI form body was not captured.");
        if (!body.Contains("params=", StringComparison.Ordinal)
            || !body.Contains("encSecKey=", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("WEAPI form is missing params or encSecKey.");
        }

        string cookie = handler.Cookie
            ?? throw new InvalidOperationException("WEAPI cookie header was not captured.");
        foreach (string part in new[] { "WEVNSM=1.0.0", "sDeviceId=unknown-", "WNMCID=" })
        {
            if (!cookie.Contains(part, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Cookie is missing '{part}'.");
            }
        }

        IReadOnlyList<NeteaseMatchCandidate> search =
            await client.SearchSongsAsync(
                "Search Song",
                "Search Artist",
                "Search Album");

        Equal(1, search.Count);
        Equal(246_810_121L, search[0].TrackId);
        Equal("Search Song", search[0].Name);
        Equal("Search Artist", search[0].Artist);
        Equal("Search Album", search[0].Album);
        Equal(
            "/api/cloudsearch/pc",
            handler.RequestUri?.AbsolutePath ?? string.Empty);
        if (handler.Body is null
            || !handler.Body.Contains(
                "Search+Artist+Search+Song+Search+Album",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "NetEase text search did not send the expected query.");
        }

        LrcDocument? downloaded = await client.GetLyricsAsync(123456789);
        if (downloaded is null
            || downloaded.Lines.Count != 4
            || downloaded.Lines[0].Text != "流れる星"
            || downloaded.Lines[0].Romanization != "na ga re ru ho shi"
            || downloaded.Lines[1].Text != "流星"
            || downloaded.Lines[1].Romanization is not null)
        {
            throw new InvalidOperationException(
                "NetEase romalrc must annotate the original, not pollute the translation.");
        }
        LrcDocument persisted = LrcDocument.Parse(downloaded.ToString());
        if (LrcPresentation.Project(persisted)[0].Romanization
                != "na ga re ru ho shi"
            || persisted.ToStandardLrcString().Contains("ml-romaji",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "NetEase romalrc was lost in the canonical cache or leaked into ordinary LRC.");
        }
    }

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
        }
    }

    private sealed class FixedDecoder : IFingerprintPcmDecoder
    {
        public int CallCount { get; private set; }

        public Task<float[]?> DecodeAsync(
            string audioFilePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;

            float[] samples = new float[NeteaseFingerprint.RequiredSampleCount];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (((i * 73) % 65_536) - 32_768) / 32_768f;
            }

            return Task.FromResult<float[]?>(samples);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }
        public string? Cookie { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Cookie = request.Headers.TryGetValues("Cookie", out IEnumerable<string>? values)
                ? string.Join("; ", values)
                : null;

            string json =
                request.RequestUri?.AbsolutePath == "/weapi/song/lyric"
                    ? "{\"code\":200,\"lrc\":{\"lyric\":\"[00:01.000]流れる星\\n[00:02.000]君の空\"},\"tlyric\":{\"lyric\":\"[00:01.000]流星\\n[00:02.000]你的天空\"},\"romalrc\":{\"lyric\":\"[00:01.000]na ga re ru ho shi\\n[00:02.000]ki mi no so ra\"}}"
                : request.RequestUri?.AbsolutePath == "/api/cloudsearch/pc"
                    ? "{\"code\":200,\"result\":{\"songs\":[{\"id\":246810121,\"name\":\"Search Song\",\"ar\":[{\"name\":\"Search Artist\"}],\"al\":{\"name\":\"Search Album\"}}]}}"
                    : "{\"code\":200,\"data\":{\"result\":[" +
                      "{\"score\":0.98,\"song\":{\"id\":123456789,\"name\":\"First Song\",\"artists\":[{\"name\":\"Artist A\"},{\"name\":\"Artist B\"}],\"album\":{\"name\":\"Album One\"}}}," +
                      "{\"song\":{\"id\":987654321,\"name\":\"Second Song\",\"ar\":[{\"name\":\"Artist C\"}],\"al\":{\"name\":\"Album Two\"}}}" +
                      "]}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
