namespace MusicLoom.Core.Configuration;

public static class LibraryPathSafety
{
    public static void Validate(MusicLoomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(
                settings.Path.SourceDirectory))
        {
            throw new InvalidDataException(
                "Source library path must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(
                settings.Path.DestinationDirectory))
        {
            throw new InvalidDataException(
                "Destination library path must not be empty.");
        }

        string source =
            Normalize(settings.Path.SourceDirectory);
        string destination =
            Normalize(settings.Path.DestinationDirectory);
        StringComparison comparison =
            PathComparison;

        if (string.Equals(
                source,
                destination,
                comparison))
        {
            throw new InvalidDataException(
                "Source and destination libraries must be different directories.");
        }

        if (IsInside(destination, source, comparison))
        {
            throw new InvalidDataException(
                "Destination library must not be inside the source library. " +
                "That would cause MusicLoom to rescan its own output.");
        }

        if (IsInside(source, destination, comparison))
        {
            throw new InvalidDataException(
                "Source library must not be inside the destination library. " +
                "That layout could place generated output back inside the source tree.");
        }
    }

    public static void ValidateStatePath(
        MusicLoomSettings settings,
        string statePath)
    {
        Validate(settings);

        if (string.IsNullOrWhiteSpace(statePath))
        {
            throw new InvalidDataException(
                "Incremental state path must not be empty.");
        }

        string source =
            Normalize(settings.Path.SourceDirectory);
        string state = Path.GetFullPath(statePath);
        StringComparison comparison =
            PathComparison;

        if (string.Equals(
                state,
                source,
                comparison)
            || IsInside(state, source, comparison))
        {
            throw new InvalidDataException(
                "Incremental state must not be stored inside the source library. " +
                "Live sync never writes into the source tree.");
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static bool IsInside(
        string candidate,
        string parent,
        StringComparison comparison)
    {
        string prefix =
            Path.TrimEndingDirectorySeparator(parent)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path));
}
