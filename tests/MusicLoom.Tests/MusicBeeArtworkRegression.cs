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

            // Audio-only directories must still honour the Embedded
            // rule even when there are ZERO external images to enumerate.
            string embeddedOnly = Path.Combine(root, "embedded-only");
            Directory.CreateDirectory(embeddedOnly);
            string embeddedSong = Path.Combine(embeddedOnly, "solo.flac");
            File.WriteAllBytes(embeddedSong, [1, 2, 3]);
            MusicBeeArtworkCandidate[] embeddedCandidates =
                MusicBeeArtworkResolver.Enumerate(
                    embeddedSong, metadata,
                    new MusicBeeArtworkPreferences(
                        ["Cover.*", "Embedded"], true)).ToArray();
            if (embeddedCandidates.Length != 1
                || !embeddedCandidates[0].IsEmbedded)
                throw new InvalidOperationException(
                    "Embedded artwork must not disappear in audio-only folders.");

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

            // A portable installation can also be discovered from the
            // configured music source when playlists are outside its root.
            string portableMusic = Path.Combine(portable, "Library", "Music");
            Directory.CreateDirectory(portableMusic);
            Environment.SetEnvironmentVariable("MUSICBEE_SETTINGS_PATH", null);
            MusicBeeArtworkPreferences fromSource =
                MusicBeeArtworkPreferences.Load(null, portableMusic);
            if (!fromSource.LoadedFromMusicBee
                || fromSource.Filters.FirstOrDefault() != "Folder.jpg")
                throw new InvalidOperationException(
                    "Portable MusicBee artwork settings were missed when the playlist path is unconfigured.");
            Environment.SetEnvironmentVariable(
                "MUSICBEE_SETTINGS_PATH",
                Path.Combine(portableAppData, "MusicBee3Settings.ini"));

            // Explicit MusicBee order wins, including when a user has
            // intentionally enabled Folder.jpg. The legacy WMP-cache
            // suppression applies ONLY to MusicLoom's fallback defaults.
            File.WriteAllBytes(Path.Combine(root, "AlbumArt.jpg"), [2]);
            if (MusicBeeArtworkResolver.Enumerate(audio, metadata, resolved)
                .First().Path != folder)
                throw new InvalidOperationException(
                    "MusicBee's explicit Folder.jpg first priority was ignored.");

            string cacheOnly = Path.Combine(root, "legacy-wmp-cache");
            Directory.CreateDirectory(cacheOnly);
            string cacheTrack = Path.Combine(cacheOnly, "legacy.mp3");
            File.WriteAllBytes(cacheTrack, [1]);
            File.WriteAllBytes(Path.Combine(cacheOnly, "Folder.jpg"), [9]);
            File.WriteAllBytes(Path.Combine(cacheOnly, "AlbumArt.jpg"), [9]);
            if (MusicBeeArtworkResolver.Enumerate(cacheTrack, metadata,
                    MusicBeeArtworkPreferences.Fallback)
                .Any(static candidate => candidate.Path is not null))
                throw new InvalidOperationException(
                    "Fallback scanning must not promote a legacy WMP cache.");

            // MusicBee can scan subfolders when TagArtworkScanFolders is set.
            string scans = Path.Combine(root, "Scans");
            Directory.CreateDirectory(scans);
            string scanCover = Path.Combine(scans, "Artwork.png");
            File.WriteAllBytes(scanCover, [4]);
            File.WriteAllText(settings, """
                <ApplicationSettings>
                  <TagArtworkScanFolders>true</TagArtworkScanFolders>
                  <TagArtworkScanFilter>
                    <string>Artwork.*</string><string>Embedded</string>
                  </TagArtworkScanFilter>
                </ApplicationSettings>
                """);
            MusicBeeArtworkPreferences deep =
                MusicBeeArtworkPreferences.Read(settings)!;
            if (!deep.ScanSubfolders
                || MusicBeeArtworkResolver.Enumerate(audio, metadata, deep)
                    .First().Path != scanCover)
                throw new InvalidOperationException(
                    "MusicBee's recursive artwork scan setting was ignored.");

            File.WriteAllText(settings, """
                <ApplicationSettings>
                  <TagArtworkScanFilter>
                    <string>Scans/Artwork.*</string>
                  </TagArtworkScanFilter>
                </ApplicationSettings>
                """);
            MusicBeeArtworkPreferences explicitSubfolder =
                MusicBeeArtworkPreferences.Read(settings)!;
            if (MusicBeeArtworkResolver.Enumerate(
                    audio, metadata, explicitSubfolder).First().Path != scanCover)
                throw new InvalidOperationException(
                    "MusicBee relative subfolder artwork rule was ignored.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MUSICBEE_SETTINGS_PATH", prior);
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
