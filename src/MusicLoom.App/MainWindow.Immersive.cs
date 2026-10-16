using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MusicLoom.Core.Configuration;
using Windows.UI;
using MusicLoom.Core.Lyrics;

namespace MusicLoom.App;

/// <summary>
/// Immersive screen uses the existing MediaPlayer and canonical SQLite
/// lyric document. It never creates another player or independent queue.
/// </summary>
public sealed partial class MainWindow
{
    private bool _immersiveActive;
    private int _immersiveLoadVersion;
    private int _immersiveArtworkVersion;
    private ElementTheme _previousImmersiveTitleTheme;
    private Brush? _previousImmersiveTitleBackground;
    private Brush? _previousImmersiveTitleForeground;
    private Brush? _previousImmersiveTitleBorder;
    private Thickness _previousImmersiveTitleBorderThickness;
    private Brush? _previousImmersiveRootBackground;
    private Color? _previousCaptionButtonBackground;
    private Color? _previousCaptionButtonForeground;
    private bool _captionColorsSupported;
    private readonly SolidColorBrush _immersiveTitleBrush =
        new(Color.FromArgb(255, 56, 108, 138));
    private readonly SolidColorBrush _immersiveWhiteTextBrush =
        new(Color.FromArgb(255, 255, 255, 255));

    private void InitializeImmersivePlayer()
    {
        ImmersiveView.CloseRequested += (_, _) => CloseImmersivePlayer();
        ImmersiveView.PaletteChanged += ApplyImmersiveTitlePalette;
        ImmersiveView.PreviousRequested += async (_, _) =>
            await PlayQueueOffsetAsync(-1);
        ImmersiveView.NextRequested += async (_, _) =>
            await PlayQueueOffsetAsync(1);
        ImmersiveView.PlayPauseRequested += (_, _) =>
            PlayerPlayPauseButton_Click(ImmersiveView, new RoutedEventArgs());
        ImmersiveView.SeekRequested += seconds =>
        {
            if (_mediaPlayer.Source is null
                || !double.IsFinite(seconds) || seconds < 0)
            {
                return;
            }
            _mediaPlayer.PlaybackSession.Position =
                TimeSpan.FromSeconds(seconds);
            UpdatePlaybackLyrics();
        };
        ImmersiveView.LyricSeekRequested += seconds =>
        {
            if (_mediaPlayer.Source is null
                || !double.IsFinite(seconds) || seconds < 0)
            {
                return;
            }
            _mediaPlayer.PlaybackSession.Position =
                TimeSpan.FromSeconds(seconds);
            _mediaPlayer.Play();
            UpdatePlaybackLyrics();
        };
        ImmersiveView.ModeChanged += async mode =>
        {
            _lyricsDisplayMode = LyricsDisplayModes.Normalize(mode);
            RenderLyrics();
            await PersistLyricsDisplayModeAsync();
            if (_playingLibraryItem is LibraryDisplayItem current)
            {
                await UpdateImmersiveTrackAsync(current, updatePalette: false);
            }
        };
    }

    private void ApplyImmersiveTitlePalette(Color background)
    {
        if (!_immersiveActive) return;
        // The player gradient is vertical: its entire top edge matches this
        // solid title/caption color. Both are advanced on the SAME animation
        // tick so no dark horizontal seam appears while switching songs.
        // Do not allocate four new brushes every 16ms during a palette
        // crossfade. Sharing the same mutable brush prevents title, border,
        // and client seam from rendering different animation frames.
        _immersiveTitleBrush.Color = background;
        if (!ReferenceEquals(AppTitleBar.Background, _immersiveTitleBrush))
        {
            AppTitleBar.Background = _immersiveTitleBrush;
            AppTitleBar.BorderBrush = _immersiveTitleBrush;
            AppTitleBar.BorderThickness = new Thickness(0);
            AppRoot.Background = _immersiveTitleBrush;
            AppTitleBar.Foreground = _immersiveWhiteTextBrush;
            if (_captionColorsSupported)
                AppWindow.TitleBar.ButtonForegroundColor =
                    Color.FromArgb(255, 255, 255, 255);
        }
        if (_captionColorsSupported)
            AppWindow.TitleBar.ButtonBackgroundColor = background;
    }

