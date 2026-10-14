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
                "Clear MusicLoom's stored lyrics? Source files and synchronized output will not be modified until you run Sync.",
                "仅清空 MusicLoom 歌词数据库。源文件和同步目标在执行同步前不会被修改。"),
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

            ShowInfo(InfoBarSeverity.Success,
                L("Lyrics cleared", "歌词已清空"),
                L("Lyrics cleared in the database; destination changes await synchronization.",
                   "已清空歌词数据库，同步目标将在下次同步时更新。"));
        }
        catch (Exception error)
        {
            HandleUiException(
                L("Could not clear lyrics.", "无法清空歌词。"),
                error);
        }
    }

    private async void AdjustLyricsButton_Click(
        object sender, RoutedEventArgs e)
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

            NumberBox offset = new()
            {
                Header = L("Total offset (milliseconds)", "总偏移量（毫秒）"),
                Minimum = -3_600_000,
                Maximum = 3_600_000,
                Value = previous,
                SmallChange = 50,
                LargeChange = 1000,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            };
            StackPanel panel = new() { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = L(
                    "Set the total lyric offset: negative means earlier, positive means later, and zero resets it.",
                    "直接设置歌词总偏移量：负数提前，正数延后，输入 0 即重置。"),
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(offset);

            ContentDialog dialog = CreateLyricsDialog(
                L("Set lyric offset", "设置歌词偏移量"));
            dialog.Content = panel;
            dialog.PrimaryButtonText = L("Apply", "应用");
            dialog.CloseButtonText = L("Cancel", "取消");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
            if (!double.IsFinite(offset.Value)
                || offset.Value < -3_600_000 || offset.Value > 3_600_000)
            {
                throw new InvalidDataException("Invalid lyric offset value.");
            }

            int total = checked((int)Math.Round(offset.Value));
            if (total == previous)
            {
                return;
            }

            await store.SetOffsetMillisecondsAsync(
                item.Entry.SourcePath, total);
            await QueueLyricOutputSyncAsync(item);
            ShowInfo(InfoBarSeverity.Success,
                L("Lyric offset saved", "歌词偏移量已保存"),
                L(
                    $"Total output offset is {total:+#;-#;0} ms; synchronization will apply it.",
                    $"输出总偏移量为 {total:+#;-#;0} 毫秒，将在同步时生效。"));
        }
        catch (Exception error)
        {
            HandleUiException(
                L("Could not set lyric offset.", "无法设置歌词偏移量。"),
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
        string tempPath = Path.Combine(
            Path.GetTempPath(),
            "MusicLoom",
            "lyrics-" + Guid.NewGuid().ToString("N") + ".lrc");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            await File.WriteAllTextAsync(
                tempPath, _currentLyricsRaw, new UTF8Encoding(false));

            string? configured = _settings?.Ui.LyricsEditorPath;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!File.Exists(configured))
                {
                    throw new FileNotFoundException(
                        "Configured lyric editor was not found.", configured);
                }

                ProcessStartInfo info = new()
                {
                    FileName = configured,
                    UseShellExecute = true,
                };
                info.ArgumentList.Add(tempPath);
                Process.Start(info);
            }
            else
            {
                StorageFile temp = await StorageFile.GetFileFromPathAsync(tempPath);
                bool opened = await Windows.System.Launcher.LaunchFileAsync(temp);
                if (!opened)
                {
                    ProcessStartInfo fallback = new()
                    {
                        FileName = "notepad.exe",
                        UseShellExecute = true,
                    };
                    fallback.ArgumentList.Add(tempPath);
                    Process.Start(fallback);
                }
            }

            ContentDialog dialog = CreateLyricsDialog(
                L("Editing lyrics", "正在编辑歌词"));
            dialog.Content = new TextBlock
            {
                Text = L(
                    "Edit and save the temporary LRC file in your editor. Click Import once saved; Cancel leaves the library unchanged.",
                    "请在编辑器中修改并保存临时 LRC 文件，完成后点击“导入”；取消则不修改音乐库。"),
                TextWrapping = TextWrapping.Wrap,
            };
            dialog.PrimaryButtonText = L("Import changes", "导入修改");
            dialog.CloseButtonText = L("Cancel", "取消");
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            string updated = await File.ReadAllTextAsync(tempPath);
            LrcDocument doc = LrcDocument.Parse(updated);
            if (doc.Lines.Count == 0)
            {
                throw new InvalidDataException(
                    "Edited LRC has no valid timestamped lyric lines.");
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
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (IOException)
            {
                // External editor may still have an open handle.
            }
            RenderLyrics();
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
