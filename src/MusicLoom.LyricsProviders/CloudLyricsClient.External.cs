using MusicLoom.Core.Lyrics;
using MusicLoom.ProviderPoc;

namespace MusicLoom.LyricsProviders;

/// <summary>
/// Provider-independent identities and labels. The existing QQ/KuGou
/// persisted integer IDs are never renumbered during expansion.
/// </summary>
public static class CloudLyricsSourceNames
{
    public static bool IsExternal(CloudLyricsSource source) =>
        source is CloudLyricsSource.MiguMusic
            or CloudLyricsSource.AppleMusic or CloudLyricsSource.Spotify;

    public static string English(CloudLyricsSource source) => source switch
    {
        CloudLyricsSource.QqMusic => "QQ Music",
        CloudLyricsSource.KugouMusic => "KuGou Music",
        CloudLyricsSource.MiguMusic => "Migu Music",
        CloudLyricsSource.AppleMusic => "Apple Music",
        CloudLyricsSource.Spotify => "Spotify",
        _ => "Unknown provider",
    };

    public static string Chinese(CloudLyricsSource source) => source switch
    {
        CloudLyricsSource.QqMusic => "QQ音乐",
        CloudLyricsSource.KugouMusic => "酷狗音乐",
        CloudLyricsSource.MiguMusic => "咪咕音乐",
        CloudLyricsSource.AppleMusic => "Apple Music",
        CloudLyricsSource.Spotify => "Spotify",
        _ => "未知音源",
    };

