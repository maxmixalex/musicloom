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
        ExitButtonLabel.Text = translate("Exit fullscreen", "退出全屏");
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
        ImmersiveLyricsList.ItemsSource = lines.ToArray();
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
            SetPalette(Color.FromArgb(255, 64, 108, 148),
                       Color.FromArgb(255, 50, 82, 111));
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
                new BitmapTransform { ScaledWidth = 32, ScaledHeight = 32 },
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            (Color primary, Color secondary) = ExtractPalette(pixels.DetachPixelData());
            if (request == _artworkRequest)
            {
                SetPalette(primary, secondary);
            }
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException("Could not decode immersive artwork palette.", error);
            if (request == _artworkRequest)
            {
                SetPalette(Color.FromArgb(255, 64, 108, 148),
                           Color.FromArgb(255, 50, 82, 111));
            }
        }
    }

    // Chroma-weighted dominant hue clusters distinguish e.g. red, blue,
    // green and purple covers without letting a small white text area or
    // neutral photographic background wash every album into the same grey.
    private static (Color Primary, Color Secondary) ExtractPalette(byte[] bgra)
    {
        const int Buckets = 24;
        double[] weights = new double[Buckets];
        double[] reds = new double[Buckets];
        double[] greens = new double[Buckets];
        double[] blues = new double[Buckets];
        double neutralR = 0, neutralG = 0, neutralB = 0, neutralWeight = 0;

        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            double alpha = bgra[i + 3] / 255.0;
            if (alpha < 0.15) continue;

            double red = bgra[i + 2] / 255.0;
            double green = bgra[i + 1] / 255.0;
            double blue = bgra[i] / 255.0;
            double max = Math.Max(red, Math.Max(green, blue));
            double min = Math.Min(red, Math.Min(green, blue));
            double chroma = max - min;
            neutralR += red * alpha;
            neutralG += green * alpha;
            neutralB += blue * alpha;
            neutralWeight += alpha;

            if (chroma < 0.10 || max < 0.12 || min > 0.95)
                continue;

            double hue = max == red
                ? (green - blue) / chroma
                : max == green
                    ? 2 + (blue - red) / chroma
                    : 4 + (red - green) / chroma;
            hue = ((hue % 6) + 6) % 6;
            int bucket = Math.Min(Buckets - 1, (int)(hue / 6 * Buckets));
            double weight = alpha * Math.Pow(chroma, 1.35)
                * (0.40 + Math.Sqrt(max));
            weights[bucket] += weight;
            reds[bucket] += red * weight;
            greens[bucket] += green * weight;
            blues[bucket] += blue * weight;
        }

        static byte Byte(double value) =>
            (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
        static Color ColorOf(double r, double g, double b) =>
            Color.FromArgb(255, Byte(r), Byte(g), Byte(b));

        int main = -1;
        for (int j = 0; j < Buckets; j++)
            if (main == -1 || weights[j] > weights[main]) main = j;
        if (main < 0 || weights[main] < 0.01)
        {
            double n = Math.Max(neutralWeight, 1);
            Color neutral = ColorOf(
                neutralR / n, neutralG / n, neutralB / n);
            return (neutral, neutral);
        }

        Color primary = ColorOf(reds[main] / weights[main],
            greens[main] / weights[main],
            blues[main] / weights[main]);
        int secondaryBin = -1;
        for (int j = 0; j < Buckets; j++)
        {
            int dist = Math.Min(
                (j - main + Buckets) % Buckets,
                (main - j + Buckets) % Buckets);
            if (dist < 3 || weights[j] < weights[main] * 0.12) continue;
            if (secondaryBin == -1 || weights[j] > weights[secondaryBin])
                secondaryBin = j;
        }
        Color secondary = secondaryBin < 0
            ? primary
            : ColorOf(reds[secondaryBin] / weights[secondaryBin],
                greens[secondaryBin] / weights[secondaryBin],
                blues[secondaryBin] / weights[secondaryBin]);
        return (primary, secondary);
    }

    private void SetPalette(Color primary, Color secondary)
    {
        static Color Blend(Color first, Color second, double weight)
        {
            byte Part(byte a, byte b) =>
                (byte)Math.Clamp((int)Math.Round(a * (1 - weight) + b * weight), 0, 255);
            return Color.FromArgb(255,
                Part(first.R, second.R),
                Part(first.G, second.G),
                Part(first.B, second.B));
        }

        Color ink = Color.FromArgb(255, 10, 14, 23);
        Color white = Color.FromArgb(255, 255, 255, 255);
        ArtworkGradient.GradientStops[0].Color = Blend(ink, primary, 0.47);
        ArtworkGradient.GradientStops[1].Color = Blend(ink, secondary, 0.36);
        ArtworkGradient.GradientStops[2].Color = Blend(ink, primary, 0.14);
        PaletteGlow.Fill = new SolidColorBrush(primary);
        Color accent = Blend(primary, white, 0.30);
        BrandAccent.Background = new SolidColorBrush(accent);
        ProgressSlider.Foreground = new SolidColorBrush(accent);
        PlayPauseButton.Background = new SolidColorBrush(accent);
        PlayPauseIcon.Foreground = new SolidColorBrush(Blend(primary, ink, 0.88));
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
