namespace MusicLoom.App;

internal sealed record PlaylistDisplayItem(
    string Name,
    string SourcePath,
    string DestinationPath,
    IReadOnlyList<LibraryDisplayItem> Tracks)
{
    public int TrackCount => Tracks.Count;
    public string CountText => $"{TrackCount:N0}";
}
