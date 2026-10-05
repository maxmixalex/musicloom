namespace MusicLoom.Core.Planning;

public static class PlaylistMapper
{
    public static IReadOnlyList<string> ParseM3u(IEnumerable<string> lines)
    {
        List<string> paths = [];
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            paths.Add(line);
        }

        return paths;
    }

    public static IReadOnlyList<string> Map(
        IEnumerable<string> sourcePaths,
        IReadOnlyDictionary<string, string> pathMapping,
        string destinationDirectory,
        string destinationPlaylistBase)
    {
        List<string> output = [];
        foreach (string sourcePath in sourcePaths)
        {
            if (!pathMapping.TryGetValue(sourcePath, out string? destinationPath))
            {
                continue;
            }

            string relative = Path.GetRelativePath(destinationDirectory, destinationPath)
                .Replace('\\', '/');
            string basePath = destinationPlaylistBase.Replace('\\', '/').TrimEnd('/');
            output.Add($"{basePath}/{relative}");
        }

        return output;
    }
}
