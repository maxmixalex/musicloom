using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MusicLoom.LyricsProviders;

/// <summary>
/// Publicly available, unauthenticated streaming previews from QQ Music and
/// KuGou. A missing provider-issued URL is NEVER replaced by a guessed media
/// path, a local file, or a URL from an unrelated source.
/// </summary>
public enum CloudAudioPreviewStatus
{
    Playable,
    Restricted,
    InvalidCandidate,
    ProviderUnavailable,
}

public sealed record CloudAudioPreviewResult(
    Uri? StreamUrl, CloudAudioPreviewStatus Status,
    bool IsFullLength = false)
{
    public bool CanPlay => StreamUrl is not null
        && Status == CloudAudioPreviewStatus.Playable;
}

public sealed partial class CloudLyricsClient
{
    private const string KugouWebPlayer =
        "https://wwwapi.kugou.com/yy/index.php";

    public Task<CloudAudioPreviewResult> GetAudioPreviewAsync(
        CloudLyricsCandidate candidate,
        CancellationToken cancellationToken = default) =>
        candidate.Source switch
        {
            CloudLyricsSource.QqMusic =>
                GetQqAudioPreviewAsync(candidate, cancellationToken),
            CloudLyricsSource.KugouMusic =>
                GetKugouAudioPreviewAsync(candidate, cancellationToken),
            CloudLyricsSource.MiguMusic or CloudLyricsSource.AppleMusic
                or CloudLyricsSource.Spotify =>
                GetExternalAudioPreviewAsync(candidate, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(candidate)),
        };

