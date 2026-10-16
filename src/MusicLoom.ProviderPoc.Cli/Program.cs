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

        Credentials (environment only, never saved):
          MUSICLOOM_APPLE_DEVELOPER_TOKEN
          MUSICLOOM_APPLE_WEB_BEARER
          MUSICLOOM_APPLE_MEDIA_USER_TOKEN
          MUSICLOOM_SPOTIFY_OAUTH_TOKEN
          MUSICLOOM_SPOTIFY_LYRICS_BEARER

        No anonymous or borrowed session is substituted for missing tokens.
        Spotify full playback uses Premium Spotify Connect, not MediaPlayer.
        Apple full subscriber playback needs MusicKit, not a raw media URL.
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
    else if (command is "detail" or "lyrics" or "playback" or "play")
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
