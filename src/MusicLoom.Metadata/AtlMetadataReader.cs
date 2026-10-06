using ATL;
using MusicLoom.Core.Scanning;

namespace MusicLoom.Metadata;

public sealed class AtlMetadataReader : ITrackMetadataReader
{
    static AtlMetadataReader()
    {
        // musicorg joins true multi-value ARTIST / ALBUMARTIST tags with ';'.
        // ATL exposes the same concept through this process-wide display separator.
        ATL.Settings.DisplayValueSeparator = ';';
    }

    public ValueTask<TrackMetadata> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Track track = new(path);
        TrackMetadata metadata = new(
            NullIfWhiteSpace(track.Artist),
            NullIfWhiteSpace(track.AlbumArtist),
            NullIfWhiteSpace(track.Album),
            track.EmbeddedPictures.Count > 0,
            NullIfWhiteSpace(track.Title));

        return ValueTask.FromResult(metadata);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
