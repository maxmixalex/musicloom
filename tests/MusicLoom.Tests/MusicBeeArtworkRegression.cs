using MusicLoom.Core.Planning;
using MusicLoom.Core.Scanning;

internal static class MusicBeeArtworkRegression
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "musicloom-mb-artwork-" + Guid.NewGuid().ToString("N"));
        string? prior = Environment.GetEnvironmentVariable(
            "MUSICBEE_SETTINGS_PATH");
        try
        {
            Directory.CreateDirectory(root);
            string audio = Path.Combine(root, "track.flac");
            File.WriteAllBytes(audio, [0, 1, 2]);
            string cover = Path.Combine(root, "Cover.jpg");
            string folder = Path.Combine(root, "Folder.jpg");
            string specific = Path.Combine(root, "track.png");
            File.WriteAllBytes(cover, [1]);
            File.WriteAllBytes(folder, [2]);
            File.WriteAllBytes(specific, [3]);
            TrackMetadata metadata = new("Artist", "Album Artist",
                "Album", HasEmbeddedArt: true, Title: "Song");

            string settings = Path.Combine(root, "MusicBee3Settings.ini");
            File.WriteAllText(settings, """
                <?xml version="1.0" encoding="utf-8"?>
                <ApplicationSettings>
                  <TagArtworkScanFilter>
                    <string>&lt;Filename&gt;.*</string>
                    <string>Embedded</string>
                    <string>Cover.*</string>
                  </TagArtworkScanFilter>
                </ApplicationSettings>
                """);
            MusicBeeArtworkPreferences options =
                MusicBeeArtworkPreferences.Read(settings)
                ?? throw new InvalidOperationException(
                    "Could not read XML stored in MusicBee3Settings.ini");
            if (!options.LoadedFromMusicBee
                || options.Filters.Count != 3
                || options.Filters[0] != "<Filename>.*")
                throw new InvalidOperationException(
                    "MusicBee TagArtworkScanFilter order was lost.");

            MusicBeeArtworkCandidate[] ordered =
                MusicBeeArtworkResolver.Enumerate(audio, metadata, options)
                    .ToArray();
            if (ordered.Length != 3
                || ordered[0].Path != specific
                || !ordered[1].IsEmbedded
                || ordered[2].Path != cover)
                throw new InvalidOperationException(
                    "MusicBee's track-specific/embedded/cover precedence was violated.");

            // Explicit MusicBee filter disabling is authoritative. A
            // standalone folder.jpg cannot be silently promoted.
            File.WriteAllText(settings, """
                <ApplicationSettings>
                  <TagArtworkScanFilter><string>Embedded</string></TagArtworkScanFilter>
                </ApplicationSettings>
                """);
            MusicBeeArtworkPreferences selected =
                MusicBeeArtworkPreferences.Read(settings)!;
            if (MusicBeeArtworkResolver.Enumerate(audio, metadata, selected)
                .Any(static candidate => !candidate.IsEmbedded))
                throw new InvalidOperationException(
                    "A disabled MusicBee cover source was unexpectedly scanned.");

            // A portable MusicBee installation can be found from its
            // configured playlists folder, even without an installer.
            string portable = Path.Combine(root, "PortableBee");
            string portableAppData = Path.Combine(portable, "AppData");
            string playlists = Path.Combine(portable, "Library", "Playlists");
            Directory.CreateDirectory(portableAppData);
            Directory.CreateDirectory(playlists);
            File.WriteAllText(Path.Combine(portableAppData,
                "MusicBee3Settings.ini"), """
                <ApplicationSettings>
                  <TagArtworkScanFilter>
                    <string>Folder.jpg</string><string>Cover.*</string>
                  </TagArtworkScanFilter>
                </ApplicationSettings>
                """);
            Environment.SetEnvironmentVariable(
                "MUSICBEE_SETTINGS_PATH",
                Path.Combine(portableAppData, "MusicBee3Settings.ini"));
            MusicBeeArtworkPreferences resolved =
                MusicBeeArtworkPreferences.Load(playlists);
            if (!resolved.LoadedFromMusicBee
                || resolved.Filters.FirstOrDefault() != "Folder.jpg"
                || MusicBeeArtworkResolver.Enumerate(
                    audio, metadata, resolved).First().Path != folder)
                throw new InvalidOperationException(
                    "Portable MusicBee cover priority was ignored.");

            // The original WMP cache-safety check still applies even when
            // a filter explicitly mentions Folder.jpg.
            File.WriteAllBytes(Path.Combine(root, "AlbumArt.jpg"), [2]);
            if (MusicBeeArtworkResolver.Enumerate(audio, metadata, resolved)
                .First().Path != cover)
                throw new InvalidOperationException(
                    "A WMP-generated Folder.jpg displaced a valid cover.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MUSICBEE_SETTINGS_PATH", prior);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