    private async void ImmersiveOpenButton_Click(
        object sender, RoutedEventArgs e)
    {
        if (_immersiveActive)
        {
            return;
        }

        try
        {
            // Keep AppTitleBar visible and theme-aware. Only replace the
            // navigation client area, without modifying OS window bounds.
            _previousImmersiveTitleTheme = AppTitleBar.RequestedTheme;
            _previousImmersiveTitleBackground = AppTitleBar.Background;
            _previousImmersiveTitleForeground = AppTitleBar.Foreground;
            _previousImmersiveTitleBorder = AppTitleBar.BorderBrush;
            _previousImmersiveTitleBorderThickness = AppTitleBar.BorderThickness;
            _previousImmersiveRootBackground = AppRoot.Background;
            _captionColorsSupported =
                Microsoft.UI.Windowing.AppWindowTitleBar
                    .IsCustomizationSupported();
            if (_captionColorsSupported)
            {
                _previousCaptionButtonBackground =
                    AppWindow.TitleBar.ButtonBackgroundColor;
                _previousCaptionButtonForeground =
                    AppWindow.TitleBar.ButtonForegroundColor;
            }

            _immersiveActive = true;
            // The WinUI title and OS window title must both survive an
            // immersive view switch; do not rely on decorative in-view text.
            AppTitleBar.Title = "MusicLoom";
            AppWindow.Title = "MusicLoom";
            AppTitleBar.RequestedTheme = ElementTheme.Dark;
            ApplyImmersiveTitlePalette(ImmersiveView.CurrentTopColor);
            ImmersiveHost.Visibility = Visibility.Visible;
            ImmersiveView.SetMode(_lyricsDisplayMode);
            UpdatePlayerProgress();
            UpdatePlayerVisualState();
            ImmersiveView.Focus(FocusState.Programmatic);
            if (_playingLibraryItem is LibraryDisplayItem playing)
            {
                await UpdateImmersiveTrackAsync(playing);
            }
            else if (_selectedLibraryItem is LibraryDisplayItem selected)
            {
                await UpdateImmersiveTrackAsync(selected);
            }
            else
            {
                ImmersiveView.SetTrack(
                    L("Nothing playing", "尚未播放"),
                    string.Empty, string.Empty, null, []);
                await ImmersiveView.ApplyArtworkColorsAsync(null);
            }
        }
        catch (Exception error)
        {
            CloseImmersivePlayer();
            HandleUiException(
                L("Could not open immersive player.", "无法进入沉浸式播放器。"),
                error);
        }
    }

    private void CloseImmersivePlayer()
    {
        if (!_immersiveActive)
        {
            return;
        }

        ++_immersiveLoadVersion;
        ++_immersiveArtworkVersion;
        _immersiveActive = false;
        ImmersiveHost.Visibility = Visibility.Collapsed;
        AppTitleBar.RequestedTheme = _previousImmersiveTitleTheme;
        AppTitleBar.Background = _previousImmersiveTitleBackground;
        AppTitleBar.Foreground = _previousImmersiveTitleForeground;
        AppTitleBar.BorderBrush = _previousImmersiveTitleBorder;
        AppTitleBar.BorderThickness = _previousImmersiveTitleBorderThickness;
        AppRoot.Background = _previousImmersiveRootBackground;
        if (_captionColorsSupported)
        {
            AppWindow.TitleBar.ButtonBackgroundColor =
                _previousCaptionButtonBackground;
            AppWindow.TitleBar.ButtonForegroundColor =
                _previousCaptionButtonForeground;
        }
        _activePlaybackLyric?.IsActive = false;
        _activePlaybackLyric = null;
        _lastAutoScrolledLyric = null;
        _lyricFollowBlockedUntil = DateTimeOffset.MinValue;

        // Restore ordinary navigation in place; the OS window retains
        // its original size, maximization and taskbar state throughout.
    }

