using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicLoom.Core.Lyrics;
using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

public sealed class CloudLyricsAcceptedEventArgs : EventArgs
{
    public CloudLyricsAcceptedEventArgs(
        string sourcePath, CloudLyricsCandidate candidate,
        LrcDocument document)
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
/// Shared searchable lyrics panel for QQ Music and KuGou.
/// Network state is per-tab; accepted lyrics are committed by MainWindow.
///</summary>
public sealed partial class CloudLyricsPanel : UserControl, IDisposable
{
    private readonly CloudLyricsClient _client = new();
    private Func<string, string, string> _translate = (en, _) => en;
    private CancellationTokenSource? _activeRequest;
    private string? _sourcePath;
    private CloudLyricsCandidate? _previewCandidate;
    private LrcDocument? _previewDocument;
    private bool _busy;

    private CloudLyricsSource _source;
    public CloudLyricsSource Source
    {
        get => _source;
        set
        {
            _source = value;
            AudioRecognizeButton.Visibility =
                value == CloudLyricsSource.KugouMusic
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            AudioRecognizeButton.IsEnabled =
                value == CloudLyricsSource.KugouMusic
                && _sourcePath is not null;
        }
    }
    public event EventHandler<CloudLyricsAcceptedEventArgs>? LyricsAccepted;

    public CloudLyricsPanel()
    {
        InitializeComponent();
    }

    public void Localize(Func<string, string, string> translate)
    {
        _translate = translate;
        ArtistBox.PlaceholderText = L("Artist", "艺术家");
        TitleBox.PlaceholderText = L("Title", "歌曲名");
        SearchButton.Content = L("Search", "搜索");
        AudioRecognizeButton.Content = L(
            "Audio recognize (experimental)",
            "音频识别（实验性）");
        UseLyricsButton.Content = L("Use selected lyrics", "采用所选歌词");
        if (CandidatesList.ItemsSource is null)
        {
            ProviderStatus.Text = L(
                "Search this source independently of NetEase matching.",
                "在此歌词源中独立搜索，不影响网易云匹配记录。");
        }
    }

    public void SetTrack(string? path, string artist = "", string title = "")
    {
        if (string.Equals(path, _sourcePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _sourcePath = path;
        CancelRequest();
        ArtistBox.Text = artist;
        TitleBox.Text = title;
        CandidatesList.ItemsSource = null;
        _previewCandidate = null;
        _previewDocument = null;
        LyricsPreview.Text = string.Empty;
        ProviderStatus.Text = path is null
            ? L("Select a track.", "请选择一首歌曲。")
            : L("Search by artist and title.", "按艺术家和歌名搜索。");
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

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is null || _busy)
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

        CancellationToken cancellation = NewRequest();
        _busy = true;
        SearchButton.IsEnabled = false;
        UseLyricsButton.IsEnabled = false;
        _previewCandidate = null;
        _previewDocument = null;
        CandidatesList.ItemsSource = null;
        LyricsPreview.Text = string.Empty;
        ProviderStatus.Text = L("Searching…", "正在搜索……");
        try
        {
            IReadOnlyList<CloudLyricsCandidate> results =
                await _client.SearchAsync(Source, query, cancellation);
            cancellation.ThrowIfCancellationRequested();
            CandidatesList.ItemsSource = results;
            ProviderStatus.Text = results.Count == 0
                ? L("No matching songs found.", "未找到匹配歌曲。")
                : L(
                    $"{results.Count} result(s). Select a song to preview lyrics.",
                    $"共 {results.Count} 条结果，选择歌曲后预览歌词。");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not search QQ/KuGou lyrics.", error);
            ProviderStatus.Text = L(
                "Search failed. The provider may be unavailable; try again later.",
                "搜索失败，歌词源可能暂时不可用，请稍后重试。");
        }
        finally
        {
            _busy = false;
            SearchButton.IsEnabled = _sourcePath is not null;
        }
    }

    private async void CandidatesList_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        CloudLyricsCandidate? chosen =
            CandidatesList.SelectedItem as CloudLyricsCandidate;
        _previewCandidate = chosen;
        _previewDocument = null;
        UseLyricsButton.IsEnabled = false;
        if (chosen is null)
        {
            LyricsPreview.Text = string.Empty;
            return;
        }

        CancellationToken cancellation = NewRequest();
        LyricsPreview.Text = L("Loading lyrics…", "正在加载歌词……");
        try
        {
            LrcDocument? doc =
                await _client.GetLyricsAsync(chosen, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_previewCandidate, chosen))
            {
                return;
            }

            if (doc is not { Lines.Count: > 0 })
            {
                LyricsPreview.Text = L(
                    "No timed lyrics are available for this candidate.",
                    "所选歌曲没有可用的带时间戳歌词。");
                return;
            }

            _previewDocument = doc;
            // Preview readable text, not QRC/KRC inline timing markup.
            // The canonical document still retains all word timestamps
            // when the user adopts the selected lyrics.
            LyricsPreview.Text = FormatPreview(doc);
            UseLyricsButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not decode QQ/KuGou lyrics.", error);
            LyricsPreview.Text = L(
                "Could not load lyrics from this provider.",
                "无法从此歌词源获取歌词。");
        }
    }

    private static string FormatPreview(LrcDocument document)
    {
        return string.Join(Environment.NewLine,
            LrcPresentation.Project(document).Select(line =>
            {
                string prefix = line.Timestamp is TimeSpan time
                    ? FormattableString.Invariant(
                        $"[{(int)time.TotalMinutes:00}:{time.Seconds:00}] ")
                    : "";
                return prefix + line.Original
                    + (string.IsNullOrWhiteSpace(line.Translation)
                        ? "" : Environment.NewLine + line.Translation);
            }));
    }

    private void UseLyricsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sourcePath is string path
            && _previewCandidate is CloudLyricsCandidate candidate
            && _previewDocument is LrcDocument lyrics)
        {
            UseLyricsButton.IsEnabled = false;
            LyricsAccepted?.Invoke(this,
                new CloudLyricsAcceptedEventArgs(path, candidate, lyrics));
        }
    }

    public void Dispose()
    {
        CancelRequest();
        _client.Dispose();
        DisposeAudioRecognition();
    }
}
