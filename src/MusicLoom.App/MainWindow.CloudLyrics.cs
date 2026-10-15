using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicLoom.Audio.Windows;
using MusicLoom.Core.Lyrics;
using MusicLoom.LyricsProviders;
using MusicLoom.Metadata;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace MusicLoom.App;

/// <summary>
/// All providers use MusicLoom's existing in-app modal. QQ and KuGou
/// lyrics share the NetEase three-column interaction without creating a
/// second OS window or separate cache. Automatic QQ/KuGou matching below
/// is explicitly metadata-based, never advertised as fingerprinting.
/// </summary>
public sealed partial class MainWindow
{
    // The current QQ/KuGou preview belongs to the existing player, never
    // the local library queue or the NetEase song preview state.
    private CloudLyricsCandidate? _playingCloudPreviewCandidate;
    private CloudLyricsPanel? _activeCloudLyricsPanel;

    private static bool SameCloudCandidate(
        CloudLyricsCandidate? first, CloudLyricsCandidate? second) =>
        first is not null && second is not null
        && first.Source == second.Source
        && string.Equals(first.Id, second.Id, StringComparison.Ordinal)
        && string.Equals(first.SongMid, second.SongMid,
            StringComparison.Ordinal)
        && string.Equals(first.Hash, second.Hash, StringComparison.Ordinal);

    private void UpdateCloudPreviewVisualState()
    {
        _activeCloudLyricsPanel?.UpdatePreviewPlaying(
            _playingCloudPreviewCandidate,
            _mediaPlayer.PlaybackSession.PlaybackState
                == MediaPlaybackState.Playing);
    }

    private async void FindNeteaseMatchItem_Click(
        object sender, RoutedEventArgs e)
    {
        if (_selectedLibraryItem is LibraryDisplayItem item)
            await ShowMatchDialogAsync(item);
    }

    private async void FindQqMatchItem_Click(
        object sender, RoutedEventArgs e)
    {
        if (_selectedLibraryItem is LibraryDisplayItem item)
            await ShowCloudMatchDialogAsync(item, CloudLyricsSource.QqMusic);
    }

    private async void FindKugouMatchItem_Click(
        object sender, RoutedEventArgs e)
    {
        if (_selectedLibraryItem is LibraryDisplayItem item)
            await ShowCloudMatchDialogAsync(item, CloudLyricsSource.KugouMusic);
    }

    private async Task ShowCloudMatchDialogAsync(
        LibraryDisplayItem item, CloudLyricsSource source)
    {
        if (_busy) return;

        CloudLyricsPanel panel = new()
        {
            Source = source,
        };
        panel.Localize(L);
        if (source == CloudLyricsSource.KugouMusic)
        {
            panel.CaptureAudioPcm16 =
                (path, token) => new KugouPcmWindowDecoder()
                    .DecodeAsync(path, token);
        }

        panel.LyricsAccepted += CloudLyricsPanel_LyricsAccepted;
        panel.PreviewRequested += async candidate =>
            await PlayCloudPreviewAsync(candidate, panel);
        panel.LyricSeekRequested += async timestamp =>
        {
            // Double-click seeks the remote candidate shown in the same
            // lyrics preview, not the unrelated local source file.
            if (panel.SelectedCandidate is CloudLyricsCandidate selected)
                await PlayCloudPreviewAsync(selected, panel, timestamp);
        };

        double? durationMs = null;
        try
        {
            durationMs = await Task.Run(
                () => AtlTrackInspector.ReadDurationMs(item.Entry.SourcePath));
        }
        catch (Exception)
        {
            // A locked or unknown audio format must not block text search.
        }

        panel.SetTrack(item.Entry.SourcePath,
            item.Artist, item.Title, item.Album, durationMs);

        CloseMatchOverlay();
        _activeCloudLyricsPanel = panel;
        UpdateCloudPreviewVisualState();
        ShowAppOverlay(
            source == CloudLyricsSource.QqMusic
                ? L("Find QQ Music lyrics", "查找 QQ 音乐歌词")
                : L("Find KuGou Music lyrics", "查找酷狗音乐歌词"),
            panel,
            maxWidth: 1200,
            maxHeight: 640,
            onClosed: () =>
            {
                if (ReferenceEquals(_activeCloudLyricsPanel, panel))
                    _activeCloudLyricsPanel = null;
                panel.Dispose();
            });
    }

