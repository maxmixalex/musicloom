using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using MusicLoom.Core.Lyrics;
using Windows.Foundation;
using Windows.UI;

namespace MusicLoom.App;

/// <summary>
/// Lightweight attached renderer for an ordinary WinUI TextBlock.
/// One Run per actual source word; the current Run fills left-to-right
/// using a four-stop gradient, driven exclusively by the MediaPlayer
/// position. No timer, animation loop or per-frame TextBlock rebuild.
/// </summary>
public static class KaraokeLyricText
{
    public static readonly DependencyProperty LineProperty =
        DependencyProperty.RegisterAttached(
            "Line", typeof(object), typeof(KaraokeLyricText),
            new PropertyMetadata(null, OnLineChanged));

    public static readonly DependencyProperty IsSecondaryProperty =
        DependencyProperty.RegisterAttached(
            "IsSecondary", typeof(bool), typeof(KaraokeLyricText),
            new PropertyMetadata(false, OnRoleChanged));

    private static readonly ConditionalWeakTable<TextBlock, Renderer>
        Renderers = new();

    public static object? GetLine(DependencyObject target) =>
        target.GetValue(LineProperty);

    public static void SetLine(DependencyObject target, object? line) =>
        target.SetValue(LineProperty, line);

    public static bool GetIsSecondary(DependencyObject target) =>
        (bool)target.GetValue(IsSecondaryProperty);

    public static void SetIsSecondary(DependencyObject target, bool value) =>
        target.SetValue(IsSecondaryProperty, value);

