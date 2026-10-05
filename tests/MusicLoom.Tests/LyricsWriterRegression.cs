using System.Buffers.Binary;
using System.Text;
using ATL;
using MusicLoom.Core.Lyrics;
using MusicLoom.Metadata;

internal static class LyricsWriterRegression
{
    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"musicloom-lyrics-{Guid.NewGuid():N}.wav");

        try
        {
            TestWaveFactory.WritePcm16(path);

            LrcDocument lyrics = LrcDocument.Parse(
                "[ar:MusicLoom]\n[00:01.000]Hello\n[00:02.500]世界");
            string lrc = lyrics.ToString();

            AtlLyricsWriter writer = new();
            LyricsWriteResult first = await writer.WriteAsync(
                path,
                lyrics,
                overrideExisting: true);
            Equal(LyricsWriteStatus.Written, first.Status);

            Track track = new(path);
            LyricsInfo synchronized = track.Lyrics.FirstOrDefault(
                static info => info.SynchronizedLyrics.Count == 2)
                ?? throw new InvalidOperationException(
                    "ATL did not expose the synchronized lyric content.");

            Equal(2, synchronized.SynchronizedLyrics.Count);
            Equal(1_000, synchronized.SynchronizedLyrics[0].TimestampStart);
            Equal("Hello", synchronized.SynchronizedLyrics[0].Text);
            Equal(2_500, synchronized.SynchronizedLyrics[1].TimestampStart);
            Equal("世界", synchronized.SynchronizedLyrics[1].Text);

            LyricsInfo unsynchronized = track.Lyrics.FirstOrDefault(
                info => string.Equals(info.UnsynchronizedLyrics, lrc, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    "ATL did not expose the full unsynchronized LRC content.");
            Equal(lrc, unsynchronized.UnsynchronizedLyrics);

            IReadOnlyList<Id3Frame> frames = ReadId3FramesFromWave(path);
            Equal(1, frames.Count(static frame => frame.Id == "SYLT"));
            Equal(1, frames.Count(static frame => frame.Id == "USLT"));
            Equal(1, frames.Count(static frame => frame.Id == "TXXX"));

            Id3Frame sylt = frames.Single(static frame => frame.Id == "SYLT");
            AssertSylt(sylt, [(1_000, "Hello"), (2_500, "世界")]);

            Id3Frame uslt = frames.Single(static frame => frame.Id == "USLT");
            AssertUslt(uslt, "eng", "Lyrics", lrc);

            Id3Frame txxx = frames.Single(static frame => frame.Id == "TXXX");
            AssertTxxx(txxx, "LYRICS", lrc);

            byte[] beforeSkip = await File.ReadAllBytesAsync(path);
            LrcDocument replacement = LrcDocument.Parse("[00:03.000]replacement");
            LyricsWriteResult skipped = await writer.WriteAsync(
                path,
                replacement,
                overrideExisting: false);
            Equal(LyricsWriteStatus.SkippedExisting, skipped.Status);
            byte[] afterSkip = await File.ReadAllBytesAsync(path);
            if (!beforeSkip.AsSpan().SequenceEqual(afterSkip))
            {
                throw new InvalidOperationException(
                    "overrideExisting=false changed a file that already had lyrics.");
            }

            LyricsWriteResult overwritten = await writer.WriteAsync(
                path,
                replacement,
                overrideExisting: true);
            Equal(LyricsWriteStatus.Written, overwritten.Status);

            IReadOnlyList<Id3Frame> overwrittenFrames = ReadId3FramesFromWave(path);
            Equal(1, overwrittenFrames.Count(static frame => frame.Id == "SYLT"));
            Equal(1, overwrittenFrames.Count(static frame => frame.Id == "USLT"));
            Equal(1, overwrittenFrames.Count(static frame => frame.Id == "TXXX"));
            AssertUslt(
                overwrittenFrames.Single(static frame => frame.Id == "USLT"),
                "eng",
                "Lyrics",
                replacement.ToString());
            AssertTxxx(
                overwrittenFrames.Single(static frame => frame.Id == "TXXX"),
                "LYRICS",
                replacement.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertSylt(
        Id3Frame frame,
        IReadOnlyList<(int Timestamp, string Text)> expected)
    {
        ReadOnlySpan<byte> data = frame.Data;
        if (data.Length < 7)
        {
            throw new InvalidOperationException("SYLT payload is too short.");
        }

        Equal((byte)3, data[0]);
        Equal("eng", Encoding.Latin1.GetString(data.Slice(1, 3)));
        Equal((byte)2, data[4]);
        Equal((byte)1, data[5]);

        int offset = 6;
        string description = ReadUtf8NullTerminated(data, ref offset);
        Equal(string.Empty, description);

        for (int index = 0; index < expected.Count; index++)
        {
            if (offset >= data.Length)
            {
                throw new InvalidOperationException(
                    $"SYLT ended before phrase {index}.");
            }

            Equal((byte)10, data[offset++]);
            string text = ReadUtf8NullTerminated(data, ref offset);
            if (offset + 4 > data.Length)
            {
                throw new InvalidOperationException(
                    $"SYLT phrase {index} is missing its timestamp.");
            }

            int timestamp = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
            offset += 4;

            Equal(expected[index].Text, text);
            Equal(expected[index].Timestamp, timestamp);
        }

        Equal(data.Length, offset);
    }

    private static void AssertUslt(
        Id3Frame frame,
        string expectedLanguage,
        string expectedDescription,
        string expectedLyrics)
    {
        ReadOnlySpan<byte> data = frame.Data;
        if (data.Length < 5)
        {
            throw new InvalidOperationException("USLT payload is too short.");
        }

        Equal((byte)3, data[0]);
        Equal(expectedLanguage, Encoding.Latin1.GetString(data.Slice(1, 3)));

        int offset = 4;
        string description = ReadUtf8NullTerminated(data, ref offset);
        string lyrics = ReadUtf8ToEnd(data, offset);

        Equal(expectedDescription, description);
        Equal(expectedLyrics, lyrics);
    }

    private static void AssertTxxx(
        Id3Frame frame,
        string expectedDescription,
        string expectedValue)
    {
        ReadOnlySpan<byte> data = frame.Data;
        if (data.Length < 3)
        {
            throw new InvalidOperationException("TXXX payload is too short.");
        }

        Equal((byte)3, data[0]);
        int offset = 1;
        string description = ReadUtf8NullTerminated(data, ref offset);
        string value = ReadUtf8ToEnd(data, offset);

        Equal(expectedDescription, description);
        Equal(expectedValue, value);
    }

    private static string ReadUtf8NullTerminated(
        ReadOnlySpan<byte> data,
        ref int offset)
    {
        int end = data[offset..].IndexOf((byte)0);
        if (end < 0)
        {
            throw new InvalidDataException("ID3 text is missing its null terminator.");
        }

        string value = Encoding.UTF8.GetString(data.Slice(offset, end));
        offset += end + 1;
        return value;
    }

    private static string ReadUtf8ToEnd(ReadOnlySpan<byte> data, int offset)
    {
        ReadOnlySpan<byte> value = data[offset..];
        while (!value.IsEmpty && value[^1] == 0)
        {
            value = value[..^1];
        }

        return Encoding.UTF8.GetString(value);
    }

    private static IReadOnlyList<Id3Frame> ReadId3FramesFromWave(string path)
    {
        byte[] wave = File.ReadAllBytes(path);
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

            if (id.Equals("id3 ", StringComparison.OrdinalIgnoreCase)
                || id.Equals("ID3 ", StringComparison.OrdinalIgnoreCase))
            {
                return ReadId3Frames(wave.AsSpan(offset + 8, size));
            }

            offset += 8 + size + (size & 1);
        }

        throw new InvalidOperationException("WAV does not contain an embedded ID3 chunk.");
    }

    private static IReadOnlyList<Id3Frame> ReadId3Frames(ReadOnlySpan<byte> tag)
    {
        if (tag.Length < 10 || !tag[..3].SequenceEqual("ID3"u8))
        {
            throw new InvalidDataException("Invalid ID3v2 header.");
        }

        int version = tag[3];
        if (version is not (3 or 4))
        {
            throw new InvalidDataException($"Unexpected ID3v2 version {version}.");
        }

        if ((tag[5] & 0x40) != 0)
        {
            throw new InvalidDataException(
                "Regression parser does not expect an ID3 extended header.");
        }

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

    private static int DecodeSynchsafe(ReadOnlySpan<byte> data)
    {
        return ((data[0] & 0x7F) << 21)
            | ((data[1] & 0x7F) << 14)
            | ((data[2] & 0x7F) << 7)
            | (data[3] & 0x7F);
    }

    private static void Equal<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
        }
    }

    private sealed record Id3Frame(string Id, byte[] Data);
}
