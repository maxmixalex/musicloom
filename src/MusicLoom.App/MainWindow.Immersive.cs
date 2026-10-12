using Microsoft.UI.Windowing;
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
    private AppWindowPresenterKind _previousPresenterKind =
        AppWindowPresenterKind.Overlapped;

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
            _previousPresenterKind = AppWindow.Presenter.Kind;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
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
        _immersiveActive = false;
        ImmersiveHost.Visibility = Visibility.Collapsed;
        _activePlaybackLyric?.IsActive = false;
        _activePlaybackLyric = null;
        _lastAutoScrolledLyric = null;
        _lyricFollowBlockedUntil = DateTimeOffset.MinValue;

        try
        {
            AppWindow.SetPresenter(_previousPresenterKind);
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not restore window presenter after immersive mode.",
                error);
        }
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

            if (!updatePalette)
            {
                return;
            }

            byte[]? imageBytes =
                ReferenceEquals(item, _selectedLibraryItem)
                    ? _currentArtworkBytes
                    : null;
            if (imageBytes is null && _settings is MusicLoomSettings settings)
            {
                imageBytes = await Task.Run(
                    () => LoadTrackDetail(settings, item.Entry).Artwork);
            }
            if (_immersiveActive && version == _immersiveLoadVersion)
            {
                await ImmersiveView.ApplyArtworkColorsAsync(imageBytes);
            }
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not refresh immersive lyrics or artwork.", error);
        }
    }
}
