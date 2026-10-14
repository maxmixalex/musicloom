using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.UI;

namespace MusicLoom.App;

/// <summary>
/// A restrained, music-reactive frequency profile across the entire row.
/// Input is a measured log-frequency spectrum from current song audio.
/// Native shape failures disable this optional visual, never the player.
/// </summary>
public sealed partial class PlayingWaveform : UserControl
{
    private readonly RectangleGeometry _outerClip = new();
    private bool _renderFailed;
    private bool _rendering;
    private bool _renderQueued;
    private bool _isVisible;
    private Storyboard? _opacityStoryboard;

    public event EventHandler? Rendered;

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(PlayingWaveform),
            new PropertyMetadata(false, OnInputChanged));

    public static readonly DependencyProperty SamplesProperty =
        DependencyProperty.Register(
            nameof(Samples),
            typeof(float[]),
            typeof(PlayingWaveform),
            new PropertyMetadata(null, OnInputChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public float[]? Samples
    {
        get => (float[]?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    public PlayingWaveform()
    {
        InitializeComponent();
        WaveRoot.Clip = _outerClip;
        Loaded += (_, _) =>
        {
            RefreshWaveContrast();
            RenderWaveform();
        };
        ActualThemeChanged += (_, _) => RefreshWaveContrast();
        // Coalesce live GridSplitter resize events into a single low-priority
        // render rather than rebuilding geometry for every pointer pixel.
        SizeChanged += (_, _) => QueueResizeRender();
    }

    private void RefreshWaveContrast()
    {
        // Preserve the chosen accent hue and wave dynamics; make the line
        // and surface legible against both Fluent theme backgrounds.
        Color accent = Application.Current.Resources.TryGetValue(
            "SystemAccentColor", out object? resource) && resource is Color value
                ? value
                : Color.FromArgb(255, 70, 125, 184);
        bool dark = ActualTheme == ElementTheme.Dark;
        double towardWhite = dark ? 0.49 : 0;
        double towardBlack = dark ? 0 : 0.22;
        byte Channel(byte part) => (byte)Math.Clamp(
            (int)Math.Round(
                (part + (255 - part) * towardWhite) * (1 - towardBlack)),
            0, 255);
        SolidColorBrush brush = new(Color.FromArgb(255,
            Channel(accent.R), Channel(accent.G), Channel(accent.B)));
        WaveLine.Stroke = brush;
        WaveArea.Fill = brush;
        WaveLine.Opacity = dark ? 0.92 : 0.74;
        WaveArea.Opacity = dark ? 0.24 : 0.14;
    }

    private void QueueResizeRender()
    {
        if (_renderQueued || _renderFailed)
        {
            return;
        }
        _renderQueued = true;
        if (!DispatcherQueue.TryEnqueue(
                DispatcherQueuePriority.Low,
                () =>
                {
                    _renderQueued = false;
                    RenderWaveform();
                }))
        {
            _renderQueued = false;
        }
    }

    private void SetVisibleWithFade(bool show)
    {
        if (_isVisible == show)
        {
            return;
        }

        _isVisible = show;
        _opacityStoryboard?.Stop();
        if (show)
        {
            Visibility = Visibility.Visible;
        }

        DoubleAnimation transition = new()
        {
            From = show ? 0 : 1,
            To = show ? 1 : 0,
            Duration = new Duration(
                TimeSpan.FromMilliseconds(show ? 280 : 240)),
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseInOut,
            },
        };
        WaveRoot.Opacity = show ? 0 : 1;
        Storyboard.SetTarget(transition, WaveRoot);
        Storyboard.SetTargetProperty(transition, "Opacity");

        Storyboard storyboard = new();
        storyboard.Children.Add(transition);
        storyboard.Completed += (_, _) =>
        {
            WaveRoot.Opacity = show ? 1 : 0;
            if (!show && !_isVisible)
            {
                Visibility = Visibility.Collapsed;
            }
        };
        _opacityStoryboard = storyboard;
        storyboard.Begin();
    }

    private static void OnInputChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((PlayingWaveform)sender).RenderWaveform();

    private void RenderWaveform()
    {
        if (_renderFailed || _rendering)
        {
            return;
        }

        _rendering = true;
        try
        {
            float[]? levels = Samples;
            bool show = IsActive && levels is { Length: > 2 };
            SetVisibleWithFade(show);
            if (!show || levels is null)
            {
                return;
            }

            double width = ActualWidth;
            double height = ActualHeight;
            if (!double.IsFinite(width)
                || !double.IsFinite(height)
                || width < 2 || height < 2
                || width > 16_384 || height > 16_384)
            {
                return;
            }

            _outerClip.Rect = new Rect(0, 0, width, height);

            // Catmull-Rom interpolation between measured adjacent frequency
            // bands yields a flowing profile. No synthetic time oscillator.
            const int Subdivisions = 8;
            PointCollection contour = new();
            PointCollection surface = new();
            double baseline = height * 0.82;
            surface.Add(new Point(0, height));

            for (int i = 0; i < levels.Length - 1; i++)
            {
                for (int step = 0; step < Subdivisions; step++)
                {
                    double t = (double)step / Subdivisions;
                    double value = SmoothBand(levels, i, t);
                    double x = width *
                        (i + t) / (levels.Length - 1);
                    // Expand quieter passages perceptually, preserving genuine
                    // spectrum peaks and avoiding a fabricated time oscillator.
                    double response = Math.Pow(value, 0.58);
                    double y = baseline - response * (height * 0.74);
                    y = Math.Clamp(y, 0, height);
                    Point point = new(x, y);
                    contour.Add(point);
                    surface.Add(point);
                }
            }

            double finalValue = SafeLevel(levels[^1]);
            Point end = new(
                width,
                Math.Clamp(baseline - Math.Pow(finalValue, 0.58) * height * 0.74, 0, height));
            contour.Add(end);
            surface.Add(end);
            surface.Add(new Point(width, height));

            // Separate collections and non-measuring Canvas primitives
            // cannot alter a ListViewItem's desired size on every frame.
            WaveLine.Points = contour;
            WaveArea.Points = surface;
            Rendered?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            DisableWaveform(exception);
        }
        finally
        {
            _rendering = false;
        }
    }

    private static double SmoothBand(float[] values, int i, double t)
    {
        double p0 = SafeLevel(values[Math.Max(0, i - 1)]);
        double p1 = SafeLevel(values[i]);
        double p2 = SafeLevel(values[i + 1]);
        double p3 = SafeLevel(values[Math.Min(values.Length - 1, i + 2)]);

        // Standard Catmull-Rom spline, clamped against overshoot at peaks.
        double t2 = t * t;
        double t3 = t2 * t;
        double spline = 0.5 * (
            2 * p1 +
            (-p0 + p2) * t +
            (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 +
            (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
        return Math.Clamp(spline, 0, 1);
    }

    private static double SafeLevel(float value) =>
        float.IsFinite(value)
            ? Math.Clamp((double)value, 0, 1)
            : 0;

    private void DisableWaveform(Exception error)
    {
        _renderFailed = true;
        try
        {
            Visibility = Visibility.Collapsed;
            AppDiagnostics.LogException(
                "Reactive waveform disabled after a render error.", error);
        }
        catch
        {
            // A visualization is never allowed to terminate playback.
        }
    }
}
