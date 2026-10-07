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
            ? "\uE8A9"
            : IsReady
                ? "\uE73E"
                : "\uE823";

    public Brush StatusBrush =>
        new SolidColorBrush(
            IsVirtual
                ? Color.FromArgb(
                    255, 96, 94, 92)
                : IsReady
                    ? Color.FromArgb(
                        255, 16, 124, 65)
                    : Color.FromArgb(
                        255, 202, 116, 0));

    public string StatusKey =>
        IsVirtual
            ? "virtual"
            : IsReady
                ? "ready"
                : "pending";
}
