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

            // The index used for library-size performance optimization
            // must produce the same ordered results as a fresh directory
            // enumeration, including the embedded sentinel.
            var cachedInventory = MusicBeeArtworkResolver.ListImages(root, options);
            MusicBeeArtworkCandidate[] indexed =
                MusicBeeArtworkResolver.Enumerate(
                    audio, metadata, options, cachedInventory).ToArray();
            if (!indexed.SequenceEqual(ordered))
                throw new InvalidOperationException(
                    "Reusing a MusicBee artwork directory index changed precedence.");

            // Multiple albums in ONE source folder are common in flat
            // MusicBee libraries. Broad "Cover.*" must never make them
            // share the first lexicographic named album cover.
            string various = Path.Combine(root, "shared-albums");
            Directory.CreateDirectory(various);
            string alpha = Path.Combine(various, "Cover.歌手甲.专辑甲.jpg");
            string beta = Path.Combine(various, "Cover.歌手乙.专辑乙.jpg");
            string generic = Path.Combine(various, "Cover.jpg");
            File.WriteAllBytes(alpha, [11]);
            File.WriteAllBytes(beta, [22]);
            File.WriteAllBytes(generic, [33]);
            string alphaSong = Path.Combine(various, "a.flac");
            string betaSong = Path.Combine(various, "b.flac");
            File.WriteAllBytes(alphaSong, [1]);
            File.WriteAllBytes(betaSong, [2]);
            TrackMetadata alphaMeta = new("歌手甲", "歌手甲",
                "专辑甲", HasEmbeddedArt: false);
            TrackMetadata betaMeta = new("歌手乙", "歌手乙",
                "专辑乙", HasEmbeddedArt: false);

            // Exact, ordered, localized filters extracted from an actual
            // MusicBee 3.5 zh-CN settings file. Never commit the complete
            // private INI: it contains personal paths, devices and plugins.
            string actualProfile = Path.Combine(root, "actual-zh-cn-artwork.ini");
            File.WriteAllText(actualProfile, """
                <?xml version="1.0" encoding="utf-8"?>
                <ApplicationSettings>
                  <TagArtworkScanFolders>false</TagArtworkScanFolders>
                  <TagArtworkScanFilter>
                    <string>嵌入的</string>
                    <string>Cover.&lt;专辑演出者&gt;.&lt;专辑&gt;.*</string>
                    <string>Cover.&lt;演出者&gt;.&lt;专辑&gt;.*</string>
                    <string>Cover.&lt;专辑演出者&gt;.*</string>
                    <string>Cover.&lt;演出者&gt;.*</string>
                    <string>Cover.*</string>
                    <string>Artwork.*</string>
                    <string>Front.*</string>
                    <string>Back.*</string>
                    <string>&lt;文件名&gt;.*</string>
                    <string>Embedded</string>
                    <string>Folder.jpg</string>
                  </TagArtworkScanFilter>
                </ApplicationSettings>
                """);
            MusicBeeArtworkPreferences realChinese =
                MusicBeeArtworkPreferences.Read(actualProfile)
                ?? throw new InvalidOperationException(
                    "The actual MusicBee zh-CN artwork configuration failed to parse.");
            if (realChinese.Filters.Count != 12
                || realChinese.Filters[0] != "嵌入的"
                || realChinese.Filters[1] != "Cover.<专辑演出者>.<专辑>.*"
                || realChinese.Filters[9] != "<文件名>.*"
                || realChinese.Filters[10] != "Embedded")
                throw new InvalidOperationException(
                    "The actual MusicBee zh-CN preference order was altered.");

            MusicBeeArtworkCandidate[] actualFirstAlbum =
                MusicBeeArtworkResolver.Enumerate(
                    alphaSong, alphaMeta, realChinese).ToArray();
            MusicBeeArtworkCandidate[] actualSecondAlbum =
                MusicBeeArtworkResolver.Enumerate(
                    betaSong, betaMeta, realChinese).ToArray();
            if (actualFirstAlbum.Length < 2
                || !actualFirstAlbum[0].IsEmbedded
                || !actualSecondAlbum[0].IsEmbedded
                || actualFirstAlbum.First(candidate => !candidate.IsEmbedded).Path
                    != alpha
                || actualSecondAlbum.First(candidate => !candidate.IsEmbedded).Path
                    != beta)
                throw new InvalidOperationException(
                    "Actual localized MusicBee settings lost embedded priority "
                    + "or assigned the same cover to independent albums.");

            // Same artist, MULTIPLE albums in a flat folder. The later
            // Cover.<Album Artist>.* rule must never reuse another album's
            // named image when the specific album template is missing.
            string sameArtist = Path.Combine(root, "multi-album-one-artist");
            Directory.CreateDirectory(sameArtist);
            string firstAlbumArt = Path.Combine(sameArtist,
                "Cover.共同演出者.第一张专辑.jpg");
            string secondAlbumArt = Path.Combine(sameArtist,
                "Cover.共同演出者.第二张专辑.jpg");
            string artistGeneric = Path.Combine(sameArtist,
                "Cover.共同演出者.jpg");
            string sharedGeneric = Path.Combine(sameArtist, "Cover.jpg");
            foreach (string image in new[]
                { firstAlbumArt, secondAlbumArt, artistGeneric, sharedGeneric })
                File.WriteAllBytes(image, [1, 2, 3]);
            string thirdAlbumSong = Path.Combine(sameArtist, "third.flac");
            File.WriteAllBytes(thirdAlbumSong, [1]);
            TrackMetadata missingAlbumCover = new("共同演出者", "共同演出者",
                "第三张专辑", HasEmbeddedArt: false);
            string? safeChoice = MusicBeeArtworkResolver.Enumerate(
                    thirdAlbumSong, missingAlbumCover, realChinese)
                .Where(candidate => !candidate.IsEmbedded)
                .Select(candidate => candidate.Path)
                .FirstOrDefault();
            if (safeChoice != artistGeneric)
                throw new InvalidOperationException(
                    "An artist-wide fallback selected a DIFFERENT album's cover.");

            File.Delete(artistGeneric);
            safeChoice = MusicBeeArtworkResolver.Enumerate(
                    thirdAlbumSong, missingAlbumCover, realChinese)
                .Where(candidate => !candidate.IsEmbedded)
                .Select(candidate => candidate.Path)
                .FirstOrDefault();
            if (safeChoice != sharedGeneric)
                throw new InvalidOperationException(
                    "Missing album artwork inherited another album instead "
                    + "of the deliberately generic Cover.jpg.");

            string filenameFolder = Path.Combine(root, "filename-only");
            Directory.CreateDirectory(filenameFolder);
            string audioByName = Path.Combine(filenameFolder, "あの曲.flac");
            string imageByName = Path.Combine(filenameFolder, "あの曲.png");
            File.WriteAllBytes(audioByName, [1]);
            File.WriteAllBytes(imageByName, [2]);
            MusicBeeArtworkCandidate[] byFilename =
                MusicBeeArtworkResolver.Enumerate(
                    audioByName, new TrackMetadata("Singer", "Singer", "Album",
                        HasEmbeddedArt: false), realChinese).ToArray();
            if (!byFilename.Any(candidate =>
                candidate.Path == imageByName))
                throw new InvalidOperationException(
                    "The localized <文件名> template never matched its cover.");

            MusicBeeArtworkPreferences broad = new(
                ["Cover.*", "Embedded"], true);
            string? aa = MusicBeeArtworkResolver.Enumerate(
                alphaSong, alphaMeta, broad).FirstOrDefault().Path;
            string? bb = MusicBeeArtworkResolver.Enumerate(
                betaSong, betaMeta, broad).FirstOrDefault().Path;
            if (aa != alpha || bb != beta)
                throw new InvalidOperationException(
                    "Shared-folder albums inherited another artist/album cover.");

            MusicBeeArtworkPreferences template = new(
                ["Cover.<专辑演出者>.<专辑>.*", "Cover.*"], true);
            if (MusicBeeArtworkResolver.Enumerate(
                    alphaSong, alphaMeta, template).First().Path != alpha
                || MusicBeeArtworkResolver.Enumerate(
                    betaSong, betaMeta, template).First().Path != beta)
                throw new InvalidOperationException(
                    "Localized MusicBee album artist / album cover naming templates were not resolved.");

            MusicBeeArtworkPreferences exactTemplate = new(
                ["Cover.<专辑演出者>.<专辑>"], true);
            if (MusicBeeArtworkResolver.Enumerate(
                    alphaSong, alphaMeta, exactTemplate).First().Path != alpha
                || MusicBeeArtworkResolver.Enumerate(
                    betaSong, betaMeta, exactTemplate).First().Path != beta)
                throw new InvalidOperationException(
                    "MusicBee album artist/album templates without an extension must match actual .jpg files.");

            MusicBeeArtworkPreferences englishTemplate = new(
                ["Cover.<Album Artist>.<Album>.*"], true);
            if (MusicBeeArtworkResolver.Enumerate(
                    alphaSong, alphaMeta, englishTemplate).First().Path != alpha)
                throw new InvalidOperationException(
                    "MusicBee English album-cover naming template was ignored.");

            TrackMetadata third = new("歌手丙", "歌手丙",
                "专辑丙", HasEmbeddedArt: true);
            MusicBeeArtworkCandidate noWrongCover =
                MusicBeeArtworkResolver.Enumerate(alphaSong, third, broad).First();
            if (!noWrongCover.IsEmbedded)
                throw new InvalidOperationException(
                    "Other albums' named covers were incorrectly used as generic fallback.");

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
