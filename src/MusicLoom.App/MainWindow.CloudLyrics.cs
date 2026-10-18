using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicLoom.Audio.Windows;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Configuration;
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

    private async Task ShowCloudMatchDialogAsync(
        LibraryDisplayItem item, CloudLyricsSource source)
    {
        if (_busy) return;

        CloudLyricsPanel panel = new()
        {
            Source = source,
        };
        // No separately invented QQ/KuGou lyric layout: use the exact
        // library template previously reused by the approved NetEase dialog.
        panel.PreviewLyricsList.ItemTemplate = LyricsList.ItemTemplate;
        panel.PreviewLyricsList.ItemContainerStyle = LyricsList.ItemContainerStyle;
        panel.Localize(L);
        if (source == CloudLyricsSource.KugouMusic)
        {
            panel.CaptureAudioPcm16 =
                (path, token) => new KugouPcmWindowDecoder()
                    .DecodeAsync(path, token);
        }

        panel.MatchAccepted += CloudLyricsPanel_MatchAccepted;
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
            L("Find ", "查找")
                + L(CloudLyricsSourceNames.English(source),
                    CloudLyricsSourceNames.Chinese(source))
                + L(" match", "匹配"),
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
        if (candidate.Source is CloudLyricsSource.Spotify
            or CloudLyricsSource.AppleMusic)
        {
            originatingPanel.SetPreviewStatus(L(
                "This account-dependent provider is not supported: only anonymous sources are accepted.",
                "当前音源依赖账户，Musicloom 仅接受完全匿名的 Provider。"));
            return;
        }
        // Local audio and provider-granted media URLs share this MediaPlayer.
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
                string why = candidate.Source switch
                {
                    CloudLyricsSource.MiguMusic => L(
                        "Migu did not grant an HTTPS audio stream for this track. Subscription, copyright or location restrictions may apply.",
                        "咪咕未向当前歌曲授予 HTTPS 音频地址，可能受到会员、版权或地区限制。"),
                    _ => preview.Status switch
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
                    },
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
            ResetRemotePlaybackLyrics();
            _remotePlaybackLyrics = SameCloudCandidate(
                originatingPanel.SelectedCandidate, candidate)
                ? originatingPanel.SelectedDocument : null;
            UpdatePlayerSourceLabel();

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
            SetCloudTrackAppearance(candidate);
            if (_immersiveActive)
                UpdateImmersiveRemoteTrack();
            if (_remotePlaybackLyrics is null)
                _ = LoadCloudPreviewLyricsAsync(
                    candidate, _remotePlaybackLyricsVersion);

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
                "Playing the provider's remotely granted audio stream.",
                "正在播放音源实际授予的远程音频。"));
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
                "Remote music provider audio preview failed.", error);
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

    private async void CloudLyricsPanel_MatchAccepted(
        object? sender, CloudMatchAcceptedEventArgs e)
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
            // One provider-specific selection persists independently of all
            // lyric documents and of the other two providers' match records.
            // An explicit selection is confirmed, even without timed lyrics.
            SqliteProviderMatchStore matches = new(DatabasePath);
            await matches.SaveMatchAsync(
                e.SourcePath, e.Candidate, confirmed: true);
            InvalidateLibraryFacetSnapshot();

            if (e.Document is { Lines.Count: > 0 } document)
            {
                // Only the canonical database changes. Actual destination
                // tags/sidecars are controlled by synchronization.
                LibraryLyricsRecord manual = new(
                    e.SourcePath,
                    LibraryLyricsOrigin.ManualEdited,
                    document.ToString(),
                    null,
                    "manual-" + CloudLyricsSourceNames.Slug(
                        e.Candidate.Source) + ":" + e.Candidate.Id,
                    DateTimeOffset.UtcNow);
                await SaveManualLyricsAsync(item, manual);
            }

            CloseMatchOverlay();
            await ShowTrackDetailsAsync(item);
            await RefreshCloudMatchTabsAsync(item);

            ShowInfo(InfoBarSeverity.Success,
                L("Match selected", "已采用匹配"),
                e.Document is { Lines.Count: > 0 }
                    ? L("The match and lyrics have been saved.",
                        "匹配及歌词已保存。")
                    : L("The match is saved; timed lyrics are not available yet.",
                        "匹配已保存，暂未找到时间轴歌词。"));
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

    private async void AutoMatchMiguItem_Click(
        object sender, RoutedEventArgs e) => await RunAutomaticMatchFromMenuAsync(
            CloudLyricsSource.MiguMusic, netease: false);

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

        await RunAutomaticProviderMatchAsync(source);
    }

    private static string CanonicalMatchKey(string? text) =>
        new string((text ?? "").Normalize(
                System.Text.NormalizationForm.FormKC)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static string ProviderSearchKey(LibraryDisplayItem item) =>
        CanonicalMatchKey(item.Artist) + "\u001F"
        + CanonicalMatchKey(item.Title);

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

    private sealed record CloudAutomaticLookup(
        CloudLyricsCandidate? Match, bool Recognized, bool Ambiguous);

    // KuGou's PCM fingerprinting may be costly and involve multiple HTTP
    // requests. Each worker owns decoder, recognizer and HTTP client.
    // Nothing here changes UI, canonical lyrics or persisted match records.
    private async Task<CloudAutomaticLookup> LookUpKugouAsync(
        LibraryDisplayItem item, CancellationToken ct)
    {
        using CloudLyricsClient client = new();
        CloudLyricsCandidate? chosen = null;
        try
        {
            KugouPcmWindowDecoder decoder = new();
            using KugouAudioRecognitionClient recognizer = new();
            byte[]? pcm = await decoder.DecodeAsync(
                item.Entry.SourcePath, ct);
            if (pcm is { Length: > 0 })
            {
                IReadOnlyList<KugouRecognitionCandidate> identified =
                    await recognizer.RecognizeAsync(pcm, ct);
                foreach (KugouRecognitionCandidate hit
                    in identified.OrderByDescending(
                        static entry => entry.Confidence))
                {
                    if (string.IsNullOrWhiteSpace(hit.Title)
                        || string.IsNullOrWhiteSpace(hit.Artist))
                        continue;
                    IReadOnlyList<CloudLyricsCandidate> results =
                        await client.SearchAsync(CloudLyricsSource.KugouMusic,
                            hit.Artist + " " + hit.Title, ct);
                    CloudLyricsCandidate[] hashes = results.Where(candidate =>
                        !string.IsNullOrWhiteSpace(hit.AudioHash)
                        && string.Equals(candidate.Hash,
                            hit.AudioHash, StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    CloudLyricsCandidate[] exact = hashes.Length > 0
                        ? hashes
                        : results.Where(candidate =>
                            CanonicalMatchKey(candidate.Title)
                                == CanonicalMatchKey(hit.Title)
                            && CanonicalMatchKey(candidate.Artist)
                                == CanonicalMatchKey(hit.Artist))
                            .ToArray();
                    if (exact.Length == 1)
                    {
                        chosen = exact[0];
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "KuGou fingerprint unavailable; using metadata fallback.",
                error);
        }

        if (chosen is not null)
            return new CloudAutomaticLookup(chosen, true, false);
        if (string.IsNullOrWhiteSpace(item.Title)
            || string.IsNullOrWhiteSpace(item.Artist))
            return new CloudAutomaticLookup(null, false, false);
        IReadOnlyList<CloudLyricsCandidate> found =
            await client.SearchAsync(CloudLyricsSource.KugouMusic,
                item.Artist + " " + item.Title, ct);
        CloudLyricsCandidate[] strict = found.Where(candidate =>
            ExactProviderMetadataMatch(item, candidate)).ToArray();
        return new CloudAutomaticLookup(
            strict.Length == 1 ? strict[0] : null,
            false, strict.Length > 1);
    }

    private async Task RunAutomaticProviderMatchAsync(
        CloudLyricsSource source)
    {
        if (_busy || _settings is null)
        {
            return;
        }

        string provider = source switch
        {
            CloudLyricsSource.QqMusic => "QQ Music",
            CloudLyricsSource.KugouMusic => "KuGou Music",
            CloudLyricsSource.MiguMusic => "Migu Music",
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        LibraryDisplayItem[] snapshot = _allLibraryItems.ToArray();

        using CancellationTokenSource cancellation = new();
        _operationCancellation = cancellation;
        SetBusy(true, source == CloudLyricsSource.KugouMusic
            ? L("Recognizing KuGou audio, with metadata fallback…",
                "正在使用酷狗音频识曲，必要时回退元数据……")
            : source == CloudLyricsSource.MiguMusic
                ? L("Searching Migu Music by metadata…",
                    "正在按元数据搜索咪咕音乐……")
                : L("Searching QQ Music by metadata…", "正在按元数据搜索QQ音乐……"));

        int updated = 0, recognized = 0, ambiguous = 0, failures = 0;
        try
        {
            using CloudLyricsClient client = new();
            SqliteLyricsCache cache = new(DatabasePath);
            SqliteProviderMatchStore matchStore = new(DatabasePath);
            // Bulk snapshots avoid thousands of SQLite connections in a
            // large library. Explicit no-match records still take priority.
            var knownMatches = await matchStore.LoadSnapshotAsync(cancellation.Token);
            var knownLyrics = await cache.LoadLibrarySnapshotAsync(cancellation.Token);
            int concurrency = MatchingSettings.Normalize(
                _settings.Matching.MaxConcurrency);
            await using ProviderMetadataSearchPrefetch? metadataPrefetch =
                source is CloudLyricsSource.QqMusic or CloudLyricsSource.MiguMusic
                    ? new((query, token) =>
                        client.SearchAsync(source, query, token),
                        cancellation.Token, maxConcurrency: concurrency)
                    : null;
            await using BoundedMatchWorkQueue<CloudAutomaticLookup>? kugouWork =
                source == CloudLyricsSource.KugouMusic
                    ? new((position, token) =>
                        LookUpKugouAsync(snapshot[position], token),
                        cancellation.Token, concurrency)
                    : null;

            for (int i = 0; i < snapshot.Length; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                LibraryDisplayItem item = snapshot[i];
                for (int ahead = i;
                     ahead < Math.Min(snapshot.Length, i + concurrency * 2);
                     ahead++)
                {
                    LibraryDisplayItem next = snapshot[ahead];
                    if (knownMatches.TryGetValue(next.Entry.SourcePath,
                            out var existingSources)
                        && existingSources.TryGetValue(source,
                            out StoredProviderMatch? existing)
                        && (existing.Suppressed || existing.IsMatched))
                        continue;
                    if (kugouWork is not null)
                        kugouWork.Prefetch(ahead);
                    else if (metadataPrefetch is not null
                        && !string.IsNullOrWhiteSpace(next.Title)
                        && !string.IsNullOrWhiteSpace(next.Artist))
                        _ = metadataPrefetch.Prefetch(ProviderSearchKey(next),
                            next.Artist + " " + next.Title);
                }
                SetProgress(i, snapshot.Length);
                SetOperationStatus(
                    source == CloudLyricsSource.KugouMusic
                        ? L($"KuGou audio/fallback {i+1:N0}/{snapshot.Length:N0} · {item.Title}",
                            $"酷狗识曲/元数据回退 {i+1:N0}/{snapshot.Length:N0} · {item.Title}")
                        : source == CloudLyricsSource.MiguMusic
                            ? L($"Migu Music metadata {i+1:N0}/{snapshot.Length:N0} · {item.Title}",
                                $"咪咕音乐元数据匹配 {i+1:N0}/{snapshot.Length:N0} · {item.Title}")
                            : L($"QQ Music metadata {i+1:N0}/{snapshot.Length:N0} · {item.Title}",
                                $"QQ音乐元数据匹配 {i+1:N0}/{snapshot.Length:N0} · {item.Title}"));

                try
                {
                    // An explicit empty state or a previously selected
                    // match is independent of the canonical lyric source.
                    // Never re-match a provider after "No match".
                    StoredProviderMatch? previousMatch = null;
                    if (knownMatches.TryGetValue(item.Entry.SourcePath,
                            out var sourceMatches))
                        sourceMatches.TryGetValue(source, out previousMatch);
                    if (previousMatch?.Suppressed == true
                        || previousMatch?.IsMatched == true)
                        continue;

                    knownLyrics.TryGetValue(
                        item.Entry.SourcePath, out LibraryLyricsRecord? canonical);

                    CloudLyricsCandidate? chosen;
                    if (kugouWork is not null)
                    {
                        CloudAutomaticLookup lookup =
                            await kugouWork.TakeAsync(i);
                        chosen = lookup.Match;
                        if (lookup.Recognized) recognized++;
                        if (lookup.Ambiguous) ambiguous++;
                        if (chosen is null) continue;
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(item.Title)
                            || string.IsNullOrWhiteSpace(item.Artist))
                            continue;
                        IReadOnlyList<CloudLyricsCandidate> found =
                            await metadataPrefetch!.GetAsync(
                                ProviderSearchKey(item),
                                item.Artist + " " + item.Title);
                        CloudLyricsCandidate[] strict = found
                            .Where(candidate =>
                                ExactProviderMetadataMatch(item, candidate))
                            .ToArray();
                        if (strict.Length != 1)
                        {
                            if (strict.Length > 1) ambiguous++;
                            continue;
                        }
                        chosen = strict[0];
                    }

                    // Do not block each QQ auto-match on a second HTTP
                    // album-image lookup. Most QQ search results already
                    // carry an album MID; truly missing covers are hydrated
                    // by the selected match card in the background.
                    if (chosen.CoverUrl is null
                        && source != CloudLyricsSource.QqMusic)
                    {
                        using CancellationTokenSource coverBudget =
                            CancellationTokenSource.CreateLinkedTokenSource(
                                cancellation.Token);
                        coverBudget.CancelAfter(TimeSpan.FromSeconds(4));
                        try
                        {
                            string? artwork = await client.ResolveArtworkAsync(
                                chosen, coverBudget.Token);
                            if (artwork is not null)
                                chosen = chosen with { CoverUrl = artwork };
                        }
                        catch (OperationCanceledException) when (
                            cancellation.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception error) when (error is
                            OperationCanceledException or HttpRequestException
                            or InvalidDataException)
                        {
                            // A cover is optional; a successfully identified
                            // song must never be lost to an image/CDN outage.
                            AppDiagnostics.LogException(
                                "Optional provider artwork lookup failed.", error);
                        }
                    }

                    // Auto-matching records actual provider metadata,
                    // even if the track has no downloadable lyrics.
                    await matchStore.SaveMatchAsync(
                        item.Entry.SourcePath, chosen,
                        confirmed: false, ct: cancellation.Token);
                    updated++;

                    // The lyrics are independent: preserve source-embedded,
                    // sidecar and user-edited canonical documents.
                    if (canonical is null
                        || (!canonical.HasLyrics
                            && canonical.Origin is not (
                                LibraryLyricsOrigin.ManualEdited
                                or LibraryLyricsOrigin.ManualNetease
                                or LibraryLyricsOrigin.ManualEmpty)))
                    {
                        LrcDocument? lyrics =
                            await client.GetLyricsAsync(chosen,
                                cancellation.Token);
                        if (lyrics is { Lines.Count: > 0 })
                        {
                            await cache.SaveLibraryAsync(new LibraryLyricsRecord(
                                item.Entry.SourcePath,
                                source switch
                                {
                                    CloudLyricsSource.QqMusic => LibraryLyricsOrigin.AutoQq,
                                    CloudLyricsSource.KugouMusic => LibraryLyricsOrigin.AutoKugou,
                                    CloudLyricsSource.MiguMusic => LibraryLyricsOrigin.AutoMigu,
                                    _ => LibraryLyricsOrigin.None,
                                },
                                lyrics.ToString(),
                                null,
                                "auto-" + CloudLyricsSourceNames.Slug(source)
                                    + ":" + chosen.Id,
                                DateTimeOffset.UtcNow),
                                cancellation.Token);
                            await QueueLyricOutputSyncAsync(item);
                            if (ReferenceEquals(item, _selectedLibraryItem))
                            {
                                _currentLyricsRaw = lyrics.ToString();
                                RenderLyrics();
                            }
                        }
                    }
                    if (ReferenceEquals(item, _selectedLibraryItem))
                        await RefreshCloudMatchTabsAsync(item);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    failures++;
                    AppDiagnostics.LogException(
                        $"Automatic {provider} matching failed for {item.Entry.SourcePath}.",
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
            SetBusy(false, source == CloudLyricsSource.KugouMusic
                ? L("KuGou recognition and metadata fallback finished.",
                    "酷狗音频识曲及元数据回退已结束。")
                : L("Metadata search finished.", "元数据匹配已结束。"));
        }

        InvalidateLibraryFacetSnapshot();

        ShowInfo(
            failures > 0 ? InfoBarSeverity.Warning
                         : InfoBarSeverity.Informational,
            source == CloudLyricsSource.KugouMusic
                ? L("KuGou audio matching", "酷狗音频识曲")
                : L($"{provider} metadata matching", $"{provider} 元数据匹配"),
            L(
                $"{updated} matches ({recognized} by audio); {ambiguous} ambiguous results skipped; {failures} errors.",
                $"匹配成功 {updated} 首（音频识曲 {recognized} 首），跳过歧义结果 {ambiguous} 个，错误 {failures} 个。"));
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
