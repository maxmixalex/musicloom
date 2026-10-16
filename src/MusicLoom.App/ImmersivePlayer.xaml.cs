using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicLoom.Core.Configuration;
using Windows.Graphics.Imaging;
using Windows.Foundation;
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
    private string _lyricMode = LyricsDisplayModes.PreferOriginal;
    private Func<string, string, string> _translate = (english, _) => english;
    private double _immersiveScale = 1;
    private int _artworkRequest;
    private bool _paletteOwnedBySourceBytes;
    private ScrollViewer? _lyricScrollViewer;

    // One shared animation clock drives the client gradient, progress accent,
    // fixed lyric veils and real native title-bar colors together.
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _paletteTimer;
    private readonly System.Diagnostics.Stopwatch _paletteClock = new();
    private readonly Color[] _paletteStart = new Color[3];
    private readonly Color[] _paletteTarget = new Color[3];
    private Color _accentStart;
    private Color _accentTarget;
    private readonly SolidColorBrush _progressAccent =
        new(Color.FromArgb(255, 232, 255, 255));
    private readonly SolidColorBrush _progressAccentHover =
        new(Color.FromArgb(255, 255, 255, 255));
    private readonly SolidColorBrush _progressAccentPressed =
        new(Color.FromArgb(255, 255, 255, 255));

    internal Color CurrentTopColor => ArtworkGradient.GradientStops[0].Color;
    internal event Action<Color>? PaletteChanged;
    public event EventHandler? CloseRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PlayPauseRequested;
    public event Action<double>? SeekRequested;
    public event Action<double>? LyricSeekRequested;
    public event Action<string>? ModeChanged;

    internal ListView LyricsList => ImmersiveLyricsList;
    internal ImageSource? CurrentArtwork => AlbumCover.Source;
    internal ScrollViewer? LyricsScrollViewer => _lyricScrollViewer;

    public ImmersivePlayer()
    {
        InitializeComponent();
        UpdateLyricModeButton();
        AlbumCover.ImageOpened += async (_, _) =>
        {
            UpdateCoverSurfaceSize();
            // Original file bytes give a more reliable palette than a
            // 32px RenderTarget sample. ImageOpened must not restart
            // the crossfade after that original palette was applied.
            if (!_paletteOwnedBySourceBytes)
                await ApplyDisplayedCoverPaletteAsync();
        };
        ImmersiveLyricsList.Loaded += (_, _) =>
            DispatcherQueue.TryEnqueue(UpdateLyricFadeMasks);
        ImmersiveLyricsList.SizeChanged += (_, _) =>
            DispatcherQueue.TryEnqueue(UpdateLyricFadeMasks);
        // Virtualization and item changes can alter ScrollableHeight
        // without changing the ListView's own ActualHeight.
        ImmersiveLyricsList.LayoutUpdated += (_, _) =>
            UpdateLyricFadeVisibility();

        _paletteTimer = DispatcherQueue.CreateTimer();
        _paletteTimer.Interval = TimeSpan.FromMilliseconds(16);
        _paletteTimer.Tick += (_, _) => AdvancePalette();
        ProgressSlider.Foreground = _progressAccent;
        ProgressSlider.Resources["SliderTrackValueFill"] = _progressAccent;
        ProgressSlider.Resources["SliderTrackValueFillPointerOver"] =
            _progressAccentHover;
        ProgressSlider.Resources["SliderTrackValueFillPressed"] =
            _progressAccentPressed;
        ProgressSlider.Resources["SliderThumbBackground"] = _progressAccent;
        ProgressSlider.Resources["SliderThumbBackgroundPointerOver"] =
            _progressAccentHover;
        ProgressSlider.Resources["SliderThumbBackgroundPressed"] =
            _progressAccentPressed;
        _initialized = true;
    }

    internal void Localize(Func<string, string, string> translate)
    {
        _translate = translate;
        UpdateLyricModeButton();
        ToolTipService.SetToolTip(ExitButton,
            translate("Exit fullscreen", "退出全屏"));
    }

    private void UpdateLyricModeButton()
    {
        LyricModeIcon.Glyph = _lyricMode switch
        {
            LyricsDisplayModes.OriginalOnly => "\uE97E",
            LyricsDisplayModes.PreferTranslation => "\uF2B7",
            _ => "\uE8C1",
        };
        string mode = _lyricMode switch
        {
            LyricsDisplayModes.OriginalOnly =>
                _translate("Original only", "仅原文"),
            LyricsDisplayModes.PreferTranslation =>
                _translate("Prefer translation", "优先译文"),
            _ => _translate("Prefer original", "优先原文"),
        };
        ToolTipService.SetToolTip(LyricModeButton,
            mode + _translate(" (click to cycle)", "（点击切换）"));
    }

    internal void SetMode(string displayMode)
    {
        _lyricMode = LyricsDisplayModes.Normalize(displayMode);
        UpdateLyricModeButton();
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
        SetCover(cover);
        SetLyrics(lines);
    }

    internal void SetCover(ImageSource? cover)
    {
        if (ReferenceEquals(AlbumCover.Source, cover)) return;
        ++_artworkRequest; // Cancel any pending palette sample for the old image.
        _paletteOwnedBySourceBytes = false;
        AlbumCover.Source = cover;
        UpdateCoverSurfaceSize();
    }

    internal void SetLyrics(IEnumerable<LyricDisplayLine> lines)
    {
        LyricDisplayLine[] display = lines.ToArray();
        foreach (LyricDisplayLine line in display)
            line.SetImmersiveScale(_immersiveScale);
        LyricsTopFade.Visibility = Visibility.Collapsed;
        LyricsBottomFade.Visibility = Visibility.Collapsed;
        ImmersiveLyricsList.ItemsSource = display;
        LyricModeButton.Visibility = display.Any(
            static line => !string.IsNullOrWhiteSpace(line.Translation))
            ? Visibility.Visible : Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(UpdateLyricFadeMasks);
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

    internal void SetNavigationAvailable(bool available)
    {
        PreviousButton.IsEnabled = available;
        NextButton.IsEnabled = available;
    }

    internal async Task ApplyArtworkColorsAsync(byte[]? artwork)
    {
        int request = ++_artworkRequest;
        _paletteOwnedBySourceBytes = artwork is { Length: > 0 };
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
            _paletteOwnedBySourceBytes = false;
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

    private static Color Blend(Color first, Color second, double weight)
    {
        byte Part(byte a, byte b) =>
            (byte)Math.Clamp((int)Math.Round(a * (1 - weight) + b * weight),
                0, 255);
        return Color.FromArgb(255,
            Part(first.R, second.R), Part(first.G, second.G),
            Part(first.B, second.B));
    }

    private void SetPalette(Color primary, Color secondary)
    {
        Color ink = Color.FromArgb(255, 10, 14, 23);
        Color white = Color.FromArgb(255, 255, 255, 255);
        Color[] next =
        [
            Blend(ink, primary, 0.70),
            Blend(ink, secondary, 0.54),
            Blend(ink, primary, 0.26),
        ];
        for (int i = 0; i < 3; i++)
        {
            // Interrupted track changes continue smoothly from the current
            // visible colors, not from the previous album's original hue.
            _paletteStart[i] = ArtworkGradient.GradientStops[i].Color;
            _paletteTarget[i] = next[i];
        }
        _accentStart = _progressAccent.Color;
        _accentTarget = Blend(primary, white, 0.30);
        _paletteClock.Restart();
        _paletteTimer.Start();
    }

    private void AdvancePalette()
    {
        const double DurationMilliseconds = 480;
        double progress = Math.Clamp(
            _paletteClock.Elapsed.TotalMilliseconds / DurationMilliseconds,
            0, 1);
        // Smoothstep: no abrupt onset or end when a new cover is selected.
        double easing = progress * progress * (3 - 2 * progress);
        for (int i = 0; i < 3; i++)
        {
            ArtworkGradient.GradientStops[i].Color =
                Blend(_paletteStart[i], _paletteTarget[i], easing);
        }
        _progressAccent.Color = Blend(_accentStart, _accentTarget, easing);
        _progressAccentHover.Color = Blend(
            _progressAccent.Color,
            Color.FromArgb(255, 255, 255, 255), 0.20);
        _progressAccentPressed.Color = Blend(
            _progressAccent.Color,
            Color.FromArgb(255, 255, 255, 255), 0.07);
        UpdateLyricFadeColors();
        PaletteChanged?.Invoke(CurrentTopColor);

        if (progress >= 1)
        {
            _paletteTimer.Stop();
            _paletteClock.Stop();
        }
    }

    // Layout dimensions are computed from the viewport, not from the image
    // pixels, track-title length or virtualized lyric items. The two columns
    // therefore always occupy identical height and resize with the window.
    private void ImmersiveLayoutRoot_SizeChanged(
        object sender, SizeChangedEventArgs e)
    {
        double width = Math.Max(0, e.NewSize.Width - 68) * 0.88;
        // 24 + 20: root padding; 38: the exit-button row.
        double height = Math.Max(0, e.NewSize.Height - 88) * 0.88;
        if (!double.IsFinite(PlayerColumns.Width)
            || Math.Abs(PlayerColumns.Width - width) > 0.5)
            PlayerColumns.Width = width;
        if (!double.IsFinite(PlayerColumns.Height)
            || Math.Abs(PlayerColumns.Height - height) > 0.5)
            PlayerColumns.Height = height;
        double scale = Math.Clamp(
            Math.Min(e.NewSize.Width / 1260, e.NewSize.Height / 770),
            0.62, 1.22);
        if (Math.Abs(scale - _immersiveScale) > 0.005)
        {
            _immersiveScale = scale;
            TrackHeading.FontSize = 25 * scale;
            ArtistHeading.FontSize = 16 * scale;
            AlbumHeading.FontSize = 13 * scale;
            LyricModeIcon.FontSize = 18 * scale;
            PlaybackTime.FontSize = 12 * scale;
            if (ImmersiveLyricsList.ItemsSource is LyricDisplayLine[] lines)
                foreach (LyricDisplayLine line in lines)
                    line.SetImmersiveScale(scale);
        }
        DispatcherQueue.TryEnqueue(UpdateLyricFadeMasks);
    }

    private void PlayerColumns_SizeChanged(
        object sender, SizeChangedEventArgs e)
    {
        double panelWidth = Math.Max(0, (e.NewSize.Width - 38) / 2);
        // Reserve the progress/time rows, buttons and row spacing.
        double panelHeight = Math.Max(0, e.NewSize.Height - 82);
        double side = Math.Min(panelWidth, panelHeight);
        if (Math.Abs(ImmersiveArtworkFrame.Width - side) > 0.5)
            ImmersiveArtworkFrame.Width = side;
        if (Math.Abs(ImmersiveArtworkFrame.Height - side) > 0.5)
            ImmersiveArtworkFrame.Height = side;
        UpdateCoverSurfaceSize();
        DispatcherQueue.TryEnqueue(UpdateLyricFadeMasks);
    }

    private void UpdateCoverSurfaceSize()
    {
        double slot = Math.Max(0, Math.Min(
            ImmersiveArtworkFrame.Width,
            ImmersiveArtworkFrame.Height));
        double ratio = AlbumCover.Source is BitmapImage bitmap
            && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0
                ? (double)bitmap.PixelWidth / bitmap.PixelHeight : 1;
        if (!double.IsFinite(ratio) || ratio <= 0) ratio = 1;
        double width = ratio >= 1 ? slot : slot * ratio;
        double height = ratio >= 1 ? slot / ratio : slot;
        if (Math.Abs(AlbumCoverSurface.Width - width) > 0.5)
            AlbumCoverSurface.Width = width;
        if (Math.Abs(AlbumCoverSurface.Height - height) > 0.5)
            AlbumCoverSurface.Height = height;
        AlbumCoverSurface.CornerRadius =
            new CornerRadius(Math.Min(16 * _immersiveScale,
                Math.Min(width, height) * 0.1));
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
            byte[] bgra = new byte[(int)pixels.Length];
            using (DataReader reader = DataReader.FromBuffer(pixels))
                reader.ReadBytes(bgra);
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

    // WinUI 3 does not expose a WPF-style OpacityMask on ListView.
    // Instead the transparent-to-opaque edge veils are positioned OVER the
    // viewport and sample the exact background gradient underneath. Unlike
    // modifying each recycled ListViewItem, these do not move on scroll.
    private void UpdateLyricFadeMasks()
    {
        EnsureLyricScrollViewer();
        double viewport = ImmersiveLyricsList.ActualHeight;
        if (viewport <= 0) return;
        double edge = Math.Min(92, viewport * 0.23);
        if (Math.Abs(LyricsTopFade.Height - edge) > 0.5)
            LyricsTopFade.Height = edge;
        if (Math.Abs(LyricsBottomFade.Height - edge) > 0.5)
            LyricsBottomFade.Height = edge;
        UpdateLyricFadeVisibility();
        DispatcherQueue.TryEnqueue(UpdateLyricFadeColors);
    }

    private void EnsureLyricScrollViewer()
    {
        if (_lyricScrollViewer is not null) return;
        _lyricScrollViewer = FindNestedScrollViewer(ImmersiveLyricsList);
        if (_lyricScrollViewer is not null)
            _lyricScrollViewer.ViewChanged += (_, _) =>
                UpdateLyricFadeVisibility();
    }

    private static ScrollViewer? FindNestedScrollViewer(
        DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            ScrollViewer? nested = FindNestedScrollViewer(
                VisualTreeHelper.GetChild(parent, i));
            if (nested is not null) return nested;
        }
        return null;
    }

    private void UpdateLyricFadeVisibility()
    {
        EnsureLyricScrollViewer();
        ScrollViewer? scroll = _lyricScrollViewer;
        if (scroll is null || scroll.ScrollableHeight < 2)
        {
            LyricsTopFade.Visibility = Visibility.Collapsed;
            LyricsBottomFade.Visibility = Visibility.Collapsed;
            return;
        }
        // Only veil an edge if there are actual offscreen lyric pixels on
        // that side. The first and last lines stay crisp at the boundaries.
        LyricsTopFade.Visibility = scroll.VerticalOffset > 1
            ? Visibility.Visible : Visibility.Collapsed;
        LyricsBottomFade.Visibility =
            scroll.VerticalOffset < scroll.ScrollableHeight - 1
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private Color BackgroundAt(double y)
    {
        double height = Math.Max(1, ImmersiveLayoutRoot.ActualHeight);
        double position = Math.Clamp(y / height, 0, 1) * 2;
        return position <= 1
            ? Blend(ArtworkGradient.GradientStops[0].Color,
                ArtworkGradient.GradientStops[1].Color, position)
            : Blend(ArtworkGradient.GradientStops[1].Color,
                ArtworkGradient.GradientStops[2].Color, position - 1);
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    private void UpdateLyricFadeColors()
    {
        if (!IsLoaded || ImmersiveLayoutRoot.ActualHeight <= 0)
            return;
        try
        {
            double top = LyricsTopFade.TransformToVisual(ImmersiveLayoutRoot)
                .TransformPoint(new Point(0, 0)).Y;
            double bottom = LyricsBottomFade.TransformToVisual(ImmersiveLayoutRoot)
                .TransformPoint(new Point(0, 0)).Y;

            // Opaque exactly at the edge, fully transparent towards center;
            // RGB at every stop matches the vertically painted background.
            LyricsTopFadeBrush.GradientStops[0].Color =
                WithAlpha(BackgroundAt(top), 255);
            LyricsTopFadeBrush.GradientStops[1].Color =
                WithAlpha(BackgroundAt(top + LyricsTopFade.ActualHeight), 0);
            LyricsBottomFadeBrush.GradientStops[0].Color =
                WithAlpha(BackgroundAt(bottom), 0);
            LyricsBottomFadeBrush.GradientStops[1].Color =
                WithAlpha(BackgroundAt(bottom + LyricsBottomFade.ActualHeight), 255);
        }
        catch (ArgumentException)
        {
            // The template is temporarily detached during layout changes.
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

    private void LyricModeButton_Click(object sender, RoutedEventArgs e)
    {
        _lyricMode = _lyricMode switch
        {
            LyricsDisplayModes.PreferOriginal =>
                LyricsDisplayModes.PreferTranslation,
            LyricsDisplayModes.PreferTranslation =>
                LyricsDisplayModes.OriginalOnly,
            _ => LyricsDisplayModes.PreferOriginal,
        };
        UpdateLyricModeButton();
        ModeChanged?.Invoke(_lyricMode);
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
