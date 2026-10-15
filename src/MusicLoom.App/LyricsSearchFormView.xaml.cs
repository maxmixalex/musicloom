using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MusicLoom.App;

/// <summary>
/// Shared provider-agnostic search fields and action layout.
/// The hosting NetEase/QQ/KuGou controller supplies the search engine and
/// optional audio recognition; no UI or state is shared between providers.
/// </summary>
public sealed partial class LyricsSearchFormView : UserControl
{
    public LyricsSearchFormView() => InitializeComponent();

    public TextBox ArtistField => ArtistInput;
    public TextBox TitleField => TitleInput;
    public TextBox AlbumField => AlbumInput;
    public Button RecognitionAction => RecognitionButton;
    public Button SearchAction => SearchActionButton;
    public TextBlock StatusLabel => StatusText;

    public void Localize(Func<string, string, string> t)
    {
        ArtistInput.Header = t("Artist", "艺术家");
        TitleInput.Header = t("Title", "歌曲名");
        AlbumInput.Header = t("Album", "专辑");
        RecognitionButton.Content =
            t("Recognize from audio", "通过音频识别");
        SearchActionButton.Content =
            t("Search by metadata", "按元数据搜索");
    }
}
