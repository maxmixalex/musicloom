using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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

        void Assert(bool condition, string description)
        {
            if (!condition)
                throw new InvalidOperationException(description);
            evidence.Add("PASS " + description);
        }

        try
        {
            string? directory = Path.GetDirectoryName(marker);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            await Task.Delay(140);
            host = new StackPanel
            {
                Padding = new Thickness(18),
                Width = Math.Min(940, Math.Max(600, AppRoot.ActualWidth - 30)),
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
            preview.ItemsSource =
            [
                new LyricDisplayLine("First original line", "第一句翻译",
                    "preferOriginal", TimeSpan.FromSeconds(12)),
                new LyricDisplayLine("Second original line", "第二句翻译",
                    "preferOriginal", TimeSpan.FromSeconds(16)),
            ];
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

            Assert(searchCard.Content is Grid cardGrid
                && cardGrid.ColumnDefinitions.Count == 3
                && cardGrid.ColumnDefinitions[0].Width.Value == 52
                && cardGrid.ColumnDefinitions[2].Width.Value == 220,
                "Search dialog: original NetEase 52px cover and 220px metadata/actions rail");
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
            string searchPng = marker + ".SearchDialog.png";
            await SavePanelScreenshotAsync(search, searchPng);
            Assert(new FileInfo(searchPng).Length > 1800,
                "Search dialog: actual WinUI candidate and bilingual lyric columns rendered");

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
            string immersivePng = marker + ".ImmersiveViewport.png";
            await SavePanelScreenshotAsync(immersive, immersivePng);
            Assert(new FileInfo(immersivePng).Length > 1800,
                "Immersive: actual WinUI viewport with long bilingual lyrics rendered");

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

    private static async Task<BitmapImage> FixtureArtworkAsync(Color tint)
    {
        const uint edge = 64;
        byte[] pixels = new byte[edge * edge * 4];
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
            edge, edge, 96, 96, pixels);
        await encoder.FlushAsync();
        stream.Seek(0);
        BitmapImage image = new();
        await image.SetSourceAsync(stream);
        return image;
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
