using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Metadata;

namespace MusicLoom.App;

/// <summary>
/// Resolves the one canonical lyric document during a library scan.
/// Embedded source tags > source-sidecar .lrc > NetEase provider cache,
/// except for explicit user-selected NetEase lyrics.
/// The destination directory is exclusively generated output: its LRCs
/// must never override a canonical database document during scanning.
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

        string signature = string.Join(
            "|",
            Stamp(entry.SourcePath),
            Stamp(sourceSidecarPath),
            songId?.ToString() ?? "none",
            remote?.FetchedAt.UtcTicks.ToString() ?? "no-remote",
            remote?.MergeVersion.ToString() ?? "no-merge-version");

        currentRows.TryGetValue(
            entry.SourcePath,
            out LibraryLyricsRecord? current);

        // Explicitly edited or cleared lyrics override every subsequent
        // rescan; neither a source tag nor a provider can undo this decision.
        if (current?.Origin is LibraryLyricsOrigin.ManualEdited
            or LibraryLyricsOrigin.ManualEmpty)
        {
            return;
        }

        // Recover the user's prior manual confirmation when upgrading
        // from builds that had no explicit lyric-provenance mode. Ordinary
        // automatic matches still follow local-first precedence.
        bool confirmedProvider = track?.NeteaseMatchConfirmed == true
            && remote is { HasLyrics: true };
        bool explicitProvider =
            current?.Origin == LibraryLyricsOrigin.ManualNetease
            && current.SongId == songId;
        bool preferProvider = explicitProvider || confirmedProvider;

        if (current?.SourceSignature == signature
            && current.SongId == songId
            && (!preferProvider
                || current.Origin == LibraryLyricsOrigin.ManualNetease))
        {
            return;
        }

        // User-selected lyrics survive source-tag/sidecar rescans and
        // remain distinct from automatic recognition.
        if (preferProvider && remote is not null)
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

}
