using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MusicLoom.LyricsProviders;

/// <summary>
/// Provider-specific album artwork metadata, independent of lyric adoption.
/// QQ search exposes album MID directly; KuGou may require an album-ID
/// detail lookup. Never substitute a singer portrait for album artwork.
/// </summary>
public sealed partial class CloudLyricsClient
{
    // Select a nonempty value; provider payloads frequently include the
    // preferred key as an empty string alongside another usable key.
    private static string FirstPresent(JsonElement node, params string[] names)
    {
        foreach (string name in names)
        {
            string value = Str(node, name).Trim();
            if (value.Length > 0 && !string.Equals(value, "null",
                    StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return string.Empty;
    }

    private static JsonElement Child(JsonElement node, params string[] names)
    {
        foreach (string name in names)
        {
            if (Find(node, name, out JsonElement value)
                && value.ValueKind == JsonValueKind.Object)
            {
                return value;
            }
        }
        return default;
    }

    internal static string? NormalizeArtworkUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string address = raw.Trim()
            .Replace("{size}", "300", StringComparison.OrdinalIgnoreCase)
            .Replace("{width}", "300", StringComparison.OrdinalIgnoreCase);
        if (address.StartsWith("//", StringComparison.Ordinal))
        {
            address = "https:" + address;
        }
        else if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            // KuGou's JSON still sends http://imge.kugou.com links
            // even though the cover CDN supports HTTPS.
            address = "https://" + address[7..];
        }

        return Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.Equals("y.gtimg.cn", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("imgy.qq.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".qq.cn", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".gtimg.cn", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".kugou.com", StringComparison.OrdinalIgnoreCase))
            ? uri.AbsoluteUri
            : null;
    }

    internal static string? QqArtworkUrl(JsonElement song)
    {
        JsonElement album = Child(song, "album", "albumInfo", "album_info");
        string? provided = NormalizeArtworkUrl(
            FirstPresent(album, "pic", "picUrl", "picurl", "albumPic",
                "album_pic", "cover", "cover_url", "img", "imgurl"));
        provided ??= NormalizeArtworkUrl(
            FirstPresent(song, "albumPic", "album_pic", "albumPicUrl",
                "albumImg", "albumpic", "pic", "picUrl", "coverUrl"));
        if (provided is not null)
        {
            return provided;
        }

        string mid = FirstPresent(album,
            "pmid", "mid", "albumMID", "albumMid", "albumMId", "albummid", "album_mid");
        if (mid.Length == 0)
        {
            mid = FirstPresent(song,
                "albummid", "albumMID", "albumMid", "albumMId",
                "album_mid", "album_pmid", "albumPMid");
        }

        return QqArtworkFromMid(mid);
    }

    private static string? QqArtworkFromMid(string? mid)
    {
        if (string.IsNullOrWhiteSpace(mid) || mid.Length < 8
            || mid.Length > 32 || !mid.All(
                static c => c is >= '0' and <= '9'
                    or >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
        {
            return null;
        }

        return "https://y.gtimg.cn/music/photo_new/T002R300x300M000"
            + mid + ".jpg";
    }

    private static string ValidNumericFrom(JsonElement node, params string[] keys)
    {
        foreach (string key in keys)
        {
            string value = FirstPresent(node, key);
            if (IsValidNumericId(value))
            {
                return value;
            }
        }
        return string.Empty;
    }

    internal static string KugouAlbumId(JsonElement song)
    {
        string value = ValidNumericFrom(song, "album_id", "albumid",
            "AlbumID", "AlbumId", "albumId");
        if (value.Length != 0)
            return value;

        JsonElement album = Child(song, "album", "album_info", "albumInfo");
        return ValidNumericFrom(album, "album_id", "albumid",
            "AlbumID", "AlbumId", "albumId", "id");
    }

    internal static string? KugouArtworkUrl(JsonElement song)
    {
        string[] fields =
        [
            "album_img", "albumImg", "album_image", "album_pic",
            "imgurl", "imgUrl", "image", "Image", "img",
            "sizable_cover", "sizableCover", "union_cover", "unionCover",
            "cover_url", "coverUrl", "cover"
        ];

        string? url = NormalizeArtworkUrl(FirstPresent(song, fields));
        if (url is not null) return url;

        JsonElement album = Child(song, "album", "album_info", "albumInfo");
        url = NormalizeArtworkUrl(FirstPresent(album, fields));
        if (url is not null) return url;

        JsonElement trans = Child(song, "trans_param", "transParam");
        return NormalizeArtworkUrl(FirstPresent(trans, fields));
    }

    private static bool IsValidNumericId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && long.TryParse(value, NumberStyles.None,
            CultureInfo.InvariantCulture, out long id) && id > 0;

    internal static string? ParseKugouAlbumArtwork(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement node = document.RootElement;
        if (Find(node, "data", out JsonElement data))
        {
            node = data.ValueKind == JsonValueKind.Array
                && data.GetArrayLength() > 0 ? data[0] : data;
        }
        return KugouArtworkUrl(node);
    }

    internal static string? ParseQqAlbumArtwork(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (Find(root, "req_0", out JsonElement req))
        {
            root = req;
        }
        else if (Find(root, "music.musichallAlbum.AlbumInfoServer",
                     out JsonElement service))
        {
            root = service;
        }

        if (Find(root, "data", out JsonElement data))
        {
            root = data;
        }

        JsonElement basics = Child(root, "basicInfo", "basic_info", "album");
        string? provided = QqArtworkUrl(basics);
        provided ??= QqArtworkUrl(root);
        return provided;
    }

    /// <summary>
    /// Network fallback only when a candidate has no album image in the
    /// search result. The calling panel bounds concurrency and cancellation.
    /// </summary>
    public async Task<string?> ResolveArtworkAsync(
        CloudLyricsCandidate candidate, CancellationToken ct = default)
    {
        if (CloudLyricsSourceNames.IsExternal(candidate.Source))
            return await GetExternalArtworkAsync(candidate, ct);
        if (candidate.CoverUrl is string cover)
            return NormalizeArtworkUrl(cover);

        if (candidate.Source == CloudLyricsSource.QqMusic)
        {
            return await ResolveQqAlbumArtworkAsync(candidate.AlbumId, ct);
        }
        if (candidate.Source != CloudLyricsSource.KugouMusic)
            return null;

        string albumId = candidate.AlbumId ?? "";
        if (!IsValidNumericId(albumId))
        {
            // The old KuGou mobile song search often omits album_id.
            // Ask its read-only song-metadata endpoint for the album ID.
            albumId = await ResolveKugouSongAlbumIdAsync(
                candidate.Hash, ct);
        }
        if (!IsValidNumericId(albumId))
            return null;

        string[] hosts =
        [
            "https://mobilecdn.kugou.com",
            "https://mobiles.kugou.com",
        ];
        foreach (string host in hosts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string url = host + "/api/v3/album/info?albumid="
                    + Uri.EscapeDataString(albumId)
                    + "&version=9108&plat=0";
                using HttpRequestMessage request = new(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
                string payload = await GetBodyAsync(request, ct);
                string? image = ParseKugouAlbumArtwork(payload);
                if (image is not null)
                    return image;
            }
            catch (HttpRequestException)
            {
                // Try the second first-party mobile host.
            }
            catch (JsonException)
            {
            }
        }
        return null;
    }

    private async Task<string> ResolveKugouSongAlbumIdAsync(
        string? hash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hash)
            || hash.Length is < 16 or > 64
            || !hash.All(static c => c is >= '0' and <= '9'
                or >= 'A' and <= 'F' or >= 'a' and <= 'f'))
        {
            return "";
        }

        try
        {
            string url = "https://m.kugou.com/app/i/getSongInfo.php"
                + "?cmd=playInfo&hash=" + Uri.EscapeDataString(hash);
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            string json = await GetBodyAsync(request, ct);
            using JsonDocument document = JsonDocument.Parse(json);
            return KugouAlbumId(document.RootElement);
        }
        catch (HttpRequestException)
        {
            return "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private async Task<string?> ResolveQqAlbumArtworkAsync(
        string? albumId, CancellationToken ct)
    {
        if (!IsValidNumericId(albumId)) return null;
        try
        {
            string body = "{\"req_0\":{\"module\":"
                + "\"music.musichallAlbum.AlbumInfoServer\","
                + "\"method\":\"GetAlbumDetail\",\"param\":{\"albumId\":"
                + albumId + "}}}";
            using HttpRequestMessage request = new(HttpMethod.Post, QqApi)
            {
                Content = new StringContent(body, Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.Referrer = new Uri("https://y.qq.com/");
            return ParseQqAlbumArtwork(await GetBodyAsync(request, ct));
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
