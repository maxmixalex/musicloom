using Microsoft.UI.Xaml;

namespace MusicLoom.App;

internal sealed record LyricDisplayLine(
    string Original,
    string? Translation,
    bool ShowTranslation)
{
    public Visibility TranslationVisibility =>
        ShowTranslation
        && !string.IsNullOrWhiteSpace(Translation)
            ? Visibility.Visible
            : Visibility.Collapsed;
}
