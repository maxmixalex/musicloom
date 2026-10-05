using MusicLoom.Core.Configuration;

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

            LibraryPathSafety.Validate(new MusicLoomSettings
            {
                Path = new PathSettings
                {
                    SourceDirectory = source,
                    DestinationDirectory = sibling,
                },
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ExpectInvalid(MusicLoomSettings settings)
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
}
