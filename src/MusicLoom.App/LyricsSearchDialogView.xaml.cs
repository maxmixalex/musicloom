using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace MusicLoom.App;
/// <summary>A shared search/results/lyrics dialog body for all three providers.</summary>
public sealed partial class LyricsSearchDialogView : UserControl
{
    public LyricsSearchDialogView() => InitializeComponent();
    public UIElement? SearchContent
    {
        get => SearchHost.Content as UIElement;
        set => SearchHost.Content = value;
    }
    public UIElement? CandidatesContent
    {
        get => CandidatesHost.Content as UIElement;
        set => CandidatesHost.Content = value;
    }
    public UIElement? LyricsContent
    {
        get => LyricsHost.Content as UIElement;
        set => LyricsHost.Content = value;
    }
    public UIElement? FooterContent
    {
        get => FooterHost.Content as UIElement;
        set => FooterHost.Content = value;
    }
    public void Localize(Func<string, string, string> t)
    {
        SearchHeading.Text = t("Search", "搜索信息");
        CandidatesHeading.Text = t("Candidates", "候选项");
        LyricsHeading.Text = t("Lyrics", "歌词");
    }
}
