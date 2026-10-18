using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MusicLoom.AudioFingerprint;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Planning;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Core.Synchronization;
using MusicLoom.Netease;

List<(string Name, Action Test)> tests =
[
    ("AFP zero vector matches original WASM", FingerprintZeroVector),
    ("AFP fixed PCM peak positions match original WASM", FingerprintFixedPcm),
    ("LRC parser preserves repeated timestamps", LrcRepeatedTimestamps),
    ("LRC timing offsets do not modify canonical lyrics", LyricsOffsetRegression.Run),
    ("Cloud lyric providers parse search and lyric payloads", CloudLyricsProviderRegression.Run),
    ("Anonymous-only Migu provider and legacy record migration", ExternalProviderIntegrationRegression.Run),
    ("QQ/KuGou remote preview URLs respect provider grants", CloudAudioPreviewRegression.Run),
    ("QQ and KuGou matches persist independently with durable explicit empty states", ProviderMatchPersistenceRegression.Run),
    ("Unofficial QQ/KRC encrypted word timings and client requests", TimedLyricsRegression.Run),
    ("Experimental KuGou PCM recognition signs binary samples", KugouAudioRecognitionRegression.Run),
    ("LRC merge aligns translations within 200 ms", LrcMerge),
    ("LRC merge keeps translations with nearest originals", LrcMergeNearestNeighbor),
    ("LRC merge ignores credits and blank alignment markers", LrcMergeIgnoresCreditsAndBlanks),
    ("LRC merge removes duplicated untranslated phrases", LrcMergeDeduplicatesOriginal),
    ("LRC projection preserves original/translation order", LrcPresentationOrder),
    ("Karaoke timing survives projection, translation and seeking", KaraokeTimingRegression.Run),
    ("LRC slash delimiter only splits explicit bilingual text", LrcSlashPresentation),
    ("Scanning resolves canonical embedded/local/NetEase lyrics", LibraryLyricsIndexerRegression.Run),
    ("NetEase candidate ranking balances duration and metadata", NeteaseCandidateRanking),
    ("Auto-matching skips already checked unchanged audio", AutoMatchEligibilityRegression.Run),
    ("QQ metadata search prefetch deduplicates requests and bounds concurrency", ProviderMetadataSearchPrefetchRegression.Run),
    ("Four-source recognition respects concurrency and commits in order", BoundedMatchWorkQueueRegression.Run),
    ("Match confirmation and clearing immediately reconcile row status", MatchActionStatusRegression.Run),
    ("All providers share review/warning semantics without changing confirmed flags",
        ProviderMatchReviewPolicyRegression.Run),
    ("Clipboard busy errors use bounded asynchronous retries", ClipboardAccessRetryRegression.Run),
    ("Library layout keeps legacy artist/album behavior", Layout),
    ("M3U parser ignores extended-M3U comments", PlaylistParsing),
    ("Warm library scan skips metadata parsing", WarmScanSkipsMetadata),
    ("Stale startup snapshots reconcile against persisted sync state", CachedSnapshotReconciliation),
    ("Legacy metadata cache migrates multi-artist separators", LegacyMetadataMigratesMultiArtist),
    ("Fingerprint window preserves legacy silence semantics", FingerprintWindowCollectorRegression.Run),
    ("WEAPI crypto matches PyNCM reference vector", WeApiCryptoReference),
    ("Recipe refresh does not recopy unchanged audio", RecipeRefreshDoesNotCopy),
    ("Local materializer preserves cover and playlist behavior", LocalMaterializerRegression),
    ("ID3 lyrics writer preserves legacy frame semantics", LyricsWriterRegression.Run),
    ("Netease file recognition uses decoder and WEAPI envelope", NeteaseClientRegression.Run),
    ("Recipe and destination changes reuse cached metadata correctly", RecipeStateRegression.Run),
    ("Transactional sync persists nonfatal outcomes and retries failures", SyncExecutorRegression.Run),
    ("Live sync rejects recursive source/destination layouts", LibraryPathSafetyRegression.Run),
    ("MusicBee ordered artwork retrieval wins over embedded and cached covers", MusicBeeArtworkRegression.Run),
    ("Cover and playlist-only changes remain actionable", AncillaryPlanningRegression.Run),
    ("Destination drift is rematerialized without touching source metadata", DestinationDriftRegression.Run),
    ("State recovery and materialization failures are isolated", ResilienceRegression.Run),
    ("Source hashing, metadata fallback, and lyric TTL stay safe", ParityRegression.Run),
    ("TOML settings and legacy persistence migration stay lossless", DatabasePersistenceRegression.Run),
];

