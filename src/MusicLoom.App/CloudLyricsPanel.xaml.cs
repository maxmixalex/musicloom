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

    // Use MainWindow's actual lyric row template for QQ and KuGou.
    internal void ConfigurePreviewFrom(ListView source)
    {
        LyricsPreviewList.ItemTemplate = source.ItemTemplate;
        LyricsPreviewList.ItemContainerStyle = source.ItemContainerStyle;
    }

    private void SetPreviewMessage(string message) =>
        LyricsPreviewList.ItemsSource =
            new[] { new LyricDisplayLine(
                message, null, LyricsDisplayModes.OriginalOnly) };

    public void Localize(Func<string, string, string> translate)
    {
        _translate = translate;
        SearchLayout.Localize(L);
        ArtistBox.Header = L("Artist", "艺术家");
        TitleBox.Header = L("Title", "歌曲名");
        AlbumBox.Header = L("Album", "专辑");
        SearchButton.Content = L("Search by metadata", "按元数据搜索");
        AudioRecognizeButton.Content = L(
            "Recognize from audio",
            "通过音频识别");
        UseLyricsButton.Content = L("Use selected", "使用所选");
        if (CandidatesList.ItemsSource is null)
        {
            ProviderStatus.Text = L(
                "Search this source independently of NetEase matching.",
                "在此歌词源中独立搜索，不影响网易云匹配记录。");
        }
    }

    public void SetTrack(string? path, string artist = "", string title = "", string album = "")
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
        AlbumBox.Text = album;
        CandidatesList.ItemsSource = null;
        _previewCandidate = null;
        _previewDocument = null;
        LyricsPreviewList.ItemsSource = null;
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

        string title = TitleBox.Text.Trim();
        string artist = ArtistBox.Text.Trim();
        string album = AlbumBox.Text.Trim();
        string query = string.Join(" ", new[]
        {
            artist, title,
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
        LyricsPreviewList.ItemsSource = null;
        ProviderStatus.Text = L("Searching…", "正在搜索……");
        try
        {
            IReadOnlyList<CloudLyricsCandidate> results =
                await _client.SearchAsync(Source, query, cancellation);
            cancellation.ThrowIfCancellationRequested();
            // A provider may reject compound artist/title searches; fall
            // back to title alone, then rank by the metadata in this dialog.
            if (results.Count == 0 && title.Length > 0 && artist.Length > 0)
            {
                results = await _client.SearchAsync(Source, title, cancellation);
                cancellation.ThrowIfCancellationRequested();
            }
            IReadOnlyList<CloudLyricsCandidate> ranked =
                CloudLyricsCandidateRanker.Rank(results, title, artist, album);
            CandidatesList.ItemsSource = ranked;
            ProviderStatus.Text = ranked.Count == 0
                ? L("No matching songs found.", "未找到匹配歌曲。")
                : L(
                    $"{ranked.Count} result(s). Select a song to preview lyrics.",
                    $"共 {ranked.Count} 条结果，选择歌曲后预览歌词。");
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
            LyricsPreviewList.ItemsSource = null;
            return;
        }

        CancellationToken cancellation = NewRequest();
        SetPreviewMessage(L("Loading lyrics…", "正在加载歌词……"));
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
                SetPreviewMessage(L(
                    "No timed lyrics are available for this candidate.",
                    "所选歌曲没有可用的带时间戳歌词。"));
                return;
            }

            _previewDocument = doc;
            // Preview readable text, not QRC/KRC inline timing markup.
            // The canonical document still retains all word timestamps
            // when the user adopts the selected lyrics.
            LyricsPreviewList.ItemsSource = MainWindow.BuildLyricLines(
                doc.ToString(), LyricsDisplayModes.PreferOriginal);
            UseLyricsButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not decode QQ/KuGou lyrics.", error);
            SetPreviewMessage(L(
                "Could not load lyrics from this provider.",
                "无法从此歌词源获取歌词。"));
        }
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
