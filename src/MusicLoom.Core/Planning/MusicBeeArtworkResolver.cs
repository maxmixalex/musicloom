using System.Text.RegularExpressions;
using MusicLoom.Core.Scanning;

namespace MusicLoom.Core.Planning;

/// <summary>
/// Yields external images and embedded tags in the exact order configured
/// by MusicBee. An embedded item is only tried when its turn is reached.
/// </summary>
public readonly record struct MusicBeeArtworkCandidate(
    string? Path, bool IsEmbedded)
{
    public string CacheKey(string audioFile) =>
        IsEmbedded ? "embedded:" + audioFile
            : "file:" + Path;
}

public static class MusicBeeArtworkResolver
{
    private static readonly HashSet<string> ImageExtensions = new(
        [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif",
         ".tif", ".tiff"], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ListImages(
        string directory, MusicBeeArtworkPreferences preferences)
    {
        bool recurse = preferences.ScanSubfolders
            || preferences.Filters.Any(static filter =>
                filter.Contains('/') || filter.Contains('\\'));
        try
        {
            return Directory.EnumerateFiles(directory, "*",
                    recurse ? SearchOption.AllDirectories
                            : SearchOption.TopDirectoryOnly)
                .Where(path => ImageExtensions.Contains(
                    System.IO.Path.GetExtension(path)))
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public static IEnumerable<MusicBeeArtworkCandidate> Enumerate(
        string audioPath, TrackMetadata metadata,
        MusicBeeArtworkPreferences preferences,
        IReadOnlyList<string>? indexedImages = null)
    {
        string? directory = Path.GetDirectoryName(audioPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            yield break;

        // During a library refresh the caller scans each audio directory
        // ONCE, then reuses that immutable listing across all songs.
        // Without this a folder holding 2,000 tracks could be enumerated
        // 2,000 times, producing effectively quadratic filesystem I/O.
        IReadOnlyList<string> images =
            indexedImages ?? ListImages(directory, preferences);
        // A library can contain only embedded artwork and no image files.
        // Never skip the configured Embedded rule merely because the
        // external candidates array is empty.
        HashSet<string> yielded = new(StringComparer.OrdinalIgnoreCase);
        foreach (string template in preferences.Filters)
        {
            if (string.Equals(template, "Embedded",
                StringComparison.OrdinalIgnoreCase))
            {
                yield return new MusicBeeArtworkCandidate(null, true);
                continue;
            }

            string rule = Substitute(template, audioPath, metadata)
                .Replace('\\', '/');
            // Reject path traversal, absolute paths and arbitrary roots.
            // Subfolder rules may target images BELOW this audio folder.
            if (rule.StartsWith('/') || rule.StartsWith("../")
                || rule.Contains("/../", StringComparison.Ordinal)
                || rule is "." or ".." || rule.Length > 256)
                continue;

            bool hasRelativeDirectory = rule.Contains('/');
            string pattern = "^" + Regex.Escape(rule)
                .Replace("\\*", ".*", StringComparison.Ordinal)
                .Replace("\\?", ".", StringComparison.Ordinal) + "$";
            foreach (string image in images)
            {
                string candidateName = hasRelativeDirectory
                    ? Path.GetRelativePath(directory, image)
                        .Replace('\\', '/')
                    : System.IO.Path.GetFileName(image);
                if (!Regex.IsMatch(candidateName,
                    pattern, RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant))
                    continue;

                if (!preferences.LoadedFromMusicBee
                    && string.Equals(
                        System.IO.Path.GetFileNameWithoutExtension(image),
                        "Folder", StringComparison.OrdinalIgnoreCase)
                    && AlbumArtLocator.IsLikelyWindowsMediaPlayerFolderCache(
                        Path.GetDirectoryName(image) ?? directory,
                        image, metadata.HasEmbeddedArt,
                        albumMissing: string.IsNullOrWhiteSpace(metadata.Album)))
                    continue;

                if (yielded.Add(image))
                    yield return new MusicBeeArtworkCandidate(image, false);
            }
        }
    }

    private static string Substitute(string pattern,
        string audioPath, TrackMetadata metadata)
    {
        string result = pattern;
        result = result.Replace("<Filename>",
            Path.GetFileNameWithoutExtension(audioPath),
            StringComparison.OrdinalIgnoreCase);
        result = result.Replace("<Album Artist>",
            Clean(metadata.AlbumArtist), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("<AlbumArtist>",
            Clean(metadata.AlbumArtist), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("<Artist>",
            Clean(metadata.Artist), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("<Album>",
            Clean(metadata.Album), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("<Title>",
            Clean(metadata.Title), StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty
            : LibraryLayout.SanitizeFileName(text);
}