int failed = 0;
foreach ((string name, Action test) in tests)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Count - failed}/{tests.Count} tests passed");
return failed == 0 ? 0 : 1;

static void FingerprintZeroVector()
{
    float[] samples = new float[NeteaseFingerprint.RequiredSampleCount];
    FingerprintResult result = NeteaseFingerprint.Generate(samples);
    const string legacyWasm = "uLsYcabnjbmgFsxqyv/mkZoFEdC1Xl9UO2/XY36Uegs7Yaj4A1AgMunhIHePtEaGnYqN5+YzcvMR0utzqQbYBQ==";
    byte[] expectedRaw = DecodeFingerprint(legacyWasm);
    byte[] actualRaw = DecodeFingerprint(result.Encoded);
    True(expectedRaw.AsSpan().SequenceEqual(actualRaw));
    Equal(0, result.Peaks.Count);
}

static void FingerprintFixedPcm()
{
    float[] samples = new float[NeteaseFingerprint.RequiredSampleCount];
    for (int i = 0; i < samples.Length; i++)
    {
        samples[i] = (((i * 73) % 65_536) - 32_768) / 32_768f;
    }

    FingerprintPeak[] peaks = NeteaseFingerprint.ExtractPeaks(samples);
    (int Frequency, int Time)[] expected =
    [
        (25, 2),
        (25, 11),
        (25, 25),
        (25, 39),
        (25, 52),
        (25, 66),
        (25, 89),
        (25, 103),
        (25, 112),
        (25, 126),
    ];

    Equal(expected.Length, peaks.Length);
    for (int i = 0; i < expected.Length; i++)
    {
        Equal(expected[i].Frequency, peaks[i].FrequencyBin);
        Equal(expected[i].Time, peaks[i].TimeFrame);
    }

    Near(3.0450659f, peaks[0].Amplitude, 0.00002f);
}

static void LrcRepeatedTimestamps()
{
    LrcDocument document = LrcDocument.Parse("[00:01.00][00:02.500]hello");
    Equal(2, document.Lines.Count);
    Equal(TimeSpan.FromSeconds(1), document.Lines[0].Timestamp);
    Equal(TimeSpan.FromMilliseconds(2500), document.Lines[1].Timestamp);
    Equal("hello", document.Lines[0].Text);
}

static void LrcMerge()
{
    LrcDocument original = LrcDocument.Parse("[00:01.000]original");
    LrcDocument translated = LrcDocument.Parse("[00:01.150]translated");
    LrcDocument merged = LrcDocument.Merge(original, translated);
    Equal(2, merged.Lines.Count);
    Equal(TimeSpan.FromSeconds(1), merged.Lines[1].Timestamp);
    Equal("translated", merged.Lines[1].Text);
}

static void LrcMergeNearestNeighbor()
{
    LrcDocument original =
        LrcDocument.Parse(
            "[00:01.000]first\n[00:01.350]second");
    LrcDocument translated =
        LrcDocument.Parse(
            "[00:01.340]第二句\n[00:01.050]作曲：Someone");

    LrcDocument merged =
        LrcDocument.Merge(
            original,
            translated);

    LrcLine translatedSecond =
        merged.Lines.First(
            static line =>
                line.Text == "第二句");

    Equal(
        TimeSpan.FromMilliseconds(1350),
        translatedSecond.Timestamp);

    LrcLine credit =
        merged.Lines.First(
            static line =>
                line.Text == "作曲：Someone");

    Equal(
        TimeSpan.FromMilliseconds(1050),
        credit.Timestamp);
}

