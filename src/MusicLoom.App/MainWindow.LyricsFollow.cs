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

    private static bool ScrollLyricIntoViewSmoothly(
        ListView list,
        LyricDisplayLine line)
    {
        ScrollViewer? viewer = FindLyricDescendant<ScrollViewer>(list);
        if (viewer is null)
        {
            return false;
        }

        if (list.ContainerFromItem(line) is not ListViewItem row)
        {
            // Never snap via ScrollIntoView: if a distant item is still
            // virtualized, animate toward its estimated position instead.
            if (viewer.ScrollableHeight < 1
                || list.ItemsSource is not IEnumerable<LyricDisplayLine> source)
            {
                return false;
            }
            LyricDisplayLine[] lines = source.ToArray();
            int index = Array.IndexOf(lines, line);
            if (index < 0)
            {
                return false;
            }
            double approximation = viewer.ScrollableHeight
                * index / Math.Max(1, lines.Length - 1);
            viewer.ChangeView(null, approximation, null,
                disableAnimation: false);
            return true;
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
            return true;
        }
        catch (ArgumentException)
        {
            // Virtualization can recycle the row during a resize.
            return false;
        }
    }

    private void AnimateLyricFont(
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

        bool immersive = ReferenceEquals(list, ImmersiveView.LyricsList);
        if (FindLyricDescendant<TextBlock>(row, "PrimaryLyricText")
            is TextBlock primary)
        {
            double ordinary = immersive ? 22 : 16;
            double active = immersive ? 28 : 20;
            Animate(primary,
                expand ? ordinary : active,
                expand ? active : ordinary);
        }
        if (!immersive
            && FindLyricDescendant<TextBlock>(row, "SecondaryLyricText")
                is TextBlock secondary)
        {
            Animate(secondary, expand ? 13 : 15, expand ? 15 : 13);
        }
        if (immersive
            && FindLyricDescendant<StackPanel>(row) is StackPanel area)
        {
            DoubleAnimation opacity = new()
            {
                From = expand ? 0.48 : 1.0,
                To = expand ? 1.0 : 0.48,
                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
                EasingFunction = new QuadraticEase
                {
                    EasingMode = EasingMode.EaseInOut,
                },
            };
            Storyboard.SetTarget(opacity, area);
            Storyboard.SetTargetProperty(opacity, "Opacity");
            Storyboard storyboard = new();
            storyboard.Children.Add(opacity);
            storyboard.Begin();
        }
    }
}