    private async Task UpdateImmersiveTrackAsync(
        LibraryDisplayItem item, bool updatePalette = true)
    {
        int version = ++_immersiveLoadVersion;
        if (updatePalette)
        {
            ImmersiveView.SetTrack(
                item.Title, item.Artist, item.Album, item.Artwork, []);
        }
        // Changing lyric language should not blank/reflow the artwork or
        // current lyric viewport while the SQLite request is running.
        ImmersiveView.SetMode(_lyricsDisplayMode);
        if (updatePalette)
        {
            int artworkVersion = ++_immersiveArtworkVersion;
            // The palette cannot be coupled to a SQLite lyric query:
            // no lyrics or switching language must never freeze cover color.
            // Avoid a blue/default intermediate flash before the real cover
            // bytes arrive. A visible BitmapImage samples itself on ImageOpened.
            if (item.Artwork is null)
                _ = ImmersiveView.ApplyArtworkColorsAsync(null);
            _ = RefreshImmersiveArtworkAsync(item, artworkVersion);
        }

        try
        {
            LibraryLyricsRecord? record =
                await new SqliteLyricsCache(DatabasePath)
                    .GetLibraryAsync(item.Entry.SourcePath);
            if (!_immersiveActive || version != _immersiveLoadVersion)
            {
                return;
            }

            LyricDisplayLine[] lines = BuildLyricLines(
                record?.Text, _lyricsDisplayMode);
            ImmersiveView.SetTrack(
                item.Title, item.Artist, item.Album, item.Artwork, lines);
            _activePlaybackLyric?.IsActive = false;
            _activePlaybackLyric = null;
            _lastAutoScrolledLyric = null;
            UpdatePlaybackLyrics();

        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not refresh immersive lyrics.", error);
        }
    }

    private async Task RefreshImmersiveArtworkAsync(
        LibraryDisplayItem item, int artworkVersion)
    {
        try
        {
            // Reuse the already decoded detail-pane artwork when the
            // selected/playing item is the same. Previously this byte cache
            // was ignored even when it held the exact displayed image.
            byte[]? imageBytes = ReferenceEquals(_selectedLibraryItem, item)
                ? _currentArtworkBytes
                : null;
            if (imageBytes is null)
            {
                imageBytes = await Task.Run(() =>
                {
                    if (_settings is not MusicLoomSettings settings)
                        return null;
                    return TryReadArtworkBytes(settings, item.Entry);
                });
            }

            if (!_immersiveActive || artworkVersion != _immersiveArtworkVersion)
            {
                return;
            }
            if (imageBytes is null && item.Artwork is not null)
            {
                // Library browsing groups tracks with the same artwork.
                // Another song in that group may own the embedded bytes.
                var companions = _allLibraryItems
                    .Where(candidate => !ReferenceEquals(candidate, item)
                        && (ReferenceEquals(candidate.Artwork, item.Artwork)
                            || string.Equals(ArtworkKey(candidate), ArtworkKey(item),
                                StringComparison.OrdinalIgnoreCase)))
                    .Take(24)
                    .Select(candidate => candidate.Entry)
                    .ToArray();
                imageBytes = await Task.Run(() =>
                {
                    if (_settings is not MusicLoomSettings saved) return null;
                    foreach (var entry in companions)
                    {
                        byte[]? bytes = TryReadArtworkBytes(saved, entry);
                        if (bytes is { Length: > 0 }) return bytes;
                    }
                    return null;
                });
                if (!_immersiveActive || artworkVersion != _immersiveArtworkVersion)
                    return;
            }
            await ImmersiveView.ApplyArtworkColorsAsync(imageBytes);
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not refresh immersive artwork.", error);
        }
    }
}
