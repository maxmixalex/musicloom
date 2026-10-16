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
            if (IsEmbeddedArtworkRule(template))
            {
                // MusicBee serializes this entry in the UI language.
                // "嵌入的" is the FIRST rule in real Chinese profiles;
                // skipping it makes the broad Cover.* fallback incorrectly
                // share a single image across unrelated albums.
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
            bool broadAlbumCover = !hasRelativeDirectory
                && IsBroadArtworkRule(rule);
            string pattern = "^" + Regex.Escape(rule)
                .Replace("\\*", ".*", StringComparison.Ordinal)
                .Replace("\\?", ".", StringComparison.Ordinal) + "$";
            // A broad Cover.* pattern must not grab a DIFFERENT album's
            // Cover.<Album Artist>.<Album>.jpg from a shared folder. Prefer
            // the matching tagged cover even when the broad filter appears
            // above MusicBee's exact naming template in the settings.
            string[] prioritizedImages = broadAlbumCover
                ? PrioritizeAlbumArtwork(images, rule, metadata)
                : images.ToArray();
            foreach (string image in prioritizedImages)
            {
                string candidateName = hasRelativeDirectory
                    ? Path.GetRelativePath(directory, image)
                        .Replace('\\', '/')
                    : System.IO.Path.GetFileName(image);
                bool matched = Regex.IsMatch(candidateName, pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!matched)
                {
                    // "Cover.<Album Artist>.<Album>" is a legal MusicBee
                    // filename template WITHOUT a .jpg suffix. Compare
                    // the stem, otherwise no images can ever match it.
                    bool hasImageExtension = ImageExtensions.Contains(
                        Path.GetExtension(rule));
                    if (!hasImageExtension
                        && !rule.Contains('*') && !rule.Contains('?'))
                    {
                        string withoutExtension = Path.ChangeExtension(
                            candidateName, null).TrimEnd('.');
                        matched = Regex.IsMatch(withoutExtension, pattern,
                            RegexOptions.IgnoreCase
                                | RegexOptions.CultureInvariant);
                    }
                }
                if (!matched) continue;

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

    private static bool IsEmbeddedArtworkRule(string text) =>
        string.Equals(text, "Embedded", StringComparison.OrdinalIgnoreCase)
        || text is "嵌入的" or "内嵌" or "內嵌";

    private static bool IsBroadArtworkRule(string rule)
    {
        string lower = rule.ToLowerInvariant();
        return lower is "cover.*" or "artwork.*"
            or "front.*" or "back.*";
    }

    private static string[] PrioritizeAlbumArtwork(
        IReadOnlyList<string> images, string rule, TrackMetadata metadata)
    {
        string prefix = rule[..rule.IndexOf('.')];
        string artist = Clean(metadata.AlbumArtist);
        if (artist.Length == 0) artist = Clean(metadata.Artist);
        string album = Clean(metadata.Album);
        string specific = artist.Length > 0 && album.Length > 0
            ? prefix + "." + artist + "." + album : "";
        string alternate = album.Length > 0
            ? prefix + "." + album : "";

        string[] belonging = images
            .Where(path =>
            {
                string stem = System.IO.Path.GetFileNameWithoutExtension(path);
                return specific.Length > 0
                    && string.Equals(stem, specific,
                        StringComparison.OrdinalIgnoreCase)
                    || alternate.Length > 0
                    && string.Equals(stem, alternate,
                        StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        if (belonging.Length > 0)
        {
            // Track-specific candidates are the only safe first choice in
            // a shared directory of several independent albums.
            return belonging.Concat(images.Where(path =>
                string.Equals(System.IO.Path.GetFileNameWithoutExtension(path),
                    prefix, StringComparison.OrdinalIgnoreCase))).ToArray();
        }

        bool containsNamedAlbums = images.Any(path =>
            System.IO.Path.GetFileNameWithoutExtension(path)
                .StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase));
        if (containsNamedAlbums)
        {
            // Never silently assign somebody else's album-specific cover.
            // Embedded artwork or the next rule can still be selected.
            return [];
        }
        return images.ToArray();
    }

    private static string Substitute(
        string pattern, string audioPath, TrackMetadata metadata)
    {
        string result = pattern;
        static string R(string value, string token, string? replacement) =>
            value.Replace(token, Clean(replacement),
                StringComparison.OrdinalIgnoreCase);

        result = R(result, "<Filename>",
            Path.GetFileNameWithoutExtension(audioPath));
        result = R(result, "<Album Artist>", metadata.AlbumArtist);
        result = R(result, "<AlbumArtist>", metadata.AlbumArtist);
        result = R(result, "<Album>", metadata.Album);
        result = R(result, "<Artist>", metadata.Artist);
        result = R(result, "<Title>", metadata.Title);

        // MusicBee tag templates also appear translated in localized
        // settings. These aliases are labels, not a different tag schema.
        result = R(result, "<文件名>",
            Path.GetFileNameWithoutExtension(audioPath));
        result = R(result, "<檔案名稱>",
            Path.GetFileNameWithoutExtension(audioPath));
        result = R(result, "<专辑演出者>", metadata.AlbumArtist);
        result = R(result, "<專輯演出者>", metadata.AlbumArtist);
        result = R(result, "<专辑艺术家>", metadata.AlbumArtist);
        result = R(result, "<專輯藝術家>", metadata.AlbumArtist);
        result = R(result, "<专辑>", metadata.Album);
        result = R(result, "<專輯>", metadata.Album);
        result = R(result, "<演出者>", metadata.Artist);
        result = R(result, "<艺术家>", metadata.Artist);
        result = R(result, "<標題>", metadata.Title);
        result = R(result, "<标题>", metadata.Title);
        return result;
    }

    private static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? string.Empty
            : LibraryLayout.SanitizeFileName(text);
}