static void LrcMergeIgnoresCreditsAndBlanks()
{
    LrcDocument originals = LrcDocument.Parse(
        "[00:01.000]词：Someone\n" +
        "[00:01.120]Original line\n" +
        "[00:04.000]\n" +
        "[00:04.110]Next original");
    LrcDocument translations = LrcDocument.Parse(
        "[00:01.045]第一句译文\n" +
        "[00:04.040]第二句译文");

    LrcDocument result = LrcDocument.Merge(
        originals, translations);
    IReadOnlyList<LrcPresentationLine> projected =
        LrcPresentation.Project(LrcDocument.Parse(result.ToString()));

    Equal(3, projected.Count);
    Equal("词：Someone", projected[0].Original);
    True(projected[0].Translation is null);

    Equal("Original line", projected[1].Original);
    Equal("第一句译文", projected[1].Translation ?? "");
    Equal(TimeSpan.FromMilliseconds(1120), projected[1].Timestamp!.Value);

    Equal("Next original", projected[2].Original);
    Equal("第二句译文", projected[2].Translation ?? "");
}

static void LrcMergeDeduplicatesOriginal()
{
    LrcDocument original = LrcDocument.Parse(
        "[00:01.000]It's on our head");
    LrcDocument repeated = LrcDocument.Parse(
        "[00:01.010]It’s on our head");

    LrcDocument merged = LrcDocument.Merge(original, repeated);
    Equal(1, merged.Lines.Count);
    Equal("It's on our head", merged.Lines[0].Text);
    Equal(1, LrcPresentation.Project(merged).Count);
}

static void LrcPresentationOrder()
{
    LrcDocument original = LrcDocument.Parse(
        "[00:01.000]Original line\n[00:02.000]Second line");
    LrcDocument translated = LrcDocument.Parse(
        "[00:01.140]翻译第一行\n[00:02.120]翻译第二行");
    LrcDocument merged = LrcDocument.Merge(original, translated);

    IReadOnlyList<LrcPresentationLine> projected =
        LrcPresentation.Project(LrcDocument.Parse(merged.ToString()));

    Equal(2, projected.Count);
    Equal("Original line", projected[0].Original);
    Equal("翻译第一行", projected[0].Translation ?? "");
    Equal("Second line", projected[1].Original);
    Equal("翻译第二行", projected[1].Translation ?? "");
}

static void LrcSlashPresentation()
{
    var bilingual = LrcPresentation.SplitInline("君がいる / You are here");
    Equal("君がいる", bilingual.Original);
    Equal("You are here", bilingual.Translation ?? "");

    var ordinary = LrcPresentation.SplitInline("and/or");
    Equal("and/or", ordinary.Original);
    True(ordinary.Translation is null);

    var spacedEnglish = LrcPresentation.SplitInline("yes / no");
    Equal("yes / no", spacedEnglish.Original);
    True(spacedEnglish.Translation is null);

    var url = LrcPresentation.SplitInline("https://example.com/a");
    Equal("https://example.com/a", url.Original);
    True(url.Translation is null);

    LrcDocument credits = LrcDocument.Parse(
        "[00:03.000]作曲：某人\n[00:03.000]Hello\n[00:03.000]你好");
    IReadOnlyList<LrcPresentationLine> lines =
        LrcPresentation.Project(credits);
    Equal(2, lines.Count);
    Equal("作曲：某人", lines[0].Original);
    True(lines[0].Translation is null);
    Equal("Hello", lines[1].Original);
    Equal("你好", lines[1].Translation ?? "");
}

