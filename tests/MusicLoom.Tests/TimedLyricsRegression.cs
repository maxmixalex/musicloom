using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MusicLoom.Core.Lyrics;
using MusicLoom.LyricsProviders;

internal static class TimedLyricsRegression
{
    private const string QrcXml =
        "<QrcInfos><LyricInfo><Lyric_1 LyricType=\"1\" LyricContent=\""
        + "[ar:Artist]\n"
        + "[500,900]Hel(500,400)lo(900,500)\n"
        + "[1900,900]World(1900,900)"
        + "\" /></LyricInfo></QrcInfos>";

    private const string KrcText =
        "[ar:Artist]\n"
        + "[500,900]<0,400,0>Hel<400,500,0>lo\n"
        + "[1900,900]<0,900,0>World";

    private static readonly byte[] KrcKey =
    [
        0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47,
        0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69,
    ];

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        VerifyQrcWordTiming();
        VerifyQqRubyAnnotation();
        VerifyRomanizationExportOptIn();
        VerifyUnequalRubySyllables();
        VerifyKrcWordTiming();
        await VerifyUnofficialTransportAsync();
    }

    private static void VerifyQrcWordTiming()
    {
        string ciphertext = BuildQrcCipher(QrcXml);
        string decrypted = TimedLyricsCodec.DecryptQrc(ciphertext);
        Assert(decrypted == QrcXml, "QRC standard 3DES/zlib roundtrip failed.");
        string qqCompat = BuildBuggyQrcCipher(QrcXml);
        Assert(TimedLyricsCodec.DecryptQrc(qqCompat) == QrcXml,
            "Real QQ Music's historical DES S-box variant must decrypt QRC.");

        LrcDocument doc = TimedLyricsCodec.ParseQrc(decrypted);
        Assert(doc.Lines.Count == 2, "QRC line count wrong.");
        Assert(doc.Lines[0].Text == "Hello", "QRC plain line lost word text.");
        Assert(doc.Lines[0].Words is { Count: 2 },
            "QRC word segmentation was lost.");
        Assert(doc.Lines[0].Words![0].Start ==
                TimeSpan.FromMilliseconds(500),
            "QRC word starts must be absolute, not line-relative.");
        Assert(doc.Lines[0].Words![1].End ==
                TimeSpan.FromMilliseconds(1400),
            "QRC word duration was lost.");

        string enhanced = doc.ToString();
        Assert(enhanced.Contains("<00:00.500>Hel<00:00.900>lo<00:01.400>"),
            "Enhanced LRC did not serialize exact word markers.");

        LrcDocument restored = LrcDocument.Parse(enhanced);
        Assert(restored.Lines[0].Text == "Hello"
            && restored.Lines[0].Words is { Count: 2 },
            "Persisted enhanced LRC must roundtrip with words.");

        LrcDocument shifted = LrcDocument.Shift(restored, 300);
        Assert(shifted.Lines[0].Words![0].Start ==
                TimeSpan.FromMilliseconds(800),
            "Offset must shift individual words, too.");
        Assert(restored.Lines[0].Words![0].Start ==
                TimeSpan.FromMilliseconds(500),
            "Output-only offset must not modify canonical words.");
    }

    private static void VerifyQqRubyAnnotation()
    {
        string qrcRoma = "<QrcInfos><LyricInfo><Lyric_1 LyricType=\"1\" LyricContent=\""
            + "[500,900]he(500,400)rō(900,500)\n"
            + "[1900,900]world(1900,900)"
            + "\" /></LyricInfo></QrcInfos>";
        using MemoryStream responseData = new();
        using (Utf8JsonWriter writer = new(responseData))
        {
            writer.WriteStartObject();
            writer.WriteString("lyric", QrcXml);
            writer.WriteString("roma", qrcRoma);
            writer.WriteString("trans", "");
            writer.WriteEndObject();
            writer.Flush();
        }
        string response = Encoding.UTF8.GetString(responseData.ToArray());
        LrcDocument? merged = CloudLyricsClient.ParseQqLyrics(response);
        Assert(merged?.Lines.Count == 2
            && merged.Lines[0].Words is { Count: 2 } words
            && words[0].Ruby == "he" && words[1].Ruby == "rō",
            "QQ's romaji QRC should align to the original words, not appear as a third lyric row.");
        LrcDocument roundtrip = LrcDocument.Parse(merged!.ToString());
        Assert(roundtrip.Lines[0].Words![1].Ruby == "rō"
            && roundtrip.Lines[1].Words![0].Ruby == "world",
            "QQ roma annotations must survive the canonical enhanced-LRC cache.");
        string exported = roundtrip.ToStandardLrcString();
        LrcDocument output = LrcDocument.Parse(exported);
        Assert(output.Lines.Count == 2
            && output.Lines[0].Text == "Hello"
            && output.Lines[0].Words is { Count: 2 } exportedWords
            && exportedWords.All(static word => word.Ruby is null)
            && !exported.Contains("[ml-ruby:", StringComparison.Ordinal),
            "Export must preserve word-timed lyric text without private ruby annotations.");
    }

    private static void VerifyRomanizationExportOptIn()
    {
        LrcDocument original = LrcDocument.Parse(
            "[00:01.000]<00:01.000>君<00:01.300>が<00:01.800>");
        LrcWord[] words = original.Lines[0].Words!
            .Select((word, index) => word with
                { Ruby = index == 0 ? "kimi" : "ga" }).ToArray();
        original.Lines[0] = original.Lines[0] with { Words = words };
        string normal = original.ForSynchronization(false).ToStandardLrcString();
        string selected = original.ForSynchronization(true).ToStandardLrcString();
        if (normal.Contains("kimi", StringComparison.Ordinal)
            || normal.Contains("ga", StringComparison.Ordinal)
            || !selected.Contains("[00:01.000]kimi ga",
                StringComparison.Ordinal)
            || selected.Contains("[ml-ruby:", StringComparison.Ordinal)
            || original.Lines.Count != 1)
            throw new InvalidOperationException(
                "The romanization export preference must default off and must never mutate canonical lyrics.");
        LrcDocument line = LrcDocument.Parse("[00:02.000]春");
        line.Lines[0] = line.Lines[0] with
            { Romanization = "haru" };
        if (!line.ForSynchronization(true).ToStandardLrcString()
                .Contains("[00:02.000]haru", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Plain LRC romanization must be included when the user opts in.");
    }

    private static void VerifyUnequalRubySyllables()
    {
        LrcWord[] originals =
        [
            new(TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(900), "Hel"),
            new(TimeSpan.FromMilliseconds(900),
                TimeSpan.FromMilliseconds(1400), "lo"),
        ];
        LrcWord[] romaji =
        [
            new(TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(680), "he"),
            new(TimeSpan.FromMilliseconds(680),
                TimeSpan.FromMilliseconds(900), "r"),
            new(TimeSpan.FromMilliseconds(900),
                TimeSpan.FromMilliseconds(1400), "ō"),
        ];
        IReadOnlyList<LrcWord>? result =
            RubyWordAlignment.Attach(originals, romaji);
        Assert(result is { Count: 2 }
            && result[0].Ruby == "her"
            && result[1].Ruby == "ō",
            "Unequal original/romanization syllable counts must use word timestamps, not drop ruby.");
        Assert(originals.All(static word => word.Ruby is null),
            "Ruby alignment must not mutate the source word timings or annotations.");

        LrcWord[] unrelated =
        [
            new(TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(11), "different"),
            new(TimeSpan.FromSeconds(12),
                TimeSpan.FromSeconds(13), "song"),
            new(TimeSpan.FromSeconds(14),
                TimeSpan.FromSeconds(15), "line"),
        ];
        Assert(RubyWordAlignment.Attach(originals, unrelated) is null,
            "Unrelated romanization must not be attached across distant timestamps.");
    }

    private static void VerifyKrcWordTiming()
    {
        string romanization = """
        {"content":[{"type":0,"lyricContent":[["He","rō"],["Wōrudo"]]},
                    {"type":1,"lyricContent":[["你好"],["世界"]]}]}
        """;
        string input = "[language:"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(romanization))
            + "]\n" + KrcText;

        byte[] encrypted = BuildKrcCipher(input);
        string actual = TimedLyricsCodec.DecryptKrc(encrypted);
        Assert(actual == input, "KuGou XOR/zlib roundtrip failed.");

        LrcDocument decoded = TimedLyricsCodec.ParseKrc(actual);
        Assert(decoded.Lines.Count == 4,
            "KRC must keep two originals and two translations; romanization belongs in ruby, not extra lyric lines.");
        LrcLine original = decoded.Lines.First(
            line => line.Words is { Count: > 0 });
        Assert(original.Text == "Hello"
            && original.Words![0].Start ==
                TimeSpan.FromMilliseconds(500)
            && original.Words![1].Start ==
                TimeSpan.FromMilliseconds(900),
            "KuGou word times must be relative to line start.");
        Assert(decoded.Lines.Any(line => line.Text == "你好"),
            "KRC line translation is missing.");
        Assert(original.Words![0].Ruby == "He"
            && original.Words![1].Ruby == "rō",
            "KRC romanization must align to the two original timed words.");
        Assert(decoded.Lines.Any(line => line.Text == "World"
            && line.Words is { Count: 1 } w && w[0].Ruby == "Wōrudo"),
            "The following KRC line must retain its own ruby annotation.");
        Assert(!decoded.Lines.Any(line => line.Text == "Herō"),
            "Romanization must not leak into a translated lyric row.");
        Assert(decoded.Metadata["ar"] == "Artist",
            "KRC album/artist tags should be preserved.");

        LrcDocument persistence = LrcDocument.Parse(decoded.ToString());
        Assert(persistence.Lines.Count == decoded.Lines.Count
            && persistence.Lines.First(line => line.Words is not null)
                .Words![1].End == TimeSpan.FromMilliseconds(1400)
            && persistence.Lines.First(line => line.Words is not null)
                .Words![1].Ruby == "rō",
            "KRC word times or ruby annotations were lost through canonical serialization.");
        Assert(!decoded.ToStandardLrcString().Contains("[ml-ruby:",
            StringComparison.Ordinal),
            "Private ruby metadata must never be written to destination LRC/audio tags.");
        Assert(LrcPresentation.Project(persistence).Count == 2,
            "Ruby annotations must not invent additional scrolled lyric rows.");

        bool rejected = false;
        try
        {
            TimedLyricsCodec.DecryptKrc("garbage"u8);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        Assert(rejected, "Malformed KRC must not be decoded as plaintext.");
    }

    private static async Task VerifyUnofficialTransportAsync()
    {
        string lyricKrc = Convert.ToBase64String(
            BuildKrcCipher(KrcText));
        string encryptedQrc = BuildBuggyQrcCipher(QrcXml);
        FakeLyricsTransport mock = new(encryptedQrc, lyricKrc);
        using HttpClient http = new(mock);
        using CloudLyricsClient client = new(http);

        IReadOnlyList<CloudLyricsCandidate> qq =
            await client.SearchAsync(CloudLyricsSource.QqMusic, "Artist Hello");
        Assert(qq.Count == 1 && qq[0].Id == "98123"
            && qq[0].SongMid == "mid-123",
            "QQ lightweight search must keep songID and MID distinct.");
        LrcDocument? qqDoc = await client.GetLyricsAsync(qq[0]);
        Assert(qqDoc?.Lines[0].Words is { Count: 2 },
            "QQ lyric request must decrypt QRC instead of LRC-only.");
        var qqRich = LrcPresentation.Project(qqDoc!);
        Assert(qqRich.First().Translation == "你好"
            && qqRich.First().Romanization == "he ro"
            && mock.QqLegacyRequests == 1,
            $"QQ must supplement missing translation and romaji from the legacy SongMID endpoint (translation={qqRich.First().Translation}, reading={qqRich.First().Romanization}, requests={mock.QqLegacyRequests}).");
        Assert(mock.QqSessions == 1,
            "Guest QQ session should be shared between search/lyrics.");
        Assert(mock.QqRequests == 3,
            "Expected QQ session, search and lyric requests.");

        IReadOnlyList<CloudLyricsCandidate> kg =
            await client.SearchAsync(CloudLyricsSource.KugouMusic, "Artist Hello");
        Assert(kg.Count == 1 && kg[0].Hash == "HASHABC",
            "Signed KuGou search parser lost hash.");
        LrcDocument? kgDoc = await client.GetLyricsAsync(kg[0]);
        Assert(kgDoc?.Lines.FirstOrDefault()?.Words is { Count: > 0 },
            "KuGou must request full encrypted KRC with word timing.");
        Assert(mock.KugouSearchCount == 1
            && mock.KugouDownloadCount == 1,
            "KuGou search + KRC download must use the signed client flow.");
        Assert(mock.MissingSignatures == 0,
            "Every KuGou client request needs a valid noncommercial signature.");
    }

    private static string BuildQrcCipher(string plaintext)
    {
        byte[] zlib = Deflate(plaintext);
        byte[] padded = new byte[(zlib.Length + 7) / 8 * 8];
        zlib.CopyTo(padded, 0);
        using TripleDES triple = TripleDES.Create();
        triple.Key = Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL");
        triple.Mode = CipherMode.ECB;
        return Convert.ToHexString(
            triple.EncryptEcb(padded, PaddingMode.None));
    }

    private static string BuildBuggyQrcCipher(string plaintext)
    {
        byte[] compressed = Deflate(plaintext);
        byte[] padded = new byte[(compressed.Length + 7) / 8 * 8];
        compressed.CopyTo(padded, 0);
        byte[][][] schedule = new byte[3][][];
        for (int k = 0; k < 3; k++)
        {
            schedule[k] = new byte[16][];
            for (int j = 0; j < 16; j++) schedule[k][j] = new byte[6];
        }
        QqMusicBuggyDes.TripleDESKeySetup(
            Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL"),
            schedule, QqMusicBuggyDes.ENCRYPT);
        byte[] encrypted = new byte[padded.Length];
        byte[] input = new byte[8];
        byte[] output = new byte[8];
        for (int i = 0; i < padded.Length; i += 8)
        {
            Buffer.BlockCopy(padded, i, input, 0, 8);
            QqMusicBuggyDes.TripleDESCrypt(input, output, schedule);
            Buffer.BlockCopy(output, 0, encrypted, i, 8);
        }
        return Convert.ToHexString(encrypted);
    }

    private static byte[] BuildKrcCipher(string plaintext)
    {
        byte[] compressed = Deflate(plaintext);
        for (int i = 0; i < compressed.Length; i++)
        {
            compressed[i] ^= KrcKey[i % KrcKey.Length];
        }
        byte[] result = new byte[compressed.Length + 4];
        "krc1"u8.CopyTo(result);
        compressed.CopyTo(result, 4);
        return result;
    }

    private static byte[] Deflate(string plaintext)
    {
        using MemoryStream stream = new();
        using (ZLibStream zipped =
            new(stream, CompressionLevel.Optimal, leaveOpen: true))
        {
            zipped.Write(Encoding.UTF8.GetBytes(plaintext));
        }
        return stream.ToArray();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeLyricsTransport : HttpMessageHandler
    {
        private readonly string _qrc;
        private readonly string _krc;

        public FakeLyricsTransport(string qrc, string krc)
        {
            _qrc = qrc;
            _krc = krc;
        }

        public int QqSessions { get; private set; }
        public int QqRequests { get; private set; }
        public int QqLegacyRequests { get; private set; }
        public int KugouSearchCount { get; private set; }
        public int KugouDownloadCount { get; private set; }
        public int MissingSignatures { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req,
            CancellationToken cancellationToken)
        {
            string request = req.Content is null ? ""
                : await req.Content.ReadAsStringAsync(cancellationToken);
            Uri path = req.RequestUri ??
                throw new InvalidOperationException("No request URI.");
            string payload;
            if (path.Host == "u.y.qq.com")
            {
                QqRequests++;
                if (request.Contains("\"GetSession\"", StringComparison.Ordinal))
                {
                    QqSessions++;
                    payload = """
                    {"code":0,"request":{"code":0,"data":{
                     "session":{"uid":"123","sid":"session-abc","userip":"127.0.0.1"}}}}
                    """;
                }
                else if (request.Contains("\"DoSearchForQQMusicLite\"",
                    StringComparison.Ordinal))
                {
                    Assert(request.Contains("session-abc", StringComparison.Ordinal),
                        "QQ client search must carry guest session.");
                    payload = """
                    {"code":0,"request":{"code":0,"data":{"body":{
                       "item_song":[{"id":98123,"mid":"mid-123","title":"Hello",
                       "singer":[{"name":"Artist"}],"album":{"name":"Album"},
                       "interval":120}]}}}}
                    """;
                }
                else if (request.Contains("\"GetPlayLyricInfo\"",
                    StringComparison.Ordinal))
                {
                    Assert(request.Contains("\"songID\":98123",
                        StringComparison.Ordinal),
                        "QRC must request numeric songID rather than MID.");
                    Assert(request.Contains("\"songMID\":\"mid-123\"",
                        StringComparison.Ordinal)
                        && request.Contains("\"type\":-1",
                            StringComparison.Ordinal)
                        && request.Contains("\"trans\":1",
                            StringComparison.Ordinal)
                        && request.Contains("\"roma\":1",
                            StringComparison.Ordinal),
                        "QQ lyric request must ask for all three QRC/translation/romanization channels and include the stable song MID.");
                    payload = "{\"code\":0,\"request\":{\"code\":0,"
                        + "\"data\":{\"qrc_t\":1,\"lyric\":\""
                        + _qrc + "\",\"trans\":\"\",\"roma\":\"\"}}}";
                }
                else
                {
                    throw new InvalidOperationException(
                        "Unexpected QQ Music request: " + request);
                }
            }
            else if (path.Host == "c.y.qq.com")
            {
                QqLegacyRequests++;
                string lyric = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    "[00:00.500]Hello" + Environment.NewLine
                    + "[00:01.900]World"));
                string trans = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    "[00:00.500]你好"));
                string roma = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    "[00:00.500]he ro"));
                payload = "{\"lyric\":\"" + lyric
                    + "\",\"trans\":\"" + trans
                    + "\",\"roma\":\"" + roma + "\"}";
            }
            else if (path.Host.Contains("kugou.com", StringComparison.Ordinal))
            {
                string[] parameters = path.Query.TrimStart('?')
                    .Split('&', StringSplitOptions.RemoveEmptyEntries);
                Dictionary<string, string> query = parameters
                    .Select(value => value.Split('=', 2))
                    .ToDictionary(
                        parts => Uri.UnescapeDataString(parts[0]),
                        parts => Uri.UnescapeDataString(parts.Length > 1
                            ? parts[1] : ""),
                        StringComparer.Ordinal);
                query.TryGetValue("signature", out string? actual);
                query.Remove("signature");
                if (!string.Equals(actual,
                    CloudLyricsClient.KugouSignature(query),
                    StringComparison.Ordinal))
                {
                    MissingSignatures++;
                }

                if (path.AbsolutePath.Contains("/v2/search/song",
                    StringComparison.Ordinal))
                {
                    KugouSearchCount++;
                    payload = """
                    {"error_code":0,"data":{"lists":[{
                      "ID":789,"FileHash":"HASHABC","SongName":"Hello",
                      "Singers":[{"name":"Artist"}],
                      "AlbumName":"Album","Duration":120}]}}
                    """;
                }
                else if (path.AbsolutePath.Contains("/v1/search",
                    StringComparison.Ordinal))
                {
                    payload = """
                    {"error_code":0,
                    "candidates":[{"id":"456","accesskey":"access-key"}]}
                    """;
                }
                else if (path.AbsolutePath.Contains("/download",
                    StringComparison.Ordinal))
                {
                    Assert(query["fmt"] == "krc",
                        "KuGou must download KRC, not plain LRC.");
                    KugouDownloadCount++;
                    payload = "{\"contenttype\":1,\"content\":\""
                        + _krc + "\"}";
                }
                else
                {
                    throw new InvalidOperationException(
                        "Unexpected KuGou endpoint " + path);
                }
            }
            else
            {
                throw new InvalidOperationException(
                    "Unexpected network host in offline test: " + path.Host);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
