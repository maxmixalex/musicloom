using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Metadata;

namespace MusicLoom.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        string configPath = ConfigPathBox.Text.Trim();
        string statePath = StatePathBox.Text.Trim();

        ScanButton.IsEnabled = false;
        ScanProgressRing.IsActive = true;
        PlanList.Items.Clear();
        ScanStatusText.Text = "Loading configuration…";

        try
        {
            MusicLoomSettings settings = await SettingsLoader.LoadAsync(configPath);
            StateDocument state = await new StateStore(statePath).LoadAsync();

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
                ScanStatusText.Text = $"Scanning {value.Processed:N0}/{value.Total:N0}{current}";
            });

            LibraryScanner scanner = new(new AtlMetadataReader());
            LibraryPlan plan = await scanner.BuildPlanAsync(settings, state, progress);

            TracksValue.Text = plan.TotalCount.ToString("N0");
            PendingValue.Text = plan.PendingCount.ToString("N0");
            ErrorsValue.Text = plan.ErrorCount.ToString("N0");
            LastScanValue.Text = DateTime.Now.ToString("t");
            ScanStatusText.Text =
                $"{plan.UnchangedCount:N0} unchanged · {plan.NewCount:N0} new · " +
                $"{plan.ModifiedCount:N0} modified · {plan.ErrorCount:N0} errors";

            int shown = 0;
            foreach (PlanEntry entry in plan.Entries)
            {
                if (entry.Status == PlanEntryStatus.Unchanged)
                {
                    continue;
                }

                if (shown >= 500)
                {
                    PlanList.Items.Add($"… {plan.PendingCount + plan.ErrorCount - shown:N0} more entries");
                    break;
                }

                string source = MakeRelative(settings.Path.SourceDirectory, entry.SourcePath);
                string destination = entry.DestinationPath is null
                    ? "—"
                    : MakeRelative(settings.Path.DestinationDirectory, entry.DestinationPath);
                string detail = string.IsNullOrWhiteSpace(entry.Detail) ? string.Empty : $" · {entry.Detail}";
                PlanList.Items.Add($"{entry.Status,-8}  {source}  →  {destination}{detail}");
                shown++;
            }

            if (PlanList.Items.Count == 0)
            {
                PlanList.Items.Add("No changes. The library is already up to date.");
            }
        }
        catch (Exception exception)
        {
            ErrorsValue.Text = "1";
            ScanStatusText.Text = $"Scan failed: {exception.Message}";
            PlanList.Items.Add(exception.ToString());
        }
        finally
        {
            ScanProgressRing.IsActive = false;
            ScanButton.IsEnabled = true;
        }
    }

    private void Navigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            SetPage("Settings", "Configuration editing will be wired to the same typed settings model used by the CLI.");
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        switch (tag)
        {
            case "overview":
                SetPage("Overview", "Preview what MusicLoom would change without writing to the music library.");
                break;
            case "library":
                SetPage("Library", "The current milestone exposes the dry-run plan here on Overview; track filtering comes next.");
                break;
            case "playlists":
                SetPage("Playlists", "MusicBee playlist conversion remains behavior-compatible and will be wired after library planning.");
                break;
            case "activity":
                SetPage("Activity", "Live scan progress is already reported by the shared planner; persistent activity history comes next.");
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
            return Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        }
        catch
        {
            return path;
        }
    }
}
