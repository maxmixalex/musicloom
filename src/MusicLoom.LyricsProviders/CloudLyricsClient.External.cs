using MusicLoom.Core.Lyrics;
using MusicLoom.LyricsProviders.Migu;

namespace MusicLoom.LyricsProviders;

/// <summary>
/// Only anonymous-capable provider adapters may be surfaced. Apple Music
/// and Spotify experimental numeric IDs are reserved solely for legacy
/// SQLite row compatibility; there is no account login, web player,
/// background Go service, OAuth or token injection in the shipped EXE.
/// </summary>
public static class CloudLyricsSourceNames
{
    public static bool IsExternal(CloudLyricsSource source) =>
        source == CloudLyricsSource.MiguMusic;

    public static string English(CloudLyricsSource source) => source switch
    {
        CloudLyricsSource.QqMusic => "QQ Music",
        CloudLyricsSource.KugouMusic => "KuGou Music",
        CloudLyricsSource.MiguMusic => "Migu Music",
        _ => "Unavailable provider",
    };

    public static string Chinese(CloudLyricsSource source) => source switch
    {
        CloudLyricsSource.QqMusic => "QQ音乐",
        CloudLyricsSource.KugouMusic => "酷狗音乐",
        CloudLyricsSource.MiguMusic => "咪咕音乐",
        _ => "不支持的音源",
    };

    public static string Slug(CloudLyricsSource source) => source switch
    {
        CloudLyricsSource.QqMusic => "qq",
        CloudLyricsSource.KugouMusic => "kugou",
        CloudLyricsSource.MiguMusic => "migu",
        _ => "unavailable",
    };

    // Only v5 album and singer deep links have verifiable web routes.
    // A music stream's songId/copyrightId must not be presented as a
    // working single-track HTML page: v3 links redirect to the v5 root,
    // and no stable v5 song view has passed real browser validation.
    public static Uri? SongPage(CloudLyricsCandidate candidate) => null;

    private static bool ValidNumericId(string? id) =>
        id is { Length: >= 1 and <= 32 }
        && id.All(char.IsAsciiDigit)
        && id.Any(static digit => digit != '0');

    public static Uri? AlbumPage(CloudLyricsCandidate candidate) =>
        candidate.Source == CloudLyricsSource.MiguMusic
        && ValidNumericId(candidate.AlbumId)
            ? new Uri("https://music.migu.cn/v5/#/albumDetail?albumId="
                + candidate.AlbumId + "&playlistType=5")
            : null;

