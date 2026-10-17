using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.LyricsProviders;
using MusicLoom.Netease;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace MusicLoom.App;

/// <summary>
/// Executed ONLY when MUSICLOOM_MATCH_SMOKE_MARKER is explicitly set by CI.
/// Verifies real WinUI controls and the MainWindow NetEase adapter, not merely
/// a string/markup inspection. No songs or user database are written.
/// The screenshots are OFFLINE FIXTURES, not real provider artwork samples.
/// </summary>
public sealed partial class MainWindow
{
    internal async Task RunMatchUiSmokeAsync(string marker)
    {
        List<string> evidence = [];
        string failed = marker + ".failed";
        StackPanel? host = null;
        LibraryDisplayItem? previousSelection = _selectedLibraryItem;
        StateDocument? previousState = _state;
        Windows.Graphics.SizeInt32 previousWindowSize = AppWindow.Size;

        void Assert(bool condition, string description)
        {
            if (!condition)
                throw new InvalidOperationException(description);
            evidence.Add("PASS " + description);
        }

        try
        {
            Assert(PlayerSourceText.FontSize == PlayerTimeText.FontSize
                && PlayerSourceMetadata.MinHeight
                    == PlayerMainMetadata.MinHeight
                && PlayerSourceMetadata.MinHeight >= 38,
                "Player: source name and timestamp use identical typography and align vertically with track metadata");
            Assert(LibraryFacetsButton.Flyout is Flyout
                && LibrarySortMenuButton.Flyout is Flyout
                && LibraryLyricsFacetBox is not null
                && LibraryNeteaseFacetBox is not null
                && LibraryQqFacetBox is not null
                && LibraryKugouFacetBox is not null
                && LibraryMiguFacetBox is not null
                && LibraryAppleFacetBox is not null
                && LibrarySpotifyFacetBox is not null
                && LibraryFilterSyncingItem.Tag?.ToString() == "syncing",
                "Library: all statuses and six independent provider facets live under the same filter flyout");
            // Verify the actual THREE direct content regions of the
            // named toolbar Grid, not framework Parent/visual relationships
            // (which are not stable for unloaded WinUI navigation content).
            var toolbarChildren = LibraryToolbarRow.Children;
            bool directStructure = toolbarChildren.Count == 3
                && toolbarChildren[0] is Grid
                && toolbarChildren[1] is StackPanel
                && toolbarChildren[2] is StackPanel;
            bool searchRegion = directStructure
                && toolbarChildren[0] is Grid searchGrid
                && searchGrid.Children.Contains(LibrarySearchBox)
                && Grid.GetColumn(LibrarySearchBox) == 1;
            bool filterRegion = directStructure
                && toolbarChildren[1] is StackPanel toolbarActions
                && toolbarActions.Children.Contains(LibraryFacetsButton)
                && toolbarActions.Children.Contains(LibrarySortMenuButton)
                && Grid.GetColumn(toolbarActions) == 1;
            bool countsRegion = directStructure
                && toolbarChildren[2] is StackPanel toolbarCounts
                && toolbarCounts.Children.Contains(LibraryVisibleCountText)
                && Grid.GetColumn(toolbarCounts) == 2;
            Assert(LibraryToolbarRow.ColumnDefinitions.Count == 3
                && directStructure && searchRegion && filterRegion
                && countsRegion,
                "Library: one toolbar row with search, filter, sort and counters"
                + $" (columns={LibraryToolbarRow.ColumnDefinitions.Count},"
                + $" children={toolbarChildren.Count}, search={searchRegion},"
                + $" actions={filterRegion}, counts={countsRegion})");
            // Flyout elements are NOT loaded until opened: checking
            // TextBlock.Parent here produced a false negative in CI even
            // though the ComboBoxItem Content is present in the XAML.
            bool ascendingMarkup = LibrarySortAscendingItem.Content
                is StackPanel ascendingStack
                && ascendingStack.Children.Count == 2
                && ascendingStack.Children[0] is FontIcon
                    { Glyph: "\uEE68" }
                && ReferenceEquals(ascendingStack.Children[1],
                    LibrarySortAscendingLabel);
            bool descendingMarkup = LibrarySortDescendingItem.Content
                is StackPanel descendingStack
                && descendingStack.Children.Count == 2
                && descendingStack.Children[0] is FontIcon
                    { Glyph: "\uEE69" }
                && ReferenceEquals(descendingStack.Children[1],
                    LibrarySortDescendingLabel);
            Assert(LibrarySortGlyph.Glyph == "\uE8CB"
                && LibrarySortDirectionGlyph.Glyph is "\uEE68" or "\uEE69"
                && LibrarySortMenuButton.Content is StackPanel
                && ascendingMarkup && descendingMarkup,
                "Library: verified Segoe Fluent Sort/SortUp/SortDown glyphs"
                + $" (glyphs={((int)LibrarySortGlyph.Glyph[0]):X4}/"
                + $"{((int)LibrarySortDirectionGlyph.Glyph[0]):X4};"
                + $" asc={ascendingMarkup}; desc={descendingMarkup})");
            Assert(LyricsRomanizationToggle is not null
                && !new MusicLoom.Core.Configuration.LyricsSettings()
                    .IncludeRomanization,
                "Settings: sync romanization is explicitly opt-in and defaults off");
            // Real go-librespot audio is decoded out of process. The WinUI
            // MediaPlayer has no Source, but transport, timeline, source
            // label and hidden previous/next must still reflect playback.
            MusicLoom.ProviderPoc.SpotifyLocalStreamBridge fakeSpotify = new();
            CloudLyricsCandidate? previousCloud = _playingCloudPreviewCandidate;
            try
            {
                _spotifyLocalPolling = true; // prevent background HTTP
                _spotifyLocalBackend = fakeSpotify;
                _spotifyLocalTrack = new CloudLyricsCandidate(
                    CloudLyricsSource.Spotify,
                    "4iV5W9uYEdYUVa79Axb7Rh",
                    "SAT remote song", "Artist", "Album", 200000);
                _playingCloudPreviewCandidate = _spotifyLocalTrack;
                _spotifyLocalStatus =
                    new MusicLoom.ProviderPoc.SpotifyLocalPlayerStatus(
                        "spotify:track:4iV5W9uYEdYUVa79Axb7Rh",
                        false, false, false, 42000, 200000);
                _spotifyLocalStatusObservedAt = DateTimeOffset.UtcNow;
                UpdatePlayerProgress();
                UpdatePlayerVisualState();
                Assert(PlayerSourceText.Text == "Spotify"
                    && PlayerPlayPauseIcon.Symbol == Symbol.Pause
                    && PlayerPreviousButton.Visibility == Visibility.Collapsed
                    && PlayerNextButton.Visibility == Visibility.Collapsed
                    && Math.Abs(PlayerProgressSlider.Value - 42) < 1.1
                    && Math.Abs(PlayerProgressSlider.Maximum - 200) < 1,
                    "Player: Spotify locally decoded full song drives source label, progress, pause icon and hides remote navigation without MediaPlayer.Source");
            }
            finally
            {
                _spotifyLocalBackend = null;
                _spotifyLocalTrack = null;
                _spotifyLocalStatus = null;
                _spotifyLocalPolling = false;
                _playingCloudPreviewCandidate = previousCloud;
                fakeSpotify.Dispose();
                UpdatePlayerProgress();
                UpdatePlayerVisualState();
            }


            string? directory = Path.GetDirectoryName(marker);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            // CI-only: make the entire three-column matching dialog fit
            // inside the *visible* window before capturing it. Earlier
            // 686px hosts silently clipped the entire lyrics preview rail.
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 900));
            await Task.Delay(400);
            host = new StackPanel
            {
                Padding = new Thickness(18),
                Width = Math.Min(1220, Math.Max(600, AppRoot.ActualWidth - 30)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(
                    Color.FromArgb(255, 24, 29, 39)),
            };
            Grid.SetRow(host, 1);
            AppRoot.Children.Add(host);

            // Verify all three instantiate the actual NetEase-derived
            // XAML and all four buttons trigger the SAME event contract.
            (MatchProviderKind Kind, string Id, Uri Cover, Color Tint)[] fixtures =
            [
                (MatchProviderKind.Netease, "186016",
                    new Uri("http://p1.music.126.net/legacy-artwork.jpg"),
                    Color.FromArgb(255, 73, 139, 205)),
                (MatchProviderKind.QqMusic, "29001",
                    new Uri("https://y.gtimg.cn/music/photo_new/T002R300x300M000003lghpv0jfFXG.jpg"),
                    Color.FromArgb(255, 180, 95, 161)),
                (MatchProviderKind.KugouMusic, "55002",
                    new Uri("https://imge.kugou.com/stdmusic/300/album.jpg"),
                    Color.FromArgb(255, 95, 166, 131)),
                (MatchProviderKind.MiguMusic, "600929000001520013",
                    new Uri("https://d.musicapp.migu.cn/cover.jpg"),
                    Color.FromArgb(255, 84, 161, 189)),
                (MatchProviderKind.AppleMusic, "123456",
                    new Uri("https://is1-ssl.mzstatic.com/cover.jpg"),
                    Color.FromArgb(255, 178, 111, 120)),
                (MatchProviderKind.Spotify, "4iV5W9uYEdYUVa79Axb7Rh",
                    new Uri("https://i.scdn.co/image/cover"),
                    Color.FromArgb(255, 92, 170, 102)),
            ];

            foreach (var fixture in fixtures)
            {
                var view = new ProviderMatchTabView();
                view.Localize((_, chinese) => chinese, fixture.Kind);
                host.Children.Clear();
                host.Children.Add(view);
                await Task.Delay(60);

                string provider = fixture.Kind.ToString();
                var empty = new MatchTabPresentation(
                    true, false, null,
                    "LOCAL SONG MUST NOT APPEAR",
                    "LOCAL ARTIST MUST NOT APPEAR",
                    "LOCAL ALBUM MUST NOT APPEAR",
                    null, null, false, false, false);
                view.Present(empty);
                Assert(view.ArtworkImage.Source is null
                    && view.ArtworkPlaceholder.Visibility == Visibility.Visible
                    && !view.TitleLabel.Text.Contains("LOCAL", StringComparison.Ordinal)
                    && view.ArtistLabel.Text.Length == 0
                    && view.AlbumLabel.Text.Length == 0
                    && view.FindButton.IsEnabled
                    && view.ClearButton.IsEnabled
                    && view.ConfirmButton.Visibility == Visibility.Collapsed
                    && !view.UpdateButton.IsEnabled,
                    provider + ": true empty state, no local metadata, NetEase button behavior");

                var matched = empty with
                {
                    Id = fixture.Id,
                    Title = provider + " Remote Match",
                    Artist = "Remote Artist",
                    Album = "Remote Album",
                    CoverUrl = fixture.Cover.AbsoluteUri,
                    NeedsConfirmation = true,
                    CanUpdateLyrics = true,
                };
                view.Present(matched);
                BitmapImage? original = view.ArtworkImage.Source as BitmapImage;
                Assert(original?.UriSource == fixture.Cover
                    && view.ArtworkPlaceholder.Visibility == Visibility.Collapsed
                    && view.TitleLabel.Text == matched.Title
                    && view.ArtistLabel.Text == matched.Artist
                    && view.AlbumLabel.Text == matched.Album
                    && view.IdLabel.Text == "#" + matched.Id
                    && view.ConfirmButton.IsEnabled
                    && view.ConfirmButton.Visibility == Visibility.Visible
                    && view.UpdateButton.IsEnabled,
                    provider + ": original NetEase match card and remote artwork visible");

                // Regression: reloading metadata with the same song ID and
                // a temporarily empty cover URL MUST keep the loaded image.
                view.Present(matched with { CoverUrl = null });
                Assert(ReferenceEquals(original, view.ArtworkImage.Source)
                    && view.ArtworkPlaceholder.Visibility == Visibility.Collapsed,
                    provider + ": refresh does not erase cached album cover");

                int find = 0, confirm = 0, clear = 0, update = 0;
                view.SearchRequested += (_, _) => find++;
                view.ConfirmRequested += (_, _) => confirm++;
                view.ClearRequested += (_, _) => clear++;
                view.UpdateRequested += (_, _) => update++;
                Invoke(view.FindButton);
                Invoke(view.ConfirmButton);
                Invoke(view.ClearButton);
                Invoke(view.UpdateButton);
                Assert(find == 1 && confirm == 1 && clear == 1 && update == 1,
                    provider + ": same four interactive buttons dispatch once each");

                view.Present(matched with { Id = fixture.Id + "9", CoverUrl = null });
                Assert(view.ArtworkImage.Source is null
                    && view.ArtworkPlaceholder.Visibility == Visibility.Visible,
                    provider + ": a different song discards the old artwork");

                view.Present(matched with
                {
                    Id = null, Suppressed = true, CoverUrl = null,
                    NeedsConfirmation = false, CanUpdateLyrics = false,
                });
                Assert(view.ArtworkImage.Source is null
                    && !view.ClearButton.IsEnabled
                    && view.ConfirmButton.Visibility == Visibility.Collapsed
                    && !view.UpdateButton.IsEnabled,
                    provider + ": explicit no-match disables inappropriate actions");

                // Render screenshots from actual WinUI panels, with a
                // deterministically generated local PNG. This tests the
                // pixels and XAML layer stacking but NOT network/CDN access.
                view.Present(matched with { NeedsConfirmation = false });
                view.ArtworkImage.Source = await FixtureArtworkAsync(fixture.Tint);
                view.ArtworkPlaceholder.Visibility = Visibility.Collapsed;
                await Task.Delay(85);
                string png = marker + "." + provider + ".png";
                await SavePanelScreenshotAsync(view, png);
                Assert(new FileInfo(png).Length > 1800,
                    provider + ": real WinUI matching panel rendered to PNG");
            }

            // QQ/KuGou/new-provider candidate lists must use the real
            // native ListViewItemPresenter selection indicator, rather
            // than just a highlight painted inside the candidate card.
            CloudLyricsPanel candidatePanel = new();
            Assert(candidatePanel.CandidateList.ItemContainerStyle?.Setters
                .OfType<Setter>().Any(setter =>
                    setter.Property == ListViewItem.TemplateProperty
                    && setter.Value is ControlTemplate) == true,
                "Cloud candidate lists apply the native selected-left-indicator ListViewItem template");
            candidatePanel.Dispose();

            // The previous evidence only rendered the provider DETAIL tabs.
            // It never exercised "Find XX match": render the actual search
            // dialog and confirm its candidate+lyric visual hierarchy.
            host.Children.Clear();
            LyricsSearchCandidateCard searchCard = new()
            {
                SongTitle = "Approved-style candidate",
                ArtistName = "Remote artist",
                AlbumName = "Remote album",
                DurationText = "#186016 · 3:42",
                ArtworkSource = await FixtureArtworkAsync(
                    Color.FromArgb(255, 73, 139, 205)),
                PreviewTooltip = "Play or pause preview",
            };
            searchCard.SetExternalLink(
                new Uri("https://music.163.com/#/song?id=186016"),
                "在网易云音乐中打开");
            int previewInvocations = 0;
            searchCard.PreviewRequested += (_, _) => previewInvocations++;
            ListView candidateResults = new()
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            candidateResults.Items.Add(new ListViewItem
            {
                Content = searchCard,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            });

            LyricsSearchPreviewView preview = new();
            preview.LyricsList.ItemTemplate = LyricsList.ItemTemplate;
            preview.LyricsList.ItemContainerStyle = LyricsList.ItemContainerStyle;
            preview.ItemsSource = new LyricDisplayLine[]
            {
                new LyricDisplayLine("First original line", "第一句翻译",
                    "preferOriginal", TimeSpan.FromSeconds(12)),
                new LyricDisplayLine("Second original line", "第二句翻译",
                    "preferOriginal", TimeSpan.FromSeconds(16)),
            };
            LyricsSearchDialogView search = new()
            {
                SearchContent = new LyricsSearchFormView(),
                CandidatesContent = candidateResults,
                LyricsContent = preview,
                FooterContent = new Button { Content = "使用所选" },
            };
            search.Localize((_, zh) => zh);
            host.Children.Add(search);
            await Task.Delay(140);

            Grid? cardGrid = searchCard.Content as Grid;
            Grid? actionsRail = searchCard.FindName("CandidateActions") as Grid;
            Assert(cardGrid is not null && actionsRail is not null
                && cardGrid.ColumnDefinitions.Count == 3
                && cardGrid.ColumnDefinitions[0].Width.Value == 52
                && (cardGrid.ActualWidth < 440
                    ? Grid.GetRow(actionsRail) == 1
                        && cardGrid.ColumnDefinitions[2].Width.Value == 0
                    : Grid.GetRow(actionsRail) == 0
                        && cardGrid.ColumnDefinitions[2].Width.Value is >= 125 and <= 220),
                "Search dialog: 52px NetEase artwork with actions stacked rather than clipped on narrow widths");
            Assert(ReferenceEquals(preview.LyricsList.ItemTemplate,
                    LyricsList.ItemTemplate)
                && ReferenceEquals(preview.LyricsList.ItemContainerStyle,
                    LyricsList.ItemContainerStyle),
                "Search dialog: exact library karaoke/translation ListView template and container style");
            Assert(search.Content is Grid,
                "Search dialog: real three-column shared XAML visual tree");
            Invoke(searchCard.FindName("PreviewButton") as Button
                ?? throw new InvalidOperationException("Preview button not found"));
            Assert(previewInvocations == 1,
                "Search dialog: actual candidate play control dispatches once");
            // Simulate a virtualized candidate control being reused as the
            // ListView scrolls. The hyperlink follows the new DataContext
            // instead of remaining tied to Loaded or an old provider result.
            CloudCandidateRow qq = new(
                new CloudLyricsCandidate(CloudLyricsSource.QqMusic, "123",
                    "QQ candidate", "Artist", "Album", 123000,
                    SongMid: "003Ngvnw2Ks69Z"),
                "Preview", "在QQ音乐中打开", "在QQ音乐中搜索");
            CloudCandidateRow kugou = new(
                new CloudLyricsCandidate(CloudLyricsSource.KugouMusic, "456",
                    "KuGou candidate", "Artist", "Album", 123000,
                    Hash: "CB7EE97F4CC11C4EA7A1FA4B516A5D97"),
                "Preview", "在酷狗音乐中打开", "在QQ音乐中搜索");
            LyricsSearchCandidateCard recycled = new()
            {
                Width = 340,
            };
            recycled.SetBinding(LyricsSearchCandidateCard.ExternalUrlProperty,
                new Binding { Path = new PropertyPath(nameof(CloudCandidateRow.ExternalUrl)) });
            recycled.SetBinding(LyricsSearchCandidateCard.ExternalLabelProperty,
                new Binding { Path = new PropertyPath(nameof(CloudCandidateRow.ExternalLabel)) });
            host.Children.Clear();
            host.Children.Add(recycled);
            recycled.DataContext = qq;
            await Task.Delay(70);
            HyperlinkButton providerLink = recycled.FindName("ExternalLinkButton")
                as HyperlinkButton
                ?? throw new InvalidOperationException("Provider link is missing");
            Assert(providerLink.Visibility == Visibility.Visible
                && providerLink.NavigateUri?.Host == "y.qq.com"
                && providerLink.NavigateUri.AbsolutePath.Contains("songDetail",
                    StringComparison.Ordinal),
                "Search result recycling: QQ song MID link resolves on DataContext bind");

            recycled.DataContext = kugou;
            await Task.Delay(70);
            Assert(providerLink.Visibility == Visibility.Visible
                && providerLink.NavigateUri?.Host == "www.kugou.com"
                && providerLink.NavigateUri.Fragment.Contains(kugou.Candidate.Hash!,
                    StringComparison.OrdinalIgnoreCase)
                && providerLink.Content is StackPanel linkContainer
                && linkContainer.Children[0] is TextBlock linkCaption
                && linkCaption.MaxWidth >= 130,
                "Search result recycling: compact KuGou song link stays full-width when row reused");
            CloudCandidateRow missingKugouHash = new(
                new CloudLyricsCandidate(CloudLyricsSource.KugouMusic,
                    "456", "Unrecognized hash", "Artist", "Album", 123000,
                    Hash: "HASHABC"),
                "Preview", "在酷狗音乐中打开", "在酷狗音乐中搜索");
            recycled.DataContext = missingKugouHash;
            await Task.Delay(70);
            Assert(providerLink.Visibility == Visibility.Visible
                && providerLink.NavigateUri?.AbsolutePath
                    == "/yy/html/search.html"
                && missingKugouHash.ExternalLabel == "在酷狗音乐中搜索",
                "Search result recycling: KuGou entries without valid hash retain an honest provider search link");
            CloudCandidateRow missingQqMid = new(
                new CloudLyricsCandidate(CloudLyricsSource.QqMusic,
                    "123", "Missing MID", "Artist", "Album", 123000),
                "Preview", "在QQ音乐中打开", "在QQ音乐中搜索");
            recycled.DataContext = missingQqMid;
            await Task.Delay(70);
            Assert(providerLink.Visibility == Visibility.Visible
                && providerLink.NavigateUri?.AbsolutePath
                    == "/n/ryqq/search"
                && missingQqMid.ExternalLabel == "在QQ音乐中搜索",
                "Search result recycling: QQ candidates without a valid song MID retain a search link");
            recycled.DataContext = null;
            await Task.Delay(70);
            Assert(providerLink.Visibility == Visibility.Collapsed
                && providerLink.NavigateUri is null,
                "Search result recycling: recycled empty row does not retain stale links");
            host.Children.Clear();
            host.Children.Add(search);
            await Task.Delay(70);

            string searchPng = marker + ".SearchDialog.png";
            await SavePanelScreenshotAsync(search, searchPng);
            double previewX = preview.TransformToVisual(search)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            double previewWidth = preview.ActualWidth;
            int previewPixels = await CountRenderedTextPixelsAsync(search,
                minX: (int)Math.Max(0, Math.Round(previewX + 45)));
            Assert(search.ActualWidth >= 780
                && previewWidth >= 180
                && previewX >= search.ActualWidth * 0.60
                && previewX + previewWidth <= search.ActualWidth + 2
                && previewPixels > 120,
                $"Search dialog: entire lyric column must be visible and painted (dialog={search.ActualWidth:0}, x={previewX:0}, width={previewWidth:0}, pixels={previewPixels})");
            Assert(new FileInfo(searchPng).Length > 1800,
                "Search dialog: actual WinUI candidate and bilingual lyric columns rendered");

            double originalHostWidth = host.Width;
            host.Width = 840; // another realistic non-maximized window
            await Task.Delay(160);
            double compactPreviewX = preview.TransformToVisual(search)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            Assert(search.ActualWidth is >= 770 and <= 840
                && preview.ActualWidth >= 170
                && compactPreviewX + preview.ActualWidth <= search.ActualWidth + 2
                && actionsRail is not null
                && Grid.GetRow(actionsRail) == 1,
                $"Search dialog: three panels and stacked actions remain visible at compact window width ({search.ActualWidth:0}px)");
            host.Width = originalHostWidth;
            await Task.Delay(110);

            // A fixed viewport must govern BOTH immersive columns. Long
            // titles, translated lyrics and the cover's natural dimensions
            // are not permitted to grow the player's overall footprint.
            host.Children.Clear();
            ImmersivePlayer immersive = new()
            {
                Width = 900,
                Height = 520,
            };
            host.Children.Add(immersive);
            immersive.SetTrack("Short title", "Artist", "Album", null,
                [new LyricDisplayLine("One", null, "preferOriginal")]);
            await Task.Delay(140);
            Border noTopFade = immersive.FindName("LyricsTopFade") as Border
                ?? throw new InvalidOperationException("Top fade not found");
            Border noBottomFade = immersive.FindName("LyricsBottomFade") as Border
                ?? throw new InvalidOperationException("Bottom fade not found");
            Assert(noTopFade.Visibility == Visibility.Collapsed
                && noBottomFade.Visibility == Visibility.Collapsed,
                "Immersive: no edge veils when one lyric fits without scrolling");
            Grid playerColumns = immersive.FindName("PlayerColumns") as Grid
                ?? throw new InvalidOperationException("Immersive columns not found");
            Grid leftColumn = immersive.FindName("LeftPanel") as Grid
                ?? throw new InvalidOperationException("Left panel not found");
            Grid rightColumn = immersive.FindName("RightPanel") as Grid
                ?? throw new InvalidOperationException("Right panel not found");
            double originalWidth = playerColumns.ActualWidth;
            double originalHeight = playerColumns.ActualHeight;
            Assert(originalWidth > 200 && originalHeight > 150
                && Math.Abs(leftColumn.ActualHeight - rightColumn.ActualHeight) < 1,
                "Immersive: equal-height viewport-proportional left and right columns");

            immersive.SetTrack(new string('L', 320), new string('A', 240),
                new string('B', 200), null,
                Enumerable.Range(0, 120).Select(i =>
                    new LyricDisplayLine(new string('X', 230),
                        new string('中', 160),
                        "preferOriginal", TimeSpan.FromSeconds(i))).ToArray());
            await Task.Delay(160);
            Assert(Math.Abs(playerColumns.ActualWidth - originalWidth) < 1
                && Math.Abs(playerColumns.ActualHeight - originalHeight) < 1
                && Math.Abs(leftColumn.ActualHeight - rightColumn.ActualHeight) < 1,
                "Immersive: 120 long translated lines cannot change container dimensions");
            Border upperFade = immersive.FindName("LyricsTopFade") as Border
                ?? throw new InvalidOperationException("Top viewport fade not found");
            Border lowerFade = immersive.FindName("LyricsBottomFade") as Border
                ?? throw new InvalidOperationException("Bottom viewport fade not found");
            Assert(!upperFade.IsHitTestVisible && !lowerFade.IsHitTestVisible
                && upperFade.Height > 0 && lowerFade.Height > 0,
                "Immersive: stationary, non-intercepting edge fades outside scrolling items");
            ScrollViewer lyricScroll = immersive.LyricsScrollViewer
                ?? throw new InvalidOperationException("Actual internal lyrics ScrollViewer missing");
            lyricScroll.ChangeView(null, 0, null, true);
            await Task.Delay(110);
            Assert(lyricScroll.ScrollableHeight > 10
                && upperFade.Visibility == Visibility.Collapsed
                && lowerFade.Visibility == Visibility.Visible,
                "Immersive: top lyric is crisp and only the bottom edge fades");
            // VirtualizingStackPanel estimates the scroll extent while
            // realizing the last hundred items. Changing to the FIRST
            // reported maximum does not necessarily reach the real tail.
            // Scroll until both the extent and offset converge, then test
            // the actual last line instead of testing an intermediate row.
            for (int attempt = 0; attempt < 16; attempt++)
            {
                lyricScroll.ChangeView(null, lyricScroll.ScrollableHeight,
                    null, disableAnimation: true);
                await Task.Delay(110);
                if (Math.Abs(lyricScroll.ScrollableHeight
                    - lyricScroll.VerticalOffset) < 1.5)
                    break;
            }
            double tailDistance = lyricScroll.ScrollableHeight
                - lyricScroll.VerticalOffset;
            Assert(tailDistance < 1.5
                && upperFade.Visibility == Visibility.Visible
                && lowerFade.Visibility == Visibility.Collapsed,
                $"Immersive: bottom lyric is crisp only when at the real end (offset={lyricScroll.VerticalOffset:0.#}, max={lyricScroll.ScrollableHeight:0.#}, distance={tailDistance:0.#}, top={upperFade.Visibility}, bottom={lowerFade.Visibility})");
            lyricScroll.ChangeView(null, 0, null, true);
            await Task.Delay(90);
            string immersivePng = marker + ".ImmersiveViewport.png";
            await SavePanelScreenshotAsync(immersive, immersivePng);
            double rightX = rightColumn.TransformToVisual(immersive)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            int rightPixels = await CountRenderedTextPixelsAsync(immersive,
                minX: (int)Math.Max(0, Math.Round(rightX + 40)));
            Assert(rightColumn.ActualWidth > 175
                && rightX + rightColumn.ActualWidth <= immersive.ActualWidth + 6
                && rightPixels > 120,
                $"Immersive: lyric panel must paint inside viewport (x={rightX:0}, width={rightColumn.ActualWidth:0}, viewport={immersive.ActualWidth:0}, pixels={rightPixels})");
            Assert(new FileInfo(immersivePng).Length > 1800,
                "Immersive: actual WinUI viewport with long bilingual lyrics rendered");

            StackPanel transport = immersive.FindName("TransportControls") as StackPanel
                ?? throw new InvalidOperationException("Transport footer not found");
            Button modeCycle = immersive.FindName("LyricModeButton") as Button
                ?? throw new InvalidOperationException("Mode cycling button not found");
            Slider playerSlider = immersive.FindName("ProgressSlider") as Slider
                ?? throw new InvalidOperationException("Immersive slider not found");
            TextBlock playerTime = immersive.FindName("PlaybackTime") as TextBlock
                ?? throw new InvalidOperationException("Player timestamp not found");
            immersive.SetNavigationAvailable(false, remoteSource: true);
            Button previous = immersive.FindName("PreviousButton") as Button
                ?? throw new InvalidOperationException("Previous immersive button not found");
            Button nextTransport = immersive.FindName("NextButton") as Button
                ?? throw new InvalidOperationException("Next immersive button not found");
            Assert(previous.Visibility == Visibility.Collapsed
                && nextTransport.Visibility == Visibility.Collapsed,
                "Immersive: remote audio hides previous/next rather than disabling visible buttons");
            immersive.SetNavigationAvailable(true, remoteSource: false);
            Assert(previous.Visibility == Visibility.Visible
                && nextTransport.Visibility == Visibility.Visible,
                "Immersive: local queue restores visible previous/next");

            Assert(transport.Parent is Grid footer
                && Grid.GetRow(footer) == 1
                && Grid.GetRow(playerTime) == 0
                && playerTime.HorizontalAlignment == HorizontalAlignment.Center
                && playerSlider.Height >= 36,
                "Immersive: controls in footer; centered time above non-clipping slider");
            string? originalMode = null;
            immersive.ModeChanged += selected => originalMode = selected;
            Invoke(modeCycle);
            Assert(originalMode == LyricsDisplayModes.PreferTranslation,
                "Immersive: three-state lyric display button cycles first mode");
            Invoke(modeCycle);
            Assert(originalMode == LyricsDisplayModes.OriginalOnly,
                "Immersive: three-state lyric display button cycles second mode");
            Invoke(modeCycle);
            Assert(originalMode == LyricsDisplayModes.PreferOriginal,
                "Immersive: three-state lyric display button wraps to preferred original");
            BitmapImage wideCover = await FixtureArtworkAsync(
                Color.FromArgb(255, 90, 130, 170), width: 120, height: 64);
            immersive.SetTrack("Wide artwork", "Artist", "Album", wideCover,
                [new LyricDisplayLine("Text", "译文", "preferOriginal")]);
            await Task.Delay(170);
            Border coverSurface = immersive.FindName("AlbumCoverSurface") as Border
                ?? throw new InvalidOperationException("Cover surface not found");
            Assert(coverSurface.Width > coverSurface.Height
                && coverSurface.CornerRadius.TopLeft > 0,
                "Immersive: rectangular cover has correctly bounded rounded/shadowed surface");
            immersive.Width = 650;
            immersive.Height = 430;
            await Task.Delay(130);
            TextBlock heading = immersive.FindName("TrackHeading") as TextBlock
                ?? throw new InvalidOperationException("Track heading not found");
            Border artworkFrame = immersive.FindName("ImmersiveArtworkFrame")
                as Border ?? throw new InvalidOperationException("Artwork frame missing");
            double artworkTop = artworkFrame.TransformToVisual(playerColumns)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            double headingTop = heading.TransformToVisual(playerColumns)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            Assert(heading.FontSize < 25
                && Math.Abs(leftColumn.ActualHeight - rightColumn.ActualHeight) < 1
                && Math.Abs(headingTop - artworkTop) < 6,
                $"Immersive: resized panels and album cover/title tops align (art={artworkTop:0}, heading={headingTop:0})");

            // Switching to a full-size bitmap must not have its dimensions
            // or identity destroyed by subsequent async lyric rendering.
            BitmapImage detailed = await FixtureArtworkAsync(
                Color.FromArgb(255, 181, 101, 58),
                width: 640, height: 480);
            immersive.SetCover(detailed);
            immersive.SetLyrics(
                [new LyricDisplayLine("Loaded after artwork", null,
                    "preferOriginal", TimeSpan.FromSeconds(1))]);
            Assert(ReferenceEquals(immersive.CurrentArtwork, detailed)
                && detailed.PixelWidth == 640
                && detailed.PixelHeight == 480,
                "Immersive: lyric reload keeps the current full-resolution cover");

            immersive.SetLyrics([]);
            await Task.Delay(90);
            Assert(upperFade.Visibility == Visibility.Collapsed
                && lowerFade.Visibility == Visibility.Collapsed,
                "Immersive: no scrolling or edge veils with an empty lyric source");

            host.Children.Clear();
            LrcWord[] firstWords =
            [
                new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "君", "kimi"),
                new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "が", "ga"),
            ];
            RubyLyricView rubyView = new()
            {
                Width = 420,
                MinHeight = 180,
                FontSize = 23,
                Foreground = new SolidColorBrush(
                    Color.FromArgb(255, 255, 255, 255)),
                Line = new LyricDisplayLine("君が", null, "preferOriginal",
                    TimeSpan.Zero, firstWords),
            };
            host.Children.Add(rubyView);
            await Task.Delay(110);
            Assert(rubyView.Content is RichTextBlock firstRuby
                && firstRuby.Blocks.Count == 1
                && firstRuby.Blocks[0] is Microsoft.UI.Xaml.Documents.Paragraph paragraph
                && paragraph.Inlines.Count == 2,
                "Ruby: real RichTextBlock contains two timed ruby/base inline atoms");
            rubyView.Line = new LyricDisplayLine("春", null, "preferOriginal",
                TimeSpan.FromSeconds(4),
                [new LrcWord(TimeSpan.FromSeconds(4),
                    TimeSpan.FromSeconds(5), "春", "haru")]);
            await Task.Delay(80);
            Assert(rubyView.Content is RichTextBlock secondRuby
                && secondRuby.Blocks.Count == 1
                && secondRuby.Blocks[0] is Microsoft.UI.Xaml.Documents.Paragraph next
                && next.Inlines.Count == 1,
                "Ruby: changing the lyric line rebuilds inline layout without losing the next line");
            // A romanization syllable may be much wider than one Kanji.
            // Measure the REAL inline element, not just source formatting:
            // the ruby must overhang but must not widen its Japanese base.
            rubyView.Line = new LyricDisplayLine("春", null, "preferOriginal",
                TimeSpan.Zero,
                [new LrcWord(TimeSpan.Zero, TimeSpan.FromSeconds(1),
                    "春", "harunoyoukinagare")]);
            await Task.Delay(85);
            Assert(rubyView.Content is RichTextBlock measuredRuby
                && measuredRuby.Blocks[0]
                    is Microsoft.UI.Xaml.Documents.Paragraph measuredParagraph
                && measuredParagraph.Inlines[0]
                    is Microsoft.UI.Xaml.Documents.InlineUIContainer
                        { Child: Canvas measuredAtom }
                && measuredAtom.Children.Count == 2
                && measuredAtom.Children[0] is TextBlock longPronunciation
                && measuredAtom.Children[1] is TextBlock japaneseBase
                && measuredAtom.Width > 5
                && longPronunciation.DesiredSize.Width > measuredAtom.Width * 1.3
                && Math.Abs(measuredAtom.Width
                    - japaneseBase.DesiredSize.Width) < 1,
                "Ruby: long romanization overhangs the Kanji instead of widening and wrapping the original");
            // Render many ruby+base atoms so the browser-style reading
            // row must wrap naturally. A one-character transparent PNG may
            // compress below 1.8 KB despite being correctly rendered.
            LrcWord[] flow = Enumerable.Range(0, 60)
                .Select(i => new LrcWord(
                    TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 1),
                    "春", "haru"))
                .ToArray();
            rubyView.Line = new LyricDisplayLine(
                new string('春', flow.Length), null, "preferOriginal",
                TimeSpan.Zero, flow);
            await Task.Delay(140);
            Assert(rubyView.Content is RichTextBlock wrap
                && wrap.Blocks.Count == 1
                && wrap.Blocks[0] is Microsoft.UI.Xaml.Documents.Paragraph wrapped
                && wrapped.Inlines.Count == flow.Length,
                "Ruby: long annotated lyrics retain every atom after line wrapping");
            // Line-level romaji is never a giant inline atom; it is two
            // separately wrapped rows sharing the same viewport width.
            rubyView.Line = new LyricDisplayLine(
                "流れてゆく星の光", null, "preferOriginal",
                TimeSpan.FromSeconds(2), romanization:
                    "nagarete yuku hoshi no hikari");
            await Task.Delay(85);
            Assert(rubyView.Content is StackPanel pronunciationLayout
                && pronunciationLayout.Children.Count == 2
                && pronunciationLayout.Children[0] is TextBlock topReading
                && topReading.Text == "nagarete yuku hoshi no hikari"
                && pronunciationLayout.Children[1] is TextBlock japanese
                && japanese.Text == "流れてゆく星の光"
                && japanese.TextWrapping == TextWrapping.WrapWholeWords,
                "Ruby: line romanization and Japanese original wrap independently without stealing width");
            rubyView.Line = new LyricDisplayLine(
                new string('春', flow.Length), null, "preferOriginal",
                TimeSpan.Zero, flow);
            await Task.Delay(80);

            string rubyPng = marker + ".RubyViewport.png";
            await SavePanelScreenshotAsync(rubyView, rubyPng);
            int brightPixels = await CountRenderedTextPixelsAsync(rubyView);
            int wrappedPixels = await CountRenderedTextPixelsAsync(rubyView, minY: 60);
            Assert(brightPixels > 120 && wrappedPixels > 120,
                $"Ruby: annotated syllables must paint AFTER line wrap ({brightPixels} total, {wrappedPixels} below y=60)");
            Assert(new FileInfo(rubyPng).Length > 500,
                "Ruby: nonempty real WinUI annotated text screenshot captured");

            host.Children.Clear();
            AppRoot.Children.Remove(host);
            host = null;

            // Exercise the REAL MainWindow NetEase state adapter (rather
            // than only testing the reusable presentational control).
            const long originalSongId = 185016;
            const long asyncSongId = 185017;
            string path = Path.Combine(Path.GetTempPath(),
                "musicloom-netsmoke-source-" + Guid.NewGuid().ToString("N") + ".flac");
            PlanEntry entry = new(
                path, Path.Combine(Path.GetTempPath(), "musicloom-netsmoke-output.flac"),
                PlanEntryStatus.Unchanged,
                new TrackMetadata(
                    Artist: "Local Artist",
                    AlbumArtist: "Local Artist",
                    Album: "Local Album",
                    HasEmbeddedArt: false,
                    Title: "Local Title"));
            LibraryDisplayItem local = new(
                entry, "ready", "Local Title", "Local Artist",
                "Local Album", "fixture.flac", "ready");
            StateDocument state = new();
            state.Tracks[path] = new TrackState
            {
                SourcePath = path,
                NeteaseSongId = originalSongId,
                NeteaseTrackName = "Actual NetEase Track",
                NeteaseTrackArtist = "Actual NetEase Artist",
                NeteaseTrackAlbum = "Actual NetEase Album",
                NeteaseCoverUrl = "http://p1.music.126.net/original-cover.jpg",
                NeteaseMatchConfirmed = true,
            };
            _state = state;
            _selectedLibraryItem = local;

            RefreshCurrentMatchCard(local);
            var first = NeteaseMatchPanel.ArtworkImage.Source as BitmapImage;
            Assert(first?.UriSource?.Scheme == Uri.UriSchemeHttp
                && first.UriSource.Host == "p1.music.126.net"
                && NeteaseMatchPanel.ArtworkPlaceholder.Visibility
                    == Visibility.Collapsed
                && NeteaseMatchPanel.TitleLabel.Text == "Actual NetEase Track",
                "ACTUAL NetEase adapter: original persisted HTTP cover and title shown");

            for (int n = 0; n < 7; n++)
                RefreshCurrentMatchCard(local);
            Assert(ReferenceEquals(first, NeteaseMatchPanel.ArtworkImage.Source),
                "ACTUAL NetEase adapter: status/progress refresh preserves cover instance");

            state.Tracks[path] = state.Tracks[path] with
            {
                NeteaseTrackName = null,
                NeteaseTrackArtist = null,
                NeteaseTrackAlbum = null,
            };
            RefreshCurrentMatchCard(local);
            Assert(NeteaseMatchPanel.TitleLabel.Text == "Local Title"
                && NeteaseMatchPanel.ArtistLabel.Text == "Local Artist"
                && NeteaseMatchPanel.AlbumLabel.Text == "Local Album",
                "ACTUAL NetEase adapter: legacy local metadata fallback restored");

            // Delayed GetSongAsync completion MUST survive multiple refreshes.
            TaskCompletionSource<NeteaseMatchCandidate?> delayed =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            int lookupCount = 0;
            _netEaseArtworkSmokeResolver = _ =>
            {
                lookupCount++;
                return delayed.Task;
            };
            state.Tracks[path] = state.Tracks[path] with
            {
                NeteaseSongId = asyncSongId,
                NeteaseCoverUrl = null,
            };
            RefreshCurrentMatchCard(local);
            for (int n = 0; n < 8; n++)
                RefreshCurrentMatchCard(local);
            Assert(lookupCount == 1,
                "ACTUAL NetEase adapter: eight refreshes issue ONE async cover lookup");

            delayed.SetResult(new NeteaseMatchCandidate(
                asyncSongId, "Resolved NetEase", "Actual NetEase Artist",
                "Actual NetEase Album", null,
                CoverUrl: "http://p1.music.126.net/async-cover.jpg"));
            await Task.Delay(180);
            BitmapImage? resolved =
                NeteaseMatchPanel.ArtworkImage.Source as BitmapImage;
            Assert(resolved?.UriSource?.AbsoluteUri
                    == "http://p1.music.126.net/async-cover.jpg"
                && NeteaseMatchPanel.ArtworkPlaceholder.Visibility
                    == Visibility.Collapsed,
                "ACTUAL NetEase adapter: late GetSongAsync restores artwork");
            RefreshCurrentMatchCard(local);
            Assert(ReferenceEquals(resolved, NeteaseMatchPanel.ArtworkImage.Source)
                && lookupCount == 1,
                "ACTUAL NetEase adapter: late artwork survives subsequent refresh");

            await File.WriteAllLinesAsync(marker, evidence);
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Matching UI behavior smoke failed.", error);
            string? directory = Path.GetDirectoryName(failed);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(failed, error.ToString());
        }
        finally
        {
            if (host is not null)
                AppRoot.Children.Remove(host);
            AppWindow.Resize(previousWindowSize);
            _netEaseArtworkSmokeResolver = null;
            _state = previousState;
            _selectedLibraryItem = previousSelection;
            _netEaseArtworkMatchIdentity = null;
            _netEaseArtworkRequestPending = false;
            ++_matchArtworkRequestId;
            RefreshCurrentMatchCard(previousSelection);
        }
    }

    private static void Invoke(Button button)
    {
        ButtonAutomationPeer peer = new(button);
        ((IInvokeProvider)peer).Invoke();
    }

    private static async Task<BitmapImage> FixtureArtworkAsync(
        Color tint, uint width = 64, uint height = 64)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = tint.B;
            pixels[i + 1] = tint.G;
            pixels[i + 2] = tint.R;
            pixels[i + 3] = 255;
        }

        using InMemoryRandomAccessStream stream = new();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        BitmapImage image = new();
        await image.SetSourceAsync(stream);
        return image;
    }

    private static async Task<int> CountRenderedTextPixelsAsync(
        FrameworkElement view, int minX = 0, int minY = 0)
    {
        // A repeated short lyric can compress into a tiny PNG. Inspect the
        // actual RenderTargetBitmap pixels instead of treating ZIP/PNG byte
        // size as proof that glyphs were painted.
        RenderTargetBitmap bitmap = new();
        await bitmap.RenderAsync(view);
        var pixels = await bitmap.GetPixelsAsync();
        byte[] bgra = new byte[checked((int)pixels.Length)];
        using (DataReader reader = DataReader.FromBuffer(pixels))
            reader.ReadBytes(bgra);

        int bright = 0;
        int width = bitmap.PixelWidth;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            int pixel = i / 4;
            if (pixel % width < minX || pixel / width < minY) continue;
            if (bgra[i + 3] > 50
                && Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2])) > 125)
                bright++;
        }
        return bright;
    }

    private static async Task SavePanelScreenshotAsync(
        FrameworkElement view, string path)
    {
        RenderTargetBitmap rendered = new();
        await rendered.RenderAsync(view);
        if (rendered.PixelWidth < 300 || rendered.PixelHeight < 50)
            throw new InvalidOperationException(
                "Matching panel failed to acquire real layout dimensions.");

        var buffer = await rendered.GetPixelsAsync();
        byte[] pixels = new byte[checked((int)buffer.Length)];
        using (DataReader source = DataReader.FromBuffer(buffer))
            source.ReadBytes(pixels);

        using InMemoryRandomAccessStream output = new();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(
            BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)rendered.PixelWidth, (uint)rendered.PixelHeight,
            96, 96, pixels);
        await encoder.FlushAsync();
        output.Seek(0);
        using DataReader reader = new(output);
        uint size = checked((uint)output.Size);
        await reader.LoadAsync(size);
        byte[] png = new byte[size];
        reader.ReadBytes(png);
        await File.WriteAllBytesAsync(path, png);
    }
}
