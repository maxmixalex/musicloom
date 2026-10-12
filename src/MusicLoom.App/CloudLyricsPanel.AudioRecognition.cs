using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

public sealed partial class CloudLyricsPanel
{
    private readonly KugouAudioRecognitionClient _audioRecognition = new();

    /// <summary>
    /// Local, read-only 8 kHz PCM16 sampling delegate injected by the
    /// Windows host. The preview is not uploaded until the user confirms.
    /// </summary>
    public Func<string, CancellationToken, Task<byte[]?>>?
        CaptureAudioPcm16 { get; set; }

    private async void AudioRecognizeButton_Click(
        object sender, RoutedEventArgs e)
    {
        if (Source != CloudLyricsSource.KugouMusic
            || _sourcePath is not string audioPath
            || CaptureAudioPcm16 is null || _busy)
        {
            return;
        }

        ContentDialog confirm = new()
        {
            XamlRoot = XamlRoot,
            Title = L(
                "Experimental KuGou audio recognition",
                "实验性酷狗音频识别"),
            Content = new TextBlock
            {
                Text = L(
                    "MusicLoom will decode up to 10 seconds from this local track and upload the PCM audio fragment to KuGou's unofficial recognition endpoint. Only the fragment is sent; no changes will be made to your source file. Continue?",
                    "Musicloom 将从本地歌曲读取最多 10 秒音频，并将 PCM 片段上传到酷狗的非官方识别接口。不会修改源文件。是否继续？"),
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = L("Recognize", "开始识别"),
            CloseButtonText = L("Cancel", "取消"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        CancellationToken token = NewRequest();
        _busy = true;
        SearchButton.IsEnabled = false;
        AudioRecognizeButton.IsEnabled = false;
        ProviderStatus.Text = L("Decoding local audio…", "正在解码本地音频……");
        bool searchRecognized = false;
        try
        {
            byte[]? pcm = await CaptureAudioPcm16(audioPath, token);
            token.ThrowIfCancellationRequested();
            if (pcm is null)
            {
                ProviderStatus.Text = L(
                    "Could not decode enough audio to recognize this track.",
                    "无法解码出足够的音频用于识别。");
                return;
            }

            ProviderStatus.Text = L(
                "Recognizing with KuGou…",
                "正在通过酷狗识曲……");
            IReadOnlyList<KugouRecognitionCandidate> matches =
                await _audioRecognition.RecognizeAsync(pcm, token);
            token.ThrowIfCancellationRequested();
            if (matches.Count == 0)
            {
                ProviderStatus.Text = L(
                    "KuGou did not recognize this audio sample.",
                    "酷狗未能识别该音频片段。");
                return;
            }

            KugouRecognitionCandidate best =
                matches.OrderByDescending(static item => item.Confidence)
                    .First();
            ArtistBox.Text = best.Artist;
            TitleBox.Text = best.Title;
            searchRecognized = true;
            ProviderStatus.Text = L(
                "Audio recognized; searching for the corresponding lyrics…",
                "识曲成功，正在搜索对应歌词……");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            ProviderStatus.Text = L(
                "The experimental KuGou recognition endpoint is unavailable. You can still search by title and artist.",
                "实验性酷狗识曲接口目前不可用；仍可按歌名和艺术家搜索歌词。");
        }
        finally
        {
            _busy = false;
            SearchButton.IsEnabled = _sourcePath is not null;
            AudioRecognizeButton.IsEnabled = _sourcePath is not null;
        }

        if (searchRecognized)
        {
            // Show candidates using the same reusable search/preview/adopt
            // workflow. Recognition never silently confirms lyrics.
            SearchButton_Click(this, new RoutedEventArgs());
        }
    }

    private void DisposeAudioRecognition() => _audioRecognition.Dispose();
}
