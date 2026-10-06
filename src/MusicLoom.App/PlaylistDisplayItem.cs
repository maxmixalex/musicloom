namespace MusicLoom.App;

internal sealed record PlaylistTrackDisplayItem(
    string Title,
    string Artist,
    string SourcePath)
{
    public string Subtitle =>
        string.IsNullOrWhiteSpace(Artist)
            ? SourcePath
            : Artist;
}

internal sealed record PlaylistDisplayItem(
    string Name,
    string SourcePath,
    string DestinationPath,
    IReadOnlyList<PlaylistTrackDisplayItem> Tracks)
{
    public int TrackCount => Tracks.Count;
    public string CountText => $"{TrackCount:N0}";
}
