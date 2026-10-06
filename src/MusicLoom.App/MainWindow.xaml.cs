using System.Globalization;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicLoom.Audio.Windows;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Core.Synchronization;
using MusicLoom.Metadata;
using MusicLoom.Netease;
using MusicLoom.Tagging;
using Windows.Storage.Pickers;

namespace MusicLoom.App;

public sealed partial class MainWindow : Window
{
    private static readonly string ConfigPath =
        Path.Combine(AppDiagnostics.DataDirectory, "config.json");

    private static readonly string StatePath =
        Path.Combine(AppDiagnostics.DataDirectory, "state.json");

    private readonly List<PlanDisplayItem> _allPlanItems = [];
    private readonly List<string> _activityItems = [];

    private MusicLoomSettings? _settings;
    private StateDocument? _state;
    private StateStore? _stateStore;
    private LibraryPlan? _plan;
    private CancellationTokenSource? _syncCancellation;
    private bool _busy;
    private bool _initializing = true;
    private string _uiLanguage = DetectSystemLanguage();

    public MainWindow()
    {
        StartupTrace.Mark("MainWindow.ctor.enter");
        StartupTrace.Mark("MainWindow.InitializeComponent.before");
        InitializeComponent();
        StartupTrace.Mark("MainWindow.InitializeComponent.after");

        PlanFilterBox.SelectedIndex = 0;
        LanguageBox.SelectedIndex =
            IsChinese ? 1 : 0;
        LogPathText.Text = AppDiagnostics.LogPath;
        Navigation.SelectedItem = Navigation.MenuItems[0];

        ApplyLanguage();
        _ = InitializeSettingsAsync();

        StartupTrace.Mark("MainWindow.ctor.ready");
    }

    private bool IsChinese =>
        string.Equals(
            _uiLanguage,
            "zh-CN",
            StringComparison.OrdinalIgnoreCase);

    private string L(string english, string chinese) =>
        IsChinese ? chinese : english;

