using System.Buffers.Binary;
using System.Text;
using MusicLoom.Core.Lyrics;
using MusicLoom.Tagging;
using TagLib;
using TagLib.Id3v2;
using IOFile = System.IO.File;

internal static class LyricsWriterRegression
{
    private const string ReferenceLrc =
        "[ar:MusicLoom]\n[00:01.000]Hello\n[00:02.500]世界";

    private const string ReferenceSyltHex =
        "03656E6702010048656C6C6F00000003E8E4B896E7958C00000009C4";

    private const string ReferenceUsltHex =
        "03656E674C7972696373005B61723A4D757369634C6F6D5D0A5B30303A30312E3030305D48656C6C6F0A5B30303A30322E3530305DE4B896E7958C00";

    private const string ReferenceTxxxHex =
        "034C5952494353005B61723A4D757369634C6F6D5D0A5B30303A30312E3030305D48656C6C6F0A5B30303A30322E3530305DE4B896E7958C00";

    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        await VerifyFormatAsync(".wav", static path => TestWaveFactory.WritePcm16(path));
        await VerifyFormatAsync(".mp3", WriteMinimalMp3);
    }

    private static async Task VerifyFormatAsync(
        string extension,
        Action<string> createFile)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"musicloom-lyrics-{Guid.NewGuid():N}{extension}");

        try
        {
            createFile(path);
            AddUnrelatedTxxx(path, extension);

            LrcDocument lyrics = LrcDocument.Parse(ReferenceLrc);
            TagLibSharpLyricsWriter writer = new();

            LyricsWriteResult first = await writer.WriteAsync(
                path,
                lyrics,
                overrideExisting: true);
            Equal(LyricsWriteStatus.Written, first.Status);

            IReadOnlyList<Id3Frame> frames = ReadId3Frames(path, extension);
            AssertReferenceFrames(frames);
            AssertKeepFrame(frames);

            byte[] beforeSkip = await IOFile.ReadAllBytesAsync(path);
            LrcDocument replacement = LrcDocument.Parse(
                "[00:03.000]replacement");

            LyricsWriteResult skipped = await writer.WriteAsync(
                path,
                replacement,
                overrideExisting: false);
            Equal(LyricsWriteStatus.SkippedExisting, skipped.Status);

            byte[] afterSkip = await IOFile.ReadAllBytesAsync(path);
            if (!beforeSkip.AsSpan().SequenceEqual(afterSkip))
            {
                throw new InvalidOperationException(
                    $"overrideExisting=false changed '{extension}' bytes.");
            }

            LyricsWriteResult overwritten = await writer.WriteAsync(
                path,
                replacement,
                overrideExisting: true);
            Equal(LyricsWriteStatus.Written, overwritten.Status);

            IReadOnlyList<Id3Frame> overwrittenFrames =
                ReadId3Frames(path, extension);
            Equal(1, overwrittenFrames.Count(static frame => frame.Id == "SYLT"));
            Equal(1, overwrittenFrames.Count(static frame => frame.Id == "USLT"));
            Equal(1, CountTxxx(overwrittenFrames, "LYRICS"));
            AssertKeepFrame(overwrittenFrames);

            string expectedReplacement = replacement.ToString();
            Equal(
                expectedReplacement,
                DecodeUslt(
                    overwrittenFrames.Single(
                        static frame => frame.Id == "USLT")));
            Equal(
                expectedReplacement,
                DecodeTxxxValue(
                    FindTxxx(overwrittenFrames, "LYRICS")));
        }
        finally
        {
            IOFile.Delete(path);
        }
    }

    private static void AssertReferenceFrames(
        IReadOnlyList<Id3Frame> frames)
    {
        Equal(1, frames.Count(static frame => frame.Id == "SYLT"));
        Equal(1, frames.Count(static frame => frame.Id == "USLT"));
        Equal(1, CountTxxx(frames, "LYRICS"));

        Equal(
            ReferenceSyltHex,
            Convert.ToHexString(
                frames.Single(static frame => frame.Id == "SYLT").Data));
        Equal(
            ReferenceUsltHex,
            Convert.ToHexString(
                frames.Single(static frame => frame.Id == "USLT").Data));
        Equal(
            ReferenceTxxxHex,
            Convert.ToHexString(
                FindTxxx(frames, "LYRICS").Data));
    }

    private static void AddUnrelatedTxxx(
        string path,
        string extension)
    {
        using TagLib.File file = OpenTagFile(path, extension);
        TagLib.Tag tag = file.GetTag(TagTypes.Id3v2, create: true)
            ?? throw new InvalidOperationException("Could not create ID3v2 tag.");

        TagLib.Id3v2.Tag id3 = (TagLib.Id3v2.Tag)tag;
        id3.Version = 4;

        UserTextInformationFrame keep = UserTextInformationFrame.Get(
            id3,
            "KEEP",
            StringType.UTF8,
            create: true);
        keep.TextEncoding = StringType.UTF8;
        keep.Text = ["untouched"];
        file.Save();
    }

    private static TagLib.File OpenTagFile(
        string path,
        string extension)
    {
        TagLib.File.IFileAbstraction abstraction =
            new TagLib.File.LocalFileAbstraction(path);

        return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? new TagLib.Riff.File(abstraction, ReadStyle.None)
            : new TagLib.Mpeg.AudioFile(abstraction, ReadStyle.None);
    }

    private static void AssertKeepFrame(
        IReadOnlyList<Id3Frame> frames)
    {
        Equal(1, CountTxxx(frames, "KEEP"));
        Equal(
            "untouched",
            DecodeTxxxValue(FindTxxx(frames, "KEEP")));
    }

    private static int CountTxxx(
        IReadOnlyList<Id3Frame> frames,
        string description) =>
        frames.Count(frame =>
            frame.Id == "TXXX"
            && string.Equals(
                DecodeTxxxDescription(frame),
                description,
                StringComparison.Ordinal));

    private static Id3Frame FindTxxx(
        IReadOnlyList<Id3Frame> frames,
        string description) =>
        frames.Single(frame =>
            frame.Id == "TXXX"
            && string.Equals(
                DecodeTxxxDescription(frame),
                description,
                StringComparison.Ordinal));

    private static string DecodeTxxxDescription(Id3Frame frame)
    {
        ReadOnlySpan<byte> data = frame.Data;
        if (data.Length < 2 || data[0] != 3)
        {
            throw new InvalidDataException(
                "Expected UTF-8 TXXX payload.");
        }

        int separator = data[1..].IndexOf((byte)0);
        if (separator < 0)
        {
            throw new InvalidDataException(
                "TXXX description is missing a terminator.");
        }

        return Encoding.UTF8.GetString(data.Slice(1, separator));
    }

    private static string DecodeTxxxValue(Id3Frame frame)
    {
        ReadOnlySpan<byte> data = frame.Data;
        int separator = data[1..].IndexOf((byte)0);
        if (separator < 0)
        {
            throw new InvalidDataException(
                "TXXX description is missing a terminator.");
        }

        ReadOnlySpan<byte> value = data[(separator + 2)..];
        if (!value.IsEmpty && value[^1] == 0)
        {
            value = value[..^1];
        }

        return Encoding.UTF8.GetString(value);
    }

    private static string DecodeUslt(Id3Frame frame)
    {
        ReadOnlySpan<byte> data = frame.Data;
        if (data.Length < 5 || data[0] != 3)
        {
            throw new InvalidDataException(
                "Expected UTF-8 USLT payload.");
        }

        int descriptionEnd = data[4..].IndexOf((byte)0);
        if (descriptionEnd < 0)
        {
            throw new InvalidDataException(
                "USLT description is missing a terminator.");
        }

        ReadOnlySpan<byte> value =
            data[(4 + descriptionEnd + 1)..];
        if (!value.IsEmpty && value[^1] == 0)
        {
            value = value[..^1];
        }

        return Encoding.UTF8.GetString(value);
    }

    private static IReadOnlyList<Id3Frame> ReadId3Frames(
        string path,
        string extension)
    {
        byte[] data = IOFile.ReadAllBytes(path);
        return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? ReadId3FramesFromWave(data)
            : ReadId3FramesFromMp3(data);
    }

    private static IReadOnlyList<Id3Frame> ReadId3FramesFromWave(
        ReadOnlySpan<byte> wave)
    {
        if (wave.Length < 12
            || !wave[..4].SequenceEqual("RIFF"u8)
            || !wave.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a RIFF/WAVE file.");
        }

        int offset = 12;
        while (offset + 8 <= wave.Length)
        {
            string id = Encoding.ASCII.GetString(wave.Slice(offset, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(
                wave.Slice(offset + 4, 4));

            if (size < 0 || offset + 8 + size > wave.Length)
            {
                throw new InvalidDataException(
                    $"Invalid RIFF chunk '{id}'.");
            }

            if (id.Equals("id3 ", StringComparison.OrdinalIgnoreCase))
            {
                return ParseId3(wave.Slice(offset + 8, size));
            }

            offset += 8 + size + (size & 1);
        }

        throw new InvalidOperationException(
            "WAV does not contain embedded ID3.");
    }

    private static IReadOnlyList<Id3Frame> ReadId3FramesFromMp3(
        ReadOnlySpan<byte> mp3)
    {
        if (mp3.Length < 10 || !mp3[..3].SequenceEqual("ID3"u8))
        {
            throw new InvalidOperationException(
                "MP3 does not start with ID3v2.");
        }

        int tagSize = DecodeSynchsafe(mp3.Slice(6, 4));
        int totalSize = 10 + tagSize;
        if ((mp3[5] & 0x10) != 0)
        {
            totalSize += 10;
        }

        if (totalSize > mp3.Length)
        {
            throw new InvalidDataException(
                "MP3 ID3v2 tag extends past the file.");
        }

        return ParseId3(mp3[..Math.Min(mp3.Length, totalSize)]);
    }

    private static IReadOnlyList<Id3Frame> ParseId3(
        ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 10 || !tag[..3].SequenceEqual("ID3"u8))
        {
            throw new InvalidDataException("Invalid ID3v2 header.");
        }

        int version = tag[3];
        Equal(4, version);

        int tagSize = DecodeSynchsafe(tag.Slice(6, 4));
        int end = Math.Min(tag.Length, 10 + tagSize);
        List<Id3Frame> frames = [];
        int offset = 10;

        while (offset + 10 <= end)
        {
            ReadOnlySpan<byte> header = tag.Slice(offset, 10);
            if (header[..4].IndexOfAnyExcept((byte)0) < 0)
            {
                break;
            }

            string id = Encoding.ASCII.GetString(header[..4]);
            int size = DecodeSynchsafe(header.Slice(4, 4));
            if (size < 0 || offset + 10 + size > end)
            {
                throw new InvalidDataException(
                    $"Invalid ID3 frame '{id}'.");
            }

            frames.Add(new Id3Frame(
                id,
                tag.Slice(offset + 10, size).ToArray()));
            offset += 10 + size;
        }

        return frames;
    }

    private static int DecodeSynchsafe(
        ReadOnlySpan<byte> data) =>
        ((data[0] & 0x7F) << 21)
        | ((data[1] & 0x7F) << 14)
        | ((data[2] & 0x7F) << 7)
        | (data[3] & 0x7F);

    private static void WriteMinimalMp3(string path)
    {
        const int frameLength = 417;
        const int frameCount = 4;
        byte[] data = new byte[frameLength * frameCount];

        for (int frame = 0; frame < frameCount; frame++)
        {
            int offset = frame * frameLength;
            data[offset] = 0xFF;
            data[offset + 1] = 0xFB;
            data[offset + 2] = 0x90;
            data[offset + 3] = 0x64;
        }

        IOFile.WriteAllBytes(path, data);
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

    private sealed record Id3Frame(string Id, byte[] Data);
}
