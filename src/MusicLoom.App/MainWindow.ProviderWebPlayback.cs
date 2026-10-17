using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

/// <summary>
/// Actual first-party WinUI hosting of Spotify/Apple site playback. The login
/// and media engine live inside MusicLoom's WebView2, with account cookies
/// managed by its isolated browser profile. No executable sidecar, shell,
/// localhost daemon, pasted OAuth key, or borrowed login cookie is required.
/// This is a provider-site-backed player, NOT a native PCM decoder.
/// </summary>
public sealed partial class MainWindow
{
    private CloudLyricsCandidate? _providerWebTrack;
    private bool _providerWebPlaying;
    private TimeSpan _providerWebPosition;
    private TimeSpan _providerWebDuration;
    private TimeSpan? _providerWebDeferredSeek;
    private DateTimeOffset _providerWebLastPoll = DateTimeOffset.MinValue;
    private bool _providerWebPolling;
    private int _providerWebVersion;

    private TimeSpan CurrentPlayerPosition =>
        ProviderWebHasTrack ? ProviderWebPosition : _mediaPlayer.PlaybackSession.Position;

    private TimeSpan CurrentPlayerDuration =>
        ProviderWebHasTrack ? ProviderWebDuration : _mediaPlayer.PlaybackSession.NaturalDuration;

    private bool ProviderWebHasTrack => _providerWebTrack is not null;
    private bool ProviderWebIsPlaying => ProviderWebHasTrack && _providerWebPlaying;
    private TimeSpan ProviderWebPosition => _providerWebPosition;
    private TimeSpan ProviderWebDuration => _providerWebDuration > TimeSpan.Zero
        ? _providerWebDuration
        : TimeSpan.FromMilliseconds(Math.Max(0,
            _providerWebTrack?.DurationMilliseconds ?? 0));

    private void InitializeProviderWebPlayback()
    {
        // Browser initializes only on a deliberate user playback action.
        ProviderWebMinimizeButton.Content = L("Hide", "收起");
        ProviderWebStopButton.Content = L("Stop", "停止");
        ProviderWebView.NavigationCompleted += (_, args) =>
        {
            if (!ProviderWebHasTrack) return;
            ProviderWebStatus.Text = args.IsSuccess
                ? L("Sign in or play inside MusicLoom. The native toolbar controls standard web audio when available.",
                    "在 Musicloom 内登录或播放。若音源支持标准网页音频控件，也可使用下方播放栏。")
                : L("Could not load the provider's playback page.",
                    "无法加载音源的播放页面。");
        };
    }

    private static Uri? ProviderWebSongUrl(CloudLyricsCandidate candidate)
    {
        if (candidate.Source is not
            (CloudLyricsSource.Spotify or CloudLyricsSource.AppleMusic))
            return null;
        Uri? url = CloudLyricsSourceNames.SongPage(candidate);
        if (url is not null) return url;
        if (candidate.Source == CloudLyricsSource.Spotify
            && candidate.Id.Length == 22
            && candidate.Id.All(char.IsAsciiLetterOrDigit))
            return new Uri("https://open.spotify.com/track/" + candidate.Id);
        return null;
    }

