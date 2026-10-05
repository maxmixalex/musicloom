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

namespace MusicLoom.App;

public sealed partial class MainWindow : Window
{
    private MusicLoomSettings? _settings;
    private StateDocument? _state;
    private StateStore? _stateStore;
    private LibraryPlan? _plan;
    private CancellationTokenSource? _syncCancellation;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        await ScanLibraryAsync(clearInfoBar: true);
    }

    private async void ApplySyncButton_Click(object sender, RoutedEventArgs e)
    {
        // Re-scan immediately before confirmation so a stale plan can never
        // be applied after the source library or configuration changed.
        if (!await ScanLibraryAsync(clearInfoBar: true)
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
                "Resolve scan errors before applying changes.");
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

        string networkText = settings.Organizer.Lyrics.Enabled
            ? "Lyrics enrichment is enabled. Netease is contacted only for tracks that still need writable lyrics."
            : "Lyrics enrichment is disabled. This sync makes no Netease requests.";

        ContentDialog confirmation = new()
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "Apply this sync plan?",
            PrimaryButtonText = "Apply sync",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
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

        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        using CancellationTokenSource syncCancellation = new();
        _syncCancellation = syncCancellation;
        SetBusy(busy: true, syncing: true);
        SyncInfoBar.IsOpen = false;
        ScanStatusText.Text = "Materializing local files, covers, and playlists…";

        try
        {
            using NeteaseClient client = new();
            CompositeFingerprintPcmDecoder decoder = new();
            NeteaseTrackProvider provider = new(client, decoder);
            TagLibSharpLyricsWriter lyricsWriter = new();
            SyncExecutor executor = new(
                new LocalLibraryMaterializer(),
                provider,
                provider,
                lyricsWriter);

            Progress<SyncExecutionProgress> progress = new(value =>
            {
                string current = Path.GetFileName(value.SourcePath);
                ScanStatusText.Text =
                    $"{value.Stage} · {value.Processed:N0}/{value.Total:N0} · {current}";
            });

            SyncExecutionSummary summary = await executor.ExecuteAsync(
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

            ShowInfo(
                summary.Errors.Count == 0
                    ? InfoBarSeverity.Success
                    : InfoBarSeverity.Warning,
                summary.Errors.Count == 0
                    ? "Sync complete"
                    : "Sync completed with retryable errors",
                message);

            if (summary.Errors.Count > 0)
            {
                foreach (TrackSyncError error in summary.Errors.Take(100))
                {
                    PlanList.Items.Add(
                        $"ERROR [{error.Stage}] {Path.GetFileName(error.SourcePath)} · {error.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            ShowInfo(
                InfoBarSeverity.Warning,
                "Sync cancelled",
                "Completed operations remain safe. A new scan will determine what still needs work.");
        }
        catch (Exception exception)
        {
            ShowInfo(
                InfoBarSeverity.Error,
                "Sync failed",
                exception.Message);
            PlanList.Items.Add(exception.ToString());
        }
        finally
        {
            _syncCancellation = null;
            SetBusy(busy: false, syncing: false);
        }

        // Reload state and rebuild the plan from disk after any live run.
        await ScanLibraryAsync(clearInfoBar: false);
    }

    private void CancelSyncButton_Click(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource? cancellation = _syncCancellation;
        if (cancellation is null)
        {
            return;
        }

        CancelSyncButton.IsEnabled = false;
        ScanStatusText.Text = "Cancelling after the current operation…";
        cancellation.Cancel();
    }

    private async Task<bool> ScanLibraryAsync(bool clearInfoBar)
    {
        string configPath = ConfigPathBox.Text.Trim();
        string statePath = StatePathBox.Text.Trim();

        SetBusy(busy: true, syncing: false);
        PlanList.Items.Clear();
        ApplySyncButton.IsEnabled = false;

        if (clearInfoBar)
        {
            SyncInfoBar.IsOpen = false;
        }

        ScanStatusText.Text = "Loading configuration…";

        try
        {
            if (string.IsNullOrWhiteSpace(statePath))
            {
                throw new InvalidDataException(
                    "Incremental state path must not be empty.");
            }

            MusicLoomSettings settings =
                await SettingsLoader.LoadAsync(configPath);
            LibraryPathSafety.Validate(settings);

            StateStore stateStore = new(statePath);
            StateDocument state = await stateStore.LoadAsync();

            Progress<ScanProgress> progress = new(value =>
            {
                if (value.Total == 0)
                {
                    ScanStatusText.Text = "Scanning…";
                    return;
                }

                string current = string.IsNullOrEmpty(value.CurrentPath)
                    ? string.Empty
                    : $" · {Path.GetFileName(value.CurrentPath)}";
                ScanStatusText.Text =
                    $"Scanning {value.Processed:N0}/{value.Total:N0}{current}";
            });

            LibraryScanner scanner = new(new AtlMetadataReader());
            LibraryPlan plan = await scanner.BuildPlanAsync(
                settings,
                state,
                progress);

            _settings = settings;
            _state = state;
            _stateStore = stateStore;
            _plan = plan;

            RenderPlan(settings, plan);
            return true;
        }
        catch (Exception exception)
        {
            _settings = null;
            _state = null;
            _stateStore = null;
            _plan = null;

            TracksValue.Text = "—";
            PendingValue.Text = "—";
            AudioCopiesText.Text = "— audio copies";
            ErrorsValue.Text = "1";
            ScanStatusText.Text = $"Scan failed: {exception.Message}";
            PlanList.Items.Add(exception.ToString());
            ShowInfo(
                InfoBarSeverity.Error,
                "Scan failed",
                exception.Message);
            return false;
        }
        finally
        {
            SetBusy(busy: false, syncing: false);
        }
    }

    private void RenderPlan(
        MusicLoomSettings settings,
        LibraryPlan plan)
    {
        TracksValue.Text = plan.TotalCount.ToString("N0");
        PendingValue.Text = plan.PendingCount.ToString("N0");
        AudioCopiesText.Text =
            $"{plan.AudioCopyCount:N0} audio · " +
            $"{plan.CoverWriteCount:N0} covers · " +
            $"{plan.PlaylistWriteCount:N0} playlists";
        ErrorsValue.Text = plan.ErrorCount.ToString("N0");
        LastScanValue.Text = DateTime.Now.ToString("t");

        ScanStatusText.Text =
            $"{plan.UnchangedCount:N0} unchanged · " +
            $"{plan.PendingTrackCount:N0} track changes · " +
            $"{plan.CoverWriteCount:N0} covers · " +
            $"{plan.PlaylistWriteCount:N0} playlists · " +
            $"{plan.ErrorCount:N0} errors";

        int totalVisible =
            plan.Entries.Count(
                static entry => entry.Status != PlanEntryStatus.Unchanged)
            + plan.CoverWriteCount
            + plan.PlaylistWriteCount;
        int shown = 0;

        foreach (PlanEntry entry in plan.Entries)
        {
            if (entry.Status == PlanEntryStatus.Unchanged)
            {
                continue;
            }

            if (shown >= 500)
            {
                break;
            }

            string source = MakeRelative(
                settings.Path.SourceDirectory,
                entry.SourcePath);
            string destination = entry.DestinationPath is null
                ? "—"
                : MakeRelative(
                    settings.Path.DestinationDirectory,
                    entry.DestinationPath);
            string detail = string.IsNullOrWhiteSpace(entry.Detail)
                ? string.Empty
                : $" · {entry.Detail}";
            string action = entry.Status == PlanEntryStatus.Error
                ? "error"
                : entry.NeedsAudioCopy
                    ? "copy"
                    : "enrich";

            PlanList.Items.Add(
                $"{entry.Status,-8} [{action}]  {source}  →  {destination}{detail}");
            shown++;
        }

        foreach (CoverPlanEntry cover in plan.Covers)
        {
            if (!cover.NeedsWrite || shown >= 500)
            {
                continue;
            }

            string source = MakeRelative(
                settings.Path.SourceDirectory,
                cover.SourcePath);
            string destination = MakeRelative(
                settings.Path.DestinationDirectory,
                cover.DestinationPath);

            PlanList.Items.Add(
                $"Modified [cover]  {source}  →  {destination}");
            shown++;
        }

        foreach (PlaylistPlanEntry playlist in plan.Playlists)
        {
            if (!playlist.NeedsWrite || shown >= 500)
            {
                continue;
            }

            string source = MakeRelative(
                settings.Path.SourcePlaylistDirectory,
                playlist.SourcePath);
            string destination = MakeRelative(
                settings.Path.DestinationDirectory,
                playlist.DestinationPath);

            PlanList.Items.Add(
                $"Modified [playlist]  {source}  →  {destination}");
            shown++;
        }

        if (shown < totalVisible)
        {
            PlanList.Items.Add(
                $"… {totalVisible - shown:N0} more entries");
        }

        if (PlanList.Items.Count == 0)
        {
            PlanList.Items.Add(
                "No changes. The library is already up to date.");
        }

        UpdateApplyButton();
    }

    private void SetBusy(bool busy, bool syncing)
    {
        _busy = busy;
        ScanButton.IsEnabled = !busy;
        ConfigPathBox.IsEnabled = !busy;
        StatePathBox.IsEnabled = !busy;
        ScanProgressRing.IsActive = busy;

        CancelSyncButton.Visibility = syncing
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelSyncButton.IsEnabled = syncing;

        UpdateApplyButton();
    }

    private void UpdateApplyButton()
    {
        ApplySyncButton.IsEnabled =
            !_busy
            && _plan is { PendingCount: > 0, ErrorCount: 0 };
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

    private void Navigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            SetPage(
                "Settings",
                "Configuration editing will use the same typed settings model as scan and sync.");
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        switch (tag)
        {
            case "overview":
                SetPage(
                    "Overview",
                    "Scan first, review the plan, then explicitly apply it to the destination library.");
                break;
            case "library":
                SetPage(
                    "Library",
                    "The current milestone exposes the actionable plan on Overview; filtering comes next.");
                break;
            case "playlists":
                SetPage(
                    "Playlists",
                    "MusicBee playlist conversion is applied by the same local materializer as audio copies.");
                break;
            case "activity":
                SetPage(
                    "Activity",
                    "Live sync progress is reported by the transactional executor; persistent history comes next.");
                break;
        }
    }

    private void SetPage(string title, string subtitle)
    {
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
    }

    private static string MakeRelative(string root, string path)
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
}
