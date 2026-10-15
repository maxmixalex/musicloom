using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicLoom.Core.Configuration;
using Windows.Graphics.Imaging;
using Windows.Foundation;
using System.Runtime.InteropServices.WindowsRuntime;
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

    internal event Action<Color>? PaletteChanged;
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
        AlbumCover.ImageOpened += async (_, _) =>
            await ApplyDisplayedCoverPaletteAsync();
        ImmersiveLyricsList.Loaded += (_, _) =>
        {
            HookLyricScroller();
            DispatcherQueue.TryEnqueue(UpdateLyricEdgeOpacity);
        };
        ImmersiveLyricsList.SizeChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(UpdateLyricEdgeOpacity);
        ImmersiveLyricsList.ContainerContentChanging += (_, _) =>
            DispatcherQueue.TryEnqueue(UpdateLyricEdgeOpacity);
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
        string artist,
        string album,
        ImageSource? cover,
        IEnumerable<LyricDisplayLine> lines)
    {
        TrackHeading.Text = title;
        ArtistHeading.Text = artist;
        AlbumHeading.Text = album;
        AlbumCover.Source = cover;
        LyricDisplayLine[] display = lines.ToArray();
        ImmersiveLyricsList.ItemsSource = display;
        LyricModeBox.Visibility = display.Any(
            static line => !string.IsNullOrWhiteSpace(line.Translation))
            ? Visibility.Visible : Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(UpdateLyricEdgeOpacity);
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
            // Album art can already exist in the UI bitmap cache when
            // neither its original audio tag nor a cover file is readable.
            // Sample what the user is ACTUALLY seeing instead of resetting
            // every album to the same default blue.
            if (await TryApplyDisplayedCoverPaletteAsync(request))
                return;
            if (request == _artworkRequest)
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
            if (request == _artworkRequest
                && !await TryApplyDisplayedCoverPaletteAsync(request))
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
        Color accent = Blend(primary, white, 0.30);
        ProgressSlider.Foreground = new SolidColorBrush(accent);
        ProgressSlider.Resources["SliderTrackValueFill"] = new SolidColorBrush(accent);
        ProgressSlider.Resources["SliderTrackValueFillPointerOver"] =
            new SolidColorBrush(Blend(primary, white, 0.45));
        ProgressSlider.Resources["SliderThumbBackground"] = new SolidColorBrush(accent);
        // The list fades via item opacity, not a dark overlay. All gradient
        // stops remain dedicated to the actual album-driven background.
        PaletteChanged?.Invoke(ArtworkGradient.GradientStops[0].Color);
    }

    // Both columns have exactly the same viewport height. Neither image
    // aspect ratio nor lyric list desired size participates in measuring it.
    private void ImmersiveLayoutRoot_SizeChanged(
        object sender, SizeChangedEventArgs e)
    {
        double width = Math.Min(1160, Math.Max(0, e.NewSize.Width - 68));
        if (Math.Abs(PlayerColumns.Width - width) > 0.5)
            PlayerColumns.Width = width;
    }

    private void PlayerColumns_SizeChanged(
        object sender, SizeChangedEventArgs e)
    {
        double width = Math.Max(0, (e.NewSize.Width - 38) / 2);
        double height = Math.Max(0, e.NewSize.Height - 146);
        double size = Math.Max(0, Math.Min(480, Math.Min(width, height)));
        if (Math.Abs(ImmersiveArtworkFrame.Width - size) > 0.5)
            ImmersiveArtworkFrame.Width = size;
        if (Math.Abs(ImmersiveArtworkFrame.Height - size) > 0.5)
            ImmersiveArtworkFrame.Height = size;
    }

    private async Task ApplyDisplayedCoverPaletteAsync()
    {
        int current = _artworkRequest;
        await TryApplyDisplayedCoverPaletteAsync(current);
    }

    private async Task<bool> TryApplyDisplayedCoverPaletteAsync(int request)
    {
        if (!IsLoaded || AlbumCover.Source is null
            || AlbumCover.ActualWidth < 1 || AlbumCover.ActualHeight < 1)
            return false;

        try
        {
            RenderTargetBitmap bitmap = new();
            await bitmap.RenderAsync(AlbumCover, 32, 32);
            var pixels = await bitmap.GetPixelsAsync();
            byte[] bgra = pixels.ToArray();
            if (request != _artworkRequest) return true;
            if (bgra.Length < 4) return false;
            (Color primary, Color secondary) = ExtractPalette(bgra);
            SetPalette(primary, secondary);
            return true;
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not sample visible album cover palette.", error);
            return false;
        }
    }

    // WinUI ListView has no WPF-style OpacityMask. Applying opacity to
    // realized item containers gives a true alpha fade top AND bottom,
    // independent of background hues and without darkening the lyrics.
    private void HookLyricScroller()
    {
        if (_lyricScroller is not null) return;
        _lyricScroller = FindScroller(ImmersiveLyricsList);
        if (_lyricScroller is not null)
            _lyricScroller.ViewChanged += (_, _) => UpdateLyricEdgeOpacity();
    }

    private ScrollViewer? _lyricScroller;

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            ScrollViewer? found = FindScroller(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    private void UpdateLyricEdgeOpacity()
    {
        Panel? panel = ImmersiveLyricsList.ItemsPanelRoot;
        if (panel is null) return;

        double viewport = ImmersiveLyricsList.ActualHeight;
        if (viewport <= 0) return;
        double edge = Math.Min(88, viewport * 0.23);
        foreach (UIElement element in panel.Children)
        {
            if (element is not ListViewItem item) continue;
            try
            {
                Point origin = item.TransformToVisual(ImmersiveLyricsList)
                    .TransformPoint(new Point(0, 0));
                double middle = origin.Y + item.ActualHeight / 2;
                double upper = Math.Clamp(middle / edge, 0, 1);
                double lower = Math.Clamp((viewport - middle) / edge, 0, 1);
                item.Opacity = Math.Clamp(Math.Min(upper, lower), 0, 1);
            }
            catch (ArgumentException)
            {
                // A recycled item may detach during a layout pass.
            }
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
