using MusicLoom.ProviderPoc;

static void Help()
{
    Console.WriteLine("""
        MusicLoom independent Provider PoC (NOT integrated into WinUI)
        Commands:
          search   migu|apple|spotify <query>
          detail   migu|apple|spotify <id> [--copyright-id=<id>]
          lyrics   migu|apple|spotify <id> [--copyright-id=<id>]
          playback migu|apple|spotify <id> [--copyright-id=<id>]
          play     spotify <track-id> --execute --device-id=<user-owned-device>
          stream-info apple <song-id>       Inspect full-song enhancedHls
          listen-local spotify <track-id> --execute (local go-librespot audio)
          status-local spotify <track-id>  Inspect actual decoded stream
          pause-local spotify <track-id>
          resume-local spotify <track-id>
          seek-local spotify <track-id> --position-ms=<milliseconds>

        Credentials (environment only, never saved):
          MUSICLOOM_APPLE_DEVELOPER_TOKEN
          MUSICLOOM_APPLE_WEB_BEARER
          MUSICLOOM_APPLE_MEDIA_USER_TOKEN
          MUSICLOOM_SPOTIFY_OAUTH_TOKEN
          MUSICLOOM_SPOTIFY_LYRICS_BEARER
          MUSICLOOM_GO_LIBRESPOT_URL (loopback HTTP only; default localhost:3678)

        No anonymous or borrowed session is substituted for missing tokens.
        Spotify can stream decoded audio locally through an independent
        go-librespot service and Windows WASAPI.
        Apple enhancedAssetUrls.enhancedHls provides full-song HLS manifests.
        The HLS probe reads media formats, not protected audio keys.
        Missing Migu stream URLs are reported unavailable, not guessed.
        Provider lyric rows including titles/performers/credits stay unchanged.
        """);
}

if (args.Length < 3
    || args[0] is "-h" or "--help"
    || args[1] is not ("migu" or "apple" or "spotify"))
{
    Help();
    return args.Length == 1 && args[0] is "-h" or "--help" ? 0 : 2;
}

string command = args[0].ToLowerInvariant();
string providerName = args[1].ToLowerInvariant();
string value = args[2];
string? Option(string key) => args.Skip(3)
    .FirstOrDefault(a => a.StartsWith("--" + key + "=", StringComparison.Ordinal))
    ?.Split('=', 2).Last();

IExternalMusicProvider client = providerName switch
{
    "migu" => new MiguProviderPoc(),
    "apple" => new AppleMusicProviderPoc(
        Environment.GetEnvironmentVariable("MUSICLOOM_APPLE_DEVELOPER_TOKEN"),
        Environment.GetEnvironmentVariable("MUSICLOOM_APPLE_WEB_BEARER"),
        Environment.GetEnvironmentVariable("MUSICLOOM_APPLE_MEDIA_USER_TOKEN")),
    "spotify" => new SpotifyProviderPoc(
        Environment.GetEnvironmentVariable("MUSICLOOM_SPOTIFY_OAUTH_TOKEN"),
        Environment.GetEnvironmentVariable("MUSICLOOM_SPOTIFY_LYRICS_BEARER")),
    _ => throw new ArgumentOutOfRangeException(nameof(providerName)),
};

