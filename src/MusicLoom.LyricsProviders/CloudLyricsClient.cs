using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MusicLoom.Core.Lyrics;

namespace MusicLoom.LyricsProviders;

/// <summary>
/// Independent clients for non-NetEase lyric searches. No QQ/KuGou audio
/// fingerprint is presumed to exist; both use explicit metadata search.
/// Provider APIs are unofficial and can change without notice.
/// </summary>
public enum CloudLyricsSource
{
    QqMusic,
    KugouMusic,
}

public sealed record CloudLyricsCandidate(
    CloudLyricsSource Source,
    string Id,
    string Title,
    string Artist,
    string Album,
    int DurationMilliseconds,
    string? Hash = null,
    string? SongMid = null,
    string? CoverUrl = null,
    string? AlbumId = null,
    string? MediaMid = null);

public sealed partial class CloudLyricsClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private const string QqApi = "https://u.y.qq.com/cgi-bin/musicu.fcg";

    public CloudLyricsClient(HttpClient? http = null)
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public Task<IReadOnlyList<CloudLyricsCandidate>> SearchAsync(
        CloudLyricsSource source, string keywords,
        CancellationToken cancellationToken = default) =>
        source switch
        {
            CloudLyricsSource.QqMusic =>
                SearchQqAsync(keywords, cancellationToken),
            CloudLyricsSource.KugouMusic =>
                SearchKugouAsync(keywords, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

    public Task<LrcDocument?> GetLyricsAsync(
        CloudLyricsCandidate selected,
        CancellationToken cancellationToken = default) =>
        selected.Source switch
        {
            CloudLyricsSource.QqMusic =>
                GetQqLyricsAsync(selected, cancellationToken),
            CloudLyricsSource.KugouMusic =>
                GetKugouLyricsAsync(selected, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(selected)),
        };

    private async Task<IReadOnlyList<CloudLyricsCandidate>> SearchQqAsync(
        string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        // LDDC uses the lightweight QQ client search service after
        // obtaining a guest session. No paid API key is involved.
        long searchId =
            Random.Shared.NextInt64(1, 20) * 18014398509481984L
            + Random.Shared.NextInt64(0, 4_194_304) * 4294967296L
            + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 86_400_000;
        string parameters = "{\"search_id\":"
            + J(searchId.ToString(CultureInfo.InvariantCulture))
            + ",\"remoteplace\":\"search.android.keyboard\",\"query\":"
            + J(text.Trim())
            + ",\"search_type\":0,\"num_per_page\":20,\"page_num\":1,"
            + "\"highlight\":0,\"nqc_flag\":0,\"page_id\":1,\"grp\":1}";
        try
        {
            string json = await QqRequestAsync(
                "DoSearchForQQMusicLite",
                "music.search.SearchCgiService",
                parameters, ct);
            return ParseQqSearch(json);
        }
        catch (HttpRequestException)
        {
            // Read-only, anonymous QQ Web fallback; not a paid OpenAPI.
            return await SearchQqWebAsync(text, ct);
        }
        catch (InvalidDataException)
        {
            return await SearchQqWebAsync(text, ct);
        }
    }

    private async Task<IReadOnlyList<CloudLyricsCandidate>> SearchQqWebAsync(
        string text, CancellationToken ct)
    {
        // Desktop Web search remains the guest fallback when the lightweight
        // Android session handshake changes. Use this exact module key,
        // without an unrelated comm object, as expected by the Web gateway.
        string body = "{\"music.search.SearchCgiService\":{"
            + "\"module\":\"music.search.SearchCgiService\","
            + "\"method\":\"DoSearchForQQMusicDesktop\",\"param\":{"
            + "\"search_type\":0,\"query\":" + J(text.Trim())
            + ",\"page_num\":1,\"num_per_page\":20}}}";
        using HttpRequestMessage req = new(HttpMethod.Post, QqApi)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Referrer = new Uri("https://y.qq.com/");
        return ParseQqSearch(await GetBodyAsync(req, ct));
    }

    private async Task<IReadOnlyList<CloudLyricsCandidate>> SearchKugouAsync(
        string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            string response = await KugouRequestAsync(
                "https://complexsearch.kugou.com/v2/search/song",
                "SearchSong",
                new Dictionary<string, string>
                {
                    ["sorttype"] = "0",
                    ["keyword"] = text.Trim(),
                    ["pagesize"] = "20",
                    ["page"] = "1",
                },
                ct);
            return ParseKugouSearch(response);
        }
        catch (HttpRequestException)
        {
            // LDDC also falls back to the older unsigned search endpoint
            // when the newer signed service is unavailable.
            string url = "https://msearchcdn.kugou.com/api/v3/search/song"
                + "?version=9108&pagesize=20&page=1&keyword="
                + Uri.EscapeDataString(text.Trim());
            using HttpRequestMessage fallback = new(HttpMethod.Get, url);
            fallback.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            return ParseKugouSearch(await GetBodyAsync(fallback, ct));
        }
        catch (InvalidDataException)
        {
            string url = "https://msearchcdn.kugou.com/api/v3/search/song"
                + "?version=9108&pagesize=20&page=1&keyword="
                + Uri.EscapeDataString(text.Trim());
            using HttpRequestMessage fallback = new(HttpMethod.Get, url);
            fallback.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            return ParseKugouSearch(await GetBodyAsync(fallback, ct));
        }
    }

    private async Task<LrcDocument?> GetQqLyricsAsync(
        CloudLyricsCandidate track, CancellationToken ct)
    {
        if (!long.TryParse(track.Id, NumberStyles.None,
                CultureInfo.InvariantCulture, out long songId))
        {
            // Some results expose only MID. Do not treat MID as songID.
            return await GetQqLegacyLyricsAsync(track, ct);
        }

        string title = Convert.ToBase64String(Encoding.UTF8.GetBytes(track.Title));
        string artist = Convert.ToBase64String(Encoding.UTF8.GetBytes(track.Artist));
        string album = Convert.ToBase64String(Encoding.UTF8.GetBytes(track.Album));
        string parameters = $$"""
            {"albumName":{{J(album)}},"crypt":1,"ct":19,"cv":2111,"interval":{{Math.Max(0,track.DurationMilliseconds/1000)}},"lrc_t":0,"qrc":1,"qrc_t":0,"roma":1,"roma_t":0,"singerName":{{J(artist)}},"songID":{{songId}},"songName":{{J(title)}},"trans":1,"trans_t":0,"type":0}
            """;
        try
        {
            string response = await QqRequestAsync(
                "GetPlayLyricInfo",
                "music.musichallSong.PlayLyricInfo",
                parameters, ct);
            return ParseQqLyrics(response)
                ?? await GetQqLegacyLyricsAsync(track, ct);
        }
        catch (HttpRequestException)
        {
            return await GetQqLegacyLyricsAsync(track, ct);
        }
        catch (InvalidDataException)
        {
            return await GetQqLegacyLyricsAsync(track, ct);
        }
    }

    private async Task<LrcDocument?> GetQqLegacyLyricsAsync(
        CloudLyricsCandidate track, CancellationToken ct)
    {
        string mid = track.SongMid ?? track.Id;
        if (string.IsNullOrWhiteSpace(mid))
        {
            return null;
        }
        string uri =
            "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg"
            + "?format=json&nobase64=0&songmid="
            + Uri.EscapeDataString(mid)
            + "&g_tk=5381&loginUin=0&hostUin=0&inCharset=utf8"
            + "&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0";
        using HttpRequestMessage legacy = new(HttpMethod.Get, uri);
        legacy.Headers.Referrer = new Uri("https://y.qq.com/");
        return ParseQqLyrics(await GetBodyAsync(legacy, ct));
    }

    private async Task<LrcDocument?> GetKugouLyricsAsync(
        CloudLyricsCandidate track, CancellationToken ct)
    {
        string keyword = track.Artist + " - " + track.Title;
        string found = await KugouRequestAsync(
            "https://lyrics.kugou.com/v1/search",
            "Lyric",
            new Dictionary<string, string>
            {
                ["album_audio_id"] = track.Id,
                ["duration"] = track.DurationMilliseconds.ToString(
                    CultureInfo.InvariantCulture),
                ["hash"] = track.Hash ?? "",
                ["keyword"] = keyword,
                ["lrctxt"] = "1",
                ["man"] = "no",
            }, ct);
        (string id, string key)? token =
            ParseKugouLyricsCandidate(found);
        if (token is null)
        {
            return null;
        }

        // KRC provides precise word timing and aligned translations.
        string response = await KugouRequestAsync(
            "https://lyrics.kugou.com/download",
            "Lyric",
            new Dictionary<string, string>
            {
                ["accesskey"] = token.Value.key,
                ["charset"] = "utf8",
                ["client"] = "mobi",
                ["fmt"] = "krc",
                ["id"] = token.Value.id,
                ["ver"] = "1",
            }, ct);
        return ParseKugouLyrics(response);
    }

    private async Task<string> GetBodyAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    // All parsers are testable offline from representative JSON responses.
    internal static IReadOnlyList<CloudLyricsCandidate> ParseQqSearch(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement data;
        if (Find(root, "request", out _))
        {
            data = QqData(root);
        }
        else if (Find(root, "music.search.SearchCgiService",
                out JsonElement service)
            && Find(service, "data", out JsonElement serviceData))
        {
            data = serviceData;
        }
        else
        {
            return [];
        }

        if (!Find(data, "body", out JsonElement body))
        {
            return [];
        }

        JsonElement songs;
        if (!Find(body, "item_song", out songs)
            || songs.ValueKind != JsonValueKind.Array)
        {
            if (!Find(body, "song", out JsonElement nested)
                || !Find(nested, "list", out songs)
                || songs.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
        }

        List<CloudLyricsCandidate> entries = [];
        foreach (JsonElement song in songs.EnumerateArray())
        {
            string mid = Str(song, "mid", "songmid");
            string numeric = Str(song, "id", "songid");
            string id = numeric.Length != 0 ? numeric : mid;
            if (id.Length == 0)
            {
                continue;
            }

            string album = Find(song, "album", out JsonElement albumInfo)
                ? Str(albumInfo, "name") : Str(song, "albumname");
            string artist = SingerNames(song);
            string title = Str(song, "title", "name");
            int seconds = Int(song, "interval");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }
            // The Android Lite and desktop/Web search responses expose
            // different album MID / image field names.
            string? cover = QqArtworkUrl(song);
            string albumId = Find(song, "album", out JsonElement albumNode)
                ? ValidNumericFrom(albumNode, "id", "albumID", "albumId")
                : "";
            if (albumId.Length == 0)
            {
                albumId = ValidNumericFrom(song, "albumid", "albumId", "albumID");
            }
            entries.Add(new CloudLyricsCandidate(
                CloudLyricsSource.QqMusic, id, title, artist,
                album, checked(seconds * 1000), SongMid: mid,
                CoverUrl: cover, AlbumId: albumId,
                MediaMid: Find(song, "file", out JsonElement fileInfo)
                    ? Str(fileInfo, "media_mid", "mediaMid") : null));
        }
        return entries;
    }

    internal static IReadOnlyList<CloudLyricsCandidate> ParseKugouSearch(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!Find(document.RootElement, "data", out JsonElement data)
            || !(Find(data, "info", out JsonElement list)
                || Find(data, "lists", out list))
            || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<CloudLyricsCandidate> entries = [];
        foreach (JsonElement song in list.EnumerateArray())
        {
            string hash = Str(song, "hash", "FileHash");
            if (hash.Length == 0)
            {
                continue;
            }

            string id = Str(song, "album_audio_id", "ID");
            string title = Str(song, "songname", "SongName");
            string artist = Str(song, "singername", "SingerName");
            if (title.Length == 0)
            {
                string[] filename =
                    Str(song, "filename", "FileName")
                        .Split(" - ", 2, StringSplitOptions.TrimEntries);
                if (filename.Length == 2)
                {
                    if (artist.Length == 0) artist = filename[0];
                    title = filename[1];
                }
            }
            if (artist.Length == 0)
            {
                artist = SingerNames(song, "Singers");
            }

            string albumId = KugouAlbumId(song);
            string? cover = KugouArtworkUrl(song);
            entries.Add(new CloudLyricsCandidate(
                CloudLyricsSource.KugouMusic, id, title, artist,
                Str(song, "album_name", "AlbumName"),
                checked(Int(song, "duration", "Duration") * 1000),
                Hash: hash, CoverUrl: cover, AlbumId: albumId));
        }
        return entries;
    }

    internal static LrcDocument? ParseQqLyrics(string json)
    {
        string raw = json.Trim().TrimStart('\ufeff');
        if (!raw.StartsWith('{'))
        {
            int start = raw.IndexOf('{');
            int end = raw.LastIndexOf('}');
            if (start < 0 || end < start) return null;
            raw = raw[start..(end + 1)];
        }

        using JsonDocument document = JsonDocument.Parse(raw);
        JsonElement response = document.RootElement;
        JsonElement data = response;
        if (Find(response, "request", out _))
        {
            data = QqData(response);
        }
        else if (Find(response, "req_0", out JsonElement request)
            && Find(request, "data", out JsonElement wrapper))
        {
            data = wrapper;
        }
        else if (Find(response, "data", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            data = nested;
        }

        LrcDocument primary = FirstQqLyricPart(data,
            "lyric", "qrc", "lrc");
        if (primary.Lines.Count == 0)
            return null;

        LrcDocument translated = FirstQqLyricPart(data,
            "trans", "tlyric", "transLyric", "translation");
        LrcDocument romanized = FirstQqLyricPart(data,
            "roma", "romalrc", "romaLyric", "romalyric");
        LrcDocument merged = translated.Lines.Count > 0
            ? LrcDocument.Merge(primary, translated)
            : primary;
        if (romanized.Lines.Count > 0)
            LrcRomanization.Attach(merged, primary, romanized);
        return merged;
    }

    private static LrcDocument FirstQqLyricPart(
        JsonElement data, params string[] fields)
    {
        foreach (string field in fields)
        {
            string? value = Str(data, field);
            if (string.IsNullOrWhiteSpace(value)) continue;
            try
            {
                LrcDocument result = DecodeQqContent(value);
                if (result.Lines.Count > 0) return result;
            }
            catch (Exception error) when (
                error is FormatException or InvalidDataException
                    or System.Security.Cryptography.CryptographicException)
            {
                // An empty or obsolete QRC variant must not suppress a
                // valid LRC fallback in another response field.
            }
        }
        return new LrcDocument();
    }

    private static LrcDocument DecodeQqContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new LrcDocument();
        }

        string payload = content.Trim();
        if (payload.StartsWith('<')
            || payload.StartsWith('['))
        {
            return TimedLyricsCodec.ParseQrc(payload);
        }

        bool hex = payload.Length % 2 == 0
            && payload.Length > 16
            && payload.All(static c => Uri.IsHexDigit(c));
        if (hex)
        {
            return TimedLyricsCodec.ParseQrc(
                TimedLyricsCodec.DecryptQrc(payload));
        }

        return TimedLyricsCodec.ParseQrc(DecodeText(payload));
    }

    internal static (string id, string key)? ParseKugouLyricsCandidate(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!Find(document.RootElement, "candidates", out JsonElement list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement lyric in list.EnumerateArray())
        {
            string id = Str(lyric, "id");
            string key = Str(lyric, "accesskey");
            if (id.Length > 0 && key.Length > 0)
            {
                return (id, key);
            }
        }
        return null;
    }

    internal static LrcDocument? ParseKugouLyrics(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement payload = document.RootElement;
        string base64 = Str(payload, "content");
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        string text;
        try
        {
            byte[] encoded = Convert.FromBase64String(base64);
            if (encoded.Length >= 4
                && encoded[0] == (byte)'k'
                && encoded[1] == (byte)'r'
                && encoded[2] == (byte)'c'
                && encoded[3] == (byte)'1')
            {
                text = TimedLyricsCodec.DecryptKrc(encoded);
            }
            else
            {
                text = new UTF8Encoding(false, true).GetString(encoded);
            }
        }
        catch (FormatException)
        {
            // Some fallback providers return text instead of base64.
            text = base64;
        }

        // KRC words may start at any relative offset, not only <0,...>.
        // The parser returns no timed lines for ordinary square-bracket
        // LRC timestamps, so falling back is both safe and lossless.
        LrcDocument parsed = TimedLyricsCodec.ParseKrc(text);
        if (parsed.Lines.Count == 0)
        {
            parsed = LrcDocument.Parse(text);
        }
        return parsed.Lines.Count > 0 ? parsed : null;
    }

    private static string DecodeText(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return "";
        }

        try
        {
            return Encoding.UTF8.GetString(
                Convert.FromBase64String(encoded.Trim()));
        }
        catch (FormatException)
        {
            // Some endpoints already send plaintext; never misinterpret
            // arbitrary encrypted data as valid timed lyrics.
            return encoded;
        }
    }

    private static bool Find(
        JsonElement node, string field, out JsonElement found)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            return node.TryGetProperty(field, out found);
        }
        found = default;
        return false;
    }

    private static string Str(
        JsonElement node, params string[] fields)
    {
        foreach (string field in fields)
        {
            if (!Find(node, field, out JsonElement value))
            {
                continue;
            }
            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? "";
            }
            if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetRawText();
            }
        }
        return "";
    }

    private static int Int(JsonElement node, params string[] fields)
    {
        foreach (string field in fields)
        {
            if (Find(node, field, out JsonElement value))
            {
                if (value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out int number))
                {
                    return number;
                }
                if (value.ValueKind == JsonValueKind.String
                    && int.TryParse(value.GetString(),
                        NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out number))
                {
                    return number;
                }
            }
        }
        return 0;
    }

    private static string SingerNames(
        JsonElement song, string property = "singer")
    {
        if (!Find(song, property, out JsonElement singers)
            || singers.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        return string.Join(" / ", singers.EnumerateArray()
            .Select(singer => Str(singer, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name)));
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
