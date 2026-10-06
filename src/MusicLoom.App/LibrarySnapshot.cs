using System.Text.Json;
using MusicLoom.Core.Scanning;

namespace MusicLoom.App;

internal sealed record LibrarySnapshotDocument(
    string SourceDirectory,
    string DestinationDirectory,
    DateTimeOffset CapturedAt,
    IReadOnlyList<LibrarySnapshotEntry> Entries);

internal sealed record LibrarySnapshotEntry(
    string SourcePath,
    string? DestinationPath,
    PlanEntryStatus Status,
    TrackMetadata? Metadata,
    string? Detail,
    bool NeedsAudioCopy);

internal static class LibrarySnapshotStore
{
    public static async Task SaveAsync(
        string path,
        string sourceDirectory,
        string destinationDirectory,
        LibraryPlan plan,
        CancellationToken cancellationToken = default)
    {
        LibrarySnapshotDocument document = new(
            Path.GetFullPath(sourceDirectory),
            Path.GetFullPath(destinationDirectory),
            DateTimeOffset.UtcNow,
            plan.Entries
                .Select(static entry =>
                    new LibrarySnapshotEntry(
                        entry.SourcePath,
                        entry.DestinationPath,
                        entry.Status,
                        entry.Metadata,
                        entry.Detail,
                        entry.NeedsAudioCopy))
                .ToArray());

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary =
            path + ".tmp." + Guid.NewGuid().ToString("N");

        try
        {
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(
                temporary,
                path,
                overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
            }
        }
    }

    public static async Task<LibrarySnapshotDocument?> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using FileStream stream =
                File.OpenRead(path);

            return await JsonSerializer.DeserializeAsync<
                LibrarySnapshotDocument>(
                    stream,
                    cancellationToken: cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public static bool Matches(
        LibrarySnapshotDocument snapshot,
        string sourceDirectory,
        string destinationDirectory)
    {
        return PathEquals(
                snapshot.SourceDirectory,
                sourceDirectory)
            && PathEquals(
                snapshot.DestinationDirectory,
                destinationDirectory);
    }

    public static LibraryPlan ToPlan(
        LibrarySnapshotDocument snapshot) =>
        new(
            snapshot.Entries
                .Select(static entry =>
                    new PlanEntry(
                        entry.SourcePath,
                        entry.DestinationPath,
                        entry.Status,
                        entry.Metadata,
                        entry.Detail,
                        entry.NeedsAudioCopy))
                .ToArray());

    private static bool PathEquals(
        string left,
        string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
