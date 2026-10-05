namespace MusicLoom.Core.Planning;

public sealed record MusicFile(
    string SourcePath,
    string SourceDirectory,
    string? Artist,
    string? AlbumArtist,
    string? Album,
    bool HasEmbeddedArt,
    long? NeteaseSongId = null);