    private static bool ValidQqMid(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length is >= 8 and <= 32
        && value.All(static c => char.IsAsciiLetterOrDigit(c));

    private static bool ValidKugouHash(string? hash) =>
        hash is { Length: 32 }
        && hash.All(static c => char.IsAsciiHexDigit(c));

    /// <summary>
    /// QQ's vkey API needs the real song MID and a proposed filename.
    /// Some tracks publish a distinct file.media_mid: offer both identifiers,
    /// but only use the exact purl that the service actually grants.
    /// </summary>
    internal static string BuildQqGuestPreviewRequest(
        string songMid, string? mediaMid, string guid)
    {
        if (!ValidQqMid(songMid)
            || guid.Length is < 4 or > 20
            || !guid.All(char.IsAsciiDigit))
            throw new ArgumentException("Invalid QQ preview identifier.");

        string[] ids = ValidQqMid(mediaMid)
            && !string.Equals(songMid, mediaMid, StringComparison.Ordinal)
                ? [mediaMid!, songMid] : [songMid];
        string[] names = ids.SelectMany(static id => new[]
            {
                "M500" + id + ".mp3",
                "C400" + id + ".m4a",
            }).ToArray();
        string mids = string.Join(",", names.Select(_ => J(songMid)));
        string quotedFiles = string.Join(",", names.Select(J));
        string kinds = string.Join(",", names.Select(_ => "0"));

        return "{\"comm\":{\"uin\":\"0\",\"format\":\"json\","
            + "\"ct\":24,\"cv\":0},\"req_0\":{"
            + "\"module\":\"vkey.GetVkeyServer\","
            + "\"method\":\"CgiGetVkey\",\"param\":{"
            + "\"guid\":" + J(guid)
            + ",\"songmid\":[" + mids + "]"
            + ",\"songtype\":[" + kinds + "]"
            + ",\"uin\":\"0\",\"loginflag\":1,"
            + "\"platform\":\"20\",\"filename\":["
            + quotedFiles + "]}}}";
    }

    private async Task<CloudAudioPreviewResult> GetQqAudioPreviewAsync(
        CloudLyricsCandidate candidate, CancellationToken ct)
    {
        string mid = candidate.SongMid ?? candidate.Id;
        if (!ValidQqMid(mid))
            return new(null, CloudAudioPreviewStatus.InvalidCandidate);

        string guid = Random.Shared.NextInt64(10_000_000, 99_999_999)
            .ToString(CultureInfo.InvariantCulture);
        using HttpRequestMessage request = new(HttpMethod.Post, QqApi);
        request.Content = new StringContent(
            BuildQqGuestPreviewRequest(mid, candidate.MediaMid, guid),
            Encoding.UTF8, "application/json");
        request.Headers.Referrer = new Uri("https://y.qq.com/");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");

        // A guest request has no account cookies, VIP tokens or DRM bypass.
        string json = await GetBodyAsync(request, ct);
        Uri? stream = ParseQqGuestPreviewResponse(json);
        return new(stream, stream is null
            ? CloudAudioPreviewStatus.Restricted
            : CloudAudioPreviewStatus.Playable);
    }

    internal static Uri? ParseQqGuestPreviewResponse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (!Find(root, "req_0", out JsonElement response)
            || Int(response, "code") != 0
            || !Find(response, "data", out JsonElement data)
            || !Find(data, "midurlinfo", out JsonElement songs)
            || songs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // Only provider-granted filenames with a nonempty purl can play.
        // Prefer the 128kb MP3 when both it and AAC are public.
        JsonElement[] infos = songs.EnumerateArray()
            .Where(static entry =>
                !string.IsNullOrWhiteSpace(Str(entry, "purl")))
            .OrderBy(static entry =>
                Str(entry, "filename").StartsWith("M500",
                    StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToArray();

        foreach (JsonElement info in infos)
        {
            string relative = Str(info, "purl");
            if (relative.Length == 0
                || relative.StartsWith("//", StringComparison.Ordinal)
                || relative.StartsWith("/", StringComparison.Ordinal)
                || relative.Contains("..", StringComparison.Ordinal)
                || relative.Contains('\\')
                || Uri.TryCreate(relative, UriKind.Absolute, out _))
                continue;

            if (Find(data, "sip", out JsonElement bases)
                && bases.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in bases.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        continue;
                    string sip = item.GetString() ?? "";
                    Uri? playable = BuildQqStreamUrl(sip, relative);
                    if (playable is not null)
                        return playable;
                }
            }

            Uri? fallback = BuildQqStreamUrl(
                "https://ws.stream.qqmusic.qq.com/", relative);
            if (fallback is not null)
                return fallback;
        }
        return null;
    }

    private static Uri? BuildQqStreamUrl(string sip, string relative)
    {
        if (!Uri.TryCreate(sip, UriKind.Absolute, out Uri? root)
            || !IsAllowedStreamHost(root, CloudLyricsSource.QqMusic)
            || !Uri.TryCreate(root, relative, out Uri? result))
            return null;

        return NormalizeStreamUrl(result.AbsoluteUri, CloudLyricsSource.QqMusic);
    }

    private async Task<CloudAudioPreviewResult> GetKugouAudioPreviewAsync(
        CloudLyricsCandidate candidate, CancellationToken ct)
    {
        if (!ValidKugouHash(candidate.Hash))
            return new(null, CloudAudioPreviewStatus.InvalidCandidate);

        string hash = candidate.Hash!;
        string album = IsValidNumericId(candidate.AlbumId)
            ? candidate.AlbumId! : "0";
        bool gotResponse = false;

        // First-party Web player endpoint: a normal, public play_url.
        // JSONP is intentionally requested because some Web versions require
        // a callback name even for a public guest playback response.
        string webUrl = KugouWebPlayer
            + "?r=play%2Fgetdata&callback=musicloomPreview"
            + "&mid=1&hash=" + Uri.EscapeDataString(hash)
            + "&album_id=" + Uri.EscapeDataString(album);
        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Get, webUrl);
            request.Headers.Referrer = new Uri("https://www.kugou.com/");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            string raw = await GetBodyAsync(request, ct);
            gotResponse = true;
            Uri? stream = ParseKugouGuestPreviewResponse(raw);
            if (stream is not null)
                return new(stream, CloudAudioPreviewStatus.Playable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            // Try the separate public mobile play-info endpoint.
        }
        catch (JsonException)
        {
            // Malformed JSONP does not prevent the official mobile fallback.
        }

        string mobileUrl = "https://m.kugou.com/app/i/getSongInfo.php"
            + "?cmd=playInfo&hash=" + Uri.EscapeDataString(hash);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, mobileUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            string json = await GetBodyAsync(request, ct);
            gotResponse = true;
            Uri? stream = ParseKugouGuestPreviewResponse(json);
            if (stream is not null)
                return new(stream, CloudAudioPreviewStatus.Playable);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
        }
        catch (JsonException)
        {
        }

        // No URL returned by either public endpoint: do not spoof access.
        return new(null, gotResponse
            ? CloudAudioPreviewStatus.Restricted
            : CloudAudioPreviewStatus.ProviderUnavailable);
    }

