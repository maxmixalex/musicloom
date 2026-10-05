using IOFile = System.IO.File;
using System.Buffers.Binary;
using System.Text;
using TagLib;
using TagLib.Id3v2;

string path = Path.Combine(
    Path.GetTempPath(),
    $"musicloom-taglibsharp-{Guid.NewGuid():N}.wav");

try
{
    WriteTestWave(path);

    const string firstLrc = "[00:01.000]Hello\n[00:02.500]世界";
    WriteLyrics(path, firstLrc, [(1_000, "Hello"), (2_500, "世界")], addKeepFrame: true);

    VerifyRawFrames(
        path,
        firstLrc,
        [(1_000, "Hello"), (2_500, "世界")],
        expectKeepFrame: true);

    const string replacementLrc = "[00:03.000]replacement";
    WriteLyrics(path, replacementLrc, [(3_000, "replacement")], addKeepFrame: false);

    VerifyRawFrames(
        path,
        replacementLrc,
        [(3_000, "replacement")],
        expectKeepFrame: true);

    Console.WriteLine("PASS TagLib# explicit RIFF/ID3 mutation under NativeAOT");
    return 0;
}
finally
{
    IOFile.Delete(path);
}

static void WriteLyrics(
    string path,
    string lrc,
    IReadOnlyList<(int Timestamp, string Text)> phrases,
    bool addKeepFrame)
{
    using TagLib.Riff.File file = new(
        new TagLib.File.LocalFileAbstraction(path),
        ReadStyle.None);

    TagLib.Tag tagBase = file.GetTag(TagTypes.Id3v2, create: true)
        ?? throw new InvalidOperationException("Could not create ID3v2 tag.");
    TagLib.Id3v2.Tag tag = (TagLib.Id3v2.Tag)tagBase;

    ByteVector syltId = "SYLT";
    ByteVector usltId = "USLT";
    ByteVector txxxId = "TXXX";

    tag.RemoveFrames(syltId);
    tag.RemoveFrames(usltId);

    foreach (UserTextInformationFrame frame in tag
        .GetFrames<UserTextInformationFrame>(txxxId)
        .Where(static frame =>
            string.Equals(frame.Description, "LYRICS", StringComparison.OrdinalIgnoreCase))
        .ToArray())
    {
        tag.RemoveFrame(frame);
    }

    SynchronisedLyricsFrame sylt = new(
        description: string.Empty,
        language: "eng",
        type: SynchedTextType.Lyrics,
        encoding: StringType.UTF8)
    {
        Format = TimestampFormat.AbsoluteMilliseconds,
        Text = phrases
            .Select(static phrase => new SynchedText(phrase.Timestamp, phrase.Text))
            .ToArray(),
    };
    tag.AddFrame(sylt);

    UnsynchronisedLyricsFrame uslt = new(
        description: "Lyrics",
        language: "eng",
        encoding: StringType.UTF8)
    {
        Text = lrc,
    };
    tag.AddFrame(uslt);

    UserTextInformationFrame lyrics = UserTextInformationFrame.Get(
        tag,
        "LYRICS",
        StringType.UTF8,
        create: true);
    lyrics.TextEncoding = StringType.UTF8;
    lyrics.Text = [lrc];

    if (addKeepFrame)
    {
        UserTextInformationFrame keep = UserTextInformationFrame.Get(
            tag,
            "KEEP",
            StringType.UTF8,
            create: true);
        keep.TextEncoding = StringType.UTF8;
        keep.Text = ["untouched"];
    }

    file.Save();
}

