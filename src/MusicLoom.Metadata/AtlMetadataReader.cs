using ATL;
using MusicLoom.Core.Scanning;

namespace MusicLoom.Metadata;

public sealed class AtlMetadataReader : ITrackMetadataReader
{
    static AtlMetadataReader()
    {
        // ATL exposes true multi-value Artist / AlbumArtist fields through
        // this display separator. Canonical MusicLoom metadata uses ';'.
        ATL.Settings.DisplayValueSeparator = ';';
    }

    public ValueTask<TrackMetadata> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Track track = new(path);

        return ValueTask.FromResult(
            new TrackMetadata(
                NormalizeMultiValue(track.Artist),
                NormalizeMultiValue(track.AlbumArtist),
                NullIfWhiteSpace(track.Album),
                track.EmbeddedPictures.Count > 0,
                NullIfWhiteSpace(track.Title)));
    }

    private static string? NormalizeMultiValue(
        string? value)
    {
        string? normalized =
            NullIfWhiteSpace(value);

        if (normalized is null)
        {
            return null;
        }

        // ATL normally uses DisplayValueSeparator, but normalize embedded
        // NULs defensively for malformed/legacy tags as well.
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
