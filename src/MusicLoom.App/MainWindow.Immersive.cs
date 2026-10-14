using Microsoft.UI.Xaml;
using MusicLoom.Core.Configuration;
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
     private void InitializeImmersivePlayer()
    {
        ImmersiveView.CloseRequested += (_, _) => CloseImmersivePlayer();
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

    private async void ImmersiveOpenButton_Click(
        object sender, RoutedEventArgs e)
    {
        if (_immersiveActive)
        {
            return;
        }

        try
        {
            // Cover the existing window's client area, including our
            // custom TitleBar. Do not resize the window or alter AppWindow.
            _immersiveActive = true;
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
                    L("Nothing playing", "尚未播放"), string.Empty, null, []);
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
        string label = string.IsNullOrWhiteSpace(item.Artist)
            ? item.Title
            : item.Artist + " - " + item.Title;
        ImmersiveView.SetTrack(label, item.Album, item.Artwork, []);
        ImmersiveView.SetMode(_lyricsDisplayMode);
        if (updatePalette)
        {
            int artworkVersion = ++_immersiveArtworkVersion;
            // The palette cannot be coupled to a SQLite lyric query:
            // no lyrics or switching language must never freeze cover color.
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
            ImmersiveView.SetTrack(label, item.Album, item.Artwork, lines);
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
            byte[]? imageBytes = await Task.Run(() =>
            {
                if (_settings is not MusicLoomSettings settings)
                    return null;
                return TryReadEmbeddedArtwork(item.Entry)
                    ?? TryReadExternalArtwork(settings, item.Entry);
            });

            if (!_immersiveActive || artworkVersion != _immersiveArtworkVersion)
            {
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
