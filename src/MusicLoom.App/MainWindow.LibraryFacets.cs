using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Core.Synchronization;
using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

/// <summary>
/// Anonymous provider library facets. Lyrics and matches come from
/// one bulk SQLite snapshot, independently of the displayed sync status.
/// </summary>
public sealed partial class MainWindow
{
    private Dictionary<string, LibraryLyricsRecord> _libraryFacetLyrics =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Dictionary<CloudLyricsSource, StoredProviderMatch>>
        _libraryFacetMatches = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _libraryFacetFetchedAt = DateTimeOffset.MinValue;
    private int _libraryFacetItemCount = -1;
    private bool _libraryFacetLoading;
    private bool _libraryFacetLoaded;
    private bool _libraryFacetRefreshPending;

    private void LibrarySearchButton_Click(object sender, RoutedEventArgs args) =>
        LibrarySearchBox.Focus(FocusState.Programmatic);

    private void LibraryClearFacetsButton_Click(
        object sender, RoutedEventArgs e)
    {
        bool previous = _refreshingLocalizedChoices;
        _refreshingLocalizedChoices = true;
        try
        {
            LibraryFilterBox.SelectedIndex = 0;
            LibraryLyricsFacetBox.SelectedIndex = 0;
            LibraryNeteaseFacetBox.SelectedIndex = 0;
            LibraryQqFacetBox.SelectedIndex = 0;
            LibraryKugouFacetBox.SelectedIndex = 0;
            LibraryMiguFacetBox.SelectedIndex = 0;
        }
        finally
        {
            _refreshingLocalizedChoices = previous;
        }
        ApplyLibraryFilter();
    }

