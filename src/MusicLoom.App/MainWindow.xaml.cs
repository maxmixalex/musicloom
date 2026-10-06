using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicLoom.Audio.Windows;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Planning;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Core.Synchronization;
using MusicLoom.Metadata;
using MusicLoom.Netease;
using MusicLoom.Tagging;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace MusicLoom.App;

public sealed partial class MainWindow : Window
{
    private static readonly string ConfigPath =
        Path.Combine(
            AppDiagnostics.DataDirectory,
            "config.json");

    private static readonly string StatePath =
        Path.Combine(
            AppDiagnostics.DataDirectory,
            "state.json");

    private readonly List<LibraryDisplayItem>
        _allLibraryItems = [];
    private readonly List<string>
        _activityItems = [];

    private MusicLoomSettings? _settings;
    private StateDocument? _state;
    private StateStore? _stateStore;
    private LibraryPlan? _plan;
    private CancellationTokenSource?
        _operationCancellation;
    private LibraryDisplayItem?
        _selectedLibraryItem;
    private bool _busy;
    private int _detailRequestId;
    private string _uiLanguage =
        DetectSystemLanguage();

    public MainWindow()
    {
        StartupTrace.Mark(
            "MainWindow.ctor.enter");
        StartupTrace.Mark(
            "MainWindow.InitializeComponent.before");
        InitializeComponent();
        StartupTrace.Mark(
            "MainWindow.InitializeComponent.after");

        LibraryFilterBox.SelectedIndex = 0;
        LyricsStorageModeBox.SelectedIndex = 0;
        LanguageBox.SelectedIndex =
            IsChinese ? 1 : 0;
        LogPathText.Text =
            AppDiagnostics.LogPath;
        Navigation.SelectedItem =
            Navigation.MenuItems[0];

        ApplyLanguage();
        ResetTrackDetails();
        _ = InitializeSettingsAsync();

        StartupTrace.Mark(
            "MainWindow.ctor.ready");
    }

    private bool IsChinese =>
        string.Equals(
            _uiLanguage,
            "zh-CN",
            StringComparison.OrdinalIgnoreCase);

    private string L(
        string english,
        string chinese) =>
        IsChinese ? chinese : english;

    private async Task InitializeSettingsAsync()
    {
        try
        {
            MusicLoomSettings settings =
                File.Exists(ConfigPath)
                    ? await SettingsLoader.LoadAsync(
                        ConfigPath)
                    : new MusicLoomSettings();

            _settings = settings;
            _uiLanguage =
                NormalizeLanguage(
                    settings.Ui.Language);

            PopulateSettingsForm(settings);
            ApplyLanguage();
            UpdateLibraryEmptyState();

            SettingsStatusText.Text =
                File.Exists(ConfigPath)
                    ? L(
                        "Settings loaded.",
                        "设置已加载。")
                    : L(
                        "Choose the source and destination once, then save.",
                        "只需设置一次源音乐库和目标目录，然后保存。");

            AddActivity(
                L("Ready", "就绪"),
                L(
                    "MusicLoom started.",
                    "MusicLoom 已启动。"));
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not load application settings.",
                exception);

            SettingsStatusText.Text =
                L(
                    "Could not load saved settings. Review the fields and save again.",
                    "无法加载已保存设置。请检查各字段并重新保存。");

            AddActivity(
                L(
                    "Settings error",
                    "设置错误"),
                UserFacingErrors.Describe(
                    exception));
        }
        finally
        {
            UpdateLibraryEmptyState();
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

    private async void SyncButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!await ScanLibraryAsync(
                clearInfoBar: true,
                recordActivity: false)
            || _settings is not
                MusicLoomSettings settings
            || _state is not
                StateDocument state
            || _stateStore is not
                StateStore stateStore
            || _plan is not
                LibraryPlan plan)
        {
            return;
        }

        if (plan.ErrorCount > 0)
        {
            ShowInfo(
                InfoBarSeverity.Error,
                L(
                    "Sync blocked",
                    "同步已阻止"),
                L(
                    "Resolve the error items shown in the library before syncing.",
                    "请先处理音乐库中显示的错误项目，再进行同步。"));

            LibraryFilterBox.SelectedIndex = 3;
            return;
        }

        if (plan.PendingCount == 0)
        {
            ShowInfo(
                InfoBarSeverity.Success,
                L(
                    "Already up to date",
                    "已经是最新"),
                L(
                    "There is nothing to sync.",
                    "没有需要同步的内容。"));
            return;
        }

        string storage =
            LyricsStorageModes.Normalize(
                settings.Organizer.Lyrics
                    .StorageMode);

        string lyricsText =
            !settings.Organizer.Lyrics.Enabled
                ? L(
                    "Lyrics are disabled.",
                    "歌词功能已关闭。")
                : storage
                    == LyricsStorageModes.Sidecar
                    ? L(
                        "Fetched lyrics will be written as sidecar .lrc files.",
                        "获取到的歌词将写入独立的 .lrc 文件。")
                    : L(
                        "Fetched lyrics will be embedded in destination audio files.",
                        "获取到的歌词将嵌入目标音频文件。");

