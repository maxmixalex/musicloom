using Microsoft.UI.Xaml.Controls;
using MusicLoom.Core.Lyrics;
using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

/// <summary>
/// An adapter around the same ProviderMatchTabView as NetEase. QQ/KuGou
/// records are durable, independent, and never inferred from the local track.
/// </summary>
public sealed partial class MainWindow
{
    private int _providerMatchLoadVersion;
    private readonly HashSet<string> _pendingProviderArtwork =
        new(StringComparer.OrdinalIgnoreCase);
    private StoredProviderMatch? _visibleQqMatch;
    private StoredProviderMatch? _visibleKugouMatch;
    private StoredProviderMatch? _visibleMiguMatch;

    private void ClearCloudMatchPanels()
    {
        ++_providerMatchLoadVersion;
        _visibleQqMatch = null;
        _visibleKugouMatch = null;
        _visibleMiguMatch = null;
        QqMatchPanel.ShowCloudState(null, selected: false, busy: _busy);
        KugouMatchPanel.ShowCloudState(null, selected: false, busy: _busy);
        MiguMatchPanel.ShowCloudState(null, selected: false, busy: _busy);
    }

    private void RenderCloudMatchPanels()
    {
        bool selected = _selectedLibraryItem is not null;
        QqMatchPanel.ShowCloudState(_visibleQqMatch, selected, _busy);
        KugouMatchPanel.ShowCloudState(_visibleKugouMatch, selected, _busy);
        MiguMatchPanel.ShowCloudState(_visibleMiguMatch, selected, _busy);
    }

    private async Task RefreshCloudMatchTabsAsync(LibraryDisplayItem item)
    {
        int version = ++_providerMatchLoadVersion;
        try
        {
            SqliteProviderMatchStore matches = new(DatabasePath);
            // On first use both lookups would otherwise concurrently
            // attempt CREATE TABLE on the same SQLite database.
            StoredProviderMatch? qq = await matches.GetAsync(
                item.Entry.SourcePath, CloudLyricsSource.QqMusic);
            StoredProviderMatch? kugou = await matches.GetAsync(
                item.Entry.SourcePath, CloudLyricsSource.KugouMusic);
            StoredProviderMatch? migu = await matches.GetAsync(
                item.Entry.SourcePath, CloudLyricsSource.MiguMusic);
            if (version != _providerMatchLoadVersion
                || !ReferenceEquals(_selectedLibraryItem, item))
                return;

            _visibleQqMatch = qq;
            _visibleKugouMatch = kugou;
            _visibleMiguMatch = migu;
            RenderCloudMatchPanels();
            _ = RestoreMissingCloudArtworkAsync(item, qq);
            _ = RestoreMissingCloudArtworkAsync(item, kugou);
            _ = RestoreMissingCloudArtworkAsync(item, migu);
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not load QQ/KuGou matching records.", exception);
            if (version == _providerMatchLoadVersion
                && ReferenceEquals(_selectedLibraryItem, item))
            {
                _visibleQqMatch = null;
                _visibleKugouMatch = null;
                RenderCloudMatchPanels();
            }
        }
    }

