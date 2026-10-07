using System.Text;
using ATL;
using ATL.AudioData;

namespace MusicLoom.Metadata;

public sealed record TrackInspection(
    byte[]? Artwork,
    string? Lyrics,
    double DurationMs,
    double Bitrate,
    double SampleRate,
    int BitDepth,
    int Channels,
    string ChannelArrangement,
    bool IsVbr,
    bool IsLossless);

public static class AtlTrackInspector
{
    static AtlTrackInspector()
    {
        ATL.Settings.DisplayValueSeparator = ';';
    }

    public static TrackInspection Read(string path)
    {
        Track track = new(path);

        byte[]? artwork =
            track.EmbeddedPictures.Count > 0
                ? [.. track.EmbeddedPictures[0].PictureData]
                : null;

        string? lyrics = ReadLyrics(track);
        return new TrackInspection(
            artwork,
            lyrics,
            track.DurationMs,
            track.Bitrate,
            track.SampleRate,
            track.BitDepth,
            track.ChannelsArrangement.NbChannels,
            track.ChannelsArrangement.Description,
            track.IsVBR,
            AudioDataIOFactory.CF_LOSSLESS
                == track.CodecFamily);
    }

    private static string? ReadLyrics(Track track)
    {
        foreach (LyricsInfo lyrics in track.Lyrics)
        {
            if (!string.IsNullOrWhiteSpace(
                    lyrics.UnsynchronizedLyrics))
            {
                return lyrics.UnsynchronizedLyrics;
            }

            if (lyrics.SynchronizedLyrics.Count == 0)
            {
                continue;
            }

            StringBuilder builder = new();
            foreach (LyricsInfo.LyricsPhrase phrase
                in lyrics.SynchronizedLyrics)
            {
                long totalMilliseconds =
                    Math.Max(0, phrase.TimestampStart);
                long minutes =
                    totalMilliseconds / 60_000;
                long seconds =
                    totalMilliseconds / 1_000 % 60;
                long milliseconds =
                    totalMilliseconds % 1_000;

                builder
                    .Append('[')
                    .Append(minutes.ToString("00"))
                    .Append(':')
                    .Append(seconds.ToString("00"))
                    .Append('.')
                    .Append(milliseconds.ToString("000"))
                    .Append(']')
                    .Append(phrase.Text)
                    .AppendLine();
            }

            string value =
                builder.ToString().TrimEnd();
            if (value.Length > 0)
            {
                return value;
            }
        }

        return null;
    }
}
