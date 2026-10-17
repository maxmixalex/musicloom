using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicLoom.LyricsProviders;
using MusicLoom.ProviderPoc;

namespace MusicLoom.App;

/// <summary>
/// Optional REAL Spotify audio on Windows, decoded by a separately installed
/// go-librespot process with WASAPI. No remote device substitution, no
/// guessed media URL and no redistribution of the GPL sidecar.
/// </summary>
public sealed partial class MainWindow
{
    private SpotifyLocalStreamBridge? _spotifyLocalBackend;
    private CloudLyricsCandidate? _spotifyLocalTrack;
    private SpotifyLocalPlayerStatus? _spotifyLocalStatus;
    private DateTimeOffset _spotifyLocalLastPoll = DateTimeOffset.MinValue;
    private bool _spotifyLocalPolling;

    private bool SpotifyLocalTrackPlaying =>
        _spotifyLocalBackend is not null
        && _spotifyLocalTrack is CloudLyricsCandidate selected
        && _spotifyLocalStatus is SpotifyLocalPlayerStatus current
        && current.IsPlaying
        && string.Equals(current.TrackUri, "spotify:track:" + selected.Id,
            StringComparison.Ordinal);

    private bool SpotifyLocalHasTrack =>
        _spotifyLocalBackend is not null && _spotifyLocalTrack is not null;

    private TimeSpan CurrentPlayerPosition =>
        SpotifyLocalHasTrack
            ? TimeSpan.FromMilliseconds(Math.Max(0,
                _spotifyLocalStatus?.PositionMilliseconds ?? 0))
            : _mediaPlayer.PlaybackSession.Position;

    private TimeSpan CurrentPlayerDuration =>
        SpotifyLocalHasTrack
            ? TimeSpan.FromMilliseconds(
                Math.Max(0, _spotifyLocalTrack!.DurationMilliseconds))
            : _mediaPlayer.PlaybackSession.NaturalDuration;

    private async Task StopSpotifyLocalPlaybackAsync()
    {
        SpotifyLocalStreamBridge? current = _spotifyLocalBackend;
        if (current is null) return;
        _spotifyLocalBackend = null;
        _spotifyLocalTrack = null;
        _spotifyLocalStatus = null;
        try
        {
            await current.PauseAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException
            or InvalidDataException or OperationCanceledException)
        {
            AppDiagnostics.LogException(
                "Could not pause local Spotify daemon while switching sources.", ex);
        }
        finally
        {
            current.Dispose();
        }
    }

