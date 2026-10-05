using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MusicLoom.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Navigation.SelectedItem = Navigation.MenuItems[0];
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            SetPage("Settings", "Configure library paths and behavior. Settings are not wired up yet.");
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        switch (tag)
        {
            case "overview":
                SetPage("Overview", "Keep the desktop source library and mobile target library in sync without rebuilding unchanged tracks.");
                break;
            case "library":
                SetPage("Library", "Track-level scan, recognition, metadata, and synchronization state will appear here.");
                break;
            case "playlists":
                SetPage("Playlists", "MusicBee playlist mappings and generated Salt Player playlists will appear here.");
                break;
            case "activity":
                SetPage("Activity", "Current scan, recognition, copy, and metadata jobs will appear here.");
                break;
        }
    }

    private void SetPage(string title, string subtitle)
    {
        PageTitle.Text = title;
        PageSubtitle.Text = subtitle;
    }
}
