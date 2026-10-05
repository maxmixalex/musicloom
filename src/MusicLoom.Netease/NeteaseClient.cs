using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MusicLoom.AudioFingerprint;
using MusicLoom.Core.Lyrics;

namespace MusicLoom.Netease;

public sealed class NeteaseClient : IDisposable
{
    private const string Host = "https://music.163.com";
    private const string CsrfToken = "";
    private static readonly MediaTypeWithQualityHeaderValue JsonMediaType = new("application/json");
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _sessionId = WeApiCrypto.RandomString(16);
    private readonly string _cookieHeader;

    public NeteaseClient(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
        _cookieHeader = $"WEVNSM=1.0.0; sDeviceId=unknown-{Random.Shared.Next(1_000_000)}; WNMCID={BuildWnmcid()}";
    }

    public async Task<long?> MatchFingerprintAsync(
        string fingerprint,
        CancellationToken cancellationToken = default)
    {
        string json = BuildMatchPayload(fingerprint);
        using JsonDocument document = await PostWeApiAsync(
            "/weapi/music/audio/match",
            json,
            cancellationToken);

        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("code", out JsonElement code) || code.GetInt32() != 200)
        {
            return null;
        }

        if (!root.TryGetProperty("data", out JsonElement data)
            || !data.TryGetProperty("result", out JsonElement result)
            || result.ValueKind != JsonValueKind.Array
            || result.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement first = result[0];
        if (!first.TryGetProperty("song", out JsonElement song)
            || !song.TryGetProperty("id", out JsonElement id)
            || !id.TryGetInt64(out long songId))
        {
            return null;
        }

        return songId;
    }

    public async Task<LrcDocument?> GetLyricsAsync(
        long songId,
        CancellationToken cancellationToken = default)
    {
        string json = BuildLyricsPayload(songId);
        using JsonDocument document = await PostWeApiAsync(
            "/weapi/song/lyric",
            json,
            cancellationToken);

        string? originalText = ReadNestedString(document.RootElement, "lrc", "lyric");
        string? translatedText = ReadNestedString(document.RootElement, "tlyric", "lyric");
        if (string.IsNullOrEmpty(originalText) && string.IsNullOrEmpty(translatedText))
        {
            return null;
        }

        LrcDocument original = LrcDocument.Parse(originalText);
        LrcDocument translated = LrcDocument.Parse(translatedText);
        if (string.IsNullOrEmpty(translatedText))
        {
            return original;
        }

        if (string.IsNullOrEmpty(originalText))
        {
            return translated;
        }

        return LrcDocument.Merge(original, translated);
    }

    public Task<long?> MatchSamplesAsync(
        ReadOnlyMemory<float> samples,
        CancellationToken cancellationToken = default)
    {
        FingerprintResult fingerprint = NeteaseFingerprint.Generate(samples.Span);
        return MatchFingerprintAsync(fingerprint.Encoded, cancellationToken);
    }

    public async Task<long?> MatchFileAsync(
        IFingerprintPcmDecoder decoder,
        string audioFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoder);

        float[]? samples = await decoder.DecodeAsync(audioFilePath, cancellationToken);
        if (samples is null)
        {
            return null;
        }

        return await MatchSamplesAsync(samples, cancellationToken);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<JsonDocument> PostWeApiAsync(
        string path,
        string json,
        CancellationToken cancellationToken)
    {
        WeApiEnvelope envelope = WeApiCrypto.Encrypt(json);
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["params"] = envelope.Params,
            ["encSecKey"] = envelope.EncSecKey,
        });

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            Host + path + "?csrf_token=" + Uri.EscapeDataString(CsrfToken));
        request.Content = form;
        request.Headers.Referrer = new Uri(Host);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/MusicLoom");
        request.Headers.TryAddWithoutValidation("Cookie", _cookieHeader);
        request.Headers.Accept.Add(JsonMediaType);

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
    }

    private string BuildMatchPayload(string fingerprint)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("algorithmCode", "shazam_v2");
            writer.WriteString("sessionId", _sessionId);
            writer.WriteNumber("duration", NeteaseFingerprint.DurationSeconds);
            writer.WriteString("from", "recognize-song");
            writer.WriteString("times", "1");
            writer.WriteString("decrypt", "1");
            writer.WriteString("rawdata", fingerprint);
            writer.WriteString("csrf_token", CsrfToken);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string BuildLyricsPayload(long songId)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("id", songId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteString("lv", "-1");
            writer.WriteString("tv", "-1");
            writer.WriteString("rv", "-1");
            writer.WriteString("csrf_token", CsrfToken);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? ReadNestedString(JsonElement root, string objectName, string propertyName)
    {
        if (!root.TryGetProperty(objectName, out JsonElement container)
            || container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static string BuildWnmcid()
    {
        string prefix = string.Create(6, 0, static (span, _) =>
        {
            Span<byte> random = stackalloc byte[6];
            RandomNumberGenerator.Fill(random);
            for (int i = 0; i < span.Length; i++)
            {
                span[i] = (char)('a' + (random[i] % 26));
            }
        });

        return $"{prefix}.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.01.0";
    }
}
