using System.Xml;
using System.Xml.Linq;

namespace MusicLoom.Core.Planning;

/// <summary>
/// MusicBee stores the ordered "Set which pictures are displayed" filters in
/// TagArtworkScanFilter within MusicBee3Settings.ini (XML content despite
/// the .ini extension). The order is authoritative when a file is available.
/// </summary>
public sealed record MusicBeeArtworkPreferences(
    IReadOnlyList<string> Filters, bool LoadedFromMusicBee,
    bool ScanSubfolders = false)
{
    public static readonly MusicBeeArtworkPreferences Fallback = new(
        ["Cover.*", "Artwork.*", "Front.*",
         "<Filename>.*", "Embedded", "Back.*", "Folder.jpg"], false);

    private static readonly object CacheLock = new();
    private static string? _cachedPath;
    private static DateTime _cachedWriteTime;
    private static MusicBeeArtworkPreferences? _cached;

    public static MusicBeeArtworkPreferences Load(
        string? musicBeePlaylistDirectory = null,
        string? sourceLibraryDirectory = null)
    {
        foreach (string path in CandidateSettingsPaths(
            musicBeePlaylistDirectory, sourceLibraryDirectory))
        {
            try
            {
                if (!File.Exists(path)) continue;
                DateTime modified = File.GetLastWriteTimeUtc(path);
                lock (CacheLock)
                {
                    if (_cached is not null
                        && string.Equals(_cachedPath, path,
                            StringComparison.OrdinalIgnoreCase)
                        && _cachedWriteTime == modified)
                        return _cached;
                }
                MusicBeeArtworkPreferences? result = Read(path);
                if (result is null) continue;
                lock (CacheLock)
                {
                    _cachedPath = path;
                    _cachedWriteTime = modified;
                    _cached = result;
                }
                return result;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }
        return Fallback;
    }

    /// <summary>
    /// Parses a MusicBee settings snapshot without guessing at undocumented
    /// storage encodings. Malformed or absent filters fall back safely.
    /// </summary>
    public static MusicBeeArtworkPreferences? Read(string path)
    {
        try
        {
            using XmlReader reader = XmlReader.Create(path,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                });
            XDocument xml = XDocument.Load(reader);
            XElement? collection = xml.Descendants()
                .FirstOrDefault(static element =>
                    element.Name.LocalName == "TagArtworkScanFilter");
            if (collection is null) return null;
            string[] rules = collection.Elements()
                .Where(static element => element.Name.LocalName == "string")
                .Select(static element => element.Value.Trim())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(64)
                .ToArray();
            // An empty filter list is a legitimate MusicBee choice: do not
            // silently resurrect "Embedded" or other disabled sources.
            bool subfolders = bool.TryParse(
                xml.Descendants().FirstOrDefault(static element =>
                    element.Name.LocalName == "TagArtworkScanFolders")?.Value,
                out bool recursive) && recursive;
            return new MusicBeeArtworkPreferences(rules, true, subfolders);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or XmlException
            or ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<string> CandidateSettingsPaths(
        string? playlists, string? library)
    {
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        List<string> paths = [];

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            try
            {
                string path = Path.GetFullPath(
                    Environment.ExpandEnvironmentVariables(candidate));
                if (visited.Add(path)) paths.Add(path);
            }
            catch (Exception e) when (e is ArgumentException
                or NotSupportedException or PathTooLongException) { }
        }

        // Documented opt-in override for nonstandard portable installations.
        Add(Environment.GetEnvironmentVariable("MUSICBEE_SETTINGS_PATH"));

        // Resolve the explicitly configured portable MusicBee first. A
        // different installed MusicBee profile must not silently override
        // the library's own portable settings.
        foreach (string? root in new[] { playlists, library })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            DirectoryInfo? parent;
            try { parent = new DirectoryInfo(Path.GetFullPath(root)); }
            catch (Exception e) when (e is ArgumentException
                or NotSupportedException or PathTooLongException)
            {
                parent = null;
            }
            for (int level = 0; parent is not null && level < 6;
                 level++, parent = parent.Parent)
            {
                Add(Path.Combine(parent.FullName, "AppData",
                    "MusicBee3Settings.ini"));
                Add(Path.Combine(parent.FullName, "AppData",
                    "MusicBee3Settings.xml"));
            }
        }

        // Installed edition remains the fallback when no matching portable
        // settings file was found in either configured location.
        string roaming = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(roaming))
        {
            Add(Path.Combine(roaming, "MusicBee", "MusicBee3Settings.ini"));
            Add(Path.Combine(roaming, "MusicBee", "MusicBee3Settings.xml"));
        }

        return paths;
    }
}
