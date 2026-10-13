using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicLoom.Audio.Windows;
using MusicLoom.Core.Lyrics;
using MusicLoom.LyricsProviders;
using MusicLoom.Metadata;

namespace MusicLoom.App;

/// <summary>
/// All providers use MusicLoom's existing in-app modal. QQ and KuGou
/// lyrics share the NetEase three-column interaction without creating a
/// second OS window or separate cache. Automatic QQ/KuGou matching below
/// is explicitly metadata-based, never advertised as fingerprinting.
/// </summary>
public sealed partial class MainWindow
{
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
        panel.LyricSeekRequested += async timestamp =>
        {
            try
            {
                if (!ReferenceEquals(_playingLibraryItem, item))
                {
                    await PlayLibraryItemAsync(item, startPosition: timestamp);
                }
                else if (_mediaPlayer.Source is not null)
                {
                    _mediaPlayer.PlaybackSession.Position = timestamp;
                    _mediaPlayer.Play();
                    UpdatePlaybackLyrics();
                }
            }
            catch (Exception error)
            {
                AppDiagnostics.LogException(
                    "Could not seek selected provider lyric.", error);
            }
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
        ShowAppOverlay(
            source == CloudLyricsSource.QqMusic
                ? L("Find QQ Music lyrics", "查找 QQ 音乐歌词")
                : L("Find KuGou Music lyrics", "查找酷狗音乐歌词"),
            panel,
            maxWidth: 1200,
            maxHeight: 640,
            onClosed: () => panel.Dispose());
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
                        LibraryLyricsOrigin.ManualEdited,
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