try
{
    if (command == "search")
    {
        IReadOnlyList<ProviderTrack> results = await client.SearchAsync(value);
        foreach (ProviderTrack track in results) Print(track);
        Console.WriteLine(results.Count + " song(s) returned by provider.");
    }
    else if (command is "detail" or "lyrics" or "playback" or "play"
        or "stream-info" or "listen-local" or "status-local"
        or "pause-local" or "resume-local" or "seek-local")
    {
        ExternalProvider kind = client.Provider;
        string? copyright = Option("copyright-id");
        ProviderTrack lookup = new(kind, value, "", "", "",
            ContentId: kind == ExternalProvider.Migu && copyright is null
                ? value : null,
            CopyrightId: copyright);

        if (command == "detail")
        {
            ProviderTrack? result = await client.GetDetailsAsync(lookup);
            if (result is null) Console.WriteLine("No detail result.");
            else Print(result);
        }
        else if (command == "lyrics")
        {
            ProviderLyricBundle? lyrics = await client.GetLyricsAsync(lookup);
            if (lyrics is null) Console.WriteLine("Lyrics unavailable.");
            else
            {
                Console.WriteLine("-- ORIGINAL: RAW PROVIDER TEXT --");
                Console.WriteLine(lyrics.RawOriginal);
                if (lyrics.RawTranslation is not null)
                {
                    Console.WriteLine("-- TRANSLATION: RAW PROVIDER TEXT --");
                    Console.WriteLine(lyrics.RawTranslation);
                }
                Console.WriteLine("Timed lines: " + lyrics.Original.Lines.Count);
                Console.WriteLine("Word-timed lines: " + (lyrics.WordTimed?.Lines.Count ?? 0));
                Console.WriteLine("Additional Apple TTML localizations: "
                    + (lyrics.Localizations?.Count ?? 0));
            }
        }
        else if (command == "playback")
        {
            ProviderPlayback status = await client.ResolvePlaybackAsync(lookup);
            Console.WriteLine(status.Disposition + ": " + status.Explanation);
            if (status.AuthorizedUri is Uri granted)
                Console.WriteLine("Provider-granted URI host/path: "
                    + granted.Host + granted.AbsolutePath);
        }
        else if (command == "stream-info"
            && client is AppleMusicProviderPoc apple)
        {
            AppleHlsManifest? manifest = await apple.InspectEnhancedHlsAsync(lookup);
            if (manifest is null)
                Console.WriteLine("No provider-issued full-song enhancedHls manifest.");
            else
            {
                Console.WriteLine("FULL-SONG HLS PLAYLIST FOUND (not a preview)");
                Console.WriteLine("Host/path: "
                    + manifest.PlaylistUrl.Host + manifest.PlaylistUrl.AbsolutePath);
                Console.WriteLine("Variants: " + manifest.VariantCount);
                Console.WriteLine("Codecs: " + string.Join(", ", manifest.Codecs));
                Console.WriteLine("Protected: "
                    + (manifest.MediaPlaylistEncrypted is null
                        ? "Unknown on master playlist"
                        : manifest.MediaPlaylistEncrypted.Value ? "Declared" : "No HLS key declared"));
                Console.WriteLine("Stream identification does not prove audible playback.");
            }
        }
        else if (providerName == "spotify"
            && command is "listen-local" or "status-local"
                or "pause-local" or "resume-local" or "seek-local")
        {
            string endpoint = Environment.GetEnvironmentVariable(
                "MUSICLOOM_GO_LIBRESPOT_URL")
                ?? "http://127.0.0.1:3678";
            using SpotifyLocalStreamBridge local = new(endpoint);
            if (command == "listen-local")
            {
                if (!args.Contains("--execute", StringComparer.Ordinal))
                {
                    Console.Error.WriteLine(
                        "Explicit --execute is required to start real local audio.");
                    return 2;
                }
                bool accepted = await local.PlayTrackAsync(value);
                Console.WriteLine(accepted
                    ? "Local go-librespot accepted play; use status-local to inspect its stream."
                    : "Local go-librespot has no ready authorized session.");
            }
            else if (command == "status-local")
            {
                SpotifyLocalPlayerStatus? status = await local.GetStatusAsync();
                Console.WriteLine(status is null ? "No active session."
                    : $"Track: {status.TrackUri}; position={status.PositionMilliseconds}ms;"
                    + $" stopped={status.Stopped}, paused={status.Paused}, buffering={status.Buffering}");
            }
            else if (command == "pause-local")
                await local.PauseAsync();
            else if (command == "resume-local")
                await local.ResumeAsync();
            else if (command == "seek-local")
            {
                if (!int.TryParse(Option("position-ms"), out int position)
                    || position < 0)
                    throw new ArgumentException("Provide --position-ms=<non-negative int>.");
                await local.SeekAsync(position);
            }
        }
        else if (command == "play"
            && client is SpotifyProviderPoc spotify
            && args.Contains("--execute", StringComparer.Ordinal)
            && Option("device-id") is string device && device.Length > 0)
        {
            bool acknowledged = await spotify.PlayOnDeviceAsync(lookup, device);
            Console.WriteLine(acknowledged
                ? "Spotify accepted the device playback command (not proof of audible output)."
                : "Spotify returned a non-204 successful response.");
        }
        else
        {
            Help();
            return 2;
        }
    }
    else
    {
        Help();
        return 2;
    }
    return 0;
}
catch (ProviderAuthorizationException error)
{
    Console.Error.WriteLine("Authorization required: " + error.Message);
    return 3;
}
catch (HttpRequestException error)
{
    Console.Error.WriteLine("Provider transport failure: HTTP "
        + (error.StatusCode is null ? "unavailable" : ((int)error.StatusCode).ToString()));
    return 4;
}
catch (Exception error) when (error is InvalidDataException
    or System.Xml.XmlException or FormatException or ArgumentException)
{
    Console.Error.WriteLine("Provider response rejected: " + error.GetType().Name);
    return 5;
}
finally
{
    (client as IDisposable)?.Dispose();
}

static void Print(ProviderTrack track)
{
    Console.WriteLine("[" + track.Provider + "] " + track.Title
        + " / " + track.Artist + " / " + track.Album);
    Console.WriteLine("  id=" + track.Id + " contentId="
        + (track.ContentId ?? "") + " copyrightId=" + (track.CopyrightId ?? "")
        + " songId=" + (track.SongId ?? ""));
    Console.WriteLine("  durationMs=" + track.DurationMilliseconds
        + " artwork=" + (track.ArtworkUrl ?? "—")
        + " page=" + (track.ExternalUrl ?? "—"));
}
