using System.Buffers.Binary;
using System.Text;
using MusicLoom.Core.Lyrics;
using TagLib;
using TagLib.Id3v2;

namespace MusicLoom.Tagging;

public sealed class TagLibSharpLyricsWriter : ITrackLyricsWriter
{
    private static readonly ReadOnlyByteVector SyltId = "SYLT";
    private static readonly ReadOnlyByteVector UsltId = "USLT";
    private static readonly ReadOnlyByteVector TxxxId = "TXXX";

    public ValueTask<LyricsWriteResult> WriteAsync(
        string path,
        LrcDocument lyrics,
        bool overrideExisting,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string extension = Path.GetExtension(path);
        if (!IsSupported(extension))
        {
            return ValueTask.FromResult(new LyricsWriteResult(
                LyricsWriteStatus.UnsupportedFormat,
                $"Legacy-compatible ID3 lyrics are not enabled for '{extension}'."));
        }

        using TagLib.File file = OpenFile(path, extension);
        TagLib.Tag tag = file.GetTag(TagTypes.Id3v2, create: true)
            ?? throw new InvalidOperationException(
                $"Could not create an ID3v2 tag for '{path}'.");

        if (tag is not TagLib.Id3v2.Tag id3)
        {
            throw new InvalidOperationException(
                $"Unexpected ID3 tag implementation '{tag.GetType().FullName}'.");
        }

        // The old Mutagen path considered only SYLT / USLT when deciding
        // whether lyrics already existed. TXXX:LYRICS alone did not trigger a skip.
        bool hasExistingLyrics =
            id3.GetFrames(SyltId).Any()
            || id3.GetFrames(UsltId).Any();

        if (hasExistingLyrics && !overrideExisting)
        {
            return ValueTask.FromResult(new LyricsWriteResult(
                LyricsWriteStatus.SkippedExisting,
                "The file already contains synchronized or unsynchronized lyrics."));
        }

        // Mutagen 1.47 writes ID3v2.4 by default. Keep the same version so
        // frame-size encoding and payload semantics stay compatible.
        id3.Version = 4;

        id3.RemoveFrames(SyltId);
        id3.RemoveFrames(UsltId);

        foreach (UserTextInformationFrame frame in id3
            .GetFrames<UserTextInformationFrame>(TxxxId)
            .Where(static frame =>
                string.Equals(
                    frame.Description,
                    "LYRICS",
                    StringComparison.Ordinal))
            .ToArray())
        {
            id3.RemoveFrame(frame);
        }

        string lrc = lyrics.ToString();

        id3.AddFrame(new MutagenCompatibleFrame(
            "SYLT",
            BuildSyltPayload(lyrics.Lines)));
        id3.AddFrame(new MutagenCompatibleFrame(
            "USLT",
            BuildUsltPayload(lrc)));
        id3.AddFrame(new MutagenCompatibleFrame(
            "TXXX",
            BuildTxxxPayload(lrc)));

        file.Save();
        return ValueTask.FromResult(new LyricsWriteResult(LyricsWriteStatus.Written));
    }

    private static bool IsSupported(string extension) =>
        extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);

    private static TagLib.File OpenFile(string path, string extension)
    {
        TagLib.File.IFileAbstraction abstraction =
            new TagLib.File.LocalFileAbstraction(path);

        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return new TagLib.Riff.File(abstraction, ReadStyle.None);
        }

        return new TagLib.Mpeg.AudioFile(abstraction, ReadStyle.None);
    }

    private static byte[] BuildSyltPayload(IReadOnlyList<LrcLine> lines)
    {
        using MemoryStream stream = new();
        stream.WriteByte((byte)StringType.UTF8);
        stream.Write("eng"u8);
        stream.WriteByte((byte)TimestampFormat.AbsoluteMilliseconds);
        stream.WriteByte((byte)SynchedTextType.Lyrics);
        stream.WriteByte(0); // empty description

        Span<byte> timestamp = stackalloc byte[4];
        foreach (LrcLine line in lines)
        {
            long milliseconds = (long)line.Timestamp.TotalMilliseconds;
            if (milliseconds < 0 || milliseconds > uint.MaxValue)
            {
                throw new InvalidDataException(
                    $"Lyric timestamp {line.Timestamp} cannot be represented by ID3 SYLT.");
            }

            stream.Write(Encoding.UTF8.GetBytes(line.Text));
            stream.WriteByte(0);
            BinaryPrimitives.WriteUInt32BigEndian(timestamp, (uint)milliseconds);
            stream.Write(timestamp);
        }

        return stream.ToArray();
    }

    private static byte[] BuildUsltPayload(string lrc)
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

    private static byte[] BuildTxxxPayload(string lrc)
    {
        using MemoryStream stream = new();
        stream.WriteByte((byte)StringType.UTF8);
        stream.Write(Encoding.UTF8.GetBytes("LYRICS"));
        stream.WriteByte(0);
        stream.Write(Encoding.UTF8.GetBytes(lrc));
        stream.WriteByte(0);
        return stream.ToArray();
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

    public override Frame Clone() =>
        new MutagenCompatibleFrame(_id, [.. _payload]);
}