    private void UpdateLibraryToolbarPresentation()
    {
        if (LibraryFacetsButton is null || LibrarySortMenuButton is null
            || LibraryLyricsFacetBox is null)
            return;

        ComboBox[] facets =
        [
            LibraryFilterBox, LibraryLyricsFacetBox, LibraryNeteaseFacetBox,
            LibraryQqFacetBox, LibraryKugouFacetBox,
            LibraryMiguFacetBox,
        ];
        var chosen = facets
            .Select(static box => box.SelectedItem as ComboBoxItem)
            .Where(static item => item is not null
                && item.Tag?.ToString() != "all")
            .Select(static item => item!.Content?.ToString() ?? "")
            .Where(static label => label.Length > 0)
            .ToArray();
        LibraryFacetsLabel.Text = chosen.Length == 0
            ? L("Filters", "筛选")
            : L($"Filters ({chosen.Length})", $"筛选（{chosen.Length}）");
        LibraryActiveFilterSummary.Visibility = chosen.Length == 0
            ? Visibility.Collapsed : Visibility.Visible;
        LibraryActiveFilterSummary.Text = string.Join(
            IsChinese ? "，" : ", ", chosen);

        string field =
            (LibrarySortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            switch
            {
                "title" => L("Title", "歌曲名"),
                "album" => L("Album", "专辑"),
                _ => L("Artist", "艺术家"),
            };
        bool descending =
            (LibrarySortDirectionBox.SelectedItem as ComboBoxItem)
                ?.Tag?.ToString() == "desc";
        // Real Segoe Fluent Icons, never Unicode arrow characters or a
        // redundant textual "Sort:" label.
        LibrarySortFieldText.Text = field;
        LibrarySortAscendingHost.Visibility = descending
            ? Visibility.Collapsed : Visibility.Visible;
        LibrarySortDescendingHost.Visibility = descending
            ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(LibrarySortMenuButton,
            L("Sort songs by ", "歌曲排序：") + field
            + (descending ? L(" descending", "，倒序")
                          : L(" ascending", "，顺序")));

        LibraryFacetPanelHeading.Text = L("Filter by", "筛选条件");
        LibrarySortPanelHeading.Text = L("Sort songs", "歌曲排序");
        LibraryStatusFacetLabel.Text = L("Synchronization", "同步状态");
        LibraryLyricsFacetLabel.Text = L("Lyrics", "歌词状态");
        LibraryNeteaseFacetLabel.Text = L("NetEase Music", "网易云匹配");
        LibraryQqFacetLabel.Text = L("QQ Music", "QQ音乐匹配");
        LibraryKugouFacetLabel.Text = L("KuGou Music", "酷狗音乐匹配");
        LibraryMiguFacetLabel.Text = L("Migu Music", "咪咕音乐匹配");
        LibraryClearFacetsButton.Content = L("Clear filters", "清除筛选");
    }

    private static string FacetValue(ComboBox? box) =>
        (box?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";

    private bool AnyLibraryFacetActive() =>
        FacetValue(LibraryLyricsFacetBox) != "all"
        || FacetValue(LibraryNeteaseFacetBox) != "all"
        || FacetValue(LibraryQqFacetBox) != "all"
        || FacetValue(LibraryKugouFacetBox) != "all"
        || FacetValue(LibraryMiguFacetBox) != "all";

    private void EnsureLibraryFacetSnapshot()
    {
        // Warnings must be visible even with NO provider facet selected:
        // auto matches are an independent source of the library status icon.
        if (_libraryFacetLoading || _allLibraryItems.Count == 0)
            return;
        if (_libraryFacetItemCount == _allLibraryItems.Count
            && DateTimeOffset.UtcNow - _libraryFacetFetchedAt
                < TimeSpan.FromSeconds(7))
            return;
        _libraryFacetFetchedAt = DateTimeOffset.UtcNow;
        _libraryFacetItemCount = _allLibraryItems.Count;
        _ = LoadLibraryFacetSnapshotAsync();
    }

    private void InvalidateLibraryFacetSnapshot()
    {
        _libraryFacetFetchedAt = DateTimeOffset.MinValue;
        _libraryFacetItemCount = -1;
        // Do not hide the selected track using stale provider/lyric rows
        // while an explicit match or lyric change is being reloaded.
        _libraryFacetLoaded = false;
        if (_libraryFacetLoading)
        {
            // A match/lyric change may arrive while the two SQLite
            // snapshots are being read; never publish those stale values
            // as the final state.
            _libraryFacetRefreshPending = true;
        }
        else
        {
            EnsureLibraryFacetSnapshot();
        }
    }

    private async Task LoadLibraryFacetSnapshotAsync()
    {
        _libraryFacetLoading = true;
        try
        {
            Dictionary<string, LibraryLyricsRecord> lyrics =
                await new SqliteLyricsCache(DatabasePath)
                    .LoadLibrarySnapshotAsync();
            var matches = await new SqliteProviderMatchStore(DatabasePath)
                .LoadSnapshotAsync();
            if (_libraryFacetRefreshPending)
                return; // A newer mutation invalidated this read mid-flight.
            _libraryFacetLyrics = lyrics;
            _libraryFacetMatches = matches;
            _libraryFacetLoaded = true;
            // Preserve the existing ListView objects and song selection.
            // Only status and counters change after provider review is known.
            RefreshProviderReviewStatuses();
            if (LibraryLyricsFacetBox is not null)
                ApplyLibraryFilter();
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException("Could not read library facets.", error);
        }
        finally
        {
            _libraryFacetLoading = false;
            if (_libraryFacetRefreshPending)
            {
                _libraryFacetRefreshPending = false;
                EnsureLibraryFacetSnapshot();
            }
        }
    }

    private void RefreshProviderReviewStatuses()
    {
        foreach (LibraryDisplayItem item in _allLibraryItems)
        {
            // A real sync error or in-progress transfer outranks review.
            if (item.Kind is "error" or "syncing")
                continue;
            _libraryFacetMatches.TryGetValue(item.Entry.SourcePath,
                out Dictionary<CloudLyricsSource, StoredProviderMatch>? providers);
            string kind = LibraryMatchStatusResolver.GetKind(
                item.Entry, _state, providers);
            // A manually queued output change remains pending when there
            // are no review warnings. Never advertise that output was synced.
            if (kind == "synced"
                && _manualPendingSources.Contains(item.Entry.SourcePath))
                kind = "pending";
            if (item.Kind != kind)
                item.SetStatus(kind, StatusLabel(kind));
        }
        RefreshLiveSyncCounters();
        if (_selectedLibraryItem is LibraryDisplayItem selected)
            RefreshTrackSyncTab(selected);
    }

    private string? GetProviderReviewDescription(PlanEntry entry)
    {
        if (!_libraryFacetLoaded
            || !_libraryFacetMatches.TryGetValue(entry.SourcePath,
                out Dictionary<CloudLyricsSource, StoredProviderMatch>? rows))
            return null;
        string[] review = rows.Values
            .Where(match => ProviderMatchReviewPolicy.NeedsReview(entry, match))
            .Select(match => match.Provider switch
            {
                CloudLyricsSource.QqMusic => L("QQ Music", "QQ音乐"),
                CloudLyricsSource.KugouMusic => L("KuGou Music", "酷狗音乐"),
                CloudLyricsSource.MiguMusic => L("Migu Music", "咪咕音乐"),
                _ => match.Provider.ToString(),
            }).Distinct().ToArray();
        return review.Length == 0 ? null
            : L("Review required for ", "以下自动匹配需要确认：")
                + string.Join(IsChinese ? "、" : ", ", review)
                + L(". The source album, title or artist differs from the provider; "
                    + "the music file and destination sync state are unchanged.",
                    "。歌曲名、艺术家或专辑信息存在差异；音乐文件和实际同步结果均未改变。");
    }

    private bool MatchesLibraryFacets(LibraryDisplayItem item)
    {
        // On the first selection, display existing rows until the bulk DB
        // snapshot resolves instead of temporarily reporting everything
        // unmatched and tearing down the currently selected detail pane.
        if (!_libraryFacetLoaded) return true;
        string lyricFilter = FacetValue(LibraryLyricsFacetBox);
        string path = item.Entry.SourcePath;
        if (lyricFilter != "all")
        {
            _libraryFacetLyrics.TryGetValue(path, out LibraryLyricsRecord? lyric);
            bool result = lyricFilter switch
            {
                "present" => lyric?.HasLyrics == true,
                // ManualEmpty is still a song without lyrics; clearing
                // lyrics is a content operation, not a provider match state.
                "missing" => lyric?.HasLyrics != true,
                _ => true,
            };
            if (!result) return false;
        }

        if (!MatchesNeteaseFacet(item.Entry,
                FacetValue(LibraryNeteaseFacetBox)))
            return false;
        return MatchesProviderFacet(item.Entry, CloudLyricsSource.QqMusic,
                FacetValue(LibraryQqFacetBox))
            && MatchesProviderFacet(item.Entry, CloudLyricsSource.KugouMusic,
                FacetValue(LibraryKugouFacetBox))
            && MatchesProviderFacet(item.Entry, CloudLyricsSource.MiguMusic,
                FacetValue(LibraryMiguFacetBox));

    }

    private bool MatchesNeteaseFacet(PlanEntry entry, string filter)
    {
        if (filter == "all") return true;
        TrackState? state = null;
        if (_state is not null)
            _state.Tracks.TryGetValue(entry.SourcePath, out state);
        bool suppressed = state?.NeteaseMatchSuppressed == true;
        bool matched = state?.NeteaseSongId is not null && !suppressed;
        bool needsReview = matched && state is not null
            && SyncExecutor.DetectMatchWarning(entry, state) is not null;
        return filter switch
        {
            "matched" => matched,
            "confirmed" => matched && !needsReview,
            "unconfirmed" => needsReview,
            "missing" => !matched && !suppressed,
            "empty" => suppressed,
            _ => true,
        };
    }

    private bool MatchesProviderFacet(PlanEntry entry,
        CloudLyricsSource source, string filter)
    {
        if (filter == "all") return true;
        StoredProviderMatch? match = null;
        if (_libraryFacetMatches.TryGetValue(entry.SourcePath, out var providers))
            providers.TryGetValue(source, out match);
        bool needsReview = ProviderMatchReviewPolicy.NeedsReview(entry, match);
        return filter switch
        {
            "matched" => match?.IsMatched == true,
            "confirmed" => match?.IsMatched == true && !needsReview,
            "unconfirmed" => needsReview,
            "missing" => match?.IsMatched != true && match?.Suppressed != true,
            "empty" => match?.Suppressed == true,
            _ => true,
        };
    }

    private void LocalizeLibraryFacetBoxes()
    {
        ToolTipService.SetToolTip(LibrarySearchButton,
            L("Focus library search", "搜索音乐库"));
        LocalizeLyricsFacet();
        LocalizeProviderFacet(LibraryNeteaseFacetBox, "NetEase", "网易云");
        LocalizeProviderFacet(LibraryQqFacetBox, "QQ Music", "QQ音乐");
        LocalizeProviderFacet(LibraryKugouFacetBox, "KuGou Music", "酷狗音乐");
        LocalizeProviderFacet(LibraryMiguFacetBox, "Migu Music", "咪咕音乐");

        void LocalizeLyricsFacet()
        {
            foreach (ComboBoxItem choice in LibraryLyricsFacetBox.Items
                .OfType<ComboBoxItem>())
            {
                choice.Content = (choice.Tag?.ToString()) switch
                {
                    "present" => L("Has lyrics", "有歌词"),
                    "missing" => L("No lyrics", "无歌词"),
                    _ => L("All lyric statuses", "全部歌词状态"),
                };
            }
        }

        void LocalizeProviderFacet(ComboBox box, string english, string chinese)
        {
            foreach (ComboBoxItem choice in box.Items.OfType<ComboBoxItem>())
            {
                choice.Content = (choice.Tag?.ToString()) switch
                {
                    "matched" => L($"{english} matched", $"{chinese}已匹配"),
                    "confirmed" => L($"{english} needs no review", $"{chinese}无需确认"),
                    "unconfirmed" => L($"{english} needs review",
                                      $"{chinese}待确认"),
                    "missing" => L($"{english} not matched", $"{chinese}未匹配"),
                    "empty" => L($"{english} match cleared", $"{chinese}匹配已置空"),
                    _ => L($"All {english} matches", $"全部{chinese}匹配"),
                };
            }
        }
    }
}
