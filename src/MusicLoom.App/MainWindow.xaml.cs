using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
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
    private static readonly string LegacyConfigPath =
        Path.Combine(
            AppDiagnostics.DataDirectory,
            "config.json");

    private static readonly string LegacyStatePath =
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
    private readonly Dictionary<string, TrackSyncViewState>
        _trackSyncStates =
            new(StringComparer.OrdinalIgnoreCase);

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
    private Storyboard? _titleScrollStoryboard;
    private Storyboard? _albumScrollStoryboard;
    private Storyboard? _lyricsSourceScrollStoryboard;
    private Storyboard? _currentMatchScrollStoryboard;
    private Storyboard? _playlistTitleScrollStoryboard;
    private Storyboard? _playlistMetaScrollStoryboard;
    private byte[]? _currentArtworkBytes;
    private string _lyricsDisplayMode =
        LyricsDisplayModes.PreferOriginal;

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
        LibrarySortBox.SelectedIndex = 2;
        LibrarySortDirectionBox.SelectedIndex = 0;
        PlaylistSortBox.SelectedIndex = 2;
        PlaylistSortDirectionBox.SelectedIndex = 0;
        LyricsModeBox.SelectedIndex = 2;
        LyricsStorageModeBox.SelectedIndex = 0;
        LanguageBox.SelectedIndex =
            IsChinese ? 1 : 0;
        Navigation.SelectedItem =
            Navigation.MenuItems[0];

        DiagnosticsLogPathText.Text =
            AppDiagnostics.LogPath;
        RefreshDiagnosticsConsole();
        AppDiagnostics.LineWritten +=
            AppDiagnostics_LineWritten;

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
            await LibraryDatabase.InitializeAsync(
                DatabasePath);

            MusicLoomSettings settings =
                await LoadPersistedSettingsAsync();

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
            RefreshDatabaseSize();

            await RestoreCachedLibraryAsync(
                settings);

            UpdateLibraryEmptyState();

            AddActivity(
                L("Ready", "就绪"),
                L(
                    "MusicLoom started.",
                    "MusicLoom 已启动."));
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

    private async Task<MusicLoomSettings>
        LoadPersistedSettingsAsync(
            CancellationToken cancellationToken = default)
    {
        MusicLoomSettings? settings =
            await LibraryDatabase
                .LoadSettingsAsync(
                    DatabasePath,
                    cancellationToken);

        if (settings is not null)
        {
            TryDeleteLegacyFile(
                LegacyConfigPath);
            return settings;
        }

        if (File.Exists(
                LegacyConfigPath))
        {
            settings =
                await SettingsLoader.LoadAsync(
                    LegacyConfigPath,
                    cancellationToken);

            await LibraryDatabase
                .SaveSettingsAsync(
                    DatabasePath,
                    settings,
                    cancellationToken);

            TryDeleteLegacyFile(
                LegacyConfigPath);

            AppDiagnostics.Log(
                "Migrated legacy JSON settings into the local database.");

            return settings;
        }

        return new MusicLoomSettings();
    }

    private static void TryDeleteLegacyFile(
        string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
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
                new SqliteStateStore(
                    DatabasePath,
                    LegacyStatePath);
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

            TryDeleteLegacyFile(
                LegacySnapshotPath);
            RefreshDatabaseSize();

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
        if (_busy)
        {
            return;
        }

        await RunBackgroundSyncAsync();
    }

    private async Task RunBackgroundSyncAsync()
    {
        using CancellationTokenSource
            cancellation = new();

        _operationCancellation =
            cancellation;

        SetBusy(
            true,
            L(
                "Checking sync plan…",
                "正在检查同步计划……"));

        await Task.Yield();

        try
        {
            MusicLoomSettings settings =
                _settings
                ?? await LoadPersistedSettingsAsync(
                    cancellation.Token);

            LibraryPathSafety.ValidateStatePath(
                settings,
                DatabasePath);

            StateStore stateStore =
                new SqliteStateStore(
                    DatabasePath,
                    LegacyStatePath);
            StateDocument state =
                await stateStore.LoadAsync(
                    cancellation.Token);

            Progress<ScanProgress>
                scanProgress = new(value =>
                {
                    if (value.Stage
                        == "ancillary")
                    {
                        SetOperationStatus(
                            L(
                                "Checking covers and playlists…",
                                "正在检查封面和播放列表……"));
                        SetIndeterminateProgress();
                        return;
                    }

                    if (value.Stage
                        == "complete")
                    {
                        SetOperationStatus(
                            L(
                                "Preparing sync…",
                                "正在准备同步……"));
                        SetIndeterminateProgress();
                        return;
                    }

                    SetOperationStatus(
                        L(
                            $"Checking tracks {value.Processed:N0}/{value.Total:N0}",
                            $"正在检查歌曲 {value.Processed:N0}/{value.Total:N0}"));
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
                            scanProgress,
                            cancellation.Token),
                    cancellation.Token);

            await stateStore.SaveAsync(
                state,
                cancellation.Token);

            _settings = settings;
            _stateStore = stateStore;
            _state = state;
            _plan = plan;

            if (plan.ErrorCount > 0)
            {
                string[] errors =
                    plan.Entries
                        .Where(static entry =>
                            entry.Status
                                == PlanEntryStatus.Error)
                        .Select(entry =>
                            $"{Path.GetFileName(entry.SourcePath)} · {entry.Detail}")
                        .ToArray();

                foreach (string error
                    in errors)
                {
                    AppDiagnostics.Log(
                        $"Scan error before sync: {error}");
                }

                ShowInfo(
                    InfoBarSeverity.Error,
                    L(
                        "Sync blocked",
                        "同步已阻止"),
                    errors.Length > 0
                        ? L(
                            $"{plan.ErrorCount:N0} scan errors. First: {errors[0]}",
                            $"{plan.ErrorCount:N0} 个扫描错误。第一个：{errors[0]}")
                        : L(
                            "The library has scan errors.",
                            "音乐库存在扫描错误。"));

                return;
            }

            if (plan.PendingCount == 0)
            {
                ShowInfo(
                    InfoBarSeverity.Success,
                    L(
                        "Already synced",
                        "已经同步"),
                    L(
                        "There are no pending changes.",
                        "没有待同步的更改。"));

                SetOperationStatus(
                    L(
                        "Library is up to date.",
                        "音乐库已经是最新。"));
                return;
            }

            AppDiagnostics.Log(
                $"Sync started: tracks={plan.PendingTrackCount}, " +
                $"audio={plan.AudioCopyCount}, covers={plan.CoverWriteCount}, " +
                $"playlists={plan.PlaylistWriteCount}.");

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
                    SetOperationStatus(
                        $"{FriendlyStage(value.Stage)} · " +
                        $"{value.Processed:N0}/{value.Total:N0} · " +
                        $"{Path.GetFileName(value.SourcePath)}");

                    SetProgress(
                        value.Processed,
                        value.Total);

                    UpdateLiveSyncRow(
                        value);
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

            string summaryText =
                L(
                    $"{summary.Local.CopiedTracks:N0} audio · " +
                    $"{summary.Local.CopiedCovers:N0} covers · " +
                    $"{summary.Local.WrittenPlaylists:N0} playlists · " +
                    $"{summary.WrittenLyrics:N0} lyrics · " +
                    $"{summary.Errors.Count:N0} errors",
                    $"音频 {summary.Local.CopiedTracks:N0} · " +
                    $"封面 {summary.Local.CopiedCovers:N0} · " +
                    $"播放列表 {summary.Local.WrittenPlaylists:N0} · " +
                    $"歌词 {summary.WrittenLyrics:N0} · " +
                    $"错误 {summary.Errors.Count:N0}");

            AppDiagnostics.Log(
                "Sync finished: " + summaryText);

            foreach (TrackSyncError error
                in summary.Errors)
            {
                LibraryDisplayItem? errorItem =
                    _allLibraryItems
                        .FirstOrDefault(
                            item =>
                                string.Equals(
                                    item.Entry.SourcePath,
                                    error.SourcePath,
                                    StringComparison.OrdinalIgnoreCase));

                errorItem?.SetStatus(
                    "error",
                    StatusLabel(
                        "error"));
            }

            RefreshLiveSyncCounters();

            foreach (TrackSyncError error
                in summary.Errors)
            {
                AppDiagnostics.Log(
                    $"Track error [{error.Stage}] {error.SourcePath}: {error.Message}");

                SetTrackSyncState(
                    error.SourcePath,
                    L("Error", "错误"),
                    $"[{error.Stage}] {error.Message}",
                    isError: true);
            }

            HashSet<string> failedSources =
                summary.Errors
                    .Select(
                        static error =>
                            error.SourcePath)
                    .ToHashSet(
                        StringComparer
                            .OrdinalIgnoreCase);

            foreach (PlanEntry entry
                in plan.Entries.Where(
                    entry =>
                        entry.Status
                            != PlanEntryStatus
                                .Unchanged
                        && entry.Status
                            != PlanEntryStatus.Error
                        && !failedSources.Contains(
                            entry.SourcePath)))
            {
                SetTrackSyncState(
                    entry.SourcePath,
                    L("Synced", "已同步"),
                    L(
                        "Sync completed successfully.",
                        "同步已成功完成。"));
            }

            if (summary.Errors.Count == 0)
            {
                ShowInfo(
                    InfoBarSeverity.Success,
                    L(
                        "Sync complete",
                        "同步完成"),
                    summaryText);
            }
            else
            {
                TrackSyncError first =
                    summary.Errors[0];

                ShowInfo(
                    InfoBarSeverity.Warning,
                    L(
                        "Sync completed with errors",
                        "同步完成，但有错误"),
                    L(
                        $"{summary.Errors.Count:N0} errors. First: {Path.GetFileName(first.SourcePath)} · {FriendlyStage(first.Stage)} · {first.Message}",
                        $"{summary.Errors.Count:N0} 个错误。第一个：{Path.GetFileName(first.SourcePath)} · {FriendlyStage(first.Stage)} · {first.Message}"));
            }

            await RefreshLibraryAfterSyncAsync(
                settings,
                summary.Errors,
                cancellation.Token);

            if (summary.Errors.Count > 0)
            {
                LibraryFilterBox.SelectedItem =
                    LibraryFilterErrorsItem;
                NavigateTo("library");
                ApplyLibraryFilter();
            }
        }
        catch (OperationCanceledException)
        {
            AppDiagnostics.Log(
                "Sync cancelled by user.");

            ShowInfo(
                InfoBarSeverity.Warning,
                L(
                    "Sync cancelled",
                    "同步已取消"),
                L(
                    "Completed work was checkpointed.",
                    "已完成的工作已经保存。"));

            SetOperationStatus(
                L(
                    "Sync cancelled.",
                    "同步已取消。"));
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Background sync failed.",
                exception);

            ShowInfo(
                InfoBarSeverity.Error,
                L(
                    "Sync failed",
                    "同步失败"),
                UserFacingErrors.Describe(
                    exception));
        }
        finally
        {
            _operationCancellation = null;
            SetBusy(
                false,
                OperationStatusText.Text);
        }
    }

    private void UpdateLiveSyncRow(
        SyncExecutionProgress progress)
    {
        LibraryDisplayItem? item =
            _allLibraryItems
                .FirstOrDefault(
                    candidate =>
                        string.Equals(
                            candidate.Entry.SourcePath,
                            progress.SourcePath,
                            StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            return;
        }

        if (progress.Stage == "complete")
        {
            item.SetStatus(
                "synced",
                StatusLabel(
                    "synced"));
        }
        else if (progress.Stage
            == "failed")
        {
            item.SetStatus(
                "error",
                StatusLabel(
                    "error"));
        }
        else if (progress.Stage
            is "copy-audio"
            or "state"
            or "inspect-lyrics"
            or "recognize"
            or "fetch-lyrics"
            or "write-lyrics"
            or "cleanup-old-destination")
        {
            item.SetStatus(
                "syncing",
                L(
                    "Syncing",
                    "同步中"));
        }

        SetTrackSyncState(
            item.Entry.SourcePath,
            progress.Stage == "failed"
                ? L("Error", "错误")
                : progress.Stage == "complete"
                    ? L("Synced", "已同步")
                    : FriendlyStage(
                        progress.Stage),
            L(
                $"[{progress.Stage}] {progress.Processed:N0}/{progress.Total:N0}",
                $"[{progress.Stage}] {progress.Processed:N0}/{progress.Total:N0}"),
            progress.Stage == "failed");

        if (ReferenceEquals(
                item,
                _selectedLibraryItem))
        {
            TrackStatusText.Text =
                item.StatusText;
            TrackStatusIcon.Glyph =
                item.StatusGlyph;
            TrackStatusIcon.Foreground =
                item.StatusBrush;
        }

        RefreshLiveSyncCounters();
    }

    private void SetTrackSyncState(
        string sourcePath,
        string status,
        string detail,
        bool isError = false)
    {
        _trackSyncStates[sourcePath] =
            new TrackSyncViewState(
                status,
                detail,
                DateTimeOffset.Now,
                isError);

        if (_selectedLibraryItem
                is LibraryDisplayItem selected
            && string.Equals(
                selected.Entry.SourcePath,
                sourcePath,
                StringComparison.OrdinalIgnoreCase))
        {
            RefreshTrackSyncTab(
                selected);
        }
    }

    private void RefreshTrackSyncTab(
        LibraryDisplayItem? item)
    {
        if (item is null)
        {
            SetTrackSyncTabVisual(
                "pending");
            TrackSyncStateText.Text =
                L(
                    "No sync information.",
                    "暂无同步信息。");
            TrackSyncTimeText.Text =
                string.Empty;
            TrackSyncLogText.Text =
                string.Empty;
            return;
        }

        if (_trackSyncStates.TryGetValue(
                item.Entry.SourcePath,
                out TrackSyncViewState? latest))
        {
            SetTrackSyncTabVisual(
                latest.IsError
                    ? "error"
                    : item.Kind);
            TrackSyncStateText.Text =
                latest.Status;
            TrackSyncTimeText.Text =
                L(
                    $"Updated {latest.UpdatedAt:G}",
                    $"更新于 {latest.UpdatedAt:G}");
            TrackSyncLogText.Text =
                latest.Detail;
            return;
        }

        SetTrackSyncTabVisual(
            item.Kind);

        TrackSyncTimeText.Text =
            L(
                "Current library plan",
                "当前音乐库状态");

        if (item.Kind == "error")
        {
            TrackSyncStateText.Text =
                L("Error", "错误");
            TrackSyncLogText.Text =
                item.Entry.Detail
                ?? L(
                    "The current scan reports an error.",
                    "当前扫描报告了错误。");
        }
        else if (item.Kind == "synced")
        {
            TrackSyncStateText.Text =
                L("Synced", "已同步");
            TrackSyncLogText.Text =
                L(
                    "No sync error is recorded for this track.",
                    "此歌曲没有记录到同步错误。");
        }
        else if (item.Kind == "syncing")
        {
            TrackSyncStateText.Text =
                L("Syncing", "同步中");
            TrackSyncLogText.Text =
                item.Entry.Detail
                ?? L(
                    "Synchronization is in progress.",
                    "正在同步。");
        }
        else
        {
            TrackSyncStateText.Text =
                L(
                    "Pending sync",
                    "待同步");
            TrackSyncLogText.Text =
                item.Entry.Detail
                ?? L(
                    "This track has pending work.",
                    "此歌曲存在待处理的同步工作。");
        }
    }

    private void SetTrackSyncTabVisual(
        string kind)
    {
        TrackSyncTabStatusIcon.Glyph =
            kind switch
            {
                "synced" => "\uE73E",
                "error" => "\uEA39",
                "syncing" => "\uE895",
                _ => "\uE823",
            };

        TrackSyncTabStatusIcon.Foreground =
            kind switch
            {
                "synced" =>
                    new Microsoft.UI.Xaml.Media
                        .SolidColorBrush(
                            Color.FromArgb(
                                255,
                                16,
                                124,
                                65)),
                "error" =>
                    new Microsoft.UI.Xaml.Media
                        .SolidColorBrush(
                            Color.FromArgb(
                                255,
                                196,
                                43,
                                28)),
                "syncing" =>
                    Application.Current.Resources[
                        "AccentTextFillColorPrimaryBrush"]
                    as Microsoft.UI.Xaml.Media.Brush,
                _ =>
                    new Microsoft.UI.Xaml.Media
                        .SolidColorBrush(
                            Color.FromArgb(
                                255,
                                202,
                                116,
                                0)),
            };
    }

    private void RefreshLiveSyncCounters()
    {
        PendingCountText.Text =
            _allLibraryItems
                .Count(static item =>
                    item.Kind
                    is "pending" or "syncing")
                .ToString("N0");

        ErrorCountText.Text =
            _allLibraryItems
                .Count(static item =>
                    item.Kind == "error")
                .ToString("N0");
    }

    private async Task RefreshLibraryAfterSyncAsync(
        MusicLoomSettings settings,
        IReadOnlyList<TrackSyncError> syncErrors,
        CancellationToken cancellationToken)
    {
        try
        {
            SetOperationStatus(
                L(
                    "Refreshing sync state…",
                    "正在刷新同步状态……"));
            SetIndeterminateProgress();

            StateStore stateStore =
                new SqliteStateStore(
                    DatabasePath,
                    LegacyStatePath);
            StateDocument state =
                await stateStore.LoadAsync(
                    cancellationToken);

            LibraryScanner scanner =
                new(
                    new AtlMetadataReader());

            LibraryPlan freshPlan =
                await Task.Run(
                    async () =>
                        await scanner.BuildPlanAsync(
                            settings,
                            state,
                            cancellationToken:
                                cancellationToken),
                    cancellationToken);

            await stateStore.SaveAsync(
                state,
                cancellationToken);

            _settings = settings;
            _stateStore = stateStore;
            _state = state;
            _plan = freshPlan;

            Dictionary<string, PlanEntry>
                freshBySource =
                    freshPlan.Entries
                        .ToDictionary(
                            static entry =>
                                entry.SourcePath,
                            StringComparer.OrdinalIgnoreCase);

            bool structureChanged =
                freshBySource.Count
                != _allLibraryItems.Count
                || _allLibraryItems.Any(
                    item =>
                        !freshBySource.ContainsKey(
                            item.Entry.SourcePath));

            if (structureChanged)
            {
                PopulateLibrary(
                    settings,
                    freshPlan);
            }
            else
            {
                foreach (LibraryDisplayItem item
                    in _allLibraryItems)
                {
                    PlanEntry entry =
                        freshBySource[
                            item.Entry.SourcePath];

                    item.UpdateEntry(
                        entry);

                    string kind =
                        entry.Status
                            == PlanEntryStatus.Error
                            ? "error"
                            : entry.Status
                                == PlanEntryStatus.Unchanged
                                ? "synced"
                                : "pending";

                    item.SetStatus(
                        kind,
                        StatusLabel(
                            kind));
                }

                ApplyLibraryFilter();
            }

            UpdateLibrarySummary(
                freshPlan);

            foreach (TrackSyncError error
                in syncErrors)
            {
                LibraryDisplayItem? errorItem =
                    _allLibraryItems
                        .FirstOrDefault(
                            item =>
                                string.Equals(
                                    item.Entry.SourcePath,
                                    error.SourcePath,
                                    StringComparison.OrdinalIgnoreCase));

                errorItem?.SetStatus(
                    "error",
                    StatusLabel(
                        "error"));
            }

            if (syncErrors.Count > 0)
            {
                ErrorCountText.Text =
                    syncErrors.Count
                        .ToString("N0");
            }

            await LibraryDatabase.SavePlanAsync(
                DatabasePath,
                settings.Path.SourceDirectory,
                settings.Path.DestinationDirectory,
                freshPlan,
                cancellationToken);
            RefreshDatabaseSize();

            PopulatePlaylists(
                await Task.Run(
                    () => BuildPlaylists(
                        settings),
                    cancellationToken));

            SetOperationStatus(
                L(
                    $"{freshPlan.TotalCount:N0} tracks · {freshPlan.PendingTrackCount:N0} pending · {freshPlan.ErrorCount:N0} errors",
                    $"{freshPlan.TotalCount:N0} 首歌曲 · {freshPlan.PendingTrackCount:N0} 首待同步 · {freshPlan.ErrorCount:N0} 个错误"));
        }
        catch (Exception exception)
            when (exception
                is not OperationCanceledException)
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
        PlaylistCancelButton.IsEnabled = false;
        SetOperationStatus(
            L(
                "Cancelling…",
                "正在取消……"));

        await cancellation.CancelAsync();
    }

    private async Task<bool> ScanLibraryAsync(
        bool clearInfoBar,
        bool recordActivity)
    {
        MusicLoomSettings configured =
            _settings
            ?? await LoadPersistedSettingsAsync();

        if (string.IsNullOrWhiteSpace(
                configured.Path.SourceDirectory)
            || string.IsNullOrWhiteSpace(
                configured.Path.DestinationDirectory))
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
                _settings
                ?? await LoadPersistedSettingsAsync(
                    cancellation.Token);

            _settings = settings;
            _uiLanguage =
                NormalizeLanguage(
                    settings.Ui.Language);

            LibraryPathSafety.ValidateStatePath(
                settings,
                DatabasePath);

            StateStore stateStore =
                new SqliteStateStore(
                    DatabasePath,
                    LegacyStatePath);
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
            RefreshDatabaseSize();

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

            LibraryDisplayItem displayItem =
                new(
                    entry,
                    kind,
                    title,
                    artist,
                    album,
                    relative,
                    StatusLabel(kind));

            displayItem.ApplyLongTextMode(
                CurrentLongTextMode);

            _allLibraryItems.Add(
                displayItem);
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

    private void LibrarySortDirectionBox_SelectionChanged(
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
            ?? "artist";

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

        bool descending =
            string.Equals(
                (LibrarySortDirectionBox.SelectedItem
                    as ComboBoxItem)
                    ?.Tag?.ToString(),
                "desc",
                StringComparison.Ordinal);

        if (descending)
        {
            items = items.Reverse();
        }

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
            OpenMatchDialogButton.IsEnabled = false;
            HideDetailPane();
            ResetTrackDetails();
            return;
        }

        _selectedLibraryItem = item;
        OpenMatchDialogButton.IsEnabled = !_busy;
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
        TrackTitleTranslateTransform.X = 0;
        TrackAlbumTranslateTransform.X = 0;

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

        TrackSyncStateText.Text =
            L(
                "No sync information.",
                "暂无同步信息。");
        SetTrackSyncTabVisual(
            "pending");
        TrackSyncTimeText.Text = string.Empty;
        TrackSyncLogText.Text = string.Empty;

        TrackSourcePathText.Text = string.Empty;
        TrackDestinationPathText.Text = string.Empty;
        TrackFormatText.Text = string.Empty;
        TrackDurationText.Text = string.Empty;
        TrackBitrateText.Text = string.Empty;
        TrackSampleRateText.Text = string.Empty;
        TrackBitDepthText.Text = string.Empty;
        TrackChannelsText.Text = string.Empty;
        TrackBitrateModeText.Text = string.Empty;
        TrackSizeText.Text = string.Empty;
        TrackModifiedText.Text = string.Empty;
        TrackCreatedText.Text = string.Empty;
        TrackDestinationSizeText.Text = string.Empty;
        TrackDestinationModifiedText.Text = string.Empty;
    }

    private string CurrentLongTextMode =>
        LongTextModes.Normalize(
            _settings?.Ui.LongTextMode,
            _settings?.Ui.AutoScrollLongTitles
                ?? true);

    private void ConfigureTitleAutoScroll()
    {
        StopTitleAutoScroll();

        TrackTitleTranslateTransform.X = 0;
        TrackAlbumTranslateTransform.X = 0;

        string mode =
            CurrentLongTextMode;

        foreach (LibraryDisplayItem item
            in _allLibraryItems)
        {
            item.ApplyLongTextMode(mode);
        }

        ConfigureDetailText(
            TrackTitleViewport,
            TrackTitleText,
            mode,
            maxLines: 3);
        ConfigureDetailText(
            TrackAlbumViewport,
            TrackAlbumText,
            mode,
            maxLines: 2);

        DispatcherQueue.TryEnqueue(
            () =>
            {
                TrackTitleViewport.UpdateLayout();
                TrackAlbumViewport.UpdateLayout();

                if (mode == LongTextModes.Hover)
                {
                    TrackTitleText.Width =
                        Math.Max(
                            0,
                            TrackTitleViewport
                                .ViewportWidth);
                    TrackAlbumText.Width =
                        Math.Max(
                            0,
                            TrackAlbumViewport
                                .ViewportWidth);
                    return;
                }

                if (mode
                    != LongTextModes.AutoScroll)
                {
                    return;
                }

                double titleOverflow =
                    GetTextOverflow(
                        TrackTitleViewport,
                        TrackTitleText);
                double albumOverflow =
                    GetTextOverflow(
                        TrackAlbumViewport,
                        TrackAlbumText);

                _titleScrollStoryboard =
                    StartTextMarquee(
                        TrackTitleTranslateTransform,
                        titleOverflow);
                _albumScrollStoryboard =
                    StartTextMarquee(
                        TrackAlbumTranslateTransform,
                        albumOverflow);
            });
    }

    private static void ConfigureDetailText(
        ScrollViewer viewport,
        TextBlock text,
        string mode,
        int maxLines)
    {
        string normalized =
            LongTextModes.Normalize(
                mode);

        bool wrap =
            normalized
            == LongTextModes.Wrap;
        bool hover =
            normalized
            == LongTextModes.Hover;

        viewport.HorizontalScrollBarVisibility =
            ScrollBarVisibility.Hidden;
        viewport.HorizontalScrollMode =
            ScrollMode.Disabled;
        viewport.IsHitTestVisible =
            hover;

        text.HorizontalAlignment =
            normalized
                == LongTextModes.AutoScroll
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Stretch;
        text.Width = double.NaN;

        text.TextWrapping =
            wrap
                ? TextWrapping.WrapWholeWords
                : TextWrapping.NoWrap;
        text.TextTrimming =
            hover
                ? TextTrimming.CharacterEllipsis
                : TextTrimming.None;
        text.MaxLines =
            wrap
                ? maxLines
                : 1;

        ToolTipService.SetToolTip(
            text,
            hover
                ? text.Text
                : null);
    }

    private static double GetTextOverflow(
        ScrollViewer viewport,
        TextBlock text)
    {
        text.Measure(
            new Windows.Foundation.Size(
                double.PositiveInfinity,
                double.PositiveInfinity));

        return Math.Max(
            0,
            text.DesiredSize.Width
            - viewport.ViewportWidth);
    }

    private void StopTitleAutoScroll()
    {
        _titleScrollStoryboard?.Stop();
        _albumScrollStoryboard?.Stop();
        _titleScrollStoryboard = null;
        _albumScrollStoryboard = null;
    }

    private static Storyboard? StartTextMarquee(
        TranslateTransform transform,
        double overflow)
    {
        if (overflow <= 1)
        {
            transform.X = 0;
            return null;
        }

        DoubleAnimation animation = new()
        {
            From = 0,
            To = -overflow,
            Duration =
                new Duration(
                    TimeSpan.FromSeconds(
                        Math.Clamp(
                            overflow / 48.0,
                            3.2,
                            18.0))),
            AutoReverse = true,
            RepeatBehavior =
                RepeatBehavior.Forever,
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(
            animation,
            transform);
        Storyboard.SetTargetProperty(
            animation,
            "X");

        Storyboard storyboard = new();
        storyboard.Children.Add(
            animation);
        storyboard.Begin();
        return storyboard;
    }

    private void ConfigureAuxiliaryLongText()
    {
        StopAuxiliaryLongText();

        string mode =
            CurrentLongTextMode;

        ConfigureDetailText(
            LyricsSourceViewport,
            LyricsSourceText,
            mode,
            maxLines: 2);
        ConfigureDetailText(
            CurrentMatchViewport,
            CurrentMatchText,
            mode,
            maxLines: 3);
        ConfigureDetailText(
            PlaylistTitleViewport,
            PlaylistTitleText,
            mode,
            maxLines: 2);
        ConfigureDetailText(
            PlaylistMetaViewport,
            PlaylistMetaText,
            mode,
            maxLines: 2);

        LyricsSourceTranslateTransform.X = 0;
        CurrentMatchTranslateTransform.X = 0;
        PlaylistTitleTranslateTransform.X = 0;
        PlaylistMetaTranslateTransform.X = 0;

        DispatcherQueue.TryEnqueue(
            () =>
            {
                _lyricsSourceScrollStoryboard =
                    ConfigureAuxiliaryViewport(
                        LyricsSourceViewport,
                        LyricsSourceText,
                        LyricsSourceTranslateTransform,
                        mode);
                _currentMatchScrollStoryboard =
                    ConfigureAuxiliaryViewport(
                        CurrentMatchViewport,
                        CurrentMatchText,
                        CurrentMatchTranslateTransform,
                        mode);
                _playlistTitleScrollStoryboard =
                    ConfigureAuxiliaryViewport(
                        PlaylistTitleViewport,
                        PlaylistTitleText,
                        PlaylistTitleTranslateTransform,
                        mode);
                _playlistMetaScrollStoryboard =
                    ConfigureAuxiliaryViewport(
                        PlaylistMetaViewport,
                        PlaylistMetaText,
                        PlaylistMetaTranslateTransform,
                        mode);
            });
    }

    private static Storyboard?
        ConfigureAuxiliaryViewport(
            ScrollViewer viewport,
            TextBlock text,
            TranslateTransform transform,
            string mode)
    {
        viewport.UpdateLayout();

        if (mode == LongTextModes.Hover)
        {
            text.Width =
                Math.Max(
                    0,
                    viewport.ViewportWidth);
            return null;
        }

        if (mode != LongTextModes.AutoScroll)
        {
            return null;
        }

        return StartTextMarquee(
            transform,
            GetTextOverflow(
                viewport,
                text));
    }

    private void StopAuxiliaryLongText()
    {
        _lyricsSourceScrollStoryboard?.Stop();
        _currentMatchScrollStoryboard?.Stop();
        _playlistTitleScrollStoryboard?.Stop();
        _playlistMetaScrollStoryboard?.Stop();

        _lyricsSourceScrollStoryboard = null;
        _currentMatchScrollStoryboard = null;
        _playlistTitleScrollStoryboard = null;
        _playlistMetaScrollStoryboard = null;
    }

    private async Task ShowTrackDetailsAsync(
        LibraryDisplayItem item)
    {
        int requestId =
            ++_detailRequestId;

        TrackTitleText.Text =
            item.Title;
        TrackArtistText.Text =
            item.Artist;
        TrackAlbumText.Text =
            item.Album;
        ConfigureTitleAutoScroll();
        TrackStatusText.Text =
            item.StatusText;
        TrackStatusIcon.Glyph =
            item.StatusGlyph;
        TrackStatusIcon.Foreground =
            item.StatusBrush;
        RefreshTrackSyncTab(item);

        CurrentMatchText.Text =
            GetCachedMatchText(
                item.Entry.SourcePath);
        ConfigureAuxiliaryLongText();

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
            ConfigureAuxiliaryLongText();
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
            TrackDurationText.Text =
                payload.Duration;
            TrackBitrateText.Text =
                payload.Bitrate;
            TrackSampleRateText.Text =
                payload.SampleRate;
            TrackBitDepthText.Text =
                payload.BitDepth;
            TrackChannelsText.Text =
                payload.Channels;
            TrackBitrateModeText.Text =
                payload.BitrateMode;
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
            FormatDuration(
                inspection.DurationMs),
            inspection.Bitrate > 0
                ? $"{inspection.Bitrate:0.#} kbps"
                : "—",
            inspection.SampleRate > 0
                ? $"{inspection.SampleRate:0} Hz"
                : "—",
            inspection.BitDepth > 0
                ? $"{inspection.BitDepth} bit"
                : "—",
            inspection.Channels > 0
                ? string.IsNullOrWhiteSpace(
                    inspection.ChannelArrangement)
                    ? inspection.Channels
                        .ToString(
                            CultureInfo.InvariantCulture)
                    : $"{inspection.Channels} · {inspection.ChannelArrangement}"
                : "—",
            inspection.IsLossless
                ? L(
                    "Lossless",
                    "无损")
                : inspection.IsVbr
                    ? "VBR"
                    : "CBR",
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
        LyricDisplayLine[] detected =
            BuildLyricLines(
                _currentLyricsRaw,
                LyricsDisplayModes
                    .PreferOriginal);

        bool hasLyrics =
            detected.Length > 0;
        bool hasTranslation =
            detected.Any(
                static line =>
                    !string.IsNullOrWhiteSpace(
                        line.Translation));

        _lyricsDisplayMode =
            LyricsDisplayModes.Normalize(
                _lyricsDisplayMode);

        int selectedIndex =
            _lyricsDisplayMode switch
            {
                LyricsDisplayModes
                    .OriginalOnly => 0,
                LyricsDisplayModes
                    .PreferTranslation => 1,
                _ => 2,
            };

        if (LyricsModeBox.SelectedIndex
            != selectedIndex)
        {
            LyricsModeBox.SelectedIndex =
                selectedIndex;
        }

        LyricsModeBox.IsEnabled =
            hasLyrics && hasTranslation;
        CopyAllLyricsButton.IsEnabled =
            hasLyrics;

        LyricDisplayLine[] lines =
            hasLyrics
                ? BuildLyricLines(
                    _currentLyricsRaw,
                    _lyricsDisplayMode)
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
                        LyricsDisplayModes
                            .OriginalOnly),
                }
                : lines;
    }

    private void LyricsModeBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingSettings
            || LyricsModeBox.SelectedItem
                is not ComboBoxItem item
            || item.Tag is not string mode)
        {
            return;
        }

        _lyricsDisplayMode =
            LyricsDisplayModes.Normalize(
                mode);

        RenderLyrics();
        _ = PersistLyricsDisplayModeAsync();
    }

    private async Task PersistLyricsDisplayModeAsync()
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            MusicLoomSettings updated =
                _settings with
                {
                    Ui =
                        _settings.Ui with
                        {
                            LyricsDisplayMode =
                                _lyricsDisplayMode,
                        },
                };

            _settings = updated;

            await LibraryDatabase.SaveSettingsAsync(
                DatabasePath,
                updated);

            RefreshDatabaseSize();
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not save the lyrics display mode.",
                exception);
        }
    }

    private void CopyAllLyricsTextItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        LyricDisplayLine[] lines =
            BuildLyricLines(
                _currentLyricsRaw,
                _lyricsDisplayMode);

        if (lines.Length == 0)
        {
            return;
        }

        CopyText(
            BuildPlainLyricText(
                lines));
    }

    private void CopyAllLyricsLrcItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        LyricDisplayLine[] lines =
            BuildLyricLines(
                _currentLyricsRaw,
                _lyricsDisplayMode);

        if (lines.Length == 0)
        {
            return;
        }

        CopyText(
            BuildLrcLyricText(
                lines));
    }


    private void LyricLineMenu_Opening(
        object sender,
        object e)
    {
        if (sender is not MenuFlyout menu
            || menu.Items.Count == 0
            || menu.Items[0]
                is not MenuFlyoutSubItem copy)
        {
            return;
        }

        copy.Text =
            L(
                "Copy line",
                "复制此行");

        if (copy.Items.Count > 0
            && copy.Items[0]
                is MenuFlyoutItem textItem)
        {
            textItem.Text =
                L(
                    "Copy as text",
                    "复制为文本");
        }

        if (copy.Items.Count > 1
            && copy.Items[1]
                is MenuFlyoutItem lrcItem)
        {
            lrcItem.Text =
                L(
                    "Copy as LRC",
                    "复制为 LRC");
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

    private void CopyLyricLineTextItem_Click(
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

        CopyText(
            BuildPlainLyricText(
                [line]));
    }

    private void CopyLyricLineLrcItem_Click(
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

        CopyText(
            BuildLrcLyricText(
                [line]));
    }

    private static string BuildPlainLyricText(
        IEnumerable<LyricDisplayLine> lines) =>
        string.Join(
            Environment.NewLine,
            lines.Select(
                static line =>
                    string.IsNullOrWhiteSpace(
                        line.SecondaryText)
                        ? line.PrimaryText
                        : line.PrimaryText
                            + Environment.NewLine
                            + line.SecondaryText));

    private static string BuildLrcLyricText(
        IEnumerable<LyricDisplayLine> lines)
    {
        List<string> output = [];

        foreach (LyricDisplayLine line
            in lines)
        {
            string tag =
                line.TimestampLrcTag;

            AppendLrcText(
                output,
                tag,
                line.PrimaryText);

            if (!string.IsNullOrWhiteSpace(
                    line.SecondaryText))
            {
                AppendLrcText(
                    output,
                    tag,
                    line.SecondaryText!);
            }
        }

        return string.Join(
            Environment.NewLine,
            output);
    }

    private static void AppendLrcText(
        List<string> output,
        string timestampTag,
        string text)
    {
        foreach (string rawLine
            in text.Split(
                ['\r', '\n'],
                StringSplitOptions
                    .RemoveEmptyEntries))
        {
            string value =
                rawLine.Trim();

            if (value.Length == 0)
            {
                continue;
            }

            output.Add(
                timestampTag
                + value);
        }
    }


    private static LyricDisplayLine[]
        BuildLyricLines(
            string? raw,
            string displayMode)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        string normalizedMode =
            LyricsDisplayModes.Normalize(
                displayMode);

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
                    string[] translatedLines =
                        texts
                            .Skip(1)
                            .Where(
                                static text =>
                                    !LooksLikeCreditLine(
                                        text))
                            .ToArray();

                    if (translatedLines.Length
                        > 0)
                    {
                        translation =
                            string.Join(
                                Environment.NewLine,
                                translatedLines);
                    }
                }

                result.Add(
                    new LyricDisplayLine(
                        original,
                        translation,
                        normalizedMode,
                        group.Key));
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
                    normalizedMode));
        }

        return [.. result];
    }

    private static (string Original,
        string? Translation)
        SplitTranslation(string text)
    {
        if (text.Contains(
                "://",
                StringComparison.Ordinal)
            || LooksLikeCreditLine(text))
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

    private static bool LooksLikeCreditLine(
        string text)
    {
        int asciiColon =
            text.IndexOf(':');
        int fullWidthColon =
            text.IndexOf('：');

        int colon =
            asciiColon < 0
                ? fullWidthColon
                : fullWidthColon < 0
                    ? asciiColon
                    : Math.Min(
                        asciiColon,
                        fullWidthColon);

        if (colon <= 0 || colon > 24)
        {
            return false;
        }

        string label =
            text[..colon]
                .Trim();

        string[] creditLabels =
        [
            "器乐",
            "演奏",
            "作词",
            "作詞",
            "作曲",
            "编曲",
            "編曲",
            "制作",
            "制作人",
            "演唱",
            "和声",
            "和聲",
            "吉他",
            "贝斯",
            "貝斯",
            "鼓",
            "键盘",
            "鍵盤",
            "弦乐",
            "弦楽",
            "混音",
            "母带",
            "母帶",
            "录音",
            "錄音",
            "监制",
            "監製",
            "lyrics",
            "lyricist",
            "composer",
            "arranger",
            "arrangement",
            "instrumental",
            "guitar",
            "bass",
            "drums",
            "keyboard",
            "producer",
            "mix",
            "mixing",
            "mastering",
            "recording",
        ];

        return creditLabels.Any(
            credit =>
                string.Equals(
                    label,
                    credit,
                    StringComparison
                        .OrdinalIgnoreCase));
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
        if (entry.Metadata is not null)
        {
            string? sourceDirectory =
                Path.GetDirectoryName(
                    entry.SourcePath);

            if (!string.IsNullOrWhiteSpace(
                    sourceDirectory)
                && Directory.Exists(
                    sourceDirectory))
            {
                string? sourceCover =
                    AlbumArtLocator.Find(
                        sourceDirectory,
                        entry.Metadata,
                        onlyExplicit: false);

                if (sourceCover is not null)
                {
                    return File.ReadAllBytes(
                        sourceCover);
                }

                // A destination Cover.* can be stale (notably from a
                // previous WMP Folder.jpg cache). Current source state wins.
                return null;
            }
        }

        if (entry.DestinationPath
            is not string destination)
        {
            return null;
        }

        string? directory =
            Path.GetDirectoryName(
                destination);

        if (string.IsNullOrWhiteSpace(
                directory)
            || !Directory.Exists(
                directory))
        {
            return null;
        }

        string? existing =
            Directory
                .EnumerateFiles(
                    directory,
                    "Cover.*",
                    SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

        return existing is null
            ? null
            : File.ReadAllBytes(
                existing);
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
        PointerRoutedEventArgs e) =>
        AnimateCoverScale(1.07);

    private void CoverButton_PointerExited(
        object sender,
        PointerRoutedEventArgs e) =>
        AnimateCoverScale(1.0);

    private void AnimateCoverScale(
        double target)
    {
        CubicEase ease = new()
        {
            EasingMode =
                EasingMode.EaseOut,
        };

        DoubleAnimation x = new()
        {
            To = target,
            Duration =
                new Duration(
                    TimeSpan.FromMilliseconds(
                        180)),
            EasingFunction = ease,
            EnableDependentAnimation = true,
        };
        DoubleAnimation y = new()
        {
            To = target,
            Duration =
                new Duration(
                    TimeSpan.FromMilliseconds(
                        180)),
            EasingFunction = ease,
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(
            x,
            CoverScaleTransform);
        Storyboard.SetTargetProperty(
            x,
            "ScaleX");
        Storyboard.SetTarget(
            y,
            CoverScaleTransform);
        Storyboard.SetTargetProperty(
            y,
            "ScaleY");

        Storyboard storyboard = new();
        storyboard.Children.Add(x);
        storyboard.Children.Add(y);
        storyboard.Begin();
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

            const double ViewportSize = 600;

            CompositeTransform imageTransform =
                new()
                {
                    ScaleX = 1,
                    ScaleY = 1,
                };

            Image image = new()
            {
                Source = bitmap,
                Width = ViewportSize,
                Height = ViewportSize,
                Stretch =
                    Microsoft.UI.Xaml.Media
                        .Stretch.Uniform,
                HorizontalAlignment =
                    HorizontalAlignment.Left,
                VerticalAlignment =
                    VerticalAlignment.Top,
                RenderTransform =
                    imageTransform,
                RenderTransformOrigin =
                    new Windows.Foundation.Point(
                        0,
                        0),
            };

            Grid viewport = new()
            {
                Width = ViewportSize,
                Height = ViewportSize,
                Background =
                    Application.Current.Resources[
                        "CardBackgroundFillColorSecondaryBrush"]
                    as Microsoft.UI.Xaml.Media.Brush,
                Clip =
                    new RectangleGeometry
                    {
                        Rect =
                            new Windows.Foundation.Rect(
                                0,
                                0,
                                ViewportSize,
                                ViewportSize),
                    },
            };
            viewport.Children.Add(image);

            double scale = 1;
            double translateX = 0;
            double translateY = 0;
            bool dragging = false;
            Windows.Foundation.Point
                lastPointer = default;

            PointerEventHandler wheelHandler =
                (_, args) =>
                {
                    var point =
                        args.GetCurrentPoint(
                            viewport);
                    int delta =
                        point.Properties
                            .MouseWheelDelta;

                    if (delta == 0)
                    {
                        return;
                    }

                    double oldScale =
                        scale;
                    double nextScale =
                        Math.Clamp(
                            scale
                            * (delta > 0
                                ? 1.14
                                : 1.0 / 1.14),
                            0.25,
                            6.0);

                    if (Math.Abs(
                            nextScale - oldScale)
                        < 0.0001)
                    {
                        args.Handled = true;
                        return;
                    }

                    Windows.Foundation.Point p =
                        point.Position;

                    double factor =
                        nextScale / oldScale;

                    translateX =
                        p.X
                        - (p.X - translateX)
                        * factor;
                    translateY =
                        p.Y
                        - (p.Y - translateY)
                        * factor;
                    scale =
                        nextScale;

                    imageTransform.ScaleX =
                        scale;
                    imageTransform.ScaleY =
                        scale;
                    imageTransform.TranslateX =
                        translateX;
                    imageTransform.TranslateY =
                        translateY;

                    args.Handled = true;
                };

            PointerEventHandler pressedHandler =
                (_, args) =>
                {
                    var point =
                        args.GetCurrentPoint(
                            viewport);

                    if (!point.Properties
                        .IsLeftButtonPressed)
                    {
                        return;
                    }

                    dragging = true;
                    lastPointer =
                        point.Position;
                    viewport.CapturePointer(
                        args.Pointer);
                    args.Handled = true;
                };

            PointerEventHandler movedHandler =
                (_, args) =>
                {
                    if (!dragging)
                    {
                        return;
                    }

                    Windows.Foundation.Point
                        position =
                            args.GetCurrentPoint(
                                viewport)
                                .Position;

                    translateX +=
                        position.X
                        - lastPointer.X;
                    translateY +=
                        position.Y
                        - lastPointer.Y;
                    lastPointer = position;

                    imageTransform.TranslateX =
                        translateX;
                    imageTransform.TranslateY =
                        translateY;
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
                viewport.ReleasePointerCapture(
                    args.Pointer);
                args.Handled = true;
            }

            viewport.AddHandler(
                UIElement.PointerWheelChangedEvent,
                wheelHandler,
                handledEventsToo: true);
            viewport.AddHandler(
                UIElement.PointerPressedEvent,
                pressedHandler,
                handledEventsToo: true);
            viewport.AddHandler(
                UIElement.PointerMovedEvent,
                movedHandler,
                handledEventsToo: true);
            viewport.PointerReleased +=
                (_, args) =>
                    EndDrag(args);
            viewport.PointerCanceled +=
                (_, args) =>
                    EndDrag(args);

            string dialogTitle =
                string.IsNullOrWhiteSpace(
                    TrackAlbumText.Text)
                    ? TrackTitleText.Text
                    : TrackAlbumText.Text;

            TextBlock titleText = new()
            {
                Text = dialogTitle,
                FontSize = 18,
                FontWeight =
                    new Windows.UI.Text.FontWeight
                    {
                        Weight = 600,
                    },
                VerticalAlignment =
                    VerticalAlignment.Center,
            };

            ScrollViewer titleScroller = new()
            {
                Content = titleText,
                HorizontalScrollBarVisibility =
                    ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility =
                    ScrollBarVisibility.Disabled,
                HorizontalScrollMode =
                    ScrollMode.Enabled,
                VerticalScrollMode =
                    ScrollMode.Disabled,
                ZoomMode =
                    ZoomMode.Disabled,
            };

            string longTextMode =
                CurrentLongTextMode;

            if (longTextMode
                == LongTextModes.Wrap)
            {
                titleText.TextWrapping =
                    TextWrapping.WrapWholeWords;
                titleText.MaxLines = 2;
                titleScroller.HorizontalScrollMode =
                    ScrollMode.Disabled;
            }
            else if (longTextMode
                == LongTextModes.Hover)
            {
                titleText.TextWrapping =
                    TextWrapping.NoWrap;
                titleText.TextTrimming =
                    TextTrimming.CharacterEllipsis;
                ToolTipService.SetToolTip(
                    titleText,
                    dialogTitle);
                titleScroller.HorizontalScrollMode =
                    ScrollMode.Disabled;
            }
            else
            {
                titleText.TextWrapping =
                    TextWrapping.NoWrap;
                titleText.TextTrimming =
                    TextTrimming.None;
            }

            ContentDialog dialog = new()
            {
                XamlRoot =
                    ((FrameworkElement)Content)
                        .XamlRoot,
                CloseButtonText =
                    L("Close", "关闭"),
                DefaultButton =
                    ContentDialogButton.Close,
            };

            Button closeButton = new()
            {
                Width = 30,
                Height = 30,
                Padding = new Thickness(0),
                HorizontalAlignment =
                    HorizontalAlignment.Right,
                VerticalAlignment =
                    VerticalAlignment.Center,
                Background =
                    Application.Current.Resources[
                        "CardBackgroundFillColorDefaultBrush"]
                    as Microsoft.UI.Xaml.Media.Brush,
                BorderThickness =
                    new Thickness(0),
                Foreground =
                    Application.Current.Resources[
                        "TextFillColorPrimaryBrush"]
                    as Microsoft.UI.Xaml.Media.Brush,
                Content = new FontIcon
                {
                    Glyph = "\uE711",
                    FontSize = 11,
                },
            };
            ToolTipService.SetToolTip(
                closeButton,
                L("Close", "关闭"));
            closeButton.Click +=
                (_, _) =>
                    dialog.Hide();

            Grid header = new()
            {
                Width = ViewportSize,
                ColumnSpacing = 8,
            };
            header.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(
                            1,
                            GridUnitType.Star),
                });
            header.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        GridLength.Auto,
                });
            header.Children.Add(
                titleScroller);
            Grid.SetColumn(
                closeButton,
                1);
            header.Children.Add(
                closeButton);

            Grid dialogContent = new()
            {
                Width = ViewportSize,
                RowSpacing = 10,
            };
            dialogContent.RowDefinitions.Add(
                new RowDefinition
                {
                    Height =
                        GridLength.Auto,
                });
            dialogContent.RowDefinitions.Add(
                new RowDefinition
                {
                    Height =
                        new GridLength(
                            ViewportSize),
                });
            dialogContent.Children.Add(
                header);
            Grid.SetRow(
                viewport,
                1);
            dialogContent.Children.Add(
                viewport);

            dialog.Content =
                dialogContent;

            DispatcherTimer?
                titleTimer = null;

            TranslateTransform
                dialogTitleTransform = new();
            titleText.RenderTransform =
                dialogTitleTransform;

            if (longTextMode
                == LongTextModes.AutoScroll)
            {
                dialog.Opened +=
                    (_, _) =>
                    {
                        titleScroller.UpdateLayout();
                        titleText.Measure(
                            new Windows.Foundation.Size(
                                double.PositiveInfinity,
                                double.PositiveInfinity));

                        double max =
                            Math.Max(
                                0,
                                titleText.DesiredSize.Width
                                - titleScroller.ViewportWidth);

                        if (max <= 1)
                        {
                            return;
                        }

                        double offset = 0;
                        double direction = 1;
                        DateTimeOffset pauseUntil =
                            DateTimeOffset.UtcNow
                                .AddMilliseconds(
                                    420);

                        titleTimer =
                            new DispatcherTimer
                            {
                                Interval =
                                    TimeSpan
                                        .FromMilliseconds(
                                            16),
                            };

                        titleTimer.Tick +=
                            (_, _) =>
                            {
                                if (DateTimeOffset.UtcNow
                                    < pauseUntil)
                                {
                                    return;
                                }

                                double next =
                                    offset
                                    + direction * 2.4;

                                if (next >= max)
                                {
                                    next = max;
                                    direction = -1;
                                    pauseUntil =
                                        DateTimeOffset.UtcNow
                                            .AddMilliseconds(
                                                450);
                                }
                                else if (next <= 0)
                                {
                                    next = 0;
                                    direction = 1;
                                    pauseUntil =
                                        DateTimeOffset.UtcNow
                                            .AddMilliseconds(
                                                450);
                                }

                                offset = next;
                                dialogTitleTransform.X =
                                    -offset;
                            };

                        titleTimer.Start();
                    };
            }

            await dialog.ShowAsync();

            titleTimer?.Stop();
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
                item.Artist.Contains(
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
                item.Album.Contains(
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
            HorizontalAlignment =
                HorizontalAlignment.Stretch,
        };

        Button searchButton = new()
        {
            Content =
                L(
                    "Search by metadata",
                    "按元数据搜索"),
            HorizontalAlignment =
                HorizontalAlignment.Stretch,
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
            SelectionMode =
                ListViewSelectionMode.Single,
            HorizontalContentAlignment =
                HorizontalAlignment.Stretch,
        };

        ListView lyricsPreview = new()
        {
            SelectionMode =
                ListViewSelectionMode.Single,
            IsItemClickEnabled = false,
            HorizontalContentAlignment =
                HorizontalAlignment.Stretch,
            ItemContainerStyle =
                LyricsList.ItemContainerStyle,
            ItemTemplate =
                LyricsList.ItemTemplate,
        };
        lyricsPreview.ItemsSource =
            new[]
            {
                new LyricDisplayLine(
                    L(
                        "Select a result to preview lyrics.",
                        "选择结果后可预览歌词。"),
                    null,
                    LyricsDisplayModes
                        .OriginalOnly),
            };

        StackPanel searchPanel = new()
        {
            Spacing = 10,
        };
        searchPanel.Children.Add(
            new TextBlock
            {
                Text =
                    L("Search", "搜索信息"),
                FontSize = 18,
                FontWeight =
                    new Windows.UI.Text.FontWeight
                    {
                        Weight = 600,
                    },
            });
        searchPanel.Children.Add(
            artistBox);
        searchPanel.Children.Add(
            titleBox);
        searchPanel.Children.Add(
            albumBox);
        searchPanel.Children.Add(
            fingerprintButton);
        searchPanel.Children.Add(
            searchButton);
        searchPanel.Children.Add(
            status);

        Grid candidatePanel = new();
        candidatePanel.RowDefinitions.Add(
            new RowDefinition
            {
                Height =
                    GridLength.Auto,
            });
        candidatePanel.RowDefinitions.Add(
            new RowDefinition
            {
                Height =
                    new GridLength(
                        1,
                        GridUnitType.Star),
            });
        TextBlock candidatesHeading = new()
        {
            Text =
                L(
                    "Candidates",
                    "候选项"),
            FontSize = 18,
            FontWeight =
                new Windows.UI.Text.FontWeight
                {
                    Weight = 600,
                },
            Margin =
                new Thickness(
                    0,
                    0,
                    0,
                    8),
        };
        candidatePanel.Children.Add(
            candidatesHeading);
        Grid.SetRow(
            results,
            1);
        candidatePanel.Children.Add(
            results);

        Grid lyricsPanel = new();
        lyricsPanel.RowDefinitions.Add(
            new RowDefinition
            {
                Height =
                    GridLength.Auto,
            });
        lyricsPanel.RowDefinitions.Add(
            new RowDefinition
            {
                Height =
                    new GridLength(
                        1,
                        GridUnitType.Star),
            });
        TextBlock lyricsHeading = new()
        {
            Text =
                L(
                    "Lyrics",
                    "歌词"),
            FontSize = 18,
            FontWeight =
                new Windows.UI.Text.FontWeight
                {
                    Weight = 600,
                },
            Margin =
                new Thickness(
                    0,
                    0,
                    0,
                    8),
        };
        lyricsPanel.Children.Add(
            lyricsHeading);
        Grid.SetRow(
            lyricsPreview,
            1);
        lyricsPanel.Children.Add(
            lyricsPreview);

        Border WrapColumn(
            UIElement child)
        {
            Border border = new()
            {
                Padding =
                    new Thickness(12),
                CornerRadius =
                    new CornerRadius(8),
                BorderThickness =
                    new Thickness(1),
                BorderBrush =
                    Application.Current
                        .Resources[
                            "CardStrokeColorDefaultBrush"]
                        as Microsoft.UI.Xaml.Media
                            .Brush,
                Background =
                    Application.Current
                        .Resources[
                            "CardBackgroundFillColorDefaultBrush"]
                        as Microsoft.UI.Xaml.Media
                            .Brush,
                Child = child,
            };

            return border;
        }

        Grid root = new()
        {
            Width = 1120,
            Height = 560,
            ColumnSpacing = 12,
        };
        root.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width =
                    new GridLength(280),
            });
        root.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width =
                    new GridLength(420),
            });
        root.ColumnDefinitions.Add(
            new ColumnDefinition
            {
                Width =
                    new GridLength(
                        1,
                        GridUnitType.Star),
            });

        Border searchColumn =
            WrapColumn(searchPanel);
        Border candidateColumn =
            WrapColumn(candidatePanel);
        Border lyricColumn =
            WrapColumn(lyricsPanel);

        root.Children.Add(
            searchColumn);
        Grid.SetColumn(
            candidateColumn,
            1);
        root.Children.Add(
            candidateColumn);
        Grid.SetColumn(
            lyricColumn,
            2);
        root.Children.Add(
            lyricColumn);

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
            MinWidth = 1180,
            MaxWidth = 1180,
            Content = root,
        };

        dialog.Resources[
            "ContentDialogMinWidth"] =
                1180d;
        dialog.Resources[
            "ContentDialogMaxWidth"] =
                1240d;

        NeteaseMatchCandidate?
            GetSelectedCandidate()
        {
            return results.SelectedItem
                is ListViewItem row
                && row.Tag
                    is NeteaseMatchCandidate
                        candidate
                    ? candidate
                    : null;
        }

        ListViewItem CreateCandidateRow(
            NeteaseMatchCandidate
                candidate)
        {
            BitmapImage? bitmap = null;

            if (!string.IsNullOrWhiteSpace(
                    candidate.CoverUrl)
                && Uri.TryCreate(
                    candidate.CoverUrl,
                    UriKind.Absolute,
                    out Uri? coverUri))
            {
                bitmap = new BitmapImage
                {
                    DecodePixelWidth = 96,
                    UriSource = coverUri,
                };
            }

            Grid row = new()
            {
                MinHeight = 64,
                Padding =
                    new Thickness(
                        8,
                        7,
                        8,
                        7),
                ColumnSpacing = 10,
            };
            row.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(48),
                });
            row.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(
                            1,
                            GridUnitType.Star),
                });
            row.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(92),
                });

            Border cover = new()
            {
                Width = 48,
                Height = 48,
                CornerRadius =
                    new CornerRadius(6),
                Background =
                    Application.Current
                        .Resources[
                            "CardBackgroundFillColorSecondaryBrush"]
                        as Microsoft.UI.Xaml.Media
                            .Brush,
                Child =
                    new Image
                    {
                        Source = bitmap,
                        Stretch =
                            Microsoft.UI.Xaml.Media
                                .Stretch.Uniform,
                    },
            };
            row.Children.Add(cover);

            StackPanel text = new()
            {
                Spacing = 2,
                VerticalAlignment =
                    VerticalAlignment.Center,
            };
            text.Children.Add(
                new TextBlock
                {
                    Text = candidate.Name,
                    FontWeight =
                        new Windows.UI.Text
                            .FontWeight
                        {
                            Weight = 600,
                        },
                    TextTrimming =
                        TextTrimming
                            .CharacterEllipsis,
                });
            text.Children.Add(
                new TextBlock
                {
                    Text =
                        string.IsNullOrWhiteSpace(
                            candidate.Album)
                            ? candidate.Artist
                            : $"{candidate.Artist} · {candidate.Album}",
                    FontSize = 12,
                    Foreground =
                        Application.Current
                            .Resources[
                                "TextFillColorSecondaryBrush"]
                            as Microsoft.UI.Xaml.Media
                                .Brush,
                    TextTrimming =
                        TextTrimming
                            .CharacterEllipsis,
                });
            Grid.SetColumn(
                text,
                1);
            row.Children.Add(text);

            StackPanel metadata = new()
            {
                Spacing = 2,
                HorizontalAlignment =
                    HorizontalAlignment.Right,
                VerticalAlignment =
                    VerticalAlignment.Center,
            };
            metadata.Children.Add(
                new TextBlock
                {
                    Text =
                        $"#{candidate.TrackId}",
                    FontSize = 11,
                    Foreground =
                        Application.Current
                            .Resources[
                                "TextFillColorSecondaryBrush"]
                            as Microsoft.UI.Xaml.Media
                                .Brush,
                    HorizontalAlignment =
                        HorizontalAlignment.Right,
                });
            metadata.Children.Add(
                new HyperlinkButton
                {
                    Content =
                        L(
                            "NetEase",
                            "网易云"),
                    Padding =
                        new Thickness(0),
                    HorizontalAlignment =
                        HorizontalAlignment.Right,
                    NavigateUri =
                        new Uri(
                            $"https://music.163.com/#/song?id={candidate.TrackId}"),
                });
            Grid.SetColumn(
                metadata,
                2);
            row.Children.Add(metadata);

            return new ListViewItem
            {
                Tag = candidate,
                Content = row,
                Padding =
                    new Thickness(0),
                Margin =
                    new Thickness(
                        0,
                        1,
                        0,
                        1),
                HorizontalContentAlignment =
                    HorizontalAlignment.Stretch,
            };
        }

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
            dialog.IsPrimaryButtonEnabled =
                false;
            results.Items.Clear();

            try
            {
                IReadOnlyList<
                    NeteaseMatchCandidate>
                    candidates =
                        await request;

                foreach (NeteaseMatchCandidate
                    candidate in candidates)
                {
                    results.Items.Add(
                        CreateCandidateRow(
                            candidate));
                }

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
                NeteaseMatchCandidate?
                    candidate =
                        GetSelectedCandidate();

                if (candidate is null)
                {
                    dialog.IsPrimaryButtonEnabled =
                        false;
                    return;
                }

                dialog.IsPrimaryButtonEnabled =
                    true;
                lyricsPreview.ItemsSource =
                    new[]
                    {
                        new LyricDisplayLine(
                            L(
                                "Loading lyrics…",
                                "正在加载歌词……"),
                            null,
                            LyricsDisplayModes
                                .OriginalOnly),
                    };

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
                            LyricsDisplayModes
                                .PreferOriginal);

                    lyricsPreview.ItemsSource =
                        lines.Length == 0
                            ? new[]
                            {
                                new LyricDisplayLine(
                                    L(
                                        "No lyrics for this result.",
                                        "这个结果没有歌词。"),
                                    null,
                                    LyricsDisplayModes
                                        .OriginalOnly),
                            }
                            : lines;
                }
                catch (Exception exception)
                {
                    lyricsPreview.ItemsSource =
                        new[]
                        {
                            new LyricDisplayLine(
                                UserFacingErrors
                                    .Describe(
                                        exception),
                                null,
                                LyricsDisplayModes
                                    .OriginalOnly),
                        };
                }
            };

        ContentDialogResult result =
            await dialog.ShowAsync();

        NeteaseMatchCandidate?
            selected =
                GetSelectedCandidate();

        if (result
                == ContentDialogResult.Primary
            && selected is not null)
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

    private async void ContextOpenSourceItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem()
            is not LibraryDisplayItem item)
        {
            return;
        }

        try
        {
            await OpenInExplorerAsync(
                item.Entry.SourcePath);
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not open the source folder.",
                    "无法打开源文件位置。"),
                exception);
        }
    }

    private async void ContextOpenDestinationItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem()
                ?.Entry.DestinationPath
            is not string path)
        {
            return;
        }

        try
        {
            await OpenInExplorerAsync(
                path);
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not open the destination folder.",
                    "无法打开目标文件位置。"),
                exception);
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

    private static async Task OpenInExplorerAsync(
        string path)
    {
        string directory =
            File.Exists(path)
                ? Path.GetDirectoryName(path)
                    ?? path
                : Directory.Exists(path)
                    ? path
                    : Path.GetDirectoryName(path)
                        ?? path;

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                directory);
        }

        bool launched =
            await Windows.System.Launcher
                .LaunchFolderPathAsync(
                    directory);

        if (!launched)
        {
            throw new InvalidOperationException(
                $"Windows could not open '{directory}'.");
        }
    }

    private PlaylistDisplayItem[]
        BuildPlaylists(
            MusicLoomSettings settings)
    {
        Dictionary<string, LibraryDisplayItem>
            trackLookup =
                _allLibraryItems.ToDictionary(
                    static item =>
                        Path.GetFullPath(
                            item.Entry.SourcePath),
                    StringComparer
                        .OrdinalIgnoreCase);

        HashSet<LibraryDisplayItem>
            categorized = [];
        List<PlaylistDisplayItem> result = [];

        string root =
            settings.Path
                .SourcePlaylistDirectory;

        if (!string.IsNullOrWhiteSpace(root)
            && Directory.Exists(root))
        {
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
                            out LibraryDisplayItem?
                                displayItem))
                    {
                        tracks.Add(
                            displayItem);
                        categorized.Add(
                            displayItem);
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
                    PlaylistMapper
                        .NormalizeDestinationBase(
                            settings.Path
                                .DestinationPlaylistBase);

                string[] expectedLines =
                    tracks
                        .Where(static displayItem =>
                            displayItem.Entry
                                .DestinationPath
                            is not null)
                        .Select(displayItem =>
                        {
                            string relative =
                                Path.GetRelativePath(
                                    settings.Path
                                        .DestinationDirectory,
                                    displayItem.Entry
                                        .DestinationPath!)
                                    .Replace(
                                        '\\',
                                        '/');

                            return
                                string.IsNullOrEmpty(
                                    playlistBase)
                                    ? relative
                                    : $"{playlistBase}/{relative}";
                        })
                        .ToArray();

                string expected =
                    string.Join(
                        "\n",
                        expectedLines);

                bool isReady =
                    File.Exists(
                        destinationPath)
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
        }

        LibraryDisplayItem[] uncategorized =
            _allLibraryItems
                .Where(item =>
                    !categorized.Contains(item))
                .OrderBy(
                    static item =>
                        item.Artist,
                    StringComparer
                        .CurrentCultureIgnoreCase)
                .ThenBy(
                    static item =>
                        item.Title,
                    StringComparer
                        .CurrentCultureIgnoreCase)
                .ToArray();

        result.Insert(
            0,
            new PlaylistDisplayItem(
                L(
                    "Uncategorized",
                    "未分类"),
                string.Empty,
                string.Empty,
                uncategorized,
                IsReady: true,
                IsVirtual: true));

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
            ConfigureAuxiliaryLongText();
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
            playlist.IsVirtual
                ? L(
                    $"{playlist.TrackCount:N0} tracks",
                    $"{playlist.TrackCount:N0} 首歌曲")
                : playlist.IsReady
                    ? L(
                        $"{playlist.TrackCount:N0} tracks · Ready",
                        $"{playlist.TrackCount:N0} 首歌曲 · 已就绪")
                    : L(
                        $"{playlist.TrackCount:N0} tracks · Pending sync",
                        $"{playlist.TrackCount:N0} 首歌曲 · 待同步");

        ConfigureAuxiliaryLongText();
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

    private void PlaylistSortDirectionBox_SelectionChanged(
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
            ?? "artist";

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

        bool descending =
            string.Equals(
                (PlaylistSortDirectionBox.SelectedItem
                    as ComboBoxItem)
                    ?.Tag?.ToString(),
                "desc",
                StringComparison.Ordinal);

        if (descending)
        {
            items = items.Reverse();
        }

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

    private async void OpenSourceButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await TryOpenConfiguredDirectoryAsync(
            SourcePathBox.Text);

    private async void OpenDestinationSettingButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await TryOpenConfiguredDirectoryAsync(
            DestinationPathBox.Text);

    private async void OpenPlaylistButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await TryOpenConfiguredDirectoryAsync(
            PlaylistSourcePathBox.Text);

    private async Task
        TryOpenConfiguredDirectoryAsync(
            string path)
    {
        if (string.IsNullOrWhiteSpace(
                path))
        {
            return;
        }

        try
        {
            string full =
                Path.GetFullPath(path);

            if (!Directory.Exists(full))
            {
                return;
            }

            bool launched =
                await Windows.System.Launcher
                    .LaunchFolderPathAsync(
                        full);

            if (!launched)
            {
                throw new InvalidOperationException(
                    $"Windows could not open '{full}'.");
            }
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not open the configured folder.",
                    "无法打开已配置的目录。"),
                exception);
        }
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

            await LibraryDatabase.SaveSettingsAsync(
                DatabasePath,
                settings,
                cancellationToken);
            RefreshDatabaseSize();

            _settings = settings;
            _uiLanguage =
                NormalizeLanguage(
                    settings.Ui.Language);
            _accentColor =
                settings.Ui.AccentColor;

            ApplyAppearance(settings);
            ApplyLanguage();

            foreach (LibraryDisplayItem item
                in _allLibraryItems)
            {
                item.ApplyLongTextMode(
                    CurrentLongTextMode);
            }

            if (_selectedLibraryItem is not null)
            {
                ConfigureTitleAutoScroll();
            }

            ConfigureAuxiliaryLongText();

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
        double refreshSeconds =
            double.IsFinite(
                LyricsRefreshHoursBox.Value)
            && LyricsRefreshHoursBox.Value
                > 0
                ? LyricsRefreshHoursBox.Value
                : 86_400;

        double refreshHours =
            refreshSeconds / 3600.0;

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

        string longTextMode =
            (LongTextModeBox.SelectedItem
                as ComboBoxItem)
                ?.Tag?.ToString()
            ?? LongTextModes.AutoScroll;

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
                    LongTextModes.Normalize(
                        longTextMode)
                    == LongTextModes.AutoScroll,
                LongTextMode =
                    LongTextModes.Normalize(
                        longTextMode),
                LyricsDisplayMode =
                    LyricsDisplayModes.Normalize(
                        _lyricsDisplayMode),
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
                            * 3600.0
                        : 86_400;

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

            string longTextMode =
                LongTextModes.Normalize(
                    settings.Ui.LongTextMode,
                    settings.Ui.AutoScrollLongTitles);

            LongTextModeBox.SelectedIndex =
                longTextMode switch
                {
                    LongTextModes.Hover => 1,
                    LongTextModes.Wrap => 2,
                    _ => 0,
                };

            _lyricsDisplayMode =
                LyricsDisplayModes.Normalize(
                    settings.Ui.LyricsDisplayMode);

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

    private void RefreshDatabaseSize()
    {
        DatabaseSizeText.Text =
            FormatBytes(
                LibraryDatabase
                    .GetStorageSizeBytes(
                        DatabasePath));
        DatabasePathText.Text =
            DatabasePath;
    }

    private async void ClearDatabaseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        ContentDialog dialog = new()
        {
            XamlRoot =
                ((FrameworkElement)Content)
                    .XamlRoot,
            Title =
                L(
                    "Clear the entire local database?",
                    "完全清空本地数据库？"),
            Content =
                L(
                    "This deletes the cached library, synchronization state, NetEase matches and saved settings. Your source and destination music files are not deleted.",
                    "这会删除音乐库缓存、同步状态、网易云匹配和已保存设置；不会删除源音乐或目标音乐文件。"),
            PrimaryButtonText =
                L(
                    "Clear everything",
                    "全部清空"),
            CloseButtonText =
                L("Cancel", "取消"),
            DefaultButton =
                ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync()
            != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            _settingsSaveCancellation
                ?.Cancel();

            Microsoft.Data.Sqlite
                .SqliteConnection
                .ClearAllPools();

            await LibraryDatabase.ClearAsync(
                DatabasePath);

            TryDeleteLegacyFile(
                LegacySnapshotPath);
            TryDeleteLegacyFile(
                LegacyConfigPath);
            TryDeleteLegacyFile(
                LegacyStatePath);
            TryDeleteLegacyFile(
                LegacyStatePath + ".bak");

            _settings =
                new MusicLoomSettings();
            _state =
                new StateDocument();
            _stateStore =
                new SqliteStateStore(
                    DatabasePath,
                    LegacyStatePath);
            _plan = null;
            _trackSyncStates.Clear();
            _allLibraryItems.Clear();
            _playlists.Clear();

            PopulateSettingsForm(
                _settings);
            ApplyAppearance(
                _settings);
            ApplyLanguage();

            LibraryList.ItemsSource = null;
            PlaylistsList.ItemsSource = null;
            PlaylistTracksList.ItemsSource = null;
            SyncButton.IsEnabled = false;
            HideDetailPane();
            ResetTrackDetails();
            UpdateLibraryEmptyState();
            RefreshDatabaseSize();

            ShowInfo(
                InfoBarSeverity.Success,
                L(
                    "Database cleared",
                    "数据库已清空"),
                L(
                    "MusicLoom local state has been reset. Configure the library again to continue.",
                    "MusicLoom 的本地状态已重置，请重新配置音乐库后继续。"));
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not clear the local database.",
                    "无法清空本地数据库。"),
                exception);
        }
    }

    private void RefreshDiagnosticsConsole()
    {
        try
        {
            string text =
                File.Exists(
                    AppDiagnostics.LogPath)
                    ? File.ReadAllText(
                        AppDiagnostics.LogPath)
                    : string.Empty;

            const int MaxCharacters =
                120_000;

            if (text.Length
                > MaxCharacters)
            {
                text =
                    text[^MaxCharacters..];

                int newline =
                    text.IndexOf(
                        Environment.NewLine,
                        StringComparison.Ordinal);
                if (newline >= 0)
                {
                    text =
                        text[
                            (newline
                                + Environment
                                    .NewLine.Length)..];
                }
            }

            DiagnosticsConsole.Text =
                text;
            DiagnosticsConsole.SelectionStart =
                DiagnosticsConsole.Text.Length;
        }
        catch (Exception exception)
        {
            DiagnosticsConsole.Text =
                UserFacingErrors.Describe(
                    exception);
        }
    }

    private void AppDiagnostics_LineWritten(
        string line)
    {
        DispatcherQueue.TryEnqueue(
            () =>
                AppendDiagnosticLine(
                    line));
    }

    private void AppendDiagnosticLine(
        string line)
    {
        const int MaxCharacters =
            120_000;

        string current =
            DiagnosticsConsole.Text;

        string next =
            string.IsNullOrEmpty(current)
                ? line
                : current
                    + Environment.NewLine
                    + line;

        if (next.Length
            > MaxCharacters)
        {
            next =
                next[^MaxCharacters..];

            int newline =
                next.IndexOf(
                    Environment.NewLine,
                    StringComparison.Ordinal);
            if (newline >= 0)
            {
                next =
                    next[
                        (newline
                            + Environment
                                .NewLine.Length)..];
            }
        }

        DiagnosticsConsole.Text =
            next;
        DiagnosticsConsole.SelectionStart =
            next.Length;
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
                    "Could not open the log folder.",
                    "无法打开日志文件夹。"),
                exception);
        }
    }

    private void OpenLogFileButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            AppDiagnostics.OpenLogFile();
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not open the log file.",
                    "无法打开日志文件。"),
                exception);
        }
    }

    private void ClearLogButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        AppDiagnostics.ClearLog();
        DiagnosticsConsole.Text =
            string.Empty;

        ShowInfo(
            InfoBarSeverity.Success,
            L(
                "Log cleared",
                "日志已清空"),
            L(
                "The current diagnostic log was cleared.",
                "当前诊断日志已清空。"));
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
        PlaylistCancelButton.IsEnabled =
            busy;

        // Browsing/searching remains usable while a background sync runs.
        LibrarySearchBox.IsEnabled = true;
        LibraryFilterBox.IsEnabled = true;
        LibrarySortBox.IsEnabled = true;
        LibrarySortDirectionBox.IsEnabled = true;
        LibraryList.IsEnabled = true;
        PlaylistSearchBox.IsEnabled = true;
        PlaylistSortBox.IsEnabled = true;
        PlaylistSortDirectionBox.IsEnabled = true;
        PlaylistTracksList.IsEnabled = true;

        OpenMatchDialogButton.IsEnabled =
            !busy
            && _selectedLibraryItem
                is not null;

        LibraryProgressPanel.Visibility =
            busy
                ? Visibility.Visible
                : Visibility.Collapsed;
        PlaylistProgressPanel.Visibility =
            busy
                ? Visibility.Visible
                : Visibility.Collapsed;

        OperationProgressBar.Visibility =
            busy
                ? Visibility.Visible
                : Visibility.Collapsed;
        PlaylistOperationProgressBar.Visibility =
            busy
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (busy)
        {
            OperationProgressBar.IsIndeterminate =
                true;
            PlaylistOperationProgressBar.IsIndeterminate =
                true;
        }
        else
        {
            OperationProgressBar.IsIndeterminate =
                false;
            OperationProgressBar.Value = 0;
            PlaylistOperationProgressBar.IsIndeterminate =
                false;
            PlaylistOperationProgressBar.Value = 0;
        }

        SetOperationStatus(
            operationStatus);
    }

    private void SetOperationStatus(
        string status)
    {
        OperationStatusText.Text =
            status;
        PlaylistOperationStatusText.Text =
            status;
    }

    private void SetIndeterminateProgress()
    {
        LibraryProgressPanel.Visibility =
            Visibility.Visible;
        PlaylistProgressPanel.Visibility =
            Visibility.Visible;

        OperationProgressBar.Visibility =
            Visibility.Visible;
        PlaylistOperationProgressBar.Visibility =
            Visibility.Visible;

        OperationProgressBar.IsIndeterminate =
            true;
        PlaylistOperationProgressBar.IsIndeterminate =
            true;
    }

    private void SetProgress(
        int processed,
        int total)
    {
        LibraryProgressPanel.Visibility =
            Visibility.Visible;
        PlaylistProgressPanel.Visibility =
            Visibility.Visible;

        OperationProgressBar.Visibility =
            Visibility.Visible;
        PlaylistOperationProgressBar.Visibility =
            Visibility.Visible;

        if (total <= 0)
        {
            OperationProgressBar.IsIndeterminate =
                true;
            PlaylistOperationProgressBar.IsIndeterminate =
                true;
            return;
        }

        double value =
            Math.Clamp(
                processed * 100.0 / total,
                0,
                100);

        OperationProgressBar.IsIndeterminate =
            false;
        OperationProgressBar.Value =
            value;

        PlaylistOperationProgressBar.IsIndeterminate =
            false;
        PlaylistOperationProgressBar.Value =
            value;
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
        AppDiagnostics.Log(
            $"{category} · {message}");
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
            "syncing" =>
                L(
                    "Syncing",
                    "同步中"),
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
            "delete-cover" =>
                L(
                    "Removing stale cover",
                    "清理错误封面"),
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
        PlaylistCancelButtonText.Text =
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
        LibrarySortAscendingItem.Content =
            L("Ascending", "顺序");
        LibrarySortDescendingItem.Content =
            L("Descending", "倒序");

        ToolTipService.SetToolTip(
            CloseDetailButton,
            L(
                "Close details",
                "关闭详情"));
        ToolTipService.SetToolTip(
            CoverButton,
            L(
                "Open artwork",
                "查看封面"));
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
        SyncTab.Header =
            L("Sync", "同步");
        TrackSyncLogHeadingText.Text =
            L(
                "Latest sync log",
                "最新同步日志");
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
        DurationHeadingText.Text =
            L("Duration", "时长");
        BitrateHeadingText.Text =
            L("Bitrate", "比特率");
        SampleRateHeadingText.Text =
            L("Sample rate", "采样率");
        BitDepthHeadingText.Text =
            L("Bit depth", "位深");
        ChannelsHeadingText.Text =
            L("Channels", "声道");
        BitrateModeHeadingText.Text =
            L("Encoding", "编码模式");
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
        DatabaseSettingTitle.Text =
            L(
                "LOCAL DATABASE",
                "本地数据库");
        ClearDatabaseButton.Content =
            L(
                "Clear entire database",
                "完全清空数据库");
        DatabasePathText.Text =
            DatabasePath;

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
                "Lyrics cache lifetime (seconds)",
                "歌词缓存有效期（秒）");
        LyricsCacheSettingDescription.Text =
            L(
                "How many seconds a result stays fresh before MusicLoom checks again.",
                "结果在 MusicLoom 再次检查前保持有效的秒数。");

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
        LongTextSettingTitle.Text =
            L(
                "Long text display",
                "过长文本显示");
        LongTextAutoScrollItem.Content =
            L(
                "Auto-scroll",
                "自动滚动");
        LongTextHoverItem.Content =
            L(
                "Show on hover",
                "悬停显示");
        LongTextWrapItem.Content =
            L(
                "Wrap",
                "换行显示");

        LyricsOriginalOnlyItem.Content =
            L(
                "Original only",
                "仅原文");
        LyricsPreferTranslationItem.Content =
            L(
                "Prefer translation",
                "优先翻译");
        LyricsPreferOriginalItem.Content =
            L(
                "Prefer original",
                "优先原文");
        CopyAllLyricsButtonText.Text =
            L(
                "Copy all",
                "复制完整歌词");
        CopyAllLyricsTextItem.Text =
            L(
                "Copy as text",
                "复制为文本");
        CopyAllLyricsLrcItem.Text =
            L(
                "Copy as LRC",
                "复制为 LRC");
        ToolTipService.SetToolTip(
            CopyAllLyricsButton,
            L(
                "Copy full lyrics",
                "复制完整歌词"));

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
        PlaylistSortAscendingItem.Content =
            L("Ascending", "顺序");
        PlaylistSortDescendingItem.Content =
            L("Descending", "倒序");
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

        LogsSettingsHeadingText.Text =
            L(
                "Logs",
                "日志");
        DiagnosticsLogSettingTitle.Text =
            L(
                "Diagnostic log",
                "诊断日志");
        DiagnosticsConsoleTitle.Text =
            L(
                "Live diagnostic console",
                "实时诊断控制台");
        OpenLogFileButton.Content =
            L(
                "Open log",
                "打开日志");
        OpenLogFolderButton.Content =
            L(
                "Open folder",
                "打开目录");
        ClearLogButton.Content =
            L(
                "Clear",
                "清空");
        DiagnosticsLogPathText.Text =
            AppDiagnostics.LogPath;

        RenderLyrics();
        RefreshTrackSyncTab(
            _selectedLibraryItem);
        ConfigureAuxiliaryLongText();
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

    private static string FormatDuration(
        double milliseconds)
    {
        if (!double.IsFinite(milliseconds)
            || milliseconds <= 0)
        {
            return "—";
        }

        TimeSpan duration =
            TimeSpan.FromMilliseconds(
                milliseconds);

        return duration.TotalHours >= 1
            ? duration.ToString(
                @"h\:mm\:ss",
                CultureInfo.InvariantCulture)
            : duration.ToString(
                @"m\:ss",
                CultureInfo.InvariantCulture);
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

    private sealed record TrackSyncViewState(
        string Status,
        string Detail,
        DateTimeOffset UpdatedAt,
        bool IsError);

    private sealed record TrackDetailPayload(
        byte[]? Artwork,
        string? Lyrics,
        string LyricsSource,
        string SourcePath,
        string? DestinationPath,
        string Format,
        string Duration,
        string Bitrate,
        string SampleRate,
        string BitDepth,
        string Channels,
        string BitrateMode,
        string SourceSize,
        string SourceModified,
        string SourceCreated,
        string DestinationSize,
        string DestinationModified);
}
