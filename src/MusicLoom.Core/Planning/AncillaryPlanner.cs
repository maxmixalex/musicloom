using System.Text;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Scanning;

namespace MusicLoom.Core.Planning;

public sealed record AncillaryPlan(
    IReadOnlyList<CoverPlanEntry> Covers,
    IReadOnlyList<PlaylistPlanEntry> Playlists);

public static class AncillaryPlanner
{
    public static async Task<AncillaryPlan> BuildAsync(
        MusicLoomSettings settings,
        IReadOnlyList<PlanEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(entries);

        string destinationRoot = Path.GetFullPath(
            settings.Path.DestinationDirectory);

        MusicBeeArtworkPreferences artworkPreferences =
            MusicBeeArtworkPreferences.Load(
                settings.Path.SourcePlaylistDirectory);
        List<CoverPlanEntry> covers = BuildCovers(
            entries, destinationRoot, artworkPreferences);

        List<PlaylistPlanEntry> playlists = await BuildPlaylistsAsync(
            settings,
            entries,
            destinationRoot,
            cancellationToken);

        return new AncillaryPlan(covers, playlists);
    }

    private static List<CoverPlanEntry> BuildCovers(
        IReadOnlyList<PlanEntry> entries,
        string destinationRoot,
        MusicBeeArtworkPreferences artworkPreferences)
    {
        List<CoverPlanEntry> covers = [];

        foreach (IGrouping<string, PlanEntry> group in entries
            .Where(static entry =>
                entry.Status != PlanEntryStatus.Error
                && entry.DestinationPath is not null
                && entry.Metadata is not null)
            .GroupBy(
                static entry =>
                    Path.GetDirectoryName(entry.DestinationPath!)
                    ?? string.Empty,
                StringComparer.OrdinalIgnoreCase))
        {
            string albumDirectory = group.Key;
            if (PathsEqual(albumDirectory, destinationRoot))
            {
                continue;
            }

            PlanEntry first = group.First();
            TrackMetadata metadata = first.Metadata!;
            string sourceDirectory =
                Path.GetDirectoryName(first.SourcePath)
                ?? string.Empty;
            string? cover = null;
            bool embeddedWins = false;
            foreach (MusicBeeArtworkCandidate candidate in
                MusicBeeArtworkResolver.Enumerate(
                    first.SourcePath, metadata, artworkPreferences))
            {
                if (candidate.IsEmbedded)
                {
                    if (group.Any(static track =>
                        track.Metadata?.HasEmbeddedArt == true))
                    {
                        embeddedWins = true;
                        break;
                    }
                    continue;
                }
                cover = candidate.Path;
                break;
            }
            // MusicBee's configured Embedded rule is a real priority:
            // never overwrite an embedded-first album with a sidecar.
            if (embeddedWins) continue;
            if (cover is null)
            {
                AddStaleWmpCoverCleanup(
                    covers,
                    sourceDirectory,
                    albumDirectory,
                    metadata.HasEmbeddedArt,
                    albumMissing:
                        string.IsNullOrWhiteSpace(
                            metadata.Album));
                continue;
            }

            string destinationCover = Path.Combine(
                albumDirectory,
                "Cover" + Path.GetExtension(cover));

            covers.Add(new CoverPlanEntry(
                cover,
                destinationCover,
                NeedsWrite: !FilesAreIdentical(
                    cover,
                    destinationCover)));
        }

        return covers;
    }