    private async Task PlayCloudPreviewAsync(
        CloudLyricsCandidate candidate,
        CloudLyricsPanel originatingPanel,
        TimeSpan? startPosition = null)
    {
        // The same shared playback session is used for all three providers.
        // Pause/resume and lyric seeking must not trigger a second network
        // request for the same already-authorized stream.
        if (SameCloudCandidate(_playingCloudPreviewCandidate, candidate)
            && _mediaPlayer.Source is not null)
        {
            try
            {
                if (startPosition is TimeSpan seek)
                {
                    await SeekPlayerWhenReadyAsync(seek);
                    _mediaPlayer.Play();
                    UpdatePlaybackLyrics();
                }
                else if (_mediaPlayer.PlaybackSession.PlaybackState
                    == MediaPlaybackState.Playing)
                {
                    _mediaPlayer.Pause();
                }
                else
                {
                    _mediaPlayer.Play();
                }
                UpdatePlayerVisualState();
            }
            catch (Exception error)
            {
                AppDiagnostics.LogException(
                    "Could not seek/resume remote provider stream.", error);
                originatingPanel.SetPreviewStatus(L(
                    "Could not resume remote preview.",
                    "无法继续播放远程试听。"));
            }
            return;
        }

        CancellationTokenSource transition = BeginPlaybackTransition();
        try
        {
            originatingPanel.SetPreviewStatus(L(
                "Requesting remote stream from provider…",
                "正在从音源获取远程试听地址……"));

            using CloudLyricsClient client = new();
            CloudAudioPreviewResult preview =
                await client.GetAudioPreviewAsync(candidate, transition.Token);
            transition.Token.ThrowIfCancellationRequested();

            // Search panel may be closed while the network request is
            // running. Do not unexpectedly start playback after it closes.
            if (!ReferenceEquals(_activeCloudLyricsPanel, originatingPanel))
                return;

            if (!preview.CanPlay)
            {
                string why = preview.Status switch
                {
                    CloudAudioPreviewStatus.InvalidCandidate => L(
                        "The result lacks a valid playback identifier.",
                        "该搜索结果缺少有效的音频播放标识。"),
                    CloudAudioPreviewStatus.ProviderUnavailable => L(
                        "The remote audio service is unavailable.",
                        "远程音频服务暂时不可用。"),
                    _ => L(
                        "No public stream for this song (login, membership or copyright restriction).",
                        "该歌曲没有游客可播放的音频地址（可能需要登录、会员权限或受版权限制）。"),
                };
                originatingPanel.SetPreviewStatus(why);
                ShowInfo(InfoBarSeverity.Warning,
                    L("Remote preview unavailable", "远程试听不可用"), why);
                return;
            }

            if (_mediaPlayer.Source is not null
                && _mediaPlayer.Volume > 0.001)
            {
                await FadePlayerVolumeAsync(
                    _mediaPlayer.Volume, fadeIn: false, transition.Token);
            }
            transition.Token.ThrowIfCancellationRequested();

            _playingLibraryItem?.SetPlaying(false);
            _playingLibraryItem = null;
            _playingPreviewCandidate = null;
            _playingCloudPreviewCandidate = candidate;

            _waveformCancellation?.Cancel();
            _playingAudioFrames = null;
            _smoothedAudioFrame = null;
            _playbackQueue = [];
            _playbackQueueIndex = -1;
            RefreshNowPlayingPlaylist();

            _activePlaybackLyric?.IsActive = false;
            _activePlaybackLyric = null;
            _suppressPlaybackLyricFollow = startPosition.HasValue;

            _mediaPlayer.Volume = 0;
            _mediaPlayer.Source = MediaSource.CreateFromUri(
                preview.StreamUrl!);
            PlayerTitleText.Text = string.IsNullOrWhiteSpace(candidate.Artist)
                ? candidate.Title
                : candidate.Artist + " - " + candidate.Title;
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

            if (startPosition is TimeSpan position)
                await SeekPlayerWhenReadyAsync(position);

            transition.Token.ThrowIfCancellationRequested();
            _mediaPlayer.Play();
            await WaitForPlaybackStartAsync(transition.Token);
            await FadePlayerVolumeAsync(0, fadeIn: true, transition.Token);
            _suppressPlaybackLyricFollow = false;

            UpdatePlayerVisualState();
            UpdatePlaybackLyrics();
            originatingPanel.SetPreviewStatus(L(
                "Playing the provider's remote audio stream.",
                "正在播放音源提供的远程音频。"));
        }
        catch (OperationCanceledException) when (
            transition.IsCancellationRequested)
        {
            // Rapidly selecting a new result is normal, not an error.
        }
        catch (Exception error)
        {
            _suppressPlaybackLyricFollow = false;
            AppDiagnostics.LogException(
                "QQ/KuGou remote audio preview failed.", error);
            string message = L(
                "Remote preview failed. The stream may be expired or blocked by the provider.",
                "远程试听失败：音频地址可能已过期，或被音源限制访问。");
            originatingPanel.SetPreviewStatus(message);
            ShowInfo(InfoBarSeverity.Warning,
                L("Remote preview failed", "远程试听失败"), message);
        }
        finally
        {
            CompletePlaybackTransition(transition);
            UpdatePlayerVisualState();
        }
    }

