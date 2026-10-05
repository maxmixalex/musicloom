using MusicLoom.Core.Lyrics;
using MusicLoom.Tagging;
using TagLib;
using TagLib.Mpeg4;
using TagLib.Ogg;
using IOFile = System.IO.File;

if (args.Length != 1)
{
    Console.Error.WriteLine(
        "usage: MusicLoom.FormatCorpus <taglib-sharp samples directory>");
    return 2;
}

string samplesRoot = Path.GetFullPath(args[0]);
if (!Directory.Exists(samplesRoot))
{
    throw new DirectoryNotFoundException(samplesRoot);
}

TagLibSharpLyricsWriter writer = new();
LrcDocument initial = LrcDocument.Parse(
    "[ar:MusicLoom]\n[00:01.000]Hello\n[00:02.500]世界");
LrcDocument replacement = LrcDocument.Parse(
    "[00:03.000]replacement");

await VerifyXiphAsync(
    Path.Combine(samplesRoot, "sample_replaygain.flac"),
    ".flac",
    writer,
    initial,
    replacement);
await VerifyXiphAsync(
    Path.Combine(samplesRoot, "sample.ogg"),
    ".ogg",
    writer,
    initial,
    replacement);
await VerifyXiphAsync(
    Path.Combine(samplesRoot, "sample.opus"),
    ".opus",
    writer,
    initial,
    replacement);
await VerifyMp4Async(
    Path.Combine(samplesRoot, "sample.m4a"),
    writer,
    initial,
    replacement);

LyricsWriteResult wma = await writer.WriteAsync(
    "does-not-need-to-exist.wma",
    initial,
    overrideExisting: true);
Equal(LyricsWriteStatus.UnsupportedFormat, wma.Status);

Console.WriteLine("PASS FLAC/Ogg/Opus/M4A legacy lyric corpus");
return 0;

static async Task VerifyXiphAsync(
    string sourcePath,
    string extension,
    TagLibSharpLyricsWriter writer,
    LrcDocument initial,
    LrcDocument replacement)
{
    string path = CopyFixture(sourcePath, extension);
    try
    {
        string? originalTitle = ReadTitle(path, extension);
        string lrc = initial.ToString();

        LyricsWriteResult first = await writer.WriteAsync(
            path,
            initial,
            overrideExisting: true);
        Equal(LyricsWriteStatus.Written, first.Status);

        using (TagLib.File file = OpenXiph(path, extension))
        {
            XiphComment tag = file.GetTag(TagTypes.Xiph, create: false) as XiphComment
                ?? throw new InvalidOperationException(
                    $"No Xiph tag found after writing {extension}.");

            EqualOne(lrc, tag.GetField("LYRICS"));
            EqualOne(lrc, tag.GetField("LYRICSENG"));
            Equal(originalTitle ?? string.Empty, file.Tag.Title ?? string.Empty);
        }

        byte[] beforeSkip = await IOFile.ReadAllBytesAsync(path);
        LyricsWriteResult skipped = await writer.WriteAsync(
            path,
            replacement,
            overrideExisting: false);
        Equal(LyricsWriteStatus.SkippedExisting, skipped.Status);
        byte[] afterSkip = await IOFile.ReadAllBytesAsync(path);
        SequenceEqual(beforeSkip, afterSkip, $"{extension} skip path changed bytes");

        LyricsWriteResult overwritten = await writer.WriteAsync(
            path,
            replacement,
            overrideExisting: true);
        Equal(LyricsWriteStatus.Written, overwritten.Status);

        using TagLib.File reopened = OpenXiph(path, extension);
        XiphComment overwrittenTag =
            reopened.GetTag(TagTypes.Xiph, create: false) as XiphComment
            ?? throw new InvalidOperationException(
                $"No Xiph tag found after overwriting {extension}.");

        string replacementLrc = replacement.ToString();
        EqualOne(replacementLrc, overwrittenTag.GetField("LYRICS"));
        EqualOne(replacementLrc, overwrittenTag.GetField("LYRICSENG"));
        Equal(originalTitle ?? string.Empty, reopened.Tag.Title ?? string.Empty);
    }
    finally
    {
        IOFile.Delete(path);
    }
}