        ContentDialog confirmation = new()
        {
            XamlRoot =
                ((FrameworkElement)Content)
                    .XamlRoot,
            Title =
                L(
                    "Sync pending changes?",
                    "同步待处理更改？"),
            PrimaryButtonText =
                L("Sync", "同步"),
            CloseButtonText =
                L("Cancel", "取消"),
            DefaultButton =
                ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text =
                    L(
                        $"{plan.PendingTrackCount:N0} tracks need work.\n" +
                        $"{plan.AudioCopyCount:N0} audio files will be copied or refreshed.\n" +
                        $"{plan.CoverWriteCount:N0} covers and {plan.PlaylistWriteCount:N0} playlists will be updated.\n\n" +
                        $"{lyricsText}\n\nDestination: {settings.Path.DestinationDirectory}\n\n" +
                        "The source library remains read-only.",
                        $"{plan.PendingTrackCount:N0} 首歌曲需要处理。\n" +
                        $"{plan.AudioCopyCount:N0} 个音频文件将被复制或刷新。\n" +
                        $"{plan.CoverWriteCount:N0} 个封面和 {plan.PlaylistWriteCount:N0} 个播放列表将被更新。\n\n" +
                        $"{lyricsText}\n\n目标目录：{settings.Path.DestinationDirectory}\n\n" +
                        "源音乐库保持只读。"),
                TextWrapping =
                    TextWrapping.Wrap,
                MaxWidth = 620,
            },
        };

        if (await confirmation.ShowAsync()
            != ContentDialogResult.Primary)
        {
            return;
        }

        using CancellationTokenSource
            cancellation = new();
        _operationCancellation =
            cancellation;

        SetBusy(
            busy: true,
            operationStatus:
                L(
                    "Preparing sync…",
                    "正在准备同步……"));

        await Task.Yield();

        AddActivity(
            L(
                "Sync started",
                "同步开始"),
            L(
                $"{plan.PendingCount:N0} pending items.",
                $"{plan.PendingCount:N0} 个待处理项目。"));

        try
        {
            using NeteaseClient client =
                new();
            CompositeFingerprintPcmDecoder
                decoder = new();
            NeteaseTrackProvider provider =
                new(
                    client,
                    decoder);

            ITrackLyricsWriter lyricsWriter =
                storage
                    == LyricsStorageModes.Sidecar
                    ? new SidecarLrcLyricsWriter()
                    : new TagLibSharpLyricsWriter();

            SyncExecutor executor = new(
                new LocalLibraryMaterializer(),
                provider,
                provider,
                lyricsWriter);

            Progress<SyncExecutionProgress>
                progress = new(value =>
                {
                    string current =
                        Path.GetFileName(
                            value.SourcePath);

                    OperationStatusText.Text =
                        $"{FriendlyStage(value.Stage)} · " +
                        $"{value.Processed:N0}/{value.Total:N0} · " +
                        current;

                    SetProgress(
                        value.Processed,
                        value.Total);
                });

            SyncExecutionSummary summary =
                await Task.Run(
                    async () =>
                        await executor.ExecuteAsync(
                            settings,
                            plan,
                            state,
                            stateStore,
                            progress,
                            cancellation.Token),
                    cancellation.Token);

            string message =
                L(
                    $"{summary.Local.CopiedTracks:N0} audio · " +
                    $"{summary.Local.CopiedCovers:N0} covers · " +
                    $"{summary.Local.WrittenPlaylists:N0} playlists · " +
                    $"{summary.WrittenLyrics:N0} lyrics written · " +
                    $"{summary.Errors.Count:N0} errors",
                    $"音频 {summary.Local.CopiedTracks:N0} · " +
                    $"封面 {summary.Local.CopiedCovers:N0} · " +
                    $"播放列表 {summary.Local.WrittenPlaylists:N0} · " +
                    $"写入歌词 {summary.WrittenLyrics:N0} · " +
                    $"错误 {summary.Errors.Count:N0}");

            LastRunTitle.Text =
                summary.Errors.Count == 0
                    ? L(
                        "Last sync completed",
                        "上次同步已完成")
                    : L(
                        "Last sync completed with errors",
                        "上次同步完成，但有错误");

            LastRunSummaryText.Text =
                message;

            AddActivity(
                summary.Errors.Count == 0
                    ? L(
                        "Sync complete",
                        "同步完成")
                    : L(
                        "Sync warning",
                        "同步警告"),
                message);

            foreach (TrackSyncError error
                in summary.Errors.Take(200))
            {
                AddActivity(
                    L(
                        "Track error",
                        "歌曲错误"),
                    $"{Path.GetFileName(error.SourcePath)} · " +
                    $"{FriendlyStage(error.Stage)} · " +
                    error.Message);
            }

            ShowInfo(
                summary.Errors.Count == 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning,
                summary.Errors.Count == 0
                    ? L(
                        "Sync complete",
                        "同步完成")
                    : L(
                        "Sync completed with errors",
                        "同步完成，但有错误"),
                message);
        }
        catch (OperationCanceledException)
        {
            ShowInfo(
                InfoBarSeverity.Warning,
                L(
                    "Sync cancelled",
                    "同步已取消"),
                L(
                    "Completed work was checkpointed. Refresh the library to continue later.",
                    "已完成的工作已经保存。稍后刷新音乐库即可继续。"));

            AddActivity(
                L(
                    "Sync cancelled",
                    "同步已取消"),
                L(
                    "Completed work was checkpointed.",
                    "已完成的工作已经保存。"));
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Sync failed.",
                    "同步失败。"),
                exception);
        }
        finally
        {
            _operationCancellation = null;
            SetBusy(
                busy: false,
                operationStatus:
                    L(
                        "Refreshing library…",
                        "正在刷新音乐库……"));
        }

        await ScanLibraryAsync(
            clearInfoBar: false,
            recordActivity: false);
    }

    private async void CancelButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        CancellationTokenSource?
            cancellation =
                _operationCancellation;

        if (cancellation is null)
        {
            return;
        }

        CancelButton.IsEnabled = false;
        OperationStatusText.Text =
            L(
                "Cancelling…",
                "正在取消……");

        await cancellation
            .CancelAsync();
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
                L(
                    "Set up your library first",
                    "请先设置音乐库"),
                L(
                    "Choose a source and destination in Settings, then save.",
                    "请在“设置”中选择源音乐库和目标目录，然后保存。"));

            return false;
        }

        using CancellationTokenSource
            cancellation = new();
        _operationCancellation =
            cancellation;

        SetBusy(
            busy: true,
            operationStatus:
                L(
                    "Scanning library…",
                    "正在扫描音乐库……"));

        if (clearInfoBar)
        {
            AppInfoBar.IsOpen = false;
        }

        await Task.Yield();

        try
        {
            MusicLoomSettings settings =
                await SettingsLoader.LoadAsync(
                    ConfigPath,
                    cancellation.Token);

            _settings = settings;
            _uiLanguage =
                NormalizeLanguage(
                    settings.Ui.Language);
            ApplyLanguage();
            PopulateSettingsForm(settings);

            LibraryPathSafety.ValidateStatePath(
                settings,
                StatePath);

            StateStore stateStore =
                new(StatePath);
            StateDocument state =
                await stateStore.LoadAsync(
                    cancellation.Token);

            Progress<ScanProgress>
                progress = new(value =>
                {
                    if (value.Stage == "ancillary")
                    {
                        OperationProgressBar.Visibility =
                            Visibility.Visible;
                        OperationProgressBar.IsIndeterminate =
                            true;
                        OperationStatusText.Text =
                            L(
                                "Organizing covers and playlists…",
                                "正在整理封面和播放列表……");
                        return;
                    }

                    if (value.Stage == "complete")
                    {
                        OperationProgressBar.Visibility =
                            Visibility.Visible;
                        OperationProgressBar.IsIndeterminate =
                            true;
                        OperationStatusText.Text =
                            L(
                                "Building library view…",
                                "正在构建音乐库视图……");
                        return;
                    }

                    string current =
                        string.IsNullOrWhiteSpace(
                            value.CurrentPath)
                            ? string.Empty
                            : " · " +
                                Path.GetFileName(
                                    value.CurrentPath);

                    OperationStatusText.Text =
                        L(
                            $"Reading tracks {value.Processed:N0}/{value.Total:N0}{current}",
                            $"正在读取歌曲 {value.Processed:N0}/{value.Total:N0}{current}");

                    SetProgress(
                        value.Processed,
                        value.Total);
                });

            LibraryScanner scanner =
                new(
                    new AtlMetadataReader());

            LibraryPlan plan =
                await Task.Run(
                    async () =>
                        await scanner.BuildPlanAsync(
                            settings,
                            state,
                            progress,
                            cancellation.Token),
                    cancellation.Token);

            _state = state;
            _stateStore = stateStore;
            _plan = plan;

            OperationStatusText.Text =
                L(
                    "Building library view…",
                    "正在构建音乐库视图……");
            OperationProgressBar.IsIndeterminate =
                true;
            await Task.Yield();

            PopulateLibrary(
                settings,
                plan);
            UpdateLibrarySummary(
                plan);

            OpenDestinationButton.IsEnabled =
                Directory.Exists(
                    settings.Path
                        .DestinationDirectory);

            string summary =
                L(
                    $"{plan.TotalCount:N0} tracks · " +
                    $"{plan.PendingCount:N0} pending · " +
                    $"{plan.ErrorCount:N0} errors",
                    $"{plan.TotalCount:N0} 首歌曲 · " +
                    $"{plan.PendingCount:N0} 项待处理 · " +
                    $"{plan.ErrorCount:N0} 个错误");

            OperationStatusText.Text =
                summary;

            if (recordActivity)
            {
                AddActivity(
                    L(
                        "Scan complete",
                        "扫描完成"),
                    summary);
            }

            if (stateStore.RecoveryWarning
                is string recoveryWarning)
            {
                AddActivity(
                    L(
                        "State recovered",
                        "状态已恢复"),
                    recoveryWarning);

                ShowInfo(
                    InfoBarSeverity.Warning,
                    L(
                        "State recovered from backup",
                        "已从备份恢复状态"),
                    L(
                        "MusicLoom recovered the last valid state file.",
                        "MusicLoom 已恢复最近的有效状态文件。"));
            }
            else if (plan.ErrorCount > 0)
            {
                ShowInfo(
                    InfoBarSeverity.Warning,
                    L(
                        "Scan completed with errors",
                        "扫描完成，但有错误"),
                    L(
                        "Use the Errors filter to review them.",
                        "请使用“错误”筛选查看。"));
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            OperationStatusText.Text =
                L(
                    "Scan cancelled.",
                    "扫描已取消。");
            return false;
        }
        catch (Exception exception)
        {
            _plan = null;
            _allLibraryItems.Clear();
            UpdateLibraryEmptyState();

            HandleUiException(
                L(
                    "Scan failed.",
                    "扫描失败。"),
                exception);
            return false;
        }
        finally
        {
            _operationCancellation = null;
            SetBusy(
                busy: false,
                operationStatus:
                    OperationStatusText.Text);
        }
    }

    private void PopulateLibrary(
        MusicLoomSettings settings,
        LibraryPlan plan)
    {
        _allLibraryItems.Clear();

        foreach (PlanEntry entry
            in plan.Entries)
        {
            string title =
                Path.GetFileNameWithoutExtension(
                    entry.SourcePath);

            string artist =
                entry.Metadata?.AlbumArtist
                ?? entry.Metadata?.Artist
                ?? L(
                    "Unknown artist",
                    "未知艺术家");

            string album =
                entry.Metadata?.Album
                ?? L(
                    "Unknown album",
                    "未知专辑");

            string kind =
                entry.Status
                    == PlanEntryStatus.Error
                    ? "error"
                    : entry.Status
                        == PlanEntryStatus.Unchanged
                        ? "synced"
                        : entry.NeedsAudioCopy
                            ? "copy"
                            : "enrich";

            string status =
                StatusLabel(kind);

            string relative =
                MakeRelative(
                    settings.Path
                        .SourceDirectory,
                    entry.SourcePath);

            _allLibraryItems.Add(
                new LibraryDisplayItem(
                    entry,
                    kind,
                    title,
                    artist,
                    album,
                    relative,
                    status,
                    $"{artist} · {album}"));
        }

        LibraryListPanel.Visibility =
            Visibility.Visible;
        LibraryEmptyPanel.Visibility =
            Visibility.Collapsed;

        ApplyLibraryFilter();

        SyncButton.IsEnabled =
            !_busy
            && plan.PendingCount > 0
            && plan.ErrorCount == 0;

        SyncButton.Content =
            L(
                "Sync pending",
                "同步待处理");

        ScanButton.Content =
            L(
                "Refresh library",
                "刷新音乐库");

        if (LibraryList.Items.Count > 0)
        {
            LibraryList.SelectedIndex = 0;
        }
        else
        {
            ResetTrackDetails();
        }
    }

    private void UpdateLibrarySummary(
        LibraryPlan plan)
    {
        TracksValue.Text =
            plan.TotalCount.ToString("N0");
        PendingValue.Text =
            plan.PendingTrackCount.ToString("N0");
        AncillaryValue.Text =
            plan.AncillaryPendingCount.ToString("N0");
        ErrorsValue.Text =
            plan.ErrorCount.ToString("N0");
        LastScanValue.Text =
            DateTime.Now.ToString("t");
    }

    private void UpdateLibraryEmptyState()
    {
        bool configured =
            _settings is MusicLoomSettings settings
            && !string.IsNullOrWhiteSpace(
                settings.Path.SourceDirectory)
            && !string.IsNullOrWhiteSpace(
                settings.Path.DestinationDirectory);

        LibraryListPanel.Visibility =
            _plan is null
                ? Visibility.Collapsed
                : Visibility.Visible;

        LibraryEmptyPanel.Visibility =
            _plan is null
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (_plan is not null)
        {
            return;
        }

        LibraryEmptyTitleText.Text =
            configured
                ? L(
                    "Ready to scan",
                    "可以开始扫描")
                : L(
                    "Set up your music library",
                    "设置你的音乐库");

        LibraryEmptyBodyText.Text =
            configured
                ? L(
                    "Scan the source library to load tracks, artwork, lyrics and sync status.",
                    "扫描源音乐库以加载歌曲、封面、歌词和同步状态。")
                : L(
                    "Choose the source and destination once. MusicLoom keeps the source read-only.",
                    "只需选择一次源音乐库和目标目录。MusicLoom 会保持源目录只读。");

        OpenSettingsButton.Content =
            configured
                ? L(
                    "Review settings",
                    "查看设置")
                : L(
                    "Open settings",
                    "打开设置");

        ScanButton.Content =
            L(
                "Scan library",
                "扫描音乐库");

        SyncButton.Content =
            L(
                "Sync pending",
                "同步待处理");

        SyncButton.IsEnabled = false;
    }

    private void LibrarySearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        ApplyLibraryFilter();
    }

    private void LibraryFilterBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        ApplyLibraryFilter();
    }

    private void ApplyLibraryFilter()
    {
        if (LibraryList is null
            || LibrarySearchBox is null
            || LibraryFilterBox is null)
        {
            return;
        }

        string query =
            LibrarySearchBox.Text.Trim();

        string filter =
            (LibraryFilterBox.SelectedItem
                as ComboBoxItem)
                ?.Tag?.ToString()
            ?? "all";

        IEnumerable<LibraryDisplayItem>
            items = _allLibraryItems;

        if (filter == "pending")
        {
            items = items.Where(
                static item =>
                    item.Kind
                        is "copy" or "enrich");
        }
        else if (filter == "synced")
        {
            items = items.Where(
                static item =>
                    item.Kind == "synced");
        }
        else if (filter == "error")
        {
            items = items.Where(
                static item =>
                    item.Kind == "error");
        }

        if (!string.IsNullOrWhiteSpace(
                query))
        {
            items = items.Where(
                item =>
                    item.SearchText.Contains(
                        query,
                        StringComparison
                            .OrdinalIgnoreCase));
        }

        LibraryDisplayItem[] visible =
            items.ToArray();

        LibraryList.ItemsSource =
            visible;

        LibraryVisibleCountText.Text =
            L(
                $"{visible.Length:N0} tracks",
                $"{visible.Length:N0} 首歌曲");
    }

    private async void LibraryList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (LibraryList.SelectedItem
            is not LibraryDisplayItem item)
        {
            _selectedLibraryItem = null;
            ResetTrackDetails();
            return;
        }

        _selectedLibraryItem = item;
        await ShowTrackDetailsAsync(item);
    }

    private async Task ShowTrackDetailsAsync(
        LibraryDisplayItem item)
    {
        int requestId =
            ++_detailRequestId;

        ResetCandidateArea();

        TrackTitleText.Text =
            item.Title;
        TrackArtistText.Text =
            item.Artist;
        TrackAlbumText.Text =
            item.Album;
        TrackStatusText.Text =
            StatusLabel(item.Kind);
        TrackSourcePathText.Text =
            item.Entry.SourcePath;
        TrackDestinationPathText.Text =
            item.Entry.DestinationPath
                ?? string.Empty;

        FindMatchesButton.IsEnabled =
            !_busy
            && item.Kind != "error";

        CurrentMatchText.Text =
            GetCachedMatchText(
                item.Entry.SourcePath);

        LyricsPreviewText.Text =
            L(
                "Loading track details…",
                "正在加载歌曲详情……");
        LyricsSourceText.Text =
            string.Empty;
        CoverImage.Source = null;
        CoverPlaceholderText.Visibility =
            Visibility.Visible;

        MusicLoomSettings? settings =
            _settings;

        if (settings is null)
        {
            return;
        }

        try
        {
            TrackDetailPayload payload =
                await Task.Run(
                    () =>
                        LoadTrackDetail(
                            settings,
                            item.Entry));

            if (requestId !=
                _detailRequestId)
            {
                return;
            }

            await SetArtworkAsync(
                payload.Artwork);

            LyricsPreviewText.Text =
                string.IsNullOrWhiteSpace(
                    payload.Lyrics)
                    ? L(
                        "No lyrics found in the current local output.",
                        "当前本地文件中没有找到歌词。")
                    : FormatLyricsForDisplay(
                        payload.Lyrics);

            LyricsSourceText.Text =
                payload.LyricsSource;
        }
        catch (Exception exception)
        {
            if (requestId !=
                _detailRequestId)
            {
                return;
            }

            LyricsPreviewText.Text =
                L(
                    "Could not read track details.",
                    "无法读取歌曲详情。");

            AppDiagnostics.LogException(
                "Could not inspect track details.",
                exception);
        }
    }

    private TrackDetailPayload LoadTrackDetail(
        MusicLoomSettings settings,
        PlanEntry entry)
    {
        string inspectPath =
            entry.DestinationPath is
                string destination
            && File.Exists(destination)
                ? destination
                : entry.SourcePath;

        TrackInspection inspection =
            AtlTrackInspector.Read(
                inspectPath);

        byte[]? artwork =
            inspection.Artwork;

        if (artwork is null)
        {
            artwork =
                TryReadExternalArtwork(
                    settings,
                    entry);
        }

        string? lyrics =
            inspection.Lyrics;
        string lyricsSource =
            L(
                "Embedded lyrics",
                "嵌入歌词");

        string storage =
            LyricsStorageModes.Normalize(
                settings.Organizer.Lyrics
                    .StorageMode);

        if (storage
            == LyricsStorageModes.Sidecar)
        {
            string? sidecar =
                FindExistingSidecar(entry);

            if (sidecar is not null)
            {
                lyrics =
                    File.ReadAllText(sidecar);
                lyricsSource =
                    L(
                        $"Sidecar: {sidecar}",
                        $"独立歌词：{sidecar}");
            }
            else if (string.IsNullOrWhiteSpace(
                         lyrics))
            {
                lyricsSource =
                    L(
                        "No local sidecar or embedded lyrics",
                        "没有本地独立歌词或嵌入歌词");
            }
        }
        else if (string.IsNullOrWhiteSpace(
                     lyrics))
        {
            lyricsSource =
                L(
                    "No embedded lyrics",
                    "没有嵌入歌词");
        }

        return new TrackDetailPayload(
            artwork,
            lyrics,
            lyricsSource);
    }

    private static string? FindExistingSidecar(
        PlanEntry entry)
    {
        if (entry.DestinationPath
            is string destination)
        {
            string sidecar =
                SidecarLrcLyricsWriter
                    .GetSidecarPath(
                        destination);

            if (File.Exists(sidecar))
            {
                return sidecar;
            }
        }

        string sourceSidecar =
            SidecarLrcLyricsWriter
                .GetSidecarPath(
                    entry.SourcePath);

        return File.Exists(sourceSidecar)
            ? sourceSidecar
            : null;
    }

    private static byte[]?
        TryReadExternalArtwork(
            MusicLoomSettings settings,
            PlanEntry entry)
    {
        if (entry.DestinationPath
            is string destination)
        {
            string? directory =
                Path.GetDirectoryName(
                    destination);

            if (!string.IsNullOrWhiteSpace(
                    directory)
                && Directory.Exists(directory))
            {
                string? existing =
                    Directory
                        .EnumerateFiles(
                            directory,
                            "Cover.*",
                            SearchOption.TopDirectoryOnly)
                        .FirstOrDefault();

                if (existing is not null)
                {
                    return File.ReadAllBytes(
                        existing);
                }
            }
        }

        if (entry.Metadata is null)
        {
            return null;
        }

        string? sourceDirectory =
            Path.GetDirectoryName(
                entry.SourcePath);

        if (string.IsNullOrWhiteSpace(
                sourceDirectory))
        {
            return null;
        }

        string? cover =
            AlbumArtLocator.Find(
                sourceDirectory,
                entry.Metadata,
                onlyExplicit: false);

        return cover is null
            ? null
            : File.ReadAllBytes(cover);
    }

    private async Task SetArtworkAsync(
        byte[]? artwork)
    {
        if (artwork is null
            || artwork.Length == 0)
        {
            CoverImage.Source = null;
            CoverPlaceholderText.Visibility =
                Visibility.Visible;
            return;
        }

        using InMemoryRandomAccessStream
            stream = new();
        using DataWriter writer =
            new(stream);

        writer.WriteBytes(artwork);
        await writer.StoreAsync();
        writer.DetachStream();

        stream.Seek(0);

        BitmapImage bitmap = new();
        await bitmap.SetSourceAsync(stream);

        CoverImage.Source = bitmap;
        CoverPlaceholderText.Visibility =
            Visibility.Collapsed;
    }

    private async void FindMatchesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedLibraryItem
            is not LibraryDisplayItem item)
        {
            return;
        }

        FindMatchesButton.IsEnabled = false;
        UseSelectedMatchButton.IsEnabled = false;
        NeteaseCandidatesList.Items.Clear();
        CandidateLyricsPreviewText.Text =
            L(
                "Waiting for a candidate…",
                "等待选择候选……");
        CandidateStatusText.Text =
            L(
                "Fingerprinting and searching NetEase…",
                "正在生成指纹并搜索网易云音乐……");

        try
        {
            using NeteaseClient client =
                new();
            CompositeFingerprintPcmDecoder
                decoder = new();
            NeteaseTrackProvider provider =
                new(
                    client,
                    decoder);

            IReadOnlyList<NeteaseMatchCandidate>
                candidates =
                    await provider
                        .FindCandidatesAsync(
                            item.Entry.SourcePath);

            foreach (NeteaseMatchCandidate
                candidate in candidates)
            {
                NeteaseCandidatesList.Items.Add(
                    candidate);
            }

            CandidateStatusText.Text =
                candidates.Count == 0
                    ? L(
                        "No candidates found.",
                        "没有找到候选。")
                    : L(
                        $"{candidates.Count:N0} candidate(s) found. Select one to preview lyrics.",
                        $"找到 {candidates.Count:N0} 个候选。选择一个即可预览歌词。");

            if (candidates.Count > 0)
            {
                NeteaseCandidatesList
                    .SelectedIndex = 0;
            }
        }
        catch (Exception exception)
        {
            CandidateStatusText.Text =
                UserFacingErrors.Describe(
                    exception);
            AppDiagnostics.LogException(
                "Could not search NetEase candidates.",
                exception);
        }
        finally
        {
            FindMatchesButton.IsEnabled =
                !_busy
                && _selectedLibraryItem
                    is not null;
        }
    }

    private async void
        NeteaseCandidatesList_SelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
    {
        if (NeteaseCandidatesList
                .SelectedItem
            is not NeteaseMatchCandidate
                candidate)
        {
            UseSelectedMatchButton.IsEnabled =
                false;
            return;
        }

        UseSelectedMatchButton.IsEnabled =
            !_busy;

        CandidateLyricsPreviewText.Text =
            L(
                "Loading candidate lyrics…",
                "正在加载候选歌词……");

        try
        {
            using NeteaseClient client =
                new();

            LrcDocument? lyrics =
                await client.GetLyricsAsync(
                    candidate.TrackId);

            CandidateLyricsPreviewText.Text =
                lyrics is null
                || lyrics.Lines.Count == 0
                    ? L(
                        "This candidate has no lyrics.",
                        "这个候选没有歌词。")
                    : FormatLyricsForDisplay(
                        lyrics.ToString());
        }
        catch (Exception exception)
        {
            CandidateLyricsPreviewText.Text =
                UserFacingErrors.Describe(
                    exception);
        }
    }

    private async void
        UseSelectedMatchButton_Click(
            object sender,
            RoutedEventArgs e)
    {
        if (_selectedLibraryItem
                is not LibraryDisplayItem item
            || NeteaseCandidatesList
                .SelectedItem
                is not NeteaseMatchCandidate
                    candidate
            || _state is null
            || _stateStore is null)
        {
            return;
        }

        UseSelectedMatchButton.IsEnabled =
            false;

        try
        {
            FileInfo source =
                new(item.Entry.SourcePath);

            ulong quickHash =
                await Task.Run(
                    () =>
                        QuickFileHash.Compute(
                            item.Entry.SourcePath));

            _state.Tracks.TryGetValue(
                item.Entry.SourcePath,
                out TrackState? previous);

            TrackMetadata metadata =
                item.Entry.Metadata
                ?? new TrackMetadata(
                    null,
                    null,
                    null,
                    false);

            TrackState updated =
                previous is null
                    ? new TrackState
                    {
                        SourcePath =
                            item.Entry.SourcePath,
                        SourceSize =
                            source.Length,
                        SourceLastWriteUtcTicks =
                            source
                                .LastWriteTimeUtc
                                .Ticks,
                        QuickHash =
                            quickHash,
                        DestinationPath =
                            item.Entry
                                .DestinationPath,
                        MetadataCaptured = true,
                        Artist =
                            metadata.Artist,
                        AlbumArtist =
                            metadata.AlbumArtist,
                        Album =
                            metadata.Album,
                        HasEmbeddedArt =
                            metadata.HasEmbeddedArt,
                        ProcessedAt =
                            DateTimeOffset.UtcNow,
                    }
                    : previous;

            updated = updated with
            {
                SourceSize =
                    source.Length,
                SourceLastWriteUtcTicks =
                    source.LastWriteTimeUtc
                        .Ticks,
                QuickHash =
                    quickHash,
                Recognition =
                    RecognitionState.Matched,
                RecognitionCheckedAt =
                    DateTimeOffset.UtcNow,
                NeteaseSongId =
                    candidate.TrackId,
                Lyrics =
                    LyricsState.Unknown,
                LyricsCheckedAt = null,
                LyricsFingerprint = null,
                RecipeVersion =
                    string.Empty,
                ProcessedAt =
                    DateTimeOffset.UtcNow,
            };

            _state.Tracks[
                item.Entry.SourcePath] =
                updated;

            await _stateStore.SaveAsync(
                _state);

            CurrentMatchText.Text =
                L(
                    $"Selected: {candidate}",
                    $"已选择：{candidate}");

            AddActivity(
                L(
                    "Match selected",
                    "已选择匹配"),
                $"{item.Title} → {candidate.TrackId}");

            ShowInfo(
                InfoBarSeverity.Success,
                L(
                    "Match saved",
                    "匹配已保存"),
                L(
                    "The next sync will use this NetEase track.",
                    "下次同步会使用这个网易云歌曲。"));
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not save the selected match.",
                    "无法保存所选匹配。"),
                exception);
        }
        finally
        {
            UseSelectedMatchButton.IsEnabled =
                NeteaseCandidatesList
                    .SelectedItem is not null;
        }
    }

    private string GetCachedMatchText(
        string sourcePath)
    {
        if (_state is null
            || !_state.Tracks.TryGetValue(
                sourcePath,
                out TrackState? state)
            || state.NeteaseSongId
                is not long songId)
        {
            return L(
                "No match cached.",
                "尚未缓存匹配。");
        }

        return L(
            $"Cached NetEase ID: {songId}",
            $"已缓存网易云 ID：{songId}");
    }

    private void ResetCandidateArea()
    {
        NeteaseCandidatesList.Items.Clear();
        CandidateStatusText.Text =
            string.Empty;
        CandidateLyricsPreviewText.Text =
            L(
                "Find matches to compare NetEase candidates.",
                "搜索匹配后可比较网易云候选。");
        UseSelectedMatchButton.IsEnabled =
            false;
    }

    private void ResetTrackDetails()
    {
        TrackTitleText.Text =
            L(
                "Select a track",
                "选择一首歌曲");
        TrackArtistText.Text = "—";
        TrackAlbumText.Text = "—";
        TrackStatusText.Text = "—";
        TrackSourcePathText.Text =
            string.Empty;
        TrackDestinationPathText.Text =
            string.Empty;
        CoverImage.Source = null;
        CoverPlaceholderText.Visibility =
            Visibility.Visible;
        LyricsPreviewText.Text =
            L(
                "Select a track to inspect its lyrics.",
                "选择歌曲后可查看歌词。");
        LyricsSourceText.Text =
            string.Empty;
        CurrentMatchText.Text =
            L(
                "No match cached.",
                "尚未缓存匹配。");
        FindMatchesButton.IsEnabled =
            false;
        ResetCandidateArea();
    }

    private async void BrowseSourceButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        string? path =
            await PickFolderAsync();

        if (path is not null)
        {
            SourcePathBox.Text = path;
        }
    }

    private async void
        BrowseDestinationButton_Click(
            object sender,
            RoutedEventArgs e)
    {
        string? path =
            await PickFolderAsync();

        if (path is not null)
        {
            DestinationPathBox.Text = path;
        }
    }

    private async void BrowsePlaylistButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        string? path =
            await PickFolderAsync();

        if (path is not null)
        {
            PlaylistSourcePathBox.Text =
                path;
        }
    }

    private async Task<string?>
        PickFolderAsync()
    {
        try
        {
            FolderPicker picker = new()
            {
                SuggestedStartLocation =
                    PickerLocationId
                        .MusicLibrary,
            };

            picker.FileTypeFilter.Add("*");

            nint hwnd =
                WinRT.Interop.WindowNative
                    .GetWindowHandle(this);

            WinRT.Interop
                .InitializeWithWindow
                .Initialize(
                    picker,
                    hwnd);

            Windows.Storage.StorageFolder?
                folder =
                    await picker
                        .PickSingleFolderAsync();

            return folder is not null
                && !string.IsNullOrWhiteSpace(
                    folder.Path)
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
                    settings.Path
                        .SourceDirectory))
            {
                throw new
                    DirectoryNotFoundException(
                        L(
                            "The source library does not exist.",
                            "源音乐库不存在。"));
            }

            LibraryPathSafety
                .ValidateStatePath(
                    settings,
                    StatePath);

            await SettingsLoader.SaveAsync(
                ConfigPath,
                settings);

            _settings = settings;
            _uiLanguage =
                NormalizeLanguage(
                    settings.Ui.Language);

            ApplyLanguage();
            PopulateSettingsForm(settings);

            _plan = null;
            _allLibraryItems.Clear();
            UpdateLibraryEmptyState();
            ResetTrackDetails();

            SettingsStatusText.Text =
                L(
                    "Settings saved. Return to Library and scan.",
                    "设置已保存。返回“音乐库”并开始扫描。");

            ShowInfo(
                InfoBarSeverity.Success,
                L(
                    "Settings saved",
                    "设置已保存"),
                L(
                    "The library is ready to scan.",
                    "音乐库已可以开始扫描。"));

            NavigateTo("library");
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

    private MusicLoomSettings
        BuildSettingsFromForm()
    {
        double refreshHours =
            double.IsFinite(
                LyricsRefreshHoursBox.Value)
            && LyricsRefreshHoursBox.Value
                > 0
                ? LyricsRefreshHoursBox.Value
                : 24;

        string language =
            (LanguageBox.SelectedItem
                as ComboBoxItem)
                ?.Tag?.ToString()
            ?? _uiLanguage;

        string storageMode =
            (LyricsStorageModeBox
                .SelectedItem
                as ComboBoxItem)
                ?.Tag?.ToString()
            ?? LyricsStorageModes.Embedded;

        return new MusicLoomSettings
        {
            Verbose = true,
            Path = new PathSettings
            {
                SourceDirectory =
                    SourcePathBox.Text.Trim(),
                DestinationDirectory =
                    DestinationPathBox.Text
                        .Trim(),
                SourcePlaylistDirectory =
                    PlaylistSourcePathBox
                        .Text.Trim(),
                DestinationPlaylistBase =
                    string.IsNullOrWhiteSpace(
                        PlaylistBaseBox.Text)
                        ? "/storage/emulated/0/Music/"
                        : PlaylistBaseBox
                            .Text.Trim(),
            },
            Organizer =
                new OrganizerSettings
                {
                    Lyrics =
                        new LyricsSettings
                        {
                            Enabled =
                                LyricsEnabledToggle
                                    .IsOn,
                            OverrideExisting =
                                LyricsOverrideToggle
                                    .IsOn,
                            RefreshIntervalHours =
                                refreshHours,
                            StorageMode =
                                LyricsStorageModes
                                    .Normalize(
                                        storageMode),
                        },
                },
            Logging =
                _settings?.Logging
                ?? new LoggingSettings(),
            Ui = new UiSettings
            {
                Language =
                    NormalizeLanguage(
                        language),
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
            settings.Path
                .SourcePlaylistDirectory;
        PlaylistBaseBox.Text =
            settings.Path
                .DestinationPlaylistBase;

        LyricsEnabledToggle.IsOn =
            settings.Organizer.Lyrics
                .Enabled;
        LyricsOverrideToggle.IsOn =
            settings.Organizer.Lyrics
                .OverrideExisting;

        LyricsRefreshHoursBox.Value =
            settings.Organizer.Lyrics
                    .RefreshIntervalHours
                > 0
                    ? settings.Organizer
                        .Lyrics
                        .RefreshIntervalHours
                    : 24;

        string storage =
            LyricsStorageModes.Normalize(
                settings.Organizer.Lyrics
                    .StorageMode);

        LyricsStorageModeBox
            .SelectedIndex =
                storage
                == LyricsStorageModes.Sidecar
                    ? 1
                    : 0;

        string language =
            NormalizeLanguage(
                settings.Ui.Language);

        LanguageBox.SelectedIndex =
            language == "zh-CN"
                ? 1
                : 0;

        SettingsStorageText.Text =
            L(
                $"Settings: {ConfigPath}\nState: {StatePath}",
                $"设置：{ConfigPath}\n状态：{StatePath}");
    }

    private void
        OpenDestinationButton_Click(
            object sender,
            RoutedEventArgs e)
    {
        if (_settings is not
            MusicLoomSettings settings)
        {
            return;
        }

        TryOpenDirectory(
            settings.Path
                .DestinationDirectory,
            L(
                "destination library",
                "目标音乐库"));
    }

    private void OpenSettingsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        NavigateTo("settings");
    }

    private void
        OpenLogFolderButton_Click(
            object sender,
            RoutedEventArgs e)
    {
        try
        {
            AppDiagnostics
                .OpenLogDirectory();
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not open the log folder.",
                    "无法打开日志文件夹。"),
                exception);
        }
    }

    private void
        OpenDataFolderButton_Click(
            object sender,
            RoutedEventArgs e)
    {
        TryOpenDirectory(
            AppDiagnostics.DataDirectory,
            L(
                "MusicLoom data folder",
                "MusicLoom 数据文件夹"));
    }

    private void ClearActivityButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _activityItems.Clear();
        RefreshActivityList();
    }

    private void
        Navigation_SelectionChanged(
            NavigationView sender,
            NavigationViewSelectionChangedEventArgs
                args)
    {
        if (args.SelectedItemContainer
                ?.Tag
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
                    item =>
                        string.Equals(
                            item.Tag?.ToString(),
                            tag,
                            StringComparison
                                .Ordinal));

        if (target is not null)
        {
            Navigation.SelectedItem =
                target;
        }

        ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        LibraryPage.Visibility =
            tag == "library"
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
        string operationStatus)
    {
        _busy = busy;

        ScanButton.IsEnabled = !busy;
        SyncButton.IsEnabled =
            !busy
            && _plan is
                {
                    PendingCount: > 0,
                    ErrorCount: 0,
                };
        OpenDestinationButton.IsEnabled =
            !busy
            && _settings is
                MusicLoomSettings settings
            && Directory.Exists(
                settings.Path
                    .DestinationDirectory);

        CancelButton.Visibility =
            busy
                ? Visibility.Visible
                : Visibility.Collapsed;
        CancelButton.IsEnabled = busy;

        LibrarySearchBox.IsEnabled =
            !busy;
        LibraryFilterBox.IsEnabled =
            !busy;
        LibraryList.IsEnabled =
            !busy;
        FindMatchesButton.IsEnabled =
            !busy
            && _selectedLibraryItem
                is not null;
        UseSelectedMatchButton.IsEnabled =
            !busy
            && NeteaseCandidatesList
                .SelectedItem
                is not null;

        SaveSettingsButton.IsEnabled =
            !busy;
        BrowseSourceButton.IsEnabled =
            !busy;
        BrowseDestinationButton.IsEnabled =
            !busy;
        BrowsePlaylistButton.IsEnabled =
            !busy;
        SourcePathBox.IsEnabled = !busy;
        DestinationPathBox.IsEnabled =
            !busy;
        PlaylistSourcePathBox.IsEnabled =
            !busy;
        PlaylistBaseBox.IsEnabled = !busy;
        LyricsEnabledToggle.IsEnabled =
            !busy;
        LyricsOverrideToggle.IsEnabled =
            !busy;
        LyricsStorageModeBox.IsEnabled =
            !busy;
        LyricsRefreshHoursBox.IsEnabled =
            !busy;
        LanguageBox.IsEnabled = !busy;

        OperationProgressBar.Visibility =
            busy
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (busy)
        {
            OperationProgressBar
                .IsIndeterminate = true;
        }
        else
        {
            OperationProgressBar
                .IsIndeterminate = false;
            OperationProgressBar.Value = 0;
        }

        OperationStatusText.Text =
            operationStatus;
    }

    private void SetProgress(
        int processed,
        int total)
    {
        OperationProgressBar.Visibility =
            Visibility.Visible;

        if (total <= 0)
        {
            OperationProgressBar
                .IsIndeterminate = true;
            return;
        }

        OperationProgressBar
            .IsIndeterminate = false;

        OperationProgressBar.Value =
            Math.Clamp(
                processed * 100.0 / total,
                0,
                100);
    }

    private void ShowInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        AppInfoBar.Severity = severity;
        AppInfoBar.Title = title;
        AppInfoBar.Message = message;
        AppInfoBar.IsOpen = true;
    }

    private void AddActivity(
        string category,
        string message)
    {
        _activityItems.Insert(
            0,
            $"[{DateTime.Now:T}] " +
            $"{category} · {message}");

        if (_activityItems.Count > 300)
        {
            _activityItems.RemoveRange(
                300,
                _activityItems.Count - 300);
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
            UserFacingErrors.Describe(
                exception);

        AddActivity(
            L(
                "Error",
                "错误"),
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

            Process.Start(
                new ProcessStartInfo
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

    private string StatusLabel(
        string kind) =>
        kind switch
        {
            "synced" =>
                L(
                    "Up to date",
                    "已同步"),
            "copy" =>
                L(
                    "Needs copy",
                    "需要复制"),
            "enrich" =>
                L(
                    "Needs lyrics/state refresh",
                    "需要刷新歌词/状态"),
            "error" =>
                L(
                    "Error",
                    "错误"),
            _ => kind,
        };

    private string FriendlyStage(
        string stage) =>
        stage switch
        {
            "state" =>
                L(
                    "Preparing",
                    "准备中"),
            "inspect-lyrics" =>
                L(
                    "Checking lyrics",
                    "检查歌词"),
            "recognize" =>
                L(
                    "Recognizing",
                    "识别歌曲"),
            "fetch-lyrics" =>
                L(
                    "Fetching lyrics",
                    "获取歌词"),
            "write-lyrics" =>
                L(
                    "Writing lyrics",
                    "写入歌词"),
            "copy-audio" =>
                L(
                    "Copying audio",
                    "复制音频"),
            "copy-cover" =>
                L(
                    "Copying cover",
                    "复制封面"),
            "write-playlist" =>
                L(
                    "Writing playlist",
                    "写入播放列表"),
            "complete" =>
                L(
                    "Complete",
                    "完成"),
            "failed" =>
                L(
                    "Failed",
                    "失败"),
            _ => stage,
        };

    private void ApplyLanguage()
    {
        LibraryNav.Content =
            L("Library", "音乐库");
        ActivityNav.Content =
            L("Activity", "活动");
        SettingsNav.Content =
            L("Settings", "设置");

        LibraryTitleText.Text =
            L(
                "Music library",
                "音乐库");
        LibrarySubtitleText.Text =
            L(
                "Browse, inspect and sync your local collection.",
                "浏览、检查并同步你的本地音乐收藏。");

        ScanButton.Content =
            _plan is null
                ? L(
                    "Scan library",
                    "扫描音乐库")
                : L(
                    "Refresh library",
                    "刷新音乐库");

        SyncButton.Content =
            L(
                "Sync pending",
                "同步待处理");

        CancelButton.Content =
            L("Cancel", "取消");
        OpenDestinationButton.Content =
            L(
                "Open destination",
                "打开目标目录");

        TracksLabelText.Text =
            L("Tracks", "歌曲");
        PendingLabelText.Text =
            L("Pending tracks", "待处理歌曲");
        AncillaryLabelText.Text =
            L("Extra updates", "附加更新");
        ErrorsLabelText.Text =
            L("Errors", "错误");
        LastScanLabelText.Text =
            L(
                "Last scan",
                "上次扫描");

        LibrarySearchBox.PlaceholderText =
            L(
                "Search title, artist, album or path",
                "搜索歌曲、艺术家、专辑或路径");

        LibraryFilterAllItem.Content =
            L(
                "All tracks",
                "全部歌曲");
        LibraryFilterPendingItem.Content =
            L(
                "Pending",
                "待处理");
        LibraryFilterSyncedItem.Content =
            L(
                "Up to date",
                "已同步");
        LibraryFilterErrorsItem.Content =
            L(
                "Errors",
                "错误");

        LyricsTab.Header =
            L(
                "Lyrics",
                "歌词");
        MatchTab.Header =
            L(
                "NetEase match",
                "网易云匹配");
        FileTab.Header =
            L(
                "File",
                "文件");
        SourcePathHeadingText.Text =
            L(
                "Source",
                "源文件");
        DestinationPathHeadingText.Text =
            L(
                "Destination",
                "目标文件");
        FindMatchesButton.Content =
            L(
                "Find matches",
                "查找匹配");
        UseSelectedMatchButton.Content =
            L(
                "Use selected match",
                "使用所选匹配");

        ActivityTitleText.Text =
            L(
                "Activity",
                "活动");
        ActivitySubtitleText.Text =
            L(
                "Recent scans, syncs, recoveries and errors.",
                "最近的扫描、同步、恢复和错误记录。");
        OpenLogFolderButton.Content =
            L(
                "Open log folder",
                "打开日志目录");
        ClearActivityButton.Content =
            L(
                "Clear activity",
                "清空活动");

        SettingsTitleText.Text =
            L(
                "Settings",
                "设置");
        SettingsSubtitleText.Text =
            L(
                "Configure the library once; MusicLoom stores settings outside the app folder.",
                "只需配置一次音乐库；MusicLoom 会把设置保存在程序目录之外。");

        LibrarySettingsHeadingText.Text =
            L(
                "Library",
                "音乐库");

        SourcePathBox.Header =
            L(
                "Source library",
                "源音乐库");
        SourcePathBox.PlaceholderText =
            L(
                "Folder containing your original music",
                "包含原始音乐的文件夹");

        DestinationPathBox.Header =
            L(
                "Destination library",
                "目标音乐库");
        DestinationPathBox.PlaceholderText =
            L(
                "Separate output folder",
                "独立的输出目录");

        PlaylistSourcePathBox.Header =
            L(
                "MusicBee playlist folder (optional)",
                "MusicBee 播放列表目录（可选）");

        PlaylistBaseBox.Header =
            L(
                "Playlist path on phone",
                "手机上的播放列表路径");

        BrowseSourceButton.Content =
            L("Browse…", "浏览……");
        BrowseDestinationButton.Content =
            L("Browse…", "浏览……");
        BrowsePlaylistButton.Content =
            L("Browse…", "浏览……");

        LyricsSettingsHeadingText.Text =
            L(
                "Lyrics",
                "歌词");
        LyricsEnabledToggle.Header =
            L(
                "Fetch lyrics from NetEase",
                "从网易云获取歌词");
        LyricsOverrideToggle.Header =
            L(
                "Overwrite existing lyrics",
                "覆盖已有歌词");
        LyricsStorageModeBox.Header =
            L(
                "Store lyrics as",
                "歌词保存方式");
        LyricsStorageEmbeddedItem.Content =
            L(
                "Embedded in audio file",
                "嵌入音频文件");
        LyricsStorageSidecarItem.Content =
            L(
                "Sidecar .lrc file",
                "独立 .lrc 文件");

        RefreshSettingsHeadingText.Text =
            L(
                "Interface",
                "界面");
        LyricsRefreshHoursBox.Header =
            L(
                "Lyrics cache lifetime (hours)",
                "歌词缓存有效期（小时）");
        LanguageBox.Header =
            L(
                "Interface language",
                "界面语言");

        SaveSettingsButton.Content =
            L(
                "Save settings",
                "保存设置");
        OpenDataFolderButton.Content =
            L(
                "Open data folder",
                "打开数据目录");

        UpdateLibraryEmptyState();
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

    private static string FormatLyricsForDisplay(
        string rawLyrics)
    {
        if (string.IsNullOrWhiteSpace(rawLyrics))
        {
            return string.Empty;
        }

        LrcDocument document =
            LrcDocument.Parse(rawLyrics);

        if (document.Lines.Count == 0)
        {
            return rawLyrics.Trim();
        }

        string[] lines =
            document.Lines
                .Select(static line =>
                    line.Text.Trim())
                .Where(static line =>
                    line.Length > 0)
                .ToArray();

        return lines.Length == 0
            ? rawLyrics.Trim()
            : string.Join(
                Environment.NewLine,
                lines);
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

    private sealed record LibraryDisplayItem(
        PlanEntry Entry,
        string Kind,
        string Title,
        string Artist,
        string Album,
        string RelativePath,
        string StatusText,
        string ArtistAlbum)
    {
        public string SearchText =>
            $"{Title}\n{Artist}\n{Album}\n" +
            $"{RelativePath}\n{Entry.SourcePath}\n" +
            $"{Entry.DestinationPath}\n{Entry.Detail}";
    }

    private sealed record TrackDetailPayload(
        byte[]? Artwork,
        string? Lyrics,
        string LyricsSource);
}
