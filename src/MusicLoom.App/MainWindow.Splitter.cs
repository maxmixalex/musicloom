using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace MusicLoom.App;

public sealed partial class MainWindow
{
    private bool _splitDragging;
    private bool _splitFrameQueued;
    private double _splitStartLeft;
    private double _splitTotal;
    private double _splitDelta;

    private void LibrarySplitter_DragStarted(
        object sender, DragStartedEventArgs e)
    {
        if (DetailPane.Visibility != Visibility.Visible)
        {
            return;
        }

        _splitStartLeft = LibraryListColumn.ActualWidth;
        _splitTotal = LibraryListColumn.ActualWidth
            + LibraryDetailColumn.ActualWidth;
        _splitDelta = 0;
        _splitDragging = true;
        PlayingWaveform.SuspendDuringLibrarySplit = true;
    }

    private void LibrarySplitter_DragDelta(
        object sender, DragDeltaEventArgs e)
    {
        if (!_splitDragging)
        {
            return;
        }

        _splitDelta += e.HorizontalChange;
        if (!_splitFrameQueued)
        {
            _splitFrameQueued = true;
            CompositionTarget.Rendering += LibrarySplitter_ApplyFrame;
        }
    }

    private void LibrarySplitter_ApplyFrame(object? sender, object? args)
    {
        if (_splitFrameQueued)
        {
            CompositionTarget.Rendering -= LibrarySplitter_ApplyFrame;
            _splitFrameQueued = false;
        }

        if (!_splitDragging || _splitTotal <= 0)
        {
            return;
        }

        // One grid relayout per rendered frame rather than once per raw
        // pointer event. Accumulated deltas preserve pixel-accurate drag.
        double leftMinimum = LibraryListColumn.MinWidth;
        double rightMinimum = LibraryDetailColumn.MinWidth;
        double maximum = Math.Max(leftMinimum, _splitTotal - rightMinimum);
        double left = Math.Clamp(
            _splitStartLeft + _splitDelta, leftMinimum, maximum);
        double right = Math.Max(rightMinimum, _splitTotal - left);

        LibraryListColumn.Width = new GridLength(left);
        LibraryDetailColumn.Width = new GridLength(right);
    }

    private void LibrarySplitter_DragCompleted(
        object sender, DragCompletedEventArgs e)
    {
        if (!_splitDragging)
        {
            return;
        }

        LibrarySplitter_ApplyFrame(this, EventArgs.Empty);
        _splitDragging = false;
        PlayingWaveform.SuspendDuringLibrarySplit = false;

        // Switch back to proportional sizing only at drag end so windows
        // still resize gracefully without the splitter fighting star math.
        double left = Math.Max(1, LibraryListColumn.ActualWidth);
        double right = Math.Max(1, LibraryDetailColumn.ActualWidth);
        LibraryListColumn.Width =
            new GridLength(left, GridUnitType.Star);
        LibraryDetailColumn.Width =
            new GridLength(right, GridUnitType.Star);
    }
}