    private async Task InitializeSettingsAsync()
    {
        try
        {
            MusicLoomSettings settings = File.Exists(ConfigPath)
                ? await SettingsLoader.LoadAsync(ConfigPath)
                : new MusicLoomSettings();

            _settings = settings;
            _uiLanguage = NormalizeLanguage(settings.Ui.Language);
            PopulateSettingsForm(settings);
            ApplyLanguage();
            UpdateConfiguredSummary(settings);

            SettingsStatusText.Text = File.Exists(ConfigPath)
                ? L(
                    "Settings loaded.",
                    "设置已加载。")
                : L(
                    "Configure source and destination, then save settings.",
                    "请设置源音乐库和目标目录，然后保存设置。");

            AddActivity(
                L("Ready", "就绪"),
                L(
                    "MusicLoom started. Scan is read-only until Apply sync is explicitly confirmed.",
                    "MusicLoom 已启动。在明确确认“应用同步”前，扫描只读。"));
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not load application settings.",
                exception);
            SettingsStatusText.Text =
                L(
                    "Could not load saved settings. Review the fields and save again.",
                    "无法加载已保存的设置。请检查各字段并重新保存。");
            AddActivity(
                L("Settings error", "设置错误"),
                UserFacingErrors.Describe(exception));
        }
        finally
        {
            _initializing = false;
            InvalidatePlan();
        }
    }

    private async void ScanButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await ScanLibraryAsync(
            clearInfoBar: true,
            recordActivity: true);
    }

    private async void BrowseSourceButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        string? path = await PickFolderAsync();
        if (path is not null)
        {
            SourcePathBox.Text = path;
        }
    }

    private async void BrowseDestinationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        string? path = await PickFolderAsync();
        if (path is not null)
        {
            DestinationPathBox.Text = path;
        }
    }

    private async void BrowsePlaylistButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        string? path = await PickFolderAsync();
        if (path is not null)
        {
            PlaylistSourcePathBox.Text = path;
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        try
        {
            FolderPicker picker = new()
            {
                SuggestedStartLocation =
                    PickerLocationId.MusicLibrary,
            };
            picker.FileTypeFilter.Add("*");

            nint hwnd =
                WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                hwnd);

            Windows.Storage.StorageFolder? folder =
                await picker.PickSingleFolderAsync();

            return folder is not null
                && !string.IsNullOrWhiteSpace(folder.Path)
                    ? folder.Path
                    : null;
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not open the folder picker.",
                    "无法打开文件夹选择器。"),
                exception);
            return null;
        }
    }

    private async void SaveSettingsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            MusicLoomSettings settings =
                BuildSettingsFromForm();

            if (!Directory.Exists(
                    settings.Path.SourceDirectory))
            {
                throw new DirectoryNotFoundException(
                    L(
                        "The source library does not exist.",
                        "源音乐库不存在。"));
            }

            LibraryPathSafety.ValidateStatePath(
                settings,
                StatePath);

            await SettingsLoader.SaveAsync(
                ConfigPath,
                settings);

            _settings = settings;
            _uiLanguage =
                NormalizeLanguage(settings.Ui.Language);
            ApplyLanguage();
            PopulateSettingsForm(settings);
            UpdateConfiguredSummary(settings);
            InvalidatePlan();

            SettingsStatusText.Text =
                L(
                    "Settings saved. Scan when ready.",
                    "设置已保存。可以开始扫描。");

            ShowInfo(
                InfoBarSeverity.Success,
                L("Settings saved", "设置已保存"),
                L(
                    "MusicLoom will use these settings for future runs.",
                    "MusicLoom 今后会使用这些设置。"));
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not save settings.",
                    "无法保存设置。"),
                exception);
        }
    }

    private MusicLoomSettings BuildSettingsFromForm()
    {
        double refreshHours =
            double.IsFinite(LyricsRefreshHoursBox.Value)
            && LyricsRefreshHoursBox.Value > 0
                ? LyricsRefreshHoursBox.Value
                : 24;

        string language =
            (LanguageBox.SelectedItem as ComboBoxItem)
                ?.Tag?.ToString()
            ?? _uiLanguage;

        return new MusicLoomSettings
        {
            Verbose = true,
            Path = new PathSettings
            {
                SourceDirectory =
                    SourcePathBox.Text.Trim(),
                DestinationDirectory =
                    DestinationPathBox.Text.Trim(),
                SourcePlaylistDirectory =
                    PlaylistSourcePathBox.Text.Trim(),
                DestinationPlaylistBase =
                    string.IsNullOrWhiteSpace(
                        PlaylistBaseBox.Text)
                        ? "/storage/emulated/0/Music/"
                        : PlaylistBaseBox.Text.Trim(),
            },
            Organizer = new OrganizerSettings
            {
                Lyrics = new LyricsSettings
                {
                    Enabled = LyricsEnabledToggle.IsOn,
                    OverrideExisting =
                        LyricsOverrideToggle.IsOn,
                    RefreshIntervalHours =
                        refreshHours,
                },
            },
            Logging = _settings?.Logging
                ?? new LoggingSettings(),
            Ui = new UiSettings
            {
                Language =
                    NormalizeLanguage(language),
            },
        };
    }

    private void PopulateSettingsForm(
        MusicLoomSettings settings)
    {
        SourcePathBox.Text =
            settings.Path.SourceDirectory;
        DestinationPathBox.Text =
            settings.Path.DestinationDirectory;
        PlaylistSourcePathBox.Text =
            settings.Path.SourcePlaylistDirectory;
        PlaylistBaseBox.Text =
            settings.Path.DestinationPlaylistBase;
        LyricsEnabledToggle.IsOn =
            settings.Organizer.Lyrics.Enabled;
        LyricsOverrideToggle.IsOn =
            settings.Organizer.Lyrics.OverrideExisting;
        LyricsRefreshHoursBox.Value =
            settings.Organizer.Lyrics.RefreshIntervalHours > 0
                ? settings.Organizer.Lyrics.RefreshIntervalHours
                : 24;

        string language =
            NormalizeLanguage(settings.Ui.Language);
        LanguageBox.SelectedIndex =
            string.Equals(
                language,
                "zh-CN",
                StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;

        SettingsStorageText.Text =
            L(
                $"Stored at: {ConfigPath}",
                $"保存位置：{ConfigPath}");
    }

    private static string NormalizeLanguage(
        string? language)
    {
        if (string.Equals(
                language,
                "zh-CN",
                StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }

        if (string.Equals(
                language,
                "en-US",
                StringComparison.OrdinalIgnoreCase))
        {
            return "en-US";
        }

        return DetectSystemLanguage();
    }

    private static string DetectSystemLanguage()
    {
        string language =
            CultureInfo.CurrentUICulture.Name;

        return language.StartsWith(
            "zh",
            StringComparison.OrdinalIgnoreCase)
                ? "zh-CN"
                : "en-US";
    }

    private void ReviewPlanButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        NavigateTo("plan");
    }

    private void OpenDestinationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_settings is not MusicLoomSettings settings)
        {
            return;
        }

        TryOpenDirectory(
            settings.Path.DestinationDirectory,
            L("destination library", "目标音乐库"));
    }

    private void OpenLogFolderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            AppDiagnostics.OpenLogDirectory();
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not open the diagnostic log folder.",
                    "无法打开诊断日志文件夹。"),
                exception);
        }
    }

    private void OpenDataFolderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        TryOpenDirectory(
            AppDiagnostics.DataDirectory,
            L("MusicLoom data folder", "MusicLoom 数据文件夹"));
    }

    private void ClearActivityButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _activityItems.Clear();
        RefreshActivityList();
        AddActivity(
            L("Activity", "活动"),
            L(
                "Activity view cleared. Diagnostic logs on disk were not deleted.",
                "活动记录已清空，磁盘上的诊断日志未删除。"));
    }

    private void PlanSearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        ApplyPlanFilter();
    }

    private void PlanFilterBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        ApplyPlanFilter();
    }

    private async void ApplySyncButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!await ScanLibraryAsync(
                clearInfoBar: true,
                recordActivity: false)
            || _settings is not MusicLoomSettings settings
            || _state is not StateDocument state
            || _stateStore is not StateStore stateStore
            || _plan is not LibraryPlan plan)
        {
            return;
        }

        if (plan.ErrorCount > 0)
        {
            ShowInfo(
                InfoBarSeverity.Error,
                L("Sync blocked", "同步已阻止"),
                L(
                    "Resolve scan errors before applying changes.",
                    "请先解决扫描错误，再应用更改。"));
            NavigateTo("plan");
            return;
        }

        if (plan.PendingCount == 0)
        {
            ShowInfo(
                InfoBarSeverity.Success,
                L("Already up to date", "已经是最新"),
                L(
                    "There are no pending changes to apply.",
                    "没有待应用的更改。"));
            return;
        }

        string networkText =
            settings.Organizer.Lyrics.Enabled
                ? L(
                    "Netease is contacted only when lyrics are missing or the configured lyric cache has expired.",
                    "仅在缺少歌词或歌词缓存到期时访问网易云音乐。")
                : L(
                    "Lyrics are disabled; this sync makes no Netease lyric requests.",
                    "歌词功能已关闭；本次同步不会请求网易歌词。");

        ContentDialog confirmation = new()
        {
            XamlRoot =
                ((FrameworkElement)Content).XamlRoot,
            Title =
                L(
                    "Apply this sync plan?",
                    "应用这次同步计划？"),
            PrimaryButtonText =
                L("Apply sync", "应用同步"),
            CloseButtonText =
                L("Cancel", "取消"),
            DefaultButton =
                ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text =
                    L(
                        $"{plan.PendingTrackCount:N0} tracks have pending work.\n" +
                        $"{plan.AudioCopyCount:N0} audio files will be copied or rematerialized.\n" +
                        $"{plan.EnrichmentOnlyCount:N0} tracks need enrichment/state work only.\n" +
                        $"{plan.CoverWriteCount:N0} cover files need updating.\n" +
                        $"{plan.PlaylistWriteCount:N0} playlists need updating.\n\n" +
                        $"Destination: {settings.Path.DestinationDirectory}\n" +
                        $"State: {stateStore.Path}\n\n{networkText}\n\n" +
                        "The source library is never modified.",
                        $"{plan.PendingTrackCount:N0} 首歌曲有待处理内容。\n" +
                        $"{plan.AudioCopyCount:N0} 个音频文件将被复制或重新生成。\n" +
                        $"{plan.EnrichmentOnlyCount:N0} 首歌曲只需要歌词/状态处理。\n" +
                        $"{plan.CoverWriteCount:N0} 个封面需要更新。\n" +
                        $"{plan.PlaylistWriteCount:N0} 个播放列表需要更新。\n\n" +
                        $"目标目录：{settings.Path.DestinationDirectory}\n" +
                        $"状态文件：{stateStore.Path}\n\n{networkText}\n\n" +
                        "源音乐库不会被修改。"),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 620,
            },
        };

        if (await confirmation.ShowAsync()
            != ContentDialogResult.Primary)
        {
            return;
        }

        using CancellationTokenSource
            syncCancellation = new();
        _syncCancellation = syncCancellation;

        SetBusy(
            busy: true,
            syncing: true);
        SetIndeterminateProgress();
        SyncInfoBar.IsOpen = false;
        ScanStatusText.Text =
            L(
                "Materializing local files, covers, and playlists…",
                "正在生成音频、封面和播放列表……");

        AddActivity(
            L("Sync started", "同步开始"),
            L(
                $"{plan.PendingCount:N0} pending changes; {plan.AudioCopyCount:N0} audio copies.",
                $"{plan.PendingCount:N0} 项待处理；{plan.AudioCopyCount:N0} 个音频需要复制。"));

        AppDiagnostics.Log(
            $"Sync started: pending={plan.PendingCount}, audio={plan.AudioCopyCount}, " +
            $"covers={plan.CoverWriteCount}, playlists={plan.PlaylistWriteCount}.");

        try
        {
            using NeteaseClient client = new();
            CompositeFingerprintPcmDecoder decoder = new();
            NeteaseTrackProvider provider =
                new(client, decoder);
            TagLibSharpLyricsWriter lyricsWriter = new();

            SyncExecutor executor = new(
                new LocalLibraryMaterializer(),
                provider,
                provider,
                lyricsWriter);

            Progress<SyncExecutionProgress>
                progress = new(value =>
                {
                    string current =
                        Path.GetFileName(value.SourcePath);
                    string stage =
                        FriendlyStage(value.Stage);

                    ScanStatusText.Text =
                        $"{stage} · {value.Processed:N0}/{value.Total:N0} · {current}";
                    SetProgress(
                        value.Processed,
                        value.Total);
                });

            SyncExecutionSummary summary =
                await executor.ExecuteAsync(
                    settings,
                    plan,
                    state,
                    stateStore,
                    progress,
                    syncCancellation.Token);

            string message =
                L(
                    $"{summary.Local.CopiedTracks:N0} audio copies · " +
                    $"{summary.Local.CopiedCovers:N0} covers · " +
                    $"{summary.Local.WrittenPlaylists:N0} playlists · " +
                    $"{summary.WrittenLyrics:N0} lyrics written · " +
                    $"{summary.ExistingLyrics:N0} existing lyrics · " +
                    $"{summary.NoMatch:N0} no match · " +
                    $"{summary.NoLyrics:N0} no lyrics · " +
                    $"{summary.UnsupportedLyrics:N0} unsupported · " +
                    $"{summary.Errors.Count:N0} errors",
                    $"复制音频 {summary.Local.CopiedTracks:N0} · " +
                    $"封面 {summary.Local.CopiedCovers:N0} · " +
                    $"播放列表 {summary.Local.WrittenPlaylists:N0} · " +
                    $"写入歌词 {summary.WrittenLyrics:N0} · " +
                    $"已有歌词 {summary.ExistingLyrics:N0} · " +
                    $"未匹配 {summary.NoMatch:N0} · " +
                    $"无歌词 {summary.NoLyrics:N0} · " +
                    $"不支持 {summary.UnsupportedLyrics:N0} · " +
                    $"错误 {summary.Errors.Count:N0}");

            LastRunTitle.Text =
                summary.Errors.Count == 0
                    ? L(
                        "Last sync completed",
                        "上次同步已完成")
                    : L(
                        "Last sync completed with retryable errors",
                        "上次同步完成，但有可重试错误");
            LastRunSummaryText.Text = message;

            AddActivity(
                summary.Errors.Count == 0
                    ? L("Sync complete", "同步完成")
                    : L("Sync warning", "同步警告"),
                message);
            AppDiagnostics.Log(
                "Sync finished: " + message);

            foreach (TrackSyncError error
                in summary.Errors.Take(200))
            {
                string readable =
                    $"{FriendlyStage(error.Stage)} · " +
                    $"{Path.GetFileName(error.SourcePath)} · " +
                    error.Message;
                AddActivity(
                    L("Retryable error", "可重试错误"),
                    readable);
                AppDiagnostics.Log(
                    $"Track error [{error.Stage}] {error.SourcePath}: {error.Message}");
            }

            ShowInfo(
                summary.Errors.Count == 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning,
                summary.Errors.Count == 0
                    ? L("Sync complete", "同步完成")
                    : L(
                        "Sync completed with retryable errors",
                        "同步完成，但有可重试错误"),
                message);
        }
        catch (OperationCanceledException)
        {
            string message =
                L(
                    "Completed work was checkpointed. Scan again to resume the remaining work.",
                    "已完成的工作已经保存。再次扫描即可继续剩余任务。");

            LastRunTitle.Text =
                L("Last sync cancelled", "上次同步已取消");
            LastRunSummaryText.Text = message;
            AddActivity(
                L("Sync cancelled", "同步已取消"),
                message);
            AppDiagnostics.Log(
                "Sync cancelled by user.");

            ShowInfo(
                InfoBarSeverity.Warning,
                L("Sync cancelled", "同步已取消"),
                message);
        }
        catch (Exception exception)
        {
            string message =
                UserFacingErrors.Describe(exception);

            LastRunTitle.Text =
                L("Last sync failed", "上次同步失败");
            LastRunSummaryText.Text = message;
            AddActivity(
                L("Sync failed", "同步失败"),
                message);
            AppDiagnostics.LogException(
                "Sync failed.",
                exception);

            ShowInfo(
                InfoBarSeverity.Error,
                L("Sync failed", "同步失败"),
                message);
        }
        finally
        {
            _syncCancellation = null;
            SetBusy(
                busy: false,
                syncing: false);
        }

        await ScanLibraryAsync(
            clearInfoBar: false,
            recordActivity: false);
    }

    private void CancelSyncButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        CancellationTokenSource? cancellation =
            _syncCancellation;
        if (cancellation is null)
        {
            return;
        }

        CancelSyncButton.IsEnabled = false;
        ScanStatusText.Text =
            L(
                "Cancelling after the current operation…",
                "正在当前操作结束后取消……");
        cancellation.Cancel();
    }

    private async Task<bool> ScanLibraryAsync(
        bool clearInfoBar,
        bool recordActivity)
    {
        if (!File.Exists(ConfigPath))
        {
            NavigateTo("settings");
            ShowInfo(
                InfoBarSeverity.Warning,
                L("Settings required", "需要设置"),
                L(
                    "Choose a source and destination in Settings, then save.",
                    "请在“设置”中选择源音乐库和目标目录，然后保存。"));
            return false;
        }

        SetBusy(
            busy: true,
            syncing: false);
        SetIndeterminateProgress();

        if (clearInfoBar)
        {
            SyncInfoBar.IsOpen = false;
        }

        ScanStatusText.Text =
            L(
                "Loading settings…",
                "正在加载设置……");

        try
        {
            MusicLoomSettings settings =
                await SettingsLoader.LoadAsync(
                    ConfigPath);

            _uiLanguage =
                NormalizeLanguage(settings.Ui.Language);
            ApplyLanguage();
            PopulateSettingsForm(settings);

            LibraryPathSafety.ValidateStatePath(
                settings,
                StatePath);

            StateStore stateStore =
                new(StatePath);
            StateDocument state =
                await stateStore.LoadAsync();

            string? recoveryWarning =
                stateStore.RecoveryWarning;

            Progress<ScanProgress> progress =
                new(value =>
                {
                    SetProgress(
                        value.Processed,
                        value.Total);

                    if (value.Total == 0)
                    {
                        ScanStatusText.Text =
                            L("Scanning…", "正在扫描……");
                        return;
                    }

                    string current =
                        string.IsNullOrEmpty(value.CurrentPath)
                            ? string.Empty
                            : $" · {Path.GetFileName(value.CurrentPath)}";

                    ScanStatusText.Text =
                        L(
                            $"Scanning {value.Processed:N0}/{value.Total:N0}{current}",
                            $"正在扫描 {value.Processed:N0}/{value.Total:N0}{current}");
                });

            LibraryScanner scanner =
                new(new AtlMetadataReader());
            LibraryPlan plan =
                await scanner.BuildPlanAsync(
                    settings,
                    state,
                    progress);

            _settings = settings;
            _state = state;
            _stateStore = stateStore;
            _plan = plan;

            RenderPlan(
                settings,
                plan);

            UpdateConfiguredSummary(settings);
            StateStatusValue.Text =
                recoveryWarning is null
                    ? L(
                        $"Loaded · {state.Tracks.Count:N0} cached tracks",
                        $"已加载 · {state.Tracks.Count:N0} 首缓存歌曲")
                    : L(
                        "Recovered from backup · review Activity",
                        "已从备份恢复 · 请查看“活动”");

            OpenDestinationButton.IsEnabled =
                Directory.Exists(
                    settings.Path.DestinationDirectory);

            string scanSummary =
                L(
                    $"{plan.TotalCount:N0} tracks · {plan.PendingCount:N0} pending · {plan.ErrorCount:N0} scan errors",
                    $"{plan.TotalCount:N0} 首歌曲 · {plan.PendingCount:N0} 项待处理 · {plan.ErrorCount:N0} 个扫描错误");

            if (recordActivity)
            {
                AddActivity(
                    L("Scan complete", "扫描完成"),
                    scanSummary);
            }

            AppDiagnostics.Log(
                "Scan complete: " + scanSummary);

            if (recoveryWarning is not null)
            {
                AddActivity(
                    L("State recovered", "状态已恢复"),
                    recoveryWarning);
                AppDiagnostics.Log(recoveryWarning);
                ShowInfo(
                    InfoBarSeverity.Warning,
                    L(
                        "State recovered from backup",
                        "已从备份恢复状态"),
                    L(
                        "The primary state file was unreadable. MusicLoom loaded the last good backup.",
                        "主状态文件无法读取，MusicLoom 已加载最近的有效备份。"));
            }
            else if (plan.ErrorCount > 0)
            {
                ShowInfo(
                    InfoBarSeverity.Warning,
                    L(
                        "Scan completed with errors",
                        "扫描完成，但有错误"),
                    L(
                        $"{plan.ErrorCount:N0} items could not be planned.",
                        $"{plan.ErrorCount:N0} 个项目无法生成计划。"));
            }
            else if (plan.PendingCount == 0)
            {
                ShowInfo(
                    InfoBarSeverity.Success,
                    L(
                        "Library is up to date",
                        "音乐库已经是最新"),
                    L(
                        "No audio, enrichment, cover, or playlist changes are pending.",
                        "没有待处理的音频、歌词、封面或播放列表更改。"));
            }

            return true;
        }
        catch (Exception exception)
        {
            _state = null;
            _stateStore = null;
            _plan = null;
            _allPlanItems.Clear();

            TracksValue.Text = "—";
            PendingValue.Text = "—";
            PendingBreakdownText.Text =
                L("Scan failed", "扫描失败");
            ErrorsValue.Text = "1";
            StateStatusValue.Text =
                L("Not loaded", "尚未加载");
            OpenDestinationButton.IsEnabled = false;
            ReviewPlanButton.IsEnabled = false;

            string message =
                UserFacingErrors.Describe(exception);
            ScanStatusText.Text =
                L("Scan failed: ", "扫描失败：") + message;

            AddActivity(
                L("Scan failed", "扫描失败"),
                message);
            AppDiagnostics.LogException(
                "Scan failed.",
                exception);

            _allPlanItems.Add(
                new PlanDisplayItem(
                    "error",
                    L("Scan failed · ", "扫描失败 · ")
                        + message));
            ApplyPlanFilter();

            ShowInfo(
                InfoBarSeverity.Error,
                L("Scan failed", "扫描失败"),
                message);

            return false;
        }
        finally
        {
            SetBusy(
                busy: false,
                syncing: false);
        }
    }

    private void UpdateConfiguredSummary(
        MusicLoomSettings settings)
    {
        SourceLibraryValue.Text =
            string.IsNullOrWhiteSpace(
                settings.Path.SourceDirectory)
                ? L("Not configured", "未配置")
                : settings.Path.SourceDirectory;

        DestinationLibraryValue.Text =
            string.IsNullOrWhiteSpace(
                settings.Path.DestinationDirectory)
                ? L("Not configured", "未配置")
                : settings.Path.DestinationDirectory;

        LyricsModeValue.Text =
            settings.Organizer.Lyrics.Enabled
                ? settings.Organizer.Lyrics.OverrideExisting
                    ? L(
                        $"Enabled · overwrite · refresh {settings.Organizer.Lyrics.RefreshIntervalHours:g} h",
                        $"已启用 · 覆盖已有歌词 · {settings.Organizer.Lyrics.RefreshIntervalHours:g} 小时刷新")
                    : L(
                        "Enabled · preserve existing lyrics",
                        "已启用 · 保留已有歌词")
                : L("Disabled", "已关闭");
    }

    private void RenderPlan(
        MusicLoomSettings settings,
        LibraryPlan plan)
    {
        TracksValue.Text =
            plan.TotalCount.ToString("N0");
        PendingValue.Text =
            plan.PendingCount.ToString("N0");
        PendingBreakdownText.Text =
            L(
                $"{plan.AudioCopyCount:N0} audio · {plan.EnrichmentOnlyCount:N0} enrich · {plan.CoverWriteCount:N0} covers · {plan.PlaylistWriteCount:N0} playlists",
                $"音频 {plan.AudioCopyCount:N0} · 歌词/状态 {plan.EnrichmentOnlyCount:N0} · 封面 {plan.CoverWriteCount:N0} · 播放列表 {plan.PlaylistWriteCount:N0}");
        ErrorsValue.Text =
            plan.ErrorCount.ToString("N0");
        LastScanValue.Text =
            DateTime.Now.ToString("t");

        ScanStatusText.Text =
            L(
                $"{plan.UnchangedCount:N0} unchanged · {plan.PendingTrackCount:N0} track changes · {plan.CoverWriteCount:N0} covers · {plan.PlaylistWriteCount:N0} playlists · {plan.ErrorCount:N0} errors",
                $"未变 {plan.UnchangedCount:N0} · 歌曲更改 {plan.PendingTrackCount:N0} · 封面 {plan.CoverWriteCount:N0} · 播放列表 {plan.PlaylistWriteCount:N0} · 错误 {plan.ErrorCount:N0}");

        _allPlanItems.Clear();

        foreach (PlanEntry entry in plan.Entries)
        {
            if (entry.Status
                == PlanEntryStatus.Unchanged)
            {
                continue;
            }

            string source =
                MakeRelative(
                    settings.Path.SourceDirectory,
                    entry.SourcePath);
            string destination =
                entry.DestinationPath is null
                    ? "—"
                    : MakeRelative(
                        settings.Path.DestinationDirectory,
                        entry.DestinationPath);
            string detail =
                string.IsNullOrWhiteSpace(entry.Detail)
                    ? string.Empty
                    : $" · {entry.Detail}";

            string kind =
                entry.Status == PlanEntryStatus.Error
                    ? "error"
                    : entry.NeedsAudioCopy
                        ? "copy"
                        : "enrich";

            string label =
                kind switch
                {
                    "copy" => L("COPY", "复制"),
                    "enrich" => L("ENRICH", "歌词"),
                    _ => L("ERROR", "错误"),
                };

            _allPlanItems.Add(
                new PlanDisplayItem(
                    kind,
                    $"{label,-8} {source}  →  {destination}{detail}"));
        }

        foreach (CoverPlanEntry cover
            in plan.Covers)
        {
            if (!cover.NeedsWrite)
            {
                continue;
            }

            string source =
                MakeRelative(
                    settings.Path.SourceDirectory,
                    cover.SourcePath);
            string destination =
                MakeRelative(
                    settings.Path.DestinationDirectory,
                    cover.DestinationPath);

            _allPlanItems.Add(
                new PlanDisplayItem(
                    "cover",
                    $"{L("COVER", "封面"),-8} {source}  →  {destination}"));
        }

        foreach (PlaylistPlanEntry playlist
            in plan.Playlists)
        {
            if (!playlist.NeedsWrite)
            {
                continue;
            }

            string source =
                MakeRelative(
                    settings.Path.SourcePlaylistDirectory,
                    playlist.SourcePath);
            string destination =
                MakeRelative(
                    settings.Path.DestinationDirectory,
                    playlist.DestinationPath);

            _allPlanItems.Add(
                new PlanDisplayItem(
                    "playlist",
                    $"{L("PLAYLIST", "列表"),-8} {source}  →  {destination}"));
        }

        ApplyPlanFilter();
        ReviewPlanButton.IsEnabled =
            _allPlanItems.Count > 0;

        UpdateApplyButton();
    }

    private void ApplyPlanFilter()
    {
        if (PlanList is null
            || PlanFilterBox is null
            || PlanSearchBox is null)
        {
            return;
        }

        string filter =
            (PlanFilterBox.SelectedItem
                as ComboBoxItem)?.Tag?.ToString()
            ?? "all";
        string query =
            PlanSearchBox.Text.Trim();

        IEnumerable<PlanDisplayItem> visible =
            _allPlanItems;

        if (!string.Equals(
                filter,
                "all",
                StringComparison.Ordinal))
        {
            visible = visible.Where(
                item => string.Equals(
                    item.Kind,
                    filter,
                    StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            visible = visible.Where(
                item => item.Text.Contains(
                    query,
                    StringComparison.OrdinalIgnoreCase));
        }

        PlanDisplayItem[] items =
            visible.ToArray();

        PlanList.Items.Clear();

        if (items.Length == 0)
        {
            PlanList.Items.Add(
                _allPlanItems.Count == 0
                    ? L(
                        "No pending changes.",
                        "没有待处理更改。")
                    : L(
                        "No plan items match this filter.",
                        "没有符合当前筛选条件的项目。"));
        }
        else
        {
            foreach (PlanDisplayItem item
                in items.Take(1000))
            {
                PlanList.Items.Add(item.Text);
            }

            if (items.Length > 1000)
            {
                PlanList.Items.Add(
                    L(
                        $"… {items.Length - 1000:N0} more matching items",
                        $"… 还有 {items.Length - 1000:N0} 个匹配项目"));
            }
        }

        PlanVisibleCountText.Text =
            L(
                $"{items.Length:N0} of {_allPlanItems.Count:N0}",
                $"{items.Length:N0} / {_allPlanItems.Count:N0}");
    }

    private void Navigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag
            is not string tag)
        {
            return;
        }

        ShowPage(tag);
    }

    private void NavigateTo(string tag)
    {
        NavigationViewItem? target =
            Navigation.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(
                    item => string.Equals(
                        item.Tag?.ToString(),
                        tag,
                        StringComparison.Ordinal));

        if (target is not null)
        {
            Navigation.SelectedItem = target;
        }

        ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        OverviewPage.Visibility =
            tag == "overview"
                ? Visibility.Visible
                : Visibility.Collapsed;
        PlanPage.Visibility =
            tag == "plan"
                ? Visibility.Visible
                : Visibility.Collapsed;
        ActivityPage.Visibility =
            tag == "activity"
                ? Visibility.Visible
                : Visibility.Collapsed;
        SettingsPage.Visibility =
            tag == "settings"
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void SetBusy(
        bool busy,
        bool syncing)
    {
        _busy = busy;

        ScanButton.IsEnabled = !busy;
        ReviewPlanButton.IsEnabled =
            !busy && _allPlanItems.Count > 0;
        SaveSettingsButton.IsEnabled = !busy;
        BrowseSourceButton.IsEnabled = !busy;
        BrowseDestinationButton.IsEnabled = !busy;
        BrowsePlaylistButton.IsEnabled = !busy;
        SourcePathBox.IsEnabled = !busy;
        DestinationPathBox.IsEnabled = !busy;
        PlaylistSourcePathBox.IsEnabled = !busy;
        PlaylistBaseBox.IsEnabled = !busy;
        LyricsEnabledToggle.IsEnabled = !busy;
        LyricsOverrideToggle.IsEnabled = !busy;
        LyricsRefreshHoursBox.IsEnabled = !busy;
        LanguageBox.IsEnabled = !busy;

        OpenDestinationButton.IsEnabled =
            !busy
            && _settings is MusicLoomSettings settings
            && Directory.Exists(
                settings.Path.DestinationDirectory);

        CancelSyncButton.Visibility =
            syncing
                ? Visibility.Visible
                : Visibility.Collapsed;
        CancelSyncButton.IsEnabled =
            syncing;

        OperationProgressBar.Visibility =
            busy
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (!busy)
        {
            OperationProgressBar.IsIndeterminate =
                false;
            OperationProgressBar.Value = 0;
        }

        UpdateApplyButton();
    }

    private void UpdateApplyButton()
    {
        ApplySyncButton.IsEnabled =
            !_busy
            && _plan is
                { PendingCount: > 0, ErrorCount: 0 };
    }

    private void InvalidatePlan()
    {
        _state = null;
        _stateStore = null;
        _plan = null;
        _allPlanItems.Clear();

        ReviewPlanButton.IsEnabled = false;
        ApplySyncButton.IsEnabled = false;
        OpenDestinationButton.IsEnabled =
            _settings is MusicLoomSettings settings
            && Directory.Exists(
                settings.Path.DestinationDirectory);

        if (!_initializing)
        {
            ScanStatusText.Text =
                L(
                    "Settings changed or no scan is loaded. Scan before applying sync.",
                    "设置已更改或尚未扫描。请先扫描，再应用同步。");
            ApplyPlanFilter();
        }
    }

    private void SetProgress(
        int processed,
        int total)
    {
        OperationProgressBar.Visibility =
            Visibility.Visible;

        if (total <= 0)
        {
            OperationProgressBar.IsIndeterminate =
                true;
            return;
        }

        OperationProgressBar.IsIndeterminate =
            false;
        OperationProgressBar.Value =
            Math.Clamp(
                processed * 100.0 / total,
                0,
                100);
    }

    private void SetIndeterminateProgress()
    {
        OperationProgressBar.Visibility =
            Visibility.Visible;
        OperationProgressBar.IsIndeterminate =
            true;
    }

    private void ShowInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        SyncInfoBar.Severity = severity;
        SyncInfoBar.Title = title;
        SyncInfoBar.Message = message;
        SyncInfoBar.IsOpen = true;
    }

    private void AddActivity(
        string category,
        string message)
    {
        _activityItems.Insert(
            0,
            $"[{DateTime.Now:T}] {category} · {message}");

        if (_activityItems.Count > 200)
        {
            _activityItems.RemoveRange(
                200,
                _activityItems.Count - 200);
        }

        RefreshActivityList();
    }

    private void RefreshActivityList()
    {
        ActivityList.Items.Clear();

        if (_activityItems.Count == 0)
        {
            ActivityList.Items.Add(
                L(
                    "No activity yet.",
                    "暂无活动记录。"));
            return;
        }

        foreach (string item
            in _activityItems)
        {
            ActivityList.Items.Add(item);
        }
    }

    private void HandleUiException(
        string context,
        Exception exception)
    {
        string message =
            UserFacingErrors.Describe(exception);

        AddActivity(
            L("UI error", "界面错误"),
            message);
        AppDiagnostics.LogException(
            context,
            exception);

        ShowInfo(
            InfoBarSeverity.Error,
            context,
            message);
    }

    private void TryOpenDirectory(
        string path,
        string description)
    {
        try
        {
            Directory.CreateDirectory(path);

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    $"Could not open {description}.",
                    $"无法打开{description}。"),
                exception);
        }
    }

    private string FriendlyStage(
        string stage) =>
        stage switch
        {
            "state" =>
                L("Preparing state", "准备状态"),
            "inspect-lyrics" =>
                L("Checking lyrics", "检查歌词"),
            "recognize" =>
                L("Recognizing track", "识别歌曲"),
            "fetch-lyrics" =>
                L("Fetching lyrics", "获取歌词"),
            "write-lyrics" =>
                L("Writing lyrics", "写入歌词"),
            "copy-audio" =>
                L("Copying audio", "复制音频"),
            "copy-cover" =>
                L("Copying cover", "复制封面"),
            "write-playlist" =>
                L("Writing playlist", "写入播放列表"),
            "complete" =>
                L("Complete", "完成"),
            "failed" =>
                L("Failed", "失败"),
            _ => stage,
        };

    private void ApplyLanguage()
    {
        OverviewNav.Content = L("Overview", "概览");
        PlanNav.Content = L("Plan", "计划");
        ActivityNav.Content = L("Activity", "活动");
        SettingsNav.Content = L("Settings", "设置");

        OverviewTitleText.Text = L("Overview", "概览");
        OverviewSubtitleText.Text = L(
            "Scan, review, then apply. The source library is never modified.",
            "先扫描、再检查、最后应用。源音乐库始终不会被修改。");

        ScanButton.Content = L("Scan library", "扫描音乐库");
        ReviewPlanButton.Content = L("Review plan", "查看计划");
        ApplySyncButton.Content = L("Apply sync", "应用同步");
        CancelSyncButton.Content = L("Cancel", "取消");
        OpenDestinationButton.Content = L("Open destination", "打开目标目录");

        TracksLabelText.Text = L("Tracks", "歌曲");
        PendingLabelText.Text = L("Pending", "待处理");
        ErrorsLabelText.Text = L("Errors", "错误");
        LastScanLabelText.Text = L("Last scan", "上次扫描");
        SourceLabelText.Text = L("Source", "源音乐库");
        DestinationLabelText.Text = L("Destination", "目标目录");
        LyricsLabelText.Text = L("Lyrics", "歌词");
        StateLabelText.Text = L("State", "状态");

        PlanTitleText.Text = L("Plan", "计划");
        PlanSubtitleText.Text = L(
            "Review pending copy, enrichment, cover, playlist, and error items.",
            "查看待处理的复制、歌词、封面、播放列表和错误项目。");
        PlanSearchBox.PlaceholderText =
            L(
                "Search source, destination, or reason",
                "搜索源文件、目标文件或原因");
        PlanFilterBox.Header = L("Show", "显示");
        FilterAllItem.Content = L("All pending", "全部待处理");
        FilterCopyItem.Content = L("Audio copy", "音频复制");
        FilterEnrichItem.Content = L("Enrichment", "歌词/状态");
        FilterCoverItem.Content = L("Cover", "封面");
        FilterPlaylistItem.Content = L("Playlist", "播放列表");
        FilterErrorsItem.Content = L("Errors", "错误");

        ActivityTitleText.Text = L("Activity", "活动");
        ActivitySubtitleText.Text = L(
            "Recent scans, sync results, recoveries, and user-visible errors.",
            "最近的扫描、同步结果、恢复记录和可见错误。");
        OpenLogFolderButton.Content =
            L("Open log folder", "打开日志目录");
        ClearActivityButton.Content =
            L("Clear activity", "清空活动");

        SettingsTitleText.Text = L("Settings", "设置");
        SettingsSubtitleText.Text = L(
            "MusicLoom stores these settings under your local application data. You do not need to edit JSON files.",
            "MusicLoom 会把这些设置保存在本地应用数据目录中，不需要手工编辑 JSON。");
        SourcePathBox.Header = L("Source library", "源音乐库");
        SourcePathBox.PlaceholderText =
            L(
                "Folder containing your original music",
                "包含原始音乐的文件夹");
        DestinationPathBox.Header =
            L("Destination library", "目标音乐库");
        DestinationPathBox.PlaceholderText =
            L(
                "Separate output folder",
                "独立的输出文件夹");
        PlaylistSourcePathBox.Header =
            L(
                "MusicBee playlist folder (optional)",
                "MusicBee 播放列表目录（可选）");
        PlaylistSourcePathBox.PlaceholderText =
            L(
                "Folder containing .m3u playlists",
                "包含 .m3u 播放列表的文件夹");
        PlaylistBaseBox.Header =
            L(
                "Playlist path on phone",
                "手机上的播放列表路径");
        BrowseSourceButton.Content = L("Browse…", "浏览……");
        BrowseDestinationButton.Content = L("Browse…", "浏览……");
        BrowsePlaylistButton.Content = L("Browse…", "浏览……");
        LyricsSettingsHeadingText.Text = L("Lyrics", "歌词");
        CacheSettingsHeadingText.Text = L("Refresh", "刷新");
        LyricsEnabledToggle.Header =
            L(
                "Fetch and embed lyrics",
                "获取并嵌入歌词");
        LyricsOverrideToggle.Header =
            L(
                "Overwrite existing lyrics",
                "覆盖已有歌词");
        LyricsRefreshHoursBox.Header =
            L(
                "Lyrics cache lifetime (hours)",
                "歌词缓存有效期（小时）");
        LanguageBox.Header =
            L("Interface language", "界面语言");
        SaveSettingsButton.Content =
            L("Save settings", "保存设置");
        OpenDataFolderButton.Content =
            L("Open data folder", "打开数据目录");

        SettingsStorageText.Text =
            L(
                $"Stored at: {ConfigPath}",
                $"保存位置：{ConfigPath}");
    }

    private static string MakeRelative(
        string root,
        string path)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return path;
        }

        try
        {
            return Path.GetRelativePath(
                Path.GetFullPath(root),
                Path.GetFullPath(path));
        }
        catch
        {
            return path;
        }
    }

    private sealed record PlanDisplayItem(
        string Kind,
        string Text);
}
