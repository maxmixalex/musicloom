using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MusicLoom.App;

/// <summary>
/// Shared provider detail tab (QQ and KuGou). Its actions never modify
/// NetEase matching information; they adopt provider lyrics into SQLite.
/// </summary>
public sealed partial class CloudLyricsMatchTab : UserControl
{
    private string _provider = "";
    private string _missing = "";
    private string _selectedText = "";
    private string _helpText = "";
    private bool _hasLyrics;

    public event EventHandler? SearchRequested;

    public CloudLyricsMatchTab() => InitializeComponent();

    public void SetProvider(
        string provider, string search, string missing,
        string selectedText, string helpText)
    {
        _provider = provider;
        _missing = missing;
        _selectedText = selectedText;
        _helpText = helpText;
        SearchText.Text = search;
        ProviderHelp.Text = _helpText;
        UpdateStatus();
    }

    public void SetTrack(string title, string artist, string album,
        ImageSource? artwork, bool hasProviderLyrics)
    {
        TrackTitle.Text = title;
        TrackArtist.Text = artist;
        TrackAlbum.Text = album;
        LocalArtwork.Source = artwork;
        _hasLyrics = hasProviderLyrics;
        UpdateStatus();
        SearchButton.IsEnabled = !string.IsNullOrWhiteSpace(title);
    }

    private void UpdateStatus()
    {
        ProviderStatus.Text = _hasLyrics ? _selectedText : _missing;
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) =>
        SearchRequested?.Invoke(this, EventArgs.Empty);
}
