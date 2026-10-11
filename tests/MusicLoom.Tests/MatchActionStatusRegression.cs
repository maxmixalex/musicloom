using MusicLoom.App;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Core.Synchronization;

internal static class MatchActionStatusRegression
{
    public static void Run()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "musicloom-match-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.mp3");
            string destination = Path.Combine(root, "destination.mp3");
            File.WriteAllBytes(source, [1, 2, 3, 4]);
            File.Copy(source, destination);
            FileInfo sourceInfo = new(source);
            FileInfo targetInfo = new(destination);

            MusicLoomSettings settings = new();
            PlanEntry entry = new(
                source, destination, PlanEntryStatus.Unchanged,
                new TrackMetadata(
                    Artist: "Local Artist",
                    AlbumArtist: "Local Artist",
                    Album: "Album",
                    HasEmbeddedArt: false,
                    Title: "Local Title"));
            TrackState original = new()
            {
                SourcePath = source,
                SourceSize = sourceInfo.Length,
                SourceLastWriteUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks,
                DestinationPath = destination,
                DestinationSize = targetInfo.Length,
                DestinationLastWriteUtcTicks =
                    targetInfo.LastWriteTimeUtc.Ticks,
                RecipeVersion = SyncRecipe.For(settings),
                Recognition = RecognitionState.Matched,
                NeteaseSongId = 42,
                NeteaseTrackName = "Different Title",
                NeteaseTrackArtist = "Different Artist",
                Lyrics = LyricsState.Existing,
            };
            StateDocument state = new();
            state.Tracks[source] = original;

            Equal("warning", LibraryMatchStatusResolver.GetKind(entry, state));
            Assert(SyncExecutor.DetectMatchWarning(entry, original) is not null);

            // Pressing Confirm must immediately remove the old warning.
            // It schedules materialization without an unnecessary audio copy.
            state.Tracks[source] = original with
            {
                NeteaseMatchConfirmed = true,
                Lyrics = LyricsState.Unknown,
                RecipeVersion = string.Empty,
            };
            LibraryMatchRowState confirming = Get(entry, state, settings);
            Equal("pending", confirming.Kind);
            Equal(PlanEntryStatus.Modified, confirming.Entry.Status);
            Assert(!confirming.Entry.NeedsAudioCopy);
            Assert(SyncExecutor.DetectMatchWarning(entry, state.Tracks[source])
                is null);

            // When the lyric-only executor finishes, the same row turns
            // Synced without a library rescan or ListView replacement.
            state.Tracks[source] = state.Tracks[source] with
            {
                Lyrics = LyricsState.Written,
                RecipeVersion = SyncRecipe.For(settings),
            };
            LibraryMatchRowState afterSync =
                Get(confirming.Entry, state, settings);
            Equal("synced", afterSync.Kind);
            Equal(PlanEntryStatus.Unchanged, afterSync.Entry.Status);

            // Clearing an accepted match removes the old warning/metadata,
            // and marks the now-stale output as pending immediately.
            state.Tracks[source] = state.Tracks[source] with
            {
                NeteaseSongId = null,
                NeteaseMatchSuppressed = true,
                NeteaseMatchConfirmed = false,
                Recognition = RecognitionState.NoMatch,
                Lyrics = LyricsState.Unknown,
                RecipeVersion = string.Empty,
            };
            LibraryMatchRowState clearing =
                Get(afterSync.Entry, state, settings);
            Equal("pending", clearing.Kind);
            Equal(PlanEntryStatus.Modified, clearing.Entry.Status);
            Assert(SyncExecutor.DetectMatchWarning(entry, state.Tracks[source])
                is null);
            state.Tracks[source] = state.Tracks[source] with
            {
                RecipeVersion = SyncRecipe.For(settings),
                Lyrics = LyricsState.NoLyrics,
            };
            Equal("synced", Get(clearing.Entry, state, settings).Kind);

            // A batch operation must reconcile each changed source while
            // leaving untouched ListView entries outside this subset alone.
            string otherSource = Path.Combine(root, "other.mp3");
            string otherDestination = Path.Combine(root, "other-dst.mp3");
            File.Copy(source, otherSource);
            File.Copy(destination, otherDestination);
            FileInfo otherInfo = new(otherSource);
            FileInfo otherDstInfo = new(otherDestination);
            PlanEntry other = entry with
            {
                SourcePath = otherSource,
                DestinationPath = otherDestination,
            };
            state.Tracks[otherSource] = original with
            {
                SourcePath = otherSource,
                SourceSize = otherInfo.Length,
                SourceLastWriteUtcTicks = otherInfo.LastWriteTimeUtc.Ticks,
                DestinationPath = otherDestination,
                DestinationSize = otherDstInfo.Length,
                DestinationLastWriteUtcTicks = otherDstInfo.LastWriteTimeUtc.Ticks,
            };

            var batch = LibraryMatchStatusResolver.Reconcile(
                [clearing.Entry, other], state, settings);
            Equal("synced", batch[source].Kind);
            Equal("warning", batch[otherSource].Kind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static LibraryMatchRowState Get(
        PlanEntry entry,
        StateDocument state,
        MusicLoomSettings settings) =>
        LibraryMatchStatusResolver.Reconcile(
            [entry], state, settings)[entry.SourcePath];

    private static void Assert(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException(
                "Match status reconciliation assertion failed.");
        }
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Expected {expected}, got {actual}.");
        }
    }
}
