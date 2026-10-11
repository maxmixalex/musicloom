using System.Security.Cryptography;
using System.Text;
using MusicLoom.Core.Configuration;

namespace MusicLoom.Core.Lyrics;

public sealed record ManagedSidecarAdoptionResult(
    string SidecarPath,
    string BackupPath,
    string AdoptedFingerprint);

/// <summary>
/// Explicit, user-approved migration for destination LRC files predating
/// MusicLoom's managed-output history. This service MUST NOT be invoked
/// implicitly by a scan or an ordinary synchronization.
/// </summary>
public static class ManagedSidecarAdoption
{
    private const long MaximumBackupBytes = 32L * 1024 * 1024;

    public static async Task<ManagedSidecarAdoptionResult>
        BackupAndAdoptAsync(
            MusicLoomSettings settings,
            string sourcePath,
            string destinationAudioPath,
            IManagedLyricsOutputRegistry registry,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(registry);
        LibraryPathSafety.Validate(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationAudioPath);

        string source = Path.GetFullPath(sourcePath);
        string destination = Path.GetFullPath(destinationAudioPath);
        if (!IsUnder(source, settings.Path.SourceDirectory)
            || !IsUnder(destination, settings.Path.DestinationDirectory))
        {
            throw new InvalidDataException(
                "The LRC adoption source or target is outside the configured music libraries.");
        }

        string lrcPath = SidecarLrcLyricsWriter.GetSidecarPath(destination);
        FileInfo original = new(lrcPath);
        if (!original.Exists)
        {
            throw new FileNotFoundException(
                "There is no destination LRC to back up and adopt.", lrcPath);
        }

        if ((original.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                "A symbolic-link LRC cannot be adopted automatically.");
        }

        if (original.Length > MaximumBackupBytes)
        {
            throw new IOException(
                "This LRC is too large for the safe backup-and-adopt operation.");
        }

        // Snapshot the exact bytes, preserving BOM/encoding, comments,
        // translation and any user modifications, not just parsed LRC lines.
        byte[] originalBytes = await File.ReadAllBytesAsync(
            lrcPath, cancellationToken);
        if (originalBytes.LongLength > MaximumBackupBytes)
        {
            throw new IOException("The LRC grew beyond the safe backup limit.");
        }

        string backupPath =
            lrcPath
            + ".musicloom-backup-"
            + DateTimeOffset.UtcNow.ToString(
                "yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N")[..8]
            + ".bak";

        // CreateNew prevents overwriting an earlier backup. A failed backup
        // never licenses a subsequent overwrite of the original.
        await using (FileStream backup = new(
            backupPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, bufferSize: 16 * 1024,
            options: FileOptions.Asynchronous))
        {
            await backup.WriteAsync(originalBytes, cancellationToken);
            await backup.FlushAsync(cancellationToken);
        }

        // No database ownership is claimed before a byte-for-byte backup
        // was persisted and the source was checked again. Keep the backup
        // on failure so the user never loses a recovery copy.
        byte[] copied = await File.ReadAllBytesAsync(
            backupPath, cancellationToken);
        byte[] current = await File.ReadAllBytesAsync(
            lrcPath, cancellationToken);
        if (!copied.AsSpan().SequenceEqual(originalBytes)
            || !current.AsSpan().SequenceEqual(originalBytes))
        {
            throw new IOException(
                "The LRC changed during adoption; the backup was preserved but the file was not adopted.");
        }

        string normalized = LrcDocument.Parse(
            await File.ReadAllTextAsync(lrcPath, cancellationToken)).ToString();
        string fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();

        // The registry marks this exact old version safe *only because*
        // the user explicitly approved its preservation and adoption.
        // SyncExecutor still verifies the on-disk hash before replacing it.
        await registry.RecordWrittenAsync(
            source, destination, fingerprint, cancellationToken);

        return new ManagedSidecarAdoptionResult(
            lrcPath, backupPath, fingerprint);
    }

    private static bool IsUnder(string path, string parent)
    {
        string root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(parent));
        string prefix = root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}
