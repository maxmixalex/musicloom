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
            LyricsInfo synchronized = track.Lyrics.Single(
                static info => info.SynchronizedLyrics.Count > 0);
            LyricsInfo unsynchronized = track.Lyrics.Single(
                static info => !string.IsNullOrEmpty(info.UnsynchronizedLyrics));

            Equal("eng", synchronized.LanguageCode);
            Equal(string.Empty, synchronized.Description);
            Equal(LyricsInfo.LyricsType.LYRICS, synchronized.ContentType);
            Equal(2, synchronized.SynchronizedLyrics.Count);
            Equal(1_000, synchronized.SynchronizedLyrics[0].TimestampStart);
            Equal("Hello", synchronized.SynchronizedLyrics[0].Text);
            Equal(2_500, synchronized.SynchronizedLyrics[1].TimestampStart);
            Equal("世界", synchronized.SynchronizedLyrics[1].Text);

            Equal("eng", unsynchronized.LanguageCode);
            Equal("Lyrics", unsynchronized.Description);
            Equal(lrc, unsynchronized.UnsynchronizedLyrics);

            if (!track.AdditionalFields.TryGetValue("LYRICS", out string? customLyrics))
            {
                throw new InvalidOperationException("TXXX:LYRICS did not round-trip through ATL.");
            }
            Equal(lrc, customLyrics);

            IReadOnlyList<Id3Frame> frames = ReadId3FramesFromWave(path);
            Equal(1, frames.Count(static frame => frame.Id == "SYLT"));
            Equal(1, frames.Count(static frame => frame.Id == "USLT"));
            Equal(1, frames.Count(static frame => frame.Id == "TXXX"));

            Id3Frame sylt = frames.Single(static frame => frame.Id == "SYLT");
            if (sylt.Data.Length < 7)
            {
                throw new InvalidOperationException("SYLT payload is too short.");
            }

            Equal("eng", Encoding.Latin1.GetString(sylt.Data.AsSpan(1, 3)));
            Equal((byte)2, sylt.Data[4]);
            Equal((byte)1, sylt.Data[5]);
            ContainsSequence(sylt.Data, [0x00, 0x00, 0x03, 0xE8]);
            ContainsSequence(sylt.Data, [0x00, 0x00, 0x09, 0xC4]);

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

            Track overwrittenTrack = new(path);
            Equal(
                replacement.ToString(),
                overwrittenTrack.Lyrics.Single(
                    static info => !string.IsNullOrEmpty(info.UnsynchronizedLyrics))
                    .UnsynchronizedLyrics);
        }
        finally
        {
            File.Delete(path);
        }
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

    private static void ContainsSequence(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected)
    {
        if (data.IndexOf(expected) < 0)
        {
            throw new InvalidOperationException(
                $"Expected byte sequence {Convert.ToHexString(expected)} was not found.");
        }
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
