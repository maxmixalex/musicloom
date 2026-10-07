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

        bool hasWindowsMediaPlayerCache =
            Directory.EnumerateFiles(sourceDirectory)
                .Select(Path.GetFileNameWithoutExtension)
                .Any(static stem =>
                    stem is not null
                    && stem.StartsWith(
                        "AlbumArt",
                        StringComparison.OrdinalIgnoreCase));

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
            if (hasWindowsMediaPlayerCache
                && string.Equals(
                    stem,
                    "Folder",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return item;
        }

        return null;
    }
}
