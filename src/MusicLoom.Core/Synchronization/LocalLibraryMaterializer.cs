using System.Collections.Concurrent;
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

public sealed record LocalMaterializeProgress(
    int Processed,
    int Total,
    string SourcePath,
    string Stage);

public sealed class LocalLibraryMaterializer
{
    public async Task<LocalMaterializeSummary> ApplyAsync(
        MusicLoomSettings settings,
        LibraryPlan plan,
        IProgress<LocalMaterializeProgress>? progress = null,
        int? maxParallelTrackCopies = null,
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

        PlanEntry[] trackWork =
            plan.Entries
                .Where(static entry =>
                    entry.Status
                        != PlanEntryStatus.Error
                    && entry.DestinationPath
                        is not null
                    && entry.NeedsAudioCopy)
                .ToArray();

        CoverPlanEntry[] coverWork =
            covers
                .Where(static cover =>
                    cover.NeedsWrite)
                .ToArray();

        PlaylistPlanEntry[] playlistWork =
            playlists
                .Where(static playlist =>
                    playlist.NeedsWrite)
                .ToArray();

        int totalWork =
            trackWork.Length
            + coverWork.Length
            + playlistWork.Length;

        int completedWork = 0;
        int copiedTracks = 0;
        int skippedTracks = 0;
        int copiedCovers = 0;
        int skippedCovers =
            covers.Count
            - coverWork.Length;
        int writtenPlaylists = 0;
        int skippedPlaylists =
            playlists.Count
            - playlistWork.Length;

        ConcurrentBag<LocalMaterializeError>
            errors = [];

        int degree =
            maxParallelTrackCopies
            ?? Math.Clamp(
                Environment.ProcessorCount / 2,
                2,
                4);

        ParallelOptions options = new()
        {
            CancellationToken =
                cancellationToken,
            MaxDegreeOfParallelism =
                degree,
        };

        await Parallel.ForEachAsync(
            trackWork,
            options,
            (entry, token) =>
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    string destination =
                        entry.DestinationPath
                        ?? throw new InvalidOperationException(
                            "Track copy is missing a destination path.");

                    string? destinationDirectory =
                        Path.GetDirectoryName(
                            destination);

                    if (!string.IsNullOrEmpty(
                            destinationDirectory))
                    {
                        Directory.CreateDirectory(
                            destinationDirectory);
                    }

                    if (FilesAreIdentical(
                            entry.SourcePath,
                            destination))
                    {
                        Interlocked.Increment(
                            ref skippedTracks);
                    }
                    else
                    {
                        AtomicCopyWithTimes(
                            entry.SourcePath,
                            destination);

                        Interlocked.Increment(
                            ref copiedTracks);
                    }
                }
                catch (Exception exception)
                    when (exception
                        is not OperationCanceledException)
                {
                    errors.Add(
                        new LocalMaterializeError(
                            LocalMaterializeErrorKind.Track,
                            entry.SourcePath,
                            entry.DestinationPath
                                ?? string.Empty,
                            exception.Message));
                }
                finally
                {
                    int processed =
                        Interlocked.Increment(
                            ref completedWork);

                    progress?.Report(
                        new LocalMaterializeProgress(
                            processed,
                            totalWork,
                            entry.SourcePath,
                            "copy-audio"));
                }

                return ValueTask.CompletedTask;
            });

        foreach (CoverPlanEntry cover
            in coverWork)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            try
            {
                if (FilesAreIdentical(
                        cover.SourcePath,
                        cover.DestinationPath))
                {
                    skippedCovers++;
                }
                else
                {
                    string? directory =
                        Path.GetDirectoryName(
                            cover.DestinationPath);

                    if (!string.IsNullOrEmpty(
                            directory))
                    {
                        Directory.CreateDirectory(
                            directory);
                    }

                    AtomicCopyWithTimes(
                        cover.SourcePath,
                        cover.DestinationPath);

                    copiedCovers++;
                }
            }
            catch (Exception exception)
                when (exception
                    is not OperationCanceledException)
            {
                errors.Add(
                    new LocalMaterializeError(
                        LocalMaterializeErrorKind.Cover,
                        cover.SourcePath,
                        cover.DestinationPath,
                        exception.Message));
            }
            finally
            {
                int processed =
                    Interlocked.Increment(
                        ref completedWork);

                progress?.Report(
                    new LocalMaterializeProgress(
                        processed,
                        totalWork,
                        cover.SourcePath,
                        "copy-cover"));
            }
        }

        foreach (PlaylistPlanEntry playlist
            in playlistWork)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            try
            {
                if (await PlaylistMatchesAsync(
                        playlist,
                        cancellationToken))
                {
                    skippedPlaylists++;
                }
                else
                {
                    string? directory =
                        Path.GetDirectoryName(
                            playlist.DestinationPath);

                    if (!string.IsNullOrEmpty(
                            directory))
                    {
                        Directory.CreateDirectory(
                            directory);
                    }

                    await AtomicWriteTextAsync(
                        playlist.DestinationPath,
                        playlist.Content,
                        cancellationToken);

                    writtenPlaylists++;
                }
            }
            catch (Exception exception)
                when (exception
                    is not OperationCanceledException)
            {
                errors.Add(
                    new LocalMaterializeError(
                        LocalMaterializeErrorKind.Playlist,
                        playlist.SourcePath,
                        playlist.DestinationPath,
                        exception.Message));
            }
            finally
            {
                int processed =
                    Interlocked.Increment(
                        ref completedWork);

                progress?.Report(
                    new LocalMaterializeProgress(
                        processed,
                        totalWork,
                        playlist.SourcePath,
                        "write-playlist"));
            }
        }

        return new LocalMaterializeSummary(
            copiedTracks,
            skippedTracks,
            copiedCovers,
            skippedCovers,
            writtenPlaylists,
            skippedPlaylists,
            errors.ToArray());
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
