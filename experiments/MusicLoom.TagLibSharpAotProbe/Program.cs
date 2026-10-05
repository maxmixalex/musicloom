using System.Buffers.Binary;
using System.Text;
using TagLib;
using TagLib.Id3v2;
using IOFile = System.IO.File;

string path = Path.Combine(
    Path.GetTempPath(),
    $"musicloom-taglibsharp-aot-{Guid.NewGuid():N}.wav");

try
{
    WritePcm16Wave(path);
    const string lrc =
        "[ar:MusicLoom]\n[00:01.000]Hello\n[00:02.500]世界";

    WriteLyrics(path, lrc, [(1_000, "Hello"), (2_500, "世界")]);

    IReadOnlyList<Id3Frame> frames = ReadId3FramesFromWave(path);
    Equal(1, frames.Count(static frame => frame.Id == "SYLT"));
    Equal(1, frames.Count(static frame => frame.Id == "USLT"));
    Equal(1, frames.Count(static frame => frame.Id == "TXXX"));

    // These payloads are the Mutagen 1.47.0 reference produced by the
    // legacy MusicOrg write path for the same input.
    Equal(
        "03656E6702010048656C6C6F00000003E8E4B896E7958C00000009C4",
        Convert.ToHexString(frames.Single(static frame => frame.Id == "SYLT").Data));
    Equal(
        "03656E674C7972696373005B61723A4D757369634C6F6F6D5D0A5B30303A30312E3030305D48656C6C6F0A5B30303A30322E3530305DE4B896E7958C00",
        Convert.ToHexString(frames.Single(static frame => frame.Id == "USLT").Data));
    Equal(
        "034C5952494353005B61723A4D757369634C6F6F6D5D0A5B30303A30312E3030305D48656C6C6F0A5B30303A30322E3530305DE4B896E7958C00",
        Convert.ToHexString(frames.Single(static frame => frame.Id == "TXXX").Data));

    // Verify an overwrite does not accumulate duplicate lyric frames and does
    // not require TagLib.File.Create / Activator.CreateInstance.
    const string replacement = "[00:03.000]replacement";
    WriteLyrics(path, replacement, [(3_000, "replacement")]);

    IReadOnlyList<Id3Frame> overwritten = ReadId3FramesFromWave(path);
    Equal(1, overwritten.Count(static frame => frame.Id == "SYLT"));
    Equal(1, overwritten.Count(static frame => frame.Id == "USLT"));
    Equal(1, overwritten.Count(static frame => frame.Id == "TXXX"));

    Console.WriteLine(
        "PASS TagLib# explicit RIFF writer matches Mutagen lyric frame payloads");
    return 0;
}
finally
{
    IOFile.Delete(path);
}

static void WriteLyrics(
    string path,
    string lrc,
    IReadOnlyList<(int Timestamp, string Text)> lines)
{
    using TagLib.File file = new TagLib.Riff.File(
        new TagLib.File.LocalFileAbstraction(path),
        ReadStyle.None);

    TagLib.Tag tag = file.GetTag(TagTypes.Id3v2, create: true)
        ?? throw new InvalidOperationException("Could not create ID3v2 tag.");
    if (tag is not TagLib.Id3v2.Tag id3)
    {
        throw new InvalidOperationException(
            $"Unexpected ID3 tag implementation: {tag.GetType().FullName}");
    }

    // Mutagen 1.47.0 writes ID3v2.4 by default.
    id3.Version = 4;

    ReadOnlyByteVector syltId = "SYLT";
    ReadOnlyByteVector usltId = "USLT";
    ReadOnlyByteVector txxxId = "TXXX";

    id3.RemoveFrames(syltId);
    id3.RemoveFrames(usltId);

    foreach (UserTextInformationFrame frame in id3
        .GetFrames<UserTextInformationFrame>(txxxId)
        .Where(static frame =>
            string.Equals(frame.Description, "LYRICS", StringComparison.Ordinal))
        .ToArray())
    {
        id3.RemoveFrame(frame);
    }

    id3.AddFrame(new MutagenCompatibleFrame(
        "SYLT",
        BuildSyltPayload(lines)));
    id3.AddFrame(new MutagenCompatibleFrame(
        "USLT",
        BuildUsltPayload(lrc)));
    id3.AddFrame(new MutagenCompatibleFrame(
        "TXXX",
        BuildTxxxPayload(lrc)));

    file.Save();
}

