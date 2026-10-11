using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using MusicLoom.App;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;

internal static class LibraryLyricsIndexerRegression
{
    public static void Run() =>
        RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "musicloom-lyrics-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            string sourcePath = Path.Combine(root, "source", "song.flac");
            string destinationPath =
                Path.Combine(root, "destination", "song.flac");
            Directory.CreateDirectory(
                Path.GetDirectoryName(sourcePath)!);
            Directory.CreateDirectory(
                Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3]);
            await File.WriteAllBytesAsync(destinationPath, [1, 2, 3]);

            string database = Path.Combine(root, "musicloom.db");
            SqliteLyricsCache cache = new(database);
            await cache.SaveAsync(
                sourcePath, 42,
                LrcDocument.Parse("[00:01.000]NetEase candidate"));

            // A provider write must atomically update the actual Lyrics
            // tab source before another full scanner pass occurs.
            LibraryLyricsRecord first =
                await GetCanonicalAsync(cache, sourcePath);
            Equal(LibraryLyricsOrigin.Netease, first.Origin);
            Equal("[00:01.000]NetEase candidate", first.Text);

            await cache.SaveAsync(
                sourcePath, 88,
                LrcDocument.Parse("[00:02.000]Updated match lyric"));
            LibraryLyricsRecord updatedMatch =
                await new SqliteLyricsCache(database)
                    .GetLibraryAsync(sourcePath)
                ?? throw new InvalidOperationException("Missing updated lyrics.");
            Equal(88L, updatedMatch.SongId ?? -1);
            Equal("[00:02.000]Updated match lyric", updatedMatch.Text);

            // Continue with the original match for the remaining cases.
            await cache.SaveAsync(
                sourcePath, 42,
                LrcDocument.Parse("[00:01.000]NetEase candidate"));

            StateDocument state = new();
            state.Tracks[sourcePath] = new TrackState
            {
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                NeteaseSongId = 42,
                Recognition = RecognitionState.Matched,
            };
            LibraryPlan plan = new([
                new PlanEntry(
                    sourcePath,
                    destinationPath,
                    PlanEntryStatus.Unchanged,
                    new TrackMetadata(
                        Artist: "Singer",
                        AlbumArtist: "Singer",
                        Album: "Album",
                        HasEmbeddedArt: false)),
            ]);

            string? embedded = "[00:01.000]Embedded is highest priority";
            int embeddedReads = 0;
            LibraryLyricsIndexer indexer = new(
                cache,
                readEmbedded: _ =>
                {
                    embeddedReads++;
                    return embedded;
                });

            await indexer.RefreshAsync(plan, state);
            LibraryLyricsRecord indexed =
                await GetCanonicalAsync(cache, sourcePath);
            Equal(LibraryLyricsOrigin.Embedded, indexed.Origin);
            Equal(embedded, indexed.Text);

            // Online refresh must never overwrite higher-priority source
            // tags, even when the same song receives fresh LRC content.
            await cache.SaveAsync(
                sourcePath, 42,
                LrcDocument.Parse("[00:03.000]Remote replacement"));
            indexed = await GetCanonicalAsync(cache, sourcePath);
            Equal(LibraryLyricsOrigin.Embedded, indexed.Origin);
            Equal(embedded, indexed.Text);
            await cache.SaveAsync(
                sourcePath, 42,
                LrcDocument.Parse("[00:01.000]NetEase candidate"));

            // The next warm scan sees unchanged signatures and does not
            // re-open the same source audio.
            await indexer.RefreshAsync(plan, state);
            Equal(1, embeddedReads);

            embedded = null;
            await File.AppendAllTextAsync(sourcePath, "newtag");
            File.SetLastWriteTimeUtc(
                sourcePath, DateTime.UtcNow.AddSeconds(2));

            string localLrc =
                SidecarLrcLyricsWriter.GetSidecarPath(sourcePath);
            await File.WriteAllTextAsync(
                localLrc,
                "[00:01.000]Local sidecar outranks NetEase");
            await indexer.RefreshAsync(plan, state);
            indexed = await GetCanonicalAsync(cache, sourcePath);
            Equal(LibraryLyricsOrigin.Sidecar, indexed.Origin);
            Equal(
                "[00:01.000]Local sidecar outranks NetEase",
                indexed.Text);

            File.Delete(localLrc);
            await indexer.RefreshAsync(plan, state);
            indexed = await GetCanonicalAsync(cache, sourcePath);
            Equal(LibraryLyricsOrigin.Netease, indexed.Origin);
            Equal(
                "[00:01.000]NetEase candidate",
                indexed.Text);

            // Clearing a NetEase match cannot resurrect the previously
            // linked remote lyric; a local source remains independent.
            state.Tracks[sourcePath] =
                state.Tracks[sourcePath] with
                {
                    NeteaseSongId = null,
                    NeteaseMatchSuppressed = true,
                };
            await indexer.RefreshAsync(plan, state);
            indexed = await GetCanonicalAsync(cache, sourcePath);
            Equal(LibraryLyricsOrigin.None, indexed.Origin);
            True(indexed.Text is null);

            // A new provider match cannot reuse an old cached song.
            state.Tracks[sourcePath] =
                state.Tracks[sourcePath] with
                {
                    NeteaseSongId = 88,
                    NeteaseMatchSuppressed = false,
                };
            await indexer.RefreshAsync(plan, state);
            indexed = await GetCanonicalAsync(cache, sourcePath);
            Equal(LibraryLyricsOrigin.None, indexed.Origin);
            True(indexed.Text is null);

            // A manually edited destination LRC is imported as a local
            // source, while a verified generated replica is not.
            string destinationLrc =
                SidecarLrcLyricsWriter.GetSidecarPath(destinationPath);
            string generated = "[00:01.000]Old generated lyric";
            await File.WriteAllTextAsync(destinationLrc, generated);
            state.Tracks[sourcePath] = state.Tracks[sourcePath] with
            {
                NeteaseSongId = 42,
                LyricsFingerprint = SHA256Hex(
                    LrcDocument.Parse(generated).ToString()),
            };
            await indexer.RefreshAsync(plan, state);
            indexed = await GetCanonicalAsync(cache, sourcePath);
            Equal(LibraryLyricsOrigin.Netease, indexed.Origin);

            await File.WriteAllTextAsync(
                destinationLrc, "[00:01.000]User changed lyric");
            await indexer.RefreshAsync(plan, state);
            indexed = await GetCanonicalAsync(cache, sourcePath);
            Equal(LibraryLyricsOrigin.Sidecar, indexed.Origin);
            Equal(
                "[00:01.000]User changed lyric",
                indexed.Text);

            // A checked no-lyrics response must be canonical and persist
            // through a new cache object/reopen.
            File.Delete(destinationLrc);
            await cache.SaveAsync(sourcePath, 42, null);
            await indexer.RefreshAsync(plan, state);
            indexed = await new SqliteLyricsCache(database)
                .GetLibraryAsync(sourcePath)
                ?? throw new InvalidOperationException(
                    "Canonical row was not persisted.");
            Equal(LibraryLyricsOrigin.Netease, indexed.Origin);
            True(!indexed.HasLyrics);
        }
        finally
        {
            // A disposed SqliteConnection can remain pooled on Windows;
            // release the underlying file handles before removing the DB.
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<LibraryLyricsRecord> GetCanonicalAsync(
        SqliteLyricsCache cache,
        string sourcePath)
    {
        return await cache.GetLibraryAsync(sourcePath)
            ?? throw new InvalidOperationException(
                "Expected canonical library lyrics row.");
    }

    private static string SHA256Hex(string text) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(text)))
        .ToLowerInvariant();

    private static void True(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "Condition was not true.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Expected {expected}, got {actual}");
        }
    }
}
