using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MusicLoom.App;

/// <summary>
/// Renders a decoded song's actual RMS envelope over the whole list row.
/// There is no looping placeholder sine: the current playback position
/// advances the darker played region over the underlying amplitude curve.
/// </summary>
public sealed partial class PlayingWaveform : UserControl
{
    private readonly RectangleGeometry _playedClip = new();

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

    public static readonly DependencyProperty PlaybackProgressProperty =
        DependencyProperty.Register(
            nameof(PlaybackProgress),
            typeof(double),
            typeof(PlayingWaveform),
            new PropertyMetadata(0d, OnProgressChanged));

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

    public double PlaybackProgress
    {
        get => (double)GetValue(PlaybackProgressProperty);
        set => SetValue(PlaybackProgressProperty, value);
    }

    public PlayingWaveform()
    {
        InitializeComponent();
        PlayedLayer.Clip = _playedClip;
        Loaded += (_, _) => RenderWaveform();
        SizeChanged += (_, _) => RenderWaveform();
    }

    private static void OnInputChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((PlayingWaveform)sender).RenderWaveform();

    private static void OnProgressChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        ((PlayingWaveform)sender).UpdatePlaybackClip();

    private void RenderWaveform()
    {
        float[]? values = Samples;
        bool show = IsActive
            && values is { Length: > 1 };
        Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || values is null
            || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        double width = ActualWidth;
        double height = ActualHeight;
        double baseline = height - 3;
        double amplitudeHeight = Math.Max(1, height - 9);

        double WaveY(int index)
        {
            // A slight three-bin average eliminates codec-window jaggies
            // without inventing dynamics that are absent in the recording.
            float level = values[index] * 0.6f
                + values[Math.Max(0, index - 1)] * 0.2f
                + values[Math.Min(values.Length - 1, index + 1)] * 0.2f;
            return baseline - Math.Clamp(level, 0f, 1f) * amplitudeHeight;
        }

        PathFigure contour = new()
        {
            StartPoint = new Point(0, WaveY(0)),
            IsClosed = false,
            IsFilled = false,
        };
        PathFigure surface = new()
        {
            StartPoint = new Point(0, baseline),
            IsClosed = true,
            IsFilled = true,
        };
        surface.Segments.Add(new LineSegment
        {
            Point = new Point(0, WaveY(0)),
        });
        for (int i = 1; i < values.Length; i++)
        {
            Point point = new(
                width * i / (values.Length - 1),
                WaveY(i));
            contour.Segments.Add(new LineSegment { Point = point });
            surface.Segments.Add(new LineSegment { Point = point });
        }
        surface.Segments.Add(new LineSegment
        {
            Point = new Point(width, baseline),
        });

        PathGeometry line = new();
        line.Figures.Add(contour);
        PathGeometry fill = new();
        fill.Figures.Add(surface);

        UnplayedLine.Data = line;
        PlayedLine.Data = line;
        UnplayedArea.Data = fill;
        PlayedArea.Data = fill;
        UpdatePlaybackClip();
    }

    private void UpdatePlaybackClip()
    {
        double width = Math.Max(0, ActualWidth);
        double height = Math.Max(0, ActualHeight);
        double progress = double.IsFinite(PlaybackProgress)
            ? Math.Clamp(PlaybackProgress, 0, 1)
            : 0;

        _playedClip.Rect = new Rect(
            0, 0, width * progress, height);
    }
}
