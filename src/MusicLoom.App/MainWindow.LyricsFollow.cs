using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace MusicLoom.App;

public sealed partial class MainWindow
{
    private void InstallLyricScrollSuppression(ListView list)
    {
        list.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((_, _) => SuspendLyricAutoFollow()),
            handledEventsToo: true);
        list.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => SuspendLyricAutoFollow()),
            handledEventsToo: true);
        list.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler((_, _) => SuspendLyricAutoFollow()),
            handledEventsToo: true);
    }

    private void SuspendLyricAutoFollow()
    {
        _lyricFollowBlockedUntil =
            DateTimeOffset.UtcNow.AddSeconds(6);
        _lastAutoScrolledLyric = null;
    }

    private static T? FindLyricDescendant<T>(
        DependencyObject root,
        string? name = null) where T : FrameworkElement
    {
        if (root is T candidate &&
            (name is null || candidate.Name == name))
        {
            return candidate;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindLyricDescendant<T>(
                    VisualTreeHelper.GetChild(root, i), name)
                is T found)
            {
                return found;
            }
        }
        return null;
    }

    private static void ScrollLyricIntoViewSmoothly(
        ListView list,
        LyricDisplayLine line)
    {
        ScrollViewer? viewer = FindLyricDescendant<ScrollViewer>(list);
        if (viewer is null)
        {
            return;
        }

        if (list.ContainerFromItem(line) is not ListViewItem row)
        {
            // A distant virtualized line must be realized first.
            list.ScrollIntoView(line);
            return;
        }

        try
        {
            double top = row.TransformToVisual(viewer)
                .TransformPoint(new Point(0, 0)).Y;
            double desired = viewer.VerticalOffset + top
                - viewer.ViewportHeight * 0.38;
            viewer.ChangeView(
                null,
                Math.Clamp(desired, 0, Math.Max(0, viewer.ScrollableHeight)),
                null,
                disableAnimation: false);
        }
        catch (ArgumentException)
        {
            // Virtualization can recycle the row during a resize.
        }
    }

    private static void AnimateLyricFont(
        ListView list,
        LyricDisplayLine line,
        bool expand)
    {
        if (list.ContainerFromItem(line) is not ListViewItem row)
        {
            return;
        }

        static void Animate(
            TextBlock label, double before, double after)
        {
            DoubleAnimation animation = new()
            {
                From = before,
                To = after,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EnableDependentAnimation = true,
                EasingFunction = new QuadraticEase
                {
                    EasingMode = EasingMode.EaseInOut,
                },
            };
            Storyboard.SetTarget(animation, label);
            Storyboard.SetTargetProperty(animation, "FontSize");
            Storyboard storyboard = new();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        if (FindLyricDescendant<TextBlock>(row, "PrimaryLyricText")
            is TextBlock primary)
        {
            Animate(primary, expand ? 16 : 20, expand ? 20 : 16);
        }
        if (FindLyricDescendant<TextBlock>(row, "SecondaryLyricText")
            is TextBlock secondary)
        {
            Animate(secondary, expand ? 13 : 15, expand ? 15 : 13);
        }
    }
}
