using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using MusicLoom.Core.Lyrics;
using Windows.UI;

namespace MusicLoom.App;

/// <summary>
/// Ruby-aware lyric rendering. Every timed original word is an inline layout
/// atom with a small reading above its base text. RichTextBlock's native
/// inline layout wraps whole atoms without clipping or dropping the next
/// lyric row; no WebView, HTML parsing or nested scrolling is involved.
/// Ordinary lyrics keep the existing lightweight KaraokeLyricText renderer.
/// </summary>
public sealed class RubyLyricView : UserControl
{
    public static readonly DependencyProperty LineProperty =
        DependencyProperty.Register(nameof(Line), typeof(object),
            typeof(RubyLyricView), new PropertyMetadata(null, OnLineChanged));
    public static readonly DependencyProperty IsSecondaryProperty =
        DependencyProperty.Register(nameof(IsSecondary), typeof(bool),
            typeof(RubyLyricView), new PropertyMetadata(false, OnRoleChanged));

    private readonly RichTextBlock _rich = new()
    {
        TextWrapping = TextWrapping.WrapWholeWords,
        IsTextSelectionEnabled = false,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly List<(LrcWord Word, TextBlock Glyph)> _glyphs = [];
    private LyricDisplayLine? _line;
    private bool _subscribed;

    public object? Line
    {
        get => GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }
    public bool IsSecondary
    {
        get => (bool)GetValue(IsSecondaryProperty);
        set => SetValue(IsSecondaryProperty, value);
    }

    public RubyLyricView()
    {
        Content = _rich;
        Loaded += (_, _) =>
        {
            Subscribe();
            Rebuild();
        };
        Unloaded += (_, _) => Unsubscribe();
        ActualThemeChanged += (_, _) => Rebuild();
        RegisterPropertyChangedCallback(Control.FontSizeProperty,
            (_, _) => Rebuild());
        RegisterPropertyChangedCallback(Control.ForegroundProperty,
            (_, _) => RefreshColors());
    }

    private static void OnLineChanged(
        DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not RubyLyricView view) return;
        view.Unsubscribe();
        view._line = args.NewValue as LyricDisplayLine;
        if (view.IsLoaded) view.Subscribe();
        view.Rebuild();
    }

    private static void OnRoleChanged(
        DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is RubyLyricView view) view.Rebuild();
    }

    private void Subscribe()
    {
        if (!_subscribed && _line is not null)
        {
            _line.PropertyChanged += OnLyricChanged;
            _subscribed = true;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed && _line is not null)
        {
            _line.PropertyChanged -= OnLyricChanged;
            _subscribed = false;
        }
    }

    private void OnLyricChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LyricDisplayLine.IsActive)
            or nameof(LyricDisplayLine.PlaybackPosition))
            RefreshColors();
    }

    private void Rebuild()
    {
        _rich.Blocks.Clear();
        _glyphs.Clear();
        if (_line is null) return;

        IReadOnlyList<LrcWord>? words =
            IsSecondary ? _line.SecondaryWords : _line.PrimaryWords;
        string plain = IsSecondary
            ? _line.SecondaryText ?? "" : _line.PrimaryText;
        double baseSize = Math.Max(10, FontSize);
        _rich.FontSize = baseSize;
        _rich.Foreground = Foreground;
        _rich.FontWeight = FontWeight;
        Paragraph paragraph = new();
        if (words is not { Count: > 0 and <= 256 }
            || !words.Any(static word => !string.IsNullOrWhiteSpace(word.Ruby))
            || !string.Equals(string.Concat(words.Select(static w => w.Text)),
                plain, StringComparison.Ordinal))
        {
            paragraph.Inlines.Add(new Run { Text = plain });
            _rich.Blocks.Add(paragraph);
            return;
        }

        foreach (LrcWord word in words)
        {
            // Both annotated and unannotated words reserve the same ruby
            // ascent, so mixed words cannot jump vertically on wrap.
            StackPanel atom = new()
            {
                Spacing = 0,
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            TextBlock reading = new()
            {
                Text = string.IsNullOrWhiteSpace(word.Ruby) ? " " : word.Ruby,
                FontSize = Math.Max(7, baseSize * 0.46),
                Height = baseSize * 0.55,
                Opacity = string.IsNullOrWhiteSpace(word.Ruby) ? 0 : 0.78,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Foreground,
            };
            TextBlock glyph = new()
            {
                Text = word.Text,
                FontSize = baseSize,
                FontWeight = FontWeight,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Foreground,
            };
            atom.Children.Add(reading);
            atom.Children.Add(glyph);
            paragraph.Inlines.Add(new InlineUIContainer { Child = atom });
            _glyphs.Add((word, glyph));
        }
        _rich.Blocks.Add(paragraph);
        RefreshColors();
    }

    private void RefreshColors()
    {
        if (_line is null || _glyphs.Count == 0) return;
        Color active = (Foreground as SolidColorBrush)?.Color
            ?? Color.FromArgb(255, 255, 255, 255);
        Color muted = Color.FromArgb(
            (byte)Math.Clamp(Math.Round(active.A * 0.44), 50, 200),
            active.R, active.G, active.B);
        SolidColorBrush played = new(active);
        SolidColorBrush unplayed = new(muted);

        foreach ((LrcWord word, TextBlock glyph) in _glyphs)
        {
            if (!_line.IsActive)
            {
                glyph.Foreground = Foreground;
                continue;
            }
            double progress = LrcKaraokeTiming.Fraction(
                word, _line.PlaybackPosition);
            if (progress <= 0) glyph.Foreground = unplayed;
            else if (progress >= 1) glyph.Foreground = played;
            else
            {
                LinearGradientBrush gradient = new()
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(1, 0),
                };
                gradient.GradientStops.Add(new GradientStop
                    { Color = active, Offset = 0 });
                gradient.GradientStops.Add(new GradientStop
                    { Color = active, Offset = Math.Max(0, progress - 0.006) });
                gradient.GradientStops.Add(new GradientStop
                    { Color = muted, Offset = Math.Min(1, progress + 0.006) });
                gradient.GradientStops.Add(new GradientStop
                    { Color = muted, Offset = 1 });
                glyph.Foreground = gradient;
            }
        }
    }
}
