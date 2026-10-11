using Microsoft.Data.Sqlite;
using MusicLoom.App;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Enrichment;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Planning;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Core.Synchronization;

internal static class ManagedSidecarRegression
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "musicloom-managed-lrc-" + Guid.NewGuid().ToString("N"));
        string sources = Path.Combine(root, "sources");
        string targets = Path.Combine(root, "targets");
        Directory.CreateDirectory(sources);
        Directory.CreateDirectory(targets);
        try
        {
            string sourcePath = Path.Combine(sources, "song.mp3");
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4, 5]);
            TrackMetadata metadata = new("Singer", "Singer", "Album", false, "Song");
            string destination = LibraryLayout.GetDestinationPath(
                new MusicFile(sourcePath, sources, "Singer", "Singer", "Album", false),
                targets);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourcePath, destination);

            string sidecar = SidecarLrcLyricsWriter.GetSidecarPath(destination);
            LrcDocument prior = LrcDocument.Parse("[00:01.000]Old MusicLoom output");
            LrcDocument latest = LrcDocument.Parse("[00:01.000]Updated provider lyric");
            await File.WriteAllTextAsync(sidecar, prior.ToString());

            MusicLoomSettings settings = new()
            {
                Path = new PathSettings
                {
                    SourceDirectory = sources,
                    DestinationDirectory = targets,
                },
                Organizer = new OrganizerSettings
                {
                    Lyrics = new LyricsSettings
                    {
                        Enabled = true,
                        StorageMode = LyricsStorageModes.Sidecar,
                        OverrideExisting = true,
                    },
                },
            };
            FileInfo source = new(sourcePath);
            StateDocument state = new();
            state.Tracks[sourcePath] = new TrackState
            {
                SourcePath = sourcePath,
                SourceSize = source.Length,
                SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks,
                QuickHash = QuickFileHash.Compute(sourcePath),
                DestinationPath = destination,
                MetadataCaptured = true,
                MetadataSchemaVersion = TrackMetadata.CurrentSchemaVersion,
                TitleCaptured = true,
                Title = metadata.Title,
                Artist = metadata.Artist,
                AlbumArtist = metadata.AlbumArtist,
                Album = metadata.Album,
                Recognition = RecognitionState.Matched,
                NeteaseSongId = 42,
                NeteaseMatchConfirmed = true,
                Lyrics = LyricsState.Written,
                LyricsFingerprint = null, // Older MusicLoom lost this.
            };
            PlanEntry entry = new(
                sourcePath, destination, PlanEntryStatus.Modified,
                metadata, NeedsAudioCopy: false);

            SqliteLyricsCache cache = new(Path.Combine(root, "musicloom.db"));
            await cache.SaveAsync(sourcePath, 42, prior);
            await cache.SaveExplicitAsync(sourcePath, 42, latest);

            // Only an exact old cached lyric counts as legacy-generated.
            string digest = Hash(prior);
            Assert(await cache.IsKnownOutputAsync(sourcePath, destination, digest));
            Assert(!await cache.IsKnownOutputAsync(
                sourcePath, destination,
                Hash(LrcDocument.Parse("[00:01.000]Manually edited"))));

            StateStore store = new(Path.Combine(root, "state.json"));
            SyncExecutor executor = new(
                new LocalLibraryMaterializer(),
                new NoRecognizer(),
                new NoLyrics(),
                new SidecarLrcLyricsWriter(),
                lyricsCache: cache,
                libraryLyricsCache: cache,
                managedLyricsRegistry: cache);

            SyncExecutionSummary result = await executor.ExecuteAsync(
                settings, new LibraryPlan([entry]), state, store,
                lyricsOnly: true);
            Assert(result.Errors.Count == 0);
            Assert((await File.ReadAllTextAsync(sidecar)) == latest.ToString());
            TrackState completed = (await store.LoadAsync()).Tracks[sourcePath];
            Assert(!string.IsNullOrWhiteSpace(completed.LyricsFingerprint));

            // Written provenance survives a reader reopening the SQLite file.
            Assert(await new SqliteLyricsCache(
                Path.Combine(root, "musicloom.db")).IsKnownOutputAsync(
                    sourcePath, destination, Hash(latest)));

            // Source rescans can leave a stale track-level hash. The
            // independent verified managed-output record is sufficient
            // to recover that output without a false conflict.
            state.Tracks[sourcePath] = completed with
            {
                LyricsFingerprint = "stale-legacy-hash",
            };
            await cache.SaveExplicitAsync(
                sourcePath, 42,
                LrcDocument.Parse("[00:01.000]Latest safely verified"));
            SyncExecutionSummary recovered = await executor.ExecuteAsync(
                settings, new LibraryPlan([entry]), state, store,
                lyricsOnly: true);
            Assert(recovered.Errors.Count == 0);
            Assert((await File.ReadAllTextAsync(sidecar))
                == "[00:01.000]Latest safely verified");
            completed = (await store.LoadAsync()).Tracks[sourcePath];

            // An external modification must still block replacement.
            await File.WriteAllTextAsync(
                sidecar, "[00:01.000]User-edited content");
            state.Tracks[sourcePath] = completed with
            {
                LyricsFingerprint = null,
                Lyrics = LyricsState.Written,
            };
            await cache.SaveExplicitAsync(
                sourcePath, 42, LrcDocument.Parse("[00:01.000]Another update"));
            SyncExecutionSummary blocked = await executor.ExecuteAsync(
                settings, new LibraryPlan([entry]), state, store,
                lyricsOnly: true);
            Assert(blocked.Errors.Count == 1);
            Assert(blocked.Errors[0].SidecarConflict
                == SidecarLyricsConflictKind.ReplacementBlocked);
            Assert((await File.ReadAllTextAsync(sidecar))
                == "[00:01.000]User-edited content");

            string zh = UserFacingErrors.DescribeSyncError(
                blocked.Errors[0], chinese: true);
            string en = UserFacingErrors.DescribeSyncError(
                blocked.Errors[0], chinese: false);
            Assert(zh.Contains("目标 LRC", StringComparison.Ordinal));
            Assert(!zh.Contains("Existing sidecar lyrics", StringComparison.Ordinal));
            Assert(en.Contains("destination LRC", StringComparison.Ordinal));
            Assert(UserFacingErrors.TranslateCoreMessage(
                "Existing sidecar lyrics cannot be replaced automatically",
                true).Contains("目标 LRC", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Hash(LrcDocument lyrics) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(lyrics.ToString())))
            .ToLowerInvariant();

    private static void Assert(bool valid)
    {
        if (!valid) throw new InvalidOperationException(
            "Managed LRC origin/i18n regression failure.");
    }

    private sealed class NoRecognizer : ITrackRecognizer
    {
        public Task<TrackRecognitionResult> RecognizeAsync(
            string audioFilePath, CancellationToken token = default) =>
            throw new InvalidOperationException("Recognition not expected.");
    }

    private sealed class NoLyrics : ITrackLyricsProvider
    {
        public Task<LyricsLookupResult> GetLyricsAsync(
            long songId, CancellationToken token = default) =>
            throw new InvalidOperationException("Cached lyrics should be used.");
    }
}