static void NeteaseCandidateRanking()
{
    NeteaseMatchCandidate[] candidates =
    [
        new(
            1,
            "Wrong title",
            "Wrong artist",
            "Wrong album",
            Score: 0.99,
            DurationMs: 200_500),
        new(
            2,
            "Target Song",
            "Wrong artist",
            "Wrong album",
            Score: 0.99,
            DurationMs: 200_500),
        new(
            3,
            "Target Song",
            "Target Artist",
            "Wrong album",
            Score: 0.99,
            DurationMs: 200_500),
        new(
            4,
            "Target Song",
            "Target Artist",
            "Target Album",
            Score: 0.99,
            DurationMs: 200_500),
        new(
            5,
            "Target Song",
            "Target Artist",
            "Target Album",
            Score: 0.99,
            DurationMs: 201_000),
        new(
            6,
            "Completely Different",
            "Other Artist",
            "Other Album",
            Score: 0.999,
            DurationMs: 260_000),
    ];

    IReadOnlyList<NeteaseMatchCandidate>
        ranked =
            NeteaseCandidateRanker.Rank(
                candidates,
                localDurationMs: 200_500,
                title: "Target Song",
                artist: "Target Artist",
                album: "Target Album");

    // Among strong fingerprint candidates with equal duration, prefer
    // the matching artist before minor score/duration differences.
    Equal(4L, ranked[0].TrackId);
    Equal(5L, ranked[1].TrackId);
    Equal(3L, ranked[2].TrackId);
    Equal(2L, ranked[3].TrackId);
    Equal(1L, ranked[4].TrackId);
    Equal(6L, ranked[5].TrackId);

    IReadOnlyList<NeteaseMatchCandidate> sameDuration =
        NeteaseCandidateRanker.Rank(
        [
            new(100, "Song", "Other Singer", "Album",
                Score: 0.998, DurationMs: 180_000),
            new(101, "Song", "Correct Singer", "Album",
                Score: 0.992, DurationMs: 180_000),
        ],
        localDurationMs: 180_000,
        title: "Song",
        artist: "Correct Singer",
        album: "Album");
    Equal(101L, sameDuration[0].TrackId);

    IReadOnlyList<NeteaseMatchCandidate> poorFingerprint =
        NeteaseCandidateRanker.Rank(
        [
            new(200, "Song", "Correct Singer", "Album",
                Score: 0.4, DurationMs: 180_000),
            new(201, "Song", "Other Singer", "Album",
                Score: 0.99, DurationMs: 180_000),
        ],
        localDurationMs: 180_000,
        title: "Song",
        artist: "Correct Singer");
    Equal(201L, poorFingerprint[0].TrackId);
}

static void Layout()
{
    MusicFile file = new(
        SourcePath: Path.Combine("source", "track.mp3"),
        SourceDirectory: "source",
        Artist: "Artist",
        AlbumArtist: "Album:Artist",
        Album: "Album?Name",
        HasEmbeddedArt: false);

    string destination = LibraryLayout.GetDestinationPath(file, "dest");
    string normalized = destination.Replace('\\', '/');
    True(normalized.EndsWith("Album Artist/Album Name/track.mp3", StringComparison.Ordinal));

    MusicFile multiArtist = file with
    {
        AlbumArtist = "Artist A;Artist B",
        Album = "Album",
    };
    string multiDestination =
        LibraryLayout.GetDestinationPath(
            multiArtist,
            "dest")
        .Replace('\\', '/');
    True(multiDestination.EndsWith(
        "Artist A;Artist B/Album/track.mp3",
        StringComparison.Ordinal));
}

static void PlaylistParsing()
{
    IReadOnlyList<string> paths = PlaylistMapper.ParseM3u(
    [
        "#EXTM3U",
        "#EXTINF:123,Example",
        "D:\\MUSIC\\Example.flac",
        "",
    ]);
    Equal(1, paths.Count);
    Equal("D:\\MUSIC\\Example.flac", paths[0]);

    string source =
        Path.GetFullPath(
            Path.Combine(
                "source",
                "Example.flac"));
    string destination =
        Path.GetFullPath(
            Path.Combine(
                "destination",
                "Artist",
                "Album",
                "Example.flac"));
    string destinationRoot =
        Path.GetFullPath(
            "destination");

    IReadOnlyList<string> mapped =
        PlaylistMapper.Map(
            [source],
            new Dictionary<string, string>
            {
                [source] = destination,
            },
            destinationRoot,
            "/storage/emulated/0/Custom Music///");

    Equal(
        1,
        mapped.Count);
    Equal(
        "/storage/emulated/0/Custom Music/Artist/Album/Example.flac",
        mapped[0]);
}