    /// <summary>
    /// Spotify is handled directly by the local streaming daemon, not a fake
    /// MediaPlayer URI. This is an explicit user click from the shared match
    /// dialog. If it cannot reach an authenticated local daemon it reports
    /// that condition without changing the playing MusicLoom audio source.
    /// </summary>
    private async Task<bool> PlaySpotifyLocalAsync(
        CloudLyricsCandidate candidate, CloudLyricsPanel panel,
        TimeSpan? startPosition)
    {
        if (candidate.Source != CloudLyricsSource.Spotify) return false;
        SpotifyLocalStreamBridge? current = _spotifyLocalBackend;
        bool owned = false;
        if (current is null)
        {
            try
            {
                string endpoint = Environment.GetEnvironmentVariable(
                    "MUSICLOOM_GO_LIBRESPOT_URL")
                    ?? "http://127.0.0.1:3678";
                current = new SpotifyLocalStreamBridge(endpoint);
                owned = true;
            }
            catch (ArgumentException ex)
            {
                panel.SetPreviewStatus(L(
                    "Invalid local Spotify player address. Only HTTP loopback is permitted.",
                    "本地 Spotify 播放服务地址无效，仅允许 HTTP 回环地址。"));
                AppDiagnostics.LogException("Invalid go-librespot URL.", ex);
                return true;
            }
        }

        try
        {
            // Same candidate: the icon toggles actual WASAPI playback, while
            // lyric double-click seeks; no new stream or API request occurs.
            if (SpotifyLocalHasTrack
                && SameCloudCandidate(_spotifyLocalTrack, candidate))
            {
                if (startPosition is TimeSpan seek)
                    await current.SeekAsync((int)Math.Clamp(
                        seek.TotalMilliseconds, 0, int.MaxValue));
                else if (SpotifyLocalTrackPlaying)
                    await current.PauseAsync();
                else
                    await current.ResumeAsync();
                _spotifyLocalLastPoll = DateTimeOffset.MinValue;
                _ = PollSpotifyLocalStatusAsync();
                return true;
            }

            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(4));
            if (!await current.IsReadyAsync(timeout.Token))
            {
                panel.SetPreviewStatus(L(
                    "Local go-librespot is not authenticated/ready. Start it with its Windows WASAPI backend.",
                    "本机 go-librespot 尚未登录或就绪；请先启动其 Windows WASAPI 播放服务。"));
                return true;
            }
            if (!await current.PlayTrackAsync(candidate.Id, timeout.Token))
            {
                panel.SetPreviewStatus(L(
                    "Local Spotify player refused the song.",
                    "本地 Spotify 播放服务拒绝了这首歌曲。"));
                return true;
            }
            if (_activeCloudLyricsPanel != panel) return true;

            CancellationTokenSource transition = BeginPlaybackTransition();
            try
            {
                _mediaPlayer.Pause();
                _mediaPlayer.Source = null;
                _playingLibraryItem?.SetPlaying(false);
                _playingLibraryItem = null;
                _playingPreviewCandidate = null;
                _playingCloudPreviewCandidate = candidate;
                _spotifyLocalBackend = current;
                _spotifyLocalTrack = candidate;
                _spotifyLocalStatus = new SpotifyLocalPlayerStatus(
                    "spotify:track:" + candidate.Id, false, false, true, 0);
                owned = false;
                _spotifyLocalLastPoll = DateTimeOffset.MinValue;
                ResetRemotePlaybackLyrics();
                _remotePlaybackLyrics = SameCloudCandidate(
                    panel.SelectedCandidate, candidate)
                        ? panel.SelectedDocument : null;
                _waveformCancellation?.Cancel();
                _playingAudioFrames = null;
                _smoothedAudioFrame = null;
                _playbackQueue = [];
                _playbackQueueIndex = -1;
                RefreshNowPlayingPlaylist();
                _activePlaybackLyric?.IsActive = false;
                _activePlaybackLyric = null;

                UpdatePlayerSourceLabel();
                SetCloudTrackAppearance(candidate);
                if (_immersiveActive) UpdateImmersiveRemoteTrack();
                if (_remotePlaybackLyrics is null)
                    _ = LoadCloudPreviewLyricsAsync(
                        candidate, _remotePlaybackLyricsVersion);
                if (startPosition is TimeSpan position)
                    await current.SeekAsync((int)Math.Clamp(
                        position.TotalMilliseconds, 0, int.MaxValue));
                panel.SetPreviewStatus(L(
                    "Spotify track sent to the local decoder. Checking actual playback status…",
                    "歌曲已发送到本机 Spotify 解码器，正在核实实际播放状态……"));
                UpdatePlayerVisualState();
                await PollSpotifyLocalStatusAsync();
            }
            finally
            {
                CompletePlaybackTransition(transition);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException
            or InvalidDataException or OperationCanceledException)
        {
            panel.SetPreviewStatus(L(
                "Local Spotify audio is unavailable. Start the authorized go-librespot player and try again.",
                "本机 Spotify 音频不可用，请启动已授权的 go-librespot 播放服务。"));
            AppDiagnostics.LogException("Local Spotify stream failed.", ex);
        }
        finally
        {
            if (owned) current.Dispose();
        }
        return true;
    }

