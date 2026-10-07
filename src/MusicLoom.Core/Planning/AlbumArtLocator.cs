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

        string[] wmpAlbumArt =
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
                && wmpAlbumArt.Any(
                    albumArt =>
                        FilesHaveSameContent(
                            item,
                            albumArt)))
            {
                continue;
            }

            return item;
        }

        return null;
    }

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