    public static Uri? SingerPage(CloudLyricsCandidate candidate)
    {
        if (candidate.Source != CloudLyricsSource.MiguMusic
            || !Uri.TryCreate(candidate.ExternalUrl, UriKind.Absolute,
                out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || !uri.Host.Equals("music.migu.cn",
                StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.TrimEnd('/') != "/v5"
            || !uri.Fragment.StartsWith("#/singerDetail?id=",
                StringComparison.Ordinal))
            return null;

        string id = uri.Fragment["#/singerDetail?id=".Length..];
        return ValidNumericId(id)
            ? new Uri("https://music.migu.cn/v5/#/singerDetail?id=" + id)
            : null;
    }

    // The library is a real first-party navigation target. Do not assert
    // that an unverified keyword fragment automatically submits search.
    public static Uri? SearchPage(CloudLyricsCandidate candidate) =>
        candidate.Source == CloudLyricsSource.MiguMusic
        && !string.IsNullOrWhiteSpace(candidate.Title)
            ? new Uri("https://music.migu.cn/v5/#/musicLibrary")
            : null;

    public static Uri? BrowsePage(CloudLyricsCandidate candidate) =>
        AlbumPage(candidate) ?? SingerPage(candidate) ?? SearchPage(candidate);
}

/// <summary>
/// Standalone Migu provider with the same approved search, lyric/cache and
/// match-confirmation behavior as the original QQ/KuGou routes.
/// </summary>
public sealed partial class CloudLyricsClient
{
    private static void RequireMigu(CloudLyricsSource source)
    {
        if (source != CloudLyricsSource.MiguMusic)
            throw new ArgumentOutOfRangeException(nameof(source),
                "This experimental provider requires an account and is not part of MusicLoom.");
    }

    private static CloudLyricsCandidate ToCloudCandidate(ProviderTrack track) =>
        new(CloudLyricsSource.MiguMusic, track.Id, track.Title, track.Artist,
            track.Album, track.DurationMilliseconds,
            CoverUrl: NormalizeExternalArtwork(track.ArtworkUrl,
                CloudLyricsSource.MiguMusic), AlbumId: track.AlbumId,
            ContentId: track.ContentId, CopyrightId: track.CopyrightId,
            ProviderSongId: track.SongId, PreviewUrl: track.PreviewUrl,
            StreamUrl: track.StreamUrl, ExternalUrl: track.ExternalUrl,
            LrcUrl: track.LrcUrl, TrcUrl: track.TrcUrl, MrcUrl: track.MrcUrl);

    private static ProviderTrack ToMiguTrack(CloudLyricsCandidate song)
    {
        RequireMigu(song.Source);
        return new(ExternalProvider.Migu, song.Id, song.Title, song.Artist,
            song.Album, song.DurationMilliseconds, song.CoverUrl,
            song.ExternalUrl, song.ContentId, song.CopyrightId,
            song.ProviderSongId, song.AlbumId,
            song.LrcUrl, song.TrcUrl, song.MrcUrl,
            song.PreviewUrl, song.StreamUrl);
    }

    private async Task<IReadOnlyList<CloudLyricsCandidate>>
        SearchExternalProviderAsync(CloudLyricsSource source,
            string query, CancellationToken ct)
    {
        RequireMigu(source);
        using MiguProviderClient adapter = new(_http);
        IReadOnlyList<ProviderTrack> matches =
            await adapter.SearchAsync(query, ct);
        return matches.Select(ToCloudCandidate).ToArray();
    }

    /// <summary>
    /// Resolve provider-owned metadata on demand without blocking the search.
    /// Only missing duration/artwork is enriched; no unrelated song or local
    /// audio duration is used as a substitute.
    /// </summary>
    public async Task<CloudLyricsCandidate> EnrichMiguCandidateAsync(
        CloudLyricsCandidate candidate, CancellationToken ct = default)
    {
        RequireMigu(candidate.Source);
        using MiguProviderClient adapter = new(_http);
        ProviderTrack details = await adapter.EnrichMetadataAsync(
            ToMiguTrack(candidate), ct);
        return candidate with
        {
            DurationMilliseconds = details.DurationMilliseconds > 0
                ? details.DurationMilliseconds : candidate.DurationMilliseconds,
            CoverUrl = NormalizeExternalArtwork(details.ArtworkUrl,
                CloudLyricsSource.MiguMusic) ?? candidate.CoverUrl,
            CopyrightId = !string.IsNullOrWhiteSpace(details.CopyrightId)
                ? details.CopyrightId : candidate.CopyrightId,
            ProviderSongId = !string.IsNullOrWhiteSpace(details.SongId)
                ? details.SongId : candidate.ProviderSongId,
            ExternalUrl = details.ExternalUrl ?? candidate.ExternalUrl,
        };
    }

    private async Task<LrcDocument?> GetExternalProviderLyricsAsync(
        CloudLyricsCandidate candidate, CancellationToken ct)
    {
        using MiguProviderClient adapter = new(_http);
        ProviderLyricBundle? lyrics =
            await adapter.GetLyricsAsync(ToMiguTrack(candidate), ct);
        return lyrics is null ? null : MergeExternalLyrics(lyrics);
    }

    /// <summary>
    /// Timed original song/artist/credit/band-member rows remain unchanged.
    /// Optional translated/MRC channels cannot erase the original LRC.
    /// </summary>
    public static LrcDocument MergeExternalLyrics(ProviderLyricBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        LrcDocument result = new();
        foreach ((string key, string value) in bundle.Original.Metadata)
            result.Metadata[key] = value;

        result.Lines.AddRange(bundle.Original.Lines);

        if (bundle.WordTimed is LrcDocument timed)
        {
            foreach (LrcLine wordLine in timed.Lines)
            {
                int at = result.Lines.FindIndex(line =>
                    string.Equals(line.Text, wordLine.Text,
                        StringComparison.Ordinal)
                    && (line.Timestamp - wordLine.Timestamp).Duration()
                        <= TimeSpan.FromMilliseconds(650));
                if (at < 0)
                {
                    result.Lines.Add(wordLine); // do not suppress [0,0] lines
                    continue;
                }
                if (wordLine.Words is { Count: > 0 } words
                    && string.Concat(words.Select(static word => word.Text))
                        == result.Lines[at].Text)
                    result.Lines[at] = result.Lines[at] with { Words = words };
            }
        }

        if (bundle.Translation is LrcDocument translations)
        {
            foreach (LrcLine translated in translations.Lines)
            {
                if (translated.Text.Length == 0)
                {
                    result.Lines.Add(translated);
                    continue;
                }

                // The translation is a distinct provider channel. It may
                // differ in row count; align only by bounded clock proximity.
                LrcLine? closest = result.Lines
                    .Where(original => original.Timestamp != translated.Timestamp
                        || original.Text != translated.Text)
                    .Where(original => (original.Timestamp - translated.Timestamp)
                        .Duration() <= TimeSpan.FromMilliseconds(650))
                    .OrderBy(original =>
                        (original.Timestamp - translated.Timestamp).Duration())
                    .FirstOrDefault();
                TimeSpan timestamp = closest?.Timestamp
                    ?? translated.Timestamp;
                result.Lines.Add(translated with { Timestamp = timestamp });
            }
        }

        LrcLine[] ordered = result.Lines
            .OrderBy(static line => line.Timestamp).ToArray();
        result.Lines.Clear();
        result.Lines.AddRange(ordered);
        return result;
    }


    private async Task<CloudAudioPreviewResult> GetExternalAudioPreviewAsync(
        CloudLyricsCandidate candidate, CancellationToken ct)
    {
        using MiguProviderClient adapter = new(_http);
        ProviderPlayback grant =
            await adapter.ResolvePlaybackAsync(ToMiguTrack(candidate), ct);
        Uri? stream = grant.Disposition is
            PlaybackDisposition.AuthorizedAudioUrl or
            PlaybackDisposition.AuthorizedPreviewUrl
            ? NormalizeStreamUrl(grant.AuthorizedUri?.AbsoluteUri,
                CloudLyricsSource.MiguMusic) : null;
        return new(stream, stream is not null
            ? CloudAudioPreviewStatus.Playable
            : grant.Disposition == PlaybackDisposition.Unavailable
                ? CloudAudioPreviewStatus.Restricted
                : CloudAudioPreviewStatus.ProviderUnavailable);
    }

    private async Task<string?> GetExternalArtworkAsync(
        CloudLyricsCandidate candidate, CancellationToken ct)
    {
        RequireMigu(candidate.Source);
        string? cover =
            NormalizeExternalArtwork(candidate.CoverUrl, candidate.Source);
        if (cover is not null) return cover;
        using MiguProviderClient adapter = new(_http);
        ProviderTrack? details =
            await adapter.GetDetailsAsync(ToMiguTrack(candidate), ct);
        return NormalizeExternalArtwork(details?.ArtworkUrl,
            CloudLyricsSource.MiguMusic);
    }

    internal static string? NormalizeExternalArtwork(
        string? url, CloudLyricsSource source)
    {
        if (source != CloudLyricsSource.MiguMusic
            || string.IsNullOrWhiteSpace(url))
            return null;
        string address = url.Trim();
        // Search JSON often returns relative OSS paths, // CDN URLs,
        // or HTTP while the same first-party CDN supports HTTPS.
        if (address.StartsWith("//", StringComparison.Ordinal))
            address = "https:" + address;
        else if (address.StartsWith("/", StringComparison.Ordinal))
            address = "https://d.musicapp.migu.cn" + address;
        else if (address.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            address = "https://" + address[7..];
        if (!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || uri.HostNameType != UriHostNameType.Dns)
            return null;
        string host = uri.IdnHost;
        return host.Equals("migu.cn", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".migu.cn",
                StringComparison.OrdinalIgnoreCase)
                ? uri.AbsoluteUri : null;
    }
}
