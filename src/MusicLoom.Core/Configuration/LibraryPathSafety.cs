namespace MusicLoom.Core.Configuration;

public static class LibraryPathSafety
{
    public static void Validate(MusicLoomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.Path.SourceDirectory))
        {
            throw new InvalidDataException(
                "Source library path must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(settings.Path.DestinationDirectory))
        {
            throw new InvalidDataException(
                "Destination library path must not be empty.");
        }

        string source = Normalize(settings.Path.SourceDirectory);
        string destination = Normalize(settings.Path.DestinationDirectory);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(source, destination, comparison))
        {
            throw new InvalidDataException(
                "Source and destination libraries must be different directories.");
        }

        string sourcePrefix = source + Path.DirectorySeparatorChar;
        if (destination.StartsWith(sourcePrefix, comparison))
        {
            throw new InvalidDataException(
                "Destination library must not be inside the source library. " +
                "That would cause MusicLoom to rescan its own output.");
        }

        string destinationPrefix =
            destination + Path.DirectorySeparatorChar;
        if (source.StartsWith(destinationPrefix, comparison))
        {
            throw new InvalidDataException(
                "Source library must not be inside the destination library. " +
                "That layout could place generated output back inside the source tree.");
        }
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
}
