using System.Text;

namespace MusicLoom.Core.Planning;

public static class LibraryLayout
{
    private static readonly HashSet<char> IllegalFileNameCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static string GetDestinationPath(MusicFile musicFile, string destinationRoot)
    {
        string? organizingArtist = musicFile.AlbumArtist ?? musicFile.Artist;
        string directory = destinationRoot;

        if (!string.IsNullOrWhiteSpace(organizingArtist) && !string.IsNullOrWhiteSpace(musicFile.Album))
        {
            directory = Path.Combine(
                destinationRoot,
                SanitizeFileName(organizingArtist),
                SanitizeFileName(musicFile.Album));
        }
        else if (!string.IsNullOrWhiteSpace(organizingArtist))
        {
            directory = Path.Combine(destinationRoot, SanitizeFileName(organizingArtist));
        }

        directory = NormalizeWindowsDirectoryName(directory);
        return Path.Combine(directory, Path.GetFileName(musicFile.SourcePath));
    }

    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        StringBuilder builder = new(name.Length);
        foreach (char character in name)
        {
            builder.Append(character < 0x20 || IllegalFileNameCharacters.Contains(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    public static string NormalizeWindowsDirectoryName(string path)
    {
        string name = Path.GetFileName(path);
        if (name.StartsWith(".", StringComparison.Ordinal))
        {
            return Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "_" + name);
        }

        if (name.EndsWith(".", StringComparison.Ordinal))
        {
            return Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, name + "_");
        }

        return path;
    }
}
