using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Lyrics;
using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

public sealed class CloudMatchAcceptedEventArgs : EventArgs
{
    public CloudMatchAcceptedEventArgs(
        string sourcePath, CloudLyricsCandidate candidate, LrcDocument? document)
    {
        SourcePath = sourcePath;
        Candidate = candidate;
        Document = document;
    }

    public string SourcePath { get; }
    public CloudLyricsCandidate Candidate { get; }
    public LrcDocument? Document { get; }
}

/// <summary>
/// Presentation-only candidate wrapper: source results retain their provider
/// IDs and times, while WinUI gets an actual ImageSource and readable duration.
/// </summary>
internal sealed class CloudCandidateRow : INotifyPropertyChanged
{
    public CloudCandidateRow(CloudLyricsCandidate candidate,
        string previewTooltip, string linkLabel, string searchLabel)
    {
        Candidate = candidate;
        PreviewTooltip = previewTooltip;
        _linkLabel = linkLabel;
        _searchLabel = searchLabel;
        ApplyArtwork(candidate.CoverUrl);
    }

    private readonly string _linkLabel;
    private readonly string _searchLabel;
    public CloudLyricsCandidate Candidate { get; private set; }
    public string PreviewTooltip { get; }
    public bool PreviewIsPlaying { get; private set; }
    public void SetPreviewPlaying(bool playing)
    {
        if (PreviewIsPlaying == playing) return;
        PreviewIsPlaying = playing;
        PropertyChanged?.Invoke(this,
            new PropertyChangedEventArgs(nameof(PreviewIsPlaying)));
    }

    public string Title => Candidate.Title;
    public string Artist => Candidate.Artist;
    public string Album => Candidate.Album;
    public string Duration
    {
        get
        {
            TimeSpan time = TimeSpan.FromMilliseconds(
                Math.Max(0, Candidate.DurationMilliseconds));
            return time.TotalMinutes < 1 && Candidate.DurationMilliseconds == 0
                ? "—"
                : $"{(int)time.TotalMinutes}:{time.Seconds:00}";
        }
    }
    public string DisplayMetadata => string.IsNullOrWhiteSpace(Candidate.Id)
        ? Duration : $"#{Candidate.Id} · {Duration}";
    public string? ExternalUrl => Candidate.Source switch
    {
        CloudLyricsSource.QqMusic
            when Candidate.SongMid is { Length: > 0 } mid
            && mid.All(char.IsAsciiLetterOrDigit) =>
            "https://y.qq.com/n/ryqq/songDetail/" + mid,
        CloudLyricsSource.QqMusic =>
            "https://y.qq.com/n/ryqq/search?w=" +
                Uri.EscapeDataString(Candidate.Artist + " " + Candidate.Title),
        CloudLyricsSource.KugouMusic
            when Candidate.Hash is { Length: >= 16 and <= 64 } hash
            && hash.All(UriHexDigit) =>
            "https://www.kugou.com/song/#hash=" + hash
                + (Candidate.AlbumId is { Length: > 0 } id
                    && id.All(char.IsAsciiDigit)
                    ? "&album_id=" + id : ""),
        CloudLyricsSource.KugouMusic =>
            "https://www.kugou.com/yy/html/search.html#searchType=song&searchKey="
                + Uri.EscapeDataString(Candidate.Artist + " " + Candidate.Title),
        CloudLyricsSource.MiguMusic or CloudLyricsSource.AppleMusic
            or CloudLyricsSource.Spotify =>
            CloudLyricsSourceNames.SongPage(Candidate)?.AbsoluteUri,
        _ => null,
    };
    public string ExternalLabel => Candidate.Source switch
    {
        CloudLyricsSource.QqMusic
            when Candidate.SongMid is not { Length: > 0 } mid
                || !mid.All(char.IsAsciiLetterOrDigit) => _searchLabel,
        CloudLyricsSource.KugouMusic
            when Candidate.Hash is not { Length: >= 16 and <= 64 } hash
                || !hash.All(UriHexDigit) => _searchLabel,
        CloudLyricsSource.MiguMusic or CloudLyricsSource.AppleMusic
            or CloudLyricsSource.Spotify
            when CloudLyricsSourceNames.SongPage(Candidate) is null => _searchLabel,
        _ => _linkLabel,
    };
    private static bool UriHexDigit(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    public BitmapImage? Artwork { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyArtwork(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)
            || !Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || Artwork?.UriSource == uri)
        {
            return;
        }

        Candidate = Candidate with { CoverUrl = address };
        Artwork = new BitmapImage
        {
            DecodePixelWidth = 112,
            UriSource = uri,
        };
        PropertyChanged?.Invoke(this,
            new PropertyChangedEventArgs(nameof(Artwork)));
    }
}

