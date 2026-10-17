using System.Text;
using System.Text.Json;
using MusicLoom.Core.Lyrics;

namespace MusicLoom.LyricsProviders.Migu;

/// <summary>
/// Migu anonymous-first production adapter, no login or OAuth.
/// Search, details, lyrics and provider-granted playback are separate operations.
/// </summary>
public sealed class MiguProviderClient : IExternalMusicProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ExternalProvider Provider => ExternalProvider.Migu;
    public ProviderCapabilities Capabilities => new(
        true, true, true, true, false, true,
        PlaybackDisposition.AuthorizedAudioUrl);

    public MiguProviderClient(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? ProviderProtocol.CreateHttpClient();
    }

    public async Task<IReadOnlyList<ProviderTrack>> SearchAsync(
        string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        string url = "https://pd.musicapp.migu.cn/MIGUM2.0/v1.0/content/search_all.do"
            + "?text=" + Uri.EscapeDataString(query.Trim())
            + "&pageNo=1&pageSize=20&searchSwitch=%7B%22song%22%3A1%7D&isCorrect=1";
        using HttpRequestMessage request = Request(url);
        using JsonDocument json = await ProviderProtocol.JsonAsync(_http, request, ct);
        JsonElement root = json.RootElement;
        string code = ProviderProtocol.Text(root, "code");
        if (code.Length > 0 && code != "000000")
            throw new InvalidDataException("Migu search returned a non-success result.");
        JsonElement songs = ProviderProtocol.Path(root, "songResultData", "result");
        if (songs.ValueKind != JsonValueKind.Array)
            songs = ProviderProtocol.Path(root, "songResultData", "resultList");
        List<ProviderTrack> results = [];
        foreach (JsonElement songOrGroup in ProviderProtocol.Array(songs))
        {
            if (songOrGroup.ValueKind == JsonValueKind.Array)
                foreach (JsonElement song in ProviderProtocol.Array(songOrGroup))
                    AddSong(song);
            else AddSong(songOrGroup);
        }
        return results;

        void AddSong(JsonElement song)
        {
            ProviderTrack? candidate = ParseSong(song);
            if (candidate is not null) results.Add(candidate);
        }
    }

    public async Task<ProviderTrack?> GetDetailsAsync(
        ProviderTrack track, CancellationToken ct = default)
    {
        ProviderProtocol.RequireSameProvider(track, Provider);
        string key;
        if (!string.IsNullOrWhiteSpace(track.ContentId))
            key = "resourceId=" + Uri.EscapeDataString(track.ContentId);
        else if (!string.IsNullOrWhiteSpace(track.CopyrightId))
            key = "copyrightId=" + Uri.EscapeDataString(track.CopyrightId);
        else return track; // Never pass a songId as though it were a contentId.

        string url = "https://app.u.nf.migu.cn/MIGUM2.0/v1.0/content/resourceinfo.do?"
            + key + "&resourceType=2";
        using HttpRequestMessage request = Request(url);
        using JsonDocument json = await ProviderProtocol.JsonAsync(_http, request, ct);
        JsonElement root = json.RootElement;
        if (ProviderProtocol.Text(root, "code") != "000000")
            throw new InvalidDataException("Migu details returned a non-success result.");

        JsonElement item = ProviderProtocol.First(ProviderProtocol.Get(root, "resource"));
        if (item.ValueKind != JsonValueKind.Object) return track;
        return track with
        {
            Title = FirstNonempty(ProviderProtocol.Text(item, "name", "title"), track.Title),
            Album = FirstNonempty(ProviderProtocol.Text(item, "album", "albumName"), track.Album),
            ContentId = FirstNonempty(ProviderProtocol.Text(item, "contentId", "resourceId"), track.ContentId),
            CopyrightId = FirstNonempty(ProviderProtocol.Text(item, "copyrightId"), track.CopyrightId),
            LrcUrl = FirstNonempty(ProviderProtocol.Text(item, "lrcUrl", "lyricUrl"), track.LrcUrl),
            TrcUrl = FirstNonempty(ProviderProtocol.Text(item, "trcUrl"), track.TrcUrl),
            MrcUrl = FirstNonempty(ProviderProtocol.Text(item, "mrcUrl", "mrcurl"), track.MrcUrl),
            PreviewUrl = FirstNonempty(
                ProviderProtocol.Text(item, "previewUrl", "auditionUrl"), track.PreviewUrl),
            StreamUrl = FirstNonempty(
                ProviderProtocol.Text(item, "listenUrl", "playUrl"), track.StreamUrl),
        };
    }

    public async Task<ProviderLyricBundle?> GetLyricsAsync(
        ProviderTrack track, CancellationToken ct = default)
    {
        ProviderProtocol.RequireSameProvider(track, Provider);
        if (string.IsNullOrEmpty(track.LrcUrl))
            track = await GetDetailsAsync(track, ct) ?? track;
        if (string.IsNullOrWhiteSpace(track.LrcUrl))
            return null;

        string original = await FetchLyricsTextAsync(track.LrcUrl, 512 * 1024, ct);
        // No metadata-line removal: a provider-delivered lyric line is DATA.
        LrcDocument parsed = LrcDocument.Parse(original);
        LrcDocument? translated = null;
        string? rawTranslation = null;
        if (!string.IsNullOrWhiteSpace(track.TrcUrl))
        {
            try
            {
                rawTranslation = await FetchLyricsTextAsync(track.TrcUrl,
                    512 * 1024, ct);
                translated = LrcDocument.Parse(rawTranslation);
            }
            catch (Exception ex) when (ex is HttpRequestException
                or InvalidDataException)
            {
                // Optional translation failure cannot erase good source lyrics.
            }
        }

        LrcDocument? wordTimed = null;
        string? rawMrc = null;
        if (!string.IsNullOrWhiteSpace(track.MrcUrl))
        {
            try
            {
                rawMrc = await FetchLyricsTextAsync(track.MrcUrl,
                    1024 * 1024, ct);
                wordTimed = MiguMrcCodec.Parse(MiguMrcCodec.Decrypt(rawMrc));
            }
            catch (Exception ex) when (ex is HttpRequestException
                or InvalidDataException or FormatException
                or DecoderFallbackException)
            {
                // Keep the original/translation and report no word-timing.
                rawMrc = null;
            }
        }
        return new ProviderLyricBundle(parsed, original,
            translated, rawTranslation, wordTimed, rawMrc);
    }

    public async Task<ProviderPlayback> ResolvePlaybackAsync(
        ProviderTrack track, CancellationToken ct = default)
    {
        ProviderProtocol.RequireSameProvider(track, Provider);
        ProviderTrack? detail = await GetDetailsAsync(track, ct);
        track = detail ?? track;
        if (ProviderProtocol.ProviderHttpsUrl(track.StreamUrl, "migu.cn", out Uri? stream))
            return new ProviderPlayback(PlaybackDisposition.AuthorizedAudioUrl,
                "Provider response supplied an HTTPS playback URL; accessibility/entitlement still requires a real test.", stream);
        if (ProviderProtocol.ProviderHttpsUrl(track.PreviewUrl, "migu.cn", out Uri? preview))
            return new ProviderPlayback(PlaybackDisposition.AuthorizedPreviewUrl,
                "Provider response supplied an HTTPS preview URL.", preview);
        // A separate PC listen operation can grant a streaming address.
        // It must not be called without BOTH provider-issued identifiers,
        // and an unsuccessful response is NEVER interpreted as permission
        // to derive or guess the audio file URL.
        Uri? granted = await TryGetExplicitListenGrantAsync(track, ct);
        if (granted is not null)
            return new ProviderPlayback(PlaybackDisposition.AuthorizedAudioUrl,
                "Migu listen/v1.0 returned a provider-granted HTTPS song URL; actual network playback still needs entitlement verification.", granted);
        return new ProviderPlayback(PlaybackDisposition.Unavailable,
            "No verified provider-granted audio URL. Song metadata is not a playback grant.");
    }

    private async Task<Uri?> TryGetExplicitListenGrantAsync(
        ProviderTrack track, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(track.ContentId)
            || string.IsNullOrWhiteSpace(track.CopyrightId))
            return null;
        string url = "https://app.c.nf.migu.cn/MIGUM3.0/strategy/pc/listen/v1.0"
            + "?contentId=" + Uri.EscapeDataString(track.ContentId)
            + "&copyrightId=" + Uri.EscapeDataString(track.CopyrightId)
            + "&resourceType=2&toneFlag=PQ";
        using HttpRequestMessage request = Request(url);
        request.Headers.TryAddWithoutValidation("Channel", "014X031");
        try
        {
            using JsonDocument json = await ProviderProtocol.JsonAsync(
                _http, request, ct);
            JsonElement root = json.RootElement;
            string code = ProviderProtocol.Text(root, "code");
            if (code.Length > 0 && code != "000000") return null;
            JsonElement data = ProviderProtocol.Get(root, "data");
            string remote = ProviderProtocol.Text(data, "url", "playUrl", "listenUrl");
            return ProviderProtocol.ProviderHttpsUrl(remote, "migu.cn",
                out Uri? authorized) ? authorized : null;
        }
        catch (HttpRequestException)
        {
            // The optional listen request is an entitlement/availability
            // probe. A denied or obsolete endpoint is not proof of playback.
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    public static ProviderTrack? ParseSong(JsonElement item)
    {
        string contentId = ProviderProtocol.Text(item, "contentId", "resourceId");
        string copyrightId = ProviderProtocol.Text(item, "copyrightId");
        string songId = ProviderProtocol.Text(item, "songId", "id");
        string id = FirstNonempty(contentId, copyrightId, songId);
        if (id.Length == 0) return null;

        string album = "";
        string albumId = "";
        JsonElement firstAlbum = ProviderProtocol.First(ProviderProtocol.Get(item, "albums"));
        if (firstAlbum.ValueKind == JsonValueKind.Object)
        {
            album = ProviderProtocol.Text(firstAlbum, "name");
            albumId = ProviderProtocol.Text(firstAlbum, "id");
        }
        album = FirstNonempty(album,
            ProviderProtocol.Text(item, "albumName", "album"));
        string artist = string.Join("/", ProviderProtocol.Array(
            ProviderProtocol.Get(item, "singers"))
            .Select(static s => ProviderProtocol.Text(s, "name"))
            .Where(static name => !string.IsNullOrWhiteSpace(name)));
        artist = FirstNonempty(artist,
            ProviderProtocol.Text(item, "singerName", "artist"));
        string art = "";
        foreach (JsonElement image in ProviderProtocol.Array(
            ProviderProtocol.Get(item, "imgItems")))
        {
            string url = ProviderProtocol.Text(image, "img");
            if (url.Length == 0) continue;
            art = url;
            if (ProviderProtocol.Text(image, "imgSizeType") == "03") break;
        }
        int seconds = ProviderProtocol.Number(item, "duration");
        return new ProviderTrack(ExternalProvider.Migu, id,
            ProviderProtocol.Text(item, "name", "songName", "title"),
            artist, album,
            seconds is > 0 and < 86400 ? seconds * 1000 : 0,
            art, null,
            contentId.Length == 0 ? null : contentId,
            copyrightId.Length == 0 ? null : copyrightId,
            songId.Length == 0 ? null : songId,
            albumId.Length == 0 ? null : albumId,
            ProviderProtocol.Text(item, "lyricUrl", "lrcUrl"),
            ProviderProtocol.Text(item, "trcUrl"),
            ProviderProtocol.Text(item, "mrcurl", "mrcUrl"));
    }

    private async Task<string> FetchLyricsTextAsync(
        string url, int maxBytes, CancellationToken ct)
    {
        if (!ProviderProtocol.ProviderHttpsUrl(url, "migu.cn", out Uri? trusted))
            throw new InvalidDataException("Untrusted or insecure Migu lyric URL.");
        using HttpRequestMessage request = Request(trusted!.AbsoluteUri);
        byte[] bytes = await ProviderProtocol.ReadBoundedAsync(
            _http, request, maxBytes, ct);
        return ProviderProtocol.DecodeText(bytes);
    }

    private static HttpRequestMessage Request(string url)
    {
        HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://m.music.migu.cn/");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        return request;
    }

    private static string FirstNonempty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? "";

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
