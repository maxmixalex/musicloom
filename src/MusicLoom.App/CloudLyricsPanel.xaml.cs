using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Lyrics;
using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

public sealed class CloudLyricsAcceptedEventArgs : EventArgs
{
    public CloudLyricsAcceptedEventArgs(
        string sourcePath, CloudLyricsCandidate candidate, LrcDocument document)
    {
        SourcePath = sourcePath;
        Candidate = candidate;
        Document = document;
    }

    public string SourcePath { get; }
    public CloudLyricsCandidate Candidate { get; }
    public LrcDocument Document { get; }
}

/// <summary>
/// Presentation-only candidate wrapper: source results retain their provider
/// IDs and times, while WinUI gets an actual ImageSource and readable duration.
/// </summary>
internal sealed class CloudCandidateRow
{
    public CloudCandidateRow(CloudLyricsCandidate candidate)
    {
        Candidate = candidate;
        if (candidate.CoverUrl is string artwork
            && Uri.TryCreate(artwork, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps)
        {
            Artwork = new BitmapImage
            {
                DecodePixelWidth = 100,
                UriSource = uri,
            };
        }
    }

    public CloudLyricsCandidate Candidate { get; }
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
    public BitmapImage? Artwork { get; }
}

/// <summary>
/// Shared NetEase-style three-column lyric matching dialog for QQ and KuGou.
/// The document is adopted into canonical SQLite only after explicit selection.
/// Recognition uploads PCM only after the user explicitly chooses audio recognition.
/// </summary>
public sealed partial class CloudLyricsPanel : UserControl, IDisposable
{
    private readonly CloudLyricsClient _client = new();
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

    public event EventHandler<CloudLyricsAcceptedEventArgs>? LyricsAccepted;
    public event Action<TimeSpan>? LyricSeekRequested;

    public CloudLyricsPanel()
    {
        InitializeComponent();
    }

    public void Localize(Func<string, string, string> translate)
    {
        _translate = translate;
        SearchHeading.Text = L("Search", "搜索信息");
        CandidatesHeading.Text = L("Candidates", "候选项");
        LyricsHeading.Text = L("Lyrics", "歌词");
        ArtistBox.Header = L("Artist", "艺术家");
        TitleBox.Header = L("Title", "歌曲名");
        AlbumBox.Header = L("Album", "专辑");
        SearchButton.Content = L("Search by metadata", "按元数据搜索");
        AudioRecognizeButton.Content = L(
            "Recognize from audio", "通过音频识别");
        UseLyricsButton.Content = L("Use selected", "使用所选");
        if (CandidatesList.ItemsSource is null)
        {
            ProviderStatus.Text = L(
                "Search this provider; your NetEase match remains unchanged.",
                "从此歌词源搜索，不会改变已有网易云匹配记录。");
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
                : L("Search by artist and title. QQ audio recognition is not available.",
                    "按艺术家和歌名搜索；QQ 音乐音频识别暂不可用。");
        UseLyricsButton.IsEnabled = false;
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
        _busy = true;
        SearchButton.IsEnabled = false;
        AudioRecognizeButton.IsEnabled = false;
        UseLyricsButton.IsEnabled = false;
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
                .Select(static candidate => new CloudCandidateRow(candidate))
                .ToArray();
            CandidatesList.ItemsSource = ranked;
            ProviderStatus.Text = ranked.Length == 0
                ? L("No matches found.", "没有找到匹配。")
                : L(
                    $"{ranked.Length} results · best metadata/duration matches first.",
                    $"找到 {ranked.Length} 个候选 · 已优先按元数据和时长排序。");
            if (ranked.Length > 0)
            {
                CandidatesList.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not search QQ/KuGou lyrics.", error);
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
        UseLyricsButton.IsEnabled = false;
        if (candidate is null)
        {
            ShowPreviewMessage(L(
                "Select a result to preview lyrics.",
                "选择结果后可预览歌词。"));
            return;
        }

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
                    line.Timestamp, line.OriginalWords, line.TranslationWords))
                .ToArray();
            UseLyricsButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not decode QQ/KuGou lyrics.", error);
            ShowPreviewMessage(L(
                "Could not load lyrics from this provider.",
                "无法从此歌词源获取歌词。"));
        }
    }

    private void LyricsPreview_DoubleTapped(
        object sender, DoubleTappedRoutedEventArgs e)
    {
        LyricDisplayLine? line =
            (e.OriginalSource as FrameworkElement)?.DataContext
                as LyricDisplayLine;
        if (line?.Timestamp is TimeSpan time)
        {
            e.Handled = true;
            LyricSeekRequested?.Invoke(time);
        }
    }

    private void UseLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is string path
            && _previewCandidate is CloudLyricsCandidate candidate
            && _previewDocument is LrcDocument document)
        {
            UseLyricsButton.IsEnabled = false;
            LyricsAccepted?.Invoke(this,
                new CloudLyricsAcceptedEventArgs(path, candidate, document));
        }
    }

    public void Dispose()
    {
        _disposed = true;
        CancelRequest();
        _client.Dispose();
        DisposeAudioRecognition();
    }
}