    private static void AddStaleWmpCoverCleanup(
        List<CoverPlanEntry> covers,
        string sourceDirectory,
        string albumDirectory,
        bool embeddedArtPresent,
        bool albumMissing)
    {
        if (!Directory.Exists(sourceDirectory)
            || !Directory.Exists(albumDirectory))
        {
            return;
        }

        string? folderCache =
            Directory.EnumerateFiles(
                    sourceDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .FirstOrDefault(
                    static path =>
                        string.Equals(
                            Path.GetFileNameWithoutExtension(
                                path),
                            "Folder",
                            StringComparison.OrdinalIgnoreCase));

        if (folderCache is null
            || !AlbumArtLocator
                .IsLikelyWindowsMediaPlayerFolderCache(
                    sourceDirectory,
                    folderCache,
                    embeddedArtPresent,
                    albumMissing))
        {
            return;
        }

        foreach (string destinationCover
            in Directory.EnumerateFiles(
                albumDirectory,
                "Cover.*",
                SearchOption.TopDirectoryOnly))
        {
            if (!FilesHaveSameContent(
                    folderCache,
                    destinationCover)
                && !LooksLikeDirectCopy(
                    folderCache,
                    destinationCover))
            {
                continue;
            }

            covers.Add(
                new CoverPlanEntry(
                    folderCache,
                    destinationCover,
                    NeedsWrite: true,
                    DeleteDestination: true));
        }
    }

    private static bool LooksLikeDirectCopy(
        string sourcePath,
        string destinationPath)
    {
        FileInfo source =
            new(sourcePath);
        FileInfo destination =
            new(destinationPath);

        return source.Exists
            && destination.Exists
            && source.Length
                == destination.Length
            && source.LastWriteTimeUtc.Ticks
                == destination.LastWriteTimeUtc.Ticks;
    }

    private static bool FilesHaveSameContent(
        string leftPath,
        string rightPath)
    {
        FileInfo left =
            new(leftPath);
        FileInfo right =
            new(rightPath);

        if (!left.Exists
            || !right.Exists
            || left.Length != right.Length)
        {
            return false;
        }

        return File.ReadAllBytes(leftPath)
            .AsSpan()
            .SequenceEqual(
                File.ReadAllBytes(
                    rightPath));
    }

    private static async Task<List<PlaylistPlanEntry>>
        BuildPlaylistsAsync(
            MusicLoomSettings settings,
            IReadOnlyList<PlanEntry> entries,
            string destinationRoot,
            CancellationToken cancellationToken)
    {
        List<PlaylistPlanEntry> playlists = [];
        string sourcePlaylistDirectory =
            settings.Path.SourcePlaylistDirectory;

        if (string.IsNullOrWhiteSpace(sourcePlaylistDirectory)
            || !Directory.Exists(sourcePlaylistDirectory))
        {
            return playlists;
        }

        Dictionary<string, string> pathMapping =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (PlanEntry entry in entries)
        {
            if (entry.Status == PlanEntryStatus.Error
                || entry.DestinationPath is null)
            {
                continue;
            }

            pathMapping[entry.SourcePath] = entry.DestinationPath;
        }

        foreach (string playlistPath in Directory.EnumerateFiles(
            sourcePlaylistDirectory,
            "*.m3u",
            SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string[] sourceLines = await File.ReadAllLinesAsync(
                playlistPath,
                Encoding.UTF8,
                cancellationToken);
            IReadOnlyList<string> parsed =
                PlaylistMapper.ParseM3u(sourceLines);
            IReadOnlyList<string> mapped = PlaylistMapper.Map(
                parsed,
                pathMapping,
                destinationRoot,
                settings.Path.DestinationPlaylistBase);

            string outputPath = Path.Combine(
                destinationRoot,
                Path.GetFileNameWithoutExtension(playlistPath)
                + ".txt");
            string output = string.Join("\n", mapped);

            bool needsWrite = true;
            if (File.Exists(outputPath))
            {
                string existing = await File.ReadAllTextAsync(
                    outputPath,
                    Encoding.UTF8,
                    cancellationToken);
                needsWrite = !string.Equals(
                    existing,
                    output,
                    StringComparison.Ordinal);
            }

            playlists.Add(new PlaylistPlanEntry(
                playlistPath,
                outputPath,
                output,
                needsWrite));
        }

        return playlists;
    }

    private static bool FilesAreIdentical(
        string sourcePath,
        string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            return false;
        }

        FileInfo source = new(sourcePath);
        FileInfo destination = new(destinationPath);
        return source.Length == destination.Length
            && source.LastWriteTimeUtc.Ticks
                == destination.LastWriteTimeUtc.Ticks;
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}
