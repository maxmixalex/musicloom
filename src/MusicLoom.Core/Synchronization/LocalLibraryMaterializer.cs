using System.Text;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Planning;
using MusicLoom.Core.Scanning;

namespace MusicLoom.Core.Synchronization;

public sealed record LocalMaterializeSummary(
    int CopiedTracks,
    int SkippedTracks,
    int CopiedCovers,
    int SkippedCovers,
    int WrittenPlaylists,
    int SkippedPlaylists);

public sealed class LocalLibraryMaterializer
{
    public async Task<LocalMaterializeSummary> ApplyAsync(
        MusicLoomSettings settings,
        LibraryPlan plan,
        CancellationToken cancellationToken = default)
    {
        string destinationRoot = Path.GetFullPath(settings.Path.DestinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        int copiedTracks = 0;
        int skippedTracks = 0;
        int copiedCovers = 0;
        int skippedCovers = 0;
        int writtenPlaylists = 0;
        int skippedPlaylists = 0;

        Dictionary<string, string> pathMapping = new(StringComparer.OrdinalIgnoreCase);

        foreach (PlanEntry entry in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Status == PlanEntryStatus.Error || entry.DestinationPath is null)
            {
                continue;
            }

            pathMapping[entry.SourcePath] = entry.DestinationPath;

            if (!entry.NeedsAudioCopy)
            {
                continue;
            }

            string? destinationDirectory = Path.GetDirectoryName(entry.DestinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (FilesAreIdentical(entry.SourcePath, entry.DestinationPath))
            {
                skippedTracks++;
                continue;
            }

            CopyWithTimes(entry.SourcePath, entry.DestinationPath);
            copiedTracks++;
        }

        foreach (IGrouping<string, PlanEntry> group in plan.Entries
            .Where(static entry =>
                entry.Status != PlanEntryStatus.Error
                && entry.DestinationPath is not null
                && entry.Metadata is not null)
            .GroupBy(
                static entry => Path.GetDirectoryName(entry.DestinationPath!) ?? string.Empty,
                StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string albumDirectory = group.Key;
            if (PathsEqual(albumDirectory, destinationRoot))
            {
                continue;
            }

            PlanEntry first = group.First();
            TrackMetadata metadata = first.Metadata!;
            string sourceDirectory = Path.GetDirectoryName(first.SourcePath) ?? string.Empty;
            bool onlyExplicit = group.Any(static entry => entry.Metadata?.HasEmbeddedArt == true);

            string? cover = AlbumArtLocator.Find(sourceDirectory, metadata, onlyExplicit);
            if (cover is null)
            {
                continue;
            }

            Directory.CreateDirectory(albumDirectory);
            string destinationCover = Path.Combine(
                albumDirectory,
                "Cover" + Path.GetExtension(cover));

            if (FilesAreIdentical(cover, destinationCover))
            {
                skippedCovers++;
            }
            else
            {
                CopyWithTimes(cover, destinationCover);
                copiedCovers++;
            }
        }

        string sourcePlaylistDirectory = settings.Path.SourcePlaylistDirectory;
        if (!string.IsNullOrWhiteSpace(sourcePlaylistDirectory)
            && Directory.Exists(sourcePlaylistDirectory))
        {
            foreach (string playlistPath in Directory.EnumerateFiles(
                sourcePlaylistDirectory,
                "*.m3u",
                SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string[] sourcePaths = await File.ReadAllLinesAsync(
                    playlistPath,
                    Encoding.UTF8,
                    cancellationToken);
                IReadOnlyList<string> parsed = PlaylistMapper.ParseM3u(sourcePaths);
                IReadOnlyList<string> mapped = PlaylistMapper.Map(
                    parsed,
                    pathMapping,
                    destinationRoot,
                    settings.Path.DestinationPlaylistBase);

                string outputPath = Path.Combine(
                    destinationRoot,
                    Path.GetFileNameWithoutExtension(playlistPath) + ".txt");
                string output = string.Join("\n", mapped);

                if (File.Exists(outputPath)
                    && string.Equals(
                        await File.ReadAllTextAsync(outputPath, Encoding.UTF8, cancellationToken),
                        output,
                        StringComparison.Ordinal))
                {
                    skippedPlaylists++;
                    continue;
                }

                await File.WriteAllTextAsync(
                    outputPath,
                    output,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken);
                writtenPlaylists++;
            }
        }

        return new LocalMaterializeSummary(
            copiedTracks,
            skippedTracks,
            copiedCovers,
            skippedCovers,
            writtenPlaylists,
            skippedPlaylists);
    }

    private static bool FilesAreIdentical(string sourcePath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
        {
            return false;
        }

        FileInfo source = new(sourcePath);
        FileInfo destination = new(destinationPath);
        return source.Length == destination.Length
            && source.LastWriteTimeUtc.Ticks == destination.LastWriteTimeUtc.Ticks;
    }

    private static void CopyWithTimes(string sourcePath, string destinationPath)
    {
        FileInfo source = new(sourcePath);
        File.Copy(sourcePath, destinationPath, overwrite: true);
        File.SetLastWriteTimeUtc(destinationPath, source.LastWriteTimeUtc);
        File.SetLastAccessTimeUtc(destinationPath, source.LastAccessTimeUtc);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}