    private async void CloudLyricsPanel_LyricsAccepted(
        object? sender, CloudLyricsAcceptedEventArgs e)
    {
        if (_busy || _lyricsEditInProgress
            || _selectedLibraryItem is not LibraryDisplayItem item
            || !string.Equals(e.SourcePath, item.Entry.SourcePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            // The explicit choice is canonical in SQLite; NO destination
            // audio/tag/sidecar changes occur before the next sync.
            LibraryLyricsRecord manual = new(
                e.SourcePath,
                LibraryLyricsOrigin.ManualEdited,
                e.Document.ToString(),
                null,
                e.Candidate.Source == CloudLyricsSource.QqMusic
                    ? "manual-qq:" + e.Candidate.Id
                    : "manual-kugou:" + e.Candidate.Id,
                DateTimeOffset.UtcNow);
            await SaveManualLyricsAsync(item, manual);
            CloseMatchOverlay();
            await ShowTrackDetailsAsync(item);
            await RefreshCloudMatchTabsAsync(item);

            ShowInfo(InfoBarSeverity.Success,
                L("Lyrics selected", "已采用歌词"),
                L("Lyrics saved to SQLite. Synchronize to update destination files.",
                  "歌词已写入 SQLite。执行同步后才会更新目标文件。"));
        }
        catch (Exception error)
        {
            HandleUiException(
                L("Could not save selected lyrics.", "无法保存所选歌词。"),
                error);
        }
    }

    private async void AutoMatchNeteaseItem_Click(
        object sender, RoutedEventArgs e) => await RunAutomaticMatchFromMenuAsync(
            CloudLyricsSource.QqMusic, netease: true);

    private async void AutoMatchQqItem_Click(
        object sender, RoutedEventArgs e) => await RunAutomaticMatchFromMenuAsync(
            CloudLyricsSource.QqMusic, netease: false);

    private async void AutoMatchKugouItem_Click(
        object sender, RoutedEventArgs e) => await RunAutomaticMatchFromMenuAsync(
            CloudLyricsSource.KugouMusic, netease: false);

    private async Task RunAutomaticMatchFromMenuAsync(
        CloudLyricsSource source, bool netease)
    {
        if (_busy) return;
        if (_plan is null && !await ScanLibraryAsync(
                clearInfoBar: true, recordActivity: true))
        {
            return;
        }

        if (netease)
        {
            await RunAutomaticNeteaseMatchAsync();
            return;
        }

        await RunAutomaticProviderMetadataMatchAsync(source);
    }