static void VerifyRawFrames(
    string path,
    string expectedLrc,
    IReadOnlyList<(int Timestamp, string Text)> expectedPhrases,
    bool expectKeepFrame)
{
    IReadOnlyList<Id3Frame> frames = ReadId3FramesFromWave(path);

    Equal(1, frames.Count(static frame => frame.Id == "SYLT"));
    Equal(1, frames.Count(static frame => frame.Id == "USLT"));

    Id3Frame sylt = frames.Single(static frame => frame.Id == "SYLT");
    AssertSylt(sylt, expectedPhrases);

    Id3Frame uslt = frames.Single(static frame => frame.Id == "USLT");
    AssertUslt(uslt, "eng", "Lyrics", expectedLrc);

    List<(string Description, string Value)> txxx = frames
        .Where(static frame => frame.Id == "TXXX")
        .Select(ParseTxxx)
        .ToList();

    Equal(1, txxx.Count(static item =>
        string.Equals(item.Description, "LYRICS", StringComparison.OrdinalIgnoreCase)));
    Equal(
        expectedLrc,
        txxx.Single(static item =>
            string.Equals(item.Description, "LYRICS", StringComparison.OrdinalIgnoreCase)).Value);

    if (expectKeepFrame)
    {
        Equal(1, txxx.Count(static item =>
            string.Equals(item.Description, "KEEP", StringComparison.Ordinal)));
        Equal(
            "untouched",
            txxx.Single(static item =>
                string.Equals(item.Description, "KEEP", StringComparison.Ordinal)).Value);
    }
}

static (string Description, string Value) ParseTxxx(Id3Frame frame)
{
    ReadOnlySpan<byte> data = frame.Data;
    if (data.Length < 3)
    {
        throw new InvalidDataException("TXXX payload is too short.");
    }

    int offset = 1;
    string description = ReadText(data, data[0], ref offset, nullTerminated: true);
    string value = ReadText(data, data[0], ref offset, nullTerminated: false);
    return (description, value.TrimEnd('\0'));
}

static void AssertSylt(
    Id3Frame frame,
    IReadOnlyList<(int Timestamp, string Text)> expected)
{
    ReadOnlySpan<byte> data = frame.Data;
    if (data.Length < 7)
    {
        throw new InvalidDataException("SYLT payload is too short.");
    }

    byte encoding = data[0];
    Equal("eng", Encoding.Latin1.GetString(data.Slice(1, 3)));
    Equal((byte)2, data[4]);
    Equal((byte)1, data[5]);

    int offset = 6;
    Equal(string.Empty, ReadText(data, encoding, ref offset, nullTerminated: true));

    foreach ((int timestamp, string expectedText) in expected)
    {
        string text = ReadText(data, encoding, ref offset, nullTerminated: true);
        if (offset + 4 > data.Length)
        {
            throw new InvalidDataException("SYLT phrase is missing its timestamp.");
        }

        int actualTimestamp = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
        offset += 4;

        Equal(expectedText, text);
        Equal(timestamp, actualTimestamp);
    }
}

static void AssertUslt(
    Id3Frame frame,
    string language,
    string description,
    string lyrics)
{
    ReadOnlySpan<byte> data = frame.Data;
    if (data.Length < 5)
    {
        throw new InvalidDataException("USLT payload is too short.");
    }

    byte encoding = data[0];
    Equal(language, Encoding.Latin1.GetString(data.Slice(1, 3)));

    int offset = 4;
    Equal(description, ReadText(data, encoding, ref offset, nullTerminated: true));
    Equal(lyrics, ReadText(data, encoding, ref offset, nullTerminated: false).TrimEnd('\0'));
}

