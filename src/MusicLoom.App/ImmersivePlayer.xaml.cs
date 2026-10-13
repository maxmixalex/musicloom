using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MusicLoom.Core.Configuration;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;

namespace MusicLoom.App;

/// <summary>
/// Fullscreen rendering is a view over MainWindow's existing MediaPlayer,
/// never a second audio session, queue or lyric source.
/// </summary>
public sealed partial class ImmersivePlayer : UserControl
{
    private bool _initialized;
    private bool _updatingProgress;
    private bool _updatingMode;
    private int _artworkRequest;

    public event EventHandler? CloseRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PlayPauseRequested;
    public event Action<double>? SeekRequested;
    public event Action<double>? LyricSeekRequested;
    public event Action<string>? ModeChanged;

    internal ListView LyricsList => ImmersiveLyricsList;

    public ImmersivePlayer()
    {
        InitializeComponent();
        LyricModeBox.SelectedIndex = 2;
        _initialized = true;
    }

    internal void Localize(Func<string, string, string> translate)
    {
        OriginalOnlyChoice.Content = translate("Original only", "仅原文");
        PreferTranslationChoice.Content = translate("Prefer translation", "优先翻译");
        PreferOriginalChoice.Content = translate("Prefer original", "优先原文");
        ToolTipService.SetToolTip(ExitButton,
            translate("Exit fullscreen", "退出全屏"));
    }

    internal void SetMode(string displayMode)
    {
        _updatingMode = true;
        try
        {
            LyricModeBox.SelectedIndex =
                LyricsDisplayModes.Normalize(displayMode) switch
                {
                    LyricsDisplayModes.OriginalOnly => 0,
                    LyricsDisplayModes.PreferTranslation => 1,
                    _ => 2,
                };
        }
        finally
        {
            _updatingMode = false;
        }
    }

    internal void SetTrack(
        string title,
        string album,
        ImageSource? cover,
        IEnumerable<LyricDisplayLine> lines)
    {
        TrackHeading.Text = title;
        AlbumHeading.Text = album;
        AlbumCover.Source = cover;
        LyricDisplayLine[] display = lines.ToArray();
        ImmersiveLyricsList.ItemsSource = display;
        LyricModeBox.Visibility = display.Any(
            static line => !string.IsNullOrWhiteSpace(line.Translation))
            ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void SetProgress(
        double currentSeconds,
        double durationSeconds,
        string formattedTime)
    {
        _updatingProgress = true;
        try
        {
            ProgressSlider.Maximum =
                durationSeconds > 0 ? durationSeconds : 1;
            ProgressSlider.Value = durationSeconds > 0
                ? Math.Clamp(currentSeconds, 0, durationSeconds) : 0;
            PlaybackTime.Text = formattedTime;
        }
        finally
        {
            _updatingProgress = false;
        }
    }

    internal void SetPlaying(bool playing) =>
        PlayPauseIcon.Symbol = playing ? Symbol.Pause : Symbol.Play;

    internal async Task ApplyArtworkColorsAsync(byte[]? artwork)
    {
        int request = ++_artworkRequest;
        if (artwork is null || artwork.Length == 0)
        {
            SetPalette(Color.FromArgb(255, 51, 103, 138));
            return;
        }

        try
        {
            using InMemoryRandomAccessStream stream = new();
            using (DataWriter writer = new(stream))
            {
                writer.WriteBytes(artwork);
                await writer.StoreAsync();
            }
            stream.Seek(0);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            PixelDataProvider pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform
                {
                    ScaledWidth = 24,
                    ScaledHeight = 24,
                },
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            byte[] data = pixels.DetachPixelData();

            double red = 0, green = 0, blue = 0, total = 0;
            for (int i = 0; i + 3 < data.Length; i += 4)
            {
                double r = data[i + 2] / 255.0;
                double g = data[i + 1] / 255.0;
                double b = data[i] / 255.0;
                double max = Math.Max(r, Math.Max(g, b));
                double min = Math.Min(r, Math.Min(g, b));
                double chroma = max - min;
                double weight = data[i + 3] / 255.0
                    * (0.2 + chroma * 2)
                    * (max > 0.15 ? 1 : 0.25);
                red += r * weight;
                green += g * weight;
                blue += b * weight;
                total += weight;
            }

            if (request != _artworkRequest)
            {
                return;
            }

            if (total > 0.01)
            {
                SetPalette(Color.FromArgb(
                    255,
                    (byte)Math.Clamp((int)Math.Round(red / total * 255), 0, 255),
                    (byte)Math.Clamp((int)Math.Round(green / total * 255), 0, 255),
                    (byte)Math.Clamp((int)Math.Round(blue / total * 255), 0, 255)));
            }
        }
        catch (Exception)
        {
            // Album decoding is optional decoration; never stop playback.
            SetPalette(Color.FromArgb(255, 51, 103, 138));
        }
    }

    private void LeftPanel_SizeChanged(
        object sender, SizeChangedEventArgs args)
    {
        // Both columns share the exact visual vertical span at every
        // window size, rather than anchoring lyrics higher than the cover.
        if (args.NewSize.Height > 0
            && Math.Abs(RightPanel.Height - args.NewSize.Height) > 0.5)
        {
            RightPanel.Height = args.NewSize.Height;
        }
    }

    private void ImmersiveLyricsList_DoubleTapped(
        object sender, DoubleTappedRoutedEventArgs args)
    {
        LyricDisplayLine? line =
            (args.OriginalSource as FrameworkElement)
                ?.DataContext as LyricDisplayLine;
        if (line?.Timestamp is TimeSpan position)
        {
            args.Handled = true;
            LyricSeekRequested?.Invoke(position.TotalSeconds);
        }
    }

    private void SetPalette(Color color)
    {
        Color Shade(double factor, double lift = 0) =>
            Color.FromArgb(255,
                (byte)Math.Clamp((int)(color.R * factor + lift), 0, 255),
                (byte)Math.Clamp((int)(color.G * factor + lift), 0, 255),
                (byte)Math.Clamp((int)(color.B * factor + lift), 0, 255));

        ArtworkGradient.GradientStops[0].Color = Shade(0.68, 20);
        ArtworkGradient.GradientStops[1].Color = Shade(0.48, 13);
        ArtworkGradient.GradientStops[2].Color = Shade(0.27, 9);
        Color fadeBase = Shade(0.34, 12);
        LyricsFadeGradient.GradientStops[0].Color =
            Color.FromArgb(0, fadeBase.R, fadeBase.G, fadeBase.B);
        LyricsFadeGradient.GradientStops[1].Color = fadeBase;
    }

    private void ProgressSlider_ValueChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initialized && !_updatingProgress)
        {
            SeekRequested?.Invoke(e.NewValue);
        }
    }

    private void LyricModeBox_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && !_updatingMode &&
            LyricModeBox.SelectedItem is ComboBoxItem choice
            && choice.Tag is string mode)
        {
            ModeChanged?.Invoke(mode);
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void PreviousButton_Click(object sender, RoutedEventArgs e) =>
        PreviousRequested?.Invoke(this, EventArgs.Empty);

    private void NextButton_Click(object sender, RoutedEventArgs e) =>
        NextRequested?.Invoke(this, EventArgs.Empty);

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) =>
        PlayPauseRequested?.Invoke(this, EventArgs.Empty);

    private void ImmersivePlayer_KeyDown(
        object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