    internal static Uri? ParseKugouGuestPreviewResponse(string raw)
    {
        string body = raw.Trim().TrimStart('\uFEFF');
        if (body.StartsWith("musicloomPreview(", StringComparison.Ordinal))
        {
            int end = body.LastIndexOf(')');
            if (end < "musicloomPreview(".Length)
                return null;
            body = body["musicloomPreview(".Length..end].Trim();
        }
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement node = doc.RootElement;

        if (Int(node, "status") == 0
            || Int(node, "err_code", "errcode") is < 0)
            return null;

        JsonElement data = Find(node, "data", out JsonElement nested)
            && nested.ValueKind == JsonValueKind.Object
                ? nested : node;

        // Do not stop at a nonempty but expired/non-provider primary URL:
        // some public responses contain a usable backup stream instead.
        foreach (string key in new[]
        {
            "play_url", "playUrl", "url", "play_url_mid",
            "play_backup_url", "play_backup_url_mid", "backupurl",
        })
        {
            Uri? playable = NormalizeStreamUrl(
                Str(data, key), CloudLyricsSource.KugouMusic);
            if (playable is not null) return playable;
        }
        return null;
    }

    /// <summary>
    /// Never play an unrelated URL supplied by a compromised/malformed
    /// provider JSON response. Only normal, provider-owned HTTP(S) media
    /// hosts are permitted; no local file, localhost or arbitrary redirects.
    /// </summary>
    internal static Uri? NormalizeStreamUrl(
        string? raw, CloudLyricsSource source)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri? parsed)
            || !IsAllowedStreamHost(parsed, source)
            || parsed.UserInfo.Length > 0
            || !parsed.IsDefaultPort)
            return null;

        // Some historic KuGou media links are HTTP-only; the Windows
        // MediaPlayer is allowed to use the provider-issued URL unchanged.
        return parsed;
    }

    private static bool IsAllowedStreamHost(
        Uri uri, CloudLyricsSource source)
    {
        if (uri.Scheme is not ("https" or "http"))
            return false;
        string host = uri.Host;
        static bool Domain(string host, string root) =>
            host.Equals(root, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + root, StringComparison.OrdinalIgnoreCase);

        return source switch
        {
            CloudLyricsSource.QqMusic => Domain(host, "qqmusic.qq.com")
                || Domain(host, "gtimg.cn"),
            CloudLyricsSource.KugouMusic => Domain(host, "kugou.com")
                || Domain(host, "kugou.net")
                || Domain(host, "kgmusic.com")
                || Domain(host, "kgimg.com"),
            CloudLyricsSource.MiguMusic => uri.Scheme == Uri.UriSchemeHttps
                && Domain(host, "migu.cn"),
            CloudLyricsSource.AppleMusic => uri.Scheme == Uri.UriSchemeHttps
                && (Domain(host, "itunes.apple.com")
                    || Domain(host, "mzstatic.com")),
            _ => false, // Spotify Connect grants no direct audio stream.
        };
    }
}