    private static void OnLineChanged(
        DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is TextBlock text)
        {
            Renderers.GetValue(text, static t => new Renderer(t))
                .SetLine(e.NewValue as LyricDisplayLine);
        }
    }

    private static void OnRoleChanged(
        DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is TextBlock text)
        {
            Renderers.GetValue(text, static t => new Renderer(t))
                .Rebuild();
        }
    }

    private sealed class Renderer
    {
        private readonly TextBlock _text;
        private LyricDisplayLine? _line;
        private Run[] _runs = [];
        private LrcWord[] _words = [];
        private LinearGradientBrush?[] _partialBrushes = [];
        private GradientStop?[] _filledStops = [];
        private GradientStop?[] _unfilledStops = [];
        // -1 unset/inactive, 0 unplayed, 1 partially played, 2 complete.
        private int[] _states = [];
        private SolidColorBrush? _played;
        private SolidColorBrush? _unplayed;
        private bool _subscribed;

        public Renderer(TextBlock text)
        {
            _text = text;
            text.Loaded += OnLoaded;
            text.Unloaded += OnUnloaded;
            text.ActualThemeChanged += OnActualThemeChanged;
        }

        public void SetLine(LyricDisplayLine? line)
        {
            if (ReferenceEquals(_line, line))
            {
                return;
            }

            Unsubscribe();
            _line = line;
            if (_text.IsLoaded)
            {
                Subscribe();
            }
            Rebuild();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Subscribe();
            Rebuild();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) =>
            Unsubscribe();

        private void OnActualThemeChanged(
            FrameworkElement sender, object args)
        {
            _played = null;
            _unplayed = null;
            // Clear any old-theme inline foreground before rebuilding
            // brushes; inactive lines should always inherit the new
            // ThemeResource instead of retaining a stale highlighted run.
            foreach (Run run in _runs)
            {
                run.Foreground = null;
            }
            Array.Fill(_states, -1);
            Refresh();
        }

        private void Subscribe()
        {
            if (_line is not null && !_subscribed)
            {
                _line.PropertyChanged += OnLinePropertyChanged;
                _subscribed = true;
            }
        }

        private void Unsubscribe()
        {
            if (_line is not null && _subscribed)
            {
                _line.PropertyChanged -= OnLinePropertyChanged;
                _subscribed = false;
            }
        }

        private void OnLinePropertyChanged(
            object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(LyricDisplayLine.IsActive)
                or nameof(LyricDisplayLine.PlaybackPosition))
            {
                Refresh();
            }
        }

        public void Rebuild()
        {
            // WinUI creates an implicit empty Run when Text="" is assigned.
            // Clear that native Run *after* clearing Text; otherwise karaoke
            // rows have a phantom inline ahead of their timed word runs.
            _text.Text = string.Empty;
            _text.Inlines.Clear();
            _runs = [];
            _words = [];
            _states = [];
            _partialBrushes = [];
            _filledStops = [];
            _unfilledStops = [];
            _played = null;
            _unplayed = null;

            if (_line is null)
            {
                return;
            }

            bool secondary = GetIsSecondary(_text);
            string value = secondary
                ? _line.SecondaryText ?? string.Empty : _line.PrimaryText;
            IReadOnlyList<LrcWord>? words = secondary
                ? _line.SecondaryWords : _line.PrimaryWords;

            // Display text may have been split/reordered for a language
            // preference. Never light up the wrong glyph sequence. Keep
            // all ordinary LRC and uncertain rows as native text.
            if (words is not { Count: > 0 and <= 256 }
                || !string.Equals(
                    string.Concat(words.Select(static w => w.Text)),
                    value, StringComparison.Ordinal))
            {
                _text.Text = value;
                return;
            }

            // Only real word Runs belong in the inline collection.
            _words = [.. words];
            _runs = new Run[_words.Length];
            _states = new int[_words.Length];
            _partialBrushes = new LinearGradientBrush?[_words.Length];
            _filledStops = new GradientStop?[_words.Length];
            _unfilledStops = new GradientStop?[_words.Length];
            Array.Fill(_states, -1);

            for (int i = 0; i < _words.Length; i++)
            {
                Run run = new() { Text = _words[i].Text };
                _text.Inlines.Add(run);
                _runs[i] = run;
            }

            Refresh();
        }

        private void EnsureBrushes()
        {
            // Foreground is a WinUI theme resource in the library detail
            // view and white in fullscreen immersive mode.
            Color color = (_text.Foreground as SolidColorBrush)?.Color
                ?? Color.FromArgb(255, 255, 255, 255);
            byte dimAlpha = (byte)Math.Clamp(
                Math.Round(color.A * 0.44), 50, 200);
            Color muted = Color.FromArgb(
                dimAlpha, color.R, color.G, color.B);

            if (_played?.Color == color && _unplayed?.Color == muted)
            {
                return;
            }

            _played = new SolidColorBrush(color);
            _unplayed = new SolidColorBrush(muted);
            for (int i = 0; i < _partialBrushes.Length; i++)
            {
                _partialBrushes[i] = null;
            }
            Array.Fill(_states, -1);
        }

        private void Refresh()
        {
            if (_line is null || _words.Length == 0)
            {
                return;
            }

            if (!_line.IsActive)
            {
                for (int i = 0; i < _runs.Length; i++)
                {
                    if (_states[i] != -1)
                    {
                        // WinUI inherits the row's theme-aware foreground
                        // again as soon as it is no longer the current line.
                        _runs[i].Foreground = null;
                        _states[i] = -1;
                    }
                }
                return;
            }

            EnsureBrushes();
            for (int i = 0; i < _words.Length; i++)
            {
                double progress = LrcKaraokeTiming.Fraction(
                    _words[i], _line.PlaybackPosition);
                int state = progress <= 0 ? 0
                    : progress >= 1 ? 2 : 1;

                if (state == 0)
                {
                    if (_states[i] != 0)
                    {
                        _runs[i].Foreground = _unplayed;
                    }
                }
                else if (state == 2)
                {
                    if (_states[i] != 2)
                    {
                        _runs[i].Foreground = _played;
                    }
                }
                else
                {
                    if (_partialBrushes[i] is null)
                    {
                        // The two close offsets create a subtle moving
                        // edge without a second horizontal overlay or
                        // incorrectly revealing wrapped lines together.
                        GradientStop leading = new()
                        {
                            Color = _played!.Color, Offset = 0,
                        };
                        GradientStop filled = new()
                        {
                            Color = _played.Color, Offset = 0,
                        };
                        GradientStop unfilled = new()
                        {
                            Color = _unplayed!.Color, Offset = 0.001,
                        };
                        GradientStop trailing = new()
                        {
                            Color = _unplayed.Color, Offset = 1,
                        };
                        LinearGradientBrush brush = new()
                        {
                            StartPoint = new Point(0, 0),
                            EndPoint = new Point(1, 0),
                        };
                        brush.GradientStops.Add(leading);
                        brush.GradientStops.Add(filled);
                        brush.GradientStops.Add(unfilled);
                        brush.GradientStops.Add(trailing);
                        _partialBrushes[i] = brush;
                        _filledStops[i] = filled;
                        _unfilledStops[i] = unfilled;
                    }

                    double edge = Math.Clamp(progress, 0, 1);
                    _filledStops[i]!.Offset = Math.Max(0, edge - 0.006);
                    _unfilledStops[i]!.Offset = Math.Min(1, edge + 0.006);
                    if (_states[i] != 1)
                    {
                        _runs[i].Foreground = _partialBrushes[i];
                    }
                }
                _states[i] = state;
            }
        }
    }
}