static void CachedSnapshotReconciliation()
{
    string root = Path.Combine(
        Path.GetTempPath(), "musicloom-cache-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);

    try
    {
        string source = Path.Combine(root, "source.mp3");
        string destination = Path.Combine(root, "destination.mp3");
        File.WriteAllBytes(source, [1, 2, 3]);
        File.WriteAllBytes(destination, [1, 2, 3]);

        MusicLoomSettings settings = new()
        {
            Path = new PathSettings
            {
                SourceDirectory = root,
                DestinationDirectory = root,
            },
            Organizer = new OrganizerSettings
            {
                Lyrics = new LyricsSettings { Enabled = false },
            },
        };

        FileInfo sourceInfo = new(source);
        FileInfo destinationInfo = new(destination);
        TrackState recorded = new()
        {
            SourcePath = source,
            SourceSize = sourceInfo.Length,
            SourceLastWriteUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks,
            DestinationPath = destination,
            DestinationSize = destinationInfo.Length,
            DestinationLastWriteUtcTicks = destinationInfo.LastWriteTimeUtc.Ticks,
            RecipeVersion = SyncRecipe.For(settings),
        };

        StateDocument state = new();
        state.Tracks[source] = recorded;
        LibraryPlan oldSnapshot = new(
        [
            new PlanEntry(
                source,
                destination,
                PlanEntryStatus.Modified,
                new TrackMetadata(
                    Artist: "Artist",
                    AlbumArtist: null,
                    Album: null,
                    HasEmbeddedArt: false),
                NeedsAudioCopy: true),
        ]);

        LibraryPlan refreshed = CachedPlanReconciler.Reconcile(
            oldSnapshot,
            state,
            settings);
        Equal(PlanEntryStatus.Unchanged, refreshed.Entries[0].Status);
        True(!refreshed.Entries[0].NeedsAudioCopy);

        state.Tracks[source] = recorded with
        {
            RecipeVersion = "old-recipe",
        };
        refreshed = CachedPlanReconciler.Reconcile(
            oldSnapshot, state, settings);
        Equal(PlanEntryStatus.Modified, refreshed.Entries[0].Status);
        True(!refreshed.Entries[0].NeedsAudioCopy);

        File.AppendAllText(source, "changed");
        refreshed = CachedPlanReconciler.Reconcile(
            oldSnapshot, state, settings);
        True(refreshed.Entries[0].NeedsAudioCopy);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void WarmScanSkipsMetadata()
{
    WarmScanSkipsMetadataAsync().GetAwaiter().GetResult();
}

static async Task WarmScanSkipsMetadataAsync()
{
    string root = Path.Combine(Path.GetTempPath(), "musicloom-test-" + Guid.NewGuid().ToString("N"));
    string sourceRoot = Path.Combine(root, "source");
    string destinationRoot = Path.Combine(root, "destination");
    Directory.CreateDirectory(sourceRoot);
    Directory.CreateDirectory(destinationRoot);

    try
    {
        string sourcePath = Path.Combine(sourceRoot, "track.mp3");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);

        MusicLoomSettings settings = new()
        {
            Path = new PathSettings
            {
                SourceDirectory = sourceRoot,
                DestinationDirectory = destinationRoot,
            },
        };

        CountingMetadataReader coldReader = new();
        LibraryPlan coldPlan = await new LibraryScanner(coldReader)
            .BuildPlanAsync(settings, new StateDocument());

        Equal(1, coldPlan.NewCount);
        Equal(1, coldReader.ReadCount);
        True(coldPlan.Entries[0].NeedsAudioCopy);
        string destinationPath = coldPlan.Entries[0].DestinationPath
            ?? throw new InvalidOperationException("cold plan did not produce a destination path");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath);

        FileInfo source = new(sourcePath);
        StateDocument state = new();
        state.Tracks[sourcePath] = new TrackState
        {
            SourcePath = sourcePath,
            SourceSize = source.Length,
            SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks,
            QuickHash = QuickFileHash.Compute(sourcePath),
            DestinationPath = destinationPath,
            RecipeVersion = SyncRecipe.For(settings),
            MetadataCaptured = true,
            MetadataSchemaVersion = TrackMetadata.CurrentSchemaVersion,
            TitleCaptured = true,
            Artist = "Artist",
            AlbumArtist = "Album Artist",
            Album = "Album",
            HasEmbeddedArt = true,
            ProcessedAt = DateTimeOffset.UtcNow,
        };

        CountingMetadataReader warmReader = new();
        LibraryPlan warmPlan = await new LibraryScanner(warmReader)
            .BuildPlanAsync(settings, state);

        Equal(1, warmPlan.UnchangedCount);
        Equal(0, warmReader.ReadCount);
        True(!warmPlan.Entries[0].NeedsAudioCopy);
        Equal("Album Artist", warmPlan.Entries[0].Metadata?.AlbumArtist ?? string.Empty);

        File.Delete(destinationPath);
        LibraryPlan missingDestinationPlan = await new LibraryScanner(warmReader)
            .BuildPlanAsync(settings, state);

        Equal(1, missingDestinationPlan.ModifiedCount);
        Equal(0, warmReader.ReadCount);
        True(missingDestinationPlan.Entries[0].NeedsAudioCopy);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void LegacyMetadataMigratesMultiArtist()
{
    LegacyMetadataMigratesMultiArtistAsync()
        .GetAwaiter()
        .GetResult();
}

static async Task LegacyMetadataMigratesMultiArtistAsync()
{
    string root = Path.Combine(
        Path.GetTempPath(),
        "musicloom-multiartist-" + Guid.NewGuid().ToString("N"));
    string sourceRoot = Path.Combine(root, "source");
    string destinationRoot = Path.Combine(root, "destination");
    Directory.CreateDirectory(sourceRoot);
    Directory.CreateDirectory(destinationRoot);

    try
    {
        string sourcePath =
            Path.Combine(sourceRoot, "track.mp3");
        await File.WriteAllBytesAsync(
            sourcePath,
            [1, 2, 3, 4]);

        MusicLoomSettings settings = new()
        {
            Path = new PathSettings
            {
                SourceDirectory = sourceRoot,
                DestinationDirectory = destinationRoot,
            },
        };

        string oldDestination =
            Path.Combine(
                destinationRoot,
                "Adam Gubman Telle Smith Sage X Price",
                "Album",
                "track.mp3");
        Directory.CreateDirectory(
            Path.GetDirectoryName(
                oldDestination)!);
        File.Copy(
            sourcePath,
            oldDestination);

        FileInfo source = new(sourcePath);
        FileInfo oldFile =
            new(oldDestination);

        StateDocument state = new();
        state.Tracks[sourcePath] =
            new TrackState
            {
                SourcePath = sourcePath,
                SourceSize = source.Length,
                SourceLastWriteUtcTicks =
                    source.LastWriteTimeUtc.Ticks,
                QuickHash =
                    QuickFileHash.Compute(
                        sourcePath),
                RecipeVersion =
                    SyncRecipe.For(settings),
                DestinationPath =
                    oldDestination,
                DestinationSize =
                    oldFile.Length,
                DestinationLastWriteUtcTicks =
                    oldFile.LastWriteTimeUtc.Ticks,
                MetadataCaptured = true,
                MetadataSchemaVersion = 0,
                TitleCaptured = true,
                Title = "Track",
                AlbumArtist =
                    "Adam Gubman Telle Smith Sage X Price",
                Album = "Album",
                ProcessedAt =
                    DateTimeOffset.UtcNow,
            };

        FixedMetadataReader reader =
            new(
                new TrackMetadata(
                    Artist: null,
                    AlbumArtist:
                        "Adam Gubman;Telle Smith;Sage;X Price",
                    Album: "Album",
                    HasEmbeddedArt: false,
                    Title: "Track"));

        LibraryPlan plan =
            await new LibraryScanner(reader)
                .BuildPlanAsync(
                    settings,
                    state);

        Equal(1, reader.ReadCount);
        Equal(
            TrackMetadata.CurrentSchemaVersion,
            state.Tracks[sourcePath]
                .MetadataSchemaVersion);

        string expectedDirectory =
            Path.Combine(
                destinationRoot,
                "Adam Gubman;Telle Smith;Sage;X Price",
                "Album");

        Equal(
            Path.GetFullPath(
                Path.Combine(
                    expectedDirectory,
                    "track.mp3")),
            Path.GetFullPath(
                plan.Entries[0]
                    .DestinationPath!));
        True(
            plan.Entries[0]
                .NeedsAudioCopy);
    }
    finally
    {
        Directory.Delete(
            root,
            recursive: true);
    }
}

static void RecipeRefreshDoesNotCopy()
{
    RecipeRefreshDoesNotCopyAsync().GetAwaiter().GetResult();
}

static async Task RecipeRefreshDoesNotCopyAsync()
{
    string root = Path.Combine(Path.GetTempPath(), "musicloom-recipe-" + Guid.NewGuid().ToString("N"));
    string sourceRoot = Path.Combine(root, "source");
    string destinationRoot = Path.Combine(root, "destination");
    Directory.CreateDirectory(sourceRoot);
    Directory.CreateDirectory(destinationRoot);

    try
    {
        string sourcePath = Path.Combine(sourceRoot, "track.mp3");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4]);
        MusicLoomSettings settings = new()
        {
            Path = new PathSettings
            {
                SourceDirectory = sourceRoot,
                DestinationDirectory = destinationRoot,
            },
        };

        CountingMetadataReader metadataReader = new();
        LibraryPlan cold = await new LibraryScanner(metadataReader)
            .BuildPlanAsync(settings, new StateDocument());
        string destinationPath = cold.Entries[0].DestinationPath!;
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath);
        File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));

        FileInfo source = new(sourcePath);
        StateDocument oldState = new();
        oldState.Tracks[sourcePath] = new TrackState
        {
            SourcePath = sourcePath,
            SourceSize = source.Length,
            SourceLastWriteUtcTicks = source.LastWriteTimeUtc.Ticks,
            QuickHash = QuickFileHash.Compute(sourcePath),
            RecipeVersion = "1",
            DestinationPath = destinationPath,
            ProcessedAt = DateTimeOffset.UtcNow,
        };

        CountingMetadataReader refreshReader = new();
        LibraryPlan refresh = await new LibraryScanner(refreshReader)
            .BuildPlanAsync(settings, oldState);

        Equal(1, refresh.ModifiedCount);
        Equal(1, refreshReader.ReadCount);
        True(!refresh.Entries[0].NeedsAudioCopy);
        Equal("Refreshing metadata state.", refresh.Entries[0].Detail ?? string.Empty);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void LocalMaterializerRegression()
{
    LocalMaterializerRegressionAsync().GetAwaiter().GetResult();
}

