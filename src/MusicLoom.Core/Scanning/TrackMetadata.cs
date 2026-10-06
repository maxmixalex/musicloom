namespace MusicLoom.Core.Scanning;

public sealed record TrackMetadata(
    string? Artist,
    string? AlbumArtist,
    string? Album,
    bool HasEmbeddedArt,
    string? Title = null);

public interface ITrackMetadataReader
{
    ValueTask<TrackMetadata> ReadAsync(
        string path,
        CancellationToken cancellationToken = default);
}
