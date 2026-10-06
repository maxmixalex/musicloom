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
        IReadOnlyList<NeteaseMatchCandidate> candidates =
            await MatchFingerprintCandidatesAsync(
                fingerprint,
                cancellationToken);

        return candidates.Count > 0
            ? candidates[0].TrackId
            : null;
    }

    public async Task<IReadOnlyList<NeteaseMatchCandidate>>
        MatchFingerprintCandidatesAsync(
            string fingerprint,
            CancellationToken cancellationToken = default)
    {
        string json = BuildMatchPayload(fingerprint);
        using JsonDocument document = await PostWeApiAsync(
            "/weapi/music/audio/match",
            json,
            cancellationToken);

        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("code", out JsonElement code)
            || code.GetInt32() != 200
            || !root.TryGetProperty("data", out JsonElement data)
            || !data.TryGetProperty("result", out JsonElement result)
            || result.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<NeteaseMatchCandidate>();
        }

        List<NeteaseMatchCandidate> candidates = [];
        foreach (JsonElement item in result.EnumerateArray())
        {
            if (!item.TryGetProperty(
                    "song",
                    out JsonElement song)
                || !song.TryGetProperty(
                    "id",
                    out JsonElement id)
                || !id.TryGetInt64(out long songId))
            {
                continue;
            }

            string name =
                ReadString(song, "name")
                ?? $"#{songId}";
            string artist =
                ReadArtists(song);
            string album =
                ReadAlbum(song);
            double? score =
                ReadDouble(item, "score")
                ?? ReadDouble(item, "confidence");

            candidates.Add(
                new NeteaseMatchCandidate(
                    songId,
                    name,
                    artist,
                    album,
                    score));
        }

        return candidates;
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
        FingerprintResult fingerprint =
            NeteaseFingerprint.Generate(samples.Span);
        return MatchFingerprintAsync(
            fingerprint.Encoded,
            cancellationToken);
    }

    public Task<IReadOnlyList<NeteaseMatchCandidate>>
        MatchSampleCandidatesAsync(
            ReadOnlyMemory<float> samples,
            CancellationToken cancellationToken = default)
    {
        FingerprintResult fingerprint =
            NeteaseFingerprint.Generate(samples.Span);
        return MatchFingerprintCandidatesAsync(
            fingerprint.Encoded,
            cancellationToken);
    }

    public async Task<long?> MatchFileAsync(
        IFingerprintPcmDecoder decoder,
        string audioFilePath,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NeteaseMatchCandidate> candidates =
            await MatchFileCandidatesAsync(
                decoder,
                audioFilePath,
                cancellationToken);

        return candidates.Count > 0
            ? candidates[0].TrackId
            : null;
    }

    public async Task<IReadOnlyList<NeteaseMatchCandidate>>
        MatchFileCandidatesAsync(
            IFingerprintPcmDecoder decoder,
            string audioFilePath,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoder);

        float[]? samples = await decoder.DecodeAsync(
            audioFilePath,
            cancellationToken);
        if (samples is null)
        {
            return Array.Empty<NeteaseMatchCandidate>();
        }

        return await MatchSampleCandidatesAsync(
            samples,
            cancellationToken);
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

    private static string? ReadString(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(
                    propertyName,
                    out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static double? ReadDouble(
        JsonElement element,
        string propertyName)
    {
        return element.TryGetProperty(
                    propertyName,
                    out JsonElement property)
            && property.TryGetDouble(out double value)
                ? value
                : null;
    }

    private static string ReadArtists(JsonElement song)
    {
        JsonElement artists;
        if (!(song.TryGetProperty("artists", out artists)
            || song.TryGetProperty("ar", out artists))
            || artists.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            ";",
            artists
                .EnumerateArray()
                .Select(static artist =>
                    artist.TryGetProperty(
                            "name",
                            out JsonElement name)
                        && name.ValueKind == JsonValueKind.String
                            ? name.GetString()
                            : null)
                .Where(static name =>
                    !string.IsNullOrWhiteSpace(name)));
    }

    private static string ReadAlbum(JsonElement song)
    {
        JsonElement album;
        if (!(song.TryGetProperty("album", out album)
            || song.TryGetProperty("al", out album))
            || album.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return ReadString(album, "name")
            ?? string.Empty;
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