static byte[] BuildSyltPayload(
    IReadOnlyList<(int Timestamp, string Text)> lines)
{
    using MemoryStream stream = new();
    stream.WriteByte((byte)StringType.UTF8);
    stream.Write("eng"u8);
    stream.WriteByte((byte)TimestampFormat.AbsoluteMilliseconds);
    stream.WriteByte((byte)SynchedTextType.Lyrics);
    stream.WriteByte(0); // empty description

    Span<byte> timestamp = stackalloc byte[4];
    foreach ((int time, string text) in lines)
    {
        stream.Write(Encoding.UTF8.GetBytes(text));
        stream.WriteByte(0);
        BinaryPrimitives.WriteInt32BigEndian(timestamp, time);
        stream.Write(timestamp);
    }

    return stream.ToArray();
}

static byte[] BuildUsltPayload(string lrc)
{
    using MemoryStream stream = new();
    stream.WriteByte((byte)StringType.UTF8);
    stream.Write("eng"u8);
    stream.Write(Encoding.UTF8.GetBytes("Lyrics"));
    stream.WriteByte(0);
    stream.Write(Encoding.UTF8.GetBytes(lrc));
    stream.WriteByte(0);
    return stream.ToArray();
}

static byte[] BuildTxxxPayload(string lrc)
{
    using MemoryStream stream = new();
    stream.WriteByte((byte)StringType.UTF8);
    stream.Write(Encoding.UTF8.GetBytes("LYRICS"));
    stream.WriteByte(0);
    stream.Write(Encoding.UTF8.GetBytes(lrc));
    stream.WriteByte(0);
    return stream.ToArray();
}

static void WritePcm16Wave(string path)
{
    const int sampleRate = 8_000;
    const short channels = 1;
    const short bitsPerSample = 16;
    const int seconds = 4;

    int frames = sampleRate * seconds;
    int bytesPerSample = bitsPerSample / 8;
    short blockAlign = checked((short)(channels * bytesPerSample));
    int dataSize = frames * blockAlign;
    byte[] data = new byte[44 + dataSize];

    "RIFF"u8.CopyTo(data);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 36 + dataSize);
    "WAVE"u8.CopyTo(data.AsSpan(8));
    "fmt "u8.CopyTo(data.AsSpan(12));
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16, 4), 16);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(20, 2), 1);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(22, 2), channels);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24, 4), sampleRate);
    BinaryPrimitives.WriteInt32LittleEndian(
        data.AsSpan(28, 4),
        sampleRate * blockAlign);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(32, 2), blockAlign);
    BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(34, 2), bitsPerSample);
    "data"u8.CopyTo(data.AsSpan(36));
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40, 4), dataSize);

    int offset = 44;
    for (int frame = 0; frame < frames; frame++)
    {
        short sample = (short)Math.Round(
            Math.Sin(2.0 * Math.PI * 440.0 * frame / sampleRate)
            * 0.25
            * short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset, 2), sample);
        offset += 2;
    }

    IOFile.WriteAllBytes(path, data);
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
        int size = BinaryPrimitives.ReadInt32LittleEndian(
            wave.AsSpan(offset + 4, 4));

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

    throw new InvalidOperationException("WAV does not contain embedded ID3.");
}

static IReadOnlyList<Id3Frame> ReadId3Frames(ReadOnlySpan<byte> tag)
{
    if (tag.Length < 10 || !tag[..3].SequenceEqual("ID3"u8))
    {
        throw new InvalidDataException("Invalid ID3 header.");
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
            throw new InvalidDataException($"Invalid ID3 frame '{id}'.");
        }

        frames.Add(new Id3Frame(id, tag.Slice(offset + 10, size).ToArray()));
        offset += 10 + size;
    }

    return frames;
}

static int DecodeSynchsafe(ReadOnlySpan<byte> data)
{
    return ((data[0] & 0x7F) << 21)
        | ((data[1] & 0x7F) << 14)
        | ((data[2] & 0x7F) << 7)
        | (data[3] & 0x7F);
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

internal sealed class MutagenCompatibleFrame : Frame
{
    private readonly string _id;
    private readonly byte[] _payload;

    public MutagenCompatibleFrame(string id, byte[] payload)
        : base((ReadOnlyByteVector)id, version: 4)
    {
        if (id.Length != 4)
        {
            throw new ArgumentException(
                "ID3v2 frame IDs must contain exactly four characters.",
                nameof(id));
        }

        _id = id;
        _payload = payload;
    }

    protected override void ParseFields(ByteVector data, byte version)
    {
        throw new NotSupportedException(
            "MutagenCompatibleFrame is write-only.");
    }

    protected override ByteVector RenderFields(byte version)
    {
        if (version != 4)
        {
            throw new NotSupportedException(
                $"Mutagen-compatible lyrics are emitted as ID3v2.4, not v2.{version}.");
        }

        return new ByteVector(_payload);
    }

    public override Frame Clone()
    {
        return new MutagenCompatibleFrame(_id, [.. _payload]);
    }
}

internal sealed record Id3Frame(string Id, byte[] Data);
