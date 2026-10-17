using System.Buffers.Binary;
using System.Text;
using MusicLoom.Core.Lyrics;
using TagLib;
using TagLib.Id3v2;
using TagLib.Mpeg4;
using TagLib.Ogg;

namespace MusicLoom.Tagging;

public sealed class TagLibSharpLyricsWriter :
    ITrackLyricsWriter, IEmbeddedLyricsCleaner
{
    private static readonly ReadOnlyByteVector SyltId = "SYLT";
    private static readonly ReadOnlyByteVector UsltId = "USLT";
    private static readonly ReadOnlyByteVector TxxxId = "TXXX";

    public ValueTask<LyricsProbeResult> ProbeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string extension = Path.GetExtension(path);
        LyricsProbeResult result;

        if (IsId3(extension))
        {
            using TagLib.File file = OpenId3File(path, extension);
            TagLib.Id3v2.Tag? id3 =
                file.GetTag(TagTypes.Id3v2, create: false) as TagLib.Id3v2.Tag;

            bool present = id3 is not null
                && (id3.GetFrames(SyltId).Any()
                    || id3.GetFrames(UsltId).Any());
            result = new LyricsProbeResult(
                present ? LyricsProbeStatus.Present : LyricsProbeStatus.Missing);
        }
        else if (IsXiph(extension))
        {
            using TagLib.File file = OpenXiphFile(path, extension);
            XiphComment? tag =
                file.GetTag(TagTypes.Xiph, create: false) as XiphComment;

            bool present = tag is not null
                && (tag.GetField("LYRICS").Length > 0
                    || tag.GetField("LYRICSENG").Length > 0);
            result = new LyricsProbeResult(
                present ? LyricsProbeStatus.Present : LyricsProbeStatus.Missing);
        }
        else if (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase))
        {
            TagLib.File.IFileAbstraction abstraction =
                new TagLib.File.LocalFileAbstraction(path);
            using TagLib.Mpeg4.File file =
                new(abstraction, ReadStyle.None);

            AppleTag? tag =
                file.GetTag(TagTypes.Apple, create: false) as AppleTag;
            result = new LyricsProbeResult(
                !string.IsNullOrEmpty(tag?.Lyrics)
                    ? LyricsProbeStatus.Present
                    : LyricsProbeStatus.Missing);
        }
        else
        {
            result = new LyricsProbeResult(
                LyricsProbeStatus.UnsupportedFormat,
                $"Legacy lyrics are not supported for '{extension}'.");
        }

        return ValueTask.FromResult(result);
    }

    public ValueTask<LyricsWriteResult> WriteAsync(
        string path,
        LrcDocument lyrics,
        bool overrideExisting,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string extension = Path.GetExtension(path);
        string lrc = lyrics.ToStandardLrcString();

        LyricsWriteResult result;
        if (IsId3(extension))
        {
            result = WriteId3(path, extension, lyrics, lrc, overrideExisting);
        }
        else if (IsXiph(extension))
        {
            result = WriteXiph(path, extension, lrc, overrideExisting);
        }
        else if (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase))
        {
            result = WriteMp4(path, lrc, overrideExisting);
        }
        else
        {
            result = new LyricsWriteResult(
                LyricsWriteStatus.UnsupportedFormat,
                $"Legacy lyrics are not enabled for '{extension}'.");
        }

        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// In sidecar mode, clean only the destination audio's old lyric tags;
    /// unrelated tags and the user's source audio remain unchanged.
    /// Idempotent, including when there is no available lyric document.
    /// </summary>
    public ValueTask<bool> RemoveAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(destinationPath);

        if (IsId3(extension))
        {
            using TagLib.File file = OpenId3File(destinationPath, extension);
            TagLib.Id3v2.Tag? id3 =
                file.GetTag(TagTypes.Id3v2, create: false)
                    as TagLib.Id3v2.Tag;
            if (id3 is null)
            {
                return ValueTask.FromResult(false);
            }

            bool hasLyrics = id3.GetFrames(SyltId).Any()
                || id3.GetFrames(UsltId).Any();
            UserTextInformationFrame[] lyricFrames = id3
                .GetFrames<UserTextInformationFrame>(TxxxId)
                .Where(static frame => string.Equals(
                    frame.Description, "LYRICS",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (!hasLyrics && lyricFrames.Length == 0)
            {
                return ValueTask.FromResult(false);
            }

            id3.RemoveFrames(SyltId);
            id3.RemoveFrames(UsltId);
            foreach (UserTextInformationFrame frame in lyricFrames)
            {
                id3.RemoveFrame(frame);
            }
            file.Save();
            return ValueTask.FromResult(true);
        }

        if (IsXiph(extension))
        {
            using TagLib.File file = OpenXiphFile(destinationPath, extension);
            XiphComment? tag =
                file.GetTag(TagTypes.Xiph, create: false) as XiphComment;
            if (tag is null)
            {
                return ValueTask.FromResult(false);
            }

            string[] fields = ["LYRICS", "LYRICSENG"];
            if (!fields.Any(field => tag.GetField(field).Length > 0))
            {
                return ValueTask.FromResult(false);
            }

            foreach (string field in fields)
            {
                tag.RemoveField(field);
            }
            file.Save();
            return ValueTask.FromResult(true);
        }

        if (extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase))
        {
            TagLib.File.IFileAbstraction abstraction =
                new TagLib.File.LocalFileAbstraction(destinationPath);
            using TagLib.Mpeg4.File file =
                new(abstraction, ReadStyle.None);
            AppleTag? tag =
                file.GetTag(TagTypes.Apple, create: false) as AppleTag;
            if (string.IsNullOrEmpty(tag?.Lyrics))
            {
                return ValueTask.FromResult(false);
            }

            tag.Lyrics = string.Empty;
            file.Save();
            return ValueTask.FromResult(true);
        }

        // Unknown formats must not be destructively rewritten.
        return ValueTask.FromResult(false);
    }

    private static LyricsWriteResult WriteId3(
        string path,
        string extension,
        LrcDocument lyrics,
        string lrc,
        bool overrideExisting)
    {
        using TagLib.File file = OpenId3File(path, extension);
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
            return new LyricsWriteResult(
                LyricsWriteStatus.SkippedExisting,
                "The file already contains synchronized or unsynchronized lyrics.");
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
        return new LyricsWriteResult(LyricsWriteStatus.Written);
    }

    private static LyricsWriteResult WriteXiph(
        string path,
        string extension,
        string lrc,
        bool overrideExisting)
    {
        using TagLib.File file = OpenXiphFile(path, extension);
        XiphComment tag = file.GetTag(TagTypes.Xiph, create: true) as XiphComment
            ?? throw new InvalidOperationException(
                $"Could not obtain a Xiph/Vorbis comment for '{path}'.");

        // Mutagen's VCommentDict is case-insensitive. The legacy sequence
        // lyrics -> LYRICS -> LYRICSENG therefore serializes as exactly these
        // two fields: LYRICS and LYRICSENG.
        bool hasExistingLyrics =
            tag.GetField("LYRICS").Length > 0
            || tag.GetField("LYRICSENG").Length > 0;

        if (hasExistingLyrics && !overrideExisting)
        {
            return new LyricsWriteResult(
                LyricsWriteStatus.SkippedExisting,
                "The file already contains Vorbis lyrics.");
        }

        tag.RemoveField("LYRICS");
        tag.RemoveField("LYRICSENG");
        tag.SetField("LYRICS", lrc);
        tag.SetField("LYRICSENG", lrc);

        file.Save();
        return new LyricsWriteResult(LyricsWriteStatus.Written);
    }

    private static LyricsWriteResult WriteMp4(
        string path,
        string lrc,
        bool overrideExisting)
    {
        TagLib.File.IFileAbstraction abstraction =
            new TagLib.File.LocalFileAbstraction(path);
        using TagLib.Mpeg4.File file =
            new(abstraction, ReadStyle.None);

        AppleTag tag = file.GetTag(TagTypes.Apple, create: true) as AppleTag
            ?? throw new InvalidOperationException(
                $"Could not obtain an Apple tag for '{path}'.");

        if (!string.IsNullOrEmpty(tag.Lyrics) && !overrideExisting)
        {
            return new LyricsWriteResult(
                LyricsWriteStatus.SkippedExisting,
                "The file already contains MP4 lyrics.");
        }

        // The old Mutagen writer emits only the standard ©lyr atom.
        tag.Lyrics = lrc;
        file.Save();
        return new LyricsWriteResult(LyricsWriteStatus.Written);
    }

    private static bool IsId3(string extension) =>
        extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);

    private static bool IsXiph(string extension) =>
        extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".opus", StringComparison.OrdinalIgnoreCase);

    private static TagLib.File OpenId3File(
        string path,
        string extension)
    {
        TagLib.File.IFileAbstraction abstraction =
            new TagLib.File.LocalFileAbstraction(path);

        return extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            ? new TagLib.Riff.File(abstraction, ReadStyle.None)
            : new TagLib.Mpeg.AudioFile(abstraction, ReadStyle.None);
    }

    private static TagLib.File OpenXiphFile(
        string path,
        string extension)
    {
        TagLib.File.IFileAbstraction abstraction =
            new TagLib.File.LocalFileAbstraction(path);

        return extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            ? new TagLib.Flac.File(abstraction, ReadStyle.None)
            : new TagLib.Ogg.File(abstraction, ReadStyle.None);
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
            // SYLT stores a time per syllable. Preserve precise QRC/KRC
            // word timings when available; plain LRC stays line-timed.
            IEnumerable<(TimeSpan Time, string Text)> segments =
                line.Words is { Count: > 0 }
                    ? line.Words.Select(static word => (word.Start, word.Text))
                    : [(line.Timestamp, line.Text)];
            foreach ((TimeSpan time, string text) in segments)
            {
                long milliseconds = (long)time.TotalMilliseconds;
                if (milliseconds < 0 || milliseconds > uint.MaxValue)
                {
                    throw new InvalidDataException(
                        $"Lyric timestamp {time} cannot be represented by ID3 SYLT.");
                }

                stream.Write(Encoding.UTF8.GetBytes(text));
                stream.WriteByte(0);
                BinaryPrimitives.WriteUInt32BigEndian(timestamp, (uint)milliseconds);
                stream.Write(timestamp);
            }
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
