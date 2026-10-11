using System.Security.Cryptography;
using System.Text;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Metadata;

namespace MusicLoom.App;

/// <summary>
/// Resolves the one canonical lyric document during a library scan.
/// Embedded source tags > user-managed .lrc > NetEase provider cache.
/// Destination sidecars previously created by MusicLoom are output replicas,
/// so they never outrank source tags or the canonical NetEase document.
/// </summary>
internal sealed class LibraryLyricsIndexer
{
    private readonly SqliteLyricsCache _cache;
    private readonly Func<string, string?> _readEmbedded;
    private readonly Action<string, Exception>? _reportError;

    public LibraryLyricsIndexer(
        SqliteLyricsCache cache,
        Func<string, string?>? readEmbedded = null,
        Action<string, Exception>? reportError = null)
    {
        _cache = cache;
        _readEmbedded =
            readEmbedded ?? AtlTrackInspector.ReadEmbeddedLyrics;
        _reportError = reportError;
    }

    public async Task RefreshAsync(
        LibraryPlan plan,
        StateDocument state,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, LibraryLyricsRecord> current =
            await _cache.LoadLibrarySnapshotAsync(cancellationToken);
        Dictionary<string, CachedTrackLyrics> remoteRows =
            await _cache.LoadRemoteSnapshotAsync(cancellationToken);
        List<LibraryLyricsRecord> changed = [];

        foreach (PlanEntry entry in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await RefreshTrackAsync(
                    entry,
                    state,
                    current,
                    remoteRows,
                    changed,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One corrupt file or unreadable sidecar must not erase the
                // previously indexed document or prevent the rest of a scan.
                _reportError?.Invoke(entry.SourcePath, exception);
            }
        }

        await _cache.SaveLibraryBatchAsync(
            changed,
            cancellationToken);
    }

