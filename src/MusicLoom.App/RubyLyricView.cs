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
    private sealed class GlyphState(LrcWord word, TextBlock glyph)
    {
        internal LrcWord Word { get; } = word;
        internal TextBlock Glyph { get; } = glyph;
        internal int Stage = -1;
        internal LinearGradientBrush? Partial;
        internal GradientStop? Filled;
        internal GradientStop? Unfilled;
    }

    private readonly List<GlyphState> _glyphs = [];
    private StackPanel? _wholeLineReading;
    private SolidColorBrush? _playedBrush;
    private SolidColorBrush? _unplayedBrush;
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
        _rich.SizeChanged += (_, _) => ResizeWholeLineReading();
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
        _wholeLineReading = null;
        _playedBrush = null;
        _unplayedBrush = null;
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
        string? wholeReading = IsSecondary
            ? _line.SecondaryRomanization : _line.PrimaryRomanization;
        if (!string.IsNullOrWhiteSpace(wholeReading))
        {
            // Two independent wrapped text blocks share the same available
            // viewport width. A full-line romaji annotation must NEVER be
            // an InlineUIContainer with an unconstrained intrinsic width:
            // that makes Japanese originals jump to the next line.
            StackPanel stack = new()
            {
                Orientation = Orientation.Vertical,
                Spacing = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            TextBlock reading = new()
            {
                Text = wholeReading,
                FontSize = Math.Max(7, baseSize * 0.50),
                Opacity = 0.76,
                TextWrapping = TextWrapping.WrapWholeWords,
                Foreground = Foreground,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            TextBlock glyph = new()
            {
                Text = plain,
                FontSize = baseSize,
                FontWeight = FontWeight,
                TextWrapping = TextWrapping.WrapWholeWords,
                Foreground = Foreground,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            stack.Children.Add(reading);
            stack.Children.Add(glyph);
            _wholeLineReading = stack;
            Content = stack;
            return;
        }
        // Word-timed ruby uses separate inline atoms for real karaoke.
        // Restore the original RichTextBlock when virtualization reuses a
        // container that previously displayed only line-level readings.
        if (!ReferenceEquals(Content, _rich))
            Content = _rich;
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
            // The inline advance must be determined ONLY by the Japanese
            // glyph. A StackPanel makes its width the MAX of ruby and base;
            // a long reading therefore steals horizontal layout space and
            // forces the original Japanese text to wrap unexpectedly.
            // Canvas lets the ruby overhang its base without contributing to
            // RichTextBlock's measured inline width.
            double rubyHeight = Math.Max(10, baseSize * 0.72);
            TextBlock reading = new()
            {
                Text = string.IsNullOrWhiteSpace(word.Ruby) ? " " : word.Ruby,
                FontSize = Math.Max(7, baseSize * 0.46),
                Height = rubyHeight,
                Opacity = string.IsNullOrWhiteSpace(word.Ruby) ? 0 : 0.78,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Center,
                Foreground = Foreground,
            };
            TextBlock glyph = new()
            {
                Text = word.Text,
                FontSize = baseSize,
                FontWeight = FontWeight,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Center,
                Foreground = Foreground,
            };
            var unconstrained = new Windows.Foundation.Size(
                double.PositiveInfinity, double.PositiveInfinity);
            reading.Measure(unconstrained);
            glyph.Measure(unconstrained);
            double glyphWidth = Math.Max(1, glyph.DesiredSize.Width);
            double glyphHeight = Math.Max(1, glyph.DesiredSize.Height);
            Canvas atom = new()
            {
                Width = glyphWidth,
                Height = rubyHeight + glyphHeight,
            };
            Canvas.SetLeft(reading, (glyphWidth - reading.DesiredSize.Width) / 2);
            Canvas.SetTop(reading, 0);
            Canvas.SetLeft(glyph, 0);
            Canvas.SetTop(glyph, rubyHeight);
            atom.Children.Add(reading);
            atom.Children.Add(glyph);
            paragraph.Inlines.Add(new InlineUIContainer { Child = atom });
            _glyphs.Add(new GlyphState(word, glyph));
        }
        _rich.Blocks.Add(paragraph);
        RefreshColors();
    }

    private void ResizeWholeLineReading()
    {
        if (_wholeLineReading is not null && _rich.ActualWidth > 100)
            _wholeLineReading.MaxWidth =
                Math.Max(120, _rich.ActualWidth - 4);
    }

    private void RefreshColors()
    {
        if (_line is null || _glyphs.Count == 0) return;
        Color active = (Foreground as SolidColorBrush)?.Color
            ?? Color.FromArgb(255, 255, 255, 255);
        Color muted = Color.FromArgb(
            (byte)Math.Clamp(Math.Round(active.A * 0.44), 50, 200),
            active.R, active.G, active.B);

        // Do not allocate 2 brushes per word per frame. Only the currently
        // partially-filled syllable updates two gradient-stop offsets.
        if (_playedBrush?.Color != active || _unplayedBrush?.Color != muted)
        {
            _playedBrush = new SolidColorBrush(active);
            _unplayedBrush = new SolidColorBrush(muted);
            foreach (GlyphState item in _glyphs)
            {
                item.Stage = -1;
                item.Partial = null;
                item.Filled = null;
                item.Unfilled = null;
            }
        }

        foreach (GlyphState item in _glyphs)
        {
            if (!_line.IsActive)
            {
                if (item.Stage != -1
                    || !ReferenceEquals(item.Glyph.Foreground, Foreground))
                {
                    item.Glyph.Foreground = Foreground;
                    item.Stage = -1;
                }
                continue;
            }

            double progress = LrcKaraokeTiming.Fraction(
                item.Word, _line.PlaybackPosition);
            int stage = progress <= 0 ? 0 : progress >= 1 ? 2 : 1;
            if (stage == 0)
            {
                if (item.Stage != 0)
                    item.Glyph.Foreground = _unplayedBrush;
            }
            else if (stage == 2)
            {
                if (item.Stage != 2)
                    item.Glyph.Foreground = _playedBrush;
            }
            else
            {
                if (item.Partial is null)
                {
                    LinearGradientBrush brush = new()
                    {
                        StartPoint = new Windows.Foundation.Point(0, 0),
                        EndPoint = new Windows.Foundation.Point(1, 0),
                    };
                    brush.GradientStops.Add(new GradientStop
                        { Color = active, Offset = 0 });
                    item.Filled = new GradientStop
                        { Color = active, Offset = 0 };
                    item.Unfilled = new GradientStop
                        { Color = muted, Offset = 0 };
                    brush.GradientStops.Add(item.Filled);
                    brush.GradientStops.Add(item.Unfilled);
                    brush.GradientStops.Add(new GradientStop
                        { Color = muted, Offset = 1 });
                    item.Partial = brush;
                }
                item.Filled!.Offset = Math.Max(0, progress - 0.006);
                item.Unfilled!.Offset = Math.Min(1, progress + 0.006);
                if (item.Stage != 1)
                    item.Glyph.Foreground = item.Partial;
            }
            item.Stage = stage;
        }
    }
}
