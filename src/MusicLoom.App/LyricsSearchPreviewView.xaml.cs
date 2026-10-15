using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace MusicLoom.App;

/// <summary>
/// One actual reusable timed-lyrics preview for all three matching dialogs.
/// A double-tapped timestamp delegates playback/seek to the hosting provider.
/// </summary>
public sealed partial class LyricsSearchPreviewView : UserControl
{
    public LyricsSearchPreviewView() => InitializeComponent();

    public ListView LyricsList => PreviewList;
    public object? ItemsSource
    {
        get => PreviewList.ItemsSource;
        set => PreviewList.ItemsSource = value;
    }
    public event Action<TimeSpan>? SeekRequested;

    private void PreviewList_DoubleTapped(
        object sender, DoubleTappedRoutedEventArgs e)
    {
        LyricDisplayLine? line =
            (e.OriginalSource as FrameworkElement)?.DataContext
                as LyricDisplayLine
            ?? PreviewList.SelectedItem as LyricDisplayLine;
        if (line?.Timestamp is TimeSpan timestamp)
        {
            e.Handled = true;
            SeekRequested?.Invoke(timestamp);
        }
    }
}
