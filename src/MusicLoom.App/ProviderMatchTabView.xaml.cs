using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

internal enum MatchProviderKind
{
    Netease,
    QqMusic,
    KugouMusic,
}

/// <summary>
/// The exact original NetEase match card and commands, now instantiated
/// three times. Data/state/services are adapters; the view is NOT copied.
/// </summary>
public sealed partial class ProviderMatchTabView : UserControl
{
    private string _providerName = "";
    private Func<string, string, string> _translate = (en, _) => en;

    public event EventHandler? SearchRequested;
    public event EventHandler? ConfirmRequested;
    public event EventHandler? ClearRequested;
    public event EventHandler? UpdateRequested;

    internal Image ArtworkImage => CurrentMatchArtworkImage;
    internal FontIcon ArtworkPlaceholder => CurrentMatchArtworkPlaceholder;
    internal TextBlock TitleLabel => CurrentMatchTitleText;
    internal TextBlock ArtistLabel => CurrentMatchArtistText;
    internal TextBlock AlbumLabel => CurrentMatchAlbumText;
    internal TextBlock IdLabel => CurrentMatchIdText;
    internal HyperlinkButton ExternalLink => CurrentMatchOpenLink;
    internal TextBlock ExternalLinkLabel => CurrentMatchOpenLinkText;
    internal TextBlock HelpLabel => MatchHelpText;
    internal Button FindButton => OpenMatchDialogButton;
    internal TextBlock FindButtonLabel => OpenMatchDialogButtonText;
    internal Button ConfirmButton => ConfirmMatchButton;
    internal TextBlock ConfirmButtonLabel => ConfirmMatchButtonText;
    internal Button ClearButton => SuppressMatchButton;
    internal TextBlock ClearButtonLabel => SuppressMatchButtonText;
    internal Button UpdateButton => RefreshMatchedLyricsButton;
    internal TextBlock UpdateButtonLabel => RefreshMatchedLyricsButtonText;

    public ProviderMatchTabView()
    {
        InitializeComponent();
        OpenMatchDialogButton.Click += (_, _) =>
            SearchRequested?.Invoke(this, EventArgs.Empty);
        ConfirmMatchButton.Click += (_, _) =>
            ConfirmRequested?.Invoke(this, EventArgs.Empty);
        SuppressMatchButton.Click += (_, _) =>
            ClearRequested?.Invoke(this, EventArgs.Empty);
        RefreshMatchedLyricsButton.Click += (_, _) =>
            UpdateRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void Localize(Func<string, string, string> translate,
        MatchProviderKind kind)
    {
        _translate = translate;
        (string en, string zh) = kind switch
        {
            MatchProviderKind.Netease => ("NetEase", "网易云"),
            MatchProviderKind.QqMusic => ("QQ Music", "QQ音乐"),
            _ => ("KuGou Music", "酷狗音乐"),
        };
        _providerName = translate(en, zh);
        OpenMatchDialogButtonText.Text = translate(
            $"Find {en} match…", $"查找{zh}匹配……");
        ConfirmMatchButtonText.Text = translate("Confirm match", "确认匹配");
        SuppressMatchButtonText.Text = translate(
            $"No {en} match", $"置空{zh}匹配");
        RefreshMatchedLyricsButtonText.Text =
            translate("Update lyrics", "更新歌词");
        CurrentMatchOpenLinkText.Text = translate(
            $"Open in {en}", $"在{zh}中打开");
        MatchHelpText.Text = kind switch
        {
            MatchProviderKind.Netease => translate(
                "Use fingerprint recognition or search by artist and title.",
                "可以使用音频指纹识别，或按艺术家和歌曲名搜索。"),
            MatchProviderKind.KugouMusic => translate(
                "Recognize audio or search by artist and title.",
                "可以通过音频识别，或按艺术家和歌曲名搜索。"),
            _ => translate(
                "Search by artist and title.",
                "按艺术家和歌曲名搜索。"),
        };
    }

    // The provider metadata is the ONLY source of the card. Local title,
    // artist, album and artwork must never be used to impersonate a match.
    internal void ShowCloudState(
        StoredProviderMatch? record, bool selected, bool busy)
    {
        ArtworkImage.Source = null;
        ArtworkPlaceholder.Visibility = Visibility.Visible;
        AlbumLabel.Text = string.Empty;
        ArtistLabel.Text = string.Empty;
        IdLabel.Text = string.Empty;
        ExternalLink.NavigateUri = null;
        ExternalLink.Visibility = Visibility.Collapsed;

        FindButton.IsEnabled = selected && !busy;
        ClearButton.Visibility = selected
            ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = selected && !busy
            && record?.Suppressed != true;
        ConfirmButton.Visibility = Visibility.Collapsed;
        ConfirmButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;

        if (record?.Suppressed == true)
        {
            TitleLabel.Text = _translate(
                $"No {_providerName} match",
                $"没有{_providerName}匹配");
            ArtistLabel.Text = _translate(
                "Automatic matching is disabled for this track.",
                "这首歌曲已禁用自动匹配。");
            return;
        }

        if (record?.Candidate is not CloudLyricsCandidate candidate)
        {
            TitleLabel.Text = _translate(
                $"No {_providerName} match cached.",
                $"尚未缓存{_providerName}匹配。");
            return;
        }

        TitleLabel.Text = candidate.Title;
        ArtistLabel.Text = candidate.Artist;
        AlbumLabel.Text = candidate.Album;
        IdLabel.Text = "#" + candidate.Id;
        if (candidate.CoverUrl is string cover
            && Uri.TryCreate(cover, UriKind.Absolute, out Uri? image)
            && image.Scheme == Uri.UriSchemeHttps)
        {
            ArtworkImage.Source = new BitmapImage
            {
                DecodePixelWidth = 108,
                UriSource = image,
            };
            ArtworkPlaceholder.Visibility = Visibility.Collapsed;
        }

        Uri? external = SongPage(candidate);
        if (external is not null)
        {
            ExternalLink.NavigateUri = external;
            ExternalLink.Visibility = Visibility.Visible;
        }
        ConfirmButton.Visibility = record.Confirmed
            ? Visibility.Collapsed : Visibility.Visible;
        ConfirmButton.IsEnabled = !busy && !record.Confirmed;
        UpdateButton.IsEnabled = !busy;
    }

    private static Uri? SongPage(CloudLyricsCandidate candidate)
    {
        // A QQ song MID is a public song-detail identifier. KuGou's
        // compact page slug is NOT derivable from its file hash: do not
        // fabricate a broken direct song link.
        if (candidate.Source != CloudLyricsSource.QqMusic)
            return null;
        string? mid = candidate.SongMid;
        return mid is { Length: >= 8 and <= 32 }
            && mid.All(char.IsAsciiLetterOrDigit)
                ? new Uri("https://y.qq.com/n/ryqq/songDetail/" + mid)
                : null;
    }
}
