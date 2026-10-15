using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MusicLoom.App;

/// <summary>
/// A single candidate row for NetEase, QQ Music and KuGou.
/// Only the data and the playback delegate differ by provider.
/// </summary>
public sealed partial class LyricsSearchCandidateCard : UserControl
{
    public static readonly DependencyProperty SongTitleProperty =
        DependencyProperty.Register(nameof(SongTitle), typeof(string),
            typeof(LyricsSearchCandidateCard), new PropertyMetadata(""));
    public static readonly DependencyProperty ArtistNameProperty =
        DependencyProperty.Register(nameof(ArtistName), typeof(string),
            typeof(LyricsSearchCandidateCard), new PropertyMetadata(""));
    public static readonly DependencyProperty AlbumNameProperty =
        DependencyProperty.Register(nameof(AlbumName), typeof(string),
            typeof(LyricsSearchCandidateCard), new PropertyMetadata(""));
    public static readonly DependencyProperty DurationTextProperty =
        DependencyProperty.Register(nameof(DurationText), typeof(string),
            typeof(LyricsSearchCandidateCard), new PropertyMetadata(""));
    public static readonly DependencyProperty ArtworkSourceProperty =
        DependencyProperty.Register(nameof(ArtworkSource), typeof(ImageSource),
            typeof(LyricsSearchCandidateCard), new PropertyMetadata(null));
    public static readonly DependencyProperty PreviewTooltipProperty =
        DependencyProperty.Register(nameof(PreviewTooltip), typeof(string),
            typeof(LyricsSearchCandidateCard),
            new PropertyMetadata("Play or pause"));

    public string SongTitle
    {
        get => (string)GetValue(SongTitleProperty);
        set => SetValue(SongTitleProperty, value);
    }
    public string ArtistName
    {
        get => (string)GetValue(ArtistNameProperty);
        set => SetValue(ArtistNameProperty, value);
    }
    public string AlbumName
    {
        get => (string)GetValue(AlbumNameProperty);
        set => SetValue(AlbumNameProperty, value);
    }
    public string DurationText
    {
        get => (string)GetValue(DurationTextProperty);
        set => SetValue(DurationTextProperty, value);
    }
    public ImageSource? ArtworkSource
    {
        get => (ImageSource?)GetValue(ArtworkSourceProperty);
        set => SetValue(ArtworkSourceProperty, value);
    }
    public string PreviewTooltip
    {
        get => (string)GetValue(PreviewTooltipProperty);
        set => SetValue(PreviewTooltipProperty, value);
    }

    public SymbolIcon PlayingIcon => PreviewSymbol;
    public event EventHandler? PreviewRequested;

    public LyricsSearchCandidateCard()
    {
        InitializeComponent();
        Loaded += (_, _) => ToolTipService.SetToolTip(
            PreviewButton, PreviewTooltip);
    }

    public void SetExternalLink(Uri uri, string label)
    {
        ExternalLinkButton.NavigateUri = uri;
        ExternalLinkButton.Content = label;
        ExternalLinkButton.Visibility = Visibility.Visible;
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e) =>
        PreviewRequested?.Invoke(this, EventArgs.Empty);
}
