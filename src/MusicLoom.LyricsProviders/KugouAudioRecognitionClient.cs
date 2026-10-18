using System.Globalization;
using System.Net.Http.Headers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MusicLoom.LyricsProviders;

/// <summary>
/// Experimental, user-triggered KuGou fingerprint recognition over
/// 8 kHz mono signed 16-bit little-endian PCM. Based on independent
/// community implementations (MakcRe/KuGouMusicApi), not a paid SDK.
/// No audio is sent unless the caller explicitly invokes RecognizeAsync.
/// </summary>
public sealed record KugouRecognitionCandidate(
    string Title,
    string Artist,
    string Album,
    double Confidence,
    string? AudioHash);

public sealed class KugouAudioRecognitionClient : IDisposable
{
    private const string StandardClientSalt = "OIlwieks28dk2k092lksi2UIkp";
    private const string NativeFingerprintPath =
        "/fingerprint.service/v1/music_trackid_mulit";
    private const string NativeGateway = "https://gateway.kugou.com";
    // The original tracker.kugou.com implementation can return 404.
    // KuGou's own gateway is used by current upstream client reversals.
    private static readonly string DeviceMid = CalculateNativeMid("-");
    private static readonly string DeviceUuid = Convert.ToHexString(
        MD5.HashData(Encoding.UTF8.GetBytes("-" + DeviceMid)))
        .ToLowerInvariant();
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public KugouAudioRecognitionClient(HttpClient? http = null)
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
    }

    public async Task<IReadOnlyList<KugouRecognitionCandidate>> RecognizeAsync(
        ReadOnlyMemory<byte> pcm,
        CancellationToken ct = default)
    {
        // Require at least two seconds, cap uploads at a ten-second sample.
        if (pcm.Length < 8_000 * 2 * 2
            || pcm.Length > 8_000 * 2 * 10
            || pcm.Length % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pcm),
                "Recognition requires 2–10 seconds of 8 kHz mono PCM16.");
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long millis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string mid = DeviceMid;

        Dictionary<string, string> request = new(StringComparer.Ordinal)
        {
            ["dfid"] = "-",
            ["mid"] = mid,
            ["uuid"] = DeviceUuid,
            ["appid"] = "1005",
            ["clientver"] = "20489",
            ["clienttime"] = now.ToString(CultureInfo.InvariantCulture),
            ["fpid"] = millis.ToString(CultureInfo.InvariantCulture),
            ["area_code"] = "1",
            ["include_unpublish"] = "1",
            ["useid"] = "0",
            ["multi_result"] = "1",
        };

        request["signature"] = SignAndroidPcm(request, pcm.Span);
        string url = CloudLyricsClient.BuildKugouUrl(
            NativeGateway + NativeFingerprintPath, request);

        using HttpRequestMessage message = new(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(pcm.ToArray()),
        };
        message.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");
        message.Headers.TryAddWithoutValidation(
            "User-Agent", "KuGou/11490 (Android)");
        message.Headers.TryAddWithoutValidation("dfid", "-");
        message.Headers.TryAddWithoutValidation("mid", mid);
        message.Headers.TryAddWithoutValidation(
            "clienttime", request["clienttime"]);
        message.Headers.TryAddWithoutValidation("kg-rec", "1");
        message.Headers.TryAddWithoutValidation("kg-rc", "1");
        message.Headers.TryAddWithoutValidation("kg-thash", "5d816a0");
        message.Headers.TryAddWithoutValidation("kg-rf",
            "B9EDA08A64250DEFFBCADDEE00F8F25F");

        using HttpResponseMessage response =
            await _http.SendAsync(message,
                HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"KuGou native recognition gateway returned {(int)response.StatusCode} ({response.ReasonPhrase}).",
                inner: null, statusCode: response.StatusCode);
        }
        string json = await response.Content.ReadAsStringAsync(ct);
        return ParseResults(json);
    }

    internal static string CalculateNativeMid(string dfid)
    {
        string hex = Convert.ToHexString(MD5.HashData(
            Encoding.UTF8.GetBytes(dfid)));
        // KuGou's official Android client serializes the unsigned 128-bit
        // MD5 as decimal, not as a randomly generated hexadecimal MID.
        return BigInteger.Parse(
            "0" + hex,
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture)
            .ToString(CultureInfo.InvariantCulture);
    }

    internal static string SignAndroidPcm(
        IReadOnlyDictionary<string, string> options,
        ReadOnlySpan<byte> pcm)
    {
        string normalized = string.Concat(options
            .Where(static entry => entry.Key != "signature")
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .Select(static entry => entry.Key + "=" + entry.Value));
        using IncrementalHash md5 =
            IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(Encoding.UTF8.GetBytes(
            StandardClientSalt + normalized));
        md5.AppendData(pcm);
        md5.AppendData(Encoding.UTF8.GetBytes(StandardClientSalt));
        return Convert.ToHexString(md5.GetHashAndReset())
            .ToLowerInvariant();
    }

    internal static IReadOnlyList<KugouRecognitionCandidate>
        ParseResults(string json)
    {
        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement root = parsed.RootElement;
        if (!root.TryGetProperty("status", out JsonElement status)
            || status.ValueKind != JsonValueKind.Number
            || status.GetInt32() != 1
            || !root.TryGetProperty("data", out JsonElement results))
        {
            return [];
        }

        // Some native versions nest candidates under data.list or
        // data.songs. Do not interpret successful nested responses as empty.
        if (results.ValueKind == JsonValueKind.Object)
        {
            if (results.TryGetProperty("list", out JsonElement list))
                results = list;
            else if (results.TryGetProperty("songs", out JsonElement nestedSongs))
                results = nestedSongs;
        }
        if (results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<KugouRecognitionCandidate> songs = [];
        foreach (JsonElement entry in results.EnumerateArray())
        {
            string title = Text(entry, "songname");
            if (title.Length == 0) title = Text(entry, "song_name");
            string artist = Text(entry, "singername");
            if (artist.Length == 0) artist = Text(entry, "singer_name");
            if (title.Length == 0)
            {
                continue;
            }

            string album = "";
            if (entry.TryGetProperty("album", out JsonElement albums)
                && albums.ValueKind == JsonValueKind.Array
                && albums.GetArrayLength() > 0)
            {
                album = Text(albums[0], "albumname");
                if (album.Length == 0) album = Text(albums[0], "album_name");
            }

            double confidence = 0;
            if (entry.TryGetProperty("dist", out JsonElement dist))
            {
                if (dist.ValueKind == JsonValueKind.Number)
                {
                    confidence = dist.GetDouble();
                }
                else if (dist.ValueKind == JsonValueKind.String)
                {
                    double.TryParse(dist.GetString(),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out confidence);
                }
            }

            songs.Add(new KugouRecognitionCandidate(
                title,
                artist,
                album,
                double.IsFinite(confidence)
                    ? Math.Clamp(confidence, 0, 1) : 0,
                Text(entry, "hash").Length > 0
                    ? Text(entry, "hash")
                    : Text(entry, "hash_128")));
        }
        return songs;
    }

    private static string Text(JsonElement json, string key) =>
        json.ValueKind == JsonValueKind.Object
        && json.TryGetProperty(key, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
