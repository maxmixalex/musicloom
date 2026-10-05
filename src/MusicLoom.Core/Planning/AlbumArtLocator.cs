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
            if (GenericStems.Contains(Path.GetFileNameWithoutExtension(item)))
            {
                return item;
            }
        }

        return null;
    }
}
