using System.Text;

namespace MusicLoom.Core.Planning;

public static class LibraryLayout
{
    private static readonly HashSet<char> IllegalFileNameCharacters =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedWindowsNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "CLOCK$",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
        };

    public static string GetDestinationPath(
        MusicFile musicFile,
        string destinationRoot)
    {
        string? organizingArtist =
            musicFile.AlbumArtist ?? musicFile.Artist;
        string directory = destinationRoot;

        if (!string.IsNullOrWhiteSpace(organizingArtist)
            && !string.IsNullOrWhiteSpace(musicFile.Album))
        {
            directory = Path.Combine(
                destinationRoot,
                SanitizeDirectoryName(organizingArtist),
                SanitizeDirectoryName(musicFile.Album));
        }
        else if (!string.IsNullOrWhiteSpace(organizingArtist))
        {
            directory = Path.Combine(
                destinationRoot,
                SanitizeDirectoryName(organizingArtist));
        }

        string destination = Path.Combine(
            directory,
            Path.GetFileName(musicFile.SourcePath));

        EnsureWithinDestinationRoot(
            destinationRoot,
            destination);
        return destination;
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
            builder.Append(
                character < 0x20
                || IllegalFileNameCharacters.Contains(character)
                    ? ' '
                    : character);
        }

        return builder.ToString().Trim();
    }

    public static string SanitizeDirectoryName(string name)
    {
        string value = SanitizeFileName(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return "_";
        }

        if (value.StartsWith(".", StringComparison.Ordinal))
        {
            value = "_" + value;
        }

        if (value.EndsWith(".", StringComparison.Ordinal))
        {
            value += "_";
        }

        string deviceStem = value.Split('.', 2)[0];
        if (ReservedWindowsNames.Contains(deviceStem))
        {
            value = "_" + value;
        }

        return value;
    }

    public static string NormalizeWindowsDirectoryName(string path)
    {
        string name = Path.GetFileName(path);
        string normalized = SanitizeDirectoryName(name);
        if (string.Equals(
                name,
                normalized,
                StringComparison.Ordinal))
        {
            return path;
        }

        return Path.Combine(
            Path.GetDirectoryName(path) ?? string.Empty,
            normalized);
    }

    private static void EnsureWithinDestinationRoot(
        string destinationRoot,
        string destinationPath)
    {
        string root = Path.GetFullPath(destinationRoot);
        string destination = Path.GetFullPath(destinationPath);
        string relative = Path.GetRelativePath(root, destination);

        if (Path.IsPathRooted(relative)
            || string.Equals(
                relative,
                "..",
                StringComparison.Ordinal)
            || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
            || relative.StartsWith(
                ".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Generated destination '{destination}' escapes library root '{root}'.");
        }
    }
}
