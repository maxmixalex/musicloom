using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MusicLoom.AudioFingerprint;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Planning;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Netease;

List<(string Name, Action Test)> tests =
[
    ("AFP zero vector matches original WASM", FingerprintZeroVector),
    ("AFP fixed PCM peak positions match original WASM", FingerprintFixedPcm),
    ("LRC parser preserves repeated timestamps", LrcRepeatedTimestamps),
    ("LRC merge aligns translations within 200 ms", LrcMerge),
    ("Library layout keeps legacy artist/album behavior", Layout),
    ("M3U parser ignores extended-M3U comments", PlaylistParsing),
    ("Warm library scan skips metadata parsing", WarmScanSkipsMetadata),
    ("Fingerprint window preserves legacy silence semantics", FingerprintWindowCollectorRegression.Run),
    ("WEAPI crypto matches PyNCM reference vector", WeApiCryptoReference),
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
            DestinationPath = destinationPath,
            MetadataCaptured = true,
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
        Equal("Album Artist", warmPlan.Entries[0].Metadata?.AlbumArtist ?? string.Empty);

        File.Delete(destinationPath);
        LibraryPlan missingDestinationPlan = await new LibraryScanner(warmReader)
            .BuildPlanAsync(settings, state);

        Equal(1, missingDestinationPlan.ModifiedCount);
        Equal(0, warmReader.ReadCount);
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