static async Task LocalMaterializerRegressionAsync()
{
    string root = Path.Combine(Path.GetTempPath(), "musicloom-materialize-" + Guid.NewGuid().ToString("N"));
    string sourceRoot = Path.Combine(root, "source");
    string albumSource = Path.Combine(sourceRoot, "incoming");
    string destinationRoot = Path.Combine(root, "destination");
    string playlistRoot = Path.Combine(root, "playlists");
    Directory.CreateDirectory(albumSource);
    Directory.CreateDirectory(playlistRoot);

    try
    {
        string firstSource = Path.Combine(albumSource, "01.mp3");
        string secondSource = Path.Combine(albumSource, "02.flac");
        await File.WriteAllBytesAsync(firstSource, [1, 2, 3, 4, 5]);
        await File.WriteAllBytesAsync(secondSource, [6, 7, 8, 9]);

        string genericCover = Path.Combine(albumSource, "Cover.jpg");
        string explicitCover = Path.Combine(albumSource, "Cover.Album Artist.Album.png");
        await File.WriteAllBytesAsync(genericCover, [10, 11]);
        await File.WriteAllBytesAsync(explicitCover, [12, 13, 14]);

        TrackMetadata firstMetadata = new("Artist", "Album Artist", "Album", HasEmbeddedArt: true);
        TrackMetadata secondMetadata = new("Artist", "Album Artist", "Album", HasEmbeddedArt: false);

        MusicFile firstFile = new(firstSource, albumSource, "Artist", "Album Artist", "Album", true);
        MusicFile secondFile = new(secondSource, albumSource, "Artist", "Album Artist", "Album", false);
        string firstDestination = LibraryLayout.GetDestinationPath(firstFile, destinationRoot);
        string secondDestination = LibraryLayout.GetDestinationPath(secondFile, destinationRoot);

        LibraryPlan plan = new(
        [
            new PlanEntry(firstSource, firstDestination, PlanEntryStatus.New, firstMetadata, NeedsAudioCopy: true),
            new PlanEntry(secondSource, secondDestination, PlanEntryStatus.New, secondMetadata, NeedsAudioCopy: true),
        ]);

        string playlistPath = Path.Combine(playlistRoot, "main.m3u");
        await File.WriteAllTextAsync(
            playlistPath,
            $"#EXTM3U\n#EXTINF:1,One\n{firstSource}\n{secondSource}\n",
            new UTF8Encoding(false));

        MusicLoomSettings settings = new()
        {
            Path = new PathSettings
            {
                SourceDirectory = sourceRoot,
                DestinationDirectory = destinationRoot,
                SourcePlaylistDirectory = playlistRoot,
                DestinationPlaylistBase = "/storage/emulated/0/Music/",
            },
        };

        LocalLibraryMaterializer materializer = new();
        LocalMaterializeSummary first = await materializer.ApplyAsync(settings, plan);

        Equal(2, first.CopiedTracks);
        Equal(1, first.CopiedCovers);
        Equal(1, first.WrittenPlaylists);
        True(File.Exists(firstDestination));
        True(File.Exists(secondDestination));

        string albumDestination = Path.GetDirectoryName(firstDestination)!;
        True(File.Exists(Path.Combine(albumDestination, "Cover.png")));
        True(!File.Exists(Path.Combine(albumDestination, "Cover.jpg")));

        string expectedPlaylist = string.Join(
            "\n",
            "/storage/emulated/0/Music/Album Artist/Album/01.mp3",
            "/storage/emulated/0/Music/Album Artist/Album/02.flac");
        Equal(
            expectedPlaylist,
            await File.ReadAllTextAsync(Path.Combine(destinationRoot, "main.txt"), Encoding.UTF8));

        LocalMaterializeSummary second = await materializer.ApplyAsync(settings, plan);
        Equal(0, second.CopiedTracks);
        Equal(2, second.SkippedTracks);
        Equal(0, second.CopiedCovers);
        Equal(1, second.SkippedCovers);
        Equal(0, second.WrittenPlaylists);
        Equal(1, second.SkippedPlaylists);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void WeApiCryptoReference()
{
    const string json = "{\"id\":\"123\",\"lv\":\"-1\",\"tv\":\"-1\",\"rv\":\"-1\",\"csrf_token\":\"\"}";
    WeApiEnvelope envelope = WeApiCrypto.Encrypt(json, "abcdefghijklmnop");

    const string expectedParams =
        "Irpq13DyPK85A35dZbsbW5MJlEWfdUQc11ob51z42Co6r2p0YOun7KilO4xbhkvVAjxCaN91jjeK7Cwxj0C4tCPw7SAoRr/+cODh07Zxo5F9CP8yzoKG6yhPl9hm4Dl/";
    const string expectedEncSecKey =
        "d15a1683c992095d0c234c19966605c5c5964911268bbeda8cb8d08d834913e59d53b32358903a121b5fca784c1f5ae44951fd02524df58ecc98e52cc7cf8689b42c2e93ddf05b0592512d87f5960467e2f086c018849d76014d323500e30f13ef4cafbb0cf5a66731a3f1776c75ca35d0062dac70a3e33245afabcf47938487";

    Equal(expectedParams, envelope.Params);
    Equal(expectedEncSecKey, envelope.EncSecKey);
}

static byte[] DecodeFingerprint(string encoded)
{
    byte[] encrypted = Convert.FromBase64String(encoded);
    using Aes aes = Aes.Create();
    aes.Key = Encoding.ASCII.GetBytes("4B97221F27F02907");
    aes.Mode = CipherMode.ECB;
    aes.Padding = PaddingMode.PKCS7;
    using ICryptoTransform decryptor = aes.CreateDecryptor();
    byte[] compressed = decryptor.TransformFinalBlock(encrypted, 0, encrypted.Length);

    using MemoryStream input = new(compressed);
    using ZLibStream zlib = new(input, CompressionMode.Decompress);
    using MemoryStream output = new();
    zlib.CopyTo(output);
    return output.ToArray();
}

static void Equal<T>(T expected, T actual)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
    }
}

static void Near(float expected, float actual, float tolerance)
{
    if (MathF.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException($"expected {expected} ± {tolerance}, got {actual}");
    }
}

static void True(bool value)
{
    if (!value)
    {
        throw new InvalidOperationException("condition was false");
    }
}

file sealed class FixedMetadataReader : ITrackMetadataReader
{
    private readonly TrackMetadata _metadata;

    public FixedMetadataReader(
        TrackMetadata metadata)
    {
        _metadata = metadata;
    }

    public int ReadCount { get; private set; }

    public ValueTask<TrackMetadata> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        return ValueTask.FromResult(
            _metadata);
    }
}

file sealed class CountingMetadataReader : ITrackMetadataReader
{
    public int ReadCount { get; private set; }

    public ValueTask<TrackMetadata> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        return ValueTask.FromResult(new TrackMetadata(
            Artist: "Artist",
            AlbumArtist: "Album Artist",
            Album: "Album",
            HasEmbeddedArt: true));
    }
}
