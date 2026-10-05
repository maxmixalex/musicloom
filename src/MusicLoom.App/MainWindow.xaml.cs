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
    private readonly List<PlanDisplayItem> _allPlanItems = [];
    private readonly List<string> _activityItems = [];

    private MusicLoomSettings? _settings;
    private StateDocument? _state;
    private StateStore? _stateStore;
    private LibraryPlan? _plan;
    private CancellationTokenSource? _syncCancellation;
    private bool _busy;
    private bool _initializing = true;

    public MainWindow()
    {
        InitializeComponent();

        ConfigPathBox.Text =
            Path.Combine(
                AppContext.BaseDirectory,
                "config.json");
        StatePathBox.Text =
            Path.Combine(
                AppDiagnostics.DataDirectory,
                "state.json");

        PlanFilterBox.SelectedIndex = 0;
        LogPathText.Text = AppDiagnostics.LogPath;

        Navigation.SelectedItem =
            Navigation.MenuItems[0];

        AddActivity(
            "Ready",
            "MusicLoom started. Scan is read-only until Apply sync is explicitly confirmed.");

        _initializing = false;
        InvalidatePlan();
    }

    private async void ScanButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await ScanLibraryAsync(
            clearInfoBar: true,
            recordActivity: true);
    }

    private async void BrowseConfigButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            FileOpenPicker picker = new()
            {
                SuggestedStartLocation =
                    PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".json");

            nint hwnd =
                WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                hwnd);

            Windows.Storage.StorageFile? file =
                await picker.PickSingleFileAsync();
            if (file is not null)
            {
                ConfigPathBox.Text = file.Path;
            }
        }
        catch (Exception exception)
        {
            HandleUiException(
                "Could not open the configuration picker.",
                exception);
        }
    }

    private async void BrowseStateFolderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            FolderPicker picker = new()
            {
                SuggestedStartLocation =
                    PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add("*");

            nint hwnd =
                WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                hwnd);

            Windows.Storage.StorageFolder? folder =
                await picker.PickSingleFolderAsync();
            if (folder is not null
                && !string.IsNullOrWhiteSpace(folder.Path))
            {
                StatePathBox.Text =
                    Path.Combine(
                        folder.Path,
                        "state.json");
            }
        }
        catch (Exception exception)
        {
            HandleUiException(
                "Could not open the state-folder picker.",
                exception);
        }
    }

    private void SetupPath_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_initializing || _busy)
        {
            return;
        }

        InvalidatePlan();
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
            "destination library");
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
                "Could not open the diagnostic log folder.",
                exception);
        }
    }

    private void ClearActivityButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _activityItems.Clear();
        RefreshActivityList();
        AddActivity(
            "Activity",
            "Activity view cleared. Diagnostic logs on disk were not deleted.");
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
        // Always rebuild immediately before confirmation. This prevents a
        // stale plan from being applied after config/source changes.
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
                "Sync blocked",
                "Resolve scan errors before applying changes. Open Plan and filter by Errors.");
            NavigateTo("plan");
            return;
        }

        if (plan.PendingCount == 0)
        {
            ShowInfo(
                InfoBarSeverity.Success,
                "Already up to date",
                "There are no pending changes to apply.");
            return;
        }

        string networkText =
            settings.Organizer.Lyrics.Enabled
                ? "Lyrics enrichment is enabled. Netease is contacted only for tracks that still need writable lyrics."
                : "Lyrics enrichment is disabled. This sync makes no Netease requests.";

        ContentDialog confirmation = new()
        {
            XamlRoot =
                ((FrameworkElement)Content).XamlRoot,
            Title = "Apply this sync plan?",
            PrimaryButtonText = "Apply sync",
            CloseButtonText = "Cancel",
            DefaultButton =
                ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text =
                    $"{plan.PendingTrackCount:N0} tracks have pending work.\n" +
                    $"{plan.AudioCopyCount:N0} audio files will be copied or rematerialized.\n" +
                    $"{plan.EnrichmentOnlyCount:N0} tracks need enrichment/state work only.\n" +
                    $"{plan.CoverWriteCount:N0} cover files need updating.\n" +
                    $"{plan.PlaylistWriteCount:N0} playlists need updating.\n\n" +
                    $"Destination: {settings.Path.DestinationDirectory}\n" +
                    $"State: {stateStore.Path}\n\n" +
                    networkText +
                    "\n\nThe source library is never modified.",
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
            "Materializing local files, covers, and playlists…";

        AddActivity(
            "Sync started",
            $"{plan.PendingCount:N0} pending changes; {plan.AudioCopyCount:N0} audio copies.");
        AppDiagnostics.Log(
            $"Sync started: pending={plan.PendingCount}, audio={plan.AudioCopyCount}, " +
            $"covers={plan.CoverWriteCount}, playlists={plan.PlaylistWriteCount}.");

        try
        {
            using NeteaseClient client = new();
            CompositeFingerprintPcmDecoder
                decoder = new();
            NeteaseTrackProvider provider =
                new(client, decoder);
            TagLibSharpLyricsWriter
                lyricsWriter = new();

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
                $"{summary.Local.CopiedTracks:N0} audio copies · " +
                $"{summary.Local.CopiedCovers:N0} covers · " +
                $"{summary.Local.WrittenPlaylists:N0} playlists · " +
                $"{summary.WrittenLyrics:N0} lyrics written · " +
                $"{summary.ExistingLyrics:N0} existing lyrics · " +
                $"{summary.NoMatch:N0} no match · " +
                $"{summary.NoLyrics:N0} no lyrics · " +
                $"{summary.UnsupportedLyrics:N0} unsupported · " +
                $"{summary.Errors.Count:N0} errors";

            LastRunTitle.Text =
                summary.Errors.Count == 0
                    ? "Last sync completed"
                    : "Last sync completed with retryable errors";
            LastRunSummaryText.Text = message;

            AddActivity(
                summary.Errors.Count == 0
                    ? "Sync complete"
                    : "Sync warning",
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
                    "Retryable error",
                    readable);
                AppDiagnostics.Log(
                    $"Track error [{error.Stage}] {error.SourcePath}: {error.Message}");
            }

            ShowInfo(
                summary.Errors.Count == 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning,
                summary.Errors.Count == 0
                    ? "Sync complete"
                    : "Sync completed with retryable errors",
                summary.Errors.Count == 0
                    ? message
                    : message + " Open Activity for the failed items; they remain pending for retry.");
        }
        catch (OperationCanceledException)
        {
            const string message =
                "Completed operations were checkpointed. A new scan will determine what still needs work.";

            LastRunTitle.Text =
                "Last sync cancelled";
            LastRunSummaryText.Text =
                message;
            AddActivity(
                "Sync cancelled",
                message);
            AppDiagnostics.Log(
                "Sync cancelled by user.");

            ShowInfo(
                InfoBarSeverity.Warning,
                "Sync cancelled",
                message);
        }
        catch (Exception exception)
        {
            string message =
                UserFacingErrors.Describe(
                    exception);

            LastRunTitle.Text =
                "Last sync failed";
            LastRunSummaryText.Text =
                message;
            AddActivity(
                "Sync failed",
                message);
            AppDiagnostics.LogException(
                "Sync failed.",
                exception);

            ShowInfo(
                InfoBarSeverity.Error,
                "Sync failed",
                message + " See Activity or the diagnostic log for details.");
        }
        finally
        {
            _syncCancellation = null;
            SetBusy(
                busy: false,
                syncing: false);
        }

        // Rebuild from persisted state after any live run.
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
            "Cancelling after the current operation…";
        cancellation.Cancel();
    }

    private async Task<bool> ScanLibraryAsync(
        bool clearInfoBar,
        bool recordActivity)
    {
        string configPath =
            ConfigPathBox.Text.Trim();
        string statePath =
            StatePathBox.Text.Trim();

        SetBusy(
            busy: true,
            syncing: false);
        SetIndeterminateProgress();

        if (clearInfoBar)
        {
            SyncInfoBar.IsOpen = false;
        }

        ScanStatusText.Text =
            "Loading configuration…";

        try
        {
            if (string.IsNullOrWhiteSpace(
                    configPath))
            {
                throw new InvalidDataException(
                    "Configuration path must not be empty.");
            }

            if (string.IsNullOrWhiteSpace(
                    statePath))
            {
                throw new InvalidDataException(
                    "Incremental state path must not be empty.");
            }

            MusicLoomSettings settings =
                await SettingsLoader.LoadAsync(
                    configPath);

            LibraryPathSafety.ValidateStatePath(
                settings,
                statePath);

            StateStore stateStore =
                new(statePath);
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
                            "Scanning…";
                        return;
                    }

                    string current =
                        string.IsNullOrEmpty(
                            value.CurrentPath)
                            ? string.Empty
                            : $" · {Path.GetFileName(value.CurrentPath)}";

                    ScanStatusText.Text =
                        $"Scanning {value.Processed:N0}/{value.Total:N0}{current}";
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

            SourceLibraryValue.Text =
                settings.Path.SourceDirectory;
            DestinationLibraryValue.Text =
                settings.Path.DestinationDirectory;
            LyricsModeValue.Text =
                settings.Organizer.Lyrics.Enabled
                    ? settings.Organizer.Lyrics.OverrideExisting
                        ? "Enabled · overwrite existing lyrics"
                        : "Enabled · preserve existing lyrics"
                    : "Disabled";
            StateStatusValue.Text =
                recoveryWarning is null
                    ? $"Loaded · {state.Tracks.Count:N0} cached tracks"
                    : "Recovered from backup · review Activity";

            OpenDestinationButton.IsEnabled =
                Directory.Exists(
                    settings.Path.DestinationDirectory);

            string scanSummary =
                $"{plan.TotalCount:N0} tracks · " +
                $"{plan.PendingCount:N0} pending · " +
                $"{plan.ErrorCount:N0} scan errors";

            if (recordActivity)
            {
                AddActivity(
                    "Scan complete",
                    scanSummary);
            }

            AppDiagnostics.Log(
                "Scan complete: " + scanSummary);

            if (recoveryWarning is not null)
            {
                AddActivity(
                    "State recovered",
                    recoveryWarning);
                AppDiagnostics.Log(
                    recoveryWarning);
                ShowInfo(
                    InfoBarSeverity.Warning,
                    "State recovered from backup",
                    "The primary state file was unreadable. MusicLoom loaded the last good backup; details are in Activity.");
            }
            else if (plan.ErrorCount > 0)
            {
                ShowInfo(
                    InfoBarSeverity.Warning,
                    "Scan completed with errors",
                    $"{plan.ErrorCount:N0} tracks could not be planned. Review Plan → Errors before applying sync.");
            }
            else if (plan.PendingCount == 0)
            {
                ShowInfo(
                    InfoBarSeverity.Success,
                    "Library is up to date",
                    "No audio, enrichment, cover, or playlist changes are pending.");
            }

            return true;
        }
        catch (Exception exception)
        {
            _settings = null;
            _state = null;
            _stateStore = null;
            _plan = null;
            _allPlanItems.Clear();

            TracksValue.Text = "—";
            PendingValue.Text = "—";
            PendingBreakdownText.Text =
                "Scan failed";
            ErrorsValue.Text = "1";
            SourceLibraryValue.Text =
                "Not loaded";
            DestinationLibraryValue.Text =
                "Not loaded";
            LyricsModeValue.Text =
                "Not loaded";
            StateStatusValue.Text =
                "Not loaded";
            OpenDestinationButton.IsEnabled =
                false;
            ReviewPlanButton.IsEnabled =
                false;

            string message =
                UserFacingErrors.Describe(
                    exception);
            ScanStatusText.Text =
                "Scan failed: " + message;

            AddActivity(
                "Scan failed",
                message);
            AppDiagnostics.LogException(
                "Scan failed.",
                exception);

            _allPlanItems.Add(
                new PlanDisplayItem(
                    "error",
                    "Scan failed · " + message));
            ApplyPlanFilter();

            ShowInfo(
                InfoBarSeverity.Error,
                "Scan failed",
                message + " Full technical details were written to the diagnostic log.");

            return false;
        }
        finally
        {
            SetBusy(
                busy: false,
                syncing: false);
        }
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
            $"{plan.AudioCopyCount:N0} audio · " +
            $"{plan.EnrichmentOnlyCount:N0} enrich · " +
            $"{plan.CoverWriteCount:N0} covers · " +
            $"{plan.PlaylistWriteCount:N0} playlists";
        ErrorsValue.Text =
            plan.ErrorCount.ToString("N0");
        LastScanValue.Text =
            DateTime.Now.ToString("t");

        ScanStatusText.Text =
            $"{plan.UnchangedCount:N0} unchanged · " +
            $"{plan.PendingTrackCount:N0} track changes · " +
            $"{plan.CoverWriteCount:N0} covers · " +
            $"{plan.PlaylistWriteCount:N0} playlists · " +
            $"{plan.ErrorCount:N0} errors";

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
                string.IsNullOrWhiteSpace(
                    entry.Detail)
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
                    "copy" => "COPY",
                    "enrich" => "ENRICH",
                    _ => "ERROR",
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
                    $"COVER    {source}  →  {destination}"));
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
                    $"PLAYLIST {source}  →  {destination}"));
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
                    ? "No pending changes."
                    : "No plan items match this filter.");
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
                    $"… {items.Length - 1000:N0} more matching items");
            }
        }

        PlanVisibleCountText.Text =
            $"{items.Length:N0} of {_allPlanItems.Count:N0}";
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
    }

    private void SetBusy(
        bool busy,
        bool syncing)
    {
        _busy = busy;

        ScanButton.IsEnabled = !busy;
        ConfigPathBox.IsEnabled = !busy;
        StatePathBox.IsEnabled = !busy;
        BrowseConfigButton.IsEnabled = !busy;
        BrowseStateFolderButton.IsEnabled = !busy;
        ReviewPlanButton.IsEnabled =
            !busy && _allPlanItems.Count > 0;
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
        _settings = null;
        _state = null;
        _stateStore = null;
        _plan = null;
        _allPlanItems.Clear();

        ReviewPlanButton.IsEnabled = false;
        ApplySyncButton.IsEnabled = false;
        OpenDestinationButton.IsEnabled = false;

        if (!_initializing)
        {
            ScanStatusText.Text =
                "Configuration/state path changed. Scan again before applying sync.";
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
                "No activity yet.");
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
            "UI error",
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
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException(
                    $"{description} does not exist: {path}");
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            HandleUiException(
                $"Could not open {description}.",
                exception);
        }
    }

    private static string FriendlyStage(
        string stage) =>
        stage switch
        {
            "state" => "Preparing state",
            "inspect-lyrics" => "Checking lyrics",
            "recognize" => "Recognizing track",
            "fetch-lyrics" => "Fetching lyrics",
            "write-lyrics" => "Writing lyrics",
            "copy-audio" => "Copying audio",
            "copy-cover" => "Copying cover",
            "write-playlist" => "Writing playlist",
            "complete" => "Complete",
            "failed" => "Failed",
            _ => stage,
        };

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
