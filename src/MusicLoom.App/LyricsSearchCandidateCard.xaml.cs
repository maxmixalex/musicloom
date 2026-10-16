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
    public static readonly DependencyProperty ExternalUrlProperty =
        DependencyProperty.Register(nameof(ExternalUrl), typeof(string),
            typeof(LyricsSearchCandidateCard),
            new PropertyMetadata(null, OnExternalChanged));
    public static readonly DependencyProperty ExternalLabelProperty =
        DependencyProperty.Register(nameof(ExternalLabel), typeof(string),
            typeof(LyricsSearchCandidateCard),
            new PropertyMetadata(null, OnExternalChanged));

    public static readonly DependencyProperty PreviewTooltipProperty =
        DependencyProperty.Register(nameof(PreviewTooltip), typeof(string),
            typeof(LyricsSearchCandidateCard),
            new PropertyMetadata("Play or pause"));
    public static readonly DependencyProperty IsPreviewPlayingProperty =
        DependencyProperty.Register(nameof(IsPreviewPlaying), typeof(bool),
            typeof(LyricsSearchCandidateCard),
            new PropertyMetadata(false, (sender, _) =>
            {
                if (sender is LyricsSearchCandidateCard card)
                    card.PlayingIcon.Symbol = card.IsPreviewPlaying
                        ? Symbol.Pause : Symbol.Play;
            }));

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

    public string? ExternalUrl
    {
        get => (string?)GetValue(ExternalUrlProperty);
        set => SetValue(ExternalUrlProperty, value);
    }
    public string? ExternalLabel
    {
        get => (string?)GetValue(ExternalLabelProperty);
        set => SetValue(ExternalLabelProperty, value);
    }

    private static void OnExternalChanged(
        DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is LyricsSearchCandidateCard card)
            card.UpdateExternalLink();
    }

    private void UpdateExternalLink()
    {
        // Recycling updates bindings without firing Loaded again. Invalidate
        // the old link before applying the new provider and candidate URL.
        if (!Uri.TryCreate(ExternalUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host is not ("y.qq.com" or "www.kugou.com"))
        {
            ExternalLinkButton.NavigateUri = null;
            ExternalLinkButton.Content = null;
            ToolTipService.SetToolTip(ExternalLinkButton, null);
            _linkText = null;
            ExternalLinkButton.Visibility = Visibility.Collapsed;
            return;
        }
        SetExternalLink(uri, ExternalLabel ?? "");
    }

    public bool IsPreviewPlaying
    {
        get => (bool)GetValue(IsPreviewPlayingProperty);
        set => SetValue(IsPreviewPlayingProperty, value);
    }

    public SymbolIcon PlayingIcon => PreviewSymbol;
    public event EventHandler? PreviewRequested;

    public LyricsSearchCandidateCard()
    {
        InitializeComponent();
        Loaded += (_, _) => ToolTipService.SetToolTip(
            PreviewButton, PreviewTooltip);
    }

    private TextBlock? _linkText;

    private bool _compactCandidateLayout;

    // The accepted 52px-artwork / title / actions hierarchy is preserved
    // on wide cards. Narrow windows stack actions UNDER the song metadata,
    // not compress the title or clip the provider link.
    private void CandidateLayout_SizeChanged(
        object sender, SizeChangedEventArgs e)
    {
        double available = e.NewSize.Width;
        if (available <= 0 || !double.IsFinite(available))
            return;

        bool compact = available < 440;
        if (_compactCandidateLayout != compact)
        {
            _compactCandidateLayout = compact;
            Grid.SetColumnSpan(CandidateInfo, compact ? 2 : 1);
            Grid.SetRow(CandidateActions, compact ? 1 : 0);
            Grid.SetColumn(CandidateActions, compact ? 1 : 2);
            Grid.SetColumnSpan(CandidateActions, compact ? 2 : 1);
            CandidateActions.HorizontalAlignment = compact
                ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        }

        const double StructuralWidth = 52 + 24 + 22 + 16;
        double rail = compact ? 0
            : Math.Clamp(available - StructuralWidth - 158, 125, 220);
        if (Math.Abs(MetadataRailColumn.Width.Value - rail) > 0.5)
            MetadataRailColumn.Width = new GridLength(rail);

        if (_linkText is not null)
            _linkText.MaxWidth = compact
                ? Math.Max(130, available - StructuralWidth - 44)
                : Math.Max(45, rail - 48);
    }

    public void SetExternalLink(Uri uri, string label)
    {
        StackPanel content = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
        };
        _linkText = new TextBlock
        {
            Text = label,
            FontSize = 11,
            // A recycled row may receive a new link without a SizeChanged
            // event. Use its CURRENT compact/full layout, never the
            // zero-width metadata rail of a previously stacked card.
            MaxWidth = _compactCandidateLayout
                ? Math.Max(130, CandidateLayout.ActualWidth - 158)
                : Math.Max(45, MetadataRailColumn.Width.Value - 48),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        content.Children.Add(_linkText);
        content.Children.Add(new FontIcon { Glyph = "\uE8A7", FontSize = 11 });
        ExternalLinkButton.NavigateUri = uri;
        ExternalLinkButton.Content = content;
        ToolTipService.SetToolTip(ExternalLinkButton, label);
        ExternalLinkButton.Visibility = Visibility.Visible;
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e) =>
        PreviewRequested?.Invoke(this, EventArgs.Empty);
}