    private static string CanonicalMatchKey(string? text) =>
        new string((text ?? "").Normalize(
                System.Text.NormalizationForm.FormKC)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static bool ExactProviderMetadataMatch(
        LibraryDisplayItem item, CloudLyricsCandidate candidate)
    {
        string title = CanonicalMatchKey(item.Title);
        string artist = CanonicalMatchKey(item.Artist);
        string otherTitle = CanonicalMatchKey(candidate.Title);
        string otherArtist = CanonicalMatchKey(candidate.Artist);
        return title.Length > 0 && artist.Length > 0
            && title == otherTitle
            && otherArtist.Length > 0
            && (artist == otherArtist
                || (artist.Length > 3
                    && otherArtist.Contains(artist, StringComparison.Ordinal)));
    }

    private async Task RunAutomaticProviderMetadataMatchAsync(
        CloudLyricsSource source)
    {
        if (_busy || _settings is null)
        {
            return;
        }

        string provider = source == CloudLyricsSource.QqMusic
            ? "QQ Music" : "KuGou Music";
        LibraryDisplayItem[] snapshot = _allLibraryItems.ToArray();

        using CancellationTokenSource cancellation = new();
        _operationCancellation = cancellation;
        SetBusy(true,
            L($"Searching {provider} by metadata…",
              $"正在通过{(source == CloudLyricsSource.QqMusic ? "QQ 音乐" : "酷狗音乐")}元数据搜索……"));

        int updated = 0, ambiguous = 0, failures = 0;
        try
        {
            using CloudLyricsClient client = new();
            SqliteLyricsCache cache = new(DatabasePath);

            for (int i = 0; i < snapshot.Length; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                LibraryDisplayItem item = snapshot[i];
                SetProgress(i, snapshot.Length);
                SetOperationStatus(L(
                    $"{provider} metadata {i+1:N0}/{snapshot.Length:N0} · {item.Title}",
                    $"{provider} 元数据匹配 {i+1:N0}/{snapshot.Length:N0} · {item.Title}"));

                try
                {
                    LibraryLyricsRecord? canonical =
                        await cache.GetLibraryAsync(
                            item.Entry.SourcePath, cancellation.Token);
                    // Never overwrite manual choices, local lyrics, or any
                    // existing canonical document during an automatic search.
                    if (canonical?.HasLyrics == true
                        || canonical?.Origin == LibraryLyricsOrigin.ManualEmpty)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(item.Title)
                        || string.IsNullOrWhiteSpace(item.Artist))
                    {
                        continue;
                    }

                    IReadOnlyList<CloudLyricsCandidate> found =
                        await client.SearchAsync(source,
                            item.Artist + " " + item.Title,
                            cancellation.Token);
                    CloudLyricsCandidate[] strict = found
                        .Where(candidate =>
                            ExactProviderMetadataMatch(item, candidate))
                        .ToArray();
                    if (strict.Length != 1)
                    {
                        if (strict.Length > 1) ambiguous++;
                        continue;
                    }

                    LrcDocument? lyrics =
                        await client.GetLyricsAsync(strict[0],
                            cancellation.Token);
                    if (lyrics?.Lines.Count is not > 0)
                        continue;

                    await cache.SaveLibraryAsync(new LibraryLyricsRecord(
                        item.Entry.SourcePath,
                        source == CloudLyricsSource.QqMusic
                            ? LibraryLyricsOrigin.AutoQq
                            : LibraryLyricsOrigin.AutoKugou,
                        lyrics.ToString(),
                        null,
                        (source == CloudLyricsSource.QqMusic
                            ? "auto-qq:" : "auto-kugou:") + strict[0].Id,
                        DateTimeOffset.UtcNow),
                        cancellation.Token);
                    await QueueLyricOutputSyncAsync(item);
                    updated++;

                    if (ReferenceEquals(item, _selectedLibraryItem))
                    {
                        _currentLyricsRaw = lyrics.ToString();
                        RenderLyrics();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    failures++;
                    AppDiagnostics.LogException(
                        $"Automatic {provider} metadata search failed for {item.Entry.SourcePath}.",
                        error);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // User cancellation is normal and keeps all finished records.
        }
        finally
        {
            _operationCancellation = null;
            SetBusy(false,
                L("Metadata search finished.", "元数据匹配已结束。"));
        }

        ShowInfo(
            failures > 0 ? InfoBarSeverity.Warning
                         : InfoBarSeverity.Informational,
            L($"{provider} metadata matching", $"{provider} 元数据匹配"),
            L(
                $"{updated} lyrics selected with exact metadata; {ambiguous} ambiguous matches skipped; {failures} errors. No audio fingerprint was used.",
                $"精确元数据匹配成功 {updated} 首，跳过歧义结果 {ambiguous} 个，错误 {failures} 个。没有使用音频指纹识别。"));
    }
}
