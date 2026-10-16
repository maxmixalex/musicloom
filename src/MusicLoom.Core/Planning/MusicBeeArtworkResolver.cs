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

    public static IEnumerable<MusicBeeArtworkCandidate> Enumerate(
        string audioPath, TrackMetadata metadata,
        MusicBeeArtworkPreferences preferences)
    {
        string? directory = Path.GetDirectoryName(audioPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            yield break;

        string[] images;
        try
        {
            images = Directory.EnumerateFiles(directory, "*",
                    SearchOption.TopDirectoryOnly)
                .Where(path => ImageExtensions.Contains(
                    System.IO.Path.GetExtension(path)))
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (IOException) { images = []; }
        catch (UnauthorizedAccessException) { images = []; }
        if (images.Length == 0) yield break;

        HashSet<string> yielded = new(StringComparer.OrdinalIgnoreCase);
        foreach (string template in preferences.Filters)
        {
            if (string.Equals(template, "Embedded",
                StringComparison.OrdinalIgnoreCase))
            {
                yield return new MusicBeeArtworkCandidate(null, true);
                continue;
            }

            string rule = Substitute(template, audioPath, metadata);
            // Only resolve artwork alongside its owning source track.
            // Do not allow a user-supplied template to traverse to some
            // unrelated path outside the library folder.
            if (rule.Contains('/') || rule.Contains('\\')
                || rule is "." or ".." || rule.Length > 256)
                continue;

            string pattern = "^" + Regex.Escape(rule)
                .Replace("\\*", ".*", StringComparison.Ordinal)
                .Replace("\\?", ".", StringComparison.Ordinal) + "$";
            foreach (string image in images)
            {
                if (!Regex.IsMatch(System.IO.Path.GetFileName(image),
                    pattern, RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant))
                    continue;

                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(image),
                        "Folder", StringComparison.OrdinalIgnoreCase)
                    && AlbumArtLocator.IsLikelyWindowsMediaPlayerFolderCache(
                        directory, image, metadata.HasEmbeddedArt,
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
