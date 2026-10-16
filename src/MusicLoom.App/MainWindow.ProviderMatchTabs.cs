using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

/// <summary>
/// Adapt the pre-existing NetEase logic to the same tab control as QQ/KuGou.
/// Legacy NetEase state, warnings and matching operations remain unchanged.
/// </summary>
public sealed partial class MainWindow
{
    private Button OpenMatchDialogButton => NeteaseMatchPanel.FindButton;
    private Button ConfirmMatchButton => NeteaseMatchPanel.ConfirmButton;
    private Button SuppressMatchButton => NeteaseMatchPanel.ClearButton;
    private Button RefreshMatchedLyricsButton =>
        NeteaseMatchPanel.UpdateButton;

    private void InitializeProviderMatchTabs()
    {
        NeteaseMatchPanel.SearchRequested += async (_, _) =>
        {
            if (_selectedLibraryItem is LibraryDisplayItem selected)
                await ShowMatchDialogAsync(selected);
        };
        NeteaseMatchPanel.ConfirmRequested += async (_, _) =>
        {
            if (_selectedLibraryItem is LibraryDisplayItem selected)
                await ConfirmNeteaseMatchAsync(selected);
        };
        NeteaseMatchPanel.ClearRequested += async (_, _) =>
        {
            if (_selectedLibraryItem is LibraryDisplayItem selected)
                await SuppressNeteaseMatchAsync(selected);
        };
        NeteaseMatchPanel.UpdateRequested += async (_, _) =>
        {
            if (_selectedLibraryItem is LibraryDisplayItem selected
                && HasCachedMatch(selected.Entry.SourcePath))
                await SynchronizeMatchedLyricsAsync([selected]);
        };

        QqMatchPanel.SearchRequested += async (_, _) =>
        {
            if (_selectedLibraryItem is LibraryDisplayItem item)
                await ShowCloudMatchDialogAsync(item, CloudLyricsSource.QqMusic);
        };
        QqMatchPanel.ConfirmRequested += async (_, _) =>
            await ConfirmCloudMatchAsync(CloudLyricsSource.QqMusic);
        QqMatchPanel.ClearRequested += async (_, _) =>
            await SuppressCloudMatchAsync(CloudLyricsSource.QqMusic);
        QqMatchPanel.UpdateRequested += async (_, _) =>
            await RefreshCloudMatchLyricsAsync(CloudLyricsSource.QqMusic);

        KugouMatchPanel.SearchRequested += async (_, _) =>
        {
            if (_selectedLibraryItem is LibraryDisplayItem item)
                await ShowCloudMatchDialogAsync(item, CloudLyricsSource.KugouMusic);
        };
        KugouMatchPanel.ConfirmRequested += async (_, _) =>
            await ConfirmCloudMatchAsync(CloudLyricsSource.KugouMusic);
        KugouMatchPanel.ClearRequested += async (_, _) =>
            await SuppressCloudMatchAsync(CloudLyricsSource.KugouMusic);
        KugouMatchPanel.UpdateRequested += async (_, _) =>
            await RefreshCloudMatchLyricsAsync(CloudLyricsSource.KugouMusic);

        WireExternalMatchTab(MiguMatchPanel, CloudLyricsSource.MiguMusic);
        WireExternalMatchTab(AppleMatchPanel, CloudLyricsSource.AppleMusic);
        WireExternalMatchTab(SpotifyMatchPanel, CloudLyricsSource.Spotify);
    }

    private void WireExternalMatchTab(
        ProviderMatchTabView panel, CloudLyricsSource provider)
    {
        // The SAME view/action semantics as QQ and KuGou, independent
        // match persistence. Only the data adapter changes by provider.
        panel.SearchRequested += async (_, _) =>
        {
            if (_selectedLibraryItem is LibraryDisplayItem item)
                await ShowCloudMatchDialogAsync(item, provider);
        };
        panel.ConfirmRequested += async (_, _) =>
            await ConfirmCloudMatchAsync(provider);
        panel.ClearRequested += async (_, _) =>
            await SuppressCloudMatchAsync(provider);
        panel.UpdateRequested += async (_, _) =>
            await RefreshCloudMatchLyricsAsync(provider);
    }
}