    private async Task RestoreMissingCloudArtworkAsync(
        LibraryDisplayItem item, StoredProviderMatch? stored)
    {
        if (stored?.Candidate is not CloudLyricsCandidate candidate
            || stored.Suppressed
            || !string.IsNullOrWhiteSpace(candidate.CoverUrl))
            return;
        string requestKey = item.Entry.SourcePath + "\u001F"
            + (int)candidate.Source + "\u001F" + candidate.Id;
        if (!_pendingProviderArtwork.Add(requestKey)) return;
        try
        {
            using CloudLyricsClient client = new();
            string? artwork = await client.ResolveArtworkAsync(candidate);
            if (artwork is null && candidate.Source == CloudLyricsSource.MiguMusic)
            {
                CloudLyricsCandidate enriched =
                    await client.EnrichMiguCandidateAsync(candidate);
                artwork = await client.ResolveArtworkAsync(enriched);
            }
            if (artwork is null && (candidate.Source is
                CloudLyricsSource.QqMusic or CloudLyricsSource.KugouMusic)
                && !string.IsNullOrWhiteSpace(candidate.Title))
            {
                // Older saved rows may have no album MID or album ID.
                // Match the *same provider song identifier*, not merely
                // a similarly titled release or a local library image.
                IReadOnlyList<CloudLyricsCandidate> results =
                    await client.SearchAsync(candidate.Source,
                        candidate.Artist + " " + candidate.Title);
                CloudLyricsCandidate? same = results.FirstOrDefault(found =>
                    candidate.Source == CloudLyricsSource.QqMusic
                        ? !string.IsNullOrWhiteSpace(candidate.SongMid)
                          && string.Equals(found.SongMid, candidate.SongMid,
                              StringComparison.Ordinal)
                        : !string.IsNullOrWhiteSpace(candidate.Hash)
                          && string.Equals(found.Hash, candidate.Hash,
                              StringComparison.OrdinalIgnoreCase));
                if (same is not null)
                    artwork = same.CoverUrl
                        ?? await client.ResolveArtworkAsync(same);
            }
            if (artwork is null) return;

            SqliteProviderMatchStore store = new(DatabasePath);
            if (!await store.FillMissingCoverAsync(item.Entry.SourcePath,
                    candidate.Source, candidate.Id, artwork))
                return;
            if (!ReferenceEquals(_selectedLibraryItem, item)) return;
            StoredProviderMatch? current = candidate.Source switch
            {
                CloudLyricsSource.QqMusic => _visibleQqMatch,
                CloudLyricsSource.KugouMusic => _visibleKugouMatch,
                CloudLyricsSource.MiguMusic => _visibleMiguMatch,
                _ => null,
            };
            if (current?.Candidate is not CloudLyricsCandidate displayed
                || current.Suppressed || displayed.Id != candidate.Id)
                return;
            StoredProviderMatch hydrated = current with
            {
                Candidate = displayed with { CoverUrl = artwork },
            };
            switch (candidate.Source)
            {
                case CloudLyricsSource.QqMusic:
                    _visibleQqMatch = hydrated;
                    break;
                case CloudLyricsSource.KugouMusic:
                    _visibleKugouMatch = hydrated;
                    break;
                case CloudLyricsSource.MiguMusic:
                    _visibleMiguMatch = hydrated;
                    break;
            }
            RenderCloudMatchPanels();
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not recover selected provider album artwork.", error);
        }
        finally { _pendingProviderArtwork.Remove(requestKey); }
    }

    private async Task ConfirmCloudMatchAsync(CloudLyricsSource provider)
    {
        if (_busy || _selectedLibraryItem is not LibraryDisplayItem item)
            return;

        try
        {
            SqliteProviderMatchStore matches = new(DatabasePath);
            StoredProviderMatch? current =
                await matches.GetAsync(item.Entry.SourcePath, provider);
            if (current?.IsMatched != true || current.Confirmed)
                return;

            await matches.ConfirmAsync(item.Entry.SourcePath, provider);
            InvalidateLibraryFacetSnapshot();
            if (ReferenceEquals(item, _selectedLibraryItem))
                await RefreshCloudMatchTabsAsync(item);
        }
        catch (Exception error)
        {
            HandleUiException(
                L("Could not confirm match.", "无法确认匹配。"), error);
        }
    }

    private static bool IsProviderOwnedLyrics(
        LibraryLyricsRecord canonical, CloudLyricsSource provider)
    {
        string prefix = CloudLyricsSourceNames.Slug(provider) + ":";
        return canonical.SourceSignature.StartsWith(
                "manual-" + prefix, StringComparison.OrdinalIgnoreCase)
            || canonical.SourceSignature.StartsWith(
                "auto-" + prefix, StringComparison.OrdinalIgnoreCase);
    }