static async Task VerifyMp4Async(
    string sourcePath,
    TagLibSharpLyricsWriter writer,
    LrcDocument initial,
    LrcDocument replacement)
{
    string path = CopyFixture(sourcePath, ".m4a");
    try
    {
        string? originalTitle;
        using (TagLib.Mpeg4.File file = OpenMp4(path))
        {
            originalTitle = file.Tag.Title;
        }

        string lrc = initial.ToString();
        LyricsWriteResult first = await writer.WriteAsync(
            path,
            initial,
            overrideExisting: true);
        Equal(LyricsWriteStatus.Written, first.Status);

        using (TagLib.Mpeg4.File file = OpenMp4(path))
        {
            AppleTag tag = file.GetTag(TagTypes.Apple, create: false) as AppleTag
                ?? throw new InvalidOperationException(
                    "No Apple tag found after writing M4A.");

            Equal(lrc, tag.Lyrics ?? string.Empty);
            Equal(originalTitle ?? string.Empty, file.Tag.Title ?? string.Empty);
        }

        byte[] beforeSkip = await IOFile.ReadAllBytesAsync(path);
        LyricsWriteResult skipped = await writer.WriteAsync(
            path,
            replacement,
            overrideExisting: false);
        Equal(LyricsWriteStatus.SkippedExisting, skipped.Status);
        byte[] afterSkip = await IOFile.ReadAllBytesAsync(path);
        SequenceEqual(beforeSkip, afterSkip, "M4A skip path changed bytes");

        LyricsWriteResult overwritten = await writer.WriteAsync(
            path,
            replacement,
            overrideExisting: true);
        Equal(LyricsWriteStatus.Written, overwritten.Status);

        using TagLib.Mpeg4.File reopened = OpenMp4(path);
        AppleTag overwrittenTag =
            reopened.GetTag(TagTypes.Apple, create: false) as AppleTag
            ?? throw new InvalidOperationException(
                "No Apple tag found after overwriting M4A.");

        Equal(replacement.ToString(), overwrittenTag.Lyrics ?? string.Empty);
        Equal(originalTitle ?? string.Empty, reopened.Tag.Title ?? string.Empty);
    }
    finally
    {
        IOFile.Delete(path);
    }
}

static string CopyFixture(string sourcePath, string extension)
{
    if (!IOFile.Exists(sourcePath))
    {
        throw new FileNotFoundException("Corpus fixture is missing.", sourcePath);
    }

    string target = Path.Combine(
        Path.GetTempPath(),
        $"musicloom-format-{Guid.NewGuid():N}{extension}");
    IOFile.Copy(sourcePath, target);
    return target;
}

static string? ReadTitle(string path, string extension)
{
    using TagLib.File file = OpenXiph(path, extension);
    return file.Tag.Title;
}

static TagLib.File OpenXiph(string path, string extension)
{
    TagLib.File.IFileAbstraction abstraction =
        new TagLib.File.LocalFileAbstraction(path);

    return extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
        ? new TagLib.Flac.File(abstraction, ReadStyle.None)
        : new TagLib.Ogg.File(abstraction, ReadStyle.None);
}

static TagLib.Mpeg4.File OpenMp4(string path)
{
    TagLib.File.IFileAbstraction abstraction =
        new TagLib.File.LocalFileAbstraction(path);
    return new TagLib.Mpeg4.File(abstraction, ReadStyle.None);
}

static void EqualOne(string expected, string[] values)
{
    Equal(1, values.Length);
    Equal(expected, values[0]);
}

static void SequenceEqual(
    ReadOnlySpan<byte> expected,
    ReadOnlySpan<byte> actual,
    string message)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException(message);
    }
}

static void Equal<T>(T expected, T actual)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"expected '{expected}', got '{actual}'");
    }
}
