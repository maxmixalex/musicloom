using Microsoft.UI.Xaml.Controls;
using MusicLoom.Audio.Windows;
using MusicLoom.Core.Lyrics;
using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

public sealed partial class MainWindow
{
    private CloudLyricsPanel QqCloudLyricsPanel = null!;
    private CloudLyricsPanel KugouCloudLyricsPanel = null!;

    private void QqLyricsSearchButton_Click(
        object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        OpenCloudLyricsSearch(QqCloudLyricsPanel, "QQ Music", "QQ 音乐");

    private void KugouLyricsSearchButton_Click(
        object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        OpenCloudLyricsSearch(KugouCloudLyricsPanel, "KuGou Music", "酷狗音乐");

    private void OpenCloudLyricsSearch(
        CloudLyricsPanel panel, string nameEn, string nameZh)
    {
        if (_busy || _selectedLibraryItem is not LibraryDisplayItem item)
        {
            return;
        }

        CloseMatchOverlay();
        panel.SetTrack(item.Entry.SourcePath, item.Artist, item.Title, item.Album);
        ShowAppOverlay(
            L("Find " + nameEn + " lyrics", "查找" + nameZh + "歌词"),
            panel, maxWidth: 1200, maxHeight: 640);
    }

    private void InitializeCloudLyricsPanels()
    {
        QqCloudLyricsPanel = new CloudLyricsPanel();
        KugouCloudLyricsPanel = new CloudLyricsPanel();
        QqCloudLyricsPanel.ConfigurePreviewFrom(LyricsList);
        KugouCloudLyricsPanel.ConfigurePreviewFrom(LyricsList);
        QqCloudLyricsPanel.Source = CloudLyricsSource.QqMusic;
        KugouCloudLyricsPanel.Source = CloudLyricsSource.KugouMusic;
        KugouCloudLyricsPanel.CaptureAudioPcm16 =
            (path, token) => new KugouPcmWindowDecoder()
                .DecodeAsync(path, token);
        QqCloudLyricsPanel.LyricsAccepted += CloudLyricsPanel_LyricsAccepted;
        KugouCloudLyricsPanel.LyricsAccepted += CloudLyricsPanel_LyricsAccepted;
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
            // Adopt only user-chosen, validated lyrics. The NetEase match
            // remains untouched; the canonical SQLite lyric becomes an
            // explicit manual choice, retaining its provider in provenance.
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

            ShowInfo(
                InfoBarSeverity.Success,
                L("Lyrics selected", "已采用歌词"),
                L(
                    "Lyrics saved in MusicLoom's database; synchronize to update the destination.",
                    "歌词已保存到 Musicloom 数据库，同步后写入目标音频或 LRC。"));
        }
        catch (Exception error)
        {
            HandleUiException(
                L("Could not save selected lyrics.", "无法保存所选歌词。"),
                error);
        }
    }
}
