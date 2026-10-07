using MusicLoom.Core.Scanning;

namespace MusicLoom.Core.Planning;

public static class AlbumArtLocator
{
    private static readonly HashSet<string> GenericStems = new(
        ["Cover", "Artwork", "Front", "Back", "Folder"],
        StringComparer.OrdinalIgnoreCase);

    public static string? Find(
        string sourceDirectory,
        TrackMetadata metadata,
        bool onlyExplicit)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return null;
        }

        List<string> explicitStems = [];
        if (!string.IsNullOrWhiteSpace(metadata.AlbumArtist)
            && !string.IsNullOrWhiteSpace(metadata.Album))
        {
            explicitStems.Add(
                $"Cover.{LibraryLayout.SanitizeFileName(metadata.AlbumArtist)}.{LibraryLayout.SanitizeFileName(metadata.Album)}");
        }

        if (!string.IsNullOrWhiteSpace(metadata.Artist)
            && !string.IsNullOrWhiteSpace(metadata.Album)
            && !string.Equals(metadata.Artist, metadata.AlbumArtist, StringComparison.Ordinal))
        {
            explicitStems.Add(
                $"Cover.{LibraryLayout.SanitizeFileName(metadata.Artist)}.{LibraryLayout.SanitizeFileName(metadata.Album)}");
        }

        foreach (string item in Directory.EnumerateFiles(sourceDirectory))
        {
            string stem = Path.GetFileNameWithoutExtension(item);
            foreach (string explicitStem in explicitStems)
            {
                if (string.Equals(stem, explicitStem, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }
        }

        if (onlyExplicit)
        {
            return null;
        }

        foreach (string item in Directory.EnumerateFiles(sourceDirectory))
        {
            string stem =
                Path.GetFileNameWithoutExtension(item);

            if (!GenericStems.Contains(stem))
            {
                continue;
            }

            // Windows Media Player commonly drops AlbumArt*.jpg together
            // with Folder.jpg after extracting embedded artwork. Treat that
            // Folder.jpg as a cache artifact rather than intentional sidecar
            // artwork, while retaining standalone Folder.jpg compatibility.
            if (string.Equals(
                    stem,
                    "Folder",
                    StringComparison.OrdinalIgnoreCase)
                && IsLikelyWindowsMediaPlayerFolderCache(
                    sourceDirectory,
                    item))
            {
                continue;
            }

            return item;
        }

        return null;
    }

    internal static bool IsLikelyWindowsMediaPlayerFolderCache(
        string sourceDirectory,
        string folderPath)
    {
        if (!Directory.Exists(sourceDirectory)
            || !File.Exists(folderPath)
            || !string.Equals(
                Path.GetFileNameWithoutExtension(folderPath),
                "Folder",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        FileInfo folder =
            new(folderPath);

        string[] albumArtFiles =
            Directory.EnumerateFiles(
                    sourceDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Where(static path =>
                {
                    string? stem =
                        Path.GetFileNameWithoutExtension(
                            path);

                    return stem is not null
                        && stem.StartsWith(
                            "AlbumArt",
                            StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();

        if (albumArtFiles.Length == 0)
        {
            return false;
        }

        foreach (string albumArtPath
            in albumArtFiles)
        {
            if (FilesHaveSameContent(
                    folderPath,
                    albumArtPath))
            {
                return true;
            }
        }

        bool folderMarkedByWindows =
            HasWmpFileAttributes(
                folder.Attributes);

        bool hasStrongWmpName =
            albumArtFiles.Any(
                static path =>
                {
                    string stem =
                        Path.GetFileNameWithoutExtension(
                            path);

                    return string.Equals(
                            stem,
                            "AlbumArtSmall",
                            StringComparison.OrdinalIgnoreCase)
                        || (stem.StartsWith(
                                "AlbumArt_",
                                StringComparison.OrdinalIgnoreCase)
                            && (stem.EndsWith(
                                    "_Large",
                                    StringComparison.OrdinalIgnoreCase)
                                || stem.EndsWith(
                                    "_Small",
                                    StringComparison.OrdinalIgnoreCase)));
                });

        bool hasWindowsMarkedAlbumArt =
            albumArtFiles.Any(
                static path =>
                {
                    try
                    {
                        return HasWmpFileAttributes(
                            File.GetAttributes(
                                path));
                    }
                    catch
                    {
                        return false;
                    }
                });

        bool hasTightlyClusteredAlbumArt =
            albumArtFiles.Any(
                path =>
                {
                    try
                    {
                        FileInfo albumArt =
                            new(path);

                        return Math.Abs(
                            (albumArt.LastWriteTimeUtc
                                - folder.LastWriteTimeUtc)
                            .TotalSeconds)
                            <= 3;
                    }
                    catch
                    {
                        return false;
                    }
                });

        if (!hasTightlyClusteredAlbumArt)
        {
            return false;
        }

        // WMP usually writes Folder.jpg and one or more AlbumArt*.jpg files
        // in the same short burst. It may resize/re-encode Folder.jpg, so
        // byte equality alone is insufficient. A close timestamp must be
        // accompanied by a WMP-specific filename/attribute signal, except
        // for the classic AlbumArt.jpg + Folder.jpg pair which WMP also uses.
        bool hasClassicAlbumArtName =
            albumArtFiles.Any(
                static path =>
                    string.Equals(
                        Path.GetFileNameWithoutExtension(
                            path),
                        "AlbumArt",
                        StringComparison.OrdinalIgnoreCase));

        return folderMarkedByWindows
            || hasWindowsMarkedAlbumArt
            || hasStrongWmpName
            || albumArtFiles.Length >= 2
            || hasClassicAlbumArtName;
    }

    private static bool HasWmpFileAttributes(
        FileAttributes attributes) =>
        (attributes
            & (FileAttributes.Hidden
                | FileAttributes.System))
        != 0;

    private static bool FilesHaveSameContent(
        string leftPath,
        string rightPath)
    {
        FileInfo left =
            new(leftPath);
        FileInfo right =
            new(rightPath);

        if (!left.Exists
            || !right.Exists
            || left.Length != right.Length)
        {
            return false;
        }

        using FileStream leftStream =
            File.OpenRead(leftPath);
        using FileStream rightStream =
            File.OpenRead(rightPath);

        Span<byte> leftBuffer =
            stackalloc byte[8192];
        Span<byte> rightBuffer =
            stackalloc byte[8192];

        while (true)
        {
            int leftRead =
                leftStream.Read(leftBuffer);
            int rightRead =
                rightStream.Read(rightBuffer);

            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer[..leftRead]
                .SequenceEqual(
                    rightBuffer[..rightRead]))
            {
                return false;
            }
        }
    }
}
