using MusicLoom.AudioFingerprint;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Planning;

List<(string Name, Action Test)> tests =
[
    ("AFP zero vector matches original WASM", FingerprintZeroVector),
    ("AFP fixed PCM peak positions match original WASM", FingerprintFixedPcm),
    ("LRC parser preserves repeated timestamps", LrcRepeatedTimestamps),
    ("LRC merge aligns translations within 200 ms", LrcMerge),
    ("Library layout keeps legacy artist/album behavior", Layout),
    ("M3U parser ignores extended-M3U comments", PlaylistParsing),
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
    const string expected = "uLsYcabnjbmgFsxqyv/mkZoFEdC1Xl9UO2/XY36Uegs7Yaj4A1AgMunhIHePtEaGnYqN5+YzcvMR0utzqQbYBQ==";
    Equal(expected, result.Encoded);
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
