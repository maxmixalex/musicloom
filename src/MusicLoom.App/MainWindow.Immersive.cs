using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using MusicLoom.LyricsProviders;
using MusicLoom.Netease;
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
    private int _remotePlaybackLyricsVersion;
    private LrcDocument? _remotePlaybackLyrics;
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
                await UpdateImmersiveTrackAsync(current, updatePalette: false);
            else if (_playingCloudPreviewCandidate is not null
                || _playingPreviewCandidate is not null)
                UpdateImmersiveRemoteTrack(updatePalette: false);
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

    private const string ConnectedCoverKey = "MusicLoom.Immersive.Cover";
    private static readonly TimeSpan ImmersiveCoverTransitionDuration =
        TimeSpan.FromMilliseconds(460);

    private void PrepareImmersiveConnectedAnimation(bool exiting)
    {
        try
        {
            if (_mediaPlayer.Source is null) return;
            ConnectedAnimationService service =
                ConnectedAnimationService.GetForCurrentView();
            // DirectConnectedAnimationConfiguration always lasts ~150 ms
            // and ignores DefaultDuration. Basic honours the requested
            // duration in BOTH directions; only artwork participates.
            service.DefaultDuration = ImmersiveCoverTransitionDuration;
            UIElement cover = exiting
                ? ImmersiveView.CoverTransitionTarget : PlayerArtworkImage;
            if ((exiting ? ImmersiveView.CurrentArtwork
                : PlayerArtworkImage.Source) is null)
                return;

            ConnectedAnimation animation = service.PrepareToAnimate(
                ConnectedCoverKey, cover);
            animation.Configuration = new BasicConnectedAnimationConfiguration();
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            AppDiagnostics.LogException(
                "Artwork transition could not be prepared; using instant navigation.",
                ex);
        }
    }

    private void StartImmersiveConnectedAnimation(bool exiting)
    {
        try
        {
            AppRoot.UpdateLayout();
            ConnectedAnimationService service =
                ConnectedAnimationService.GetForCurrentView();
            UIElement cover = exiting
                ? PlayerArtworkImage : ImmersiveView.CoverTransitionTarget;
            service.GetAnimation(ConnectedCoverKey)?.TryStart(cover);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            AppDiagnostics.LogException(
                "Artwork transition could not start; displaying destination normally.",
                ex);
        }
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

            PrepareImmersiveConnectedAnimation(exiting: false);
            _immersiveActive = true;
            ImmersiveWindowBorder.TrySetImmersive(this, immersive: true);
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
                Task loading = UpdateImmersiveTrackAsync(playing);
                StartImmersiveConnectedAnimation(exiting: false);
                await loading;
            }
            else if (_playingCloudPreviewCandidate is not null
                || _playingPreviewCandidate is not null)
            {
                UpdateImmersiveRemoteTrack();
                StartImmersiveConnectedAnimation(exiting: false);
            }
            else
            {
                // A selected library item is NOT the currently playing
                // source. Never borrow its art/lyrics for remote or idle.
                ImmersiveView.SetTrack(
                    L("Nothing playing", "尚未播放"),
                    string.Empty, string.Empty, null, []);
                ImmersiveView.SetNavigationAvailable(false);
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

        PrepareImmersiveConnectedAnimation(exiting: true);
        ++_immersiveLoadVersion;
        ++_immersiveArtworkVersion;
        _immersiveActive = false;
        ImmersiveWindowBorder.TrySetImmersive(this, immersive: false);
        ImmersiveHost.Visibility = Visibility.Collapsed;
        StartImmersiveConnectedAnimation(exiting: true);
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

    private void ResetRemotePlaybackLyrics()
    {
        ++_remotePlaybackLyricsVersion;
        _remotePlaybackLyrics = null;
    }

    private void UpdatePlayerSourceLabel()
    {
        string next = _playingLibraryItem is not null
            ? L("Local library", "本地曲库")
            : _playingPreviewCandidate is not null
                ? L("NetEase Music", "网易云音乐")
            : _playingCloudPreviewCandidate?.Source switch
            {
                CloudLyricsSource.QqMusic => L("QQ Music", "QQ音乐"),
                CloudLyricsSource.KugouMusic => L("KuGou Music", "酷狗音乐"),
                CloudLyricsSource.MiguMusic => L("Migu Music", "咪咕音乐"),
                CloudLyricsSource.AppleMusic => "Apple Music",
                CloudLyricsSource.Spotify => "Spotify",
                _ => "",
            };
        if (PlayerSourceText.Text != next)
            PlayerSourceText.Text = next;
    }

    // Remote preview is a first-class playback context. The currently
    // selected LOCAL library item must never supply its cover or lyrics.
    private void UpdateImmersiveRemoteTrack(bool updatePalette = true)
    {
        if (!_immersiveActive)
            return;

        ++_immersiveLoadVersion;  // invalidate in-flight LOCAL SQL reads
        ++_immersiveArtworkVersion; // invalidate in-flight LOCAL art reads
        string title;
        string artist;
        string album;
        if (_playingCloudPreviewCandidate is CloudLyricsCandidate cloud)
        {
            title = cloud.Title;
            artist = cloud.Artist;
            album = cloud.Album;
        }
        else if (_playingPreviewCandidate is NeteaseMatchCandidate netease)
        {
            title = netease.Name;
            artist = netease.Artist;
            album = netease.Album;
        }
        else
            return;

        ImageSource? cover = ImmersiveView.CurrentArtwork;
        if (updatePalette || cover is null)
        {
            // The small now-playing image is decoded at 144px. Request the
            // original remote cover URL without any DecodePixelWidth cap.
            string? artwork = _playingCloudPreviewCandidate?.CoverUrl
                ?? _playingPreviewCandidate?.CoverUrl;
            if (!string.IsNullOrWhiteSpace(artwork)
                && Uri.TryCreate(artwork, UriKind.Absolute,
                    out Uri? uri)
                && uri.Scheme is "http" or "https")
                cover = new BitmapImage { UriSource = uri };
            else
                cover = PlayerArtworkImage.Source;
        }
        ImmersiveView.SetMode(_lyricsDisplayMode);
        ImmersiveView.SetTrack(title, artist, album, cover,
            BuildLyricLines(_remotePlaybackLyrics?.ToString(),
                _lyricsDisplayMode));
        ImmersiveView.SetNavigationAvailable(false, remoteSource: true);
        _activePlaybackLyric?.IsActive = false;
        _activePlaybackLyric = null;
        _lastAutoScrolledLyric = null;
        UpdatePlaybackLyrics();

        if (updatePalette)
        {
            // Sample the remote cover itself on ImageOpened. Never call
            // TryReadArtworkBytes with a local library entry here.
            _ = ImmersiveView.ApplyArtworkColorsAsync(null);
        }
    }

    private async Task LoadNeteasePreviewLyricsAsync(
        NeteaseMatchCandidate candidate, int version)
    {
        try
        {
            using NeteaseClient client = new();
            LrcDocument? lyrics = await client.GetLyricsAsync(candidate.TrackId);
            if (version != _remotePlaybackLyricsVersion
                || _playingPreviewCandidate?.TrackId != candidate.TrackId
                || _playingCloudPreviewCandidate is not null
                || _playingLibraryItem is not null)
                return;
            _remotePlaybackLyrics = lyrics;
            UpdateImmersiveRemoteTrack(updatePalette: false);
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not fetch NetEase remote preview lyrics.", error);
        }
    }

    private async Task LoadCloudPreviewLyricsAsync(
        CloudLyricsCandidate candidate, int version)
    {
        try
        {
            using CloudLyricsClient client = new();
            LrcDocument? lyrics = await client.GetLyricsAsync(candidate);
            if (version != _remotePlaybackLyricsVersion
                || !SameCloudCandidate(_playingCloudPreviewCandidate, candidate)
                || _playingLibraryItem is not null)
                return;
            _remotePlaybackLyrics = lyrics;
            UpdateImmersiveRemoteTrack(updatePalette: false);
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not fetch remote provider preview lyrics.", error);
        }
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
        ImmersiveView.SetNavigationAvailable(_playbackQueue.Length > 1);
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
            // SetTrack would replace the freshly loaded full-resolution
            // cover with the 192px list thumbnail. Only lyrics change here.
            ImmersiveView.SetLyrics(lines);
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
            // Read the preferred ORIGINAL image according to the same
            // MusicBee source order as the library and details. Never
            // promote another album member's 192px thumbnail into this
            // playback track just because its folder key is identical.
            byte[]? bytes = await Task.Run(() =>
                _settings is MusicLoomSettings settings
                    ? TryReadArtworkBytes(settings, item.Entry) : null);
            if (!_immersiveActive
                || artworkVersion != _immersiveArtworkVersion
                || !ReferenceEquals(_playingLibraryItem, item))
                return;

            if (bytes is not { Length: > 0 })
            {
                await ImmersiveView.ApplyArtworkColorsAsync(null);
                return; // The list thumbnail remains a fallback only.
            }

            // The main library intentionally decodes thumbnails at 192px;
            // immersive playback deliberately decodes ALL available pixels.
            BitmapImage fullSize = await CreateBitmapImageAsync(
                bytes, decodePixelWidth: 0);
            if (!_immersiveActive
                || artworkVersion != _immersiveArtworkVersion
                || !ReferenceEquals(_playingLibraryItem, item))
                return;

            ImmersiveView.SetCover(fullSize);
            await ImmersiveView.ApplyArtworkColorsAsync(bytes);
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not refresh full-resolution immersive artwork.", error);
        }
    }

}