    private async Task PollSpotifyLocalStatusAsync()
    {
        SpotifyLocalStreamBridge? backend = _spotifyLocalBackend;
        if (backend is null || _spotifyLocalPolling) return;
        _spotifyLocalPolling = true;
        _spotifyLocalLastPoll = DateTimeOffset.UtcNow;
        try
        {
            using CancellationTokenSource timeout =
                new(TimeSpan.FromSeconds(2));
            SpotifyLocalPlayerStatus? state =
                await backend.GetStatusAsync(timeout.Token);
            if (!ReferenceEquals(backend, _spotifyLocalBackend))
                return;
            _spotifyLocalStatus = state;
            if (_spotifyLocalTrack is CloudLyricsCandidate selected
                && state is not null)
            {
                bool sameTrack = string.Equals(state.TrackUri,
                    "spotify:track:" + selected.Id, StringComparison.Ordinal);
                _activeCloudLyricsPanel?.SetPreviewStatus(sameTrack
                    ? state.IsPlaying
                        ? L("Playing Spotify audio locally through WASAPI.",
                            "正通过本机 WASAPI 播放 Spotify 完整音频。")
                        : L("Spotify player is paused or buffering.",
                            "Spotify 播放器已暂停或正在缓冲。")
                    : L("Local Spotify player switched to another song.",
                        "本地 Spotify 播放服务已切换至其他歌曲。"));
            }
            UpdatePlayerVisualState();
            UpdatePlayerProgress();
            UpdatePlaybackLyrics();
        }
        catch (Exception ex) when (ex is HttpRequestException
            or InvalidDataException or OperationCanceledException)
        {
            if (ReferenceEquals(backend, _spotifyLocalBackend))
                _spotifyLocalStatus = null;
            AppDiagnostics.LogException(
                "Could not read local Spotify playback status.", ex);
        }
        finally
        {
            _spotifyLocalPolling = false;
        }
    }

    private void ScheduleSpotifyLocalStatusPoll()
    {
        if (!SpotifyLocalHasTrack || _spotifyLocalPolling
            || DateTimeOffset.UtcNow - _spotifyLocalLastPoll
                < TimeSpan.FromMilliseconds(450))
            return;
        _ = PollSpotifyLocalStatusAsync();
    }

    private async Task ToggleSpotifyLocalAsync()
    {
        SpotifyLocalStreamBridge? backend = _spotifyLocalBackend;
        if (backend is null) return;
        try
        {
            if (SpotifyLocalTrackPlaying)
                await backend.PauseAsync();
            else
                await backend.ResumeAsync();
            _spotifyLocalLastPoll = DateTimeOffset.MinValue;
            await PollSpotifyLocalStatusAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException
            or InvalidDataException or OperationCanceledException)
        {
            AppDiagnostics.LogException(
                "Spotify local play/pause failed.", ex);
        }
    }

    private bool SeekSpotifyLocal(TimeSpan position)
    {
        SpotifyLocalStreamBridge? backend = _spotifyLocalBackend;
        if (backend is null) return false;
        _ = SeekSpotifyLocalAsync(backend, position);
        return true;
    }

    private async Task SeekSpotifyLocalAsync(
        SpotifyLocalStreamBridge backend, TimeSpan position)
    {
        try
        {
            await backend.SeekAsync((int)Math.Clamp(
                position.TotalMilliseconds, 0, int.MaxValue));
            _spotifyLocalLastPoll = DateTimeOffset.MinValue;
            await PollSpotifyLocalStatusAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException
            or InvalidDataException or OperationCanceledException)
        {
            AppDiagnostics.LogException("Spotify local seek failed.", ex);
        }
    }

    private void SetCloudTrackAppearance(CloudLyricsCandidate candidate)
    {
        PlayerTitleText.Text = string.IsNullOrWhiteSpace(candidate.Artist)
            ? candidate.Title : candidate.Artist + " - " + candidate.Title;
        PlayerMetadataText.Text = candidate.Album;
        if (candidate.CoverUrl is string artwork
            && Uri.TryCreate(artwork, UriKind.Absolute, out Uri? artUri)
            && artUri.Scheme == Uri.UriSchemeHttps)
        {
            PlayerArtworkImage.Source = new BitmapImage
            {
                DecodePixelWidth = 144,
                UriSource = artUri,
            };
            PlayerArtworkPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            PlayerArtworkImage.Source = null;
            PlayerArtworkPlaceholder.Visibility = Visibility.Visible;
        }
        ConfigureAuxiliaryLongText();
    }
}
