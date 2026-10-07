using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MusicLoom.App;

internal sealed record PlaylistDisplayItem(
    string Name,
    string SourcePath,
    string DestinationPath,
    IReadOnlyList<LibraryDisplayItem> Tracks,
    bool IsReady,
    bool IsVirtual = false,
    bool IsNowPlaying = false,
    bool ShowDividerAfter = false)
{
    public TextWrapping NameTextWrapping { get; private set; } =
        TextWrapping.NoWrap;
    public TextTrimming NameTextTrimming { get; private set; } =
        TextTrimming.CharacterEllipsis;
    public int NameMaxLines { get; private set; } = 1;
    public string? NameToolTip { get; private set; } =
        Name;

    public int TrackCount => Tracks.Count;
    public string CountText => $"{TrackCount:N0}";

    public Visibility SeparatorVisibility =>
        ShowDividerAfter
            ? Visibility.Visible
            : Visibility.Collapsed;

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

    public void ApplyLongTextMode(
        string mode)
    {
        if (MusicLoom.Core.Configuration
                .LongTextModes.Normalize(
                    mode)
            == MusicLoom.Core.Configuration
                .LongTextModes.Wrap)
        {
            NameTextWrapping =
                TextWrapping.WrapWholeWords;
            NameTextTrimming =
                TextTrimming.None;
            NameMaxLines = 2;
            NameToolTip = null;
        }
        else
        {
            NameTextWrapping =
                TextWrapping.NoWrap;
            NameTextTrimming =
                TextTrimming.CharacterEllipsis;
            NameMaxLines = 1;
            NameToolTip = Name;
        }
    }
}