    private async Task SuppressCloudMatchAsync(CloudLyricsSource provider)
    {
        if (_busy || _selectedLibraryItem is not LibraryDisplayItem item)
            return;

        try
        {
            SqliteProviderMatchStore matches = new(DatabasePath);
            StoredProviderMatch? existing =
                await matches.GetAsync(item.Entry.SourcePath, provider);
            if (existing?.Suppressed == true)
                return;

            // The durable explicit no-match marker is provider-specific.
            // It prevents a later metadata auto-match from filling it again.
            await matches.SuppressAsync(item.Entry.SourcePath, provider);
            InvalidateLibraryFacetSnapshot();

            SqliteLyricsCache cache = new(DatabasePath);
            LibraryLyricsRecord? canonical =
                await cache.GetLibraryAsync(item.Entry.SourcePath);
            if (canonical is not null
                && IsProviderOwnedLyrics(canonical, provider))
            {
                // Never remove an embedded/local/manual lyric from an
                // unrelated provider. The sync pipeline owns output files.
                await cache.SaveLibraryAsync(canonical with
                {
                    Origin = LibraryLyricsOrigin.None,
                    Text = null,
                    SongId = null,
                    SourceSignature = "provider-match-cleared",
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await QueueLyricOutputSyncAsync(item);
                if (ReferenceEquals(item, _selectedLibraryItem))
                    await ShowTrackDetailsAsync(item);
            }

            if (ReferenceEquals(item, _selectedLibraryItem))
                await RefreshCloudMatchTabsAsync(item);
            ShowInfo(InfoBarSeverity.Success,
                L("Match cleared", "匹配已置空"),
                L("The selected provider's match is now empty.",
                  "已置空当前音源的匹配。"));
        }
        catch (Exception error)
        {
            HandleUiException(
                L("Could not clear match.", "无法置空匹配。"), error);
        }
    }

    private async Task RefreshCloudMatchLyricsAsync(CloudLyricsSource provider)
    {
        if (_busy || _selectedLibraryItem is not LibraryDisplayItem item)
            return;

        try
        {
            SqliteProviderMatchStore matches = new(DatabasePath);
            StoredProviderMatch? record =
                await matches.GetAsync(item.Entry.SourcePath, provider);
            if (record?.Candidate is not CloudLyricsCandidate selected
                || record.Suppressed)
                return;

            using CloudLyricsClient client = new();
            LrcDocument? lyrics = await client.GetLyricsAsync(selected);
            if (lyrics is not { Lines.Count: > 0 })
            {
                ShowInfo(InfoBarSeverity.Warning,
                    L("No lyrics", "没有可用歌词"),
                    L("This provider did not return timed lyrics for the matched song.",
                      "当前音源没有返回该匹配歌曲的时间轴歌词。"));
                return;
            }

            // Explicit Update uses the matched provider's actual song ID;
            // output files remain unchanged until the next sync.
            await SaveManualLyricsAsync(item, new LibraryLyricsRecord(
                item.Entry.SourcePath,
                LibraryLyricsOrigin.ManualEdited,
                lyrics.ToString(),
                null,
                "manual-" + CloudLyricsSourceNames.Slug(provider)
                    + ":" + selected.Id,
                DateTimeOffset.UtcNow));

            if (ReferenceEquals(item, _selectedLibraryItem))
            {
                await ShowTrackDetailsAsync(item);
                await RefreshCloudMatchTabsAsync(item);
            }
            ShowInfo(InfoBarSeverity.Success,
                L("Lyrics updated", "歌词已更新"),
                L("The lyrics have been saved. Synchronize to update destination files.",
                  "歌词已保存，同步后才会更新目标文件。"));
        }
        catch (Exception error)
        {
            HandleUiException(
                L("Could not refresh matched lyrics.", "无法更新匹配歌词。"),
                error);
        }
    }
}