    public static string Slug(CloudLyricsSource source) => source switch
    {
        CloudLyricsSource.QqMusic => "qq",
        CloudLyricsSource.KugouMusic => "kugou",
        CloudLyricsSource.MiguMusic => "migu",
        CloudLyricsSource.AppleMusic => "apple",
        CloudLyricsSource.Spotify => "spotify",
        _ => "unknown",
    };
    public static Uri? SongPage(CloudLyricsCandidate candidate)
    {
        if (!IsExternal(candidate.Source)
            || !Uri.TryCreate(candidate.ExternalUrl, UriKind.Absolute,
                out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            return null;

        string trustedDomain = candidate.Source switch
        {
            CloudLyricsSource.MiguMusic => "migu.cn",
            CloudLyricsSource.AppleMusic => "music.apple.com",
            CloudLyricsSource.Spotify => "open.spotify.com",
            _ => "",
        };
        return uri.Host.Equals(trustedDomain,
                   StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith("." + trustedDomain,
                   StringComparison.OrdinalIgnoreCase)
            ? uri : null;
    }

}

/// <summary>
/// New providers reuse the approved QQ/KuGou matching model and canonical
/// LrcDocument format while remaining independently credentialed adapters.
/// They never borrow QQ/NetEase cookies or grant protected full audio URLs.
/// </summary>
public sealed partial class CloudLyricsClient
{
    private async Task<IExternalMusicProvider> CreateExternalProviderAsync(
        CloudLyricsSource source, CancellationToken ct)
    {
        if (source != CloudLyricsSource.Spotify)
            return CreateExternalProvider(source);

        string? oauth = Environment.GetEnvironmentVariable(
            "MUSICLOOM_SPOTIFY_OAUTH_TOKEN");
        string? lyrics = Environment.GetEnvironmentVariable(
            "MUSICLOOM_SPOTIFY_LYRICS_BEARER");
        if (string.IsNullOrWhiteSpace(oauth)
            || string.IsNullOrWhiteSpace(lyrics))
        {
            try
            {
                string address = Environment.GetEnvironmentVariable(
                    "MUSICLOOM_GO_LIBRESPOT_URL")
                    ?? "http://127.0.0.1:3678";
                // Exactly the same authenticated local session controls
                // actual Spotify audio. Do not make the user configure a
                // second account or copy a login cookie for metadata search.
                using SpotifyLocalStreamBridge local = new(address, _http);
                using CancellationTokenSource limit =
                    CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(2));
                string? ephemeral = await local.GetSessionTokenAsync(
                    limit.Token);
                if (!string.IsNullOrWhiteSpace(ephemeral))
                {
                    oauth = string.IsNullOrWhiteSpace(oauth)
                        ? ephemeral : oauth;
                    lyrics = string.IsNullOrWhiteSpace(lyrics)
                        ? ephemeral : lyrics;
                }
            }
            catch (HttpRequestException) { /* Local player not running. */ }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // A missing/slow loopback player should not hang the search
                // dialog. The existing provider authorization message applies.
            }
            catch (System.Text.Json.JsonException)
            {
                // A stale local daemon without a valid /token payload is
                // unavailable; do not treat its reply as an OAuth token.
            }
        }
        return new SpotifyProviderPoc(oauth, lyrics, http: _http);
    }

    private IExternalMusicProvider CreateExternalProvider(
        CloudLyricsSource source) => source switch
        {
            CloudLyricsSource.MiguMusic => new MiguProviderPoc(_http),
            CloudLyricsSource.AppleMusic => new AppleMusicProviderPoc(
                Environment.GetEnvironmentVariable("MUSICLOOM_APPLE_DEVELOPER_TOKEN"),
                Environment.GetEnvironmentVariable("MUSICLOOM_APPLE_WEB_BEARER"),
                Environment.GetEnvironmentVariable("MUSICLOOM_APPLE_MEDIA_USER_TOKEN"),
                http: _http),
            CloudLyricsSource.Spotify => new SpotifyProviderPoc(
                Environment.GetEnvironmentVariable("MUSICLOOM_SPOTIFY_OAUTH_TOKEN"),
                Environment.GetEnvironmentVariable("MUSICLOOM_SPOTIFY_LYRICS_BEARER"),
                http: _http),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

    private static CloudLyricsSource Source(ExternalProvider provider) =>
        provider switch
        {
            ExternalProvider.Migu => CloudLyricsSource.MiguMusic,
            ExternalProvider.AppleMusic => CloudLyricsSource.AppleMusic,
            ExternalProvider.Spotify => CloudLyricsSource.Spotify,
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

    private static ExternalProvider Source(CloudLyricsSource provider) =>
        provider switch
        {
            CloudLyricsSource.MiguMusic => ExternalProvider.Migu,
            CloudLyricsSource.AppleMusic => ExternalProvider.AppleMusic,
            CloudLyricsSource.Spotify => ExternalProvider.Spotify,
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

    private static CloudLyricsCandidate ToCloudCandidate(ProviderTrack track) =>
        new(Source(track.Provider), track.Id, track.Title, track.Artist,
            track.Album, track.DurationMilliseconds,
            CoverUrl: NormalizeExternalArtwork(track.ArtworkUrl,
                Source(track.Provider)), AlbumId: track.AlbumId,
            ContentId: track.ContentId, CopyrightId: track.CopyrightId,
            ProviderSongId: track.SongId, PreviewUrl: track.PreviewUrl,
            StreamUrl: track.StreamUrl,
            ExternalUrl: track.ExternalUrl,
            LrcUrl: track.LrcUrl, TrcUrl: track.TrcUrl, MrcUrl: track.MrcUrl);

    private static ProviderTrack ToExternalTrack(CloudLyricsCandidate song) =>
        new(Source(song.Source), song.Id, song.Title, song.Artist,
            song.Album, song.DurationMilliseconds, song.CoverUrl,
            song.ExternalUrl, song.ContentId, song.CopyrightId,
            song.ProviderSongId, song.AlbumId,
            song.LrcUrl, song.TrcUrl, song.MrcUrl,
            song.PreviewUrl, song.StreamUrl);

    private async Task<IReadOnlyList<CloudLyricsCandidate>>
        SearchExternalProviderAsync(CloudLyricsSource source, string query,
            CancellationToken ct)
    {
        IExternalMusicProvider adapter = await CreateExternalProviderAsync(source, ct);
        try
        {
            IReadOnlyList<ProviderTrack> matches =
                await adapter.SearchAsync(query, ct);
            return matches.Select(ToCloudCandidate).ToArray();
        }
        finally
        {
            (adapter as IDisposable)?.Dispose();
        }
    }

    private async Task<LrcDocument?> GetExternalProviderLyricsAsync(
        CloudLyricsCandidate candidate, CancellationToken ct)
    {
        IExternalMusicProvider adapter = await CreateExternalProviderAsync(candidate.Source, ct);
        try
        {
            ProviderLyricBundle? bundle = await adapter.GetLyricsAsync(
                ToExternalTrack(candidate), ct);
            return bundle is null ? null : MergeExternalLyrics(bundle);
        }
        finally
        {
            (adapter as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Never drop a row because it looks like a song name, singer name,
    /// composer credit, or a band member. Only attach real word timestamps
    /// to exact original lines. Keep unmatched original/MRC rows verbatim.
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

    /// <summary>
    /// SAT-only Apple full-song HLS attempt. The private enhancedHls manifest
    /// may declare media protection that native WinUI cannot decode. Keep
    /// ordinary preview behavior unless the user explicitly enables the
    /// experimental flow in their process environment.
    /// </summary>
    public static bool PreferFullAppleHls =>
        Environment.GetEnvironmentVariable(
            "MUSICLOOM_APPLE_PREFER_FULL_HLS") == "1";

    private async Task<CloudAudioPreviewResult>
        GetExternalAudioPreviewAsync(CloudLyricsCandidate candidate,
            CancellationToken ct)
    {
        IExternalMusicProvider adapter = await CreateExternalProviderAsync(candidate.Source, ct);
        try
        {
            if (candidate.Source == CloudLyricsSource.AppleMusic
                && PreferFullAppleHls
                && adapter is AppleMusicProviderPoc apple)
            {
                try
                {
                    Uri? manifest = await apple.ResolveEnhancedHlsAsync(
                        ToExternalTrack(candidate), ct);
                    Uri? accepted = NormalizeStreamUrl(
                        manifest?.AbsoluteUri, candidate.Source);
                    if (accepted is not null)
                        return new(accepted, CloudAudioPreviewStatus.Playable,
                            IsFullLength: true);
                }
                catch (ProviderAuthorizationException)
                {
                    // Private full-song credentials missing; the ordinary
                    // officially catalogued preview may still be usable.
                }
                catch (HttpRequestException)
                {
                    // Unavailable full stream does not erase a valid preview.
                }
                catch (System.Text.Json.JsonException)
                {
                    // Service shape changed; fall back without faking audio.
                }
            }
            ProviderPlayback grant = await adapter.ResolvePlaybackAsync(
                ToExternalTrack(candidate), ct);
            Uri? uri = grant.Disposition is
                PlaybackDisposition.AuthorizedAudioUrl
                or PlaybackDisposition.AuthorizedPreviewUrl
                    ? NormalizeStreamUrl(grant.AuthorizedUri?.AbsoluteUri,
                        candidate.Source) : null;

            return new(uri, uri is not null
                ? CloudAudioPreviewStatus.Playable
                : grant.Disposition is PlaybackDisposition.Unavailable
                    ? CloudAudioPreviewStatus.Restricted
                    : CloudAudioPreviewStatus.ProviderUnavailable);
        }
        finally
        {
            (adapter as IDisposable)?.Dispose();
        }
    }

    private async Task<string?> GetExternalArtworkAsync(
        CloudLyricsCandidate candidate, CancellationToken ct)
    {
        string? cover = NormalizeExternalArtwork(
            candidate.CoverUrl, candidate.Source);
        if (cover is not null) return cover;
        IExternalMusicProvider adapter = await CreateExternalProviderAsync(candidate.Source, ct);
        try
        {
            ProviderTrack? details = await adapter.GetDetailsAsync(
                ToExternalTrack(candidate), ct);
            return NormalizeExternalArtwork(details?.ArtworkUrl, candidate.Source);
        }
        finally
        {
            (adapter as IDisposable)?.Dispose();
        }
    }

    internal static string? NormalizeExternalArtwork(
        string? url, CloudLyricsSource source)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || uri.HostNameType != UriHostNameType.Dns)
            return null;
        string host = uri.Host;
        static bool Domain(string candidate, string root) =>
            candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.EndsWith("." + root, StringComparison.OrdinalIgnoreCase);
        bool allowed = source switch
        {
            CloudLyricsSource.MiguMusic => Domain(host, "migu.cn"),
            CloudLyricsSource.AppleMusic => Domain(host, "mzstatic.com"),
            CloudLyricsSource.Spotify => Domain(host, "scdn.co"),
            _ => false,
        };
        return allowed ? uri.AbsoluteUri : null;
    }
}
