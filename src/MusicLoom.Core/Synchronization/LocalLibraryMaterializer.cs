using System.Text;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Planning;
using MusicLoom.Core.Scanning;

namespace MusicLoom.Core.Synchronization;

public enum LocalMaterializeErrorKind
{
    Track,
    Cover,
    Playlist,
}

public sealed record LocalMaterializeError(
    LocalMaterializeErrorKind Kind,
    string SourcePath,
    string DestinationPath,
    string Message);

public sealed record LocalMaterializeSummary(
    int CopiedTracks,
    int SkippedTracks,
    int CopiedCovers,
    int SkippedCovers,
    int WrittenPlaylists,
    int SkippedPlaylists,
    IReadOnlyList<LocalMaterializeError> Errors);

public sealed class LocalLibraryMaterializer
{
    public async Task<LocalMaterializeSummary> ApplyAsync(
        MusicLoomSettings settings,
        LibraryPlan plan,
        CancellationToken cancellationToken = default)
    {
        string destinationRoot =
            Path.GetFullPath(
                settings.Path.DestinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        IReadOnlyList<CoverPlanEntry> covers =
            plan.Covers;
        IReadOnlyList<PlaylistPlanEntry> playlists =
            plan.Playlists;

        // Preserve compatibility for tests/callers that manually construct a
        // track-only plan. Real scans already contain ancillary work.
        if (covers.Count == 0
            && playlists.Count == 0)
        {
            AncillaryPlan ancillary =
                await AncillaryPlanner.BuildAsync(
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
        List<LocalMaterializeError> errors = [];

        foreach (PlanEntry entry in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Status == PlanEntryStatus.Error
                || entry.DestinationPath is null
                || !entry.NeedsAudioCopy)
            {
                continue;
            }

            try
            {
                string? destinationDirectory =
                    Path.GetDirectoryName(
                        entry.DestinationPath);
                if (!string.IsNullOrEmpty(
                        destinationDirectory))
                {
                    Directory.CreateDirectory(
                        destinationDirectory);
                }

                if (FilesAreIdentical(
                        entry.SourcePath,
                        entry.DestinationPath))
                {
                    skippedTracks++;
                    continue;
                }

                AtomicCopyWithTimes(
                    entry.SourcePath,
                    entry.DestinationPath);
                copiedTracks++;
            }
            catch (Exception exception)
                when (exception
                    is not OperationCanceledException)
            {
                errors.Add(new LocalMaterializeError(
                    LocalMaterializeErrorKind.Track,
                    entry.SourcePath,
                    entry.DestinationPath,
                    exception.Message));
            }
        }

        foreach (CoverPlanEntry cover in covers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!cover.NeedsWrite)
            {
                skippedCovers++;
                continue;
            }

            try
            {
                if (FilesAreIdentical(
                        cover.SourcePath,
                        cover.DestinationPath))
                {
                    skippedCovers++;
                    continue;
                }

                string? directory =
                    Path.GetDirectoryName(
                        cover.DestinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                AtomicCopyWithTimes(
                    cover.SourcePath,
                    cover.DestinationPath);
                copiedCovers++;
            }
            catch (Exception exception)
                when (exception
                    is not OperationCanceledException)
            {
                errors.Add(new LocalMaterializeError(
                    LocalMaterializeErrorKind.Cover,
                    cover.SourcePath,
                    cover.DestinationPath,
                    exception.Message));
            }
        }

        foreach (PlaylistPlanEntry playlist in playlists)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!playlist.NeedsWrite)
            {
                skippedPlaylists++;
                continue;
            }

            try
            {
                if (await PlaylistMatchesAsync(
                        playlist,
                        cancellationToken))
                {
                    skippedPlaylists++;
                    continue;
                }

                string? directory =
                    Path.GetDirectoryName(
                        playlist.DestinationPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                await AtomicWriteTextAsync(
                    playlist.DestinationPath,
                    playlist.Content,
                    cancellationToken);
                writtenPlaylists++;
            }
            catch (Exception exception)
                when (exception
                    is not OperationCanceledException)
            {
                errors.Add(new LocalMaterializeError(
                    LocalMaterializeErrorKind.Playlist,
                    playlist.SourcePath,
                    playlist.DestinationPath,
                    exception.Message));
            }
        }

        return new LocalMaterializeSummary(
            copiedTracks,
            skippedTracks,
            copiedCovers,
            skippedCovers,
            writtenPlaylists,
            skippedPlaylists,
            errors);
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

    private static void AtomicCopyWithTimes(
        string sourcePath,
        string destinationPath)
    {
        FileInfo source = new(sourcePath);
        string temporaryPath =
            CreateSiblingTemporaryPath(
                destinationPath);

        try
        {
            File.Copy(
                sourcePath,
                temporaryPath,
                overwrite: false);
            File.SetLastWriteTimeUtc(
                temporaryPath,
                source.LastWriteTimeUtc);
            File.SetLastAccessTimeUtc(
                temporaryPath,
                source.LastAccessTimeUtc);

            File.Move(
                temporaryPath,
                destinationPath,
                overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static async Task AtomicWriteTextAsync(
        string destinationPath,
        string content,
        CancellationToken cancellationToken)
    {
        string temporaryPath =
            CreateSiblingTemporaryPath(
                destinationPath);

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            File.Move(
                temporaryPath,
                destinationPath,
                overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static string CreateSiblingTemporaryPath(
        string destinationPath)
    {
        string? directory =
            Path.GetDirectoryName(destinationPath);
        string fileName =
            Path.GetFileName(destinationPath);

        return Path.Combine(
            directory ?? string.Empty,
            $".{fileName}.musicloom-{Guid.NewGuid():N}.tmp");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Never let cleanup hide the operation that actually failed.
        }
    }
}
