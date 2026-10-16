using Microsoft.UI.Xaml.Controls;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.State;
using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

/// <summary>
/// Four composable library facets. Lyrics and provider matches come from
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

    private static string FacetValue(ComboBox? box) =>
        (box?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";

    private bool AnyLibraryFacetActive() =>
        FacetValue(LibraryLyricsFacetBox) != "all"
        || FacetValue(LibraryNeteaseFacetBox) != "all"
        || FacetValue(LibraryQqFacetBox) != "all"
        || FacetValue(LibraryKugouFacetBox) != "all";

    private void EnsureLibraryFacetSnapshot()
    {
        if (_libraryFacetLoading || _allLibraryItems.Count == 0
            || !AnyLibraryFacetActive())
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
            _libraryFacetLyrics = lyrics;
            _libraryFacetMatches = matches;
            _libraryFacetLoaded = true;
            // Keep the selection when the snapshot arrives asynchronously.
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
                "empty" => lyric?.Origin == LibraryLyricsOrigin.ManualEmpty,
                "missing" => lyric?.HasLyrics != true
                    && lyric?.Origin != LibraryLyricsOrigin.ManualEmpty,
                _ => true,
            };
            if (!result) return false;
        }

        if (!MatchesNeteaseFacet(path, FacetValue(LibraryNeteaseFacetBox)))
            return false;
        return MatchesProviderFacet(path, CloudLyricsSource.QqMusic,
                FacetValue(LibraryQqFacetBox))
            && MatchesProviderFacet(path, CloudLyricsSource.KugouMusic,
                FacetValue(LibraryKugouFacetBox));
    }

    private bool MatchesNeteaseFacet(string path, string filter)
    {
        if (filter == "all") return true;
        TrackState? state = null;
        if (_state is not null)
            _state.Tracks.TryGetValue(path, out state);
        bool suppressed = state?.NeteaseMatchSuppressed == true;
        bool matched = state?.NeteaseSongId is not null && !suppressed;
        return filter switch
        {
            "matched" => matched,
            "confirmed" => matched && state?.NeteaseMatchConfirmed == true,
            "unconfirmed" => matched && state?.NeteaseMatchConfirmed != true,
            "missing" => !matched && !suppressed,
            "empty" => suppressed,
            _ => true,
        };
    }

    private bool MatchesProviderFacet(string path,
        CloudLyricsSource source, string filter)
    {
        if (filter == "all") return true;
        StoredProviderMatch? match = null;
        if (_libraryFacetMatches.TryGetValue(path, out var providers))
            providers.TryGetValue(source, out match);
        return filter switch
        {
            "matched" => match?.IsMatched == true,
            "confirmed" => match?.IsMatched == true && match.Confirmed,
            "unconfirmed" => match?.IsMatched == true && !match.Confirmed,
            "missing" => match?.IsMatched != true && match?.Suppressed != true,
            "empty" => match?.Suppressed == true,
            _ => true,
        };
    }

    private void LocalizeLibraryFacetBoxes()
    {
        Set(LibraryLyricsFacetBox, "Lyrics", "歌词");
        Set(LibraryNeteaseFacetBox, "NetEase", "网易云");
        Set(LibraryQqFacetBox, "QQ Music", "QQ音乐");
        Set(LibraryKugouFacetBox, "KuGou", "酷狗");
        void Set(ComboBox box, string en, string zh)
        {
            foreach (ComboBoxItem choice in box.Items.OfType<ComboBoxItem>())
            {
                string key = choice.Tag?.ToString() ?? "all";
                string value = key switch
                {
                    "all" => L("All", "全部"),
                    "present" => L("Available", "有歌词"),
                    "missing" => ReferenceEquals(box, LibraryLyricsFacetBox)
                        ? L("Missing", "无歌词") : L("Unmatched", "未匹配"),
                    "empty" => L("Explicitly empty", "已置空"),
                    "matched" => L("Matched", "已匹配"),
                    "confirmed" => L("Confirmed", "已确认"),
                    "unconfirmed" => L("Needs confirmation", "待确认"),
                    _ => key,
                };
                choice.Content = L(en, zh) + " · " + value;
            }
        }
    }
}
