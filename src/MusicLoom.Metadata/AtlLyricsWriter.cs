using ATL;
using ATL.AudioData;
using MusicLoom.Core.Lyrics;

namespace MusicLoom.Metadata;

public sealed class AtlLyricsWriter : ITrackLyricsWriter
{
    public ValueTask<LyricsWriteResult> WriteAsync(
        string path,
        LrcDocument lyrics,
        bool overrideExisting,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string extension = Path.GetExtension(path);
        if (!extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(new LyricsWriteResult(
                LyricsWriteStatus.UnsupportedFormat,
                $"Legacy-compatible ID3 lyrics are not enabled for '{extension}'."));
        }

        Track track = new(path);
        bool hasExistingLyrics = track.Lyrics.Any(static info => info.Exists());
        if (hasExistingLyrics && !overrideExisting)
        {
            return ValueTask.FromResult(new LyricsWriteResult(
                LyricsWriteStatus.SkippedExisting,
                "The file already contains synchronized or unsynchronized lyrics."));
        }

        string lrc = lyrics.ToString();

        LyricsInfo synchronized = new()
        {
            ContentType = LyricsInfo.LyricsType.LYRICS,
            Format = LyricsInfo.LyricsFormat.SYNCHRONIZED,
            LanguageCode = "eng",
            Description = string.Empty,
        };

        foreach (LrcLine line in lyrics.Lines)
        {
            long timestamp = (long)line.Timestamp.TotalMilliseconds;
            if (timestamp < 0 || timestamp > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"Lyric timestamp {line.Timestamp} cannot be represented by the ID3 SYLT millisecond format.");
            }

            synchronized.SynchronizedLyrics.Add(
                new LyricsInfo.LyricsPhrase((int)timestamp, line.Text));
        }

        LyricsInfo unsynchronized = new()
        {
            ContentType = LyricsInfo.LyricsType.LYRICS,
            Format = LyricsInfo.LyricsFormat.UNSYNCHRONIZED,
            LanguageCode = "eng",
            Description = "Lyrics",
            UnsynchronizedLyrics = lrc,
        };

        // Keep the legacy write order: SYLT first, then USLT.
        track.Lyrics = [synchronized, unsynchronized];

        Dictionary<string, string> additionalFields = new(
            track.AdditionalFields,
            StringComparer.Ordinal);
        additionalFields["LYRICS"] = lrc;
        track.AdditionalFields = additionalFields;

        if (!track.Save(MetaDataIOFactory.TagType.ID3V2))
        {
            throw new IOException($"ATL failed to save ID3v2 lyrics to '{path}'.");
        }

        return ValueTask.FromResult(new LyricsWriteResult(LyricsWriteStatus.Written));
    }
}
