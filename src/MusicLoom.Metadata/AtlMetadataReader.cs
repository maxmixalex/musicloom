using ATL;
using MusicLoom.Core.Scanning;

namespace MusicLoom.Metadata;

public sealed class AtlMetadataReader : ITrackMetadataReader
{
    public ValueTask<TrackMetadata> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using TagLib.File file =
                TagLib.File.Create(path);

            TagLib.Tag tag = file.Tag;

            return ValueTask.FromResult(
                new TrackMetadata(
                    JoinValues(tag.Performers),
                    JoinValues(tag.AlbumArtists),
                    NullIfWhiteSpace(tag.Album),
                    tag.Pictures.Length > 0,
                    NullIfWhiteSpace(tag.Title)));
        }
        catch
        {
            // ATL remains the compatibility fallback for files that TagLib#
            // cannot open. Do not rely on ATL's flattened multi-value display
            // for the common path: true multi-value tags must keep boundaries.
            ATL.Settings.DisplayValueSeparator = ';';

            Track track = new(path);

            return ValueTask.FromResult(
                new TrackMetadata(
                    NormalizeFlattenedValue(
                        track.Artist),
                    NormalizeFlattenedValue(
                        track.AlbumArtist),
                    NullIfWhiteSpace(track.Album),
                    track.EmbeddedPictures.Count > 0,
                    NullIfWhiteSpace(track.Title)));
        }
    }

    private static string? JoinValues(
        IEnumerable<string> values)
    {
        string[] normalized =
            values
                .Select(static value =>
                    value.Trim())
                .Where(static value =>
                    value.Length > 0)
                .ToArray();

        return normalized.Length == 0
            ? null
            : string.Join(';', normalized);
    }

    private static string? NormalizeFlattenedValue(
        string? value)
    {
        string? normalized =
            NullIfWhiteSpace(value);

        if (normalized is null)
        {
            return null;
        }

        // ATL may expose embedded NUL-separated multi-values depending on the
        // source tag/container. Canonical MusicLoom metadata always uses ';'.
        return string.Join(
            ';',
            normalized
                .Split(
                    ['\0', ';'],
                    StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries));
    }

    private static string? NullIfWhiteSpace(
        string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
}
