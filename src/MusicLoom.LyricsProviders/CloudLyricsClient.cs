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
    string? SongMid = null);

public sealed class CloudLyricsClient : IDisposable
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

        // SearchCgiService's Desktop method provides normal song metadata
        // without requiring a personal QQ Music login.
        string json =
            "{\"music.search.SearchCgiService\":{\"method\":\"DoSearchForQQMusicDesktop\"," +
            "\"module\":\"music.search.SearchCgiService\"," +
            "\"param\":{\"num_per_page\":20,\"page_num\":1,\"search_type\":0," +
            "\"query\":\"" + JsonEncodedText.Encode(text.Trim()) + "\"}}}";
        using HttpRequestMessage request =
            new(HttpMethod.Post, QqApi)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        string response = await GetBodyAsync(request, ct);
        return ParseQqSearch(response);
    }

    private async Task<IReadOnlyList<CloudLyricsCandidate>> SearchKugouAsync(
        string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        string url = "https://msearchcdn.kugou.com/api/v3/search/song"
            + "?version=9108&pagesize=20&page=1"
            + "&keyword=" + Uri.EscapeDataString(text.Trim());
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        return ParseKugouSearch(await GetBodyAsync(request, ct));
    }

    private async Task<LrcDocument?> GetQqLyricsAsync(
        CloudLyricsCandidate track, CancellationToken ct)
    {
        string mid = track.SongMid ?? track.Id;
        if (string.IsNullOrWhiteSpace(mid))
        {
            return null;
        }

        // The legacy LRC endpoint supplies ordinary base64 LRC and tlyric.
        // QRC word-level ciphertext intentionally is not misread as LRC.
        string uri =
            "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg"
            + "?format=json&nobase64=0&songmid="
            + Uri.EscapeDataString(mid)
            + "&g_tk=5381&loginUin=0&hostUin=0&inCharset=utf8"
            + "&outCharset=utf-8&notice=0&platform=yqq.json&needNewCode=0";
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        string response = await GetBodyAsync(request, ct);
        return ParseQqLyrics(response);
    }

    private async Task<LrcDocument?> GetKugouLyricsAsync(
        CloudLyricsCandidate track, CancellationToken ct)
    {
        string keyword = track.Artist + " - " + track.Title;
        string url = "https://lyrics.kugou.com/v1/search"
            + "?ver=1&man=yes&client=mobi&lrctxt=1"
            + "&keyword=" + Uri.EscapeDataString(keyword)
            + "&duration=" + Math.Max(0, track.DurationMilliseconds)
                .ToString(CultureInfo.InvariantCulture)
            + "&hash=" + Uri.EscapeDataString(track.Hash ?? "")
            + "&album_audio_id=" + Uri.EscapeDataString(track.Id);
        using HttpRequestMessage search = new(HttpMethod.Get, url);
        string found = await GetBodyAsync(search, ct);
        (string id, string key)? token = ParseKugouLyricsCandidate(found);
        if (token is null)
        {
            return null;
        }

        string lyricUrl = "https://lyrics.kugou.com/download"
            + "?ver=1&client=pc&fmt=lrc&charset=utf8"
            + "&id=" + Uri.EscapeDataString(token.Value.id)
            + "&accesskey=" + Uri.EscapeDataString(token.Value.key);
        using HttpRequestMessage download =
            new(HttpMethod.Get, lyricUrl);
        return ParseKugouLyrics(await GetBodyAsync(download, ct));
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
        if (!Find(root, "music.search.SearchCgiService", out JsonElement service)
            || !Find(service, "data", out JsonElement data)
            || !Find(data, "body", out JsonElement body))
        {
            return [];
        }

        if (!Find(body, "song", out JsonElement songs)
            || !Find(songs, "list", out JsonElement list)
            || list.ValueKind != JsonValueKind.Array)
        {
            if (!Find(body, "item_song", out list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
        }

        List<CloudLyricsCandidate> entries = [];
        foreach (JsonElement song in list.EnumerateArray())
        {
            string id = Str(song, "mid");
            if (id.Length == 0)
            {
                continue;
            }

            string album = Find(song, "album", out JsonElement albumObject)
                ? Str(albumObject, "name") : "";
            string artist = SingerNames(song);
            string title = Str(song, "title", "name");
            int seconds = Int(song, "interval");
            entries.Add(new CloudLyricsCandidate(
                CloudLyricsSource.QqMusic, id, title, artist,
                album, checked(seconds * 1000), SongMid: id));
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

            entries.Add(new CloudLyricsCandidate(
                CloudLyricsSource.KugouMusic, id, title, artist,
                Str(song, "album_name", "AlbumName"),
                checked(Int(song, "duration", "Duration") * 1000),
                Hash: hash));
        }
        return entries;
    }

    internal static LrcDocument? ParseQqLyrics(string json)
    {
        // The older service may return a JavaScript callback wrapper.
        string raw = json.Trim().TrimStart('\ufeff');
        if (!raw.StartsWith('{'))
        {
            int start = raw.IndexOf('{');
            int end = raw.LastIndexOf('}');
            if (start < 0 || end < start)
            {
                return null;
            }
            raw = raw[start..(end + 1)];
        }

        using JsonDocument document = JsonDocument.Parse(raw);
        JsonElement root = document.RootElement;
        LrcDocument original = LrcDocument.Parse(
            DecodeText(Str(root, "lyric")));
        LrcDocument translation = LrcDocument.Parse(
            DecodeText(Str(root, "trans", "tlyric")));
        if (original.Lines.Count == 0)
        {
            return null;
        }
        return translation.Lines.Count > 0
            ? LrcDocument.Merge(original, translation) : original;
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
        string decoded = DecodeText(Str(document.RootElement, "content"));
        LrcDocument parsed = LrcDocument.Parse(decoded);
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
