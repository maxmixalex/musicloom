using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MusicLoom.App;

internal sealed record PlaylistDisplayItem(
    string Name,
    string SourcePath,
    string DestinationPath,
    IReadOnlyList<LibraryDisplayItem> Tracks,
    bool IsReady,
    bool IsVirtual = false)
{
    public int TrackCount => Tracks.Count;
    public string CountText => $"{TrackCount:N0}";

    public string StatusGlyph =>
        IsVirtual
            ? string.Empty
            : IsReady
                ? "\uE73E"
                : "\uE823";

    public Brush StatusBrush =>
        new SolidColorBrush(
            IsReady
                ? Color.FromArgb(
                    255, 16, 124, 65)
                : Color.FromArgb(
                    255, 202, 116, 0));

    public string StatusKey =>
        IsReady ? "ready" : "pending";
}
