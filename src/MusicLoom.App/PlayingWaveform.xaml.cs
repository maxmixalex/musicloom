using Microsoft.UI.Xaml;
using MusicLoom.Audio.Windows;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MusicLoom.App;

/// <summary>
/// Renders a real PCM RMS envelope behind the currently playing row.
/// Coordinates are bounded before reaching native WinUI shapes; a failed
/// visualization cannot take down the audio player or the application.
/// </summary>
public sealed partial class PlayingWaveform : UserControl
{
    private readonly RectangleGeometry _playedClip = new();
    private readonly RectangleGeometry _outerClip = new();
    private bool _renderFailed;

    // Used by the Windows UI launch regression to confirm that native
    // shapes were actually rendered, not merely that the app started.
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
        WaveRoot.Clip = _outerClip;
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
        if (_renderFailed)
        {
            return;
        }

        try
        {
            float[]? values = Samples;
            bool show = IsActive && values is { Length: > 1 };
            Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show || values is null)
            {
                return;
            }

            double width = ActualWidth;
            double height = ActualHeight;
            WaveformCoordinate[] shape = TrackWaveformGeometry.Project(
                values, width, height);
            if (shape.Length < 2)
            {
                return;
            }

            // Distinct PointCollections are intentional. WinUI must not
            // receive the same mutable native object for two shape owners.
            PointCollection unplayedLine = new();
            PointCollection playedLine = new();
            PointCollection unplayedArea = new();
            PointCollection playedArea = new();
            double baseline = Math.Clamp(height - 3, 0, height);
            Point first = new(0, baseline);
            unplayedArea.Add(first);
            playedArea.Add(first);

            foreach (WaveformCoordinate point in shape)
            {
                Point visualPoint = new(point.X, point.Y);
                unplayedLine.Add(visualPoint);
                playedLine.Add(visualPoint);
                unplayedArea.Add(visualPoint);
                playedArea.Add(visualPoint);
            }

            Point last = new(width, baseline);
            unplayedArea.Add(last);
            playedArea.Add(last);

            // Child bounds cannot change Canvas's DesiredSize, so updating
            // the waveform cannot produce another expanding layout cycle.
            PlayedLayer.Width = width;
            PlayedLayer.Height = height;
            UnplayedLine.Points = unplayedLine;
            PlayedLine.Points = playedLine;
            UnplayedArea.Points = unplayedArea;
            PlayedArea.Points = playedArea;
            UpdatePlaybackClip();
            if (!_renderFailed)
            {
                Rendered?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception error)
        {
            DisableWaveform(error);
        }
    }

    private void UpdatePlaybackClip()
    {
        if (_renderFailed)
        {
            return;
        }

        try
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (!double.IsFinite(width)
                || !double.IsFinite(height)
                || width <= 0
                || height <= 0
                || width > 16_384
                || height > 16_384)
            {
                return;
            }

            double progress = double.IsFinite(PlaybackProgress)
                ? Math.Clamp(PlaybackProgress, 0, 1)
                : 0;
            _outerClip.Rect = new Rect(0, 0, width, height);
            _playedClip.Rect = new Rect(
                0, 0, width * progress, height);
        }
        catch (Exception error)
        {
            DisableWaveform(error);
        }
    }

    private void DisableWaveform(Exception error)
    {
        // Optional artwork must never terminate playback. Disable this
        // instance once rather than retrying from every SizeChanged event.
        _renderFailed = true;
        try
        {
            Visibility = Visibility.Collapsed;
            AppDiagnostics.LogException(
                "Waveform visualization disabled after a render error.",
                error);
        }
        catch
        {
            // Diagnostics failures must not turn a visual fallback into a
            // second unhandled exception.
        }
    }
}