    private async Task PlayProviderWebAsync(
        CloudLyricsCandidate candidate, CloudLyricsPanel panel,
        TimeSpan? startPosition)
    {
        Uri? page = ProviderWebSongUrl(candidate);
        if (page is null)
        {
            panel.SetPreviewStatus(L("No verified provider playback page.",
                "没有可验证的在线播放页面。"));
            return;
        }
        if (ProviderWebHasTrack && SameCloudCandidate(_providerWebTrack, candidate))
        {
            ProviderWebOverlay.Visibility = Visibility.Visible;
            if (startPosition is TimeSpan t)
                await SeekProviderWebAsync(t);
            else
                await ToggleProviderWebAsync();
            return;
        }

        // Do not stop an existing song before proving that the in-app
        // browser can initialize on this particular Windows installation.
        try
        {
            ProviderWebOverlay.Visibility = Visibility.Visible;
            await ProviderWebView.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            ProviderWebOverlay.Visibility = Visibility.Collapsed;
            AppDiagnostics.LogException("WebView2 online playback initialization failed.", ex);
            panel.SetPreviewStatus(L("Windows online playback component is unavailable.",
                "当前 Windows 无法初始化软件内的在线播放组件。"));
            return;
        }

        StopProviderWebPlayback();
        _mediaPlayer.Pause();
        _mediaPlayer.Source = null;
        _playingLibraryItem?.SetPlaying(false);
        _playingLibraryItem = null;
        _playingPreviewCandidate = null;
        _playingCloudPreviewCandidate = candidate;
        ResetRemotePlaybackLyrics();
        _remotePlaybackLyrics = SameCloudCandidate(panel.SelectedCandidate, candidate)
            ? panel.SelectedDocument : null;
        _waveformCancellation?.Cancel();
        _playingAudioFrames = null;
        _smoothedAudioFrame = null;
        _playbackQueue = [];
        _playbackQueueIndex = -1;
        RefreshNowPlayingPlaylist();
        _activePlaybackLyric?.IsActive = false;
        _activePlaybackLyric = null;

        _providerWebTrack = candidate;
        _providerWebPlaying = false;
        _providerWebPosition = TimeSpan.Zero;
        _providerWebDuration = TimeSpan.Zero;
        _providerWebDeferredSeek = startPosition;
        _providerWebLastPoll = DateTimeOffset.MinValue;
        ++_providerWebVersion;
        SetCloudTrackAppearance(candidate);
        UpdatePlayerSourceLabel();
        ProviderWebTitle.Text =
            CloudLyricsSourceNames.English(candidate.Source) + " · " + candidate.Title;
        ProviderWebShowButton.Visibility = Visibility.Visible;
        ProviderWebOverlay.Visibility = Visibility.Visible;
        ProviderWebStatus.Text = L("Opening the built-in player…",
            "正在打开内置播放器……");
        ProviderWebView.Source = page;
        if (_immersiveActive) UpdateImmersiveRemoteTrack();
        if (_remotePlaybackLyrics is null)
            _ = LoadCloudPreviewLyricsAsync(candidate, _remotePlaybackLyricsVersion);
        UpdatePlayerProgress();
        UpdatePlayerVisualState();
        panel.SetPreviewStatus(L("In-app provider player opened. Sign in/play in the panel.",
            "已在软件内打开音源播放器，可直接在面板中登录和播放。"));
    }

