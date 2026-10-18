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

    /// <summary>
    /// Migu's old /v3/music/song/{copyrightId} pages now redirect to the
    /// generic /v5 home, discarding the target track. The live web player
    /// uses a hash-router song page keyed by SONG ID, not copyrightId.
    /// </summary>
    public static Uri? SongPage(CloudLyricsCandidate candidate)
    {
        if (candidate.Source != CloudLyricsSource.MiguMusic) return null;

        // A previously saved provider URL is honored only if it already
        // points into the specific v5 song route. Reject old v3 redirects,
        // generic /v5 home pages and first-party streaming/cover URLs.
        if (Uri.TryCreate(candidate.ExternalUrl, UriKind.Absolute, out Uri? url)
            && url.Scheme == Uri.UriSchemeHttps && url.IsDefaultPort
            && url.UserInfo.Length == 0
            && url.Host.Equals("music.migu.cn",
                StringComparison.OrdinalIgnoreCase)
            && url.AbsolutePath.TrimEnd('/').Equals("/v5",
                StringComparison.OrdinalIgnoreCase)
            && url.Fragment.StartsWith("#/song/", StringComparison.Ordinal)
            && ValidWebSongId(url.Fragment["#/song/".Length..].Split('?')[0]))
            return url;

        string? songId = ValidWebSongId(candidate.ProviderSongId)
            ? candidate.ProviderSongId
            // A community v5 client uses the contentId when songId is
            // absent. Never use copyrightId for this new web route.
            : ValidWebSongId(candidate.ContentId)
                ? candidate.ContentId : null;
        return songId is null ? null : new Uri(
            "https://music.migu.cn/v5/#/song/" + songId);
    }

    private static bool ValidWebSongId(string? id) =>
        id is { Length: >= 6 and <= 32 }
        && id.All(static c => char.IsAsciiDigit(c))
        && id.Any(static c => c != '0');

    /// <summary>
    /// When no songId or contentId exists, navigate to the public v5 music
    /// library instead of a broken v3 search endpoint. The hash keyword is
    /// advisory; the website might still require manual search.
    /// </summary>
    public static Uri? SearchPage(CloudLyricsCandidate candidate)
    {
        if (candidate.Source != CloudLyricsSource.MiguMusic
            || string.IsNullOrWhiteSpace(candidate.Title))
            return null;
        string query = string.Join(" ", new[]
        {
            candidate.Artist?.Trim(), candidate.Title.Trim(),
        }.Where(static part => !string.IsNullOrWhiteSpace(part)));
        return new Uri("https://music.migu.cn/v5/#/musicLibrary?keyword="
            + Uri.EscapeDataString(query));
    }

    public static Uri? BrowsePage(CloudLyricsCandidate candidate) =>
        SongPage(candidate) ?? SearchPage(candidate);
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
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
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
