using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicLoom.Core.Lyrics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace MusicLoom.App;

public sealed partial class MainWindow
{
    private async void ClearLyricsButton_Click(
        object sender, RoutedEventArgs e)
    {
        if (_busy || _lyricsEditInProgress ||
            _selectedLibraryItem is not LibraryDisplayItem item)
        {
            return;
        }

        ContentDialog confirm = CreateLyricsDialog(
            L("Clear all lyrics?", "清空全部歌词？"));
        confirm.Content = new TextBlock
        {
            Text = L(
                "Clear the stored lyrics. The destination will only be changed during the next sync; source files remain untouched.",
                "清空歌词数据库中的歌词。目标文件仅在下次同步时修改，源文件保持不变。"),
            TextWrapping = TextWrapping.Wrap,
        };
        confirm.PrimaryButtonText = L("Clear", "清空");
        confirm.CloseButtonText = L("Cancel", "取消");

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await SaveManualLyricsAsync(
                item,
                new LibraryLyricsRecord(
                    item.Entry.SourcePath,
                    LibraryLyricsOrigin.ManualEmpty,
                    null,
                    null,
                    "manual-empty",
                    DateTimeOffset.UtcNow));
            _currentLyricsRaw = null;
            RenderLyrics();

            // Only the SQLite canonical lyric changes here. The sync
            // executor owns ALL target filesystem mutations, including
            // removing embedded tags and orphaned sidecar LRC files.
            ShowInfo(InfoBarSeverity.Success,
                L("Lyrics cleared", "歌词已清空"),
                L("Lyrics cleared in MusicLoom; synchronize to update destination files. The source library is unchanged.",
                  "已清空 Musicloom 中的歌词；同步后才修改目标文件。源音乐库未修改。"));
        }
        catch (Exception error)
        {
            HandleUiException(
                L("Could not clear lyrics.", "无法清空歌词。"),
                error);
        }
    }

    private async void AdjustLyricsFlyout_Opening(object sender, object e)
    {
        CurrentLyricsOffsetItem.Text =
            L("Current offset: loading…", "当前偏移：读取中……");
        if (_selectedLibraryItem is not LibraryDisplayItem item)
        {
            CurrentLyricsOffsetItem.Text = L(
                "Current offset: 0 ms", "当前偏移：0 毫秒");
            return;
        }

        try
        {
            int offset = await new SqliteLyricsCache(DatabasePath)
                .GetOffsetMillisecondsAsync(item.Entry.SourcePath) ?? 0;
            if (ReferenceEquals(item, _selectedLibraryItem))
            {
                CurrentLyricsOffsetItem.Text = L(
                    $"Current offset: {offset:+#;-#;0} ms",
                    $"当前偏移：{offset:+#;-#;0} 毫秒");
            }
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException("Could not retrieve lyric offset.", error);
            CurrentLyricsOffsetItem.Text = L(
                "Current offset unavailable", "无法读取当前偏移量");
        }
    }

    private async void AdvanceLyricsItem_Click(
        object sender, RoutedEventArgs e) =>
        await AdjustLyricsAsync(moveEarlier: true);

    private async void DelayLyricsItem_Click(
        object sender, RoutedEventArgs e) =>
        await AdjustLyricsAsync(moveEarlier: false);

    private async Task AdjustLyricsAsync(bool moveEarlier)
    {
        if (_busy || _lyricsEditInProgress ||
            _selectedLibraryItem is not LibraryDisplayItem item)
        {
            return;
        }

        try
        {
            SqliteLyricsCache store = new(DatabasePath);
            int previous = await store.GetOffsetMillisecondsAsync(
                item.Entry.SourcePath) ?? 0;

            NumberBox amount = new()
            {
                Header = L("Offset (milliseconds)", "偏移量（毫秒）"),
                Minimum = 0,
                Maximum = 3_600_000,
                Value = 500,
                SmallChange = 50,
                LargeChange = 1000,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            StackPanel panel = new() { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = L(
                    $"Current total offset: {previous:+#;-#;0} ms. Enter the amount to {(moveEarlier ? "move earlier" : "delay")}.",
                    $"当前累计偏移：{previous:+#;-#;0} 毫秒。请输入本次{(moveEarlier ? "前移" : "后移")}的毫秒数。"),
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(amount);
            ContentDialog dialog = CreateLyricsDialog(
                moveEarlier
                    ? L("Move lyrics earlier", "歌词前移")
                    : L("Delay lyrics", "歌词后移"));
            dialog.Content = panel;
            dialog.PrimaryButtonText = L("Apply", "应用");
            dialog.CloseButtonText = L("Cancel", "取消");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
            if (!double.IsFinite(amount.Value) || amount.Value < 0
                || amount.Value > 3_600_000)
            {
                throw new InvalidDataException(
                    "Invalid lyric offset value.");
            }
            int shift = checked((int)Math.Round(amount.Value));
            int total = checked(previous + (moveEarlier ? -shift : shift));
            await store.SetOffsetMillisecondsAsync(
                item.Entry.SourcePath, total);
            CurrentLyricsOffsetItem.Text = L(
                $"Current offset: {total:+#;-#;0} ms",
                $"当前偏移：{total:+#;-#;0} 毫秒");
            await QueueLyricOutputSyncAsync(item);
            ShowInfo(InfoBarSeverity.Success,
                L("Lyric timing adjusted", "歌词时间已调整"),
                L(
                    $"Output offset is now {total:+#;-#;0} ms; apply it on the next synchronization.",
                    $"输出偏移已设为 {total:+#;-#;0} 毫秒；下次同步时生效。"));
        }
        catch (Exception error)
        {
            HandleUiException(
                L("Could not adjust lyric timing.", "无法调整歌词时间。"),
                error);
        }
    }

    private async void EditLyricsButton_Click(
        object sender, RoutedEventArgs e)
    {
        if (_busy || _lyricsEditInProgress ||
            _selectedLibraryItem is not LibraryDisplayItem item
            || string.IsNullOrWhiteSpace(_currentLyricsRaw))
        {
            return;
        }

        _lyricsEditInProgress = true;
        EditLyricsButton.IsEnabled = false;
        ClearLyricsButton.IsEnabled = false;
        AdjustLyricsButton.IsEnabled = false;
        bool preserveTemporaryFile = false;
        string originalLyrics = _currentLyricsRaw;
        string tempPath = Path.Combine(
            Path.GetTempPath(),
            "MusicLoom",
            "lyrics-" + Guid.NewGuid().ToString("N") + ".lrc");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            await File.WriteAllTextAsync(
                tempPath, originalLyrics, new UTF8Encoding(false));

            string? configured = _settings?.Ui.LyricsEditorPath;
            if (!string.IsNullOrWhiteSpace(configured)
                && !File.Exists(configured))
            {
                throw new FileNotFoundException(
                    L("Configured lyrics editor does not exist.",
                      "指定的歌词编辑器不存在。"), configured);
            }

            // Git commit-message semantics: block this editor session
            // without blocking the UI; automatically import on exit.
            // Notepad is the default text editor rather than the .lrc
            // shell association, which could launch an audio player.
            ProcessStartInfo info = new()
            {
                FileName = string.IsNullOrWhiteSpace(configured)
                    ? "notepad.exe" : configured,
                UseShellExecute = false,
            };
            ConfigureBlockingEditor(info);
            info.ArgumentList.Add(tempPath);
            using Process editor = Process.Start(info)
                ?? throw new InvalidOperationException(
                    L("The lyrics editor did not start.",
                      "无法启动歌词编辑器。"));

            ShowInfo(InfoBarSeverity.Informational,
                L("Editing lyrics", "正在编辑歌词"),
                L("Save the temporary LRC and close the editor to import changes.",
                  "保存临时 LRC 并关闭编辑器，Musicloom 将自动导入修改。"));
            await editor.WaitForExitAsync();

            string updated = await File.ReadAllTextAsync(tempPath);
            if (string.Equals(originalLyrics, updated, StringComparison.Ordinal))
            {
                return; // Closing without edits must never dirty SQLite.
            }

            LrcDocument doc = LrcDocument.Parse(updated);
            if (doc.Lines.Count == 0)
            {
                preserveTemporaryFile = true;
                throw new InvalidDataException(
                    L($"Edited lyrics were not valid timed LRC. Your draft was preserved at: {tempPath}",
                      $"编辑后的歌词不是有效的带时间戳 LRC，草稿已保留：{tempPath}"));
            }

            await SaveManualLyricsAsync(item, new LibraryLyricsRecord(
                item.Entry.SourcePath,
                LibraryLyricsOrigin.ManualEdited,
                doc.ToString(),
                null,
                "manual-edited",
                DateTimeOffset.UtcNow));
            if (ReferenceEquals(item, _selectedLibraryItem))
            {
                _currentLyricsRaw = doc.ToString();
                RenderLyrics();
            }

            ShowInfo(InfoBarSeverity.Success,
                L("Lyrics imported", "歌词已导入"),
                L("Changes are stored in the database and await synchronization.",
                  "修改已写入歌词数据库，等待同步至目标文件。"));
        }
        catch (Exception error)
        {
            HandleUiException(
                L("Could not edit lyrics.", "无法编辑歌词。"),
                error);
        }
        finally
        {
            _lyricsEditInProgress = false;
            if (!preserveTemporaryFile)
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // A file lock is nonfatal; do not replace the source.
                }
            }
            RenderLyrics();
        }
    }

    /// <summary>
    /// Some editors hand the file to an existing instance and their launcher
    /// exits immediately. Git's commit-message workflow uses an editor wait
    /// flag; apply the same documented flags to common user-selected editors.
    /// An unknown executable is launched unchanged.
    /// </summary>
    private static void ConfigureBlockingEditor(ProcessStartInfo info)
    {
        string executable = Path.GetFileNameWithoutExtension(info.FileName)
            .ToLowerInvariant();
        switch (executable)
        {
            case "code":
            case "code - insiders":
            case "codium":
            case "vscodium":
            case "cursor":
            case "windsurf":
                info.ArgumentList.Add("--wait");
                break;
            case "subl":
            case "sublime_text":
                info.ArgumentList.Add("--wait");
                break;
            case "notepad++":
                // Force an independent editor process so WaitForExitAsync
                // follows the actual editing window, not IPC hand-off.
                info.ArgumentList.Add("-multiInst");
                break;
            case "kate":
                info.ArgumentList.Add("--block");
                break;
        }
    }

    private async Task SaveManualLyricsAsync(
        LibraryDisplayItem item,
        LibraryLyricsRecord record)
    {
        SqliteLyricsCache store = new(DatabasePath);
        await store.SaveLibraryAsync(record);
        await QueueLyricOutputSyncAsync(item);
    }

    private async Task QueueLyricOutputSyncAsync(
        LibraryDisplayItem item)
    {
        if (_state is not null && _stateStore is not null
            && _state.Tracks.TryGetValue(
                item.Entry.SourcePath, out var previous))
        {
            _state.Tracks[item.Entry.SourcePath] = previous with
            {
                RecipeVersion = string.Empty,
                ProcessedAt = DateTimeOffset.UtcNow,
            };
            await _stateStore.SaveAsync(_state);
        }
        MarkTrackForEnrichmentSync(item,
            L("Lyrics changed; output needs synchronization.",
              "歌词已修改，需要同步目标输出。"));
    }

    private ContentDialog CreateLyricsDialog(string title) => new()
    {
        XamlRoot = Navigation.XamlRoot,
        Title = title,
        DefaultButton = ContentDialogButton.Primary,
    };

    private async void BrowseLyricsEditorButton_Click(
        object sender, RoutedEventArgs e)
    {
        try
        {
            FileOpenPicker picker = new();
            picker.FileTypeFilter.Add(".exe");
            picker.FileTypeFilter.Add(".com");
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            StorageFile? file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                LyricsEditorPathBox.Text = file.Path;
            }
        }
        catch (Exception error)
        {
            HandleUiException(
                L("Could not select lyrics editor.", "无法选择歌词编辑器。"),
                error);
        }
    }

    private void ResetLyricsEditorButton_Click(
        object sender, RoutedEventArgs e)
    {
        LyricsEditorPathBox.Text = string.Empty;
    }
}