static string ReadText(
    ReadOnlySpan<byte> data,
    byte encoding,
    ref int offset,
    bool nullTerminated)
{
    Encoding textEncoding = encoding switch
    {
        0 => Encoding.Latin1,
        1 => Encoding.Unicode,
        2 => Encoding.BigEndianUnicode,
        3 => Encoding.UTF8,
        _ => throw new InvalidDataException($"Unsupported ID3 text encoding {encoding}."),
    };

    int terminatorLength = encoding is 1 or 2 ? 2 : 1;
    ReadOnlySpan<byte> remaining = data[offset..];
    int length;

    if (!nullTerminated)
    {
        length = remaining.Length;
    }
    else if (terminatorLength == 1)
    {
        length = remaining.IndexOf((byte)0);
        if (length < 0)
        {
            throw new InvalidDataException("ID3 text is missing its terminator.");
        }
    }
    else
    {
        length = -1;
        for (int i = 0; i + 1 < remaining.Length; i += 2)
        {
            if (remaining[i] == 0 && remaining[i + 1] == 0)
            {
                length = i;
                break;
            }
        }

        if (length < 0)
        {
            throw new InvalidDataException("ID3 UTF-16 text is missing its terminator.");
        }
    }

    string value = textEncoding.GetString(remaining[..length]);
    if (value.Length > 0 && value[0] == '\uFEFF')
    {
        value = value[1..];
    }

    offset += length + (nullTerminated ? terminatorLength : 0);
    return value;
}

static IReadOnlyList<Id3Frame> ReadId3FramesFromWave(string path)
{
    byte[] wave = IOFile.ReadAllBytes(path);
    if (!wave.AsSpan(0, 4).SequenceEqual("RIFF"u8)
        || !wave.AsSpan(8, 4).SequenceEqual("WAVE"u8))
    {
        throw new InvalidDataException("Not a RIFF/WAVE file.");
    }

    int offset = 12;
    while (offset + 8 <= wave.Length)
    {
        string id = Encoding.ASCII.GetString(wave, offset, 4);
        int size = BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(offset + 4, 4));
        if (size < 0 || offset + 8 + size > wave.Length)
        {
            throw new InvalidDataException($"Invalid RIFF chunk '{id}'.");
        }

        if (id.Equals("id3 ", StringComparison.OrdinalIgnoreCase))
        {
            return ReadId3Frames(wave.AsSpan(offset + 8, size));
        }

        offset += 8 + size + (size & 1);
    }

    throw new InvalidOperationException("WAV does not contain an embedded ID3 chunk.");
}

static IReadOnlyList<Id3Frame> ReadId3Frames(ReadOnlySpan<byte> tag)
{
    if (tag.Length < 10 || !tag[..3].SequenceEqual("ID3"u8))
    {
        throw new InvalidDataException("Invalid ID3v2 header.");
    }

    int version = tag[3];
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
        int size = version == 4
            ? DecodeSynchsafe(header.Slice(4, 4))
            : BinaryPrimitives.ReadInt32BigEndian(header.Slice(4, 4));
        if (size < 0 || offset + 10 + size > end)
        {
            throw new InvalidDataException($"Invalid ID3 frame '{id}'.");
        }

        frames.Add(new Id3Frame(id, tag.Slice(offset + 10, size).ToArray()));
        offset += 10 + size;
    }

    return frames;
}

static int DecodeSynchsafe(ReadOnlySpan<byte> data) =>
    ((data[0] & 0x7F) << 21)
    | ((data[1] & 0x7F) << 14)
    | ((data[2] & 0x7F) << 7)
    | (data[3] & 0x7F);

static void WriteTestWave(string path)
{
    const int sampleRate = 8_000;
    const int channels = 1;
    const int bitsPerSample = 16;
    const int seconds = 4;

    int frames = sampleRate * seconds;
    int blockAlign = channels * (bitsPerSample / 8);
    int dataSize = frames * blockAlign;
    byte[] data = new byte[44 + dataSize];

    "RIFF"u8.CopyTo(data);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 36 + dataSize);
    "WAVE"u8.CopyTo(data.AsSpan(8));
    "fmt "u8.CopyTo(data.AsSpan(12));
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16, 4), 16);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20, 2), 1);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22, 2), (short)channels);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24, 4), sampleRate);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28, 4), sampleRate * blockAlign);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32, 2), (short)blockAlign);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(34, 2), (short)bitsPerSample);
    "data"u8.CopyTo(data.AsSpan(36));
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40, 4), dataSize);

    IOFile.WriteAllBytes(path, data);
}

static void Equal<T>(T expected, T actual)
    where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
    }
}

file sealed record Id3Frame(string Id, byte[] Data);
