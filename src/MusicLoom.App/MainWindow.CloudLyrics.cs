using Microsoft.UI.Xaml.Controls;
using MusicLoom.Core.Lyrics;
using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

public sealed partial class MainWindow
{
    private void InitializeCloudLyricsPanels()
    {
        QqCloudLyricsPanel.Source = CloudLyricsSource.QqMusic;
        KugouCloudLyricsPanel.Source = CloudLyricsSource.KugouMusic;
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