/// <summary>
/// Shared NetEase-style three-column lyric matching dialog for QQ and KuGou.
/// The document is adopted into canonical SQLite only after explicit selection.
/// Recognition uploads PCM only after the user explicitly chooses audio recognition.
/// </summary>
public sealed partial class CloudLyricsPanel : UserControl, IDisposable
{
    private readonly CloudLyricsClient _client = new();

    // These are the actual controls inside the shared LyricsSearchFormView,
    // not a second provider-specific copy with equivalent styling.
    private TextBox ArtistBox => SearchInputs.ArtistField;
    private TextBox TitleBox => SearchInputs.TitleField;
    private TextBox AlbumBox => SearchInputs.AlbumField;
    private Button AudioRecognizeButton => SearchInputs.RecognitionAction;
    private Button SearchButton => SearchInputs.SearchAction;
    private TextBlock ProviderStatus => SearchInputs.StatusLabel;
    private Func<string, string, string> _translate = (english, _) => english;
    private CancellationTokenSource? _activeRequest;
    private string? _sourcePath;
    private CloudLyricsCandidate? _previewCandidate;
    private LrcDocument? _previewDocument;
    private string _trackArtist = "";
    private string _trackTitle = "";
    private string _trackAlbum = "";
    private double? _trackDurationMs;
    private bool _busy;
    private bool _disposed;
    private CloudLyricsSource _source;

    public CloudLyricsSource Source
    {
        get => _source;
        set
        {
            _source = value;
            AudioRecognizeButton.Visibility =
                value == CloudLyricsSource.KugouMusic
                    ? Visibility.Visible : Visibility.Collapsed;
            AudioRecognizeButton.IsEnabled =
                value == CloudLyricsSource.KugouMusic
                && _sourcePath is not null;
        }
    }

    public event EventHandler<CloudMatchAcceptedEventArgs>? MatchAccepted;
    public event Action<TimeSpan>? LyricSeekRequested;
    public event Action<CloudLyricsCandidate>? PreviewRequested;

    // Used by the host to keep the shared remote preview and lyric seek
    // locked to the same selected provider candidate.
    public CloudLyricsCandidate? SelectedCandidate => _previewCandidate;
    public LrcDocument? SelectedDocument => _previewDocument;
    public ListView PreviewLyricsList => LyricsPreview.LyricsList;

    public void SetPreviewStatus(string message)
    {
        if (!_disposed)
            ProviderStatus.Text = message;
    }

    public void UpdatePreviewPlaying(
        CloudLyricsCandidate? activeCandidate, bool isPlaying)
    {
        if (_disposed || CandidatesList.ItemsSource
            is not IEnumerable<CloudCandidateRow> rows)
            return;
        foreach (CloudCandidateRow row in rows)
        {
            row.SetPreviewPlaying(isPlaying && activeCandidate is not null
                && row.Candidate.Source == activeCandidate.Source
                && string.Equals(row.Candidate.Id, activeCandidate.Id,
                    StringComparison.Ordinal)
                && string.Equals(row.Candidate.SongMid,
                    activeCandidate.SongMid, StringComparison.Ordinal));
        }
    }

    public CloudLyricsPanel()
    {
        InitializeComponent();
        SearchButton.Click += SearchButton_Click;
        AudioRecognizeButton.Click += AudioRecognizeButton_Click;
        LyricsPreview.SeekRequested += time => LyricSeekRequested?.Invoke(time);
    }

    public void Localize(Func<string, string, string> translate)
    {
        _translate = translate;
        SearchLayout.Localize(L);
        SearchInputs.Localize(L);
        ArtistBox.Header = L("Artist", "艺术家");
        TitleBox.Header = L("Title", "歌曲名");
        AlbumBox.Header = L("Album", "专辑");
        SearchButton.Content = L("Search by metadata", "按元数据搜索");
        AudioRecognizeButton.Content = L(
            "Recognize from audio", "通过音频识别");
        UseMatchButton.Content = L("Use selected", "使用所选");
        if (CandidatesList.ItemsSource is null)
        {
            ProviderStatus.Text = L(
                "Search for a match.", "搜索以查找匹配。");
        }
        if (LyricsPreview.ItemsSource is null)
        {
            ShowPreviewMessage(
                L("Select a result to preview lyrics.",
                  "选择结果后可预览歌词。"));
        }
    }

