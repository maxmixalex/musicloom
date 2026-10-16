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
    MiguMusic,
    AppleMusic,
    Spotify,
}


/// <summary>
/// One shared view-state contract for all three matching services.
/// Providers supply actual remote metadata and capabilities only.
/// </summary>
internal sealed record MatchTabPresentation(
    bool Selected,
    bool Suppressed,
    string? Id,
    string? Title,
    string? Artist,
    string? Album,
    string? CoverUrl,
    Uri? ExternalLink,
    bool NeedsConfirmation,
    bool CanUpdateLyrics,
    bool Busy)
{
    public bool HasMatch => !Suppressed && !string.IsNullOrWhiteSpace(Id);
}

/// <summary>
/// The exact original NetEase match card and commands, now instantiated
/// three times. Data/state/services are adapters; the view is NOT copied.
/// </summary>
public sealed partial class ProviderMatchTabView : UserControl
{
    private string _providerName = "";
    private MatchProviderKind _providerKind = MatchProviderKind.Netease;
    private string? _displayedMatchId;
    private Uri? _displayedArtworkUri;

    internal Uri? DisplayedArtworkUri => _displayedArtworkUri;
    internal string? DisplayedMatchId => _displayedMatchId;

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
        _providerKind = kind;
        (string en, string zh) = kind switch
        {
            MatchProviderKind.Netease => ("NetEase", "网易云"),
            MatchProviderKind.QqMusic => ("QQ Music", "QQ音乐"),
            MatchProviderKind.KugouMusic => ("KuGou Music", "酷狗音乐"),
            MatchProviderKind.MiguMusic => ("Migu Music", "咪咕音乐"),
            MatchProviderKind.AppleMusic => ("Apple Music", "Apple Music"),
            MatchProviderKind.Spotify => ("Spotify", "Spotify"),
            _ => ("NetEase", "网易云"),
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

    // The same state machine is used for NetEase, QQ and KuGou. Only
    // provider adapters build a MatchTabPresentation. This is not merely
    // three separately managed controls with identical-looking markup.
    internal void Present(MatchTabPresentation state)
    {
        // Preserve a successfully loaded NetEase/QQ/KuGou cover whenever
        // the same provider match is re-rendered (busy, language, sync,
        // details reload). Only a DIFFERENT song ID or an empty/suppressed
        // state invalidates the artwork. The old renderer cleared every
        // time, cancelling asynchronous NetEase cover hydration.
        string? nextId = state.HasMatch ? state.Id : null;
        if (nextId is null || !string.Equals(
                _displayedMatchId, nextId, StringComparison.Ordinal))
        {
            ArtworkImage.Source = null;
            ArtworkPlaceholder.Visibility = Visibility.Visible;
            _displayedArtworkUri = null;
        }
        _displayedMatchId = nextId;

        TitleLabel.Text = string.Empty;
        ArtistLabel.Text = string.Empty;
        AlbumLabel.Text = string.Empty;
        IdLabel.Text = string.Empty;
        ExternalLink.NavigateUri = null;
        ExternalLink.Visibility = Visibility.Collapsed;

        FindButton.IsEnabled = state.Selected && !state.Busy;
        ClearButton.Visibility = state.Selected
            ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = state.Selected && !state.Busy
            && !state.Suppressed;
        ConfirmButton.Visibility = state.HasMatch && state.NeedsConfirmation
            ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.IsEnabled = state.Selected && !state.Busy
            && state.NeedsConfirmation && state.HasMatch;
        UpdateButton.IsEnabled = state.Selected && !state.Busy
            && state.HasMatch && state.CanUpdateLyrics;

        if (state.Suppressed)
        {
            TitleLabel.Text = _translate(
                $"No {_providerName} match",
                $"没有{_providerName}匹配");
            ArtistLabel.Text = _translate(
                "Automatic matching is disabled for this track.",
                "这首歌曲已禁用自动匹配。");
            return;
        }

        if (!state.HasMatch)
        {
            TitleLabel.Text = _translate(
                $"No {_providerName} match cached.",
                $"尚未缓存{_providerName}匹配。");
            return;
        }

        // Never display the local file title/artist/cover as an allegedly
        // matched provider song; missing remote fields remain blank.
        TitleLabel.Text = string.IsNullOrWhiteSpace(state.Title)
            ? "#" + state.Id : state.Title;
        ArtistLabel.Text = state.Artist ?? string.Empty;
        AlbumLabel.Text = state.Album ?? string.Empty;
        IdLabel.Text = "#" + state.Id;
        ExternalLink.NavigateUri = state.ExternalLink;
        ExternalLink.Visibility = state.ExternalLink is null
            ? Visibility.Collapsed : Visibility.Visible;

        if (state.CoverUrl is string cover
            && Uri.TryCreate(cover, UriKind.Absolute, out Uri? image))
            ShowArtwork(image);
    }

    internal void ShowArtwork(Uri? artwork)
    {
        if (artwork is null || !IsAcceptedCoverUri(artwork))
            return;
        if (artwork.Equals(_displayedArtworkUri)
            && ArtworkImage.Source is BitmapImage)
            return;

        // The ORIGINAL NetEase card accepted HTTP as well as HTTPS cover
        // URLs. Old persisted NetEase records can have an HTTP URL; the
        // previous shared renderer rejected those and displayed nothing.
        ArtworkImage.Source = new BitmapImage
        {
            DecodePixelWidth = 108,
            UriSource = artwork,
        };
        _displayedArtworkUri = artwork;
        ArtworkPlaceholder.Visibility = Visibility.Collapsed;
    }

    private bool IsAcceptedCoverUri(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.UserInfo.Length == 0
        && uri.IsDefaultPort
        && (uri.Scheme == Uri.UriSchemeHttps
            || (_providerKind == MatchProviderKind.Netease
                && uri.Scheme == Uri.UriSchemeHttp));


    internal void ShowCloudState(
        StoredProviderMatch? record, bool selected, bool busy)
    {
        CloudLyricsCandidate? candidate = record?.Candidate;
        Present(new MatchTabPresentation(
            Selected: selected,
            Suppressed: record?.Suppressed == true,
            Id: candidate?.Id,
            Title: candidate?.Title,
            Artist: candidate?.Artist,
            Album: candidate?.Album,
            CoverUrl: candidate?.CoverUrl,
            ExternalLink: candidate is null ? null : SongPage(candidate),
            NeedsConfirmation: record?.IsMatched == true
                && record.Confirmed == false,
            CanUpdateLyrics: record?.IsMatched == true,
            Busy: busy));
    }

    private static Uri? SongPage(CloudLyricsCandidate candidate)
    {
        // A QQ song MID is a public song-detail identifier. KuGou's
        // compact page slug is NOT derivable from its file hash: do not
        // fabricate a broken direct song link.
        if (CloudLyricsSourceNames.IsExternal(candidate.Source))
        {
            if (!Uri.TryCreate(candidate.ExternalUrl, UriKind.Absolute,
                    out Uri? external)
                || external.Scheme != Uri.UriSchemeHttps
                || !external.IsDefaultPort
                || external.UserInfo.Length > 0)
                return null;

            string expected = candidate.Source switch
            {
                CloudLyricsSource.MiguMusic => "migu.cn",
                CloudLyricsSource.AppleMusic => "music.apple.com",
                CloudLyricsSource.Spotify => "open.spotify.com",
                _ => "",
            };
            return external.Host.Equals(expected, StringComparison.OrdinalIgnoreCase)
                || external.Host.EndsWith("." + expected,
                    StringComparison.OrdinalIgnoreCase)
                    ? external : null;
        }
        if (candidate.Source != CloudLyricsSource.QqMusic)
            return null;
        string? mid = candidate.SongMid;
        return mid is { Length: >= 8 and <= 32 }
            && mid.All(char.IsAsciiLetterOrDigit)
                ? new Uri("https://y.qq.com/n/ryqq/songDetail/" + mid)
                : null;
    }
}
