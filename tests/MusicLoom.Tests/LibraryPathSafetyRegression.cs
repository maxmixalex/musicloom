using MusicLoom.Core.Configuration;
using MusicLoom.Core.Planning;

internal static class LibraryPathSafetyRegression
{
    public static void Run()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "musicloom-path-safety-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string sibling = Path.Combine(root, "destination");
        string nested = Path.Combine(source, "output");
        string destinationParent = Path.Combine(root, "destination-parent");
        string sourceInsideDestination =
            Path.Combine(destinationParent, "source");

        Directory.CreateDirectory(source);
        Directory.CreateDirectory(sibling);

        try
        {
            ExpectInvalid(new MusicLoomSettings
            {
                Path = new PathSettings
                {
                    SourceDirectory = source,
                    DestinationDirectory = source,
                },
            });

            ExpectInvalid(new MusicLoomSettings
            {
                Path = new PathSettings
                {
                    SourceDirectory = source,
                    DestinationDirectory = nested,
                },
            });

            Directory.CreateDirectory(sourceInsideDestination);
            ExpectInvalid(new MusicLoomSettings
            {
                Path = new PathSettings
                {
                    SourceDirectory = sourceInsideDestination,
                    DestinationDirectory = destinationParent,
                },
            });

            ExpectInvalid(new MusicLoomSettings
            {
                Path = new PathSettings
                {
                    SourceDirectory = string.Empty,
                    DestinationDirectory = sibling,
                },
            });

            ExpectInvalid(new MusicLoomSettings
            {
                Path = new PathSettings
                {
                    SourceDirectory = source,
                    DestinationDirectory = string.Empty,
                },
            });

            MusicLoomSettings valid = new()
            {
                Path = new PathSettings
                {
                    SourceDirectory = source,
                    DestinationDirectory = sibling,
                },
            };

            LibraryPathSafety.Validate(valid);

            ExpectInvalidState(
                valid,
                Path.Combine(
                    source,
                    ".musicloom",
                    "state.json"));
            ExpectInvalidState(
                valid,
                Path.Combine(source, "state.json"));

            LibraryPathSafety.ValidateStatePath(
                valid,
                Path.Combine(
                    root,
                    ".musicloom",
                    "state.json"));

            AssertMetadataPathSafe(
                source,
                sibling,
                artist: "..",
                album: "Album");
            AssertMetadataPathSafe(
                source,
                sibling,
                artist: "Artist",
                album: "..");
            AssertMetadataPathSafe(
                source,
                sibling,
                artist: ".hidden.",
                album: "Album");
            AssertMetadataPathSafe(
                source,
                sibling,
                artist: "CON",
                album: "Album");
            AssertMetadataPathSafe(
                source,
                sibling,
                artist: "Artist",
                album: "LPT1.txt");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertMetadataPathSafe(
        string sourceRoot,
        string destinationRoot,
        string artist,
        string album)
    {
        MusicFile file = new(
            SourcePath: Path.Combine(sourceRoot, "track.mp3"),
            SourceDirectory: sourceRoot,
            Artist: artist,
            AlbumArtist: artist,
            Album: album,
            HasEmbeddedArt: false);

        string destination =
            LibraryLayout.GetDestinationPath(
                file,
                destinationRoot);

        string fullRoot =
            Path.GetFullPath(destinationRoot);
        string fullDestination =
            Path.GetFullPath(destination);
        string relative =
            Path.GetRelativePath(
                fullRoot,
                fullDestination);

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
            throw new InvalidOperationException(
                $"Metadata path escaped destination root: {artist} / {album} -> {fullDestination}");
        }

        foreach (string component in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or "..")
            {
                throw new InvalidOperationException(
                    $"Unsafe path component '{component}' remained in '{relative}'.");
            }
        }
    }

    private static void ExpectInvalid(
        MusicLoomSettings settings)
    {
        try
        {
            LibraryPathSafety.Validate(settings);
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Expected path configuration to be rejected.");
    }

    private static void ExpectInvalidState(
        MusicLoomSettings settings,
        string statePath)
    {
        try
        {
            LibraryPathSafety.ValidateStatePath(
                settings,
                statePath);
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected state path '{statePath}' to be rejected.");
    }
}
