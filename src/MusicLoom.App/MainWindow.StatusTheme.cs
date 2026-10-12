using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace MusicLoom.App;

public sealed partial class MainWindow
{
    private void RefreshLibraryStatusColors()
    {
        bool dark = AppRoot.ActualTheme == ElementTheme.Dark;
        foreach (LibraryDisplayItem item in _allLibraryItems)
        {
            item.SetDarkTheme(dark);
        }

        WarningSummaryIcon.Foreground = new SolidColorBrush(
            dark ? Color.FromArgb(255, 252, 194, 84)
                 : Color.FromArgb(255, 202, 116, 0));
        ErrorSummaryIcon.Foreground = new SolidColorBrush(
            dark ? Color.FromArgb(255, 255, 138, 132)
                 : Color.FromArgb(255, 196, 43, 28));

        if (_selectedLibraryItem is LibraryDisplayItem current)
        {
            TrackStatusIcon.Foreground = current.StatusBrush;
        }
    }
}
