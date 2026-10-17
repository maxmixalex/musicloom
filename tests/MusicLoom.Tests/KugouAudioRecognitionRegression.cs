using System.Net;
using System.Net.Http;
using System.Text;
using MusicLoom.LyricsProviders;

internal static class KugouAudioRecognitionRegression
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        const string fixture = """
        {"status":1,"data":[
        {"songname":"Song","singername":"Artist","album":[{"albumname":"Album"}],
         "hash":"audio-hash","dist":"0.923"},
        {"songname":"Runner-up","singername":"Other","dist":0.8}]}
        """;
        IReadOnlyList<KugouRecognitionCandidate> parsed =
            KugouAudioRecognitionClient.ParseResults(fixture);
        if (parsed.Count != 2
            || parsed[0].Title != "Song"
            || parsed[0].Album != "Album"
            || Math.Abs(parsed[0].Confidence - 0.923) > 0.00001)
        {
            throw new InvalidOperationException(
                "KuGou fingerprint result parser failed.");
        }

        FakeTransport handler = new();
        using HttpClient http = new(handler);
        using KugouAudioRecognitionClient client = new(http);
        byte[] pcm = new byte[8_000 * 2 * 3];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (byte)(i * 73 + 17);
        }

        IReadOnlyList<KugouRecognitionCandidate> results =
            await client.RecognizeAsync(pcm);
        if (handler.Requests != 1 || results.Count != 1
            || results[0].Artist != "Found Artist")
        {
            throw new InvalidOperationException(
                "Unofficial KuGou recognition flow was not signed correctly.");
        }

        bool rejected = false;
        try
        {
            await client.RecognizeAsync(new byte[128]);
        }
        catch (ArgumentOutOfRangeException)
        {
            rejected = true;
        }
        if (!rejected || handler.Requests != 1)
        {
            throw new InvalidOperationException(
                "Undersized audio must never reach the remote endpoint.");
        }
    }

    private sealed class FakeTransport : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct)
        {
            Requests++;
            if (request.Method != HttpMethod.Post
                || request.RequestUri?.Host != "gateway.kugou.com"
                || request.RequestUri.AbsolutePath
                    != "/fingerprint.service/v1/music_trackid_mulit"
                || request.Content?.Headers.ContentType?.MediaType
                    != "application/octet-stream")
            {
                throw new InvalidOperationException(
                    "Unexpected KuGou experimental fingerprint endpoint.");
            }

            byte[] body = await request.Content.ReadAsByteArrayAsync(ct);
            Dictionary<string, string> fields =
                request.RequestUri.Query.TrimStart('?')
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(static segment => segment.Split('=', 2))
                    .ToDictionary(
                        pair => Uri.UnescapeDataString(pair[0]),
                        pair => Uri.UnescapeDataString(pair[1]),
                        StringComparer.Ordinal);
            fields.TryGetValue("signature", out string? received);
            fields.Remove("signature");

            string expected = KugouAudioRecognitionClient.SignAndroidPcm(
                fields, body);
            if (!string.Equals(received, expected,
                StringComparison.Ordinal)
                || fields["mid"] !=
                    KugouAudioRecognitionClient.CalculateNativeMid("-")
                || fields["uuid"] != Convert.ToHexString(
                    System.Security.Cryptography.MD5.HashData(
                        Encoding.UTF8.GetBytes("-" + fields["mid"])))
                    .ToLowerInvariant()
                || fields["appid"] != "1005"
                || fields["clientver"] != "20489"
                || fields["multi_result"] != "1"
                || fields["fpid"].Length == 0
                || body.Length != 8_000 * 2 * 3)
            {
                throw new InvalidOperationException(
                    "PCM content, signed parameters or device fields mismatched.");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":1,"data":[{"songname":"Found Track","singername":"Found Artist","dist":0.9}]}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
