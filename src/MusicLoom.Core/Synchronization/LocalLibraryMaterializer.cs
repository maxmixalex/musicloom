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
        string destinationRoot =
            Path.GetFullPath(settings.Path.DestinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        IReadOnlyList<CoverPlanEntry> covers = plan.Covers;
        IReadOnlyList<PlaylistPlanEntry> playlists = plan.Playlists;

        // Preserve compatibility for tests/callers that manually construct a
        // track-only plan. Real scans already contain ancillary work.
        if (covers.Count == 0 && playlists.Count == 0)
        {
            AncillaryPlan ancillary = await AncillaryPlanner.BuildAsync(
                settings,
                plan.Entries,
                cancellationToken);
            covers = ancillary.Covers;
            playlists = ancillary.Playlists;
        }

        int copiedTracks = 0;
        int skippedTracks = 0;
        int copiedCovers = 0;
        int skippedCovers = 0;
        int writtenPlaylists = 0;
        int skippedPlaylists = 0;

        foreach (PlanEntry entry in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Status == PlanEntryStatus.Error
                || entry.DestinationPath is null
                || !entry.NeedsAudioCopy)
            {
                continue;
            }

            string? destinationDirectory =
                Path.GetDirectoryName(entry.DestinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (FilesAreIdentical(
                    entry.SourcePath,
                    entry.DestinationPath))
            {
                skippedTracks++;
                continue;
            }

            CopyWithTimes(
                entry.SourcePath,
                entry.DestinationPath);
            copiedTracks++;
        }

        foreach (CoverPlanEntry cover in covers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!cover.NeedsWrite
                || FilesAreIdentical(
                    cover.SourcePath,
                    cover.DestinationPath))
            {
                skippedCovers++;
                continue;
            }

            string? directory =
                Path.GetDirectoryName(cover.DestinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            CopyWithTimes(
                cover.SourcePath,
                cover.DestinationPath);
            copiedCovers++;
        }

        foreach (PlaylistPlanEntry playlist in playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!playlist.NeedsWrite
                || await PlaylistMatchesAsync(
                    playlist,
                    cancellationToken))
            {
                skippedPlaylists++;
                continue;
            }

            string? directory =
                Path.GetDirectoryName(playlist.DestinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(
                playlist.DestinationPath,
                playlist.Content,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            writtenPlaylists++;
        }

        return new LocalMaterializeSummary(
            copiedTracks,
            skippedTracks,
            copiedCovers,
            skippedCovers,
            writtenPlaylists,
            skippedPlaylists);
    }

    private static async Task<bool> PlaylistMatchesAsync(
        PlaylistPlanEntry playlist,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(playlist.DestinationPath))
        {
            return false;
        }

        string existing = await File.ReadAllTextAsync(
            playlist.DestinationPath,
            Encoding.UTF8,
            cancellationToken);

        return string.Equals(
            existing,
            playlist.Content,
            StringComparison.Ordinal);
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

    private static void CopyWithTimes(
        string sourcePath,
        string destinationPath)
    {
        FileInfo source = new(sourcePath);
        File.Copy(sourcePath, destinationPath, overwrite: true);
        File.SetLastWriteTimeUtc(
            destinationPath,
            source.LastWriteTimeUtc);
        File.SetLastAccessTimeUtc(
            destinationPath,
            source.LastAccessTimeUtc);
    }
}
