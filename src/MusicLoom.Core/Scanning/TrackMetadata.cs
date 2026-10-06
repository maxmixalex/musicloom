namespace MusicLoom.Core.Scanning;

public sealed record TrackMetadata(
    string? Artist,
    string? AlbumArtist,
    string? Album,
    bool HasEmbeddedArt,
    string? Title = null)
{
    // v2 preserves true multi-value Artist/AlbumArtist boundaries as ';'.
    public const int CurrentSchemaVersion = 2;
}

public interface ITrackMetadataReader
{
    ValueTask<TrackMetadata> ReadAsync(
        string path,
        CancellationToken cancellationToken = default);
}