    public void SetTrack(
        string? path, string artist = "", string title = "",
        string album = "", double? durationMilliseconds = null)
    {
        if (string.Equals(path, _sourcePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _sourcePath = path;
        _trackArtist = artist;
        _trackTitle = title;
        _trackAlbum = album;
        _trackDurationMs = durationMilliseconds;
        CancelRequest();
        CancelArtworkLoad();
        ArtistBox.Text = artist;
        TitleBox.Text = title;
        AlbumBox.Text = album;
        CandidatesList.ItemsSource = null;
        _previewCandidate = null;
        _previewDocument = null;
        ShowPreviewMessage(L("Select a result to preview lyrics.",
            "选择结果后可预览歌词。"));
        ProviderStatus.Text = path is null
            ? L("Select a track.", "请选择一首歌曲。")
            : Source == CloudLyricsSource.KugouMusic
                ? L("Use audio recognition or search by artist and title.",
                    "可通过音频识别，或按艺术家与歌名搜索。")
                : L("Search by artist and title.",
                    "按艺术家和歌曲名搜索。");
        UseMatchButton.IsEnabled = false;
        AudioRecognizeButton.IsEnabled = path is not null
            && Source == CloudLyricsSource.KugouMusic;
    }

    private string L(string english, string chinese) =>
        _translate(english, chinese);

    private CancellationToken NewRequest()
    {
        CancelRequest();
        _activeRequest = new CancellationTokenSource();
        return _activeRequest.Token;
    }

    private void CancelRequest()
    {
        _activeRequest?.Cancel();
        _activeRequest?.Dispose();
        _activeRequest = null;
    }

    private static string Key(string input) =>
        new string(input.Normalize(System.Text.NormalizationForm.FormKC)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private double CandidateScore(CloudLyricsCandidate candidate)
    {
        string title = Key(TitleBox.Text);
        string artist = Key(ArtistBox.Text);
        string album = Key(AlbumBox.Text);
        string candidateTitle = Key(candidate.Title);
        string candidateArtist = Key(candidate.Artist);
        double score = 0;
        if (title.Length > 0)
        {
            if (title == candidateTitle) score += 70;
            else if (candidateTitle.Contains(title, StringComparison.Ordinal)
                || title.Contains(candidateTitle, StringComparison.Ordinal))
                score += 30;
        }
        if (artist.Length > 0)
        {
            if (artist == candidateArtist) score += 45;
            else if (candidateArtist.Contains(artist, StringComparison.Ordinal)
                || artist.Contains(candidateArtist, StringComparison.Ordinal))
                score += 16;
        }
        if (album.Length > 0 && album == Key(candidate.Album))
            score += 15;
        if (_trackDurationMs is double duration
            && duration > 0 && candidate.DurationMilliseconds > 0)
        {
            double diff = Math.Abs(duration - candidate.DurationMilliseconds);
            score += 25 * Math.Max(0, 1 - diff / 6_000);
        }
        return score;
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _sourcePath is null || _busy)
        {
            return;
        }

        string query = string.Join(" ", new[]
        {
            ArtistBox.Text.Trim(), TitleBox.Text.Trim(),
        }.Where(static part => part.Length != 0));

        if (query.Length == 0)
        {
            ProviderStatus.Text = L(
                "Enter a song title or artist.", "请输入歌名或艺术家。");
            return;
        }

        CancellationToken ct = NewRequest();
        CancelArtworkLoad();
        _busy = true;
        SearchButton.IsEnabled = false;
        AudioRecognizeButton.IsEnabled = false;
        UseMatchButton.IsEnabled = false;
        _previewCandidate = null;
        _previewDocument = null;
        CandidatesList.ItemsSource = null;
        ShowPreviewMessage(L("Select a result to preview lyrics.",
            "选择结果后可预览歌词。"));
        ProviderStatus.Text = L("Searching…", "正在搜索……");

        try
        {
            IReadOnlyList<CloudLyricsCandidate> found =
                await _client.SearchAsync(Source, query, ct);
            ct.ThrowIfCancellationRequested();
            if (_disposed) return;

            CloudCandidateRow[] ranked = found
                .OrderByDescending(CandidateScore)
                .Select(candidate =>
                {
                    string name = L(
                        CloudLyricsSourceNames.English(candidate.Source),
                        CloudLyricsSourceNames.Chinese(candidate.Source));
                    return new CloudCandidateRow(candidate,
                        candidate.Source == CloudLyricsSource.Spotify
                            ? L("Check provider playback availability",
                                "检查音源播放权限")
                            : L("Play remote preview", "播放远程试听"),
                        L("Open in ", "在") + name + L("", "中打开"),
                        L("Search in ", "在") + name + L(" search", "中搜索"));
                })
                .ToArray();
            CandidatesList.ItemsSource = ranked;
            ProviderStatus.Text = ranked.Length == 0
                ? L("No matches found.", "没有找到匹配。")
                : L(
                    $"{ranked.Length} results found. Candidates are ranked by matching metadata and duration.",
                    $"找到 {ranked.Length} 个候选，已根据元数据和时长排序。");
            if (ranked.Length > 0)
            {
                CandidatesList.SelectedIndex = 0;
                BeginArtworkLoading(ranked);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (MusicLoom.ProviderPoc.ProviderAuthorizationException)
        {
            ProviderStatus.Text = L(
                "Sign-in required. Supply your own provider token through the documented environment variables and restart MusicLoom.",
                "需要账户授权。请按 Provider PoC 文档设置自己的环境变量令牌，然后重启 MusicLoom。");
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not search music provider songs.", error);
            ProviderStatus.Text = L(
                "Provider search failed; see Diagnostics for details.",
                "歌词源搜索失败，详情请查看诊断日志。");
        }
        finally
        {
            _busy = false;
            SearchButton.IsEnabled = !_disposed && _sourcePath is not null;
            AudioRecognizeButton.IsEnabled = !_disposed
                && _sourcePath is not null
                && Source == CloudLyricsSource.KugouMusic;
        }
    }

    private void ShowPreviewMessage(string message) =>
        LyricsPreview.ItemsSource = new[]
        {
            new LyricDisplayLine(message, null, LyricsDisplayModes.OriginalOnly),
        };

    private async void CandidatesList_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        CloudLyricsCandidate? candidate =
            (CandidatesList.SelectedItem as CloudCandidateRow)?.Candidate;
        _previewCandidate = candidate;
        _previewDocument = null;
        UseMatchButton.IsEnabled = false;
        if (candidate is null)
        {
            ShowPreviewMessage(L(
                "Select a result to preview lyrics.",
                "选择结果后可预览歌词。"));
            return;
        }

        // Matching is independent of lyrics availability, just as it is
        // for NetEase. A song can be selected even if lyrics are absent.
        UseMatchButton.IsEnabled = true;
        CancellationToken ct = NewRequest();
        ShowPreviewMessage(L("Loading lyrics…", "正在加载歌词……"));
        try
        {
            LrcDocument? document =
                await _client.GetLyricsAsync(candidate, ct);
            ct.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_previewCandidate, candidate))
                return;

            if (document is not { Lines.Count: > 0 })
            {
                ShowPreviewMessage(L(
                    "No timed lyrics are available for this candidate.",
                    "所选歌曲没有可用的带时间戳歌词。"));
                return;
            }

            _previewDocument = document;
            LyricsPreview.ItemsSource = LrcPresentation.Project(document)
                .Select(line => new LyricDisplayLine(
                    line.Original, line.Translation,
                    LyricsDisplayModes.PreferOriginal,
                    line.Timestamp, line.OriginalWords, line.TranslationWords,
                    line.Romanization))
                .ToArray();
            UseMatchButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (MusicLoom.ProviderPoc.ProviderAuthorizationException)
        {
            ShowPreviewMessage(L(
                "This provider needs your authorized account to retrieve lyrics.",
                "当前音源需要账户授权才能获取歌词。"));
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not decode provider lyrics.", error);
            ShowPreviewMessage(L(
                "Could not load lyrics from this provider.",
                "无法从此歌词源获取歌词。"));
        }
    }

    private void CandidateCard_PreviewRequested(object sender, EventArgs e)
    {
        if (sender is LyricsSearchCandidateCard card
            && card.DataContext is CloudCandidateRow row)
        {
            // Clicking a play icon also selects that candidate so its lyrics
            // and seek targets cannot point to the previous search result.
            if (!ReferenceEquals(CandidatesList.SelectedItem, row))
                CandidatesList.SelectedItem = row;
            PreviewRequested?.Invoke(row.Candidate);
        }
    }

    private void UseMatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is string path
            && CandidatesList.SelectedItem is CloudCandidateRow selected)
        {
            UseMatchButton.IsEnabled = false;
            MatchAccepted?.Invoke(this,
                new CloudMatchAcceptedEventArgs(
                    path, selected.Candidate, _previewDocument));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        CancelRequest();
        CancelArtworkLoad();
        _client.Dispose();
        DisposeAudioRecognition();
    }
}
