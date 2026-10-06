using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;

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

    private static readonly string DatabasePath =
        Path.Combine(
            AppDiagnostics.DataDirectory,
            "musicloom.db");

    private static readonly string LegacySnapshotPath =
        Path.Combine(
            AppDiagnostics.DataDirectory,
            "library-index.json");

    private readonly List<LibraryDisplayItem>
        _allLibraryItems = [];
    private readonly List<PlaylistDisplayItem>
        _playlists = [];
    private readonly List<string>
        _activityItems = [];

    private MusicLoomSettings? _settings;
    private StateDocument? _state;
    private StateStore? _stateStore;
    private LibraryPlan? _plan;
    private LibraryDisplayItem? _selectedLibraryItem;
    private LibraryDisplayItem? _contextLibraryItem;
    private LibraryDisplayItem? _playlistContextItem;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _settingsSaveCancellation;
    private bool _busy;
    private bool _loadingSettings = true;
    private int _detailRequestId;
    private int _artworkGeneration;
    private string _uiLanguage =
        DetectSystemLanguage();
    private string _accentColor = "system";
    private string? _currentLyricsRaw;
    private DispatcherTimer? _titleScrollTimer;
    private double _titleScrollDirection = 1;
    private DateTimeOffset _titleScrollPauseUntil;
    private byte[]? _currentArtworkBytes;
    private bool _showTranslations = true;

    public MainWindow()
    {
        StartupTrace.Mark(
            "MainWindow.ctor.enter");
        StartupTrace.Mark(
            "MainWindow.InitializeComponent.before");
        InitializeComponent();
        StartupTrace.Mark(
            "MainWindow.InitializeComponent.after");

        ApplyWindowIcon();

        LibraryFilterBox.SelectedIndex = 0;
        LibrarySortBox.SelectedIndex = 0;
        PlaylistSortBox.SelectedIndex = 0;
        LyricsStorageModeBox.SelectedIndex = 0;
        LanguageBox.SelectedIndex =
            IsChinese ? 1 : 0;
        Navigation.SelectedItem =
            Navigation.MenuItems[0];

        ApplyLanguage();
        HideDetailPane();
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

    private void ApplyWindowIcon()
    {
        try
        {
            string iconPath =
                Path.Combine(
                    AppDiagnostics.DataDirectory,
                    "MusicLoom.ico");

            Directory.CreateDirectory(
                AppDiagnostics.DataDirectory);

            using Stream? resource =
                typeof(MainWindow)
                    .Assembly
                    .GetManifestResourceStream(
                        "MusicLoom.AppIcon.ico");

            if (resource is null)
            {
                return;
            }

            using (FileStream output =
                new(
                    iconPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read))
            {
                resource.CopyTo(output);
            }

            AppWindow.SetIcon(iconPath);
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not apply the MusicLoom window icon.",
                exception);
        }
    }

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
            _accentColor =
                string.IsNullOrWhiteSpace(
                    settings.Ui.AccentColor)
                    ? "system"
                    : settings.Ui.AccentColor;

            PopulateSettingsForm(settings);
            ApplyLanguage();
            ApplyAppearance(settings);

            SettingsStatusText.Text =
                string.Empty;

            await LibraryDatabase.InitializeAsync(
                DatabasePath);

            await RestoreCachedLibraryAsync(
                settings);

            UpdateLibraryEmptyState();

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

            ShowInfo(
                InfoBarSeverity.Error,
                L(
                    "Settings error",
                    "设置错误"),
                UserFacingErrors.Describe(
                    exception));

            AddActivity(
                L(
                    "Settings error",
                    "设置错误"),
                UserFacingErrors.Describe(
                    exception));
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private async Task RestoreCachedLibraryAsync(
        MusicLoomSettings settings)
    {
        if (string.IsNullOrWhiteSpace(
                settings.Path.SourceDirectory)
            || string.IsNullOrWhiteSpace(
                settings.Path.DestinationDirectory))
        {
            return;
        }

        try
        {
            StateStore stateStore =
                new(StatePath);
            StateDocument state =
                await stateStore.LoadAsync();

            _stateStore = stateStore;
            _state = state;

            LibraryPlan? cachedPlan =
                await LibraryDatabase.LoadPlanAsync(
                    DatabasePath,
                    settings.Path.SourceDirectory,
                    settings.Path.DestinationDirectory);

            // One-version migration path from the earlier JSON snapshot.
            if (cachedPlan is null)
            {
                LibrarySnapshotDocument? snapshot =
                    await LibrarySnapshotStore.LoadAsync(
                        LegacySnapshotPath);

                if (snapshot is not null
                    && LibrarySnapshotStore.Matches(
                        snapshot,
                        settings.Path.SourceDirectory,
                        settings.Path.DestinationDirectory))
                {
                    cachedPlan =
                        LibrarySnapshotStore.ToPlan(
                            snapshot);
                }
            }

            cachedPlan ??=
                BuildCachedPlanFromState(
                    settings,
                    state);

            if (cachedPlan.TotalCount == 0)
            {
                return;
            }

            await LibraryDatabase.SavePlanAsync(
                DatabasePath,
                settings.Path.SourceDirectory,
                settings.Path.DestinationDirectory,
                cachedPlan);

            PopulateLibrary(
                settings,
                cachedPlan);
            UpdateLibrarySummary(
                cachedPlan);

            PlaylistDisplayItem[] playlists =
                await Task.Run(
                    () => BuildPlaylists(
                        settings));
            PopulatePlaylists(playlists);

            // A cached view is for continuity only. Sync always requires a
            // fresh scan so source/destination drift is revalidated.
            _plan = null;
            SyncButton.IsEnabled = false;
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not restore cached library view.",
                exception);
        }
    }

    private static LibraryPlan BuildCachedPlanFromState(
        MusicLoomSettings settings,
        StateDocument state)
    {
        string sourceRoot =
            Path.GetFullPath(
                settings.Path.SourceDirectory);

        List<PlanEntry> entries = [];

        foreach (TrackState track
            in state.Tracks.Values)
        {
            if (!File.Exists(track.SourcePath)
                || !IsPathInside(
                    track.SourcePath,
                    sourceRoot))
            {
                continue;
            }

            TrackMetadata metadata = new(
                track.Artist,
                track.AlbumArtist,
                track.Album,
                track.HasEmbeddedArt,
                track.Title);

            bool destinationMissing =
                string.IsNullOrWhiteSpace(
                    track.DestinationPath)
                || !File.Exists(
                    track.DestinationPath);

            PlanEntryStatus status =
                string.IsNullOrWhiteSpace(
                    track.RecipeVersion)
                || destinationMissing
                    ? PlanEntryStatus.Modified
                    : PlanEntryStatus.Unchanged;

            entries.Add(
                new PlanEntry(
                    track.SourcePath,
                    track.DestinationPath,
                    status,
                    metadata,
                    Detail:
                        destinationMissing
                            ? "Cached destination is missing."
                            : null,
                    NeedsAudioCopy:
                        destinationMissing));
        }

        return new LibraryPlan(entries);
    }

    private static bool IsPathInside(
        string path,
        string root)
    {
        string fullPath =
            Path.GetFullPath(path);
        string fullRoot =
            Path.GetFullPath(root)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(
            fullRoot,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private void ApplyAppearance(
        MusicLoomSettings settings)
    {
        Navigation.RequestedTheme =
            UiThemeModes.Normalize(
                settings.Ui.Theme) switch
            {
                UiThemeModes.Light =>
                    ElementTheme.Light,
                UiThemeModes.Dark =>
                    ElementTheme.Dark,
                _ => ElementTheme.Default,
            };

        AppearanceManager.Apply(
            this,
            settings.Ui);

        if (AppearanceManager.TryParseColor(
                settings.Ui.AccentColor,
                out Color accent))
        {
            Microsoft.UI.Xaml.Media.SolidColorBrush brush =
                new(accent);

            AccentPreview.Background = brush;
            SyncButton.Background = brush;
            OperationProgressBar.Foreground = brush;
        }
        else
        {
            AccentPreview.Background =
                Application.Current.Resources[
                    "AccentFillColorDefaultBrush"]
                as Microsoft.UI.Xaml.Media.Brush;

            SyncButton.ClearValue(
                Control.BackgroundProperty);
            OperationProgressBar.ClearValue(
                Control.ForegroundProperty);
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
        await ShowSyncWizardAsync();
    }

    private async Task ShowSyncWizardAsync()
    {
        TextBlock stepText = new()
        {
            FontSize = 12,
            Foreground =
                Application.Current.Resources[
                    "TextFillColorSecondaryBrush"]
                as Microsoft.UI.Xaml.Media.Brush,
        };

        TextBlock headingText = new()
        {
            FontSize = 20,
            FontWeight =
                new Windows.UI.Text.FontWeight
                {
                    Weight = 600,
                },
            TextWrapping =
                TextWrapping.Wrap,
        };

        TextBlock detailText = new()
        {
            Foreground =
                Application.Current.Resources[
                    "TextFillColorSecondaryBrush"]
                as Microsoft.UI.Xaml.Media.Brush,
            TextWrapping =
                TextWrapping.Wrap,
        };

        ProgressBar progressBar = new()
        {
            Height = 4,
            IsIndeterminate = true,
        };

        ListView errorsList = new()
        {
            MinHeight = 0,
            MaxHeight = 250,
            Visibility = Visibility.Collapsed,
        };

        Button startButton = new()
        {
            Content =
                L(
                    "Start sync",
                    "开始同步"),
            Style =
                (Style)Application.Current.Resources[
                    "AccentButtonStyle"],
            HorizontalAlignment =
                HorizontalAlignment.Right,
            IsEnabled = false,
        };

        StackPanel panel = new()
        {
            Width = 650,
            Spacing = 12,
        };
        panel.Children.Add(stepText);
        panel.Children.Add(headingText);
        panel.Children.Add(detailText);
        panel.Children.Add(progressBar);
        panel.Children.Add(errorsList);
        panel.Children.Add(startButton);

        ContentDialog dialog = new()
        {
            XamlRoot =
                ((FrameworkElement)Content)
                    .XamlRoot,
            Title =
                L(
                    "Sync library",
                    "同步音乐库"),
            CloseButtonText =
                L(
                    "Close",
                    "关闭"),
            Content = panel,
        };

        MusicLoomSettings? settings = null;
        StateStore? stateStore = null;
        StateDocument? state = null;
        LibraryPlan? plan = null;
        bool operationRunning = false;
        bool completedSync = false;

        using CancellationTokenSource
            cancellation = new();

        _operationCancellation =
            cancellation;
        _busy = true;
        ScanButton.IsEnabled = false;
        SyncButton.IsEnabled = false;

        void SetStep(
            string step,
            string heading,
            string detail,
            bool indeterminate = true)
        {
            stepText.Text = step;
            headingText.Text = heading;
            detailText.Text = detail;
            progressBar.Visibility =
                Visibility.Visible;
            progressBar.IsIndeterminate =
                indeterminate;
        }

        void ShowErrors(
            IEnumerable<string> errors)
        {
            string[] rows =
                errors.ToArray();

            errorsList.ItemsSource = rows;
            errorsList.Visibility =
                rows.Length == 0
                    ? Visibility.Collapsed
                    : Visibility.Visible;
        }

        async Task RunPreflightAsync()
        {
            operationRunning = true;
            startButton.IsEnabled = false;
            ShowErrors([]);

            SetStep(
                L(
                    "Step 1 of 3",
                    "第 1 步，共 3 步"),
                L(
                    "Checking the library",
                    "正在检查音乐库"),
                L(
                    "MusicLoom is validating source files, destination state, covers and playlists without changing the current view.",
                    "MusicLoom 正在校验源文件、目标状态、封面和播放列表，不会刷新当前列表。"));

            try
            {
                settings =
                    await SettingsLoader.LoadAsync(
                        ConfigPath,
                        cancellation.Token);

                LibraryPathSafety.ValidateStatePath(
                    settings,
                    StatePath);

                stateStore =
                    new StateStore(
                        StatePath);
                state =
                    await stateStore.LoadAsync(
                        cancellation.Token);

                Progress<ScanProgress>
                    scanProgress = new(value =>
                    {
                        if (value.Stage
                            == "ancillary")
                        {
                            detailText.Text =
                                L(
                                    "Checking covers and playlists…",
                                    "正在检查封面和播放列表……");
                            progressBar.IsIndeterminate =
                                true;
                            return;
                        }

                        if (value.Stage
                            == "complete")
                        {
                            detailText.Text =
                                L(
                                    "Preparing sync summary…",
                                    "正在生成同步摘要……");
                            progressBar.IsIndeterminate =
                                true;
                            return;
                        }

                        progressBar.IsIndeterminate =
                            false;
                        progressBar.Value =
                            value.Total <= 0
                                ? 0
                                : Math.Clamp(
                                    value.Processed
                                    * 100.0
                                    / value.Total,
                                    0,
                                    100);

                        detailText.Text =
                            L(
                                $"Checking tracks {value.Processed:N0}/{value.Total:N0}",
                                $"正在检查歌曲 {value.Processed:N0}/{value.Total:N0}");
                    });

                LibraryScanner scanner =
                    new(
                        new AtlMetadataReader());

                plan =
                    await Task.Run(
                        async () =>
                            await scanner.BuildPlanAsync(
                                settings,
                                state,
                                scanProgress,
                                cancellation.Token),
                        cancellation.Token);

                await stateStore.SaveAsync(
                    state,
                    cancellation.Token);

                if (plan.ErrorCount > 0)
                {
                    SetStep(
                        L(
                            "Step 1 of 3",
                            "第 1 步，共 3 步"),
                        L(
                            "The library has scan errors",
                            "音乐库存在扫描错误"),
                        L(
                            "Fix these items before syncing.",
                            "请先处理以下项目，再进行同步。"),
                        indeterminate: false);

                    progressBar.Visibility =
                        Visibility.Collapsed;

                    ShowErrors(
                        plan.Entries
                            .Where(static entry =>
                                entry.Status
                                    == PlanEntryStatus.Error)
                            .Select(entry =>
                                $"{Path.GetFileName(entry.SourcePath)} · {entry.Detail}"));

                    return;
                }

                if (plan.PendingCount == 0)
                {
                    SetStep(
                        L(
                            "Step 1 of 3",
                            "第 1 步，共 3 步"),
                        L(
                            "Everything is already synced",
                            "已经全部同步"),
                        L(
                            "No files, lyrics, covers or playlists need changes.",
                            "没有需要更新的文件、歌词、封面或播放列表。"),
                        indeterminate: false);

                    progressBar.Visibility =
                        Visibility.Collapsed;
                    return;
                }

                SetStep(
                    L(
                        "Step 1 of 3",
                        "第 1 步，共 3 步"),
                    L(
                        "Ready to sync",
                        "可以开始同步"),
                    L(
                        $"{plan.PendingTrackCount:N0} pending tracks · {plan.AudioCopyCount:N0} audio copies · {plan.CoverWriteCount:N0} covers · {plan.PlaylistWriteCount:N0} playlists",
                        $"{plan.PendingTrackCount:N0} 首歌曲待处理 · {plan.AudioCopyCount:N0} 个音频复制 · {plan.CoverWriteCount:N0} 个封面 · {plan.PlaylistWriteCount:N0} 个播放列表"),
                    indeterminate: false);

                progressBar.Visibility =
                    Visibility.Collapsed;
                startButton.IsEnabled = true;
            }
            catch (OperationCanceledException)
            {
                detailText.Text =
                    L(
                        "Check cancelled.",
                        "检查已取消。");
            }
            catch (Exception exception)
            {
                AppDiagnostics.LogException(
                    "Sync wizard preflight failed.",
                    exception);

                SetStep(
                    L(
                        "Step 1 of 3",
                        "第 1 步，共 3 步"),
                    L(
                        "Could not check the library",
                        "无法检查音乐库"),
                    UserFacingErrors.Describe(
                        exception),
                    indeterminate: false);
                progressBar.Visibility =
                    Visibility.Collapsed;
            }
            finally
            {
                operationRunning = false;
            }
        }

        async Task RunSyncAsync()
        {
            if (settings is null
                || stateStore is null
                || state is null
                || plan is null)
            {
                return;
            }

            operationRunning = true;
            startButton.IsEnabled = false;
            ShowErrors([]);

            SetStep(
                L(
                    "Step 2 of 3",
                    "第 2 步，共 3 步"),
                L(
                    "Syncing",
                    "正在同步"),
                L(
                    "Preparing local files…",
                    "正在准备本地文件……"));

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
                    LyricsStorageModes.Normalize(
                        settings.Organizer.Lyrics
                            .StorageMode)
                        == LyricsStorageModes.Sidecar
                            ? new SidecarLrcLyricsWriter()
                            : new TagLibSharpLyricsWriter();

                SyncExecutor executor =
                    new(
                        new LocalLibraryMaterializer(),
                        provider,
                        provider,
                        lyricsWriter);

                Progress<SyncExecutionProgress>
                    syncProgress = new(value =>
                    {
                        progressBar.IsIndeterminate =
                            value.Total <= 0;
                        if (value.Total > 0)
                        {
                            progressBar.Value =
                                Math.Clamp(
                                    value.Processed
                                    * 100.0
                                    / value.Total,
                                    0,
                                    100);
                        }

                        detailText.Text =
                            $"{FriendlyStage(value.Stage)} · " +
                            $"{value.Processed:N0}/{value.Total:N0} · " +
                            $"{Path.GetFileName(value.SourcePath)}";
                    });

                SyncExecutionSummary summary =
                    await Task.Run(
                        async () =>
                            await executor.ExecuteAsync(
                                settings,
                                plan,
                                state,
                                stateStore,
                                syncProgress,
                                cancellation.Token),
                        cancellation.Token);

                completedSync = true;

                string summaryText =
                    L(
                        $"{summary.Local.CopiedTracks:N0} audio · {summary.Local.CopiedCovers:N0} covers · {summary.Local.WrittenPlaylists:N0} playlists · {summary.WrittenLyrics:N0} lyrics · {summary.Errors.Count:N0} errors",
                        $"音频 {summary.Local.CopiedTracks:N0} · 封面 {summary.Local.CopiedCovers:N0} · 播放列表 {summary.Local.WrittenPlaylists:N0} · 歌词 {summary.WrittenLyrics:N0} · 错误 {summary.Errors.Count:N0}");

                SetStep(
                    L(
                        "Step 3 of 3",
                        "第 3 步，共 3 步"),
                    summary.Errors.Count == 0
                        ? L(
                            "Sync complete",
                            "同步完成")
                        : L(
                            "Sync complete with errors",
                            "同步完成，但有错误"),
                    summaryText,
                    indeterminate: false);

                progressBar.Value = 100;

                string[] errorRows =
                    summary.Errors
                        .Select(error =>
                            $"{Path.GetFileName(error.SourcePath)} · {FriendlyStage(error.Stage)} · {error.Message}")
                        .ToArray();

                ShowErrors(errorRows);

                LastRunTitle.Text =
                    summary.Errors.Count == 0
                        ? L(
                            "Last sync completed",
                            "上次同步已完成")
                        : L(
                            "Last sync completed with errors",
                            "上次同步完成，但有错误");
                LastRunSummaryText.Text =
                    summaryText;

                AddActivity(
                    summary.Errors.Count == 0
                        ? L(
                            "Sync complete",
                            "同步完成")
                        : L(
                            "Sync warning",
                            "同步警告"),
                    summaryText);

                foreach (TrackSyncError error
                    in summary.Errors)
                {
                    AddActivity(
                        L(
                            "Track error",
                            "歌曲错误"),
                        $"{Path.GetFileName(error.SourcePath)} · {FriendlyStage(error.Stage)} · {error.Message}");
                }

                if (summary.Errors.Count > 0)
                {
                    errorsList.ScrollIntoView(
                        errorRows.FirstOrDefault());
                }
            }
            catch (OperationCanceledException)
            {
                SetStep(
                    L(
                        "Step 3 of 3",
                        "第 3 步，共 3 步"),
                    L(
                        "Sync cancelled",
                        "同步已取消"),
                    L(
                        "Completed work was checkpointed.",
                        "已完成的工作已经保存。"),
                    indeterminate: false);
                progressBar.Visibility =
                    Visibility.Collapsed;
            }
            catch (Exception exception)
            {
                AppDiagnostics.LogException(
                    "Sync wizard failed.",
                    exception);

                SetStep(
                    L(
                        "Step 3 of 3",
                        "第 3 步，共 3 步"),
                    L(
                        "Sync failed",
                        "同步失败"),
                    UserFacingErrors.Describe(
                        exception),
                    indeterminate: false);
                progressBar.Visibility =
                    Visibility.Collapsed;
            }
            finally
            {
                operationRunning = false;
            }
        }

        startButton.Click +=
            async (_, _) =>
                await RunSyncAsync();

        dialog.Opened +=
            async (_, _) =>
                await RunPreflightAsync();

        dialog.Closing +=
            (_, args) =>
            {
                if (!operationRunning)
                {
                    return;
                }

                args.Cancel = true;
                cancellation.Cancel();
                detailText.Text =
                    L(
                        "Cancelling…",
                        "正在取消……");
            };

        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            if (!cancellation.IsCancellationRequested)
            {
                await cancellation
                    .CancelAsync();
            }

            _operationCancellation = null;
            _busy = false;
            ScanButton.IsEnabled = true;

            if (completedSync
                && settings is not null)
            {
                await RefreshLibraryAfterSyncAsync(
                    settings);
            }
            else
            {
                SyncButton.IsEnabled =
                    _plan is
                    {
                        PendingCount: > 0,
                        ErrorCount: 0,
                    };
            }
        }
    }

    private async Task RefreshLibraryAfterSyncAsync(
        MusicLoomSettings settings)
    {
        try
        {
            StateStore stateStore =
                new(StatePath);
            StateDocument state =
                await stateStore.LoadAsync();

            LibraryScanner scanner =
                new(
                    new AtlMetadataReader());

            LibraryPlan freshPlan =
                await Task.Run(
                    async () =>
                        await scanner.BuildPlanAsync(
                            settings,
                            state));

            await stateStore.SaveAsync(
                state);

            _settings = settings;
            _stateStore = stateStore;
            _state = state;
            _plan = freshPlan;

            PopulateLibrary(
                settings,
                freshPlan);
            UpdateLibrarySummary(
                freshPlan);

            await LibraryDatabase.SavePlanAsync(
                DatabasePath,
                settings.Path.SourceDirectory,
                settings.Path.DestinationDirectory,
                freshPlan);

            PopulatePlaylists(
                await Task.Run(
                    () => BuildPlaylists(
                        settings)));

            OperationStatusText.Text =
                L(
                    $"{freshPlan.TotalCount:N0} tracks · {freshPlan.PendingTrackCount:N0} pending · {freshPlan.ErrorCount:N0} errors",
                    $"{freshPlan.TotalCount:N0} 首歌曲 · {freshPlan.PendingTrackCount:N0} 首待同步 · {freshPlan.ErrorCount:N0} 个错误");
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not refresh the library after sync.",
                exception);

            ShowInfo(
                InfoBarSeverity.Warning,
                L(
                    "Sync finished, but the library view could not refresh",
                    "同步已完成，但无法刷新音乐库视图"),
                UserFacingErrors.Describe(
                    exception));
        }
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

        await cancellation.CancelAsync();
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
                    "Choose a source and destination in Settings.",
                    "请在“设置”中选择源音乐库和目标目录。"));
            return false;
        }

        using CancellationTokenSource
            cancellation = new();
        _operationCancellation =
            cancellation;

        SetBusy(
            true,
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

            // Scans may enrich MusicLoom's private state cache (for example
            // migrating TITLE metadata) but never mutate the source library.
            await stateStore.SaveAsync(
                state,
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

            await LibraryDatabase.SavePlanAsync(
                DatabasePath,
                settings.Path.SourceDirectory,
                settings.Path.DestinationDirectory,
                plan,
                cancellation.Token);

            PlaylistDisplayItem[] playlists =
                await Task.Run(
                    () =>
                        BuildPlaylists(
                            settings),
                    cancellation.Token);
            PopulatePlaylists(playlists);

            UpdateLibrarySummary(plan);

            string summary =
                L(
                    $"{plan.TotalCount:N0} tracks · " +
                    $"{plan.PendingTrackCount:N0} pending · " +
                    $"{plan.ErrorCount:N0} errors",
                    $"{plan.TotalCount:N0} 首歌曲 · " +
                    $"{plan.PendingTrackCount:N0} 首待同步 · " +
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
            LibraryList.ItemsSource = null;
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
                false,
                OperationStatusText.Text);
        }
    }

    private void PopulateLibrary(
        MusicLoomSettings settings,
        LibraryPlan plan)
    {
        _allLibraryItems.Clear();
        int artworkGeneration =
            ++_artworkGeneration;

        foreach (PlanEntry entry
            in plan.Entries)
        {
            string title =
                entry.Metadata?.Title
                ?? Path.GetFileNameWithoutExtension(
                    entry.SourcePath);

            string artist =
                FormatArtistsForDisplay(
                    entry.Metadata?.AlbumArtist
                    ?? entry.Metadata?.Artist)
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
                        : "pending";

            string relative =
                MakeRelative(
                    settings.Path.SourceDirectory,
                    entry.SourcePath);

            _allLibraryItems.Add(
                new LibraryDisplayItem(
                    entry,
                    kind,
                    title,
                    artist,
                    album,
                    relative,
                    StatusLabel(kind)));
        }

        LibraryEmptyPanel.Visibility =
            Visibility.Collapsed;
        LibraryList.Visibility =
            Visibility.Visible;

        ApplyLibraryFilter();

        SyncButton.IsEnabled =
            !_busy
            && plan.PendingCount > 0
            && plan.ErrorCount == 0;

        _ = LoadArtworkThumbnailsAsync(
            settings,
            artworkGeneration);

        if (LibraryList.Items.Count > 0)
        {
            LibraryList.SelectedIndex = -1;
        }

        HideDetailPane();
    }

    private void UpdateLibrarySummary(
        LibraryPlan plan)
    {
        PendingCountText.Text =
            plan.PendingTrackCount.ToString("N0");
        ErrorCountText.Text =
            plan.ErrorCount.ToString("N0");
    }

    private void UpdateLibraryEmptyState()
    {
        bool configured =
            _settings is MusicLoomSettings settings
            && !string.IsNullOrWhiteSpace(
                settings.Path.SourceDirectory)
            && !string.IsNullOrWhiteSpace(
                settings.Path.DestinationDirectory);

        bool empty =
            _allLibraryItems.Count == 0;

        LibraryList.Visibility =
            empty
                ? Visibility.Collapsed
                : Visibility.Visible;
        LibraryEmptyPanel.Visibility =
            empty
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (!empty)
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
                    "Refresh the library to load tracks, artwork, lyrics and sync status.",
                    "刷新音乐库以加载歌曲、封面、歌词和同步状态。")
                : L(
                    "Choose the source and destination once. MusicLoom keeps the source read-only.",
                    "只需选择一次源音乐库和目标目录。MusicLoom 会保持源目录只读。");
    }

    private void LibrarySearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e) =>
        ApplyLibraryFilter();

    private void LibraryFilterBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        ApplyLibraryFilter();

    private void LibrarySortBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        ApplyLibraryFilter();

    private void ApplyLibraryFilter()
    {
        if (LibraryList is null
            || LibrarySearchBox is null
            || LibraryFilterBox is null
            || LibrarySortBox is null)
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

        string sort =
            (LibrarySortBox.SelectedItem
                as ComboBoxItem)
                ?.Tag?.ToString()
            ?? "title";

        IEnumerable<LibraryDisplayItem>
            items = _allLibraryItems;

        if (filter != "all")
        {
            items = items.Where(
                item =>
                    string.Equals(
                        item.Kind,
                        filter,
                        StringComparison.Ordinal));
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

        items = sort switch
        {
            "album" =>
                items
                    .OrderBy(
                        static item =>
                            item.Album,
                        StringComparer
                            .CurrentCultureIgnoreCase)
                    .ThenBy(
                        static item =>
                            item.Title,
                        StringComparer
                            .CurrentCultureIgnoreCase),
            "artist" =>
                items
                    .OrderBy(
                        static item =>
                            item.Artist,
                        StringComparer
                            .CurrentCultureIgnoreCase)
                    .ThenBy(
                        static item =>
                            item.Title,
                        StringComparer
                            .CurrentCultureIgnoreCase),
            _ =>
                items.OrderBy(
                    static item =>
                        item.Title,
                    StringComparer
                        .CurrentCultureIgnoreCase),
        };

        LibraryDisplayItem[] visible =
            items.ToArray();

        LibraryDisplayItem?
            selected =
                LibraryList.SelectedItem
                    as LibraryDisplayItem;

        LibraryList.ItemsSource =
            visible;

        if (selected is not null
            && visible.Contains(selected))
        {
            LibraryList.SelectedItem =
                selected;
        }
        else if (selected is not null)
        {
            HideDetailPane();
        }

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
            HideDetailPane();
            ResetTrackDetails();
            return;
        }

        _selectedLibraryItem = item;
        ShowDetailPane();
        await ShowTrackDetailsAsync(item);
    }

    private void ShowDetailPane()
    {
        LibraryListColumn.Width =
            new GridLength(
                5,
                GridUnitType.Star);
        LibrarySplitterColumn.Width =
            new GridLength(6);
        LibraryDetailColumn.Width =
            new GridLength(
                7,
                GridUnitType.Star);
        LibraryDetailColumn.MinWidth = 420;

        LibrarySplitter.Visibility =
            Visibility.Visible;
        DetailPane.Visibility =
            Visibility.Visible;
    }

    private void HideDetailPane()
    {
        LibraryListColumn.Width =
            new GridLength(
                1,
                GridUnitType.Star);
        LibrarySplitterColumn.Width =
            new GridLength(0);
        LibraryDetailColumn.MinWidth = 0;
        LibraryDetailColumn.Width =
            new GridLength(0);

        LibrarySplitter.Visibility =
            Visibility.Collapsed;
        DetailPane.Visibility =
            Visibility.Collapsed;
    }

    private void CloseDetailButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        LibraryList.SelectedItem = null;
        _selectedLibraryItem = null;
        HideDetailPane();
        ResetTrackDetails();
    }

    private void ResetTrackDetails()
    {
        StopTitleAutoScroll();
        TrackTitleScroller.ChangeView(
            0,
            null,
            null,
            disableAnimation: true);

        _currentLyricsRaw = null;
        _currentArtworkBytes = null;

        TrackTitleText.Text = string.Empty;
        TrackArtistText.Text = string.Empty;
        TrackAlbumText.Text = string.Empty;
        TrackStatusText.Text = string.Empty;
        TrackStatusIcon.Glyph = "\uE823";
        TrackStatusIcon.Foreground =
            new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Color.FromArgb(255, 202, 116, 0));

        CoverImage.Source = null;
        CoverPlaceholderText.Visibility =
            Visibility.Visible;

        LyricsSourceText.Text = string.Empty;
        LyricsList.ItemsSource =
            Array.Empty<LyricDisplayLine>();

        CurrentMatchText.Text =
            L(
                "No NetEase match cached.",
                "尚未缓存网易云匹配。");

        TrackSourcePathText.Text = string.Empty;
        TrackDestinationPathText.Text = string.Empty;
        TrackFormatText.Text = string.Empty;
        TrackSizeText.Text = string.Empty;
        TrackModifiedText.Text = string.Empty;
        TrackCreatedText.Text = string.Empty;
        TrackDestinationSizeText.Text = string.Empty;
        TrackDestinationModifiedText.Text = string.Empty;
    }

    private void ConfigureTitleAutoScroll()
    {
        StopTitleAutoScroll();

        TrackTitleScroller.ChangeView(
            0,
            null,
            null,
            disableAnimation: true);

        if (_settings?.Ui.AutoScrollLongTitles
            != true)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(
            () =>
            {
                TrackTitleScroller.UpdateLayout();

                if (TrackTitleScroller.ScrollableWidth
                    <= 1)
                {
                    return;
                }

                _titleScrollDirection = 1;
                _titleScrollPauseUntil =
                    DateTimeOffset.UtcNow
                        .AddSeconds(1.2);

                DispatcherTimer timer = new()
                {
                    Interval =
                        TimeSpan.FromMilliseconds(
                            24),
                };

                timer.Tick +=
                    TitleScrollTimer_Tick;

                _titleScrollTimer = timer;
                timer.Start();
            });
    }

    private void StopTitleAutoScroll()
    {
        if (_titleScrollTimer is null)
        {
            return;
        }

        _titleScrollTimer.Stop();
        _titleScrollTimer.Tick -=
            TitleScrollTimer_Tick;
        _titleScrollTimer = null;
    }

    private void TitleScrollTimer_Tick(
        object? sender,
        object e)
    {
        if (_settings?.Ui.AutoScrollLongTitles
                != true
            || DetailPane.Visibility
                != Visibility.Visible)
        {
            StopTitleAutoScroll();
            return;
        }

        if (DateTimeOffset.UtcNow
            < _titleScrollPauseUntil)
        {
            return;
        }

        double max =
            TrackTitleScroller.ScrollableWidth;

        if (max <= 1)
        {
            StopTitleAutoScroll();
            return;
        }

        double next =
            TrackTitleScroller.HorizontalOffset
            + _titleScrollDirection * 0.9;

        if (next >= max)
        {
            next = max;
            _titleScrollDirection = -1;
            _titleScrollPauseUntil =
                DateTimeOffset.UtcNow
                    .AddSeconds(1.1);
        }
        else if (next <= 0)
        {
            next = 0;
            _titleScrollDirection = 1;
            _titleScrollPauseUntil =
                DateTimeOffset.UtcNow
                    .AddSeconds(1.1);
        }

        TrackTitleScroller.ChangeView(
            next,
            null,
            null,
            disableAnimation: true);
    }

    private async Task ShowTrackDetailsAsync(
        LibraryDisplayItem item)
    {
        int requestId =
            ++_detailRequestId;

        TrackTitleText.Text =
            item.Title;
        ConfigureTitleAutoScroll();
        TrackArtistText.Text =
            item.Artist;
        TrackAlbumText.Text =
            item.Album;
        TrackStatusText.Text =
            item.StatusText;
        TrackStatusIcon.Glyph =
            item.StatusGlyph;
        TrackStatusIcon.Foreground =
            item.StatusBrush;

        CurrentMatchText.Text =
            GetCachedMatchText(
                item.Entry.SourcePath);

        CoverImage.Source =
            item.Artwork;
        CoverPlaceholderText.Visibility =
            item.Artwork is null
                ? Visibility.Visible
                : Visibility.Collapsed;

        LyricsSourceText.Text =
            string.Empty;
        LyricsList.ItemsSource =
            Array.Empty<LyricDisplayLine>();

        try
        {
            MusicLoomSettings settings =
                _settings
                ?? throw new InvalidOperationException(
                    "Settings are not loaded.");

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

            if (payload.Artwork
                is { Length: > 0 })
            {
                BitmapImage bitmap =
                    await CreateBitmapImageAsync(
                        payload.Artwork,
                        decodePixelWidth: 0);
                item.Artwork = bitmap;
                CoverImage.Source = bitmap;
                CoverPlaceholderText.Visibility =
                    Visibility.Collapsed;
            }

            _currentArtworkBytes =
                payload.Artwork;
            _currentLyricsRaw =
                payload.Lyrics;
            LyricsSourceText.Text =
                payload.LyricsSource;
            RenderLyrics();

            TrackSourcePathText.Text =
                payload.SourcePath;
            TrackDestinationPathText.Text =
                payload.DestinationPath
                ?? L(
                    "Not created yet",
                    "尚未创建");
            TrackFormatText.Text =
                payload.Format;
            TrackSizeText.Text =
                payload.SourceSize;
            TrackModifiedText.Text =
                payload.SourceModified;
            TrackCreatedText.Text =
                payload.SourceCreated;
            TrackDestinationSizeText.Text =
                payload.DestinationSize;
            TrackDestinationModifiedText.Text =
                payload.DestinationModified;
        }
        catch (Exception exception)
        {
            if (requestId !=
                _detailRequestId)
            {
                return;
            }

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
            inspection.Artwork
            ?? TryReadExternalArtwork(
                settings,
                entry);

        string? lyrics =
            inspection.Lyrics;
        string lyricsSource =
            L(
                "Embedded lyrics",
                "嵌入歌词");

        string? sidecar =
            FindExistingSidecar(entry);
        if (sidecar is not null)
        {
            lyrics =
                File.ReadAllText(sidecar);
            lyricsSource =
                L(
                    $"Sidecar · {Path.GetFileName(sidecar)}",
                    $"独立歌词 · {Path.GetFileName(sidecar)}");
        }
        else if (string.IsNullOrWhiteSpace(
                     lyrics))
        {
            lyricsSource =
                L(
                    "No local lyrics",
                    "没有本地歌词");
        }

        FileInfo source =
            new(entry.SourcePath);
        FileInfo? destinationInfo =
            entry.DestinationPath is
                string destinationPath
            && File.Exists(destinationPath)
                ? new FileInfo(destinationPath)
                : null;

        return new TrackDetailPayload(
            artwork,
            lyrics,
            lyricsSource,
            entry.SourcePath,
            entry.DestinationPath,
            Path.GetExtension(
                entry.SourcePath)
                .TrimStart('.')
                .ToUpperInvariant(),
            FormatBytes(source.Length),
            source.LastWriteTime.ToString("G"),
            source.CreationTime.ToString("G"),
            destinationInfo is null
                ? "—"
                : FormatBytes(
                    destinationInfo.Length),
            destinationInfo is null
                ? "—"
                : destinationInfo
                    .LastWriteTime
                    .ToString("G"));
    }

    private void RenderLyrics()
    {
        LyricDisplayLine[] translatedLines =
            BuildLyricLines(
                _currentLyricsRaw,
                showTranslation: true);

        bool hasLyrics =
            translatedLines.Length > 0;
        bool hasTranslation =
            translatedLines.Any(
                static line =>
                    !string.IsNullOrWhiteSpace(
                        line.Translation));

        TranslationButton.Visibility =
            hasLyrics && hasTranslation
                ? Visibility.Visible
                : Visibility.Collapsed;

        ToolTipService.SetToolTip(
            TranslationButton,
            _showTranslations
                ? L(
                    "Hide translation",
                    "仅显示原文")
                : L(
                    "Show translation",
                    "显示翻译"));

        LyricDisplayLine[] lines =
            hasLyrics
                ? BuildLyricLines(
                    _currentLyricsRaw,
                    _showTranslations)
                : [];

        LyricsList.ItemsSource =
            lines.Length == 0
                ? new[]
                {
                    new LyricDisplayLine(
                        L(
                            "No lyrics found.",
                            "没有找到歌词。"),
                        null,
                        false),
                }
                : lines;
    }

    private void TranslationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _showTranslations =
            !_showTranslations;
        RenderLyrics();
    }

    private void LyricLineMenu_Opening(
        object sender,
        object e)
    {
        if (sender is MenuFlyout menu
            && menu.Items.Count > 0
            && menu.Items[0]
                is MenuFlyoutItem item)
        {
            item.Text =
                L(
                    "Copy line",
                    "复制此行");
        }
    }

    private void LyricsList_RightTapped(
        object sender,
        RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource
                is FrameworkElement element
            && element.DataContext
                is LyricDisplayLine line)
        {
            LyricsList.SelectedItem =
                line;
        }
    }

    private void CopyLyricLineItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        LyricDisplayLine? line =
            (sender as MenuFlyoutItem)
                ?.Tag
                as LyricDisplayLine
            ?? LyricsList.SelectedItem
                as LyricDisplayLine;

        if (line is null)
        {
            return;
        }

        string text =
            line.ShowTranslation
            && !string.IsNullOrWhiteSpace(
                line.Translation)
                ? $"{line.Original}/{line.Translation}"
                : line.Original;

        CopyText(text);
    }

    private static LyricDisplayLine[]
        BuildLyricLines(
            string? raw,
            bool showTranslation)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        LrcDocument document =
            LrcDocument.Parse(raw);

        List<LyricDisplayLine> result = [];

        if (document.Lines.Count > 0)
        {
            foreach (IGrouping<TimeSpan, LrcLine>
                group in document.Lines
                    .GroupBy(
                        static line =>
                            line.Timestamp))
            {
                string[] texts =
                    group
                        .Select(
                            static line =>
                                line.Text.Trim())
                        .Where(
                            static text =>
                                text.Length > 0)
                        .ToArray();

                if (texts.Length == 0)
                {
                    continue;
                }

                (string original,
                    string? translation) =
                    SplitTranslation(
                        texts[0]);

                if (translation is null
                    && texts.Length > 1)
                {
                    translation =
                        string.Join(
                            Environment.NewLine,
                            texts.Skip(1));
                }

                result.Add(
                    new LyricDisplayLine(
                        original,
                        translation,
                        showTranslation));
            }

            return [.. result];
        }

        foreach (string rawLine
            in raw.Split(
                ['\r', '\n'],
                StringSplitOptions
                    .RemoveEmptyEntries))
        {
            string text =
                rawLine.Trim();

            if (text.Length == 0
                || (text.StartsWith('[')
                    && text.Contains(':')
                    && text.EndsWith(']')))
            {
                continue;
            }

            (string original,
                string? translation) =
                SplitTranslation(text);

            result.Add(
                new LyricDisplayLine(
                    original,
                    translation,
                    showTranslation));
        }

        return [.. result];
    }

    private static (string Original,
        string? Translation)
        SplitTranslation(string text)
    {
        if (text.Contains(
                "://",
                StringComparison.Ordinal))
        {
            return (text, null);
        }

        int slash =
            text.IndexOf('/');

        if (slash <= 0
            || slash >= text.Length - 1)
        {
            return (text, null);
        }

        string left =
            text[..slash].Trim();
        string right =
            text[(slash + 1)..].Trim();

        return left.Length > 0
            && right.Length > 0
                ? (left, right)
                : (text, null);
    }

    private async Task
        LoadArtworkThumbnailsAsync(
            MusicLoomSettings settings,
            int generation)
    {
        // Embedded artwork belongs to the track, not to the album folder.
        // Load these individually so one song's embedded cover never leaks to
        // neighboring tracks that happen to share Artist/Album.
        foreach (LibraryDisplayItem item
            in _allLibraryItems.Where(
                static item =>
                    item.Entry.Metadata
                        ?.HasEmbeddedArt
                    == true))
        {
            if (generation !=
                _artworkGeneration)
            {
                return;
            }

            byte[]? embedded =
                await Task.Run(
                    () =>
                        TryReadEmbeddedArtwork(
                            item.Entry));

            if (embedded is not
                { Length: > 0 }
                || generation !=
                    _artworkGeneration)
            {
                continue;
            }

            try
            {
                item.Artwork =
                    await CreateBitmapImageAsync(
                        embedded);
            }
            catch
            {
                // Corrupt embedded artwork is optional; continue with the
                // remaining library rather than blocking browsing.
            }
        }

        // Tracks without embedded art may safely share the same album-folder
        // cover. Group those for efficient I/O.
        IGrouping<string, LibraryDisplayItem>[]
            groups =
                _allLibraryItems
                    .Where(
                        static item =>
                            item.Entry.Metadata
                                ?.HasEmbeddedArt
                            != true)
                    .GroupBy(
                        ArtworkKey,
                        StringComparer
                            .OrdinalIgnoreCase)
                    .ToArray();

        foreach (IGrouping<string,
            LibraryDisplayItem> group
            in groups)
        {
            if (generation !=
                _artworkGeneration)
            {
                return;
            }

            LibraryDisplayItem[] groupItems =
                group.ToArray();

            byte[]? bytes =
                await Task.Run(
                    () =>
                    {
                        foreach (LibraryDisplayItem candidate
                            in groupItems)
                        {
                            byte[]? candidateBytes =
                                TryReadExternalArtwork(
                                    settings,
                                    candidate.Entry)
                                ?? TryReadEmbeddedArtwork(
                                    candidate.Entry);

                            if (candidateBytes is
                                { Length: > 0 })
                            {
                                return candidateBytes;
                            }
                        }

                        return null;
                    });

            if (bytes is null
                || bytes.Length == 0
                || generation !=
                    _artworkGeneration)
            {
                continue;
            }

            try
            {
                BitmapImage bitmap =
                    await CreateBitmapImageAsync(
                        bytes);

                foreach (LibraryDisplayItem item
                    in groupItems)
                {
                    item.Artwork = bitmap;
                }
            }
            catch
            {
                // Artwork is optional. Corrupt pictures must not block browsing.
            }
        }
    }

    private static string ArtworkKey(
        LibraryDisplayItem item)
    {
        if (item.Entry.DestinationPath
            is string destination)
        {
            return Path.GetDirectoryName(
                       destination)
                   ?? item.Entry.SourcePath;
        }

        return string.Join(
            "|",
            Path.GetDirectoryName(
                item.Entry.SourcePath),
            item.Artist,
            item.Album);
    }

    private static byte[]?
        TryReadArtworkBytes(
            MusicLoomSettings settings,
            PlanEntry entry)
    {
        try
        {
            // Embedded artwork is track-specific and therefore wins over a
            // generic Cover.* file in the album folder.
            if (entry.Metadata?.HasEmbeddedArt
                == true)
            {
                byte[]? embedded =
                    TryReadEmbeddedArtwork(
                        entry);

                if (embedded is
                    { Length: > 0 })
                {
                    return embedded;
                }
            }

            byte[]? external =
                TryReadExternalArtwork(
                    settings,
                    entry);

            if (external is
                { Length: > 0 })
            {
                return external;
            }

            // Metadata can be stale/missing for legacy cache entries. Try the
            // actual file as a final embedded-art fallback.
            return TryReadEmbeddedArtwork(
                entry);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]?
        TryReadEmbeddedArtwork(
            PlanEntry entry)
    {
        foreach (string path
            in CandidateAudioPaths(entry))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                byte[]? artwork =
                    AtlTrackInspector
                        .Read(path)
                        .Artwork;

                if (artwork is
                    { Length: > 0 })
                {
                    return artwork;
                }
            }
            catch
            {
                // Keep trying another candidate path.
            }
        }

        return null;
    }

    private static IEnumerable<string>
        CandidateAudioPaths(
            PlanEntry entry)
    {
        // Source first: destination may have been materialized before an
        // embedded-art tag was corrected.
        yield return entry.SourcePath;

        if (entry.DestinationPath
            is string destination
            && !string.Equals(
                destination,
                entry.SourcePath,
                StringComparison.OrdinalIgnoreCase))
        {
            yield return destination;
        }
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
                && Directory.Exists(
                    directory))
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
            : File.ReadAllBytes(
                cover);
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

    private static async Task<BitmapImage>
        CreateBitmapImageAsync(
            byte[] artwork,
            int decodePixelWidth = 192)
    {
        using InMemoryRandomAccessStream
            stream = new();
        using DataWriter writer =
            new(stream);

        writer.WriteBytes(artwork);
        await writer.StoreAsync();
        writer.DetachStream();
        stream.Seek(0);

        BitmapImage bitmap = new();
        if (decodePixelWidth > 0)
        {
            bitmap.DecodePixelWidth =
                decodePixelWidth;
        }

        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private void CoverButton_PointerEntered(
        object sender,
        PointerRoutedEventArgs e)
    {
        CoverScaleTransform.ScaleX = 1.035;
        CoverScaleTransform.ScaleY = 1.035;
    }

    private void CoverButton_PointerExited(
        object sender,
        PointerRoutedEventArgs e)
    {
        CoverScaleTransform.ScaleX = 1;
        CoverScaleTransform.ScaleY = 1;
    }

    private async void CoverButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_currentArtworkBytes
            is not { Length: > 0 } artwork)
        {
            return;
        }

        try
        {
            BitmapImage bitmap =
                await CreateBitmapImageAsync(
                    artwork,
                    decodePixelWidth: 0);

            const double ViewportSize = 620;

            Image image = new()
            {
                Source = bitmap,
                Width = ViewportSize,
                Height = ViewportSize,
                Stretch =
                    Microsoft.UI.Xaml.Media
                        .Stretch.Uniform,
                HorizontalAlignment =
                    HorizontalAlignment.Center,
                VerticalAlignment =
                    VerticalAlignment.Center,
            };

            ScrollViewer viewer = new()
            {
                Content = image,
                Width = ViewportSize,
                Height = ViewportSize,
                ZoomMode = ZoomMode.Enabled,
                MinZoomFactor = 0.25f,
                MaxZoomFactor = 6f,
                HorizontalScrollMode =
                    ScrollMode.Enabled,
                VerticalScrollMode =
                    ScrollMode.Enabled,
                HorizontalScrollBarVisibility =
                    ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility =
                    ScrollBarVisibility.Hidden,
            };

            bool dragging = false;
            Windows.Foundation.Point
                lastPointer = default;

            viewer.PointerWheelChanged +=
                (_, args) =>
                {
                    var point =
                        args.GetCurrentPoint(
                            viewer);
                    int delta =
                        point.Properties
                            .MouseWheelDelta;

                    if (delta == 0)
                    {
                        return;
                    }

                    float current =
                        viewer.ZoomFactor;
                    float target =
                        Math.Clamp(
                            current
                            * (delta > 0
                                ? 1.16f
                                : 1f / 1.16f),
                            viewer.MinZoomFactor,
                            viewer.MaxZoomFactor);

                    Windows.Foundation.Point
                        position =
                            point.Position;

                    double contentX =
                        (viewer.HorizontalOffset
                            + position.X)
                        / current;
                    double contentY =
                        (viewer.VerticalOffset
                            + position.Y)
                        / current;

                    double horizontal =
                        Math.Max(
                            0,
                            contentX * target
                            - position.X);
                    double vertical =
                        Math.Max(
                            0,
                            contentY * target
                            - position.Y);

                    viewer.ChangeView(
                        horizontal,
                        vertical,
                        target,
                        disableAnimation: true);

                    args.Handled = true;
                };

            viewer.PointerPressed +=
                (_, args) =>
                {
                    var point =
                        args.GetCurrentPoint(
                            viewer);

                    if (!point.Properties
                        .IsLeftButtonPressed)
                    {
                        return;
                    }

                    dragging = true;
                    lastPointer =
                        point.Position;
                    viewer.CapturePointer(
                        args.Pointer);
                    args.Handled = true;
                };

            viewer.PointerMoved +=
                (_, args) =>
                {
                    if (!dragging)
                    {
                        return;
                    }

                    Windows.Foundation.Point
                        position =
                            args.GetCurrentPoint(
                                viewer)
                                .Position;

                    double deltaX =
                        position.X
                        - lastPointer.X;
                    double deltaY =
                        position.Y
                        - lastPointer.Y;

                    viewer.ChangeView(
                        Math.Max(
                            0,
                            viewer.HorizontalOffset
                            - deltaX),
                        Math.Max(
                            0,
                            viewer.VerticalOffset
                            - deltaY),
                        null,
                        disableAnimation: true);

                    lastPointer = position;
                    args.Handled = true;
                };

            void EndDrag(
                PointerRoutedEventArgs args)
            {
                if (!dragging)
                {
                    return;
                }

                dragging = false;
                viewer.ReleasePointerCapture(
                    args.Pointer);
                args.Handled = true;
            }

            viewer.PointerReleased +=
                (_, args) =>
                    EndDrag(args);
            viewer.PointerCanceled +=
                (_, args) =>
                    EndDrag(args);

            ContentDialog dialog = new()
            {
                XamlRoot =
                    ((FrameworkElement)Content)
                        .XamlRoot,
                Title =
                    string.IsNullOrWhiteSpace(
                        TrackAlbumText.Text)
                        ? TrackTitleText.Text
                        : TrackAlbumText.Text,
            };

            Button closeButton = new()
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                HorizontalAlignment =
                    HorizontalAlignment.Right,
                Background = null,
                BorderThickness =
                    new Thickness(0),
                Content = new FontIcon
                {
                    Glyph = "\uE711",
                    FontSize = 12,
                },
            };
            ToolTipService.SetToolTip(
                closeButton,
                L("Close", "关闭"));
            closeButton.Click +=
                (_, _) =>
                    dialog.Hide();

            Grid content = new()
            {
                Width = ViewportSize,
                RowSpacing = 6,
            };
            content.RowDefinitions.Add(
                new RowDefinition
                {
                    Height =
                        GridLength.Auto,
                });
            content.RowDefinitions.Add(
                new RowDefinition
                {
                    Height =
                        new GridLength(
                            ViewportSize),
                });

            Grid.SetRow(
                closeButton,
                0);
            content.Children.Add(
                closeButton);

            Grid.SetRow(
                viewer,
                1);
            content.Children.Add(
                viewer);

            dialog.Content = content;

            await dialog.ShowAsync();
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not show artwork preview.",
                exception);
        }
    }

    private async void OpenMatchDialogButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedLibraryItem
            is LibraryDisplayItem item)
        {
            await ShowMatchDialogAsync(
                item);
        }
    }

    private async Task ShowMatchDialogAsync(
        LibraryDisplayItem item)
    {
        TextBox artistBox = new()
        {
            Header =
                L("Artist", "艺术家"),
            Text =
                item.Artist
                    .Contains(
                        L(
                            "Unknown artist",
                            "未知艺术家"),
                        StringComparison
                            .OrdinalIgnoreCase)
                        ? string.Empty
                        : item.Artist,
        };

        TextBox titleBox = new()
        {
            Header =
                L("Title", "歌曲名"),
            Text = item.Title,
        };

        TextBox albumBox = new()
        {
            Header =
                L(
                    "Album (optional)",
                    "专辑（可选）"),
            Text =
                item.Album
                    .Contains(
                        L(
                            "Unknown album",
                            "未知专辑"),
                        StringComparison
                            .OrdinalIgnoreCase)
                        ? string.Empty
                        : item.Album,
        };

        Button fingerprintButton = new()
        {
            Content =
                L(
                    "Recognize from audio",
                    "通过音频指纹识别"),
        };

        Button searchButton = new()
        {
            Content =
                L(
                    "Search by metadata",
                    "按元数据搜索"),
            Style =
                (Style)Application.Current
                    .Resources[
                        "AccentButtonStyle"],
        };

        TextBlock status = new()
        {
            Foreground =
                (Microsoft.UI.Xaml.Media.Brush)
                    Application.Current
                        .Resources[
                            "TextFillColorSecondaryBrush"],
            TextWrapping =
                TextWrapping.Wrap,
        };

        ListView results = new()
        {
            MinHeight = 170,
            MaxHeight = 250,
        };

        TextBlock lyricsPreview = new()
        {
            Text =
                L(
                    "Select a result to preview lyrics.",
                    "选择结果后可预览歌词。"),
            TextWrapping =
                TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

        ScrollViewer lyricsScroll = new()
        {
            MaxHeight = 190,
            Content = lyricsPreview,
        };

        Grid buttons = new()
        {
            ColumnSpacing = 8,
        };
        buttons.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width =
                    new GridLength(
                        1,
                        GridUnitType.Star),
            });
        buttons.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width =
                    GridLength.Auto,
            });
        buttons.Children.Add(
            fingerprintButton);
        Grid.SetColumn(
            searchButton,
            1);
        buttons.Children.Add(
            searchButton);

        StackPanel panel = new()
        {
            Spacing = 10,
            MaxWidth = 680,
        };
        panel.Children.Add(
            artistBox);
        panel.Children.Add(
            titleBox);
        panel.Children.Add(
            albumBox);
        panel.Children.Add(
            buttons);
        panel.Children.Add(
            status);
        panel.Children.Add(
            results);
        panel.Children.Add(
            lyricsScroll);

        ContentDialog dialog = new()
        {
            XamlRoot =
                ((FrameworkElement)Content)
                    .XamlRoot,
            Title =
                L(
                    "Find NetEase match",
                    "查找网易云匹配"),
            PrimaryButtonText =
                L(
                    "Use selected",
                    "使用所选"),
            CloseButtonText =
                L("Cancel", "取消"),
            DefaultButton =
                ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            Content = panel,
        };

        async Task SetResultsAsync(
            Task<IReadOnlyList<
                NeteaseMatchCandidate>>
                request)
        {
            fingerprintButton.IsEnabled =
                false;
            searchButton.IsEnabled =
                false;
            status.Text =
                L(
                    "Searching…",
                    "正在搜索……");

            try
            {
                IReadOnlyList<
                    NeteaseMatchCandidate>
                    candidates =
                        await request;

                results.ItemsSource =
                    candidates;

                status.Text =
                    candidates.Count == 0
                        ? L(
                            "No matches found.",
                            "没有找到匹配。")
                        : L(
                            $"{candidates.Count:N0} matches found.",
                            $"找到 {candidates.Count:N0} 个匹配。");

                if (candidates.Count > 0)
                {
                    results.SelectedIndex = 0;
                }
            }
            catch (Exception exception)
            {
                status.Text =
                    UserFacingErrors.Describe(
                        exception);
            }
            finally
            {
                fingerprintButton.IsEnabled =
                    true;
                searchButton.IsEnabled =
                    true;
            }
        }

        fingerprintButton.Click +=
            async (_, _) =>
            {
                using NeteaseClient client =
                    new();
                CompositeFingerprintPcmDecoder
                    decoder = new();

                await SetResultsAsync(
                    client.MatchFileCandidatesAsync(
                        decoder,
                        item.Entry.SourcePath));
            };

        searchButton.Click +=
            async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(
                        titleBox.Text))
                {
                    status.Text =
                        L(
                            "Enter a song title.",
                            "请输入歌曲名。");
                    return;
                }

                using NeteaseClient client =
                    new();

                await SetResultsAsync(
                    client.SearchSongsAsync(
                        titleBox.Text,
                        artistBox.Text,
                        albumBox.Text));
            };

        results.SelectionChanged +=
            async (_, _) =>
            {
                if (results.SelectedItem
                    is not
                        NeteaseMatchCandidate
                            candidate)
                {
                    dialog.IsPrimaryButtonEnabled =
                        false;
                    return;
                }

                dialog.IsPrimaryButtonEnabled =
                    true;
                lyricsPreview.Text =
                    L(
                        "Loading lyrics…",
                        "正在加载歌词……");

                try
                {
                    using NeteaseClient client =
                        new();

                    LrcDocument? lyrics =
                        await client.GetLyricsAsync(
                            candidate.TrackId);

                    LyricDisplayLine[] lines =
                        BuildLyricLines(
                            lyrics?.ToString(),
                            showTranslation: true);

                    lyricsPreview.Text =
                        lines.Length == 0
                            ? L(
                                "No lyrics for this result.",
                                "这个结果没有歌词。")
                            : string.Join(
                                Environment.NewLine,
                                lines.Select(
                                    static line =>
                                        string.IsNullOrWhiteSpace(
                                            line.Translation)
                                            ? line.Original
                                            : $"{line.Original}\n{line.Translation}"));
                }
                catch (Exception exception)
                {
                    lyricsPreview.Text =
                        UserFacingErrors.Describe(
                            exception);
                }
            };

        ContentDialogResult result =
            await dialog.ShowAsync();

        if (result
                == ContentDialogResult.Primary
            && results.SelectedItem
                is NeteaseMatchCandidate
                    selected)
        {
            await SaveManualMatchAsync(
                item,
                selected);
        }
    }

    private async Task SaveManualMatchAsync(
        LibraryDisplayItem item,
        NeteaseMatchCandidate candidate)
    {
        if (_state is null
            || _stateStore is null)
        {
            return;
        }

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
                false,
                item.Title);

        TrackState updated =
            previous
            ?? new TrackState
            {
                SourcePath =
                    item.Entry.SourcePath,
                SourceSize =
                    source.Length,
                SourceLastWriteUtcTicks =
                    source.LastWriteTimeUtc
                        .Ticks,
                QuickHash = quickHash,
                DestinationPath =
                    item.Entry.DestinationPath,
                MetadataCaptured = true,
                TitleCaptured = true,
                Title = metadata.Title,
                Artist = metadata.Artist,
                AlbumArtist =
                    metadata.AlbumArtist,
                Album = metadata.Album,
                HasEmbeddedArt =
                    metadata.HasEmbeddedArt,
                ProcessedAt =
                    DateTimeOffset.UtcNow,
            };

        updated = updated with
        {
            SourceSize =
                source.Length,
            SourceLastWriteUtcTicks =
                source.LastWriteTimeUtc
                    .Ticks,
            QuickHash = quickHash,
            Recognition =
                RecognitionState.Matched,
            RecognitionCheckedAt =
                DateTimeOffset.UtcNow,
            NeteaseSongId =
                candidate.TrackId,
            NeteaseTrackName =
                candidate.Name,
            NeteaseTrackArtist =
                candidate.Artist,
            NeteaseTrackAlbum =
                candidate.Album,
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
            GetCachedMatchText(
                item.Entry.SourcePath);

        SyncButton.IsEnabled = true;

        ShowInfo(
            InfoBarSeverity.Success,
            L(
                "Match saved",
                "匹配已保存"),
            L(
                "The next sync will use the selected NetEase track.",
                "下次同步会使用所选的网易云歌曲。"));
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
                "No NetEase match cached.",
                "尚未缓存网易云匹配。");
        }

        string artist =
            state.NeteaseTrackArtist
            ?? _selectedLibraryItem?.Artist
            ?? string.Empty;
        string title =
            state.NeteaseTrackName
            ?? _selectedLibraryItem?.Title
            ?? $"#{songId}";

        return IsChinese
            ? $"{artist} - {title}（缓存 ID: {songId}）"
            : $"{artist} - {title} (cached ID: {songId})";
    }

    private void LibraryList_RightTapped(
        object sender,
        RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource
                is FrameworkElement element
            && element.DataContext
                is LibraryDisplayItem item)
        {
            LibraryList.SelectedItem = item;
            _contextLibraryItem = item;
        }
        else
        {
            _contextLibraryItem =
                LibraryList.SelectedItem
                    as LibraryDisplayItem;
        }
    }

    private void TrackContextMenu_Opening(
        object sender,
        object e)
    {
        LibraryDisplayItem? item =
            _contextLibraryItem
            ?? LibraryList.SelectedItem
                as LibraryDisplayItem;

        bool hasItem =
            item is not null;
        bool hasDestination =
            item?.Entry.DestinationPath
                is string destination
            && File.Exists(destination);

        ContextOpenSourceItem.IsEnabled =
            hasItem;
        ContextCopySourceItem.IsEnabled =
            hasItem;
        ContextFindMatchItem.IsEnabled =
            hasItem;
        ContextOpenDestinationItem.IsEnabled =
            hasDestination;
        ContextCopyDestinationItem.IsEnabled =
            item?.Entry.DestinationPath
                is not null;
    }

    private void ContextOpenSourceItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem()
            is LibraryDisplayItem item)
        {
            OpenInExplorer(
                item.Entry.SourcePath);
        }
    }

    private void ContextOpenDestinationItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem()
                ?.Entry.DestinationPath
            is string path)
        {
            OpenInExplorer(path);
        }
    }

    private void ContextCopySourceItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem()
            is LibraryDisplayItem item)
        {
            CopyText(
                item.Entry.SourcePath);
        }
    }

    private void ContextCopyDestinationItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem()
                ?.Entry.DestinationPath
            is string path)
        {
            CopyText(path);
        }
    }

    private async void ContextFindMatchItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem()
            is LibraryDisplayItem item)
        {
            LibraryList.SelectedItem =
                item;
            await ShowMatchDialogAsync(
                item);
        }
    }

    private LibraryDisplayItem?
        GetContextItem() =>
        _contextLibraryItem
        ?? LibraryList.SelectedItem
            as LibraryDisplayItem;

    private static void CopyText(
        string text)
    {
        DataPackage package = new();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    private static void OpenInExplorer(
        string path)
    {
        if (File.Exists(path))
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName =
                        "explorer.exe",
                    Arguments =
                        $"/select,\"{path}\"",
                    UseShellExecute =
                        true,
                });
            return;
        }

        string directory =
            Directory.Exists(path)
                ? path
                : Path.GetDirectoryName(path)
                    ?? path;

        Process.Start(
            new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
    }

    private PlaylistDisplayItem[]
        BuildPlaylists(
            MusicLoomSettings settings)
    {
        string root =
            settings.Path
                .SourcePlaylistDirectory;

        if (string.IsNullOrWhiteSpace(root)
            || !Directory.Exists(root))
        {
            return [];
        }

        Dictionary<string, LibraryDisplayItem>
            trackLookup =
                _allLibraryItems.ToDictionary(
                    static item =>
                        Path.GetFullPath(
                            item.Entry.SourcePath),
                    StringComparer
                        .OrdinalIgnoreCase);

        List<PlaylistDisplayItem> result = [];

        foreach (string playlistPath
            in Directory.EnumerateFiles(
                root,
                "*.m3u",
                SearchOption.TopDirectoryOnly)
                .Order(
                    StringComparer
                        .CurrentCultureIgnoreCase))
        {
            IReadOnlyList<string> paths =
                PlaylistMapper.ParseM3u(
                    File.ReadAllLines(
                        playlistPath));

            List<LibraryDisplayItem>
                tracks = [];

            string playlistDirectory =
                Path.GetDirectoryName(
                    playlistPath)
                ?? root;

            foreach (string rawPath in paths)
            {
                string normalized;
                try
                {
                    normalized =
                        Path.GetFullPath(
                            Path.IsPathRooted(
                                rawPath)
                                ? rawPath
                                : Path.Combine(
                                    playlistDirectory,
                                    rawPath));
                }
                catch
                {
                    normalized = rawPath;
                }

                if (trackLookup.TryGetValue(
                        normalized,
                        out LibraryDisplayItem? item))
                {
                    tracks.Add(item);
                }
            }

            string destinationPath =
                Path.Combine(
                    settings.Path
                        .DestinationDirectory,
                    Path.GetFileNameWithoutExtension(
                        playlistPath)
                    + ".txt");

            string playlistBase =
                settings.Path
                    .DestinationPlaylistBase
                    .Replace('\\', '/')
                    .TrimEnd('/');

            string[] expectedLines =
                tracks
                    .Where(static item =>
                        item.Entry
                            .DestinationPath
                        is not null)
                    .Select(item =>
                    {
                        string relative =
                            Path.GetRelativePath(
                                settings.Path
                                    .DestinationDirectory,
                                item.Entry
                                    .DestinationPath!)
                                .Replace('\\', '/');

                        return $"{playlistBase}/{relative}";
                    })
                    .ToArray();

            string expected =
                string.Join(
                    "\n",
                    expectedLines);

            bool isReady =
                File.Exists(destinationPath)
                && string.Equals(
                    File.ReadAllText(
                        destinationPath),
                    expected,
                    StringComparison.Ordinal);

            result.Add(
                new PlaylistDisplayItem(
                    Path.GetFileNameWithoutExtension(
                        playlistPath),
                    playlistPath,
                    destinationPath,
                    tracks,
                    isReady));
        }

        return [.. result];
    }

    private void PopulatePlaylists(
        IEnumerable<PlaylistDisplayItem>
            playlists)
    {
        _playlists.Clear();
        _playlists.AddRange(playlists);
        PlaylistsList.ItemsSource =
            _playlists.ToArray();

        if (_playlists.Count > 0)
        {
            PlaylistsList.SelectedIndex = 0;
        }
        else
        {
            PlaylistTitleText.Text =
                L(
                    "No playlists",
                    "没有播放列表");
            PlaylistMetaText.Text =
                L(
                    "Configure a MusicBee playlist folder in Settings.",
                    "请在“设置”中配置 MusicBee 播放列表目录。");
            PlaylistTracksList.ItemsSource =
                null;
        }
    }

    private void PlaylistsList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (PlaylistsList.SelectedItem
            is not PlaylistDisplayItem playlist)
        {
            PlaylistTracksList.ItemsSource =
                null;
            return;
        }

        PlaylistTitleText.Text =
            playlist.Name;
        PlaylistMetaText.Text =
            playlist.IsReady
                ? L(
                    $"{playlist.TrackCount:N0} tracks · Ready",
                    $"{playlist.TrackCount:N0} 首歌曲 · 已就绪")
                : L(
                    $"{playlist.TrackCount:N0} tracks · Pending sync",
                    $"{playlist.TrackCount:N0} 首歌曲 · 待同步");

        ApplyPlaylistTrackFilter();
    }

    private void PlaylistSearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e) =>
        ApplyPlaylistTrackFilter();

    private void PlaylistSortBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        ApplyPlaylistTrackFilter();

    private void ApplyPlaylistTrackFilter()
    {
        if (PlaylistsList.SelectedItem
            is not PlaylistDisplayItem playlist)
        {
            PlaylistTracksList.ItemsSource =
                null;
            return;
        }

        string query =
            PlaylistSearchBox.Text.Trim();

        string sort =
            (PlaylistSortBox.SelectedItem
                as ComboBoxItem)
                ?.Tag?.ToString()
            ?? "title";

        IEnumerable<LibraryDisplayItem>
            items = playlist.Tracks;

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

        items = sort switch
        {
            "album" =>
                items
                    .OrderBy(
                        static item =>
                            item.Album,
                        StringComparer
                            .CurrentCultureIgnoreCase)
                    .ThenBy(
                        static item =>
                            item.Title,
                        StringComparer
                            .CurrentCultureIgnoreCase),
            "artist" =>
                items
                    .OrderBy(
                        static item =>
                            item.Artist,
                        StringComparer
                            .CurrentCultureIgnoreCase)
                    .ThenBy(
                        static item =>
                            item.Title,
                        StringComparer
                            .CurrentCultureIgnoreCase),
            _ =>
                items.OrderBy(
                    static item =>
                        item.Title,
                    StringComparer
                        .CurrentCultureIgnoreCase),
        };

        PlaylistTracksList.ItemsSource =
            items.ToArray();
    }

    private void PlaylistTracksList_RightTapped(
        object sender,
        RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource
                is FrameworkElement element
            && element.DataContext
                is LibraryDisplayItem item)
        {
            PlaylistTracksList.SelectedItem =
                item;
            _playlistContextItem = item;
        }
        else
        {
            _playlistContextItem =
                PlaylistTracksList.SelectedItem
                    as LibraryDisplayItem;
        }
    }

    private void PlaylistShowInLibraryItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        LibraryDisplayItem? item =
            _playlistContextItem
            ?? PlaylistTracksList.SelectedItem
                as LibraryDisplayItem;

        if (item is null)
        {
            return;
        }

        LibrarySearchBox.Text =
            string.Empty;
        LibraryFilterBox.SelectedIndex = 0;
        ApplyLibraryFilter();

        NavigateTo("library");

        LibraryList.SelectedItem =
            item;
        LibraryList.ScrollIntoView(item);
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

    private async void BrowseDestinationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        string? path =
            await PickFolderAsync();
        if (path is not null)
        {
            DestinationPathBox.Text =
                path;
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

    private void OpenSourceButton_Click(
        object sender,
        RoutedEventArgs e) =>
        TryOpenConfiguredDirectory(
            SourcePathBox.Text);

    private void OpenDestinationSettingButton_Click(
        object sender,
        RoutedEventArgs e) =>
        TryOpenConfiguredDirectory(
            DestinationPathBox.Text);

    private void OpenPlaylistButton_Click(
        object sender,
        RoutedEventArgs e) =>
        TryOpenConfiguredDirectory(
            PlaylistSourcePathBox.Text);

    private static void
        TryOpenConfiguredDirectory(
            string path)
    {
        if (string.IsNullOrWhiteSpace(
                path))
        {
            return;
        }

        string full =
            Path.GetFullPath(path);

        if (!Directory.Exists(full))
        {
            return;
        }

        Process.Start(
            new ProcessStartInfo
            {
                FileName = full,
                UseShellExecute = true,
            });
    }

    private void SettingsControl_Changed(
        object sender,
        TextChangedEventArgs e) =>
        ScheduleSettingsSave();

    private void SettingsControl_Changed(
        object sender,
        RoutedEventArgs e) =>
        ScheduleSettingsSave();

    private void SettingsControl_Changed(
        object sender,
        SelectionChangedEventArgs e) =>
        ScheduleSettingsSave();

    private void LyricsRefreshHoursBox_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        ScheduleSettingsSave();

    private void ScheduleSettingsSave()
    {
        if (_loadingSettings)
        {
            return;
        }

        _settingsSaveCancellation
            ?.Cancel();
        _settingsSaveCancellation
            ?.Dispose();

        CancellationTokenSource cts =
            new();
        _settingsSaveCancellation =
            cts;

        _ = SaveSettingsAfterDelayAsync(
            cts.Token);
    }

    private async Task
        SaveSettingsAfterDelayAsync(
            CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                450,
                cancellationToken);

            MusicLoomSettings settings =
                BuildSettingsFromForm();

            bool planChanged =
                _settings is null
                || SettingsAffectPlan(
                    _settings,
                    settings);

            await SettingsLoader.SaveAsync(
                ConfigPath,
                settings,
                cancellationToken);

            _settings = settings;
            _uiLanguage =
                NormalizeLanguage(
                    settings.Ui.Language);
            _accentColor =
                settings.Ui.AccentColor;

            ApplyAppearance(settings);
            ApplyLanguage();

            if (_selectedLibraryItem is not null)
            {
                ConfigureTitleAutoScroll();
            }

            if (planChanged
                && _plan is not null)
            {
                SyncButton.IsEnabled = false;
                OperationStatusText.Text =
                    L(
                        "Settings changed · refresh the library.",
                        "设置已更改 · 请刷新音乐库。");
            }

            UpdateLibraryEmptyState();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not auto-save settings.",
                exception);

            ShowInfo(
                InfoBarSeverity.Error,
                L(
                    "Could not save settings",
                    "无法保存设置"),
                UserFacingErrors.Describe(
                    exception));
        }
    }

    private static bool SettingsAffectPlan(
        MusicLoomSettings left,
        MusicLoomSettings right)
    {
        return left.Path
                != right.Path
            || left.Organizer
                != right.Organizer;
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

        string theme =
            (ThemeBox.SelectedItem
                as ComboBoxItem)
                ?.Tag?.ToString()
            ?? UiThemeModes.System;

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
                AcrylicEnabled =
                    AcrylicToggle.IsOn,
                AccentColor =
                    string.IsNullOrWhiteSpace(
                        _accentColor)
                        ? "system"
                        : _accentColor,
                Theme =
                    UiThemeModes.Normalize(
                        theme),
                AutoScrollLongTitles =
                    AutoScrollTitleToggle.IsOn,
            },
        };
    }

    private void PopulateSettingsForm(
        MusicLoomSettings settings)
    {
        _loadingSettings = true;
        try
        {
            SourcePathBox.Text =
                settings.Path
                    .SourceDirectory;
            DestinationPathBox.Text =
                settings.Path
                    .DestinationDirectory;
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
                        == LyricsStorageModes
                            .Sidecar
                        ? 1
                        : 0;

            string language =
                NormalizeLanguage(
                    settings.Ui.Language);
            LanguageBox.SelectedIndex =
                language == "zh-CN"
                    ? 1
                    : 0;

            AcrylicToggle.IsOn =
                settings.Ui
                    .AcrylicEnabled;

            ThemeBox.SelectedIndex =
                UiThemeModes.Normalize(
                    settings.Ui.Theme) switch
                {
                    UiThemeModes.Light => 1,
                    UiThemeModes.Dark => 2,
                    _ => 0,
                };

            AutoScrollTitleToggle.IsOn =
                settings.Ui.AutoScrollLongTitles;

            _accentColor =
                string.IsNullOrWhiteSpace(
                    settings.Ui.AccentColor)
                    ? "system"
                    : settings.Ui.AccentColor;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private async void ChooseAccentButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Color initial =
            AppearanceManager.TryParseColor(
                _accentColor,
                out Color custom)
                ? custom
                : Color.FromArgb(
                    255,
                    0,
                    120,
                    212);

        ColorPicker picker = new()
        {
            Color = initial,
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible =
                true,
            IsHexInputVisible = true,
        };

        ContentDialog dialog = new()
        {
            XamlRoot =
                ((FrameworkElement)Content)
                    .XamlRoot,
            Title =
                L(
                    "Accent color",
                    "主题色"),
            PrimaryButtonText =
                L("Use color", "使用此颜色"),
            CloseButtonText =
                L("Cancel", "取消"),
            Content = picker,
        };

        if (await dialog.ShowAsync()
            == ContentDialogResult.Primary)
        {
            _accentColor =
                AppearanceManager.ToHex(
                    picker.Color);

            if (_settings is not null)
            {
                ApplyAppearance(
                    BuildSettingsFromForm());
            }

            ScheduleSettingsSave();
        }
    }

    private void ResetAccentButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _accentColor = "system";

        if (_settings is not null)
        {
            ApplyAppearance(
                BuildSettingsFromForm());
        }

        ScheduleSettingsSave();
    }

    private void OpenSettingsButton_Click(
        object sender,
        RoutedEventArgs e) =>
        NavigateTo("settings");

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
                    "Could not open the log folder.",
                    "无法打开日志文件夹。"),
                exception);
        }
    }

    private void ClearActivityButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _activityItems.Clear();
        RefreshActivityList();
    }

    private void Navigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs
            args)
    {
        if (args.SelectedItemContainer
                ?.Tag
            is string tag)
        {
            ShowPage(tag);
        }
    }

    private void NavigateTo(
        string tag)
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

    private void ShowPage(
        string tag)
    {
        LibraryPage.Visibility =
            tag == "library"
                ? Visibility.Visible
                : Visibility.Collapsed;
        PlaylistsPage.Visibility =
            tag == "playlists"
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

        ScanButton.IsEnabled =
            !busy;
        SyncButton.IsEnabled =
            !busy
            && _plan is
                {
                    PendingCount: > 0,
                    ErrorCount: 0,
                };
        CancelButton.Visibility =
            busy
                ? Visibility.Visible
                : Visibility.Collapsed;
        CancelButton.IsEnabled =
            busy;

        LibrarySearchBox.IsEnabled =
            !busy;
        LibraryFilterBox.IsEnabled =
            !busy;
        LibrarySortBox.IsEnabled =
            !busy;
        LibraryList.IsEnabled =
            !busy;
        OpenMatchDialogButton.IsEnabled =
            !busy
            && _selectedLibraryItem
                is not null;

        LibraryProgressPanel.Visibility =
            busy
                ? Visibility.Visible
                : Visibility.Collapsed;

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
        ActivityList.ItemsSource =
            _activityItems.Count == 0
                ? new[]
                {
                    L(
                        "No activity yet.",
                        "暂无活动记录。"),
                }
                : _activityItems.ToArray();
    }

    private void HandleUiException(
        string context,
        Exception exception)
    {
        string message =
            UserFacingErrors.Describe(
                exception);

        AddActivity(
            L("Error", "错误"),
            message);

        AppDiagnostics.LogException(
            context,
            exception);

        ShowInfo(
            InfoBarSeverity.Error,
            context,
            message);
    }

    private string StatusLabel(
        string kind) =>
        kind switch
        {
            "synced" =>
                L(
                    "Synced",
                    "已同步"),
            "error" =>
                L(
                    "Error",
                    "错误"),
            _ =>
                L(
                    "Pending",
                    "待同步"),
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
            "cleanup-old-destination" =>
                L(
                    "Cleaning old destination",
                    "清理旧目标路径"),
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
        PlaylistsNav.Content =
            L("Playlists", "音乐列表");
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

        ScanButtonText.Text =
            L(
                "Refresh library",
                "刷新音乐库");
        SyncButtonText.Text =
            L(
                "Sync pending",
                "同步待处理");
        CancelButtonText.Text =
            L("Cancel", "取消");

        if (OperationStatusText.Text
            is "Ready."
            or "就绪。")
        {
            OperationStatusText.Text =
                L("Ready.", "就绪。");
        }

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
                "待同步");
        LibraryFilterSyncedItem.Content =
            L(
                "Synced",
                "已同步");
        LibraryFilterErrorsItem.Content =
            L(
                "Errors",
                "错误");
        SortTitleItem.Content =
            L(
                "Sort by title",
                "按歌曲名排序");
        SortAlbumItem.Content =
            L(
                "Sort by album",
                "按专辑排序");
        SortArtistItem.Content =
            L(
                "Sort by artist",
                "按艺术家排序");

        ToolTipService.SetToolTip(
            CloseDetailButton,
            L(
                "Close details",
                "关闭详情"));
        ToolTipService.SetToolTip(
            CoverButton,
            L(
                "Double-click to view artwork",
                "双击查看大图"));
        ToolTipService.SetToolTip(
            PendingSummaryIcon,
            L(
                "Pending",
                "待同步"));
        ToolTipService.SetToolTip(
            ErrorSummaryIcon,
            L(
                "Errors",
                "错误"));
        LyricsTab.Header =
            L("Lyrics", "歌词");
        MatchTab.Header =
            L(
                "NetEase match",
                "网易云匹配");
        PropertiesTab.Header =
            L(
                "Properties",
                "属性");
        MatchHelpText.Text =
            L(
                "Use fingerprint recognition or search by artist and title.",
                "可以使用音频指纹识别，或按艺术家和歌曲名搜索。");
        OpenMatchDialogButtonText.Text =
            L(
                "Find or change match…",
                "查找或更改匹配……");

        SourcePathHeadingText.Text =
            L("Source", "源文件");
        DestinationPathHeadingText.Text =
            L(
                "Destination",
                "目标文件");
        FormatHeadingText.Text =
            L("Format", "格式");
        SizeHeadingText.Text =
            L(
                "Source size",
                "源文件大小");
        ModifiedHeadingText.Text =
            L(
                "Modified",
                "修改时间");
        CreatedHeadingText.Text =
            L(
                "Created",
                "创建时间");
        DestinationSizeHeadingText.Text =
            L(
                "Destination size",
                "目标文件大小");
        DestinationModifiedHeadingText.Text =
            L(
                "Destination modified",
                "目标修改时间");

        ContextOpenSourceItem.Text =
            L(
                "Open source folder",
                "打开源文件位置");
        ContextOpenDestinationItem.Text =
            L(
                "Open destination folder",
                "打开目标文件位置");
        ContextCopySourceItem.Text =
            L(
                "Copy source path",
                "复制源文件路径");
        ContextCopyDestinationItem.Text =
            L(
                "Copy destination path",
                "复制目标文件路径");
        ContextFindMatchItem.Text =
            L(
                "Find NetEase match…",
                "查找网易云匹配……");

        PlaylistsTitleText.Text =
            L(
                "Playlists",
                "音乐列表");
        PlaylistsSubtitleText.Text =
            L(
                "Browse your MusicBee playlists.",
                "浏览你的 MusicBee 播放列表。");

        if (PlaylistsList.SelectedItem is null)
        {
            PlaylistTitleText.Text =
                L(
                    "Select a playlist",
                    "选择一个音乐列表");
        }

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
                "Changes are saved automatically.",
                "更改会自动保存。");
        LibrarySettingsHeadingText.Text =
            L(
                "Library",
                "音乐库");
        LyricsSettingsHeadingText.Text =
            L(
                "Lyrics",
                "歌词");
        AppearanceSettingsHeadingText.Text =
            L(
                "Appearance",
                "外观");

        SourceLibrarySettingTitle.Text =
            L(
                "Source library",
                "源音乐库");
        DestinationLibrarySettingTitle.Text =
            L(
                "Destination library",
                "目标音乐库");
        PlaylistFolderSettingTitle.Text =
            L(
                "MusicBee playlist folder",
                "MusicBee 播放列表目录");
        PlaylistBaseSettingTitle.Text =
            L(
                "Playlist path on phone",
                "手机上的播放列表路径");

        FetchLyricsSettingTitle.Text =
            L(
                "Fetch lyrics from NetEase",
                "从网易云获取歌词");
        FetchLyricsSettingDescription.Text =
            L(
                "Recognize tracks and cache synchronized lyrics.",
                "识别歌曲并缓存同步歌词。");
        OverwriteLyricsSettingTitle.Text =
            L(
                "Overwrite existing lyrics",
                "覆盖已有歌词");
        OverwriteLyricsSettingDescription.Text =
            L(
                "Replace lyrics already present in the destination.",
                "替换目标文件中已经存在的歌词。");
        LyricsStorageSettingTitle.Text =
            L(
                "Lyrics storage",
                "歌词保存方式");
        LyricsStorageSettingDescription.Text =
            L(
                "Choose embedded tags or sidecar .lrc files.",
                "选择嵌入标签或独立 .lrc 文件。");
        LyricsStorageEmbeddedItem.Content =
            L(
                "Embedded in audio file",
                "嵌入音频文件");
        LyricsStorageSidecarItem.Content =
            L(
                "Sidecar .lrc file",
                "独立 .lrc 文件");
        LyricsCacheSettingTitle.Text =
            L(
                "Lyrics cache lifetime",
                "歌词缓存有效期");
        LyricsCacheSettingDescription.Text =
            L(
                "How long a result stays fresh before MusicLoom checks again.",
                "在 MusicLoom 再次检查前，结果保持有效的时间。");

        ThemeSettingTitle.Text =
            L(
                "Theme",
                "主题");
        ThemeSystemItem.Content =
            L(
                "Use system setting",
                "跟随系统");
        ThemeLightItem.Content =
            L(
                "Light",
                "浅色");
        ThemeDarkItem.Content =
            L(
                "Dark",
                "深色");
        AutoScrollTitleSettingTitle.Text =
            L(
                "Auto-scroll long track titles",
                "自动滚动过长歌曲名");

        AcrylicSettingTitle.Text =
            L(
                "Acrylic backdrop",
                "亚克力背景");
        AcrylicSettingDescription.Text =
            L(
                "Use desktop acrylic instead of the default Mica backdrop.",
                "使用桌面亚克力效果代替默认的 Mica 背景。");
        AccentSettingTitle.Text =
            L(
                "Accent color",
                "主题色");
        AccentSettingDescription.Text =
            L(
                "Customize MusicLoom's primary accent or follow Windows.",
                "自定义 MusicLoom 的主题色，或跟随 Windows。");
        ChooseAccentButton.Content =
            L("Choose…", "选择……");
        ResetAccentButton.Content =
            L(
                "Use system",
                "跟随系统");
        LanguageSettingTitle.Text =
            L(
                "Interface language",
                "界面语言");

        OpenSettingsButton.Content =
            L(
                "Open settings",
                "打开设置");

        CoverPlaceholderText.Text =
            L(
                "No artwork",
                "无封面");

        PlaylistSearchBox.PlaceholderText =
            L(
                "Search this playlist",
                "搜索当前音乐列表");
        PlaylistSortTitleItem.Content =
            L(
                "Sort by title",
                "按歌曲名排序");
        PlaylistSortAlbumItem.Content =
            L(
                "Sort by album",
                "按专辑排序");
        PlaylistSortArtistItem.Content =
            L(
                "Sort by artist",
                "按艺术家排序");
        PlaylistShowInLibraryItem.Text =
            L(
                "Show in library",
                "在音乐库中显示");

        BrowseSourceButton.Content =
            L("Browse", "浏览");
        OpenSourceButton.Content =
            L("Open", "打开");
        BrowseDestinationButton.Content =
            L("Browse", "浏览");
        OpenDestinationSettingButton.Content =
            L("Open", "打开");
        BrowsePlaylistButton.Content =
            L("Browse", "浏览");
        OpenPlaylistButton.Content =
            L("Open", "打开");

        if (LastRunTitle.Text
            is "No sync run yet"
            or "尚无同步记录")
        {
            LastRunTitle.Text =
                L(
                    "No sync run yet",
                    "尚无同步记录");
            LastRunSummaryText.Text =
                L(
                    "Refresh the library to begin.",
                    "刷新音乐库即可开始。");
        }

        foreach (LibraryDisplayItem item
            in _allLibraryItems)
        {
            item.StatusText =
                StatusLabel(item.Kind);
        }

        if (_selectedLibraryItem
            is LibraryDisplayItem selected)
        {
            TrackStatusText.Text =
                selected.StatusText;
            CurrentMatchText.Text =
                GetCachedMatchText(
                    selected.Entry.SourcePath);
        }
        else
        {
            CurrentMatchText.Text =
                L(
                    "No NetEase match cached.",
                    "尚未缓存网易云匹配。");
        }

        UpdateLibraryEmptyState();
        RenderLyrics();
        RefreshActivityList();
        ApplyLibraryFilter();
        ApplyPlaylistTrackFilter();
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

    private static string? FormatArtistsForDisplay(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(
            "; ",
            value
                .Split(
                    ';',
                    StringSplitOptions
                        .RemoveEmptyEntries
                    | StringSplitOptions
                        .TrimEntries));
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

    private static string FormatBytes(
        long bytes)
    {
        string[] suffixes =
            ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int index = 0;

        while (value >= 1024
            && index
                < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {suffixes[index]}";
    }

    private sealed record TrackDetailPayload(
        byte[]? Artwork,
        string? Lyrics,
        string LyricsSource,
        string SourcePath,
        string? DestinationPath,
        string Format,
        string SourceSize,
        string SourceModified,
        string SourceCreated,
        string DestinationSize,
        string DestinationModified);
}
