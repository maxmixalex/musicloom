using System.Text;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Planning;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Core.Synchronization;

internal static class AncillaryPlanningRegression
{
    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "musicloom-ancillary-" + Guid.NewGuid().ToString("N"));
        string sourceRoot = Path.Combine(root, "source");
        string albumSource = Path.Combine(sourceRoot, "incoming");
        string destinationRoot = Path.Combine(root, "destination");
        string playlistRoot = Path.Combine(root, "playlists");

        Directory.CreateDirectory(albumSource);
        Directory.CreateDirectory(destinationRoot);
        Directory.CreateDirectory(playlistRoot);

        try
        {
            string sourcePath = Path.Combine(albumSource, "track.mp3");
            await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);

            TrackMetadata metadata = new(
                "Artist",
                "Album Artist",
                "Album",
                HasEmbeddedArt: false);

            MusicFile file = new(
                sourcePath,
                albumSource,
                metadata.Artist,
                metadata.AlbumArtist,
                metadata.Album,
                metadata.HasEmbeddedArt);

            string destinationPath =
                LibraryLayout.GetDestinationPath(
                    file,
                    destinationRoot);
            Directory.CreateDirectory(
                Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
            File.SetLastWriteTimeUtc(
                destinationPath,
                File.GetLastWriteTimeUtc(sourcePath));

            string sourceCover = Path.Combine(albumSource, "Cover.jpg");
            await File.WriteAllBytesAsync(sourceCover, [10, 11, 12]);
            string destinationCover = Path.Combine(
                Path.GetDirectoryName(destinationPath)!,
                "Cover.jpg");
            File.Copy(sourceCover, destinationCover);
            File.SetLastWriteTimeUtc(
                destinationCover,
                File.GetLastWriteTimeUtc(sourceCover));

            string playlistPath = Path.Combine(playlistRoot, "main.m3u");
            await File.WriteAllTextAsync(
                playlistPath,
                sourcePath + "\n",
                new UTF8Encoding(false));

            string mappedLine =
                "/storage/emulated/0/Music/"
                + Path.GetRelativePath(
                    destinationRoot,
                    destinationPath)
                    .Replace('\\', '/');
            string destinationPlaylist =
                Path.Combine(destinationRoot, "main.txt");
            await File.WriteAllTextAsync(
                destinationPlaylist,
                mappedLine,
                new UTF8Encoding(false));

            MusicLoomSettings settings = new()
            {
                Path = new PathSettings
                {
                    SourceDirectory = sourceRoot,
                    DestinationDirectory = destinationRoot,
                    SourcePlaylistDirectory = playlistRoot,
                    DestinationPlaylistBase =
                        "/storage/emulated/0/Music/",
                },
                Organizer = new OrganizerSettings
                {
                    Lyrics = new LyricsSettings
                    {
                        Enabled = false,
                    },
                },
            };

            FileInfo source = new(sourcePath);
            StateDocument state = new();
            state.Tracks[sourcePath] = new TrackState
            {
                SourcePath = sourcePath,
                SourceSize = source.Length,
                SourceLastWriteUtcTicks =
                    source.LastWriteTimeUtc.Ticks,
                QuickHash = QuickFileHash.Compute(sourcePath),
                DestinationPath = destinationPath,
                RecipeVersion = SyncRecipe.For(settings),
                MetadataCaptured = true,
                TitleCaptured = true,
                Artist = metadata.Artist,
                AlbumArtist = metadata.AlbumArtist,
                Album = metadata.Album,
                HasEmbeddedArt = metadata.HasEmbeddedArt,
                ProcessedAt = DateTimeOffset.UtcNow,
            };

            CountingReader initialReader = new();
            LibraryPlan initialPlan =
                await new LibraryScanner(initialReader)
                    .BuildPlanAsync(settings, state);

            Equal(1, initialPlan.UnchangedCount);
            Equal(0, initialPlan.PendingTrackCount);
            Equal(0, initialPlan.CoverWriteCount);
            Equal(0, initialPlan.PlaylistWriteCount);
            Equal(0, initialPlan.PendingCount);
            Equal(0, initialReader.ReadCount);

            await File.WriteAllBytesAsync(
                sourceCover,
                [20, 21, 22, 23]);
            File.SetLastWriteTimeUtc(
                sourceCover,
                File.GetLastWriteTimeUtc(sourceCover)
                    .AddSeconds(2));

            await File.WriteAllTextAsync(
                playlistPath,
                sourcePath + "\n" + sourcePath + "\n",
                new UTF8Encoding(false));

            CountingReader changedReader = new();
            LibraryPlan changedPlan =
                await new LibraryScanner(changedReader)
                    .BuildPlanAsync(settings, state);

            Equal(1, changedPlan.UnchangedCount);
            Equal(0, changedPlan.PendingTrackCount);
            Equal(0, changedPlan.AudioCopyCount);
            Equal(1, changedPlan.CoverWriteCount);
            Equal(1, changedPlan.PlaylistWriteCount);
            Equal(2, changedPlan.PendingCount);
            Equal(0, changedReader.ReadCount);

            LocalMaterializeSummary summary =
                await new LocalLibraryMaterializer()
                    .ApplyAsync(settings, changedPlan);

            Equal(0, summary.CopiedTracks);
            Equal(1, summary.CopiedCovers);
            Equal(1, summary.WrittenPlaylists);

            CountingReader finalReader = new();
            LibraryPlan finalPlan =
                await new LibraryScanner(finalReader)
                    .BuildPlanAsync(settings, state);

            Equal(0, finalPlan.PendingTrackCount);
            Equal(0, finalPlan.CoverWriteCount);
            Equal(0, finalPlan.PlaylistWriteCount);
            Equal(0, finalPlan.PendingCount);
            Equal(0, finalReader.ReadCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"expected '{expected}', got '{actual}'");
        }
    }

    private sealed class CountingReader : ITrackMetadataReader
    {
        public int ReadCount { get; private set; }

        public ValueTask<TrackMetadata> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(new TrackMetadata(
                "Artist",
                "Album Artist",
                "Album",
                HasEmbeddedArt: false));
        }
    }
}