    private void ProviderWebShow_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderWebHasTrack) ProviderWebOverlay.Visibility = Visibility.Visible;
    }

    private void ProviderWebMinimize_Click(object sender, RoutedEventArgs e) =>
        ProviderWebOverlay.Visibility = Visibility.Collapsed;

    private void ProviderWebStop_Click(object sender, RoutedEventArgs e) =>
        StopProviderWebPlayback();

    private void StopProviderWebPlayback()
    {
        if (_providerWebTrack is null) return;
        ++_providerWebVersion;
        _providerWebTrack = null;
        _providerWebPlaying = false;
        _providerWebPosition = TimeSpan.Zero;
        _providerWebDuration = TimeSpan.Zero;
        _providerWebDeferredSeek = null;
        ProviderWebOverlay.Visibility = Visibility.Collapsed;
        ProviderWebShowButton.Visibility = Visibility.Collapsed;
        try { ProviderWebView.CoreWebView2?.Navigate("about:blank"); }
        catch (InvalidOperationException) { }
        if (_playingLibraryItem is null && _playingPreviewCandidate is null)
        {
            _playingCloudPreviewCandidate = null;
            ResetRemotePlaybackLyrics();
            PlayerTitleText.Text = L("Nothing playing", "尚未播放");
            PlayerMetadataText.Text = "";
            PlayerArtworkImage.Source = null;
            PlayerArtworkPlaceholder.Visibility = Visibility.Visible;
            UpdatePlayerSourceLabel();
        }
        UpdatePlayerProgress();
        UpdatePlayerVisualState();
    }

    private const string WebMediaStateScript = """
        (() => {
          const a = Array.from(document.querySelectorAll('audio,video'))
            .find(x => x.readyState > 0);
          if (!a) return null;
          return {
            playing: !a.paused && !a.ended && a.readyState >= 2,
            position: Number.isFinite(a.currentTime) ? Math.max(0, a.currentTime) : 0,
            duration: Number.isFinite(a.duration) ? Math.max(0, a.duration) : 0
          };
        })()
        """;

    private async Task PollProviderWebAsync()
    {
        if (!ProviderWebHasTrack || _providerWebPolling
            || ProviderWebView.CoreWebView2 is null) return;
        _providerWebPolling = true;
        int version = _providerWebVersion;
        _providerWebLastPoll = DateTimeOffset.UtcNow;
        try
        {
            string json = await ProviderWebView.CoreWebView2
                .ExecuteScriptAsync(WebMediaStateScript);
            if (version != _providerWebVersion) return;
            using JsonDocument payload = JsonDocument.Parse(json);
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
            {
                _providerWebPlaying = false;
                UpdatePlayerVisualState();
                return;
            }
            JsonElement node = payload.RootElement;
            _providerWebPlaying = node.TryGetProperty("playing", out JsonElement p)
                && p.ValueKind == JsonValueKind.True;
            static TimeSpan Time(JsonElement root, string key)
            {
                if (root.TryGetProperty(key, out JsonElement e)
                    && e.ValueKind == JsonValueKind.Number
                    && e.TryGetDouble(out double s) && double.IsFinite(s)
                    && s >= 0 && s < 86400) return TimeSpan.FromSeconds(s);
                return TimeSpan.Zero;
            }
            _providerWebPosition = Time(node, "position");
            _providerWebDuration = Time(node, "duration");
            if (_providerWebDeferredSeek is TimeSpan seek
                && _providerWebDuration > TimeSpan.Zero)
            {
                _providerWebDeferredSeek = null;
                await SeekProviderWebAsync(seek);
            }
            UpdatePlayerProgress();
            UpdatePlayerVisualState();
            UpdatePlaybackLyrics();
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or JsonException or System.Runtime.InteropServices.COMException)
        {
            AppDiagnostics.LogException("Provider page media status unavailable.", ex);
            _providerWebPlaying = false;
        }
        finally { _providerWebPolling = false; }
    }

    private void ScheduleProviderWebPoll()
    {
        if (!ProviderWebHasTrack || _providerWebPolling
            || DateTimeOffset.UtcNow - _providerWebLastPoll
                < TimeSpan.FromMilliseconds(550)) return;
        _ = PollProviderWebAsync();
    }

    private async Task ToggleProviderWebAsync()
    {
        if (!ProviderWebHasTrack || ProviderWebView.CoreWebView2 is null) return;
        try
        {
            string response = await ProviderWebView.CoreWebView2.ExecuteScriptAsync("""
                (async () => {
                  const a = Array.from(document.querySelectorAll('audio,video'))
                    .find(x => x.readyState > 0);
                  if (!a) return false;
                  try { if (a.paused) await a.play(); else a.pause(); return true; }
                  catch { return false; }
                })()
                """);
            if (response != "true")
            {
                ProviderWebOverlay.Visibility = Visibility.Visible;
                ProviderWebStatus.Text = L("Use the service's playback controls in this window.",
                    "请在此窗口内使用音源自己的播放控件。");
            }
        }
        catch (InvalidOperationException) { ProviderWebOverlay.Visibility = Visibility.Visible; }
        _providerWebLastPoll = DateTimeOffset.MinValue;
        await PollProviderWebAsync();
    }

    private bool SeekProviderWeb(TimeSpan position)
    {
        if (!ProviderWebHasTrack) return false;
        _ = SeekProviderWebAsync(position);
        return true;
    }

    private async Task SeekProviderWebAsync(TimeSpan position)
    {
        if (ProviderWebView.CoreWebView2 is null) return;
        string secs = Math.Max(0, position.TotalSeconds)
            .ToString("R", CultureInfo.InvariantCulture);
        string script = """
            (() => { const a = Array.from(document.querySelectorAll('audio,video'))
              .find(x => x.readyState > 0);
              if (!a || !Number.isFinite(a.duration)) return false;
              a.currentTime =
            """ + secs + "; return true; })()";
        try
        {
            string answer = await ProviderWebView.CoreWebView2.ExecuteScriptAsync(script);
            if (answer != "true") ProviderWebOverlay.Visibility = Visibility.Visible;
        }
        catch (InvalidOperationException) { ProviderWebOverlay.Visibility = Visibility.Visible; }
    }

    private async Task SetProviderWebVolumeAsync(double fraction)
    {
        if (!ProviderWebHasTrack || ProviderWebView.CoreWebView2 is null) return;
        string value = Math.Clamp(fraction, 0, 1)
            .ToString("R", CultureInfo.InvariantCulture);
        string script = """
            (() => { const a = Array.from(document.querySelectorAll('audio,video'))
              .find(x => x.readyState > 0);
              if (!a) return false; a.volume =
            """ + value + "; return true; })()";
        try { await ProviderWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch (InvalidOperationException) { }
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