    private async Task RefreshTrackAsync(
        PlanEntry entry,
        StateDocument state,
        Dictionary<string, LibraryLyricsRecord> currentRows,
        Dictionary<string, CachedTrackLyrics> remoteRows,
        List<LibraryLyricsRecord> changed,
        CancellationToken cancellationToken)
    {
        FileInfo source = new(entry.SourcePath);
        if (!source.Exists)
        {
            return;
        }

        state.Tracks.TryGetValue(
            entry.SourcePath, out TrackState? track);

        long? songId =
            track is { NeteaseMatchSuppressed: false }
                ? track.NeteaseSongId
                : null;

        CachedTrackLyrics? remote =
            songId is long id
            && remoteRows.TryGetValue(
                entry.SourcePath,
                out CachedTrackLyrics? candidate)
            && candidate.SongId == id
                ? candidate
                : null;

        string sourceSidecarPath =
            SidecarLrcLyricsWriter.GetSidecarPath(entry.SourcePath);

        string? destinationSidecarPath =
            entry.DestinationPath is string destination
                ? SidecarLrcLyricsWriter.GetSidecarPath(destination)
                : null;

        string signature = string.Join(
            "|",
            Stamp(entry.SourcePath),
            Stamp(sourceSidecarPath),
            Stamp(destinationSidecarPath),
            songId?.ToString() ?? "none",
            remote?.FetchedAt.UtcTicks.ToString() ?? "no-remote",
            remote?.MergeVersion.ToString() ?? "no-merge-version",
            track?.LyricsFingerprint ?? "no-fingerprint");

        currentRows.TryGetValue(
            entry.SourcePath,
            out LibraryLyricsRecord? current);

        if (current?.SourceSignature == signature
            && current.SongId == songId)
        {
            return;
        }

        // A deliberate NetEase lyric choice outranks automatic local
        // discovery. Keep it until the match is changed or suppressed;
        // never silently restore source-embedded/sidecar lyrics on rescan.
        if (current is
            {
                Origin: LibraryLyricsOrigin.ManualNetease,
            }
            && current.SongId == songId
            && remote is not null)
        {
            LibraryLyricsRecord preferred = new(
                entry.SourcePath,
                LibraryLyricsOrigin.ManualNetease,
                remote.Lrc,
                songId,
                signature,
                DateTimeOffset.UtcNow);
            changed.Add(preferred);
            currentRows[entry.SourcePath] = preferred;
            return;
        }

        string? embedded =
            await Task.Run(
                () => _readEmbedded(entry.SourcePath),
                cancellationToken);

        LibraryLyricsOrigin origin = LibraryLyricsOrigin.None;
        string? lyrics = null;

        if (!string.IsNullOrWhiteSpace(embedded))
        {
            origin = LibraryLyricsOrigin.Embedded;
            lyrics = embedded;
        }

        if (lyrics is null)
        {
            string? localSidecar =
                await ReadIfPresentAsync(
                    sourceSidecarPath, cancellationToken);

            if (!string.IsNullOrWhiteSpace(localSidecar))
            {
                origin = LibraryLyricsOrigin.Sidecar;
                lyrics = localSidecar;
            }
        }

        if (lyrics is null
            && destinationSidecarPath is not null
            && !string.Equals(
                sourceSidecarPath,
                destinationSidecarPath,
                StringComparison.OrdinalIgnoreCase))
        {
            string? destinationSidecar =
                await ReadIfPresentAsync(
                    destinationSidecarPath, cancellationToken);

            if (!string.IsNullOrWhiteSpace(destinationSidecar))
            {
                bool generatedOutput =
                    IsManagedOutput(
                        destinationSidecar,
                        track?.LyricsFingerprint);

                if (!generatedOutput)
                {
                    origin = LibraryLyricsOrigin.Sidecar;
                    lyrics = destinationSidecar;
                }
                else if (remote is null
                    && songId is long oldSongId)
                {
                    // One-time migration from an old version of MusicLoom
                    // that only recorded a fingerprint and wrote an LRC.
                    LrcDocument migrated =
                        LrcDocument.Parse(destinationSidecar);
                    if (migrated.Lines.Count > 0)
                    {
                        await _cache.SaveAsync(
                            entry.SourcePath,
                            oldSongId,
                            migrated,
                            cancellationToken);
                        remote = await _cache.GetAsync(
                            entry.SourcePath,
                            oldSongId,
                            cancellationToken);
                        if (remote is not null)
                        {
                            remoteRows[entry.SourcePath] = remote;
                        }
                    }
                }
            }
        }

        if (lyrics is null && remote?.HasLyrics == true)
        {
            origin = LibraryLyricsOrigin.Netease;
            lyrics = remote.Lrc;
        }
        else if (lyrics is null && remote is not null)
        {
            // Known no-lyrics responses remain distinct from a missing
            // provider record; neither can resurrect a previous match.
            origin = LibraryLyricsOrigin.Netease;
        }

        // A one-time migration above may have added a remote row. Persist
        // its timestamp in the signature to make the warm path idempotent.
        signature = string.Join(
            "|",
            Stamp(entry.SourcePath),
            Stamp(sourceSidecarPath),
            Stamp(destinationSidecarPath),
            songId?.ToString() ?? "none",
            remote?.FetchedAt.UtcTicks.ToString() ?? "no-remote",
            remote?.MergeVersion.ToString() ?? "no-merge-version",
            track?.LyricsFingerprint ?? "no-fingerprint");

        if (current is not null
            && current.Origin == origin
            && string.Equals(
                current.Text, lyrics, StringComparison.Ordinal)
            && current.SongId == songId
            && current.SourceSignature == signature)
        {
            return;
        }

        LibraryLyricsRecord updated = new(
            entry.SourcePath,
            origin,
            lyrics,
            songId,
            signature,
            DateTimeOffset.UtcNow);
        changed.Add(updated);
        currentRows[entry.SourcePath] = updated;
    }

    private static async Task<string?> ReadIfPresentAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        string content = await File.ReadAllTextAsync(
            path, cancellationToken);
        return string.IsNullOrWhiteSpace(content)
            ? null
            : content;
    }

    private static string Stamp(string? path)
    {
        if (path is null)
        {
            return "-";
        }

        FileInfo info = new(path);
        return info.Exists
            ? $"{Path.GetFullPath(path)}:{info.Length}:{info.LastWriteTimeUtc.Ticks}"
            : $"{Path.GetFullPath(path)}:missing";
    }

    private static bool IsManagedOutput(
        string text,
        string? expectedFingerprint)
    {
        if (string.IsNullOrWhiteSpace(expectedFingerprint))
        {
            return false;
        }

        LrcDocument parsed = LrcDocument.Parse(text);
        if (parsed.Lines.Count == 0)
        {
            return false;
        }

        string hash = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(parsed.ToString())))
            .ToLowerInvariant();

        return string.Equals(
            hash,
            expectedFingerprint,
            StringComparison.OrdinalIgnoreCase);
    }
}
