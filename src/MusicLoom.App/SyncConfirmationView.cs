using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.Synchronization;
using Windows.UI;

namespace MusicLoom.App;

/// <summary>
/// Presents a read-only review of the scan plan. Buttons remain in an
/// independent footer while potentially long diagnostics scroll above them.
/// </summary>
internal static class SyncConfirmationView
{
    private static readonly Windows.UI.Text.FontWeight SemiBold =
        new() { Weight = 600 };

    internal static (Grid Content, Button Cancel, Button Confirm) Create(
        LibraryPlan plan,
        IReadOnlyList<TrackSyncWarning> warnings,
        IReadOnlyList<PlanEntry> errors,
        Func<string, string, string> localize,
        Func<TrackSyncWarning, string> describeWarning)
    {
        Brush neutral = Resource(
            "CardBackgroundFillColorSecondaryBrush",
            Color.FromArgb(14, 128, 128, 128));
        Brush muted = Resource(
            "TextFillColorSecondaryBrush",
            Color.FromArgb(255, 140, 140, 140));
        Brush stroke = Resource(
            "CardStrokeColorDefaultBrush",
            Color.FromArgb(30, 128, 128, 128));
        Brush warning = new SolidColorBrush(
            Color.FromArgb(255, 188, 123, 24));
        Brush danger = new SolidColorBrush(
            Color.FromArgb(255, 196, 43, 28));

        Grid root = new()
        {
            RowSpacing = 14,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1, GridUnitType.Star),
        });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        TextBlock intro = new()
        {
            Text = localize(
                "Review the pending changes before MusicLoom writes to your destination.",
                "请确认即将写入目标音乐库的更改。"),
            FontSize = 13,
            Foreground = muted,
            TextWrapping = TextWrapping.WrapWholeWords,
        };
        root.Children.Add(intro);

        Border summary = new()
        {
            Padding = new Thickness(8, 14, 8, 14),
            CornerRadius = new CornerRadius(9),
            Background = neutral,
        };
        Grid metrics = new();
        for (int index = 0; index < 4; index++)
        {
            metrics.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
            });
        }
        (int Count, string Label)[] values =
        [
            (plan.PendingTrackCount, localize("Pending", "待同步")),
            (plan.AudioCopyCount, localize("Audio copies", "音频复制")),
            (plan.CoverWriteCount, localize("Covers", "封面")),
            (plan.PlaylistWriteCount, localize("Playlists", "播放列表")),
        ];
        for (int index = 0; index < values.Length; index++)
        {
            StackPanel metric = new()
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 3,
            };
            metric.Children.Add(new TextBlock
            {
                Text = values[index].Count.ToString("N0"),
                FontSize = 21,
                FontWeight = SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            metric.Children.Add(new TextBlock
            {
                Text = values[index].Label,
                FontSize = 12,
                Foreground = muted,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            Grid.SetColumn(metric, index);
            metrics.Children.Add(metric);

            if (index > 0)
            {
                Border divider = new()
                {
                    Width = 1,
                    Margin = new Thickness(-1, 5, 0, 5),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = stroke,
                    IsHitTestVisible = false,
                };
                Grid.SetColumn(divider, index);
                metrics.Children.Add(divider);
            }
        }
        summary.Child = metrics;
        Grid.SetRow(summary, 1);
        root.Children.Add(summary);

        StackPanel diagnostics = new()
        {
            Spacing = 14,
        };
        if (warnings.Count == 0 && errors.Count == 0)
        {
            diagnostics.Children.Add(new TextBlock
            {
                Text = localize(
                    "No known warnings or scan errors.",
                    "没有已知警告或扫描错误。"),
                FontSize = 12,
                Foreground = muted,
                Margin = new Thickness(0, 2, 0, 2),
            });
        }

        if (warnings.Count > 0)
        {
            diagnostics.Children.Add(
                CreateSection(
                    localize("Warnings", "警告"),
                    warnings.Count,
                    localize(
                        "These tracks are awaiting manual match review and will be skipped.",
                        "以下歌曲需要人工确认匹配，本次会跳过。"),
                    "\uE7BA",
                    warning,
                    muted,
                    stroke,
                    warnings.Select(w => (
                        System.IO.Path.GetFileName(w.SourcePath),
                        describeWarning(w)))));
        }

        if (errors.Count > 0)
        {
            diagnostics.Children.Add(
                CreateSection(
                    localize("Scan errors", "扫描错误"),
                    errors.Count,
                    localize(
                        "Resolve these errors before running synchronization.",
                        "需先解决以下错误，才能开始同步。"),
                    "\uEA39",
                    danger,
                    muted,
                    stroke,
                    errors.Select(e => (
                        System.IO.Path.GetFileName(e.SourcePath),
                        e.Detail ?? localize("Unknown error", "未知错误")))));
        }

        ScrollViewer issueScroll = new()
        {
            Content = diagnostics,
            MaxHeight = 280,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(issueScroll, 2);
        root.Children.Add(issueScroll);

        Button cancel = new()
        {
            Content = localize("Cancel", "取消"),
            MinWidth = 88,
        };
        Button confirm = new()
        {
            Content = localize("Start sync", "开始同步"),
            MinWidth = 104,
            IsEnabled = errors.Count == 0,
        };
        if (Application.Current.Resources.TryGetValue(
                "AccentButtonStyle", out object? style)
            && style is Style accentStyle)
        {
            confirm.Style = accentStyle;
        }

        StackPanel actions = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);

        Grid footer = new()
        {
            ColumnSpacing = 12,
            Padding = new Thickness(0, 13, 0, 0),
        };
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        footer.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
        });
        footer.Children.Add(new TextBlock
        {
            Text = errors.Count == 0
                ? localize("Destination files are unchanged until you start.", "点击开始前不会修改目标音乐库。")
                : localize("Synchronization is blocked by scan errors.", "存在扫描错误，无法开始同步。"),
            FontSize = 12,
            Foreground = muted,
            TextWrapping = TextWrapping.WrapWholeWords,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        return (root, cancel, confirm);
    }

    private static StackPanel CreateSection(
        string heading,
        int count,
        string description,
        string glyph,
        Brush severity,
        Brush secondary,
        Brush stroke,
        IEnumerable<(string Title, string Detail)> issues)
    {
        StackPanel section = new()
        {
            Spacing = 9,
        };
        Grid titleRow = new()
        {
            ColumnSpacing = 8,
        };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
        });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        titleRow.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            Foreground = severity,
            VerticalAlignment = VerticalAlignment.Center,
        });
        StackPanel headingText = new() { Spacing = 2 };
        headingText.Children.Add(new TextBlock
        {
            Text = $"{heading}  ·  {count:N0}",
            FontSize = 14,
            FontWeight = SemiBold,
        });
        headingText.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = secondary,
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        Grid.SetColumn(headingText, 1);
        titleRow.Children.Add(headingText);
        section.Children.Add(titleRow);

        Border listPanel = new()
        {
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = stroke,
        };
        StackPanel rows = new() { Spacing = 0 };
        bool first = true;
        foreach ((string title, string detail) in issues)
        {
            if (!first)
            {
                rows.Children.Add(new Border
                {
                    Height = 1,
                    Background = stroke,
                    IsHitTestVisible = false,
                });
            }
            first = false;

            StackPanel item = new()
            {
                Spacing = 4,
                Padding = new Thickness(0, 10, 0, 10),
            };
            item.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13,
                FontWeight = SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            item.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                Foreground = secondary,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
            rows.Children.Add(item);
        }
        listPanel.Child = rows;
        section.Children.Add(listPanel);
        return section;
    }

    private static Brush Resource(string key, Color fallback)
    {
        return Application.Current.Resources.TryGetValue(key, out object? result)
            && result is Brush brush
            ? brush
            : new SolidColorBrush(fallback);
    }
}
