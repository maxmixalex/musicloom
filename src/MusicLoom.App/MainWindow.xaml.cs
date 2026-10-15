using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using MusicLoom.Audio.Windows;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Enrichment;
using MusicLoom.Core.Lyrics;
using MusicLoom.Core.Planning;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Core.Synchronization;
using MusicLoom.Metadata;
using MusicLoom.Netease;
using MusicLoom.Tagging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI;

namespace MusicLoom.App;

public sealed partial class MainWindow : Window
{
    private static readonly string ConfigPath =
        Path.Combine(
            AppDiagnostics.DataDirectory,
            "config.toml");

    private static readonly string LegacyConfigPath =
        Path.Combine(
            AppDiagnostics.DataDirectory,
            "config.json");

    private static readonly string LegacyStatePath =
        Path.Combine(
            AppDiagnostics.DataDirectory,
            "state.json");

    private static readonly string DatabasePath =
        Path.Combine(
            AppDiagnostics.DataDirectory,
            "musicloom.db");

    private static readonly string LegacySnapshotPath =
        Path.Combine(
            AppDiagnostics.DataDirectory,
            "library-index.json");

    private readonly List<LibraryDisplayItem>
        _allLibraryItems = [];
    private readonly List<PlaylistDisplayItem>
        _playlists = [];
    private readonly Dictionary<string, TrackSyncViewState>
        _trackSyncStates =
            new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LiveNeteaseMatch>
        _liveNeteaseMatches =
            new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>
        _manualPendingSources =
            new(StringComparer.OrdinalIgnoreCase);
    private readonly MediaPlayer
        _mediaPlayer = new();

    private MusicLoomSettings? _settings;
    private StateDocument? _state;
    private StateStore? _stateStore;
    private LibraryPlan? _plan;
    private LibraryDisplayItem? _selectedLibraryItem;
    private LibraryDisplayItem? _contextLibraryItem;
    private LibraryDisplayItem? _playlistContextItem;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _settingsSaveCancellation;
    private int _syncProgressGeneration;
    private CancellationTokenSource? _playbackTransitionCancellation;
    private CancellationTokenSource? _waveformCancellation;
    private readonly Dictionary<string, CachedWaveform> _waveformCache =
        new(StringComparer.OrdinalIgnoreCase);
    private ReactiveAudioFrames? _playingAudioFrames;
    private float[]? _smoothedAudioFrame;
    private TimeSpan _lastReactiveTime;
    private bool _busy;
    private bool _loadingSettings = true;
    private bool _refreshingLocalizedChoices;
    private int _detailRequestId;
    private int _artworkGeneration;
    private int _matchArtworkRequestId;
    private string? _netEaseArtworkMatchIdentity;
    private bool _netEaseArtworkRequestPending;
    // Only used by the explicit Windows UI matching smoke harness.
    // Ordinary application runs still call the original NeteaseClient.
    private Func<long, Task<NeteaseMatchCandidate?>>?
        _netEaseArtworkSmokeResolver;
    private double _playerTargetVolume;
    private string _uiLanguage =
        DetectSystemLanguage();
    private string _accentColor = "system";
    private string? _currentLyricsRaw;
    private Storyboard? _titleScrollStoryboard;
    private Storyboard? _albumScrollStoryboard;
    private Storyboard? _lyricsSourceScrollStoryboard;
    private Storyboard? _playlistTitleScrollStoryboard;
    private Storyboard? _playlistMetaScrollStoryboard;
    private Storyboard? _trackArtistScrollStoryboard;
    private Storyboard? _playerTitleScrollStoryboard;
    private Storyboard? _playerMetadataScrollStoryboard;
    private Storyboard? _diagnosticsPathScrollStoryboard;
    private DispatcherTimer? _playerTimer;
    private LibraryDisplayItem? _playingLibraryItem;
    private NeteaseMatchCandidate? _playingPreviewCandidate;
    private LibraryDisplayItem[] _playbackQueue = [];
    private int _playbackQueueIndex = -1;
    private bool _updatingPlayerProgress;
    private bool _suppressPlaybackLyricFollow;
    private LyricDisplayLine? _activePlaybackLyric;
    private LyricDisplayLine? _lastAutoScrolledLyric;
    private DateTimeOffset _lyricFollowBlockedUntil = DateTimeOffset.MinValue;
    private readonly HashSet<LyricDisplayLine>
        _matchDialogLyricLines = [];
    private NeteaseMatchCandidate?
        _activeMatchDialogCandidate;
    private ListView? _matchLyricsPreviewList;
    private TaskCompletionSource<NeteaseMatchCandidate?>?
        _matchDialogCompletion;
    private Action? _overlayCloseAction;
    private readonly Dictionary<long, SymbolIcon>
        _neteasePreviewIcons = [];
    private byte[]? _currentArtworkBytes;
    private string _lyricsDisplayMode =
        LyricsDisplayModes.PreferOriginal;
    private bool _lyricsEditInProgress;

    public MainWindow()
    {
        StartupTrace.Mark(
            "MainWindow.ctor.enter");
        StartupTrace.Mark(
            "MainWindow.InitializeComponent.before");
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            StartupTrace.Mark(
                "MainWindow.InitializeComponent.failed: "
                + exception);
            throw;
        }
        StartupTrace.Mark(
            "MainWindow.InitializeComponent.after");

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppRoot.ActualThemeChanged += (_, _) =>
            RefreshLibraryStatusColors();
        InitializePlayer();
        InitializeProviderMatchTabs();
        InstallLyricScrollSuppression(LyricsList);
        InstallLyricScrollSuppression(ImmersiveView.LyricsList);
        InitializeImmersivePlayer();
        Closed += MainWindow_Closed;
        ApplyWindowIcon();

        LibraryFilterBox.SelectedIndex = 0;
        LibraryLyricsFacetBox.SelectedIndex = 0;
        LibraryNeteaseFacetBox.SelectedIndex = 0;
        LibraryQqFacetBox.SelectedIndex = 0;
        LibraryKugouFacetBox.SelectedIndex = 0;
        LibrarySortBox.SelectedIndex = 2;
        LibrarySortDirectionBox.SelectedIndex = 0;
        PlaylistSortBox.SelectedIndex = 2;
        PlaylistSortDirectionBox.SelectedIndex = 0;
        LyricsModeBox.SelectedIndex = 2;
        LyricsStorageModeBox.SelectedIndex = 0;
        LanguageBox.SelectedIndex =
            IsChinese ? 1 : 0;
        Navigation.SelectedItem =
            Navigation.MenuItems[0];

        DiagnosticsLogPathText.Text =
            AppDiagnostics.LogPath;
        RefreshDiagnosticsConsole();
        AppDiagnostics.LineWritten +=
            AppDiagnostics_LineWritten;

        ApplyLanguage();
        HideDetailPane();
        ResetTrackDetails();
        InstallWaveformSmokeIfRequested();
        InstallKaraokeSmokeIfRequested();

        _ = InitializeSettingsAsync();

        StartupTrace.Mark(
            "MainWindow.ctor.ready");
    }

    private void InstallWaveformSmokeIfRequested()
    {
        string? marker = Environment.GetEnvironmentVariable(
            "MUSICLOOM_WAVEFORM_SMOKE_MARKER");
        if (string.IsNullOrWhiteSpace(marker))
        {
            return;
        }

        // CI previously launched MusicLoom successfully but never
        // exercised the native Path.Data renderer, missing a fatal crash.
        // Mount a real waveform control only in this explicit test mode.
        if (Navigation.Content is not Grid root)
        {
            AppDiagnostics.Log(
                "Waveform smoke failed: navigation content is not a Grid.");
            return;
        }

        Grid host = new()
        {
            Width = 640,
            Height = 62,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(host, 1);
        root.Children.Add(host);

        PlayingWaveform waveform = new();
        host.Children.Add(waveform);
        int successfulRenders = 0;
        waveform.Rendered += (_, _) =>
        {
            successfulRenders++;
            if (successfulRenders == 1)
            {
                // Reproduce the originally failing SizeChanged pathway.
                host.Width = 520;
                waveform.Samples = BuildWaveformSmokeFrame(0.35f);
            }
            else if (successfulRenders == 2)
            {
                host.Width = 640;
                waveform.Samples = BuildWaveformSmokeFrame(0.85f);
            }
            else if (successfulRenders == 3)
            {
                try
                {
                    string? directory = Path.GetDirectoryName(marker);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.WriteAllText(
                        marker,
                        "Three real native waveform render/resize passes succeeded.");
                }
                catch (Exception exception)
                {
                    AppDiagnostics.LogException(
                        "Could not write waveform smoke marker.",
                        exception);
                }
            }
        };

        waveform.Samples = BuildWaveformSmokeFrame(0.6f);
        waveform.IsActive = true;
    }

    private static float[] BuildWaveformSmokeFrame(float amplitude)
    {
        float[] levels = new float[ReactiveAudioFrames.BandCount];
        for (int i = 0; i < levels.Length; i++)
        {
            levels[i] = amplitude *
                (float)Math.Abs(Math.Sin(i / 3.4));
        }

        return levels;
    }

    private void InitializePlayer()
    {
        _mediaPlayer.AutoPlay = false;
        _playerTargetVolume =
            PlayerVolumeSlider.Value / 100.0;
        _mediaPlayer.Volume =
            _playerTargetVolume;

        _mediaPlayer.MediaOpened +=
            (_, _) =>
                DispatcherQueue.TryEnqueue(
                    () =>
                    {
                        UpdatePlayerProgress();
                        UpdatePlayerVisualState();
                    });

        _mediaPlayer.MediaEnded +=
            (_, _) =>
                DispatcherQueue.TryEnqueue(
                    async () =>
                    {
                        if (_playingLibraryItem
                            is not null)
                        {
                            await PlayQueueOffsetAsync(
                                1);
                        }
                    });

        _mediaPlayer.MediaFailed +=
            (_, args) =>
                DispatcherQueue.TryEnqueue(
                    () =>
                    {
                        ShowInfo(
                            InfoBarSeverity.Error,
                            _playingCloudPreviewCandidate is not null
                                ? L("Remote preview failed", "远程试听失败")
                                : L("Playback failed", "播放失败"),
                            args.ErrorMessage);
                        if (_playingCloudPreviewCandidate is not null)
                            _activeCloudLyricsPanel?.SetPreviewStatus(L(
                                "The remote stream could not be played. The provider may restrict access.",
                                "无法播放远程音频，音源可能限制访问。"));
                    });

        _mediaPlayer.PlaybackSession
            .PlaybackStateChanged +=
            (_, _) =>
                DispatcherQueue.TryEnqueue(
                    UpdatePlayerVisualState);

        DispatcherTimer timer = new()
        {
            Interval =
                TimeSpan.FromMilliseconds(
                    50),
        };
        timer.Tick +=
            PlayerTimer_Tick;
        _playerTimer = timer;
        timer.Start();

        UpdatePlayerVisualState();
    }

    private void MainWindow_Closed(
        object sender,
        WindowEventArgs args)
    {
        _playerTimer?.Stop();
        _playbackTransitionCancellation
            ?.Cancel();
        _waveformCancellation?.Cancel();
        _waveformCancellation?.Dispose();
        CloseMatchOverlay();
         StopKaraokeFrameRendering();
        _mediaPlayer.Dispose();
    }

    private async Task PlayLibraryItemAsync(
        LibraryDisplayItem item,
        bool rebuildQueue = true,
        TimeSpan? startPosition = null)
    {
        CancellationTokenSource transition =
            BeginPlaybackTransition();

        try
        {
            StorageFile file =
                await StorageFile.GetFileFromPathAsync(
                    item.Entry.SourcePath);

            if (_mediaPlayer.Source is not null
                && _mediaPlayer.Volume > 0.001)
            {
                await FadePlayerVolumeAsync(
                    _mediaPlayer.Volume,
                    fadeIn: false,
                    transition.Token);
            }

            transition.Token
                .ThrowIfCancellationRequested();

            if (rebuildQueue)
            {
                _playbackQueue =
                    GetCurrentPlaybackQueue(
                        item);
                _playbackQueueIndex =
                    Array.IndexOf(
                        _playbackQueue,
                        item);
                RefreshNowPlayingPlaylist();
            }

            _playingLibraryItem
                ?.SetPlaying(
                    false);
            item.SetPlaying(
                true);

            _playingLibraryItem = item;
            _playingPreviewCandidate = null;
            _playingCloudPreviewCandidate = null;
            _ = LoadWaveformForTrackAsync(item);

            if (_activePlaybackLyric
                is not null)
            {
                _activePlaybackLyric.IsActive =
                    false;
            }
            _activePlaybackLyric = null;
            _suppressPlaybackLyricFollow =
                startPosition.HasValue;

            _mediaPlayer.Volume = 0;
            _mediaPlayer.Source =
                MediaSource.CreateFromStorageFile(
                    file);

            PlayerTitleText.Text =
                string.IsNullOrWhiteSpace(
                    item.Artist)
                    ? item.Title
                    : $"{item.Artist} - {item.Title}";
            PlayerMetadataText.Text =
                item.Album;
            PlayerArtworkImage.Source =
                item.Artwork;
            PlayerArtworkPlaceholder.Visibility =
                item.Artwork is null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            ConfigureAuxiliaryLongText();

            if (_immersiveActive)
            {
                // Album inspection / database loading must not postpone
                // starting audio when the fullscreen view is open.
                _ = UpdateImmersiveTrackAsync(item);
            }

            if (startPosition
                is TimeSpan initialPosition)
            {
                await SeekPlayerWhenReadyAsync(
                    initialPosition);
            }

            transition.Token
                .ThrowIfCancellationRequested();

            _mediaPlayer.Play();

            await WaitForPlaybackStartAsync(
                transition.Token);
            await FadePlayerVolumeAsync(
                0,
                fadeIn: true,
                transition.Token);

            _suppressPlaybackLyricFollow =
                false;
            UpdatePlayerVisualState();

            if (startPosition.HasValue)
            {
                UpdatePlaybackLyrics();
            }
        }
        catch (OperationCanceledException)
        {
            _suppressPlaybackLyricFollow =
                false;
        }
        catch (Exception exception)
        {
            _suppressPlaybackLyricFollow =
                false;
            HandleUiException(
                L(
                    "Could not play the track.",
                    "无法播放歌曲。"),
                exception);
        }
        finally
        {
            CompletePlaybackTransition(
                transition);
        }
    }

    // The visualizer is an asynchronous, cancellable PCM analysis of the
    // actual file. Only the currently playing song gets decoded, and recent
    // results are reused while their source file metadata is unchanged.
    private async Task LoadWaveformForTrackAsync(
        LibraryDisplayItem item)
    {
        _waveformCancellation?.Cancel();
        _waveformCancellation?.Dispose();

        CancellationTokenSource operation = new();
        _waveformCancellation = operation;
        string path = item.Entry.SourcePath;
        _playingAudioFrames = null;
        _smoothedAudioFrame = null;
        _lastReactiveTime = TimeSpan.Zero;
        item.SetWaveform(null);

        try
        {
            FileInfo source = new(path);
            ReactiveAudioFrames? frames;
            if (_waveformCache.TryGetValue(path, out CachedWaveform? cached)
                && cached.Size == source.Length
                && cached.LastWriteUtcTicks == source.LastWriteTimeUtc.Ticks)
            {
                frames = cached.Frames;
            }
            else
            {
                // Actual frequency-domain energy and PCM loudness per 50 ms
                // frame. This is unrelated to the horizontal playback
                // progress track that made the previous visual static.
                frames = await new ReactiveAudioAnalyzer()
                    .AnalyzeAsync(path, operation.Token);
                if (frames is null || operation.IsCancellationRequested)
                {
                    return;
                }

                if (_waveformCache.Count >= 8)
                {
                    _waveformCache.Remove(_waveformCache.Keys.First());
                }

                _waveformCache[path] = new CachedWaveform(
                    source.Length,
                    source.LastWriteTimeUtc.Ticks,
                    frames);
            }

            if (!operation.IsCancellationRequested
                && string.Equals(
                    _playingLibraryItem?.Entry.SourcePath,
                    path,
                    StringComparison.OrdinalIgnoreCase))
            {
                _playingAudioFrames = frames;
                UpdateReactiveWaveform();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not analyze live music frequency/loudness.", exception);
        }
    }

    private void UpdateReactiveWaveform()
    {
        if (_playingLibraryItem is not LibraryDisplayItem item
            || _playingAudioFrames is not ReactiveAudioFrames frames
            || _mediaPlayer.PlaybackSession.PlaybackState
                != MediaPlaybackState.Playing)
        {
            return;
        }

        // Audio timestamps select measured spectral frames. The shape
        // itself expresses the frequencies and loudness at that moment,
        // not how far progress has moved across the row.
        TimeSpan time = _mediaPlayer.PlaybackSession.Position;
        float[] measured = frames.At(time);

        // Respect actual player output gain, including the fade during
        // track changes. Low-level audio produces a quieter wave.
        double volume = Math.Clamp(_mediaPlayer.Volume, 0, 1);
        float gain = (float)Math.Sqrt(volume);
        bool discontinuity = _smoothedAudioFrame is null
            || Math.Abs((time - _lastReactiveTime).TotalSeconds) > 0.6;
        if (discontinuity)
        {
            _smoothedAudioFrame = new float[measured.Length];
            for (int i = 0; i < measured.Length; i++)
            {
                _smoothedAudioFrame[i] = measured[i] * gain;
            }
        }
        else
        {
            // Lightweight temporal damping prevents 20 Hz PCM windows
            // from making sharp, strobing jumps in the Fluent background.
            for (int i = 0; i < measured.Length; i++)
            {
                float target = measured[i] * gain;
                _smoothedAudioFrame![i] +=
                    (target - _smoothedAudioFrame[i]) * 0.38f;
            }
        }

        _lastReactiveTime = time;
        item.SetWaveform((float[])_smoothedAudioFrame!.Clone());
    }

    private sealed record CachedWaveform(
        long Size, long LastWriteUtcTicks, ReactiveAudioFrames Frames);

    private async Task PlayNetEasePreviewAsync(
        NeteaseMatchCandidate candidate,
        TimeSpan? startPosition = null)
    {
        CancellationTokenSource transition =
            BeginPlaybackTransition();

        try
        {
            if (_mediaPlayer.Source is not null
                && _mediaPlayer.Volume > 0.001)
            {
                await FadePlayerVolumeAsync(
                    _mediaPlayer.Volume,
                    fadeIn: false,
                    transition.Token);
            }

            transition.Token
                .ThrowIfCancellationRequested();

            _playingLibraryItem
                ?.SetPlaying(
                    false);
            _playingLibraryItem = null;
            _waveformCancellation?.Cancel();
            _playingAudioFrames = null;
            _smoothedAudioFrame = null;
            _playingPreviewCandidate =
                candidate;
            _playingCloudPreviewCandidate = null;
            _playbackQueue = [];
            _playbackQueueIndex = -1;
            RefreshNowPlayingPlaylist();

            if (_activePlaybackLyric
                is not null)
            {
                _activePlaybackLyric.IsActive =
                    false;
            }
            _activePlaybackLyric = null;
            _suppressPlaybackLyricFollow =
                startPosition.HasValue;

            _mediaPlayer.Volume = 0;
            _mediaPlayer.Source =
                MediaSource.CreateFromUri(
                    NeteaseClient.GetPreviewUri(
                        candidate.TrackId));

            PlayerTitleText.Text =
                string.IsNullOrWhiteSpace(
                    candidate.Artist)
                    ? candidate.Name
                    : $"{candidate.Artist} - {candidate.Name}";
            PlayerMetadataText.Text =
                candidate.Album;

            if (!string.IsNullOrWhiteSpace(
                    candidate.CoverUrl)
                && Uri.TryCreate(
                    candidate.CoverUrl,
                    UriKind.Absolute,
                    out Uri? coverUri))
            {
                PlayerArtworkImage.Source =
                    new BitmapImage
                    {
                        DecodePixelWidth = 144,
                        UriSource = coverUri,
                    };
                PlayerArtworkPlaceholder.Visibility =
                    Visibility.Collapsed;
            }
            else
            {
                PlayerArtworkImage.Source = null;
                PlayerArtworkPlaceholder.Visibility =
                    Visibility.Visible;
            }

            ConfigureAuxiliaryLongText();

            if (startPosition
                is TimeSpan initialPosition)
            {
                await SeekPlayerWhenReadyAsync(
                    initialPosition);
            }

            transition.Token
                .ThrowIfCancellationRequested();

            _mediaPlayer.Play();

            await WaitForPlaybackStartAsync(
                transition.Token);
            await FadePlayerVolumeAsync(
                0,
                fadeIn: true,
                transition.Token);

            _suppressPlaybackLyricFollow =
                false;
            UpdatePlayerVisualState();

            if (startPosition.HasValue)
            {
                UpdatePlaybackLyrics();
            }
        }
        catch (OperationCanceledException)
        {
            _suppressPlaybackLyricFollow =
                false;
        }
        catch (Exception exception)
        {
            _suppressPlaybackLyricFollow =
                false;
            HandleUiException(
                L(
                    "Could not play the NetEase preview.",
                    "无法播放网易云试听。"),
                exception);
        }
        finally
        {
            CompletePlaybackTransition(
                transition);
        }
    }

    private CancellationTokenSource
        BeginPlaybackTransition()
    {
        _playbackTransitionCancellation
            ?.Cancel();

        CancellationTokenSource current =
            new();
        _playbackTransitionCancellation =
            current;
        return current;
    }

    private async Task FadePlayerVolumeAsync(
        double startVolume,
        bool fadeIn,
        CancellationToken cancellationToken)
    {
        const int Steps = 12;
        TimeSpan delay =
            TimeSpan.FromMilliseconds(
                15);

        for (int step = 1;
            step <= Steps;
            step++)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            double progress =
                step / (double)Steps;
            double target =
                fadeIn
                    ? _playerTargetVolume
                    : 0;
            _mediaPlayer.Volume =
                Math.Clamp(
                    startVolume
                    + (target - startVolume)
                    * progress,
                    0,
                    1);

            await Task.Delay(
                delay,
                cancellationToken);
        }
    }

    private async Task WaitForPlaybackStartAsync(
        CancellationToken cancellationToken)
    {
        for (int attempt = 0;
            attempt < 80;
            attempt++)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            MediaPlaybackState state =
                _mediaPlayer
                    .PlaybackSession
                    .PlaybackState;

            if (state
                is MediaPlaybackState.Playing
                or MediaPlaybackState.Paused)
            {
                return;
            }

            await Task.Delay(
                15,
                cancellationToken);
        }
    }

    private void CompletePlaybackTransition(
        CancellationTokenSource transition)
    {
        if (ReferenceEquals(
                _playbackTransitionCancellation,
                transition))
        {
            _playbackTransitionCancellation =
                null;
            try
            {
                _mediaPlayer.Volume =
                    _playerTargetVolume;
            }
            catch (ObjectDisposedException)
            {
            }
        }

        transition.Dispose();
    }

    private LibraryDisplayItem[]
        GetCurrentPlaybackQueue(
            LibraryDisplayItem item)
    {
        if (PlaylistsPage.Visibility
                == Visibility.Visible
            && PlaylistTracksList.ItemsSource
                is IEnumerable<LibraryDisplayItem>
                    playlistItems)
        {
            LibraryDisplayItem[] playlistQueue =
                playlistItems.ToArray();

            if (playlistQueue.Contains(
                    item))
            {
                return playlistQueue;
            }
        }

        if (LibraryList.ItemsSource
            is IEnumerable<LibraryDisplayItem>
                visibleItems)
        {
            LibraryDisplayItem[] visible =
                visibleItems.ToArray();

            if (visible.Contains(item))
            {
                return visible;
            }
        }

        return [.. _allLibraryItems];
    }

    private async Task PlayQueueOffsetAsync(
        int offset)
    {
        if (_playbackQueue.Length == 0)
        {
            return;
        }

        int current =
            _playbackQueueIndex;

        if (current < 0)
        {
            current = 0;
        }

        int next =
            (current
                + offset
                + _playbackQueue.Length)
            % _playbackQueue.Length;

        _playbackQueueIndex = next;

        await PlayLibraryItemAsync(
            _playbackQueue[next],
            rebuildQueue: false);
    }

    private void PlayerTimer_Tick(
        object? sender,
        object e)
    {
        // Dragging the list/detail boundary already forces a full grid
        // measure. Skip periodic progress/lyric/waveform UI work until the
        // pointer is released; the underlying MediaPlayer keeps playing.
        if (_splitDragging)
            return;

        UpdatePlayerProgress();
        UpdateReactiveWaveform();
        UpdatePlaybackLyrics();
    }

    private void UpdatePlayerProgress()
    {
        MediaPlaybackSession session =
            _mediaPlayer
                .PlaybackSession;

        TimeSpan duration =
            session.NaturalDuration;
        TimeSpan position =
            session.Position;

        double durationSeconds =
            Math.Max(
                0,
                duration.TotalSeconds);
        double positionSeconds =
            Math.Clamp(
                position.TotalSeconds,
                0,
                Math.Max(
                    durationSeconds,
                    position.TotalSeconds));

        _updatingPlayerProgress = true;
        try
        {
            PlayerProgressSlider.Maximum =
                durationSeconds > 0
                    ? durationSeconds
                    : 1;
            PlayerProgressSlider.Value =
                durationSeconds > 0
                    ? Math.Min(
                        positionSeconds,
                        durationSeconds)
                    : 0;
        }
        finally
        {
            _updatingPlayerProgress = false;
        }

        PlayerTimeText.Text =
            $"{FormatPlayerTime(position)} / {FormatPlayerTime(duration)}";
        if (_immersiveActive)
        {
            ImmersiveView.SetProgress(
                positionSeconds,
                durationSeconds,
                PlayerTimeText.Text);
        }
    }

    private void UpdatePlayerVisualState()
    {
        bool playing =
            _mediaPlayer.PlaybackSession
                .PlaybackState
            == MediaPlaybackState.Playing;

        PlayerPlayPauseIcon.Symbol =
            playing
                ? Symbol.Pause
                : Symbol.Play;
        if (_immersiveActive)
        {
            ImmersiveView.SetPlaying(playing);
        }
        RefreshKaraokeFrameRendering();

        ToolTipService.SetToolTip(
            PlayerPlayPauseButton,
            playing
                ? L("Pause", "暂停")
                : L("Play", "播放"));

        PlayerPreviousButton.IsEnabled =
            _playingLibraryItem is not null
            && _playbackQueue.Length > 1;
        PlayerNextButton.IsEnabled =
            _playingLibraryItem is not null
            && _playbackQueue.Length > 1;

        foreach ((long trackId,
            SymbolIcon icon)
            in _neteasePreviewIcons)
        {
            icon.Symbol =
                _playingPreviewCandidate
                        ?.TrackId
                    == trackId
                    && playing
                        ? Symbol.Pause
                        : Symbol.Play;
        }

        UpdateCloudPreviewVisualState();

        if (_selectedLibraryItem is not null)
        {
            DetailPlayIcon.Symbol =
                ReferenceEquals(
                        _selectedLibraryItem,
                        _playingLibraryItem)
                    && playing
                        ? Symbol.Pause
                        : Symbol.Play;
        }
    }

    private static string FormatPlayerTime(
        TimeSpan value)
    {
        if (value < TimeSpan.Zero
            || value == TimeSpan.MaxValue)
        {
            return "0:00";
        }

        return value.TotalHours >= 1
            ? value.ToString(
                @"h\:mm\:ss",
                CultureInfo.InvariantCulture)
            : value.ToString(
                @"m\:ss",
                CultureInfo.InvariantCulture);
    }

    private void UpdatePlaybackLyrics()
    {
        if (_suppressPlaybackLyricFollow)
        {
            return;
        }

        IEnumerable<LyricDisplayLine> source;
        ListView targetList;

        if (_immersiveActive
            && _playingLibraryItem is not null
            && ImmersiveView.LyricsList.ItemsSource
                is IEnumerable<LyricDisplayLine> immersiveLines)
        {
            source = immersiveLines;
            targetList = ImmersiveView.LyricsList;
        }
        else if (_playingCloudPreviewCandidate is not null
            && _activeCloudLyricsPanel is CloudLyricsPanel cloudPanel
            && SameCloudCandidate(
                _playingCloudPreviewCandidate, cloudPanel.SelectedCandidate)
            && cloudPanel.PreviewLyricsList.ItemsSource
                is IEnumerable<LyricDisplayLine> cloudLines)
        {
            source = cloudLines;
            targetList = cloudPanel.PreviewLyricsList;
        }
        else if (_playingPreviewCandidate is not null
            && _activeMatchDialogCandidate
                is NeteaseMatchCandidate match
            && match.TrackId
                == _playingPreviewCandidate.TrackId
            && _matchLyricsPreviewList
                is ListView previewList
            && _matchDialogLyricLines.Count
                > 0)
        {
            source =
                _matchDialogLyricLines;
            targetList =
                previewList;
        }
        else if (_playingLibraryItem is not null
            && _selectedLibraryItem is not null
            && ReferenceEquals(
                _playingLibraryItem,
                _selectedLibraryItem)
            && LyricsList.ItemsSource
                is IEnumerable<LyricDisplayLine>
                    libraryLines)
        {
            source =
                libraryLines;
            targetList =
                LyricsList;
        }
        else
        {
            StopKaraokeFrameRendering();
            return;
        }

        TimeSpan position =
            _mediaPlayer
                .PlaybackSession
                .Position;

        LyricDisplayLine? active =
            source
                .Where(
                    static line =>
                        line.Timestamp.HasValue)
                .LastOrDefault(
                    line =>
                        line.Timestamp
                        <= position);

        if (active is null)
        {
            // A seek to the intro before the first lyric clears the
            // previously highlighted line/word immediately.
            if (_activePlaybackLyric is LyricDisplayLine previous)
            {
                previous.IsActive = false;
                AnimateLyricFont(targetList, previous, expand: false);
            }
            _activePlaybackLyric = null;
            _lastAutoScrolledLyric = null;
            StopKaraokeFrameRendering();
            return;
        }

        active.PlaybackPosition = position;
        if (!ReferenceEquals(active, _activePlaybackLyric))
        {
            if (_activePlaybackLyric is LyricDisplayLine previous)
            {
                previous.IsActive = false;
                AnimateLyricFont(targetList, previous, expand: false);
            }

            _activePlaybackLyric = active;
            active.IsActive = true;
            AnimateLyricFont(targetList, active, expand: true);
        }
        RefreshKaraokeFrameRendering();

        // Track the active lyric without hijacking reader selection.
        // Respect a six-second cooldown after any manual scroll or tap.
        if (DateTimeOffset.UtcNow >= _lyricFollowBlockedUntil
            && !ReferenceEquals(active, _lastAutoScrolledLyric))
        {
            if (ScrollLyricIntoViewSmoothly(targetList, active))
            {
                _lastAutoScrolledLyric = active;
            }
        }
    }

    private void PlayerPlayPauseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_mediaPlayer.Source is null)
        {
            if (_selectedLibraryItem
                is LibraryDisplayItem selected)
            {
                _ = PlayLibraryItemAsync(
                    selected);
            }

            return;
        }

        if (_mediaPlayer.PlaybackSession
                .PlaybackState
            == MediaPlaybackState.Playing)
        {
            _mediaPlayer.Pause();
        }
        else
        {
            _mediaPlayer.Play();
        }
    }

    private async void PlayerPreviousButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await PlayQueueOffsetAsync(-1);

    private async void PlayerNextButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await PlayQueueOffsetAsync(1);

    private void PlayerProgressSlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        if (_updatingPlayerProgress
            || _mediaPlayer.Source is null)
        {
            return;
        }

        TimeSpan duration =
            _mediaPlayer
                .PlaybackSession
                .NaturalDuration;

        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        double seconds =
            Math.Clamp(
                e.NewValue,
                0,
                duration.TotalSeconds);

        _mediaPlayer
            .PlaybackSession
            .Position =
                TimeSpan.FromSeconds(
                    seconds);
        UpdatePlaybackLyrics();
    }

    private void PlayerVolumeSlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        double volume =
            Math.Clamp(
                e.NewValue / 100.0,
                0,
                1);

        _playerTargetVolume =
            volume;

        if (_playbackTransitionCancellation
            is null)
        {
            _mediaPlayer.Volume =
                volume;
        }

        if (PlayerVolumeIcon is null)
        {
            return;
        }

        PlayerVolumeIcon.Glyph =
            volume <= 0.001
                ? "\uE74F"
                : volume < 0.34
                    ? "\uE993"
                    : volume < 0.67
                        ? "\uE994"
                        : "\uE767";
    }

    private void PlayerArtworkButton_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        if (_playingLibraryItem
            is not LibraryDisplayItem item)
        {
            return;
        }

        LibrarySearchBox.Text =
            string.Empty;
        LibraryFilterBox.SelectedIndex = 0;
        LibraryLyricsFacetBox.SelectedIndex = 0;
        LibraryNeteaseFacetBox.SelectedIndex = 0;
        LibraryQqFacetBox.SelectedIndex = 0;
        LibraryKugouFacetBox.SelectedIndex = 0;
        ApplyLibraryFilter();

        NavigateTo("library");

        LibraryList.SelectedItem =
            item;
        LibraryList.ScrollIntoView(
            item);
    }

    private async void LibraryList_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        if (LibraryList.SelectedItems.Count > 1)
        {
            return;
        }

        LibraryDisplayItem? item =
            (e.OriginalSource
                as FrameworkElement)
                ?.DataContext
            as LibraryDisplayItem
            ?? LibraryList.SelectedItem
                as LibraryDisplayItem;

        if (item is null)
        {
            return;
        }

        LibraryList.SelectedItem =
            item;
        e.Handled = true;
        await PlayLibraryItemAsync(
            item);
    }

    private async void PlaylistTracksList_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        LibraryDisplayItem? item =
            (e.OriginalSource
                as FrameworkElement)
                ?.DataContext
            as LibraryDisplayItem
            ?? PlaylistTracksList
                .SelectedItem
                as LibraryDisplayItem;

        if (item is null)
        {
            return;
        }

        PlaylistTracksList.SelectedItem =
            item;
        e.Handled = true;
        await PlayLibraryItemAsync(
            item);
    }

    private async void DetailPlayButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedLibraryItem
            is not LibraryDisplayItem item)
        {
            return;
        }

        if (ReferenceEquals(
                item,
                _playingLibraryItem))
        {
            PlayerPlayPauseButton_Click(
                sender,
                e);
            return;
        }

        await PlayLibraryItemAsync(
            item);
    }

    private async void ContextPlayItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem()
            is LibraryDisplayItem item)
        {
            await PlayLibraryItemAsync(
                item);
        }
    }

    private async void PlaylistPlayItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        LibraryDisplayItem? item =
            _playlistContextItem
            ?? PlaylistTracksList
                .SelectedItem
                as LibraryDisplayItem;

        if (item is not null)
        {
            await PlayLibraryItemAsync(
                item);
        }
    }

    private async void PlaylistHeaderPlayButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (PlaylistsList.SelectedItem
                is not PlaylistDisplayItem playlist
            || playlist.IsVirtual)
        {
            return;
        }

        LibraryDisplayItem[] queue =
            PlaylistTracksList.ItemsSource
                is IEnumerable<LibraryDisplayItem>
                    visibleItems
                ? visibleItems.ToArray()
                : playlist.Tracks.ToArray();

        if (queue.Length == 0)
        {
            return;
        }

        _playbackQueue = queue;
        _playbackQueueIndex = 0;
        RefreshNowPlayingPlaylist();

        await PlayLibraryItemAsync(
            queue[0],
            rebuildQueue: false);
    }

    private bool IsChinese =>
        string.Equals(
            _uiLanguage,
            "zh-CN",
            StringComparison.OrdinalIgnoreCase);

    private string L(
        string english,
        string chinese) =>
        IsChinese ? chinese : english;

    private void ApplyWindowIcon()
    {
        try
        {
            string iconPath =
                Path.Combine(
                    AppDiagnostics.DataDirectory,
                    "MusicLoom.ico");

            Directory.CreateDirectory(
                AppDiagnostics.DataDirectory);

            using Stream? resource =
                typeof(MainWindow)
                    .Assembly
                    .GetManifestResourceStream(
                        "MusicLoom.AppIcon.ico");

            if (resource is null)
            {
                return;
            }

            using (FileStream output =
                new(
                    iconPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read))
            {
                resource.CopyTo(output);
            }

            AppWindow.SetIcon(iconPath);
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not apply the MusicLoom window icon.",
                exception);
        }
    }

    private async Task RefreshLibraryLyricsIndexAsync(
        LibraryPlan plan,
        StateDocument state,
        CancellationToken cancellationToken = default)
    {
        LibraryLyricsIndexer indexer = new(
            new SqliteLyricsCache(DatabasePath),
            reportError: (source, exception) =>
                AppDiagnostics.LogException(
                    $"Could not index local lyrics for {source}.",
                    exception));

        await indexer.RefreshAsync(
            plan,
            state,
            cancellationToken);
    }

    private async Task InitializeSettingsAsync()
    {
        bool startScan = false;
        try
        {
            await LibraryDatabase.InitializeAsync(
                DatabasePath);

            MusicLoomSettings settings =
                NormalizeGuiSettings(
                    await LoadPersistedSettingsAsync());

            _settings = settings;
            startScan = settings.Path.AutoScanOnStartup
                && !string.IsNullOrWhiteSpace(settings.Path.SourceDirectory)
                && !string.IsNullOrWhiteSpace(settings.Path.DestinationDirectory);
            _uiLanguage =
                NormalizeLanguage(
                    settings.Ui.Language);
            _accentColor =
                string.IsNullOrWhiteSpace(
                    settings.Ui.AccentColor)
                    ? "system"
                    : settings.Ui.AccentColor;

            PopulateSettingsForm(settings);
            ApplyLanguage();
            ApplyAppearance(settings);

            SettingsStatusText.Text =
                string.Empty;
            RefreshDatabaseSize();

            await RestoreCachedLibraryAsync(
                settings);

            UpdateLibraryEmptyState();

            AddActivity(
                L("Ready", "就绪"),
                L(
                    "MusicLoom started.",
                    "MusicLoom 已启动."));
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not load application settings.",
                exception);

            ShowInfo(
                InfoBarSeverity.Error,
                L(
                    "Settings error",
                    "设置错误"),
                UserFacingErrors.Describe(
                    exception, IsChinese));

            AddActivity(
                L(
                    "Settings error",
                    "设置错误"),
                UserFacingErrors.Describe(
                    exception, IsChinese));
        }
        finally
        {
            _loadingSettings = false;
        }

        if (startScan)
        {
            // Restore the cached view, then validate both libraries once.
            await ScanLibraryAsync(
                clearInfoBar: false,
                recordActivity: true);
        }
    }

    private static MusicLoomSettings NormalizeGuiSettings(
        MusicLoomSettings settings) =>
        settings with
        {
            Organizer = settings.Organizer with
            {
                Lyrics = settings.Organizer.Lyrics with
                {
                    Enabled = true,
                },
            },
        };

    private async Task<MusicLoomSettings>
        LoadPersistedSettingsAsync(
            CancellationToken cancellationToken = default)
    {
        if (File.Exists(
                ConfigPath))
        {
            MusicLoomSettings current =
                await TomlSettingsStore.LoadAsync(
                    ConfigPath,
                    cancellationToken);

            await LibraryDatabase
                .DropLegacySettingsAsync(
                    DatabasePath,
                    cancellationToken);
            TryDeleteLegacyFile(
                LegacyConfigPath);

            return NormalizeGuiSettings(current);
        }

        MusicLoomSettings? settings =
            await LibraryDatabase
                .LoadLegacySettingsAsync(
                    DatabasePath,
                    cancellationToken);

        if (settings is not null)
        {
            settings = NormalizeGuiSettings(settings);
            await TomlSettingsStore.SaveAsync(
                ConfigPath,
                settings,
                cancellationToken);
            await LibraryDatabase
                .DropLegacySettingsAsync(
                    DatabasePath,
                    cancellationToken);
            TryDeleteLegacyFile(
                LegacyConfigPath);

            AppDiagnostics.Log(
                "Migrated legacy SQLite application settings to config.toml.");

            return settings;
        }

        if (File.Exists(
                LegacyConfigPath))
        {
            settings =
                NormalizeGuiSettings(
                    await SettingsLoader.LoadAsync(
                        LegacyConfigPath,
                        cancellationToken));

            await TomlSettingsStore.SaveAsync(
                ConfigPath,
                settings,
                cancellationToken);

            TryDeleteLegacyFile(
                LegacyConfigPath);

            AppDiagnostics.Log(
                "Migrated legacy JSON settings to config.toml.");

            return settings;
        }

        return new MusicLoomSettings();
    }

    private static void TryDeleteLegacyFile(
        string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }


    private async Task RestoreCachedLibraryAsync(
        MusicLoomSettings settings)
    {
        if (string.IsNullOrWhiteSpace(
                settings.Path.SourceDirectory)
            || string.IsNullOrWhiteSpace(
                settings.Path.DestinationDirectory))
        {
            return;
        }

        try
        {
            StateStore stateStore =
                new SqliteStateStore(
                    DatabasePath,
                    LegacyStatePath);
            StateDocument state =
                await stateStore.LoadAsync();

            _stateStore = stateStore;
            _state = state;

            LibraryPlan? cachedPlan =
                await LibraryDatabase.LoadPlanAsync(
                    DatabasePath,
                    settings.Path.SourceDirectory,
                    settings.Path.DestinationDirectory);

            // One-version migration path from the earlier JSON snapshot.
            if (cachedPlan is null)
            {
                LibrarySnapshotDocument? snapshot =
                    await LibrarySnapshotStore.LoadAsync(
                        LegacySnapshotPath);

                if (snapshot is not null
                    && LibrarySnapshotStore.Matches(
                        snapshot,
                        settings.Path.SourceDirectory,
                        settings.Path.DestinationDirectory))
                {
                    cachedPlan =
                        LibrarySnapshotStore.ToPlan(
                            snapshot);
                }
            }

            cachedPlan ??=
                BuildCachedPlanFromState(
                    settings,
                    state);

            // The saved UI snapshot can predate a completed/cancelled sync.
            // Derive display readiness from the more recent state, but still
            // require a fresh scanner pass before enabling synchronization.
            cachedPlan = CachedPlanReconciler.Reconcile(
                cachedPlan,
                state,
                settings);

            if (cachedPlan.TotalCount == 0)
            {
                return;
            }

            await RefreshLibraryLyricsIndexAsync(
                cachedPlan,
                state);

            await LibraryDatabase.SavePlanAsync(
                DatabasePath,
                settings.Path.SourceDirectory,
                settings.Path.DestinationDirectory,
                cachedPlan);

            TryDeleteLegacyFile(
                LegacySnapshotPath);
            RefreshDatabaseSize();

            PopulateLibrary(
                settings,
                cachedPlan);
            UpdateLibrarySummary(
                cachedPlan);

            PlaylistDisplayItem[] playlists =
                await Task.Run(
                    () => BuildPlaylists(
                        settings));
            PopulatePlaylists(playlists);

            // A cached view is for continuity only. Sync always requires a
            // fresh scan so source/destination drift is revalidated.
            _plan = null;
            SyncButton.IsEnabled = false;
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not restore cached library view.",
                exception);
        }
    }

    private static LibraryPlan BuildCachedPlanFromState(
        MusicLoomSettings settings,
        StateDocument state)
    {
        string sourceRoot =
            Path.GetFullPath(
                settings.Path.SourceDirectory);

        List<PlanEntry> entries = [];

        foreach (TrackState track
            in state.Tracks.Values)
        {
            if (!File.Exists(track.SourcePath)
                || !IsPathInside(
                    track.SourcePath,
                    sourceRoot))
            {
                continue;
            }

            TrackMetadata metadata = new(
                track.Artist,
                track.AlbumArtist,
                track.Album,
                track.HasEmbeddedArt,
                track.Title);

            bool destinationMissing =
                string.IsNullOrWhiteSpace(
                    track.DestinationPath)
                || !File.Exists(
                    track.DestinationPath);

            PlanEntryStatus status =
                string.IsNullOrWhiteSpace(
                    track.RecipeVersion)
                || destinationMissing
                    ? PlanEntryStatus.Modified
                    : PlanEntryStatus.Unchanged;

            entries.Add(
                new PlanEntry(
                    track.SourcePath,
                    track.DestinationPath,
                    status,
                    metadata,
                    Detail:
                        destinationMissing
                            ? "Cached destination is missing."
                            : null,
                    NeedsAudioCopy:
                        destinationMissing));
        }

        return new LibraryPlan(entries);
    }

    private static bool IsPathInside(
        string path,
        string root)
    {
        string fullPath =
            Path.GetFullPath(path);
        string fullRoot =
            Path.GetFullPath(root)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(
            fullRoot,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }

    private void ApplyAppearance(
        MusicLoomSettings settings)
    {
        AppRoot.RequestedTheme =
            UiThemeModes.Normalize(
                settings.Ui.Theme) switch
            {
                UiThemeModes.Light =>
                    ElementTheme.Light,
                UiThemeModes.Dark =>
                    ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        Navigation.RequestedTheme = AppRoot.RequestedTheme;

        AppearanceManager.Apply(
            this,
            settings.Ui);
        RefreshLibraryStatusColors();

        if (AppearanceManager.TryParseColor(
                settings.Ui.AccentColor,
                out Color accent))
        {
            Microsoft.UI.Xaml.Media.SolidColorBrush brush =
                new(accent);

            AccentPreview.Background = brush;
            // Let Fluent AccentButtonStyle resolve theme-aware states
            // instead of pinning one hardcoded background in dark mode.
            SyncButton.ClearValue(Control.BackgroundProperty);
            OperationProgressBar.ClearValue(Control.ForegroundProperty);
        }
        else
        {
            AccentPreview.Background =
                Application.Current.Resources[
                    "AccentFillColorDefaultBrush"]
                as Microsoft.UI.Xaml.Media.Brush;

            SyncButton.ClearValue(
                Control.BackgroundProperty);
            OperationProgressBar.ClearValue(
                Control.ForegroundProperty);
        }
    }

    private async void ScanButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await ScanLibraryAsync(
            clearInfoBar: true,
            recordActivity: true);
    }

    private async void AutoMatchButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        // Avoid rebuilding an existing filtered library view simply
        // because the user asked to run incremental recognition.
        if (_plan is null
            && !await ScanLibraryAsync(
                clearInfoBar: true,
                recordActivity: true))
        {
            return;
        }

        await RunAutomaticNeteaseMatchAsync();
    }

    private async Task RunAutomaticNeteaseMatchAsync()
    {
        if (_busy
            || _settings is not MusicLoomSettings settings
            || _state is not StateDocument state
            || _stateStore is not StateStore store
            || _plan is not LibraryPlan plan)
        {
            return;
        }

        // Incremental selection includes new, changed, or untried audio,
        // not unchanged no-match results or already confirmed matches.
        PlanEntry[] candidates = await Task.Run(() =>
            plan.Entries
                .Where(entry =>
                {
                    state.Tracks.TryGetValue(
                        entry.SourcePath, out TrackState? previous);
                    return AutoMatchEligibility.ShouldAttempt(
                        entry, previous);
                })
                .ToArray());

        if (candidates.Length == 0)
        {
            ShowInfo(
                InfoBarSeverity.Informational,
                L("Nothing to match", "无需匹配"),
                L(
                    "Every track already has a match or was explicitly excluded.",
                    "所有歌曲均已有匹配，或已手动设置为不匹配。"));
            return;
        }

        using CancellationTokenSource cancellation = new();
        _operationCancellation = cancellation;
        SetBusy(
            true,
            L("Matching tracks with NetEase…", "正在匹配网易云音乐……"));

        int matched = 0;
        int missing = 0;
        int warnings = 0;
        int failures = 0;
        int cachedLyrics = 0;
        bool cancelled = false;

        try
        {
            LibraryPathSafety.ValidateStatePath(
                settings, DatabasePath);

            using NeteaseClient client = new();
            CompositeFingerprintPcmDecoder decoder = new();
            NeteaseTrackProvider recognizer = new(client, decoder);
            SqliteLyricsCache lyricsCache = new(DatabasePath);

            for (int index = 0; index < candidates.Length; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                PlanEntry entry = candidates[index];

                SetOperationStatus(
                    L(
                        $"Matching {index + 1:N0}/{candidates.Length:N0} · {Path.GetFileName(entry.SourcePath)}",
                        $"正在匹配 {index + 1:N0}/{candidates.Length:N0} · {Path.GetFileName(entry.SourcePath)}"));
                SetProgress(index, candidates.Length);

                try
                {
                    // Fingerprint is primary; a uniquely verified artist/title
                    // search is a conservative fallback, including on decoder
                    // or upstream fingerprint failure.
                    TrackRecognitionResult result =
                        await RecognizeNeteaseWithMetadataFallbackAsync(
                            recognizer, client, entry, cancellation.Token);

                    FileInfo source = new(entry.SourcePath);
                    TrackMetadata metadata = entry.Metadata!;
                    ulong hash = await Task.Run(
                        () => QuickFileHash.Compute(entry.SourcePath),
                        cancellation.Token);
                    state.Tracks.TryGetValue(
                        entry.SourcePath, out TrackState? previous);
                    TrackState baseState = (previous ?? new TrackState
                    {
                        SourcePath = entry.SourcePath,
                    }) with
                    {
                        SourceSize = source.Length,
                        SourceLastWriteUtcTicks =
                            source.LastWriteTimeUtc.Ticks,
                        QuickHash = hash,
                        DestinationPath = entry.DestinationPath,
                        MetadataCaptured = true,
                        MetadataSchemaVersion =
                            TrackMetadata.CurrentSchemaVersion,
                        TitleCaptured = true,
                        Title = metadata.Title,
                        Artist = metadata.Artist,
                        AlbumArtist = metadata.AlbumArtist,
                        HasEmbeddedArt = metadata.HasEmbeddedArt,
                    };

                    if (result.Status != TrackRecognitionStatus.Matched
                        || result.TrackId is not long songId)
                    {
                        // Persist source signature and the negative result
                        // so an unchanged song is skipped on the next run.
                        state.Tracks[entry.SourcePath] =
                            baseState with
                            {
                                Recognition = RecognitionState.NoMatch,
                                RecognitionCheckedAt = DateTimeOffset.UtcNow,
                            };
                        missing++;
                        continue;
                    }

                    LibraryLyricsRecord? local =
                        await lyricsCache.GetLibraryAsync(
                            entry.SourcePath,
                            cancellation.Token);
                    bool hasLocalLyrics =
                        local is not null
                        && (local.Origin == LibraryLyricsOrigin.Embedded
                            || local.Origin == LibraryLyricsOrigin.Sidecar)
                        && !string.IsNullOrWhiteSpace(local.Text);

                    TrackState updated = baseState with
                    {
                        Recognition = RecognitionState.Matched,
                        RecognitionCheckedAt = DateTimeOffset.UtcNow,
                        NeteaseSongId = songId,
                        NeteaseTrackName = result.TrackName,
                        NeteaseTrackArtist = result.Artist,
                        NeteaseTrackAlbum = result.Album,
                        NeteaseCoverUrl = result.CoverUrl,
                        NeteaseMatchSuppressed = false,
                        NeteaseMatchConfirmed = false,
                        Lyrics = hasLocalLyrics
                            ? LyricsState.Existing
                            : LyricsState.Unknown,
                        LyricsCheckedAt = hasLocalLyrics
                            ? DateTimeOffset.UtcNow
                            : null,
                        ProcessedAt = DateTimeOffset.UtcNow,
                    };

                    // Suspicious matches remain a manual review gate.
                    if (SyncExecutor.DetectMatchWarning(
                            entry, updated) is not null)
                    {
                        warnings++;
                    }
                    else if (!hasLocalLyrics)
                    {
                        try
                        {
                            LrcDocument? remote =
                                await client.GetLyricsAsync(
                                    songId,
                                    cancellation.Token);
                            await lyricsCache.SaveAsync(
                                entry.SourcePath,
                                songId,
                                remote,
                                cancellation.Token);

                            if (remote is { Lines.Count: > 0 })
                            {
                                // The lyrics are in the authoritative DB;
                                // copying them to the target is a later sync.
                                cachedLyrics++;
                                updated = updated with
                                {
                                    Lyrics = LyricsState.Unknown,
                                    LyricsCheckedAt = null,
                                    RecipeVersion = string.Empty,
                                };
                            }
                            else
                            {
                                updated = updated with
                                {
                                    Lyrics = LyricsState.NoLyrics,
                                    LyricsCheckedAt = DateTimeOffset.UtcNow,
                                };
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            failures++;
                            AppDiagnostics.LogException(
                                "Could not cache recognized track lyrics.",
                                exception);
                        }
                    }

                    state.Tracks[entry.SourcePath] = updated;
                    matched++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failures++;
                    AppDiagnostics.LogException(
                        "Automatic NetEase matching failed for "
                        + entry.SourcePath,
                        exception);
                }
                finally
                {
                    // Partial matching results survive cancellation.
                    if ((index + 1) % 25 == 0)
                    {
                        await store.SaveAsync(
                            state, CancellationToken.None);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception exception)
        {
            failures++;
            AppDiagnostics.LogException(
                "Automatic NetEase matching failed.",
                exception);
        }
        finally
        {
            try
            {
                await store.SaveAsync(
                    state, CancellationToken.None);
            }
            catch (Exception exception)
            {
                failures++;
                AppDiagnostics.LogException(
                    "Could not save automatic NetEase matches.",
                    exception);
                ShowInfo(
                    InfoBarSeverity.Error,
                    L("Match persistence failed", "匹配结果保存失败"),
                    UserFacingErrors.Describe(exception, IsChinese));
            }

            _operationCancellation = null;
            SetBusy(
                false,
                cancelled
                    ? L("Match cancelled.", "匹配已取消。")
                    : L("Matching finished.", "匹配已完成。"));
        }

        InvalidateLibraryFacetSnapshot();

        // Canonicalize only changed rows; do not replace ItemsSource or
        // reapply the active filter, which could hide the selected track.
        if (matched > 0)
        {
            try
            {
                PlanEntry[] changed = candidates
                    .Where(entry =>
                        state.Tracks.TryGetValue(
                            entry.SourcePath, out TrackState? track)
                        && track.NeteaseSongId.HasValue)
                    .ToArray();

                await RefreshLibraryLyricsIndexAsync(
                    new LibraryPlan(changed), state);

                HashSet<string> changedPaths = changed
                    .Select(static entry => entry.SourcePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                ReconcileMatchActionRows(_allLibraryItems
                    .Where(row => changedPaths.Contains(row.Entry.SourcePath))
                    .ToArray());

                if (_selectedLibraryItem is LibraryDisplayItem selected
                    && changed.Any(entry =>
                        string.Equals(
                            entry.SourcePath,
                            selected.Entry.SourcePath,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    await ShowTrackDetailsAsync(selected);
                }

                UpdateBatchMatchActions();
                RefreshLiveSyncCounters();
            }
            catch (Exception exception)
            {
                failures++;
                AppDiagnostics.LogException(
                    "Could not reconcile new NetEase lyrics.", exception);
            }
        }

        string summary = L(
            $"{matched:N0} matched · {missing:N0} unmatched · {cachedLyrics:N0} lyrics cached · {warnings:N0} need review · {failures:N0} errors",
            $"成功匹配 {matched:N0} · 未匹配 {missing:N0} · 缓存歌词 {cachedLyrics:N0} · 待确认 {warnings:N0} · 错误 {failures:N0}");

        ShowInfo(
            cancelled
                ? InfoBarSeverity.Informational
                : failures > 0
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success,
            cancelled
                ? L("Matching cancelled", "已取消匹配")
                : L("NetEase matching completed", "网易云自动匹配完成"),
            summary);
        AddActivity(
            L("Automatic NetEase matching", "自动网易云匹配"),
            summary);
    }

    private async void SyncButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        await RunBackgroundSyncAsync();
    }

    private async Task RunBackgroundSyncAsync()
    {
        int runGeneration =
            ++_syncProgressGeneration;
        bool executionStarted = false;

        using CancellationTokenSource
            cancellation = new();

        _operationCancellation =
            cancellation;

        SetBusy(
            true,
            L(
                "Checking sync plan…",
                "正在检查同步计划……"));

        await Task.Yield();

        try
        {
            MusicLoomSettings settings =
                _settings
                ?? await LoadPersistedSettingsAsync(
                    cancellation.Token);

            LibraryPathSafety.ValidateStatePath(
                settings,
                DatabasePath);

            StateStore stateStore =
                new SqliteStateStore(
                    DatabasePath,
                    LegacyStatePath);
            StateDocument state =
                await stateStore.LoadAsync(
                    cancellation.Token);

            bool checkingPlan = true;

            Progress<ScanProgress>
                scanProgress = new(value =>
                {
                    if (!checkingPlan
                        || runGeneration != _syncProgressGeneration)
                    {
                        return;
                    }

                    if (value.Stage
                        == "ancillary")
                    {
                        SetOperationStatus(
                            L(
                                "Checking covers and playlists…",
                                "正在检查封面和播放列表……"));
                        SetIndeterminateProgress();
                        return;
                    }

                    if (value.Stage
                        == "complete")
                    {
                        SetOperationStatus(
                            L(
                                "Preparing sync…",
                                "正在准备同步……"));
                        SetIndeterminateProgress();
                        return;
                    }

                    SetOperationStatus(
                        L(
                            $"Checking tracks {value.Processed:N0}/{value.Total:N0}",
                            $"正在检查歌曲 {value.Processed:N0}/{value.Total:N0}"));
                    SetProgress(
                        value.Processed,
                        value.Total);
                });

            LibraryScanner scanner =
                new(
                    new AtlMetadataReader());

            LibraryPlan plan =
                await Task.Run(
                    async () =>
                        await scanner.BuildPlanAsync(
                            settings,
                            state,
                            scanProgress,
                            cancellation.Token),
                    cancellation.Token);

            checkingPlan = false;

            await RefreshLibraryLyricsIndexAsync(
                plan,
                state,
                cancellation.Token);

            await stateStore.SaveAsync(
                state,
                cancellation.Token);

            _settings = settings;
            _stateStore = stateStore;
            _state = state;
            _plan = plan;

            if (plan.PendingCount == 0
                && plan.ErrorCount == 0)
            {
                ShowInfo(
                    InfoBarSeverity.Success,
                    L(
                        "Already synced",
                        "已经同步"),
                    L(
                        "There are no pending changes.",
                        "没有待同步的更改。"));

                SetOperationStatus(
                    L(
                        "Library is up to date.",
                        "音乐库已经是最新。"));
                return;
            }

            bool confirmed =
                await ConfirmSyncPlanAsync(
                    plan,
                    state);

            if (!confirmed)
            {
                SetOperationStatus(
                    plan.ErrorCount > 0
                        ? L(
                            "Sync blocked by scan errors.",
                            "同步被扫描错误阻止。")
                        : L(
                            "Sync cancelled.",
                            "同步已取消。"));
                return;
            }

            AppDiagnostics.Log(
                $"Sync started: tracks={plan.PendingTrackCount}, " +
                $"audio={plan.AudioCopyCount}, covers={plan.CoverWriteCount}, " +
                $"playlists={plan.PlaylistWriteCount}.");

            using NeteaseClient client =
                new();
            CompositeFingerprintPcmDecoder
                decoder = new();
            NeteaseTrackProvider provider =
                new(
                    client,
                    decoder);

            ITrackLyricsWriter lyricsWriter =
                LyricsStorageModes.Normalize(
                    settings.Organizer.Lyrics
                        .StorageMode)
                    == LyricsStorageModes.Sidecar
                        ? new SidecarLrcLyricsWriter()
                        : new TagLibSharpLyricsWriter();

            SqliteLyricsCache lyricStore =
                new(DatabasePath);
            SyncExecutor executor =
                new(
                    new LocalLibraryMaterializer(),
                    provider,
                    provider,
                    lyricsWriter,
                    lyricsCache: lyricStore,
                    libraryLyricsCache: lyricStore,
                    embeddedLyricsCleaner: new TagLibSharpLyricsWriter(),
                    lyricsOffsetStore: lyricStore);

            SyncProgressTracker progressTracker = new();

            Progress<SyncExecutionProgress>
                syncProgress = new(value =>
                {
                    if (runGeneration != _syncProgressGeneration
                        || !progressTracker.ShouldApply(value))
                    {
                        return;
                    }

                    SetOperationStatus(
                        $"{FriendlyStage(value.Stage)} · " +
                        $"{value.Processed:N0}/{value.Total:N0} · " +
                        $"{Path.GetFileName(value.SourcePath)}");

                    SetProgress(
                        value.Processed,
                        value.Total);

                    UpdateLiveSyncRow(
                        value);
                });

            _liveNeteaseMatches.Clear();

            executionStarted = true;

            SyncExecutionSummary summary =
                await Task.Run(
                    async () =>
                        await executor.ExecuteAsync(
                            settings,
                            plan,
                            state,
                            stateStore,
                            syncProgress,
                            cancellation.Token,
                            recognizeUnmatched: false),
                    cancellation.Token);

            // Progress<T> dispatches asynchronously onto the UI thread.
            // Ignore late events once the persisted final state is being read.
            ++_syncProgressGeneration;

            string summaryText =
                L(
                    $"{summary.Local.CopiedTracks:N0} audio · " +
                    $"{summary.Local.CopiedCovers:N0} covers · " +
                    $"{summary.Local.WrittenPlaylists:N0} playlists · " +
                    $"{summary.WrittenLyrics:N0} lyrics · " +
                    $"{summary.Warnings.Count:N0} warnings · " +
                    $"{summary.Errors.Count:N0} errors",
                    $"音频 {summary.Local.CopiedTracks:N0} · " +
                    $"封面 {summary.Local.CopiedCovers:N0} · " +
                    $"播放列表 {summary.Local.WrittenPlaylists:N0} · " +
                    $"歌词 {summary.WrittenLyrics:N0} · " +
                    $"警告 {summary.Warnings.Count:N0} · " +
                    $"错误 {summary.Errors.Count:N0}");

            AppDiagnostics.Log(
                "Sync finished: " + summaryText);

            foreach (TrackSyncWarning warning
                in summary.Warnings)
            {
                LibraryDisplayItem? warningItem =
                    _allLibraryItems
                        .FirstOrDefault(
                            item =>
                                string.Equals(
                                    item.Entry.SourcePath,
                                    warning.SourcePath,
                                    StringComparison.OrdinalIgnoreCase));

                warningItem?.SetStatus(
                    "warning",
                    StatusLabel(
                        "warning"));

                SetTrackSyncState(
                    warning.SourcePath,
                    L("Warning", "警告"),
                    FormatTrackMatchWarning(
                        warning));
            }

            foreach (TrackSyncError error
                in summary.Errors)
            {
                LibraryDisplayItem? errorItem =
                    _allLibraryItems
                        .FirstOrDefault(
                            item =>
                                string.Equals(
                                    item.Entry.SourcePath,
                                    error.SourcePath,
                                    StringComparison.OrdinalIgnoreCase));

                errorItem?.SetStatus(
                    "error",
                    StatusLabel(
                        "error"));
            }

            RefreshLiveSyncCounters();

            foreach (TrackSyncError error
                in summary.Errors)
            {
                AppDiagnostics.Log(
                    $"Track error [{error.Stage}] {error.SourcePath}: {error.Message}");

                SetTrackSyncState(
                    error.SourcePath,
                    L("Error", "错误"),
                    $"[{error.Stage}] {UserFacingErrors.DescribeSyncError(error, IsChinese)}",
                    isError: true);
            }

            HashSet<string> failedSources =
                summary.Errors
                    .Select(
                        static error =>
                            error.SourcePath)
                    .ToHashSet(
                        StringComparer
                            .OrdinalIgnoreCase);
            HashSet<string> completedSources =
                summary.CompletedSourcePaths.ToHashSet(
                    StringComparer.OrdinalIgnoreCase);

            // Completion is a positive acknowledgement from the executor,
            // not merely the absence of an error. In particular a warning
            // is an intentionally incomplete, review-required track.
            foreach (PlanEntry entry
                in plan.Entries.Where(
                    entry =>
                        completedSources.Contains(entry.SourcePath)
                        && !failedSources.Contains(entry.SourcePath)))
            {
                SetTrackSyncState(
                    entry.SourcePath,
                    L("Synced", "已同步"),
                    L(
                        "Sync completed successfully.",
                        "同步已成功完成。"));
            }

            if (summary.Errors.Count > 0)
            {
                TrackSyncError first =
                    summary.Errors[0];

                ShowInfo(
                    InfoBarSeverity.Warning,
                    L(
                        "Sync completed with errors",
                        "同步完成，但有错误"),
                    L(
                        $"{summary.Errors.Count:N0} errors. First: {Path.GetFileName(first.SourcePath)} · {FriendlyStage(first.Stage)} · {UserFacingErrors.DescribeSyncError(first, IsChinese)}",
                        $"{summary.Errors.Count:N0} 个错误。第一个：{Path.GetFileName(first.SourcePath)} · {FriendlyStage(first.Stage)} · {UserFacingErrors.DescribeSyncError(first, IsChinese)}"));
            }
            else if (summary.Warnings.Count > 0)
            {
                TrackSyncWarning first =
                    summary.Warnings[0];

                ShowInfo(
                    InfoBarSeverity.Warning,
                    L(
                        "Sync completed with warnings",
                        "同步完成，但有警告"),
                    L(
                        $"{summary.Warnings.Count:N0} metadata mismatch warnings. First: {Path.GetFileName(first.SourcePath)} · {FormatTrackMatchWarning(first)}",
                        $"{summary.Warnings.Count:N0} 个元数据不一致警告。第一个：{Path.GetFileName(first.SourcePath)} · {FormatTrackMatchWarning(first)}"));
            }
            else
            {
                ShowInfo(
                    InfoBarSeverity.Success,
                    L(
                        "Sync complete",
                        "同步完成"),
                    summaryText);
            }

            await RefreshLibraryAfterSyncAsync(
                settings,
                summary,
                cancellation.Token);

            if (summary.Errors.Count > 0)
            {
                LibraryFilterBox.SelectedItem =
                    LibraryFilterErrorsItem;
                NavigateTo("library");
                ApplyLibraryFilter();
            }
            else if (summary.Warnings.Count > 0)
            {
                LibraryFilterBox.SelectedItem =
                    LibraryFilterWarningsItem;
                NavigateTo("library");
                ApplyLibraryFilter();
            }
        }
        catch (OperationCanceledException)
        {
            ++_syncProgressGeneration;

            // An interrupted sync may already have checkpointed successful
            // tracks. Replace any lingering "Syncing" row with the actual
            // persisted pending/synced/warning state.
            if (executionStarted
                && _settings is MusicLoomSettings cancelledSettings)
            {
                await RefreshLibraryAfterSyncAsync(
                    cancelledSettings,
                    null,
                    CancellationToken.None);
            }

            AppDiagnostics.Log(
                "Sync cancelled by user.");

            ShowInfo(
                InfoBarSeverity.Warning,
                L(
                    "Sync cancelled",
                    "同步已取消"),
                L(
                    "Completed work was checkpointed.",
                    "已完成的工作已经保存。"));

            SetOperationStatus(
                L(
                    "Sync cancelled.",
                    "同步已取消。"));
        }
        catch (Exception exception)
        {
            ++_syncProgressGeneration;

            if (executionStarted
                && _settings is MusicLoomSettings failedSettings)
            {
                await RefreshLibraryAfterSyncAsync(
                    failedSettings,
                    null,
                    CancellationToken.None);
            }

            AppDiagnostics.LogException(
                "Background sync failed.",
                exception);

            ShowInfo(
                InfoBarSeverity.Error,
                L(
                    "Sync failed",
                    "同步失败"),
                UserFacingErrors.Describe(
                    exception, IsChinese));
        }
        finally
        {
            ++_syncProgressGeneration;
            _operationCancellation = null;
            SetBusy(
                false,
                OperationStatusText.Text);
        }
    }

    private async Task<bool> ConfirmSyncPlanAsync(
        LibraryPlan plan,
        StateDocument state)
    {
        TrackSyncWarning[] warnings =
            plan.Entries
                .Select(entry =>
                    state.Tracks.TryGetValue(
                        entry.SourcePath,
                        out TrackState? trackState)
                        ? SyncExecutor.DetectMatchWarning(
                            entry,
                            trackState)
                        : null)
                .OfType<TrackSyncWarning>()
                .ToArray();

        PlanEntry[] errors =
            plan.Entries
                .Where(static entry =>
                    entry.Status == PlanEntryStatus.Error)
                .ToArray();

        (Grid content, Button cancel, Button confirm) =
            SyncConfirmationView.Create(
                plan,
                warnings,
                errors,
                L,
                FormatTrackMatchWarning);

        TaskCompletionSource<bool> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        cancel.Click += (_, _) =>
        {
            completion.TrySetResult(false);
            CloseMatchOverlay();
        };
        confirm.Click += (_, _) =>
        {
            completion.TrySetResult(true);
            CloseMatchOverlay();
        };

        CloseMatchOverlay();
        ShowAppOverlay(
            L("Confirm synchronization", "确认同步"),
            content,
            maxWidth: 740,
            maxHeight: 620,
            onClosed: () =>
                completion.TrySetResult(false),
            compact: true);

        return await completion.Task;
    }

    private void UpdateLiveSyncRow(
        SyncExecutionProgress progress)
    {
        if (progress.Stage == "write-playlist")
        {
            RefreshPlaylistReadinessFromDisk();
        }

        LibraryDisplayItem? item =
            _allLibraryItems
                .FirstOrDefault(
                    candidate =>
                        string.Equals(
                            candidate.Entry.SourcePath,
                            progress.SourcePath,
                            StringComparison.OrdinalIgnoreCase));

        if (item is null)
        {
            return;
        }

        if (progress.Stage == "match"
            && progress.NeteaseSongId
                is long songId)
        {
            _liveNeteaseMatches[
                progress.SourcePath] =
                new LiveNeteaseMatch(
                    songId,
                    progress.NeteaseTrackName,
                    progress.NeteaseTrackArtist,
                    progress.NeteaseTrackAlbum,
                    progress.NeteaseCoverUrl,
                    progress.Warning);

            if (ReferenceEquals(
                    item,
                    _selectedLibraryItem))
            {
                RefreshCurrentMatchCard(
                    item);
            }
        }

        TrackSyncWarning? warning =
            progress.Warning;

        if (warning is null
            && _liveNeteaseMatches.TryGetValue(
                progress.SourcePath,
                out LiveNeteaseMatch? liveMatch))
        {
            warning =
                liveMatch.Warning;
        }

        if (progress.Stage == "match"
            && warning is not null)
        {
            item.SetStatus(
                "warning",
                StatusLabel(
                    "warning"));
        }
        else if (progress.Stage == "complete")
        {
            item.SetStatus(
                warning is null
                    ? "synced"
                    : "warning",
                StatusLabel(
                    warning is null
                        ? "synced"
                        : "warning"));
        }
        else if (progress.Stage == "failed")
        {
            item.SetStatus(
                "error",
                StatusLabel(
                    "error"));
        }
        else if (progress.Stage
            is "copy-audio"
            or "state"
            or "inspect-lyrics"
            or "recognize"
            or "match"
            or "fetch-lyrics"
            or "write-lyrics"
            or "cleanup-old-destination")
        {
            item.SetStatus(
                "syncing",
                L(
                    "Syncing",
                    "同步中"));
        }

        if (warning is not null
            && progress.Stage
                is "match" or "complete")
        {
            SetTrackSyncState(
                item.Entry.SourcePath,
                L("Warning", "警告"),
                FormatTrackMatchWarning(
                    warning));
        }
        else
        {
            SetTrackSyncState(
                item.Entry.SourcePath,
                progress.Stage == "failed"
                    ? L("Error", "错误")
                    : progress.Stage == "complete"
                        ? L("Synced", "已同步")
                        : FriendlyStage(
                            progress.Stage),
                L(
                    $"[{progress.Stage}] {progress.Processed:N0}/{progress.Total:N0}",
                    $"[{progress.Stage}] {progress.Processed:N0}/{progress.Total:N0}"),
                progress.Stage == "failed");
        }

        if (ReferenceEquals(
                item,
                _selectedLibraryItem))
        {
            TrackStatusText.Text =
                item.StatusText;
            TrackStatusIcon.Glyph =
                item.StatusGlyph;
            TrackStatusIcon.Foreground =
                item.StatusBrush;
        }

        RefreshLiveSyncCounters();
        UpdateBatchMatchActions();
    }

    private void SetTrackSyncState(
        string sourcePath,
        string status,
        string detail,
        bool isError = false)
    {
        string kind =
            isError
                ? "error"
                : status == L("Warning", "警告")
                    ? "warning"
                    : status == L("Synced", "已同步")
                        ? "synced"
                        : status == L("Pending sync", "待同步")
                            ? "pending"
                            : "syncing";

        _trackSyncStates[sourcePath] =
            new TrackSyncViewState(
                status,
                detail,
                DateTimeOffset.Now,
                isError,
                kind);

        if (_selectedLibraryItem
                is LibraryDisplayItem selected
            && string.Equals(
                selected.Entry.SourcePath,
                sourcePath,
                StringComparison.OrdinalIgnoreCase))
        {
            RefreshTrackSyncTab(
                selected);
        }
    }

    private void RefreshTrackSyncTab(
        LibraryDisplayItem? item)
    {
        if (item is null)
        {
            SetTrackSyncTabVisual(
                "pending");
            TrackSyncStateText.Text =
                L(
                    "No sync information.",
                    "暂无同步信息。");
            TrackSyncTimeText.Text =
                string.Empty;
            TrackSyncLogText.Text =
                string.Empty;
            return;
        }

        if (_trackSyncStates.TryGetValue(
                item.Entry.SourcePath,
                out TrackSyncViewState? latest))
        {
            SetTrackSyncTabVisual(
                latest.IsError
                    ? "error"
                    : item.Kind);
            TrackSyncStateText.Text =
                latest.Status;
            TrackSyncTimeText.Text =
                L(
                    $"Updated {latest.UpdatedAt:G}",
                    $"更新于 {latest.UpdatedAt:G}");
            TrackSyncLogText.Text =
                latest.Detail;
            return;
        }

        SetTrackSyncTabVisual(
            item.Kind);

        TrackSyncTimeText.Text =
            L(
                "Current library plan",
                "当前音乐库状态");

        if (item.Kind == "error")
        {
            TrackSyncStateText.Text =
                L("Error", "错误");
            TrackSyncLogText.Text =
                (item.Entry.Detail is string rawDetail
                    ? UserFacingErrors.TranslateCoreMessage(rawDetail, IsChinese)
                    : null)
                ?? L(
                    "The current scan reports an error.",
                    "当前扫描报告了错误。");
        }
        else if (item.Kind == "synced")
        {
            TrackSyncStateText.Text =
                L("Synced", "已同步");
            TrackSyncLogText.Text =
                L(
                    "No sync error is recorded for this track.",
                    "此歌曲没有记录到同步错误。");
        }
        else if (item.Kind == "warning")
        {
            TrackSyncWarning? warning =
                GetStoredMatchWarning(
                    item.Entry);

            TrackSyncStateText.Text =
                L("Warning", "警告");
            TrackSyncLogText.Text =
                warning is null
                    ? L(
                        "The NetEase match differs from the local title or artist.",
                        "网易云匹配与本地记录的歌曲名或艺术家不一致。")
                    : FormatTrackMatchWarning(
                        warning);
        }
        else if (item.Kind == "syncing")
        {
            TrackSyncStateText.Text =
                L("Syncing", "同步中");
            TrackSyncLogText.Text =
                (item.Entry.Detail is string rawDetail
                    ? UserFacingErrors.TranslateCoreMessage(rawDetail, IsChinese)
                    : null)
                ?? L(
                    "Synchronization is in progress.",
                    "正在同步。");
        }
        else
        {
            TrackSyncStateText.Text =
                L(
                    "Pending sync",
                    "待同步");
            TrackSyncLogText.Text =
                (item.Entry.Detail is string rawDetail
                    ? UserFacingErrors.TranslateCoreMessage(rawDetail, IsChinese)
                    : null)
                ?? L(
                    "This track has pending work.",
                    "此歌曲存在待处理的同步工作。");
        }
    }

    private void SetTrackSyncTabVisual(
        string kind)
    {
        TrackSyncTabStatusIcon.Glyph =
            kind switch
            {
                "synced" => "\uE73E",
                "error" => "\uEA39",
                "warning" => "\uE7BA",
                "syncing" => "\uE895",
                _ => "\uE823",
            };

        TrackSyncTabStatusIcon.Foreground =
            kind switch
            {
                "synced" =>
                    new Microsoft.UI.Xaml.Media
                        .SolidColorBrush(
                            Color.FromArgb(
                                255,
                                16,
                                124,
                                65)),
                "error" =>
                    new Microsoft.UI.Xaml.Media
                        .SolidColorBrush(
                            Color.FromArgb(
                                255,
                                196,
                                43,
                                28)),
                "warning" =>
                    new Microsoft.UI.Xaml.Media
                        .SolidColorBrush(
                            Color.FromArgb(
                                255,
                                202,
                                116,
                                0)),
                "syncing" =>
                    Application.Current.Resources[
                        "AccentTextFillColorPrimaryBrush"]
                    as Microsoft.UI.Xaml.Media.Brush,
                _ =>
                    new Microsoft.UI.Xaml.Media
                        .SolidColorBrush(
                            Color.FromArgb(
                                255,
                                202,
                                116,
                                0)),
            };
    }

    private void ReconcileSyncLogsWithCurrentRows()
    {
        Dictionary<string, string> currentKinds =
            _allLibraryItems.ToDictionary(
                static item => item.Entry.SourcePath,
                static item => item.Kind,
                StringComparer.OrdinalIgnoreCase);

        foreach (string sourcePath in
            _trackSyncStates.Keys.ToArray())
        {
            if (!currentKinds.TryGetValue(
                    sourcePath,
                    out string? kind)
                || _trackSyncStates[sourcePath].Kind
                    != kind)
            {
                _trackSyncStates.Remove(sourcePath);
            }
        }
    }

    private void RefreshLiveSyncCounters()
    {
        PendingCountText.Text =
            _allLibraryItems
                .Count(static item =>
                    item.Kind
                    is "pending" or "syncing")
                .ToString("N0");

        WarningCountText.Text =
            _allLibraryItems
                .Count(static item =>
                    item.Kind == "warning")
                .ToString("N0");

        ErrorCountText.Text =
            _allLibraryItems
                .Count(static item =>
                    item.Kind == "error")
                .ToString("N0");
    }

    private async Task RefreshLibraryAfterSyncAsync(
        MusicLoomSettings settings,
        SyncExecutionSummary? summary,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<TrackSyncError> runErrors =
                summary?.Errors ?? [];
            IReadOnlyList<string> completedSources =
                summary?.CompletedSourcePaths ?? [];

            SetOperationStatus(
                L(
                    "Refreshing sync state…",
                    "正在刷新同步状态……"));
            SetIndeterminateProgress();

            StateStore stateStore =
                new SqliteStateStore(
                    DatabasePath,
                    LegacyStatePath);
            StateDocument state =
                await stateStore.LoadAsync(
                    cancellationToken);

            LibraryScanner scanner =
                new(
                    new AtlMetadataReader());

            LibraryPlan freshPlan =
                await Task.Run(
                    async () =>
                        await scanner.BuildPlanAsync(
                            settings,
                            state,
                            cancellationToken:
                                cancellationToken),
                    cancellationToken);

            await RefreshLibraryLyricsIndexAsync(
                freshPlan,
                state,
                cancellationToken);

            await stateStore.SaveAsync(
                state,
                cancellationToken);

            _settings = settings;
            _stateStore = stateStore;
            _state = state;
            _plan = freshPlan;
            _liveNeteaseMatches.Clear();
            _trackSyncStates.Clear();

            string? selectedSource =
                _selectedLibraryItem?.Entry.SourcePath;

            Dictionary<string, PlanEntry>
                freshBySource =
                    freshPlan.Entries
                        .ToDictionary(
                            static entry =>
                                entry.SourcePath,
                            StringComparer.OrdinalIgnoreCase);

            bool structureChanged =
                freshBySource.Count
                != _allLibraryItems.Count
                || _allLibraryItems.Any(
                    item =>
                        !freshBySource.ContainsKey(
                            item.Entry.SourcePath));

            if (structureChanged)
            {
                PopulateLibrary(
                    settings,
                    freshPlan);

                LibraryDisplayItem? restored =
                    _allLibraryItems.FirstOrDefault(
                        item => string.Equals(
                            item.Entry.SourcePath,
                            selectedSource,
                            StringComparison.OrdinalIgnoreCase));

                if (restored is not null
                    && LibraryList.Items.Contains(restored))
                {
                    LibraryList.SelectedItem = restored;
                }
            }
            else
            {
                foreach (LibraryDisplayItem item
                    in _allLibraryItems)
                {
                    PlanEntry entry =
                        freshBySource[
                            item.Entry.SourcePath];

                    item.UpdateEntry(
                        entry);

                    string kind =
                        GetLibraryDisplayKind(
                            entry);

                    item.SetStatus(
                        kind,
                        StatusLabel(
                            kind));
                }

                ApplyLibraryFilter();
            }

            _manualPendingSources.RemoveWhere(
                sourcePath =>
                    !freshBySource.TryGetValue(
                        sourcePath,
                        out PlanEntry? freshEntry)
                    || freshEntry.Status
                        == PlanEntryStatus.Unchanged);

            // The refreshed scan is authoritative. Rebuild only diagnostics
            // that still correspond to the actual status of each source,
            // rather than retaining a stale "Syncing" or "Synced" log.
            HashSet<string> failedSources =
                runErrors
                    .Select(static error =>
                        error.SourcePath)
                    .ToHashSet(
                        StringComparer.OrdinalIgnoreCase);

            foreach (LibraryDisplayItem item
                in _allLibraryItems)
            {
                if (item.Kind != "warning")
                {
                    continue;
                }

                TrackSyncWarning? warning =
                    GetStoredMatchWarning(item.Entry);
                if (warning is not null)
                {
                    SetTrackSyncState(
                        item.Entry.SourcePath,
                        L("Warning", "警告"),
                        FormatTrackMatchWarning(warning));
                }
            }

            foreach (string sourcePath
                in completedSources)
            {
                LibraryDisplayItem? completed =
                    _allLibraryItems.FirstOrDefault(
                        item => string.Equals(
                            item.Entry.SourcePath,
                            sourcePath,
                            StringComparison.OrdinalIgnoreCase));

                if (completed?.Kind == "synced"
                    && !failedSources.Contains(sourcePath))
                {
                    SetTrackSyncState(
                        sourcePath,
                        L("Synced", "已同步"),
                        L(
                            "Sync completed successfully.",
                            "同步已成功完成。"));
                }
            }

            foreach (TrackSyncError error
                in runErrors)
            {
                LibraryDisplayItem? errorItem =
                    _allLibraryItems.FirstOrDefault(
                        item => string.Equals(
                            item.Entry.SourcePath,
                            error.SourcePath,
                            StringComparison.OrdinalIgnoreCase));

                if (errorItem is null)
                {
                    continue;
                }

                errorItem.SetStatus(
                    "error",
                    StatusLabel("error"));
                SetTrackSyncState(
                    error.SourcePath,
                    L("Error", "错误"),
                    $"[{error.Stage}] {UserFacingErrors.DescribeSyncError(error, IsChinese)}",
                    isError: true);
            }

            RefreshLiveSyncCounters();

            // Status and cached NetEase data may have changed while syncing.
            // Refresh details for the selected row, never for the playing row.
            if (LibraryList.SelectedItem
                is LibraryDisplayItem selected)
            {
                TrackStatusText.Text = selected.StatusText;
                TrackStatusIcon.Glyph = selected.StatusGlyph;
                TrackStatusIcon.Foreground = selected.StatusBrush;
                RefreshTrackSyncTab(selected);
                RefreshCurrentMatchCard(selected);
            }

            await LibraryDatabase.SavePlanAsync(
                DatabasePath,
                settings.Path.SourceDirectory,
                settings.Path.DestinationDirectory,
                freshPlan,
                cancellationToken);
            RefreshDatabaseSize();

            PopulatePlaylists(
                await Task.Run(
                    () => BuildPlaylists(
                        settings),
                    cancellationToken));

            SetOperationStatus(
                L(
                    $"{freshPlan.TotalCount:N0} tracks · {freshPlan.PendingTrackCount:N0} pending · {freshPlan.ErrorCount:N0} errors",
                    $"{freshPlan.TotalCount:N0} 首歌曲 · {freshPlan.PendingTrackCount:N0} 首待同步 · {freshPlan.ErrorCount:N0} 个错误"));
        }
        catch (Exception exception)
            when (exception
                is not OperationCanceledException)
        {
            AppDiagnostics.LogException(
                "Could not refresh the library after sync.",
                exception);

            ShowInfo(
                InfoBarSeverity.Warning,
                L(
                    "Sync finished, but the library view could not refresh",
                    "同步已完成，但无法刷新音乐库视图"),
                UserFacingErrors.Describe(
                    exception, IsChinese));
        }
    }

    private async void CancelButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        CancellationTokenSource?
            cancellation =
                _operationCancellation;

        if (cancellation is null)
        {
            return;
        }

        CancelButton.IsEnabled = false;
        PlaylistCancelButton.IsEnabled = false;
        SetOperationStatus(
            L(
                "Cancelling…",
                "正在取消……"));

        await cancellation.CancelAsync();
    }

    private async Task<bool> ScanLibraryAsync(
        bool clearInfoBar,
        bool recordActivity)
    {
        MusicLoomSettings configured =
            _settings
            ?? await LoadPersistedSettingsAsync();

        if (string.IsNullOrWhiteSpace(
                configured.Path.SourceDirectory)
            || string.IsNullOrWhiteSpace(
                configured.Path.DestinationDirectory))
        {
            NavigateTo("settings");
            ShowInfo(
                InfoBarSeverity.Warning,
                L(
                    "Set up your library first",
                    "请先设置音乐库"),
                L(
                    "Choose a source and destination in Settings.",
                    "请在“设置”中选择源音乐库和目标目录。"));
            return false;
        }

        using CancellationTokenSource
            cancellation = new();
        _operationCancellation =
            cancellation;
        bool scanning = true;

        SetBusy(
            true,
            L(
                "Scanning library…",
                "正在扫描音乐库……"));

        if (clearInfoBar)
        {
            AppInfoBar.IsOpen = false;
        }

        await Task.Yield();

        try
        {
            MusicLoomSettings settings =
                _settings
                ?? await LoadPersistedSettingsAsync(
                    cancellation.Token);

            _settings = settings;
            _uiLanguage =
                NormalizeLanguage(
                    settings.Ui.Language);

            LibraryPathSafety.ValidateStatePath(
                settings,
                DatabasePath);

            StateStore stateStore =
                new SqliteStateStore(
                    DatabasePath,
                    LegacyStatePath);
            StateDocument state =
                await stateStore.LoadAsync(
                    cancellation.Token);

            Progress<ScanProgress>
                progress = new(value =>
                {
                    if (!scanning)
                    {
                        return;
                    }

                    if (value.Stage == "ancillary")
                    {
                        OperationProgressBar.Visibility =
                            Visibility.Visible;
                        OperationProgressBar.IsIndeterminate =
                            true;
                        OperationStatusText.Text =
                            L(
                                "Organizing covers and playlists…",
                                "正在整理封面和播放列表……");
                        return;
                    }

                    if (value.Stage == "complete")
                    {
                        OperationProgressBar.Visibility =
                            Visibility.Visible;
                        OperationProgressBar.IsIndeterminate =
                            true;
                        OperationStatusText.Text =
                            L(
                                "Building library view…",
                                "正在构建音乐库视图……");
                        return;
                    }

                    string current =
                        string.IsNullOrWhiteSpace(
                            value.CurrentPath)
                            ? string.Empty
                            : " · " +
                                Path.GetFileName(
                                    value.CurrentPath);

                    OperationStatusText.Text =
                        L(
                            $"Reading tracks {value.Processed:N0}/{value.Total:N0}{current}",
                            $"正在读取歌曲 {value.Processed:N0}/{value.Total:N0}{current}");

                    SetProgress(
                        value.Processed,
                        value.Total);
                });

            LibraryScanner scanner =
                new(
                    new AtlMetadataReader());

            LibraryPlan plan =
                await Task.Run(
                    async () =>
                        await scanner.BuildPlanAsync(
                            settings,
                            state,
                            progress,
                            cancellation.Token),
                    cancellation.Token);

            scanning = false;
            SetOperationStatus(
                L(
                    "Indexing embedded and local lyrics…",
                    "正在索引内嵌及本地歌词……"));

            await RefreshLibraryLyricsIndexAsync(
                plan,
                state,
                cancellation.Token);

            // Scans may enrich MusicLoom's private state cache (for example
            // migrating TITLE metadata) but never mutate the source library.
            await stateStore.SaveAsync(
                state,
                cancellation.Token);

            _state = state;
            _stateStore = stateStore;
            _plan = plan;
            _liveNeteaseMatches.Clear();

            // A manual pending override only matters until the next scan
            // includes that source as actual pending work.
            HashSet<string> pendingSources =
                plan.Entries
                    .Where(static entry =>
                        entry.Status
                        is PlanEntryStatus.New or PlanEntryStatus.Modified)
                    .Select(static entry =>
                        entry.SourcePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _manualPendingSources.IntersectWith(pendingSources);

            OperationStatusText.Text =
                L(
                    "Building library view…",
                    "正在构建音乐库视图……");
            OperationProgressBar.IsIndeterminate =
                true;
            await Task.Yield();

            PopulateLibrary(
                settings,
                plan);
            ReconcileSyncLogsWithCurrentRows();

            await LibraryDatabase.SavePlanAsync(
                DatabasePath,
                settings.Path.SourceDirectory,
                settings.Path.DestinationDirectory,
                plan,
                cancellation.Token);
            RefreshDatabaseSize();

            PlaylistDisplayItem[] playlists =
                await Task.Run(
                    () =>
                        BuildPlaylists(
                            settings),
                    cancellation.Token);
            PopulatePlaylists(playlists);

            UpdateLibrarySummary(plan);

            string summary =
                L(
                    $"{plan.TotalCount:N0} tracks · " +
                    $"{plan.PendingTrackCount:N0} pending · " +
                    $"{plan.ErrorCount:N0} errors",
                    $"{plan.TotalCount:N0} 首歌曲 · " +
                    $"{plan.PendingTrackCount:N0} 首待同步 · " +
                    $"{plan.ErrorCount:N0} 个错误");

            OperationStatusText.Text =
                summary;

            if (recordActivity)
            {
                AddActivity(
                    L(
                        "Scan complete",
                        "扫描完成"),
                    summary);
            }

            if (stateStore.RecoveryWarning
                is string recoveryWarning)
            {
                AddActivity(
                    L(
                        "State recovered",
                        "状态已恢复"),
                    recoveryWarning);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            OperationStatusText.Text =
                L(
                    "Scan cancelled.",
                    "扫描已取消。");
            return false;
        }
        catch (Exception exception)
        {
            _plan = null;
            _allLibraryItems.Clear();
            LibraryList.ItemsSource = null;
            UpdateLibraryEmptyState();

            HandleUiException(
                L(
                    "Scan failed.",
                    "扫描失败。"),
                exception);
            return false;
        }
        finally
        {
            scanning = false;
            _operationCancellation = null;
            SetBusy(
                false,
                OperationStatusText.Text);
        }
    }

    private void PopulateLibrary(
        MusicLoomSettings settings,
        LibraryPlan plan)
    {
        string? playingSource =
            _playingLibraryItem
                ?.Entry.SourcePath;
        float[]? playingWaveform = _playingLibraryItem?.WaveformSamples;
        string[] queueSources =
            _playbackQueue
                .Select(static item =>
                    item.Entry.SourcePath)
                .ToArray();

        _allLibraryItems.Clear();
        int artworkGeneration =
            ++_artworkGeneration;

        foreach (PlanEntry entry
            in plan.Entries)
        {
            string title =
                entry.Metadata?.Title
                ?? Path.GetFileNameWithoutExtension(
                    entry.SourcePath);

            string artist =
                FormatArtistsForDisplay(
                    entry.Metadata?.AlbumArtist
                    ?? entry.Metadata?.Artist)
                ?? L(
                    "Unknown artist",
                    "未知艺术家");

            string album =
                entry.Metadata?.Album
                ?? L(
                    "Unknown album",
                    "未知专辑");

            string kind =
                GetLibraryDisplayKind(
                    entry);

            string relative =
                MakeRelative(
                    settings.Path.SourceDirectory,
                    entry.SourcePath);

            LibraryDisplayItem displayItem =
                new(
                    entry,
                    kind,
                    title,
                    artist,
                    album,
                    relative,
                    StatusLabel(kind));

            displayItem.ApplyLongTextMode(
                CurrentLongTextMode);
            displayItem.SetDarkTheme(
                AppRoot.ActualTheme == ElementTheme.Dark);

            if (playingSource is not null
                && string.Equals(
                    playingSource,
                    entry.SourcePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                displayItem.SetPlaying(true);
                displayItem.SetWaveform(playingWaveform);
                _playingLibraryItem =
                    displayItem;
            }

            _allLibraryItems.Add(
                displayItem);
        }

        if (queueSources.Length > 0)
        {
            Dictionary<string, LibraryDisplayItem>
                bySource =
                    _allLibraryItems
                        .ToDictionary(
                            static item =>
                                item.Entry.SourcePath,
                            StringComparer.OrdinalIgnoreCase);

            _playbackQueue =
                queueSources
                    .Select(source =>
                        bySource.TryGetValue(
                            source,
                            out LibraryDisplayItem? item)
                            ? item
                            : null)
                    .OfType<LibraryDisplayItem>()
                    .ToArray();

            _playbackQueueIndex =
                _playingLibraryItem is null
                    ? -1
                    : Array.IndexOf(
                        _playbackQueue,
                        _playingLibraryItem);
        }

        LibraryEmptyPanel.Visibility =
            Visibility.Collapsed;
        LibraryList.Visibility =
            Visibility.Visible;

        ApplyLibraryFilter();

        SyncButton.IsEnabled =
            !_busy
            && plan.PendingCount > 0;

        _ = LoadArtworkThumbnailsAsync(
            settings,
            artworkGeneration);

        if (LibraryList.Items.Count > 0)
        {
            LibraryList.SelectedIndex = -1;
        }

        HideDetailPane();
    }

    private void UpdateLibrarySummary(
        LibraryPlan plan)
    {
        _ = plan;
        RefreshLiveSyncCounters();
    }

    private void UpdateLibraryEmptyState()
    {
        bool configured =
            _settings is MusicLoomSettings settings
            && !string.IsNullOrWhiteSpace(
                settings.Path.SourceDirectory)
            && !string.IsNullOrWhiteSpace(
                settings.Path.DestinationDirectory);

        bool empty =
            _allLibraryItems.Count == 0;

        AutoMatchButton.IsEnabled = !_busy && configured;

        LibraryList.Visibility =
            empty
                ? Visibility.Collapsed
                : Visibility.Visible;
        LibraryEmptyPanel.Visibility =
            empty
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (!empty)
        {
            return;
        }

        LibraryEmptyTitleText.Text =
            configured
                ? L(
                    "Ready to scan",
                    "可以开始扫描")
                : L(
                    "Set up your music library",
                    "设置你的音乐库");

        LibraryEmptyBodyText.Text =
            configured
                ? L(
                    "Refresh the library to load tracks, artwork, lyrics and sync status.",
                    "刷新音乐库以加载歌曲、封面、歌词和同步状态。")
                : L(
                    "Choose the source and destination once. MusicLoom keeps the source read-only.",
                    "只需选择一次源音乐库和目标目录。MusicLoom 会保持源目录只读。");
    }

    private void LibrarySearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e) =>
        ApplyLibraryFilter();

    private void LibraryFilterBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_refreshingLocalizedChoices)
        {
            ApplyLibraryFilter();
        }
    }

    private void LibraryFacetFilter_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingLocalizedChoices)
            ApplyLibraryFilter();
    }

    private void LibrarySortBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_refreshingLocalizedChoices)
        {
            ApplyLibraryFilter();
        }
    }

    private void LibrarySortDirectionBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_refreshingLocalizedChoices)
        {
            ApplyLibraryFilter();
        }
    }

    private void ApplyLibraryFilter()
    {
        if (LibraryList is null
            || LibrarySearchBox is null
            || LibraryFilterBox is null
            || LibrarySortBox is null
            || LibraryLyricsFacetBox is null
            || LibraryNeteaseFacetBox is null
            || LibraryQqFacetBox is null
            || LibraryKugouFacetBox is null)
        {
            return;
        }

        string query =
            LibrarySearchBox.Text.Trim();

        string filter =
            (LibraryFilterBox.SelectedItem
                as ComboBoxItem)
                ?.Tag?.ToString()
            ?? "all";

        string sort =
            (LibrarySortBox.SelectedItem
                as ComboBoxItem)
                ?.Tag?.ToString()
            ?? "artist";

        IEnumerable<LibraryDisplayItem>
            items = _allLibraryItems;
        EnsureLibraryFacetSnapshot();

        if (filter != "all")
        {
            items = items.Where(
                item =>
                    string.Equals(
                        item.Kind,
                        filter,
                        StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(
                query))
        {
            items = items.Where(
                item =>
                    item.SearchText.Contains(
                        query,
                        StringComparison
                            .OrdinalIgnoreCase));
        }

        items = items.Where(MatchesLibraryFacets);

        items =
            SortLibraryItems(
                items,
                sort);

        bool descending =
            string.Equals(
                (LibrarySortDirectionBox.SelectedItem
                    as ComboBoxItem)
                    ?.Tag?.ToString(),
                "desc",
                StringComparison.Ordinal);

        if (descending)
        {
            items = items.Reverse();
        }

        LibraryDisplayItem[] visible =
            items.ToArray();

        LibraryDisplayItem?
            selected =
                LibraryList.SelectedItem
                    as LibraryDisplayItem;

        LibraryList.ItemsSource =
            visible;

        if (selected is not null
            && visible.Contains(selected))
        {
            LibraryList.SelectedItem =
                selected;
        }
        else if (selected is not null)
        {
            HideDetailPane();
        }

        LibraryVisibleCountText.Text =
            L(
                $"{visible.Length:N0} tracks",
                $"{visible.Length:N0} 首歌曲");
    }

    private static IOrderedEnumerable<LibraryDisplayItem>
        SortLibraryItems(
            IEnumerable<LibraryDisplayItem> items,
            string sort)
    {
        StringComparer comparer =
            StringComparer
                .CurrentCultureIgnoreCase;

        return sort switch
        {
            "album" =>
                items
                    .OrderBy(
                        static item =>
                            item.Album,
                        comparer)
                    .ThenBy(
                        static item =>
                            item.Artist,
                        comparer)
                    .ThenBy(
                        static item =>
                            item.Title,
                        comparer)
                    .ThenBy(
                        static item =>
                            item.RelativePath,
                        comparer),
            "artist" =>
                items
                    .OrderBy(
                        static item =>
                            item.Artist,
                        comparer)
                    .ThenBy(
                        static item =>
                            item.Album,
                        comparer)
                    .ThenBy(
                        static item =>
                            item.Title,
                        comparer)
                    .ThenBy(
                        static item =>
                            item.RelativePath,
                        comparer),
            _ =>
                items
                    .OrderBy(
                        static item =>
                            item.Title,
                        comparer)
                    .ThenBy(
                        static item =>
                            item.Artist,
                        comparer)
                    .ThenBy(
                        static item =>
                            item.Album,
                        comparer)
                    .ThenBy(
                        static item =>
                            item.RelativePath,
                        comparer),
        };
    }

    private string GetLibraryDisplayKind(
        PlanEntry entry) =>
        LibraryMatchStatusResolver.GetKind(entry, _state);

    private void ReconcileMatchActionRows(
        IReadOnlyList<LibraryDisplayItem> candidates)
    {
        if (_state is null
            || _settings is not MusicLoomSettings settings
            || candidates.Count == 0)
        {
            return;
        }

        // Match confirmation/clearing changes the persisted source state.
        // Reconcile only affected tracks, never rebuild the ListView or
        // reapply the filter (which could hide the just-edited selection).
        IReadOnlyDictionary<string, LibraryMatchRowState> changed =
            LibraryMatchStatusResolver.Reconcile(
                candidates.Select(static item => item.Entry),
                _state,
                settings);
        if (_plan is LibraryPlan currentPlan)
        {
            _plan = new LibraryPlan(
                currentPlan.Entries
                    .Select(entry =>
                        changed.TryGetValue(
                            entry.SourcePath, out LibraryMatchRowState? row)
                            ? row.Entry
                            : entry)
                    .ToArray(),
                currentPlan.Covers,
                currentPlan.Playlists);
        }

        foreach (LibraryDisplayItem item in candidates.DistinctBy(
                     static item => item.Entry.SourcePath,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (!changed.TryGetValue(
                    item.Entry.SourcePath, out LibraryMatchRowState? row))
            {
                continue;
            }

            item.UpdateEntry(row.Entry);
            item.SetStatus(row.Kind, StatusLabel(row.Kind));

            if (row.Entry.Status == PlanEntryStatus.Unchanged)
            {
                _manualPendingSources.Remove(row.Entry.SourcePath);
            }
            else if (row.Entry.Status
                is PlanEntryStatus.New or PlanEntryStatus.Modified)
            {
                _manualPendingSources.Add(row.Entry.SourcePath);
            }

            string detail = row.Kind switch
            {
                "warning" when GetStoredMatchWarning(row.Entry)
                    is TrackSyncWarning warning =>
                    FormatTrackMatchWarning(warning),
                "synced" => L(
                    "Synchronization completed successfully.",
                    "同步已成功完成。"),
                _ when _trackSyncStates.TryGetValue(
                    row.Entry.SourcePath, out TrackSyncViewState? previous) =>
                    previous.Detail,
                _ => L(
                    "This track has pending synchronization work.",
                    "此歌曲存在待处理的同步工作。"),
            };
            SetTrackSyncState(
                row.Entry.SourcePath,
                StatusLabel(row.Kind),
                detail,
                isError: row.Kind == "error");
        }

        ReconcileSyncLogsWithCurrentRows();
        RefreshLiveSyncCounters();
        UpdateBatchMatchActions();
        SyncButton.IsEnabled =
            !_busy && (_manualPendingSources.Count > 0
                || _plan is { PendingCount: > 0 });

        // Update the detail header immediately but preserve the actual
        // selected row and the current filter until the user changes them.
        if (_selectedLibraryItem is LibraryDisplayItem selected
            && changed.ContainsKey(selected.Entry.SourcePath))
        {
            TrackStatusText.Text = selected.StatusText;
            TrackStatusIcon.Glyph = selected.StatusGlyph;
            TrackStatusIcon.Foreground = selected.StatusBrush;
            RefreshTrackSyncTab(selected);
        }
    }

    private TrackSyncWarning? GetStoredMatchWarning(
        PlanEntry entry)
    {
        if (_state is null
            || !_state.Tracks.TryGetValue(
                entry.SourcePath,
                out TrackState? state))
        {
            return null;
        }

        return SyncExecutor
            .DetectMatchWarning(
                entry,
                state);
    }

    private string FormatTrackMatchWarning(
        TrackSyncWarning warning)
    {
        List<string> differences = [];

        if (warning.TitleMismatch)
        {
            differences.Add(
                L(
                    $"title “{warning.RecordedTitle}” → “{warning.MatchedTitle}”",
                    $"歌曲名“{warning.RecordedTitle}” → “{warning.MatchedTitle}”"));
        }

        if (warning.ArtistMismatch)
        {
            differences.Add(
                L(
                    $"artist “{warning.RecordedArtist}” → “{warning.MatchedArtist}”",
                    $"艺术家“{warning.RecordedArtist}” → “{warning.MatchedArtist}”"));
        }

        return L(
            "NetEase match differs from local metadata: ",
            "网易云匹配与本地记录不一致：")
            + string.Join(
                L("; ", "；"),
                differences);
    }

    private async void LibraryList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        UpdateBatchMatchActions();

        if (LibraryList.SelectedItems.Count > 1)
        {
            // A multi-selection has no meaningful single-track detail.
            // Hide playback, file actions, match and lyric controls.
            ++_detailRequestId;
            _selectedLibraryItem = null;
            HideDetailPane();
            return;
        }

        // Extended selection is for Ctrl/Shift batch operations. The right
        // detail pane still follows the most recently selected item and
        // never switches to the playing track.
        LibraryDisplayItem? item =
            e.AddedItems.OfType<LibraryDisplayItem>().LastOrDefault()
            ?? (_selectedLibraryItem is not null
                && LibraryList.SelectedItems.Contains(_selectedLibraryItem)
                    ? _selectedLibraryItem
                    : LibraryList.SelectedItem as LibraryDisplayItem);

        if (item is null)
        {
            ++_detailRequestId;
            _selectedLibraryItem = null;
            OpenMatchDialogButton.IsEnabled = false;
            HideDetailPane();
            ResetTrackDetails();
            return;
        }

        if (ReferenceEquals(_selectedLibraryItem, item))
        {
            return;
        }

        _selectedLibraryItem = item;
        // Clear both provider cards synchronously. A previous song's match
        // must never masquerade as the newly selected song while SQLite
        // is still loading.
        ClearCloudMatchPanels();
        OpenMatchDialogButton.IsEnabled = !_busy;
        UpdatePlayerVisualState();
        ShowDetailPane();
        await ShowTrackDetailsAsync(item);
        await RefreshCloudMatchTabsAsync(item);
    }

    private void ShowDetailPane()
    {
        if (DetailPane.Visibility == Visibility.Visible)
        {
            // Keep the user's resized pane widths when changing songs.
            return;
        }

        LibraryListColumn.Width =
            new GridLength(
                5,
                GridUnitType.Star);
        LibrarySplitterColumn.Width =
            new GridLength(12);
        LibraryDetailColumn.Width =
            new GridLength(
                7,
                GridUnitType.Star);
        LibraryDetailColumn.MinWidth = 420;

        LibrarySplitter.Visibility =
            Visibility.Visible;
        DetailPane.Visibility =
            Visibility.Visible;
    }

    private void HideDetailPane()
    {
        LibraryListColumn.Width =
            new GridLength(
                1,
                GridUnitType.Star);
        LibrarySplitterColumn.Width =
            new GridLength(0);
        LibraryDetailColumn.MinWidth = 0;
        LibraryDetailColumn.Width =
            new GridLength(0);

        LibrarySplitter.Visibility =
            Visibility.Collapsed;
        DetailPane.Visibility =
            Visibility.Collapsed;
    }

    private void CloseDetailButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        LibraryList.SelectedItem = null;
        _selectedLibraryItem = null;
        HideDetailPane();
        ResetTrackDetails();
    }

    private void ResetTrackDetails()
    {
        ClearCloudMatchPanels();
        StopTitleAutoScroll();
        TrackTitleTranslateTransform.X = 0;
        TrackAlbumTranslateTransform.X = 0;

        _currentLyricsRaw = null;
        _currentArtworkBytes = null;

         TrackTitleText.Text = string.Empty;
        TrackArtistText.Text = string.Empty;
        TrackAlbumText.Text = string.Empty;
        DetailDurationText.Text = "—";
        TrackStatusText.Text = string.Empty;
        TrackStatusIcon.Glyph = "\uE823";
        TrackStatusIcon.Foreground =
            new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Color.FromArgb(255, 202, 116, 0));

        CoverImage.Source = null;
        CoverPlaceholderText.Visibility =
            Visibility.Visible;

        LyricsSourceText.Text = string.Empty;
        LyricsList.ItemsSource =
            Array.Empty<LyricDisplayLine>();

        RefreshCurrentMatchCard(
            null);

        TrackSyncStateText.Text =
            L(
                "No sync information.",
                "暂无同步信息。");
        SetTrackSyncTabVisual(
            "pending");
        TrackSyncTimeText.Text = string.Empty;
        TrackSyncLogText.Text = string.Empty;

        TrackSourcePathText.Text = string.Empty;
        TrackDestinationPathText.Text = string.Empty;
        TrackFormatText.Text = string.Empty;
        TrackDurationText.Text = string.Empty;
        TrackBitrateText.Text = string.Empty;
        TrackSampleRateText.Text = string.Empty;
        TrackBitDepthText.Text = string.Empty;
        TrackChannelsText.Text = string.Empty;
        TrackBitrateModeText.Text = string.Empty;
        TrackSizeText.Text = string.Empty;
        TrackModifiedText.Text = string.Empty;
        TrackCreatedText.Text = string.Empty;
        TrackDestinationSizeText.Text = string.Empty;
        TrackDestinationModifiedText.Text = string.Empty;
    }

    private string CurrentLongTextMode =>
        LongTextModes.Normalize(
            _settings?.Ui.LongTextMode,
            _settings?.Ui.AutoScrollLongTitles
                ?? true);

    private void ConfigureTitleAutoScroll()
    {
        StopTitleAutoScroll();

        TrackTitleTranslateTransform.X = 0;
        TrackAlbumTranslateTransform.X = 0;

        string mode =
            CurrentLongTextMode;

        foreach (LibraryDisplayItem item
            in _allLibraryItems)
        {
            item.ApplyLongTextMode(mode);
        }

        ConfigureDetailText(
            TrackTitleViewport,
            TrackTitleText,
            mode,
            maxLines: 3);
        ConfigureDetailText(
            TrackAlbumViewport,
            TrackAlbumText,
            mode,
            maxLines: 2);

        DispatcherQueue.TryEnqueue(
            () =>
            {
                TrackTitleViewport.UpdateLayout();
                TrackAlbumViewport.UpdateLayout();

                if (mode == LongTextModes.Hover)
                {
                    TrackTitleText.Width =
                        Math.Max(
                            0,
                            TrackTitleViewport
                                .ViewportWidth);
                    TrackAlbumText.Width =
                        Math.Max(
                            0,
                            TrackAlbumViewport
                                .ViewportWidth);
                    return;
                }

                if (mode
                    != LongTextModes.AutoScroll)
                {
                    return;
                }

                double titleOverflow =
                    GetTextOverflow(
                        TrackTitleViewport,
                        TrackTitleText);
                double albumOverflow =
                    GetTextOverflow(
                        TrackAlbumViewport,
                        TrackAlbumText);

                _titleScrollStoryboard =
                    StartTextMarquee(
                        TrackTitleTranslateTransform,
                        titleOverflow);
                _albumScrollStoryboard =
                    StartTextMarquee(
                        TrackAlbumTranslateTransform,
                        albumOverflow);
            });
    }

    private static void ConfigureDetailText(
        ScrollViewer viewport,
        TextBlock text,
        string mode,
        int maxLines)
    {
        string normalized =
            LongTextModes.Normalize(
                mode);

        bool wrap =
            normalized
            == LongTextModes.Wrap;
        bool hover =
            normalized
            == LongTextModes.Hover;

        viewport.HorizontalScrollBarVisibility =
            ScrollBarVisibility.Hidden;
        viewport.HorizontalScrollMode =
            ScrollMode.Disabled;
        viewport.IsHitTestVisible =
            hover;

        text.HorizontalAlignment =
            normalized
                == LongTextModes.AutoScroll
                ? HorizontalAlignment.Left
                : HorizontalAlignment.Stretch;
        text.Width = double.NaN;

        text.TextWrapping =
            wrap
                ? TextWrapping.WrapWholeWords
                : TextWrapping.NoWrap;
        text.TextTrimming =
            hover
                ? TextTrimming.CharacterEllipsis
                : TextTrimming.None;
        text.MaxLines =
            wrap
                ? maxLines
                : 1;

        ToolTipService.SetToolTip(
            text,
            hover
                ? text.Text
                : null);
    }

    private static double GetTextOverflow(
        ScrollViewer viewport,
        TextBlock text)
    {
        double available =
            viewport.ViewportWidth > 1
                ? viewport.ViewportWidth
                : viewport.ActualWidth;

        if (!double.IsFinite(available)
            || available <= 1)
        {
            return 0;
        }

        text.Measure(
            new Windows.Foundation.Size(
                double.PositiveInfinity,
                double.PositiveInfinity));

        double desired =
            text.DesiredSize.Width;

        if (!double.IsFinite(desired)
            || desired <= available + 1)
        {
            return 0;
        }

        return desired - available;
    }

    private void StopTitleAutoScroll()
    {
        _titleScrollStoryboard?.Stop();
        _albumScrollStoryboard?.Stop();
        _titleScrollStoryboard = null;
        _albumScrollStoryboard = null;
    }

    private static Storyboard? StartTextMarquee(
        TranslateTransform transform,
        double overflow)
    {
        if (overflow <= 1)
        {
            transform.X = 0;
            return null;
        }

        DoubleAnimation animation = new()
        {
            From = 0,
            To = -overflow,
            Duration =
                new Duration(
                    TimeSpan.FromSeconds(
                        Math.Clamp(
                            overflow / 48.0,
                            3.2,
                            18.0))),
            AutoReverse = true,
            RepeatBehavior =
                RepeatBehavior.Forever,
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(
            animation,
            transform);
        Storyboard.SetTargetProperty(
            animation,
            "X");

        Storyboard storyboard = new();
        storyboard.Children.Add(
            animation);
        storyboard.Begin();
        return storyboard;
    }

    private void ConfigureAuxiliaryLongText()
    {
        StopAuxiliaryLongText();

        string mode =
            CurrentLongTextMode;

        ConfigureDetailText(
            LyricsSourceViewport,
            LyricsSourceText,
            mode,
            maxLines: 2);
        ConfigureDetailText(
            PlaylistTitleViewport,
            PlaylistTitleText,
            mode,
            maxLines: 2);
        ConfigureDetailText(
            PlaylistMetaViewport,
            PlaylistMetaText,
            mode,
            maxLines: 2);
        ConfigureDetailText(
            TrackArtistViewport,
            TrackArtistText,
            mode,
            maxLines: 2);
        ConfigureDetailText(
            PlayerTitleViewport,
            PlayerTitleText,
            mode,
            maxLines: 2);
        ConfigureDetailText(
            PlayerMetadataViewport,
            PlayerMetadataText,
            mode,
            maxLines: 2);
        ConfigureDetailText(
            DiagnosticsLogPathViewport,
            DiagnosticsLogPathText,
            mode,
            maxLines: 2);

        LyricsSourceTranslateTransform.X = 0;
        PlaylistTitleTranslateTransform.X = 0;
        PlaylistMetaTranslateTransform.X = 0;
        TrackArtistTranslateTransform.X = 0;
        PlayerTitleTranslateTransform.X = 0;
        PlayerMetadataTranslateTransform.X = 0;
        DiagnosticsLogPathTranslateTransform.X = 0;

        DispatcherQueue.TryEnqueue(
            () =>
            {
                _lyricsSourceScrollStoryboard =
                    ConfigureAuxiliaryViewport(
                        LyricsSourceViewport,
                        LyricsSourceText,
                        LyricsSourceTranslateTransform,
                        mode);
                _playlistTitleScrollStoryboard =
                    ConfigureAuxiliaryViewport(
                        PlaylistTitleViewport,
                        PlaylistTitleText,
                        PlaylistTitleTranslateTransform,
                        mode);
                _playlistMetaScrollStoryboard =
                    ConfigureAuxiliaryViewport(
                        PlaylistMetaViewport,
                        PlaylistMetaText,
                        PlaylistMetaTranslateTransform,
                        mode);
                _trackArtistScrollStoryboard =
                    ConfigureAuxiliaryViewport(
                        TrackArtistViewport,
                        TrackArtistText,
                        TrackArtistTranslateTransform,
                        mode);
                if (_mediaPlayer.Source is null)
                {
                    PlayerTitleTranslateTransform.X = 0;
                    PlayerMetadataTranslateTransform.X = 0;
                    _playerTitleScrollStoryboard = null;
                    _playerMetadataScrollStoryboard = null;
                }
                else
                {
                    _playerTitleScrollStoryboard =
                        ConfigureAuxiliaryViewport(
                            PlayerTitleViewport,
                            PlayerTitleText,
                            PlayerTitleTranslateTransform,
                            mode);
                    _playerMetadataScrollStoryboard =
                        ConfigureAuxiliaryViewport(
                            PlayerMetadataViewport,
                            PlayerMetadataText,
                            PlayerMetadataTranslateTransform,
                            mode);
                }
                _diagnosticsPathScrollStoryboard =
                    ConfigureAuxiliaryViewport(
                        DiagnosticsLogPathViewport,
                        DiagnosticsLogPathText,
                        DiagnosticsLogPathTranslateTransform,
                        mode);
            });
    }

    private static Storyboard?
        ConfigureAuxiliaryViewport(
            ScrollViewer viewport,
            TextBlock text,
            TranslateTransform transform,
            string mode)
    {
        viewport.UpdateLayout();

        if (mode == LongTextModes.Hover)
        {
            text.Width =
                Math.Max(
                    0,
                    viewport.ViewportWidth);
            return null;
        }

        if (mode != LongTextModes.AutoScroll)
        {
            return null;
        }

        return StartTextMarquee(
            transform,
            GetTextOverflow(
                viewport,
                text));
    }

    private void StopAuxiliaryLongText()
    {
        _lyricsSourceScrollStoryboard?.Stop();
        _playlistTitleScrollStoryboard?.Stop();
        _playlistMetaScrollStoryboard?.Stop();
        _trackArtistScrollStoryboard?.Stop();
        _playerTitleScrollStoryboard?.Stop();
        _playerMetadataScrollStoryboard?.Stop();
        _diagnosticsPathScrollStoryboard?.Stop();

        _lyricsSourceScrollStoryboard = null;
        _playlistTitleScrollStoryboard = null;
        _playlistMetaScrollStoryboard = null;
        _trackArtistScrollStoryboard = null;
        _playerTitleScrollStoryboard = null;
        _playerMetadataScrollStoryboard = null;
        _diagnosticsPathScrollStoryboard = null;
    }

    private async Task ShowTrackDetailsAsync(
        LibraryDisplayItem item)
    {
        int requestId =
            ++_detailRequestId;

         TrackTitleText.Text =
            item.Title;
        TrackArtistText.Text =
            item.Artist;
        TrackAlbumText.Text =
            item.Album;
        ConfigureTitleAutoScroll();
        TrackStatusText.Text =
            item.StatusText;
        TrackStatusIcon.Glyph =
            item.StatusGlyph;
        TrackStatusIcon.Foreground =
            item.StatusBrush;
        RefreshTrackSyncTab(item);

        RefreshCurrentMatchCard(
            item);
        ConfigureAuxiliaryLongText();

        CoverImage.Source =
            item.Artwork;
        CoverPlaceholderText.Visibility =
            item.Artwork is null
                ? Visibility.Visible
                : Visibility.Collapsed;

        LyricsSourceText.Text =
            string.Empty;
        LyricsList.ItemsSource =
            Array.Empty<LyricDisplayLine>();

        _currentLyricsRaw = null;

        // Load the canonical document independently of audio inspection.
        // Missing/locked audio must not hide lyrics already in SQLite.
            // The Lyrics tab never reads embedded tags or a sidecar.
            // Only library_lyrics is the canonical, scan-resolved source.
            LibraryLyricsRecord? canonical = null;
            try
            {
                canonical = await new SqliteLyricsCache(
                    DatabasePath).GetLibraryAsync(item.Entry.SourcePath);

                if (requestId != _detailRequestId)
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                AppDiagnostics.LogException(
                    "Could not load canonical library lyrics.",
                    exception);
            }

            _currentLyricsRaw = canonical?.Text;
            // The lyric content is from SQLite; hide the source badge.
            LyricsSourceText.Text = string.Empty;
        ConfigureAuxiliaryLongText();
        RenderLyrics();

        try
        {
            MusicLoomSettings settings =
                _settings
                ?? throw new InvalidOperationException(
                    "Settings are not loaded.");

            TrackDetailPayload payload =
                await Task.Run(
                    () =>
                        LoadTrackDetail(
                            settings,
                            item.Entry));

            if (requestId !=
                _detailRequestId)
            {
                return;
            }

            if (payload.Artwork
                is { Length: > 0 })
            {
                BitmapImage bitmap =
                    await CreateBitmapImageAsync(
                        payload.Artwork,
                        decodePixelWidth: 0);
                item.Artwork = bitmap;
                CoverImage.Source = bitmap;
                CoverPlaceholderText.Visibility =
                    Visibility.Collapsed;
                RefreshCurrentMatchCard(
                    item);
            }

            _currentArtworkBytes =
                payload.Artwork;
            ConfigureAuxiliaryLongText();

            TrackSourcePathText.Text =
                payload.SourcePath;
            TrackDestinationPathText.Text =
                payload.DestinationPath
                ?? L(
                    "Not created yet",
                    "尚未创建");
            TrackFormatText.Text =
                payload.Format;
            TrackDurationText.Text =
                payload.Duration;
            DetailDurationText.Text = payload.Duration;
            TrackBitrateText.Text =
                payload.Bitrate;
            TrackSampleRateText.Text =
                payload.SampleRate;
            TrackBitDepthText.Text =
                payload.BitDepth;
            TrackChannelsText.Text =
                payload.Channels;
            TrackBitrateModeText.Text =
                payload.BitrateMode;
            TrackSizeText.Text =
                payload.SourceSize;
            TrackModifiedText.Text =
                payload.SourceModified;
            TrackCreatedText.Text =
                payload.SourceCreated;
            TrackDestinationSizeText.Text =
                payload.DestinationSize;
            TrackDestinationModifiedText.Text =
                payload.DestinationModified;
        }
        catch (Exception exception)
        {
            if (requestId !=
                _detailRequestId)
            {
                return;
            }

            AppDiagnostics.LogException(
                "Could not inspect track details.",
                exception);
        }
    }

    private TrackDetailPayload LoadTrackDetail(
        MusicLoomSettings settings,
        PlanEntry entry)
    {
        string inspectPath =
            entry.DestinationPath is
                string destination
            && File.Exists(destination)
                ? destination
                : entry.SourcePath;

        TrackInspection inspection =
            AtlTrackInspector.Read(
                inspectPath);

        byte[]? artwork =
            inspection.Artwork
            ?? TryReadExternalArtwork(
                settings,
                entry);

        // The scan/indexing pipeline imports the lyrics into SQLite.
        // Details only inspect audio metadata and artwork.
        string? lyrics = null;
        string lyricsSource = string.Empty;

        FileInfo source =
            new(entry.SourcePath);
        FileInfo? destinationInfo =
            entry.DestinationPath is
                string destinationPath
            && File.Exists(destinationPath)
                ? new FileInfo(destinationPath)
                : null;

        return new TrackDetailPayload(
            artwork,
            lyrics,
            lyricsSource,
            entry.SourcePath,
            entry.DestinationPath,
            Path.GetExtension(
                entry.SourcePath)
                .TrimStart('.')
                .ToUpperInvariant(),
            FormatDuration(
                inspection.DurationMs),
            inspection.Bitrate > 0
                ? $"{inspection.Bitrate:0.#} kbps"
                : "—",
            inspection.SampleRate > 0
                ? $"{inspection.SampleRate:0} Hz"
                : "—",
            inspection.BitDepth > 0
                ? $"{inspection.BitDepth} bit"
                : "—",
            inspection.Channels > 0
                ? string.IsNullOrWhiteSpace(
                    inspection.ChannelArrangement)
                    ? inspection.Channels
                        .ToString(
                            CultureInfo.InvariantCulture)
                    : $"{inspection.Channels} · {inspection.ChannelArrangement}"
                : "—",
            inspection.IsLossless
                ? L(
                    "Lossless",
                    "无损")
                : inspection.IsVbr
                    ? "VBR"
                    : "CBR",
            FormatBytes(source.Length),
            source.LastWriteTime.ToString("G"),
            source.CreationTime.ToString("G"),
            destinationInfo is null
                ? "—"
                : FormatBytes(
                    destinationInfo.Length),
            destinationInfo is null
                ? "—"
                : destinationInfo
                    .LastWriteTime
                    .ToString("G"));
    }

    private void RenderLyrics()
    {
        if (_activePlaybackLyric
            is not null)
        {
            _activePlaybackLyric.IsActive =
                false;
            _activePlaybackLyric = null;
        }

        LyricDisplayLine[] detected =
            BuildLyricLines(
                _currentLyricsRaw,
                LyricsDisplayModes
                    .PreferOriginal);

        bool hasLyrics =
            detected.Length > 0;
        bool hasTranslation =
            detected.Any(
                static line =>
                    !string.IsNullOrWhiteSpace(
                        line.Translation));

        _lyricsDisplayMode =
            LyricsDisplayModes.Normalize(
                _lyricsDisplayMode);

        int selectedIndex =
            _lyricsDisplayMode switch
            {
                LyricsDisplayModes
                    .OriginalOnly => 0,
                LyricsDisplayModes
                    .PreferTranslation => 1,
                _ => 2,
            };

        if (LyricsModeBox.SelectedIndex
            != selectedIndex)
        {
            LyricsModeBox.SelectedIndex =
                selectedIndex;
        }

        LyricsModeBox.Visibility =
            hasLyrics && hasTranslation
                ? Visibility.Visible
                : Visibility.Collapsed;
        LyricsModeBox.IsEnabled =
            hasLyrics && hasTranslation;
        CopyAllLyricsButton.IsEnabled = hasLyrics;
        ClearLyricsButton.IsEnabled = hasLyrics && !_lyricsEditInProgress;
        AdjustLyricsButton.IsEnabled = hasLyrics && !_lyricsEditInProgress;
        EditLyricsButton.IsEnabled = hasLyrics && !_lyricsEditInProgress;

        LyricDisplayLine[] lines =
            hasLyrics
                ? BuildLyricLines(
                    _currentLyricsRaw,
                    _lyricsDisplayMode)
                : [];

        _lastAutoScrolledLyric = null;
        LyricsList.ItemsSource =
            lines.Length == 0
                ? new[]
                {
                    new LyricDisplayLine(
                        L(
                            "No lyrics found.",
                            "没有找到歌词。"),
                        null,
                        LyricsDisplayModes
                            .OriginalOnly),
                }
                : lines;
    }

    private void LyricsModeBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingSettings
            || _refreshingLocalizedChoices
            || LyricsModeBox.SelectedItem
                is not ComboBoxItem item
            || item.Tag is not string mode)
        {
            return;
        }

        _lyricsDisplayMode =
            LyricsDisplayModes.Normalize(
                mode);

        RenderLyrics();
        _ = PersistLyricsDisplayModeAsync();
    }

    private async Task PersistLyricsDisplayModeAsync()
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            MusicLoomSettings updated =
                _settings with
                {
                    Ui =
                        _settings.Ui with
                        {
                            LyricsDisplayMode =
                                _lyricsDisplayMode,
                        },
                };

            _settings = updated;

            await TomlSettingsStore.SaveAsync(
                ConfigPath,
                updated);
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not save the lyrics display mode.",
                exception);
        }
    }

    private async void CopyAllLyricsTextItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        LyricDisplayLine[] lines =
            BuildLyricLines(
                _currentLyricsRaw,
                _lyricsDisplayMode);

        if (lines.Length == 0)
        {
            return;
        }

        await CopyTextAsync(
            BuildPlainLyricText(
                lines));
    }

    private async void CopyAllLyricsLrcItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        LyricDisplayLine[] lines =
            BuildLyricLines(
                _currentLyricsRaw,
                _lyricsDisplayMode);

        if (lines.Length == 0)
        {
            return;
        }

        await CopyTextAsync(
            BuildLrcLyricText(
                lines));
    }


    private void LyricLineMenu_Opening(
        object sender,
        object e)
    {
        if (sender is not MenuFlyout menu
            || menu.Items.Count == 0
            || menu.Items[0]
                is not MenuFlyoutSubItem copy)
        {
            return;
        }

        copy.Text =
            L(
                "Copy line",
                "复制此行");

        if (copy.Items.Count > 0
            && copy.Items[0]
                is MenuFlyoutItem textItem)
        {
            textItem.Text =
                L(
                    "Copy as text",
                    "复制为文本");
        }

        if (copy.Items.Count > 1
            && copy.Items[1]
                is MenuFlyoutItem lrcItem)
        {
            lrcItem.Text =
                L(
                    "Copy as LRC",
                    "复制为 LRC");
        }

        if (menu.Items.Count > 2
            && menu.Items[2]
                is MenuFlyoutItem seekItem)
        {
            seekItem.Text =
                L(
                    "Play from here",
                    "跳转到当前位置播放");
        }
    }



    private async void LyricsList_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        LyricDisplayLine? line =
            (e.OriginalSource as FrameworkElement)
                ?.DataContext as LyricDisplayLine
            ?? LyricsList.SelectedItem as LyricDisplayLine;

        if (line?.Timestamp is null)
        {
            return;
        }

        e.Handled = true;
        await SeekLyricLineAsync(line);
    }

    private async void SeekLyricLineItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        LyricDisplayLine? line =
            (sender as MenuFlyoutItem)
                ?.Tag as LyricDisplayLine
            ?? LyricsList.SelectedItem as LyricDisplayLine;

        if (line?.Timestamp is not null)
        {
            await SeekLyricLineAsync(line);
        }
    }

    private async Task SeekLyricLineAsync(
        LyricDisplayLine line)
    {
        if (line.Timestamp
            is not TimeSpan timestamp)
        {
            return;
        }

        bool positionAlreadyApplied =
            false;

        if (_matchDialogLyricLines.Contains(
                line)
            && _activeMatchDialogCandidate
                is NeteaseMatchCandidate
                    candidate)
        {
            if (_playingPreviewCandidate
                    ?.TrackId
                != candidate.TrackId)
            {
                await PlayNetEasePreviewAsync(
                    candidate,
                    timestamp);
                positionAlreadyApplied =
                    true;
            }
        }
        else if (_selectedLibraryItem
            is LibraryDisplayItem selected)
        {
            if (!ReferenceEquals(
                    selected,
                    _playingLibraryItem))
            {
                await PlayLibraryItemAsync(
                    selected,
                    startPosition:
                        timestamp);
                positionAlreadyApplied =
                    true;
            }
        }

        if (!positionAlreadyApplied)
        {
            _suppressPlaybackLyricFollow =
                true;
            try
            {
                await SeekPlayerWhenReadyAsync(
                    timestamp);
            }
            finally
            {
                _suppressPlaybackLyricFollow =
                    false;
            }

            _mediaPlayer.Play();
        }

        if (_activePlaybackLyric
            is not null)
        {
            _activePlaybackLyric.IsActive =
                false;
        }

        _activePlaybackLyric = line;
        line.PlaybackPosition = _mediaPlayer.PlaybackSession.Position;
        line.IsActive = true;
        UpdatePlaybackLyrics();
    }

    private async Task SeekPlayerWhenReadyAsync(
        TimeSpan position)
    {
        if (_mediaPlayer.Source is null)
        {
            return;
        }

        for (int attempt = 0;
            attempt < 100;
            attempt++)
        {
            TimeSpan duration =
                _mediaPlayer
                    .PlaybackSession
                    .NaturalDuration;

            if (duration > TimeSpan.Zero)
            {
                _mediaPlayer
                    .PlaybackSession
                    .Position =
                        position <= duration
                            ? position
                            : duration;
                return;
            }

            await Task.Delay(50);
        }
    }

    private void LyricsList_RightTapped(
        object sender,
        RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource
                is FrameworkElement element
            && element.DataContext
                is LyricDisplayLine line)
        {
            LyricsList.SelectedItem =
                line;
        }
    }

    private async void CopyLyricLineTextItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        LyricDisplayLine? line =
            (sender as MenuFlyoutItem)
                ?.Tag
                as LyricDisplayLine
            ?? LyricsList.SelectedItem
                as LyricDisplayLine;

        if (line is null)
        {
            return;
        }

        await CopyTextAsync(
            BuildPlainLyricText(
                [line]));
    }

    private async void CopyLyricLineLrcItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        LyricDisplayLine? line =
            (sender as MenuFlyoutItem)
                ?.Tag
                as LyricDisplayLine
            ?? LyricsList.SelectedItem
                as LyricDisplayLine;

        if (line is null)
        {
            return;
        }

        await CopyTextAsync(
            BuildLrcLyricText(
                [line]));
    }

    private static string BuildPlainLyricText(
        IEnumerable<LyricDisplayLine> lines) =>
        string.Join(
            Environment.NewLine,
            lines.Select(
                static line =>
                    string.IsNullOrWhiteSpace(
                        line.SecondaryText)
                        ? line.PrimaryText
                        : line.PrimaryText
                            + Environment.NewLine
                            + line.SecondaryText));

    private static string BuildLrcLyricText(
        IEnumerable<LyricDisplayLine> lines)
    {
        List<string> output = [];

        foreach (LyricDisplayLine line
            in lines)
        {
            string tag =
                line.TimestampLrcTag;

            AppendLrcText(
                output,
                tag,
                line.PrimaryText,
                line.Timestamp,
                line.PrimaryWords);

            if (!string.IsNullOrWhiteSpace(
                    line.SecondaryText))
            {
                AppendLrcText(
                    output,
                    tag,
                    line.SecondaryText!,
                    line.Timestamp,
                    line.SecondaryWords);
            }
        }

        return string.Join(
            Environment.NewLine,
            output);
    }

    private static void AppendLrcText(
        List<string> output,
        string timestampTag,
        string text,
        TimeSpan? timestamp,
        IReadOnlyList<LrcWord>? words)
    {
        if (timestamp is TimeSpan start
            && words is { Count: > 0 }
            && string.Equals(
                string.Concat(words.Select(static word => word.Text)),
                text, StringComparison.Ordinal))
        {
            // Copy-as-LRC must not throw away the word timings that the
            // canonical database retained. This also handles "prefer
            // translation" where the timed original appears second.
            output.Add(new LrcLine(start, text, words).ToString());
            return;
        }

        foreach (string rawLine
            in text.Split(
                ['\r', '\n'],
                StringSplitOptions
                    .RemoveEmptyEntries))
        {
            string value =
                rawLine.Trim();

            if (value.Length == 0)
            {
                continue;
            }

            output.Add(
                timestampTag
                + value);
        }
    }


    private static LyricDisplayLine[] BuildLyricLines(
        string? raw,
        string displayMode)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        string mode = LyricsDisplayModes.Normalize(displayMode);
        LrcDocument document = LrcDocument.Parse(raw);

        IReadOnlyList<LrcPresentationLine> projected =
            document.Lines.Count > 0
                ? LrcPresentation.Project(document)
                : raw.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries)
                    .Select(static line => line.Trim())
                    .Where(static text =>
                        text.Length > 0
                        && !(text.StartsWith('[')
                            && text.Contains(':')
                            && text.EndsWith(']')))
                    .Select(text =>
                    {
                        (string original, string? translation) =
                            LrcPresentation.SplitInline(text);
                        return new LrcPresentationLine(
                            null,
                            original,
                            translation);
                    })
                    .ToArray();

        return projected.Select(line =>
            new LyricDisplayLine(
                line.Original,
                line.Translation,
                mode,
                line.Timestamp,
                line.OriginalWords,
                line.TranslationWords)).ToArray();
    }

    private async Task
        LoadArtworkThumbnailsAsync(
            MusicLoomSettings settings,
            int generation)
    {
        // A rescan can replace the live list while an awaited decode is in
        // progress. Enumerate a snapshot to avoid invalidating the iterator.
        LibraryDisplayItem[] snapshot = _allLibraryItems.ToArray();

        // Embedded artwork belongs to the track, not to the album folder.
        // Load these individually so one song's embedded cover never leaks to
        // neighboring tracks that happen to share Artist/Album.
        foreach (LibraryDisplayItem item
            in snapshot.Where(
                static item =>
                    item.Entry.Metadata
                        ?.HasEmbeddedArt
                    == true))
        {
            if (generation !=
                _artworkGeneration)
            {
                return;
            }

            byte[]? embedded =
                await Task.Run(
                    () =>
                        TryReadEmbeddedArtwork(
                            item.Entry));

            if (embedded is not
                { Length: > 0 }
                || generation !=
                    _artworkGeneration)
            {
                continue;
            }

            try
            {
                item.Artwork =
                    await CreateBitmapImageAsync(
                        embedded);
            }
            catch
            {
                // Corrupt embedded artwork is optional; continue with the
                // remaining library rather than blocking browsing.
            }
        }

        // Tracks without embedded art may safely share the same album-folder
        // cover. Group those for efficient I/O.
        IGrouping<string, LibraryDisplayItem>[]
            groups =
                snapshot
                    .Where(
                        static item =>
                            item.Entry.Metadata
                                ?.HasEmbeddedArt
                            != true)
                    .GroupBy(
                        ArtworkKey,
                        StringComparer
                            .OrdinalIgnoreCase)
                    .ToArray();

        foreach (IGrouping<string,
            LibraryDisplayItem> group
            in groups)
        {
            if (generation !=
                _artworkGeneration)
            {
                return;
            }

            LibraryDisplayItem[] groupItems =
                group.ToArray();

            byte[]? bytes =
                await Task.Run(
                    () =>
                    {
                        foreach (LibraryDisplayItem candidate
                            in groupItems)
                        {
                            byte[]? candidateBytes =
                                TryReadExternalArtwork(
                                    settings,
                                    candidate.Entry)
                                ?? TryReadEmbeddedArtwork(
                                    candidate.Entry);

                            if (candidateBytes is
                                { Length: > 0 })
                            {
                                return candidateBytes;
                            }
                        }

                        return null;
                    });

            if (bytes is null
                || bytes.Length == 0
                || generation !=
                    _artworkGeneration)
            {
                continue;
            }

            try
            {
                BitmapImage bitmap =
                    await CreateBitmapImageAsync(
                        bytes);

                foreach (LibraryDisplayItem item
                    in groupItems)
                {
                    item.Artwork = bitmap;
                }
            }
            catch
            {
                // Artwork is optional. Corrupt pictures must not block browsing.
            }
        }
    }

    private static string ArtworkKey(
        LibraryDisplayItem item)
    {
        if (item.Entry.DestinationPath
            is string destination)
        {
            return Path.GetDirectoryName(
                       destination)
                   ?? item.Entry.SourcePath;
        }

        return string.Join(
            "|",
            Path.GetDirectoryName(
                item.Entry.SourcePath),
            item.Artist,
            item.Album);
    }

    private static byte[]?
        TryReadArtworkBytes(
            MusicLoomSettings settings,
            PlanEntry entry)
    {
        try
        {
            // Embedded artwork is track-specific and therefore wins over a
            // generic Cover.* file in the album folder.
            if (entry.Metadata?.HasEmbeddedArt
                == true)
            {
                byte[]? embedded =
                    TryReadEmbeddedArtwork(
                        entry);

                if (embedded is
                    { Length: > 0 })
                {
                    return embedded;
                }
            }

            byte[]? external =
                TryReadExternalArtwork(
                    settings,
                    entry);

            if (external is
                { Length: > 0 })
            {
                return external;
            }

            // Metadata can be stale/missing for legacy cache entries. Try the
            // actual file as a final embedded-art fallback.
            return TryReadEmbeddedArtwork(
                entry);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]?
        TryReadEmbeddedArtwork(
            PlanEntry entry)
    {
        foreach (string path
            in CandidateAudioPaths(entry))
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                byte[]? artwork =
                    AtlTrackInspector
                        .Read(path)
                        .Artwork;

                if (artwork is
                    { Length: > 0 })
                {
                    return artwork;
                }
            }
            catch
            {
                // Keep trying another candidate path.
            }
        }

        return null;
    }

    private static IEnumerable<string>
        CandidateAudioPaths(
            PlanEntry entry)
    {
        // Source first: destination may have been materialized before an
        // embedded-art tag was corrected.
        yield return entry.SourcePath;

        if (entry.DestinationPath
            is string destination
            && !string.Equals(
                destination,
                entry.SourcePath,
                StringComparison.OrdinalIgnoreCase))
        {
            yield return destination;
        }
    }

    private static byte[]?
        TryReadExternalArtwork(
            MusicLoomSettings settings,
            PlanEntry entry)
    {
        if (entry.Metadata is not null)
        {
            string? sourceDirectory =
                Path.GetDirectoryName(
                    entry.SourcePath);

            if (!string.IsNullOrWhiteSpace(
                    sourceDirectory)
                && Directory.Exists(
                    sourceDirectory))
            {
                string? sourceCover =
                    AlbumArtLocator.Find(
                        sourceDirectory,
                        entry.Metadata,
                        onlyExplicit: false);

                if (sourceCover is not null)
                {
                    return File.ReadAllBytes(
                        sourceCover);
                }

                // A destination Cover.* can be stale (notably from a
                // previous WMP Folder.jpg cache). Current source state wins.
                return null;
            }
        }

        if (entry.DestinationPath
            is not string destination)
        {
            return null;
        }

        string? directory =
            Path.GetDirectoryName(
                destination);

        if (string.IsNullOrWhiteSpace(
                directory)
            || !Directory.Exists(
                directory))
        {
            return null;
        }

        string? existing =
            Directory
                .EnumerateFiles(
                    directory,
                    "Cover.*",
                    SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

        return existing is null
            ? null
            : File.ReadAllBytes(
                existing);
    }

    private static async Task<BitmapImage>
        CreateBitmapImageAsync(
            byte[] artwork,
            int decodePixelWidth = 192)
    {
        using InMemoryRandomAccessStream
            stream = new();
        using DataWriter writer =
            new(stream);

        writer.WriteBytes(artwork);
        await writer.StoreAsync();
        writer.DetachStream();
        stream.Seek(0);

        BitmapImage bitmap = new();
        if (decodePixelWidth > 0)
        {
            bitmap.DecodePixelWidth =
                decodePixelWidth;
        }

        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    private void CoverButton_PointerEntered(
        object sender,
        PointerRoutedEventArgs e) =>
        AnimateCoverScale(1.04);

    private void CoverButton_PointerExited(
        object sender,
        PointerRoutedEventArgs e) =>
        AnimateCoverScale(1.0);

    private void AnimateCoverScale(
        double target)
    {
        CubicEase ease = new()
        {
            EasingMode =
                EasingMode.EaseOut,
        };

        DoubleAnimation x = new()
        {
            To = target,
            Duration =
                new Duration(
                    TimeSpan.FromMilliseconds(
                        150)),
            EasingFunction = ease,
            EnableDependentAnimation = true,
        };
        DoubleAnimation y = new()
        {
            To = target,
            Duration =
                new Duration(
                    TimeSpan.FromMilliseconds(
                        150)),
            EasingFunction = ease,
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(
            x,
            CoverScaleTransform);
        Storyboard.SetTargetProperty(
            x,
            "ScaleX");
        Storyboard.SetTarget(
            y,
            CoverScaleTransform);
        Storyboard.SetTargetProperty(
            y,
            "ScaleY");

        Storyboard storyboard = new();
        storyboard.Children.Add(x);
        storyboard.Children.Add(y);
        storyboard.Begin();
    }

    private async void CoverButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_currentArtworkBytes
            is not { Length: > 0 } artwork)
        {
            return;
        }

        try
        {
            BitmapImage bitmap =
                await CreateBitmapImageAsync(
                    artwork,
                    decodePixelWidth: 0);

            const double ViewportSize = 520;

            CompositeTransform imageTransform =
                new()
                {
                    ScaleX = 1,
                    ScaleY = 1,
                };

            Image image = new()
            {
                Source = bitmap,
                Width = ViewportSize,
                Height = ViewportSize,
                Stretch =
                    Microsoft.UI.Xaml.Media
                        .Stretch.Uniform,
                HorizontalAlignment =
                    HorizontalAlignment.Left,
                VerticalAlignment =
                    VerticalAlignment.Top,
                RenderTransform =
                    imageTransform,
                RenderTransformOrigin =
                    new Windows.Foundation.Point(
                        0,
                        0),
            };

            Grid viewport = new()
            {
                Width = ViewportSize,
                Height = ViewportSize,
                HorizontalAlignment =
                    HorizontalAlignment.Center,
                VerticalAlignment =
                    VerticalAlignment.Center,
                Background =
                    Application.Current.Resources[
                        "CardBackgroundFillColorSecondaryBrush"]
                    as Microsoft.UI.Xaml.Media.Brush,
                Clip =
                    new RectangleGeometry
                    {
                        Rect =
                            new Windows.Foundation.Rect(
                                0,
                                0,
                                ViewportSize,
                                ViewportSize),
                    },
            };
            viewport.Children.Add(
                image);

            double scale = 1;
            double translateX = 0;
            double translateY = 0;
            bool dragging = false;
            Windows.Foundation.Point
                lastPointer = default;

            PointerEventHandler wheelHandler =
                (_, args) =>
                {
                    var point =
                        args.GetCurrentPoint(
                            viewport);
                    int delta =
                        point.Properties
                            .MouseWheelDelta;

                    if (delta == 0)
                    {
                        return;
                    }

                    double oldScale =
                        scale;
                    double nextScale =
                        Math.Clamp(
                            scale
                            * (delta > 0
                                ? 1.14
                                : 1.0 / 1.14),
                            0.25,
                            6.0);

                    if (Math.Abs(
                            nextScale - oldScale)
                        < 0.0001)
                    {
                        args.Handled = true;
                        return;
                    }

                    Windows.Foundation.Point p =
                        point.Position;
                    double factor =
                        nextScale
                        / oldScale;

                    translateX =
                        p.X
                        - (p.X - translateX)
                        * factor;
                    translateY =
                        p.Y
                        - (p.Y - translateY)
                        * factor;
                    scale =
                        nextScale;

                    imageTransform.ScaleX =
                        scale;
                    imageTransform.ScaleY =
                        scale;
                    imageTransform.TranslateX =
                        translateX;
                    imageTransform.TranslateY =
                        translateY;
                    args.Handled = true;
                };

            PointerEventHandler pressedHandler =
                (_, args) =>
                {
                    var point =
                        args.GetCurrentPoint(
                            viewport);

                    if (!point.Properties
                        .IsLeftButtonPressed)
                    {
                        return;
                    }

                    dragging = true;
                    lastPointer =
                        point.Position;
                    viewport.CapturePointer(
                        args.Pointer);
                    args.Handled = true;
                };

            PointerEventHandler movedHandler =
                (_, args) =>
                {
                    if (!dragging)
                    {
                        return;
                    }

                    Windows.Foundation.Point
                        position =
                            args.GetCurrentPoint(
                                viewport)
                                .Position;

                    translateX +=
                        position.X
                        - lastPointer.X;
                    translateY +=
                        position.Y
                        - lastPointer.Y;
                    lastPointer =
                        position;

                    imageTransform.TranslateX =
                        translateX;
                    imageTransform.TranslateY =
                        translateY;
                    args.Handled = true;
                };

            void EndDrag(
                PointerRoutedEventArgs args)
            {
                if (!dragging)
                {
                    return;
                }

                dragging = false;
                viewport.ReleasePointerCapture(
                    args.Pointer);
                args.Handled = true;
            }

            viewport.AddHandler(
                UIElement.PointerWheelChangedEvent,
                wheelHandler,
                handledEventsToo: true);
            viewport.AddHandler(
                UIElement.PointerPressedEvent,
                pressedHandler,
                handledEventsToo: true);
            viewport.AddHandler(
                UIElement.PointerMovedEvent,
                movedHandler,
                handledEventsToo: true);
            viewport.PointerReleased +=
                (_, args) =>
                    EndDrag(args);
            viewport.PointerCanceled +=
                (_, args) =>
                    EndDrag(args);

            string dialogTitle =
                string.IsNullOrWhiteSpace(
                    TrackAlbumText.Text)
                    ? TrackTitleText.Text
                    : TrackAlbumText.Text;

            CloseMatchOverlay();
            ShowAppOverlay(
                dialogTitle,
                viewport,
                maxWidth: 580,
                maxHeight: 620);
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not show artwork preview.",
                exception);
        }
    }

    private void MatchDialogCloseButton_Click(
        object sender,
        RoutedEventArgs e) =>
        CloseMatchOverlay();

    private void ShowAppOverlay(
        string title,
        UIElement content,
        double maxWidth,
        double maxHeight,
        Action? onClosed = null,
        bool compact = false)
    {
        MatchDialogSurface.VerticalAlignment =
            compact
                ? VerticalAlignment.Center
                : VerticalAlignment.Stretch;
        MatchDialogTitleText.Text =
            title;
        MatchDialogSurface.MaxWidth =
            maxWidth;
        MatchDialogSurface.MaxHeight =
            maxHeight;
        MatchDialogHost.Content =
            content;
        _overlayCloseAction =
            onClosed;
        MatchOverlay.Visibility =
            Visibility.Visible;
    }

    private void CloseMatchOverlay()
    {
        Action? closeAction =
            _overlayCloseAction;
        _overlayCloseAction =
            null;

        if (MatchOverlay is not null)
        {
            MatchOverlay.Visibility =
                Visibility.Collapsed;
        }

        if (MatchDialogHost is not null)
        {
            MatchDialogHost.Content =
                null;
        }

        _matchDialogCompletion
            ?.TrySetResult(
                null);
        _matchDialogCompletion =
            null;

        _neteasePreviewIcons.Clear();
        _matchDialogLyricLines.Clear();
        _matchLyricsPreviewList = null;
        _activeMatchDialogCandidate =
            null;

        closeAction?.Invoke();
    }

    private static void ApplyDangerButtonStyle(
        Button button)
    {
        SolidColorBrush background =
            new(
                Color.FromArgb(
                    255,
                    196,
                    43,
                    28));
        SolidColorBrush hover =
            new(
                Color.FromArgb(
                    255,
                    164,
                    38,
                    30));
        SolidColorBrush pressed =
            new(
                Color.FromArgb(
                    255,
                    140,
                    29,
                    24));
        SolidColorBrush foreground =
            new(
                Color.FromArgb(
                    255,
                    255,
                    255,
                    255));
        SolidColorBrush transparent =
            new(
                Color.FromArgb(
                    0,
                    0,
                    0,
                    0));

        button.Background =
            background;
        button.Foreground =
            foreground;
        button.BorderThickness =
            new Thickness(0);
        button.Resources[
            "ButtonBackground"] =
            background;
        button.Resources[
            "ButtonBackgroundPointerOver"] =
            hover;
        button.Resources[
            "ButtonBackgroundPressed"] =
            pressed;
        button.Resources[
            "ButtonForeground"] =
            foreground;
        button.Resources[
            "ButtonForegroundPointerOver"] =
            foreground;
        button.Resources[
            "ButtonForegroundPressed"] =
            foreground;
        button.Resources[
            "ButtonBorderBrush"] =
            transparent;
        button.Resources[
            "ButtonBorderBrushPointerOver"] =
            transparent;
        button.Resources[
            "ButtonBorderBrushPressed"] =
            transparent;
    }

    private async Task ShowMatchDialogAsync(
        LibraryDisplayItem item)
    {
        double? localDurationMs = null;
        try
        {
            double duration =
                await Task.Run(
                    () =>
                        AtlTrackInspector
                            .ReadDurationMs(
                                item.Entry.SourcePath));

            if (double.IsFinite(duration)
                && duration > 0)
            {
                localDurationMs = duration;
            }
        }
        catch
        {
        }

        // The exact same form control is hosted by every provider;
        // only its actions and recognition adapter vary.
        LyricsSearchFormView form = new();
        form.Localize(L);
        TextBox artistBox = form.ArtistField;
        artistBox.Text = item.Artist.Contains(
                L("Unknown artist", "未知艺术家"),
                StringComparison.OrdinalIgnoreCase)
            ? string.Empty : item.Artist;
        TextBox titleBox = form.TitleField;
        titleBox.Text = item.Title;
        TextBox albumBox = form.AlbumField;
        albumBox.Text = item.Album.Contains(
                L("Unknown album", "未知专辑"),
                StringComparison.OrdinalIgnoreCase)
            ? string.Empty : item.Album;
        Button fingerprintButton = form.RecognitionAction;
        fingerprintButton.Visibility = Visibility.Visible;
        fingerprintButton.Content =
            L("Recognize from audio", "通过音频指纹识别");
        Button searchButton = form.SearchAction;
        TextBlock status = form.StatusLabel;

        ListView results = new()
        {
            SelectionMode =
                ListViewSelectionMode.Single,
            HorizontalContentAlignment =
                HorizontalAlignment.Stretch,
            Padding =
                new Thickness(
                    12,
                    0,
                    22,
                    0),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(
            results,
            ScrollBarVisibility.Auto);

        // One actual lyrics control and template, reused by NetEase,
        // QQ Music and KuGou. Not just three similar-looking containers.
        LyricsSearchPreviewView lyricView = new();
        ListView lyricsPreview = lyricView.LyricsList;
        // Restore the EXACT pre-provider NetEase behavior: preview rows
        // reuse the existing library lyric template and container style.
        lyricsPreview.ItemTemplate = LyricsList.ItemTemplate;
        lyricsPreview.ItemContainerStyle = LyricsList.ItemContainerStyle;
        InstallLyricScrollSuppression(lyricsPreview);
        _matchLyricsPreviewList = lyricsPreview;

        lyricView.SeekRequested += async timestamp =>
        {
            NeteaseMatchCandidate? selectedCandidate = GetSelectedCandidate();
            if (selectedCandidate is null) return;
            try
            {
                if (_playingPreviewCandidate?.TrackId
                    != selectedCandidate.TrackId)
                {
                    await PlayNetEasePreviewAsync(selectedCandidate, timestamp);
                }
                else
                {
                    _suppressPlaybackLyricFollow = true;
                    try
                    {
                        await SeekPlayerWhenReadyAsync(timestamp);
                        _mediaPlayer.Play();
                    }
                    finally
                    {
                        _suppressPlaybackLyricFollow = false;
                    }
                    UpdatePlaybackLyrics();
                }
            }
            catch (Exception error)
            {
                AppDiagnostics.LogException(
                    "Could not seek NetEase preview lyrics.", error);
            }
        };

        lyricsPreview.ItemsSource = new[]
        {
            new LyricDisplayLine(
                L("Select a result to preview lyrics.",
                  "选择结果后可预览歌词。"),
                null, LyricsDisplayModes.OriginalOnly),
        };

        // Shared search/results/preview XAML for all three providers.
        CloseMatchOverlay();
        Button useSelectedButton = new()
        {
            Content = L("Use selected", "使用所选"),
            IsEnabled = false,
            Style = Application.Current.Resources["AccentButtonStyle"] as Style,
        };
        LyricsSearchDialogView shell = new()
        {
            SearchContent = form,
            CandidatesContent = results,
            LyricsContent = lyricView,
            FooterContent = useSelectedButton,
        };
        shell.Localize(L);

        TaskCompletionSource<NeteaseMatchCandidate?>
            completion =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);
        _matchDialogCompletion =
            completion;

        useSelectedButton.Click +=
            (_, _) =>
            {
                completion.TrySetResult(
                    GetSelectedCandidate());
                CloseMatchOverlay();
            };

        ShowAppOverlay(
            L(
                "Find NetEase match",
                "查找网易云匹配"),
            shell,
            maxWidth: 1200,
            maxHeight: 640);

        NeteaseMatchCandidate?
            GetSelectedCandidate()
        {
            return results.SelectedItem
                is ListViewItem row
                && row.Tag
                    is NeteaseMatchCandidate
                        candidate
                    ? candidate
                    : null;
        }

        // The same XAML candidate card is instantiated by all providers.
        // NetEase additionally supplies its stream preview and song link;
        // QQ/KuGou use the local-track playback delegate.
        ListViewItem CreateCandidateRow(NeteaseMatchCandidate candidate)
        {
            BitmapImage? artwork = null;
            if (Uri.TryCreate(candidate.CoverUrl, UriKind.Absolute,
                out Uri? coverUri)
                && (coverUri.Scheme == Uri.UriSchemeHttps
                    || coverUri.Scheme == Uri.UriSchemeHttp))
            {
                artwork = new BitmapImage
                {
                    DecodePixelWidth = 112,
                    UriSource = coverUri,
                };
            }

            LyricsSearchCandidateCard card = new()
            {
                SongTitle = candidate.Name,
                ArtistName = candidate.Artist,
                AlbumName = candidate.Album,
                DurationText = candidate.DurationMs is long duration
                    ? $"#{candidate.TrackId} · {FormatNetEaseDuration(duration)}"
                    : $"#{candidate.TrackId}",
                ArtworkSource = artwork,
                PreviewTooltip = L(
                    "Play or pause preview", "播放或暂停试听"),
            };
            card.SetExternalLink(
                new Uri($"https://music.163.com/#/song?id={candidate.TrackId}"),
                L("Open in NetEase Music", "在网易云音乐中打开"));
            _neteasePreviewIcons[candidate.TrackId] = card.PlayingIcon;

            card.PreviewRequested += async (_, _) =>
            {
                if (_playingPreviewCandidate?.TrackId == candidate.TrackId
                    && _mediaPlayer.PlaybackSession.PlaybackState
                        == MediaPlaybackState.Playing)
                {
                    _mediaPlayer.Pause();
                }
                else
                {
                    await PlayNetEasePreviewAsync(candidate);
                }
                UpdatePlayerVisualState();
            };

            return new ListViewItem
            {
                Tag = candidate,
                Content = card,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 1, 12, 1),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
        }

        async Task SetResultsAsync(
            Task<IReadOnlyList<
                NeteaseMatchCandidate>>
                request)
        {
            fingerprintButton.IsEnabled =
                false;
            searchButton.IsEnabled =
                false;
            status.Text =
                L(
                    "Searching…",
                    "正在搜索……");
            useSelectedButton.IsEnabled =
                false;
            results.Items.Clear();

            try
            {
                IReadOnlyList<
                    NeteaseMatchCandidate>
                    candidates =
                        await request;

                IReadOnlyList<
                    NeteaseMatchCandidate>
                    ranked =
                        NeteaseCandidateRanker
                            .Rank(
                                candidates,
                                localDurationMs,
                                titleBox.Text,
                                artistBox.Text,
                                albumBox.Text);

                foreach (NeteaseMatchCandidate
                    candidate in ranked)
                {
                    results.Items.Add(
                        CreateCandidateRow(
                            candidate));
                }

                status.Text =
                    ranked.Count == 0
                        ? L(
                            "No matches found.",
                            "没有找到匹配。")
                        : L(
                            $"{ranked.Count:N0} matches found · best duration match selected first.",
                            $"找到 {ranked.Count:N0} 个匹配 · 已优先按时长选择。");

                if (ranked.Count > 0)
                {
                    results.SelectedIndex = 0;
                }
            }
            catch (Exception exception)
            {
                status.Text =
                    UserFacingErrors.Describe(
                        exception, IsChinese);
            }
            finally
            {
                fingerprintButton.IsEnabled =
                    true;
                searchButton.IsEnabled =
                    true;
            }
        }

        fingerprintButton.Click +=
            async (_, _) =>
            {
                using NeteaseClient client =
                    new();
                CompositeFingerprintPcmDecoder
                    decoder = new();

                await SetResultsAsync(
                    client.MatchFileCandidatesAsync(
                        decoder,
                        item.Entry.SourcePath));
            };

        searchButton.Click +=
            async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(
                        titleBox.Text))
                {
                    status.Text =
                        L(
                            "Enter a song title.",
                            "请输入歌曲名。");
                    return;
                }

                using NeteaseClient client =
                    new();

                await SetResultsAsync(
                    client.SearchSongsAsync(
                        titleBox.Text,
                        artistBox.Text,
                        albumBox.Text));
            };

        int lyricPreviewRequest = 0;
        results.SelectionChanged +=
            async (_, _) =>
            {
                int previewRequest = ++lyricPreviewRequest;
                NeteaseMatchCandidate?
                    candidate =
                        GetSelectedCandidate();

                _matchDialogLyricLines.Clear();
                _activeMatchDialogCandidate =
                    candidate;

                if (candidate is null)
                {
                    useSelectedButton.IsEnabled =
                        false;
                    return;
                }

                useSelectedButton.IsEnabled =
                    true;
                lyricsPreview.ItemsSource =
                    new[]
                    {
                        new LyricDisplayLine(
                            L(
                                "Loading lyrics…",
                                "正在加载歌词……"),
                            null,
                            LyricsDisplayModes
                                .OriginalOnly),
                    };

                try
                {
                    using NeteaseClient client =
                        new();

                    LrcDocument? lyrics =
                        await client.GetLyricsAsync(
                            candidate.TrackId);

                    if (previewRequest != lyricPreviewRequest
                        || !ReferenceEquals(
                            _activeMatchDialogCandidate, candidate))
                        return;

                    LyricDisplayLine[] lines =
                        BuildLyricLines(
                            lyrics?.ToString(),
                            LyricsDisplayModes
                                .PreferOriginal);

                    if (lines.Length == 0)
                    {
                        lyricsPreview.ItemsSource =
                            new[]
                            {
                                new LyricDisplayLine(
                                    L(
                                        "No lyrics for this result.",
                                        "这个结果没有歌词。"),
                                    null,
                                    LyricsDisplayModes
                                        .OriginalOnly),
                            };
                    }
                    else
                    {
                        _matchDialogLyricLines.UnionWith(
                            lines);
                        lyricsPreview.ItemsSource =
                            lines;
                    }
                }
                catch (Exception exception)
                {
                    lyricsPreview.ItemsSource =
                        new[]
                        {
                            new LyricDisplayLine(
                                UserFacingErrors
                                    .Describe(
                                        exception, IsChinese),
                                null,
                                LyricsDisplayModes
                                    .OriginalOnly),
                        };
                }
            };

        NeteaseMatchCandidate?
            selected =
                await completion.Task;

        if (ReferenceEquals(
                _matchDialogCompletion,
                completion))
        {
            _matchDialogCompletion =
                null;
        }

        if (selected is not null)
        {
            await SaveManualMatchAsync(
                item,
                selected);
        }
    }

    private static string FormatNetEaseDuration(
        long durationMs)
    {
        TimeSpan duration =
            TimeSpan.FromMilliseconds(
                Math.Max(
                    0,
                    durationMs));

        return duration.TotalHours >= 1
            ? duration.ToString(
                @"h\:mm\:ss",
                CultureInfo.InvariantCulture)
            : duration.ToString(
                @"m\:ss",
                CultureInfo.InvariantCulture);
    }

    private async Task SaveManualMatchAsync(
        LibraryDisplayItem item,
        NeteaseMatchCandidate candidate)
    {
        if (_state is null
            || _stateStore is null)
        {
            return;
        }

        FileInfo source =
            new(item.Entry.SourcePath);

        ulong quickHash =
            await Task.Run(
                () =>
                    QuickFileHash.Compute(
                        item.Entry.SourcePath));

        _state.Tracks.TryGetValue(
            item.Entry.SourcePath,
            out TrackState? previous);

        TrackMetadata metadata =
            item.Entry.Metadata
            ?? new TrackMetadata(
                null,
                null,
                null,
                false,
                item.Title);

        TrackState updated =
            previous
            ?? new TrackState
            {
                SourcePath =
                    item.Entry.SourcePath,
                SourceSize =
                    source.Length,
                SourceLastWriteUtcTicks =
                    source.LastWriteTimeUtc
                        .Ticks,
                QuickHash = quickHash,
                DestinationPath =
                    item.Entry.DestinationPath,
                MetadataCaptured = true,
                TitleCaptured = true,
                Title = metadata.Title,
                Artist = metadata.Artist,
                AlbumArtist =
                    metadata.AlbumArtist,
                Album = metadata.Album,
                HasEmbeddedArt =
                    metadata.HasEmbeddedArt,
                ProcessedAt =
                    DateTimeOffset.UtcNow,
            };

        updated = updated with
        {
            SourceSize =
                source.Length,
            SourceLastWriteUtcTicks =
                source.LastWriteTimeUtc
                    .Ticks,
            QuickHash = quickHash,
            Recognition =
                RecognitionState.Matched,
            RecognitionCheckedAt =
                DateTimeOffset.UtcNow,
            NeteaseSongId =
                candidate.TrackId,
            NeteaseTrackName =
                candidate.Name,
            NeteaseTrackArtist =
                candidate.Artist,
            NeteaseTrackAlbum =
                candidate.Album,
            NeteaseCoverUrl =
                candidate.CoverUrl,
            NeteaseMatchSuppressed =
                false,
            NeteaseMatchConfirmed =
                true,
            Lyrics =
                LyricsState.Unknown,
            LyricsCheckedAt = null,
            // Keep the fingerprint of the old managed lyrics until a
            // successful replacement, so external edits can be detected.
            LyricsFingerprint =
                previous?.LyricsFingerprint,
            RecipeVersion =
                string.Empty,
            ProcessedAt =
                DateTimeOffset.UtcNow,
        };

        _state.Tracks[
            item.Entry.SourcePath] =
            updated;
        _liveNeteaseMatches.Remove(
            item.Entry.SourcePath);

        await _stateStore.SaveAsync(
            _state);

        RefreshCurrentMatchCard(
            item);
        ConfigureAuxiliaryLongText();

        MarkTrackForEnrichmentSync(
            item,
            L(
                "NetEase match changed. Lyrics need to be synchronized.",
                "网易云匹配已更改，需要重新同步歌词。"));
        ReconcileMatchActionRows([item]);

        await SynchronizeMatchedLyricsAsync([item]);
    }

    private async Task ConfirmNeteaseMatchAsync(
        LibraryDisplayItem item)
    {
        await ConfirmNeteaseMatchesAsync([item]);
    }

    private LibraryDisplayItem[] GetSelectedWarningMatches() =>
        LibraryList.SelectedItems
            .OfType<LibraryDisplayItem>()
            .Where(item => GetStoredMatchWarning(item.Entry) is not null)
            .Distinct()
            .ToArray();

    private LibraryDisplayItem[] GetSelectedMatchedItems() =>
        LibraryList.SelectedItems
            .OfType<LibraryDisplayItem>()
            .Where(item =>
                _state is not null
                && _state.Tracks.TryGetValue(
                    item.Entry.SourcePath,
                    out TrackState? track)
                && track.NeteaseSongId.HasValue
                && !track.NeteaseMatchSuppressed)
            .Distinct()
            .ToArray();

    private void UpdateBatchMatchActions()
    {
        if (ConfirmSelectedMatchesButton is null
            || LibraryList is null)
        {
            return;
        }

        bool multi = LibraryList.SelectedItems.Count > 1;
        int warnings = GetSelectedWarningMatches().Length;
        int matched = GetSelectedMatchedItems().Length;

        // Exactly two batch commands in multi-selection mode. When no
        // selected row is eligible, disable the command instead of
        // exposing an unrelated single-track operation.
        ConfirmSelectedMatchesButton.Visibility =
            multi ? Visibility.Visible : Visibility.Collapsed;
        ConfirmSelectedMatchesButton.IsEnabled =
            multi && !_busy && warnings > 0;
        ClearSelectedMatchesButton.Visibility =
            multi ? Visibility.Visible : Visibility.Collapsed;
        ClearSelectedMatchesButton.IsEnabled =
            multi && !_busy && matched > 0;
    }

    private async void ConfirmSelectedMatchesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await ConfirmNeteaseMatchesAsync(GetSelectedWarningMatches());
    }

    private async void ContextConfirmSelectedMatchesItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        await ConfirmNeteaseMatchesAsync(GetSelectedWarningMatches());
    }

    private async Task ConfirmNeteaseMatchesAsync(
        IReadOnlyList<LibraryDisplayItem> candidates)
    {
        if (_busy || _state is null || _stateStore is null)
        {
            return;
        }

        List<LibraryDisplayItem> confirmed = [];
        foreach (LibraryDisplayItem item in candidates.Distinct())
        {
            if (!_state.Tracks.TryGetValue(
                    item.Entry.SourcePath,
                    out TrackState? state)
                || state.NeteaseSongId is null
                || SyncExecutor.DetectMatchWarning(
                    item.Entry,
                    state) is null)
            {
                continue;
            }

            _state.Tracks[item.Entry.SourcePath] =
                state with
                {
                    NeteaseMatchConfirmed = true,
                    NeteaseMatchSuppressed = false,
                    Lyrics = LyricsState.Unknown,
                    LyricsCheckedAt = null,
                    // Retain provenance for a safe lyric replacement.
                    LyricsFingerprint = state.LyricsFingerprint,
                    RecipeVersion = string.Empty,
                    ProcessedAt = DateTimeOffset.UtcNow,
                };

            _liveNeteaseMatches.Remove(item.Entry.SourcePath);
            confirmed.Add(item);
        }

        if (confirmed.Count == 0)
        {
            return;
        }

        await _stateStore.SaveAsync(_state);

        foreach (LibraryDisplayItem item in confirmed)
        {
            MarkTrackForEnrichmentSync(
                item,
                L(
                    "NetEase match confirmed; lyrics need to be refreshed.",
                    "已确认网易云匹配，歌词需要更新。"));
        }

        ReconcileMatchActionRows(confirmed);
        if (_selectedLibraryItem is not null)
        {
            RefreshCurrentMatchCard(_selectedLibraryItem);
        }

        UpdateBatchMatchActions();
        await SynchronizeMatchedLyricsAsync(confirmed);
    }

    private async Task SuppressNeteaseMatchAsync(
        LibraryDisplayItem item) =>
        await SuppressNeteaseMatchesAsync([item]);

    private async void ClearSelectedMatchesButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await SuppressNeteaseMatchesAsync(
            GetSelectedMatchedItems());

    private async void ContextClearSelectedMatchesItem_Click(
        object sender,
        RoutedEventArgs e) =>
        await SuppressNeteaseMatchesAsync(
            GetSelectedMatchedItems());

    private async Task SuppressNeteaseMatchesAsync(
        IReadOnlyList<LibraryDisplayItem> candidates)
    {
        if (_busy || _state is null || _stateStore is null)
        {
            return;
        }

        List<LibraryDisplayItem> changed = [];
        foreach (LibraryDisplayItem item in candidates.Distinct())
        {
            if (!_state.Tracks.TryGetValue(
                    item.Entry.SourcePath,
                    out TrackState? previous)
                || previous.NeteaseSongId is null
                || previous.NeteaseMatchSuppressed)
            {
                continue;
            }

            _state.Tracks[item.Entry.SourcePath] =
                previous with
                {
                    Recognition = RecognitionState.NoMatch,
                    RecognitionCheckedAt = DateTimeOffset.UtcNow,
                    NeteaseSongId = null,
                    NeteaseTrackName = null,
                    NeteaseTrackArtist = null,
                    NeteaseTrackAlbum = null,
                    NeteaseCoverUrl = null,
                    NeteaseMatchSuppressed = true,
                    NeteaseMatchConfirmed = false,
                    Lyrics = LyricsState.Unknown,
                    LyricsCheckedAt = null,
                    // Preserve the fingerprint of old MusicLoom output so
                    // the scanner can recognize it as an output replica.
                    LyricsFingerprint = previous.LyricsFingerprint,
                    RecipeVersion = string.Empty,
                    ProcessedAt = DateTimeOffset.UtcNow,
                };

            _liveNeteaseMatches.Remove(
                item.Entry.SourcePath);
            changed.Add(item);
        }

        if (changed.Count == 0)
        {
            return;
        }

        await _stateStore.SaveAsync(_state);

        SqliteLyricsCache lyricStore = new(DatabasePath);
        foreach (LibraryDisplayItem item in changed)
        {
            LibraryLyricsRecord? canonical =
                await lyricStore.GetLibraryAsync(item.Entry.SourcePath);

            if (canonical is not null
                && canonical.Origin is
                    LibraryLyricsOrigin.Netease
                    or LibraryLyricsOrigin.ManualNetease)
            {
                // Source files may temporarily be absent; never leave an
                // old remote lyric displayed after the match is suppressed.
                // Existing embedded/local canonical lyrics are retained.
                await lyricStore.SaveLibraryAsync(
                    canonical with
                    {
                        Origin = LibraryLyricsOrigin.None,
                        Text = null,
                        SongId = null,
                        SourceSignature = "match-suppressed",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
            }
        }

        await RefreshLibraryLyricsIndexAsync(
            new LibraryPlan(changed.Select(static item => item.Entry).ToArray()),
            _state);

        foreach (LibraryDisplayItem item in changed)
        {
            MarkTrackForEnrichmentSync(
                item,
                L(
                    "NetEase match explicitly cleared; automatic recognition is disabled.",
                    "已明确置空网易云匹配，自动识别已禁用。"));
        }

        ReconcileMatchActionRows(changed);
        if (_selectedLibraryItem is LibraryDisplayItem selected)
        {
            RefreshCurrentMatchCard(selected);
            await ShowTrackDetailsAsync(selected);
        }

        UpdateBatchMatchActions();
        ShowInfo(
            InfoBarSeverity.Success,
            L("NetEase matches cleared", "网易云匹配已置空"),
            L(
                $"{changed.Count:N0} match(es) cleared. Automatic recognition remains disabled for these tracks.",
                $"已置空 {changed.Count:N0} 首歌曲的网易云匹配；这些歌曲不会再自动识别。"));
    }

    private async Task<bool> SynchronizeMatchedLyricsAsync(
        IReadOnlyList<LibraryDisplayItem> candidates)
    {
        if (_busy || _state is null || _stateStore is null
            || _settings is not MusicLoomSettings settings)
        {
            return false;
        }

        (LibraryDisplayItem Item, long SongId)[] matched =
            candidates.Distinct()
                .Select(item =>
                {
                    long? id = _state.Tracks.TryGetValue(
                        item.Entry.SourcePath,
                        out TrackState? track)
                        && !track.NeteaseMatchSuppressed
                            ? track.NeteaseSongId
                            : null;
                    return (Item: item, SongId: id);
                })
                .Where(static pair => pair.SongId.HasValue)
                .Select(static pair => (pair.Item, pair.SongId!.Value))
                .ToArray();

        if (matched.Length == 0)
        {
            return false;
        }

        using CancellationTokenSource cancellation = new();
        _operationCancellation = cancellation;
        int generation = ++_syncProgressGeneration;
        SetBusy(
            true,
            L(
                "Caching matched lyrics…",
                "正在缓存匹配歌词……"));

        SqliteLyricsCache lyricsCache = new(DatabasePath);
        List<PlanEntry> materialize = [];
        List<string> fetchErrors = [];
        int cached = 0;
        int deferred = 0;
        int noLyrics = 0;

        try
        {
            LibraryPathSafety.ValidateStatePath(
                settings,
                DatabasePath);

            // Make the new canonical version a pending output *before*
            // network access. A crash between fetching and materializing
            // cannot leave an old recipe falsely marked synchronized.
            foreach (var (item, songId) in matched)
            {
                if (_state.Tracks.TryGetValue(
                        item.Entry.SourcePath,
                        out TrackState? track)
                    && track.NeteaseSongId == songId)
                {
                    _state.Tracks[item.Entry.SourcePath] =
                        track with
                        {
                            RecipeVersion = string.Empty,
                            Lyrics = LyricsState.Unknown,
                            LyricsCheckedAt = null,
                            ProcessedAt = DateTimeOffset.UtcNow,
                        };
                }
            }

            await _stateStore.SaveAsync(
                _state,
                cancellation.Token);

            using NeteaseClient client = new();
            CompositeFingerprintPcmDecoder decoder = new();
            NeteaseTrackProvider provider = new(client, decoder);

            // Phase one: commit the fetched document to SQLite *before*
            // considering destination drift or even destination existence.
            // A failed copy can never discard a successfully fetched lyric.
            foreach (var (item, songId) in matched)
            {
                cancellation.Token.ThrowIfCancellationRequested();

                SetOperationStatus(
                    L(
                        $"Caching lyrics {cached + fetchErrors.Count + 1:N0}/{matched.Length:N0} · {item.Title}",
                        $"正在缓存歌词 {cached + fetchErrors.Count + 1:N0}/{matched.Length:N0} · {item.Title}"));

                try
                {
                    LrcDocument? latest =
                        await client.GetLyricsAsync(
                            songId,
                            cancellation.Token);

                    // Match actions are disabled during this operation, but
                    // still check identity before publishing the canonical
                    // document to protect against any stale callbacks.
                    if (_state.Tracks.TryGetValue(
                            item.Entry.SourcePath,
                            out TrackState? current)
                        && current.NeteaseSongId == songId
                        && !current.NeteaseMatchSuppressed)
                    {
                        await lyricsCache.SaveExplicitAsync(
                            item.Entry.SourcePath,
                            songId,
                            latest,
                            cancellation.Token);

                        // Validate what the Lyrics tab actually reads,
                        // not just the provider cache. Local embedded/LRC
                        // rows are intentionally allowed to retain priority.
                        LibraryLyricsRecord? canonical =
                            await lyricsCache.GetLibraryAsync(
                                item.Entry.SourcePath,
                                cancellation.Token);
                        if (canonical is null
                            || canonical.Origin != LibraryLyricsOrigin.ManualNetease
                            || canonical.SongId != songId
                            || !string.Equals(
                                canonical.Text,
                                latest is { Lines.Count: > 0 }
                                    ? latest.ToString()
                                    : null,
                                StringComparison.Ordinal))
                        {
                            throw new InvalidDataException(
                                "Canonical lyrics were not updated in SQLite.");
                        }
                        cached++;

                        if (latest is null || latest.Lines.Count == 0)
                        {
                            noLyrics++;
                        }
                    }
                    else
                    {
                        deferred++;
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    fetchErrors.Add(
                        $"{item.Title}: {UserFacingErrors.Describe(exception, IsChinese)}");
                    AppDiagnostics.LogException(
                        "Could not cache matched lyrics.",
                        exception);
                    continue;
                }

                PlanEntry entry = item.Entry;
                TrackState track =
                    _state.Tracks[item.Entry.SourcePath];

                if (entry.DestinationPath is not string destination
                    || entry.Metadata is null
                    || !File.Exists(destination)
                    || !IsPathInside(
                        destination,
                        settings.Path.DestinationDirectory)
                    || !string.Equals(
                        track.DestinationPath,
                        destination,
                        StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(entry.SourcePath))
                {
                    deferred++;
                    continue;
                }

                FileInfo source = new(entry.SourcePath);
                FileInfo target = new(destination);
                ulong hash = await Task.Run(
                    () => QuickFileHash.Compute(
                        entry.SourcePath),
                    cancellation.Token);

                if (!StateStore.IsSourceVersionUnchanged(
                        track, source, hash)
                    || (StateStore.HasDestinationVersion(track)
                        && !StateStore.IsDestinationVersionUnchanged(
                            track, target)))
                {
                    // The cached lyrics are safe even if the destination is
                    // not. The regular scanner will repair materialization.
                    deferred++;
                    continue;
                }

                materialize.Add(entry with
                {
                    Status = PlanEntryStatus.Modified,
                    NeedsAudioCopy = false,
                    Detail = "Materialize canonical cached lyrics.",
                });
            }

            // Re-evaluate local embedded/sidecar priority for the affected
            // sources before any output write. This also removes stale local
            // signatures when a sidecar disappeared since the last scan.
            await RefreshLibraryLyricsIndexAsync(
                new LibraryPlan(
                    matched.Select(static pair => pair.Item.Entry).ToArray()),
                _state,
                cancellation.Token);

            // Phase two is optional. Its only input is the canonical DB
            // record; the NetEase endpoint is never fetched a second time.
            SyncExecutionSummary? summary = null;
            if (materialize.Count > 0)
            {
                ITrackLyricsWriter writer =
                    LyricsStorageModes.Normalize(
                        settings.Organizer.Lyrics.StorageMode)
                        == LyricsStorageModes.Sidecar
                            ? new SidecarLrcLyricsWriter()
                            : new TagLibSharpLyricsWriter();

                SyncExecutor executor = new(
                    new LocalLibraryMaterializer(),
                    provider,
                    provider,
                    writer,
                    lyricsCache: lyricsCache,
                    libraryLyricsCache: lyricsCache,
                    embeddedLyricsCleaner: new TagLibSharpLyricsWriter(),
                    lyricsOffsetStore: lyricsCache);

                SyncProgressTracker tracker = new();
                Progress<SyncExecutionProgress> progress =
                    new(value =>
                    {
                        if (generation != _syncProgressGeneration
                            || !tracker.ShouldApply(value))
                        {
                            return;
                        }

                        SetOperationStatus(
                            $"{FriendlyStage(value.Stage)} · " +
                            $"{value.Processed:N0}/{value.Total:N0}");
                        SetProgress(value.Processed, value.Total);
                        // This is lyric-only work. Avoid turning a matching
                        // warning into pending/synced for a brief moment.
                    });

                summary = await Task.Run(
                    () => executor.ExecuteAsync(
                        settings,
                        new LibraryPlan(materialize),
                        _state,
                        _stateStore,
                        progress,
                        cancellation.Token,
                        lyricsOnly: true),
                    cancellation.Token);
            }

            ++_syncProgressGeneration;
            await RefreshMatchedLyricsPresentationAsync(
                matched.Select(static match => match.Item).ToArray(),
                cancellation.Token);

            int materializationErrors = summary?.Errors.Count ?? 0;
            bool complete =
                cached == matched.Length
                && deferred == 0
                && fetchErrors.Count == 0
                && materializationErrors == 0
                && (summary?.CompletedSourcePaths.Count ?? 0)
                    == materialize.Count;

            if (fetchErrors.Count > 0
                || materializationErrors > 0)
            {
                string message = fetchErrors.FirstOrDefault()
                    ?? (summary?.Errors.FirstOrDefault() is TrackSyncError firstError
                        ? UserFacingErrors.DescribeSyncError(firstError, IsChinese)
                        : null)
                    ?? L(
                        "Could not materialize cached lyrics.",
                        "无法写入数据库中缓存的歌词。");
                ShowInfo(
                    InfoBarSeverity.Warning,
                    L("Some lyrics still need attention", "部分歌词需要处理"),
                    message);
            }
            else if (deferred > 0)
            {
                ShowInfo(
                    InfoBarSeverity.Informational,
                    L("Lyrics cached", "歌词已缓存"),
                    L(
                        $"{cached:N0} track(s) are saved in the database; {deferred:N0} await destination synchronization.",
                        $"已将 {cached:N0} 首歌曲的歌词保存到数据库；其中 {deferred:N0} 首等待目标文件同步。"));
            }
            else if (!settings.Organizer.Lyrics.OverrideExisting
                     && (summary?.ExistingLyrics ?? 0) > 0)
            {
                ShowInfo(
                    InfoBarSeverity.Informational,
                    L("Lyrics cached; target preserved", "歌词已缓存，目标原有歌词已保留"),
                    L(
                        $"{cached:N0} lyric record(s) updated in the database. Existing destination lyrics were kept because 'Overwrite existing lyrics' is off.",
                        $"已将 {cached:N0} 首歌词更新到数据库。由于已关闭“覆盖已有歌词”，目标目录中的原有歌词保持不变。"));
            }
            else if (noLyrics > 0)
            {
                ShowInfo(
                    InfoBarSeverity.Informational,
                    L("Lyrics checked", "已检查歌词"),
                    L(
                        $"{noLyrics:N0} matching track(s) have no available NetEase lyrics.",
                        $"{noLyrics:N0} 首匹配歌曲没有可用的网易云歌词。"));
            }
            else
            {
                ShowInfo(
                    InfoBarSeverity.Success,
                    L("Lyrics synchronized", "歌词已同步"),
                    L(
                        $"{cached:N0} track(s) cached and synchronized.",
                        $"已缓存并同步 {cached:N0} 首歌曲的歌词。"));
            }

            return complete;
        }
        catch (OperationCanceledException)
        {
            ++_syncProgressGeneration;
            await RefreshMatchedLyricsPresentationAsync(
                matched.Select(static match => match.Item).ToArray(),
                CancellationToken.None);
            return false;
        }
        catch (Exception exception)
        {
            ++_syncProgressGeneration;
            AppDiagnostics.LogException(
                "Could not update the lyrics cache.",
                exception);

            await RefreshMatchedLyricsPresentationAsync(
                matched.Select(static match => match.Item).ToArray(),
                CancellationToken.None);

            ShowInfo(
                InfoBarSeverity.Warning,
                L("Lyrics update failed", "歌词更新失败"),
                UserFacingErrors.Describe(exception, IsChinese));
            return false;
        }
        finally
        {
            ++_syncProgressGeneration;
            _operationCancellation = null;
            SetBusy(false, OperationStatusText.Text);
        }
    }

    private async Task RefreshMatchedLyricsPresentationAsync(
        IReadOnlyList<LibraryDisplayItem> candidates,
        CancellationToken cancellationToken)
    {
        // SyncExecutor checkpoints to SQLite, so reload only its durable
        // state. The list's object identity, selection and filters remain
        // unchanged during a manual matching/lyric action.
        if (_stateStore is not null)
        {
            _state = await _stateStore.LoadAsync(cancellationToken);
        }

        ReconcileMatchActionRows(candidates);
        if (_selectedLibraryItem is LibraryDisplayItem selected
            && candidates.Any(item =>
                string.Equals(
                    item.Entry.SourcePath,
                    selected.Entry.SourcePath,
                    StringComparison.OrdinalIgnoreCase)))
        {
            await ShowTrackDetailsAsync(selected);
        }

        RefreshDatabaseSize();
    }

    private bool HasCachedMatch(
        string sourcePath) =>
        _state is not null
        && _state.Tracks.TryGetValue(
            sourcePath,
            out TrackState? state)
        && state.NeteaseSongId.HasValue
        && !state.NeteaseMatchSuppressed;

    private void MarkTrackForEnrichmentSync(
        LibraryDisplayItem item,
        string detail)
    {
        _manualPendingSources.Add(
            item.Entry.SourcePath);

        // Register work for the next sync, but never change the visible
        // diagnostic classification during a lyric lookup. In particular,
        // an unconfirmed NetEase warning stays a warning throughout.
        SetTrackSyncState(
            item.Entry.SourcePath,
            item.Kind == "warning"
                ? L("Warning", "警告")
                : item.StatusText,
            detail);

        RefreshLiveSyncCounters();
        UpdateBatchMatchActions();
        RefreshTrackSyncTab(
            item);

        if (!_busy)
        {
            SyncButton.IsEnabled = true;
        }
    }

    private void RefreshCurrentMatchCard(LibraryDisplayItem? item)
    {
        long? songId = null;
        string? trackName = null;
        string? artist = null;
        string? album = null;
        string? coverUrl = null;
        bool suppressed = false;

        if (item is not null
            && _liveNeteaseMatches.TryGetValue(
                item.Entry.SourcePath, out LiveNeteaseMatch? live))
        {
            songId = live.TrackId;
            trackName = live.Name;
            artist = live.Artist;
            album = live.Album;
            coverUrl = live.CoverUrl;
        }
        else if (item is not null
            && _state is not null
            && _state.Tracks.TryGetValue(
                item.Entry.SourcePath, out TrackState? state))
        {
            suppressed = state.NeteaseMatchSuppressed;
            if (state.NeteaseSongId is long cachedSongId)
            {
                songId = cachedSongId;
                trackName = state.NeteaseTrackName;
                artist = state.NeteaseTrackArtist;
                album = state.NeteaseTrackAlbum;
                coverUrl = state.NeteaseCoverUrl;
            }
        }

        TrackSyncWarning? warning = item is null
            ? null : GetStoredMatchWarning(item.Entry);
        NeteaseMatchPanel.Present(new MatchTabPresentation(
            Selected: item is not null,
            Suppressed: suppressed,
            Id: songId?.ToString(CultureInfo.InvariantCulture),
            // NetEase legacy behavior: keep the local title/artist/album
            // as display fallbacks for an existing real NetEase ID only.
            // QQ/KuGou adapters never infer a remote match from local tags.
            Title: songId is null ? null : trackName ?? item?.Title,
            Artist: songId is null ? null : artist ?? item?.Artist,
            Album: songId is null ? null : album ?? item?.Album,
            CoverUrl: coverUrl,
            ExternalLink: songId is long id
                ? new Uri($"https://music.163.com/#/song?id={id}")
                : null,
            NeedsConfirmation: warning is not null,
            CanUpdateLyrics: item is not null
                && HasCachedMatch(item.Entry.SourcePath),
            Busy: _busy));

        string? identity = item is not null && songId is long resolvedId
            && !suppressed
                ? item.Entry.SourcePath + "\0" +
                    resolvedId.ToString(CultureInfo.InvariantCulture)
                : null;
        if (!string.Equals(_netEaseArtworkMatchIdentity, identity,
                StringComparison.Ordinal))
        {
            _netEaseArtworkMatchIdentity = identity;
            _netEaseArtworkRequestPending = false;
            ++_matchArtworkRequestId;
        }

        // A missing URL is hydrated once while a request is active.
        // Progress/status/theme refreshes may call this repeatedly: do NOT
        // cancel/restart the in-flight NetEase GetSongAsync on each call.
        if (identity is not null && songId is long matchId
            && item is not null && !_busy
            && string.IsNullOrWhiteSpace(coverUrl)
            && NeteaseMatchPanel.DisplayedArtworkUri is null
            && !_netEaseArtworkRequestPending)
        {
            _netEaseArtworkRequestPending = true;
            _ = LoadCurrentMatchArtworkAsync(
                item, matchId, _matchArtworkRequestId);
        }
    }

    private async Task LoadCurrentMatchArtworkAsync(
        LibraryDisplayItem item,
        long songId,
        int requestId)
    {
        try
        {
            NeteaseMatchCandidate? song;
            if (_netEaseArtworkSmokeResolver is not null)
            {
                song = await _netEaseArtworkSmokeResolver(songId);
            }
            else
            {
                using NeteaseClient client = new();
                song = await client.GetSongAsync(songId);
            }

            if (song is null
                || string.IsNullOrWhiteSpace(
                    song.CoverUrl)
                || requestId
                    != _matchArtworkRequestId
                || _selectedLibraryItem
                    is not LibraryDisplayItem selected
                || !string.Equals(
                    selected.Entry.SourcePath,
                    item.Entry.SourcePath,
                    StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(
                    song.CoverUrl,
                    UriKind.Absolute,
                    out Uri? coverUri))
            {
                return;
            }

            NeteaseMatchPanel.ShowArtwork(coverUri);

            if (_netEaseArtworkSmokeResolver is null
                && _state is not null
                && _stateStore is not null
                && _state.Tracks.TryGetValue(
                    item.Entry.SourcePath,
                    out TrackState? state)
                && state.NeteaseSongId
                    == songId
                && !_busy
                && string.IsNullOrWhiteSpace(
                    state.NeteaseCoverUrl))
            {
                _state.Tracks[
                    item.Entry.SourcePath] =
                    state with
                    {
                        NeteaseCoverUrl =
                            song.CoverUrl,
                    };

                await _stateStore.SaveAsync(
                    _state);
            }
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not load NetEase artwork.",
                exception);
        }
        finally
        {
            // Retry is possible on a subsequent explicit refresh. The
            // successful image itself is retained by ProviderMatchTabView.
            if (requestId == _matchArtworkRequestId)
                _netEaseArtworkRequestPending = false;
        }
    }

    private void LibraryList_RightTapped(
        object sender,
        RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource
                is FrameworkElement element
            && element.DataContext
                is LibraryDisplayItem item)
        {
            if (!LibraryList.SelectedItems.Contains(item))
            {
                LibraryList.SelectedItem = item;
            }

            _contextLibraryItem = item;
        }
        else
        {
            _contextLibraryItem =
                LibraryList.SelectedItem
                    as LibraryDisplayItem;
        }
    }

    private void TrackContextMenu_Opening(
        object sender,
        object e)
    {
        LibraryDisplayItem? item =
            _contextLibraryItem
            ?? LibraryList.SelectedItem as LibraryDisplayItem;

        bool multi = LibraryList.SelectedItems.Count > 1;
        bool hasItem = item is not null && !multi;
        bool hasDestination =
            item?.Entry.DestinationPath is string destination
            && File.Exists(destination);

        // Do not merely disable single-item actions: hide the entire
        // group, including its separators, during extended selection.
        Visibility single = multi
            ? Visibility.Collapsed
            : Visibility.Visible;
        ContextPlayItem.Visibility = single;
        ContextOpenSourceItem.Visibility = single;
        ContextOpenDestinationItem.Visibility = single;
        ContextCopySourceItem.Visibility = single;
        ContextCopyDestinationItem.Visibility = single;
        ContextFindMatchItem.Visibility = single;
        ContextFileSeparator.Visibility = single;
        ContextCopySeparator.Visibility = single;
        ContextMatchSeparator.Visibility = single;

        ContextPlayItem.IsEnabled = hasItem;
        ContextOpenSourceItem.IsEnabled = hasItem;
        ContextCopySourceItem.IsEnabled = hasItem;
        ContextFindMatchItem.IsEnabled = hasItem && !_busy;
        ContextOpenDestinationItem.IsEnabled =
            hasItem && hasDestination;
        ContextCopyDestinationItem.IsEnabled =
            hasItem && item!.Entry.DestinationPath is not null;

        bool canConfirm = hasItem
            && GetStoredMatchWarning(item!.Entry) is not null;
        ContextConfirmMatchItem.Visibility =
            canConfirm ? Visibility.Visible : Visibility.Collapsed;
        ContextConfirmMatchItem.IsEnabled =
            canConfirm && !_busy;

        bool canClear = hasItem
            && HasCachedMatch(item!.Entry.SourcePath);
        ContextClearMatchItem.Visibility =
            canClear ? Visibility.Visible : Visibility.Collapsed;
        ContextClearMatchItem.IsEnabled =
            canClear && !_busy;

        int selectedWarnings = GetSelectedWarningMatches().Length;
        ContextConfirmSelectedMatchesItem.Visibility =
            multi ? Visibility.Visible : Visibility.Collapsed;
        ContextConfirmSelectedMatchesItem.IsEnabled =
            multi && selectedWarnings > 0 && !_busy;

        int selectedMatches = GetSelectedMatchedItems().Length;
        ContextClearSelectedMatchesItem.Visibility =
            multi ? Visibility.Visible : Visibility.Collapsed;
        ContextClearSelectedMatchesItem.IsEnabled =
            multi && selectedMatches > 0 && !_busy;
        ContextUpdateSelectedLyricsItem.Visibility =
            multi ? Visibility.Visible : Visibility.Collapsed;
        ContextUpdateSelectedLyricsItem.IsEnabled =
            multi && selectedMatches > 0 && !_busy;
    }

    private async void ContextOpenSourceItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem()
            is not LibraryDisplayItem item)
        {
            return;
        }

        try
        {
            await OpenInExplorerAsync(
                item.Entry.SourcePath);
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not open the source folder.",
                    "无法打开源文件位置。"),
                exception);
        }
    }

    private async void ContextOpenDestinationItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem()
                ?.Entry.DestinationPath
            is not string path)
        {
            return;
        }

        try
        {
            await OpenInExplorerAsync(
                path);
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not open the destination folder.",
                    "无法打开目标文件位置。"),
                exception);
        }
    }

    private async void ContextCopySourceItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem()
            is LibraryDisplayItem item)
        {
            await CopyTextAsync(
                item.Entry.SourcePath);
        }
    }

    private async void ContextCopyDestinationItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem()
                ?.Entry.DestinationPath
            is string path)
        {
            await CopyTextAsync(path);
        }
    }

    private async void ContextFindMatchItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem()
            is LibraryDisplayItem item)
        {
            LibraryList.SelectedItem =
                item;
            await ShowMatchDialogAsync(
                item);
        }
    }

    private async void ContextConfirmMatchItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem()
            is LibraryDisplayItem item)
        {
            LibraryList.SelectedItem =
                item;
            await ConfirmNeteaseMatchAsync(
                item);
        }
    }

    private async void ContextClearMatchItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (GetContextItem() is LibraryDisplayItem item
            && HasCachedMatch(item.Entry.SourcePath))
        {
            LibraryList.SelectedItem = item;
            await SuppressNeteaseMatchAsync(item);
        }
    }

    private async void ContextUpdateSelectedLyricsItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        await SynchronizeMatchedLyricsAsync(
            GetSelectedMatchedItems());
    }

    private LibraryDisplayItem?
        GetContextItem() =>
        _contextLibraryItem
        ?? LibraryList.SelectedItem
            as LibraryDisplayItem;

    private async Task CopyTextAsync(
        string text)
    {
        try
        {
            DataPackage package = new();
            package.SetText(text);
            await ClipboardAccessRetry.RunAsync(
                () => Clipboard.SetContent(package));
        }
        catch (Exception exception)
        {
            // A temporarily locked clipboard must never crash the UI.
            AppDiagnostics.LogException(
                "Could not copy text to the clipboard.",
                exception);
            bool clipboardBusy =
                exception is System.Runtime.InteropServices.COMException com
                && com.HResult == ClipboardAccessRetry.ClipboardBusyHresult;
            ShowInfo(
                InfoBarSeverity.Warning,
                L("Copy failed", "复制失败"),
                clipboardBusy
                    ? L(
                        "Another app is using the clipboard. Please try again.",
                        "其他程序正在占用剪贴板，请稍后重试。")
                    : L(
                        "Could not access the clipboard. See the diagnostic log.",
                        "无法访问剪贴板，请查看诊断日志。"));
            return;
        }

        try
        {
            // SetContent already copied the text. Flush makes it persist
            // past process exit; failure here must never crash a user action.
            await ClipboardAccessRetry.RunAsync(Clipboard.Flush);
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Clipboard copy succeeded, but Flush could not persist it.",
                exception);
        }
    }

    private static async Task OpenInExplorerAsync(
        string path)
    {
        Exception? shellError = null;

        if (File.Exists(path)
            && ExplorerIntegration.TryRevealFile(
                path,
                out shellError))
        {
            return;
        }

        string directory =
            Directory.Exists(path)
                ? path
                : Path.GetDirectoryName(path)
                    ?? path;

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                directory);
        }

        bool launched =
            await Windows.System.Launcher
                .LaunchFolderPathAsync(
                    directory);

        if (!launched)
        {
            throw shellError
                ?? new InvalidOperationException(
                    $"Windows could not open '{directory}'.");
        }
    }

    private PlaylistDisplayItem[]
        BuildPlaylists(
            MusicLoomSettings settings)
    {
        Dictionary<string, LibraryDisplayItem>
            trackLookup =
                _allLibraryItems.ToDictionary(
                    static item =>
                        Path.GetFullPath(
                            item.Entry.SourcePath),
                    StringComparer
                        .OrdinalIgnoreCase);

        HashSet<LibraryDisplayItem>
            categorized = [];
        List<PlaylistDisplayItem> result = [];

        string root =
            settings.Path
                .SourcePlaylistDirectory;

        if (!string.IsNullOrWhiteSpace(root)
            && Directory.Exists(root))
        {
            foreach (string playlistPath
                in Directory.EnumerateFiles(
                    root,
                    "*.m3u",
                    SearchOption.TopDirectoryOnly)
                    .Order(
                        StringComparer
                            .CurrentCultureIgnoreCase))
            {
                IReadOnlyList<string> paths =
                    PlaylistMapper.ParseM3u(
                        File.ReadAllLines(
                            playlistPath));

                List<LibraryDisplayItem>
                    tracks = [];

                string playlistDirectory =
                    Path.GetDirectoryName(
                        playlistPath)
                    ?? root;

                foreach (string rawPath in paths)
                {
                    string normalized;
                    try
                    {
                        normalized =
                            Path.GetFullPath(
                                Path.IsPathRooted(
                                    rawPath)
                                    ? rawPath
                                    : Path.Combine(
                                        playlistDirectory,
                                        rawPath));
                    }
                    catch
                    {
                        normalized = rawPath;
                    }

                    if (trackLookup.TryGetValue(
                            normalized,
                            out LibraryDisplayItem?
                                displayItem))
                    {
                        tracks.Add(
                            displayItem);
                        categorized.Add(
                            displayItem);
                    }
                }

                string destinationPath =
                    Path.Combine(
                        settings.Path
                            .DestinationDirectory,
                        Path.GetFileNameWithoutExtension(
                            playlistPath)
                        + ".txt");

                string playlistBase =
                    PlaylistMapper
                        .NormalizeDestinationBase(
                            settings.Path
                                .DestinationPlaylistBase);

                string[] expectedLines =
                    tracks
                        .Where(static displayItem =>
                            displayItem.Entry
                                .DestinationPath
                            is not null)
                        .Select(displayItem =>
                        {
                            string relative =
                                Path.GetRelativePath(
                                    settings.Path
                                        .DestinationDirectory,
                                    displayItem.Entry
                                        .DestinationPath!)
                                    .Replace(
                                        '\\',
                                        '/');

                            return
                                string.IsNullOrEmpty(
                                    playlistBase)
                                    ? relative
                                    : $"{playlistBase}/{relative}";
                        })
                        .ToArray();

                string expected =
                    string.Join(
                        "\n",
                        expectedLines);

                bool isReady =
                    File.Exists(
                        destinationPath)
                    && string.Equals(
                        File.ReadAllText(
                            destinationPath),
                        expected,
                        StringComparison.Ordinal);

                result.Add(
                    new PlaylistDisplayItem(
                        Path.GetFileNameWithoutExtension(
                            playlistPath),
                        playlistPath,
                        destinationPath,
                        tracks,
                        isReady));
            }
        }

        LibraryDisplayItem[] uncategorized =
            SortLibraryItems(
                _allLibraryItems
                    .Where(item =>
                        !categorized.Contains(item)),
                "artist")
                .ToArray();

        result.Insert(
            0,
            new PlaylistDisplayItem(
                L(
                    "Uncategorized",
                    "未分类"),
                string.Empty,
                string.Empty,
                uncategorized,
                IsReady: true,
                IsVirtual: true,
                ShowDividerAfter: true));

        result.Insert(
            0,
            new PlaylistDisplayItem(
                L(
                    "Now Playing",
                    "正在播放"),
                string.Empty,
                string.Empty,
                _playbackQueue.ToArray(),
                IsReady: true,
                IsVirtual: true,
                IsNowPlaying: true));

        return [.. result];
    }

    private void RefreshNowPlayingPlaylist()
    {
        int index =
            _playlists.FindIndex(
                static playlist =>
                    playlist.IsNowPlaying);

        if (index < 0
            || PlaylistsList is null)
        {
            return;
        }

        PlaylistDisplayItem?
            selected =
                PlaylistsList.SelectedItem
                    as PlaylistDisplayItem;

        PlaylistDisplayItem updated =
            new(
                L(
                    "Now Playing",
                    "正在播放"),
                string.Empty,
                string.Empty,
                _playbackQueue.ToArray(),
                IsReady: true,
                IsVirtual: true,
                IsNowPlaying: true);

        updated.ApplyLongTextMode(
            CurrentLongTextMode);

        _playlists[index] =
            updated;
        PlaylistsList.ItemsSource =
            _playlists.ToArray();

        if (selected?.IsNowPlaying
            == true)
        {
            PlaylistsList.SelectedItem =
                updated;
        }
        else if (selected is not null
            && _playlists.Contains(
                selected))
        {
            PlaylistsList.SelectedItem =
                selected;
        }
    }

    private void PopulatePlaylists(
        IEnumerable<PlaylistDisplayItem>
            playlists)
    {
        PlaylistDisplayItem? previous =
            PlaylistsList.SelectedItem as PlaylistDisplayItem;

        _playlists.Clear();
        _playlists.AddRange(playlists);

        foreach (PlaylistDisplayItem playlist
            in _playlists)
        {
            playlist.ApplyLongTextMode(
                CurrentLongTextMode);
        }

        PlaylistsList.ItemsSource =
            _playlists.ToArray();

        if (_playlists.Count > 0)
        {
            PlaylistDisplayItem? restored = previous is null
                ? null
                : _playlists.FirstOrDefault(item =>
                    previous.IsNowPlaying
                        ? item.IsNowPlaying
                        : previous.IsVirtual
                            ? item.IsVirtual && !item.IsNowPlaying
                            : !item.IsVirtual
                                && string.Equals(
                                    item.SourcePath,
                                    previous.SourcePath,
                                    StringComparison.OrdinalIgnoreCase));

            PlaylistsList.SelectedItem =
                restored ?? _playlists[
                    _playlists.Count > 1 ? 1 : 0];
        }
        else
        {
            PlaylistTitleText.Text =
                L(
                    "No playlists",
                    "没有播放列表");
            PlaylistMetaText.Text =
                L(
                    "Configure a MusicBee playlist folder in Settings.",
                    "请在“设置”中配置 MusicBee 播放列表目录。");
            PlaylistTracksList.ItemsSource =
                null;
            ConfigureAuxiliaryLongText();
        }
    }

    private void PlaylistsList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (PlaylistsList.SelectedItem
            is not PlaylistDisplayItem playlist)
        {
            PlaylistTracksList.ItemsSource =
                null;
            PlaylistHeaderPlayButton.Visibility =
                Visibility.Collapsed;
            PlaylistHeaderPlayButton.IsEnabled =
                false;
            return;
        }

        PlaylistHeaderPlayButton.Visibility =
            playlist.IsVirtual
                ? Visibility.Collapsed
                : Visibility.Visible;
        PlaylistHeaderPlayButton.IsEnabled =
            playlist.TrackCount > 0;

        PlaylistTitleText.Text =
            playlist.Name;
        PlaylistMetaText.Text =
            playlist.IsVirtual
                ? L(
                    $"{playlist.TrackCount:N0} tracks",
                    $"{playlist.TrackCount:N0} 首歌曲")
                : playlist.IsReady
                    ? L(
                        $"{playlist.TrackCount:N0} tracks · Ready",
                        $"{playlist.TrackCount:N0} 首歌曲 · 已就绪")
                    : L(
                        $"{playlist.TrackCount:N0} tracks · Pending sync",
                        $"{playlist.TrackCount:N0} 首歌曲 · 待同步");

        ConfigureAuxiliaryLongText();
        ApplyPlaylistTrackFilter();
    }

    private void PlaylistSearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e) =>
        ApplyPlaylistTrackFilter();

    private void PlaylistSortBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_refreshingLocalizedChoices)
        {
            ApplyPlaylistTrackFilter();
        }
    }

    private void PlaylistSortDirectionBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_refreshingLocalizedChoices)
        {
            ApplyPlaylistTrackFilter();
        }
    }

    private void ApplyPlaylistTrackFilter()
    {
        if (PlaylistsList.SelectedItem
            is not PlaylistDisplayItem playlist)
        {
            PlaylistTracksList.ItemsSource =
                null;
            return;
        }

        string query =
            PlaylistSearchBox.Text.Trim();

        string sort =
            (PlaylistSortBox.SelectedItem
                as ComboBoxItem)
                ?.Tag?.ToString()
            ?? "artist";

        IEnumerable<LibraryDisplayItem>
            items = playlist.Tracks;

        if (!string.IsNullOrWhiteSpace(
                query))
        {
            items = items.Where(
                item =>
                    item.SearchText.Contains(
                        query,
                        StringComparison
                            .OrdinalIgnoreCase));
        }

        items =
            SortLibraryItems(
                items,
                sort);

        bool descending =
            string.Equals(
                (PlaylistSortDirectionBox.SelectedItem
                    as ComboBoxItem)
                    ?.Tag?.ToString(),
                "desc",
                StringComparison.Ordinal);

        if (descending)
        {
            items = items.Reverse();
        }

        PlaylistTracksList.ItemsSource =
            items.ToArray();
    }

    private void PlaylistTracksList_RightTapped(
        object sender,
        RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource
                is FrameworkElement element
            && element.DataContext
                is LibraryDisplayItem item)
        {
            PlaylistTracksList.SelectedItem =
                item;
            _playlistContextItem = item;
        }
        else
        {
            _playlistContextItem =
                PlaylistTracksList.SelectedItem
                    as LibraryDisplayItem;
        }
    }

    private void PlaylistShowInLibraryItem_Click(
        object sender,
        RoutedEventArgs e)
    {
        LibraryDisplayItem? item =
            _playlistContextItem
            ?? PlaylistTracksList.SelectedItem
                as LibraryDisplayItem;

        if (item is null)
        {
            return;
        }

        LibrarySearchBox.Text =
            string.Empty;
        LibraryFilterBox.SelectedIndex = 0;
        LibraryLyricsFacetBox.SelectedIndex = 0;
        LibraryNeteaseFacetBox.SelectedIndex = 0;
        LibraryQqFacetBox.SelectedIndex = 0;
        LibraryKugouFacetBox.SelectedIndex = 0;
        ApplyLibraryFilter();

        NavigateTo("library");

        LibraryList.SelectedItem =
            item;
        LibraryList.ScrollIntoView(item);
    }

    private async void BrowseSourceButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        string? path =
            await PickFolderAsync();
        if (path is not null)
        {
            SourcePathBox.Text = path;
        }
    }

    private async void BrowseDestinationButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        string? path =
            await PickFolderAsync();
        if (path is not null)
        {
            DestinationPathBox.Text =
                path;
        }
    }

    private async void BrowsePlaylistButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        string? path =
            await PickFolderAsync();
        if (path is not null)
        {
            PlaylistSourcePathBox.Text =
                path;
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        try
        {
            FolderPicker picker = new()
            {
                SuggestedStartLocation =
                    PickerLocationId.MusicLibrary,
            };
            picker.FileTypeFilter.Add("*");

            nint hwnd =
                WinRT.Interop.WindowNative
                    .GetWindowHandle(this);
            WinRT.Interop
                .InitializeWithWindow
                .Initialize(
                    picker,
                    hwnd);

            Windows.Storage.StorageFolder?
                folder =
                    await picker
                        .PickSingleFolderAsync();

            return folder is not null
                && !string.IsNullOrWhiteSpace(
                    folder.Path)
                    ? folder.Path
                    : null;
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not open the folder picker.",
                    "无法打开文件夹选择器。"),
                exception);
            return null;
        }
    }

    private async void OpenSourceButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await TryOpenConfiguredDirectoryAsync(
            SourcePathBox.Text);

    private async void OpenDestinationSettingButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await TryOpenConfiguredDirectoryAsync(
            DestinationPathBox.Text);

    private async void OpenPlaylistButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await TryOpenConfiguredDirectoryAsync(
            PlaylistSourcePathBox.Text);

    private async Task
        TryOpenConfiguredDirectoryAsync(
            string path)
    {
        if (string.IsNullOrWhiteSpace(
                path))
        {
            return;
        }

        try
        {
            string full =
                Path.GetFullPath(path);

            if (!Directory.Exists(full))
            {
                return;
            }

            bool launched =
                await Windows.System.Launcher
                    .LaunchFolderPathAsync(
                        full);

            if (!launched)
            {
                throw new InvalidOperationException(
                    $"Windows could not open '{full}'.");
            }
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not open the configured folder.",
                    "无法打开已配置的目录。"),
                exception);
        }
    }

    private void SettingsControl_Changed(
        object sender,
        TextChangedEventArgs e) =>
        ScheduleSettingsSave();

    private void SettingsControl_Changed(
        object sender,
        RoutedEventArgs e) =>
        ScheduleSettingsSave();

    private void SettingsControl_Changed(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_refreshingLocalizedChoices)
        {
            ScheduleSettingsSave();
        }
    }

    private void LyricsRefreshHoursBox_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args) =>
        ScheduleSettingsSave();

    private void ScheduleSettingsSave()
    {
        if (_loadingSettings)
        {
            return;
        }

        _settingsSaveCancellation
            ?.Cancel();
        _settingsSaveCancellation
            ?.Dispose();

        CancellationTokenSource cts =
            new();
        _settingsSaveCancellation =
            cts;

        _ = SaveSettingsAfterDelayAsync(
            cts.Token);
    }

    private async Task
        SaveSettingsAfterDelayAsync(
            CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                450,
                cancellationToken);

            MusicLoomSettings settings =
                BuildSettingsFromForm();

            bool planChanged =
                _settings is null
                || SettingsAffectPlan(
                    _settings,
                    settings);

            await TomlSettingsStore.SaveAsync(
                ConfigPath,
                settings,
                cancellationToken);

            _settings = settings;
            _uiLanguage =
                NormalizeLanguage(
                    settings.Ui.Language);
            _accentColor =
                settings.Ui.AccentColor;

            ApplyAppearance(settings);
            ApplyLanguage();

            foreach (LibraryDisplayItem item
                in _allLibraryItems)
            {
                item.ApplyLongTextMode(
                    CurrentLongTextMode);
            }

            PlaylistDisplayItem?
                selectedPlaylist =
                    PlaylistsList.SelectedItem
                        as PlaylistDisplayItem;

            foreach (PlaylistDisplayItem playlist
                in _playlists)
            {
                playlist.ApplyLongTextMode(
                    CurrentLongTextMode);
            }

            if (_playlists.Count > 0)
            {
                PlaylistsList.ItemsSource =
                    _playlists.ToArray();

                if (selectedPlaylist is not null
                    && _playlists.Contains(
                        selectedPlaylist))
                {
                    PlaylistsList.SelectedItem =
                        selectedPlaylist;
                }
            }

            if (_selectedLibraryItem is not null)
            {
                ConfigureTitleAutoScroll();
            }

            ConfigureAuxiliaryLongText();

            if (planChanged
                && _plan is not null)
            {
                SyncButton.IsEnabled = false;
                OperationStatusText.Text =
                    L(
                        "Settings changed · refresh the library.",
                        "设置已更改 · 请刷新音乐库。");
            }

            UpdateLibraryEmptyState();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not auto-save settings.",
                exception);

            ShowInfo(
                InfoBarSeverity.Error,
                L(
                    "Could not save settings",
                    "无法保存设置"),
                UserFacingErrors.Describe(
                    exception, IsChinese));
        }
    }

    private static bool SettingsAffectPlan(
        MusicLoomSettings left,
        MusicLoomSettings right)
    {
        return left.Path.SourceDirectory != right.Path.SourceDirectory
            || left.Path.DestinationDirectory != right.Path.DestinationDirectory
            || left.Path.SourcePlaylistDirectory != right.Path.SourcePlaylistDirectory
            || left.Path.DestinationPlaylistBase != right.Path.DestinationPlaylistBase
            || left.Organizer != right.Organizer;
    }

    private MusicLoomSettings
        BuildSettingsFromForm()
    {
        double refreshSeconds =
            double.IsFinite(
                LyricsRefreshHoursBox.Value)
            && LyricsRefreshHoursBox.Value
                > 0
                ? LyricsRefreshHoursBox.Value
                : 86_400;

        double refreshHours =
            refreshSeconds / 3600.0;

        string language =
            (LanguageBox.SelectedItem
                as ComboBoxItem)
                ?.Tag?.ToString()
            ?? _uiLanguage;

        string storageMode =
            (LyricsStorageModeBox
                .SelectedItem
                as ComboBoxItem)
                ?.Tag?.ToString()
            ?? LyricsStorageModes.Embedded;

        string theme =
            (ThemeBox.SelectedItem
                as ComboBoxItem)
                ?.Tag?.ToString()
            ?? UiThemeModes.System;

        string longTextMode =
            (LongTextModeBox.SelectedItem
                as ComboBoxItem)
                ?.Tag?.ToString()
            ?? LongTextModes.AutoScroll;

        return new MusicLoomSettings
        {
            Verbose = true,
            Path = new PathSettings
            {
                SourceDirectory =
                    SourcePathBox.Text.Trim(),
                DestinationDirectory =
                    DestinationPathBox.Text
                        .Trim(),
                SourcePlaylistDirectory =
                    PlaylistSourcePathBox
                        .Text.Trim(),
                DestinationPlaylistBase =
                    string.IsNullOrWhiteSpace(
                        PlaylistBaseBox.Text)
                        ? "/storage/emulated/0/Music/"
                        : PlaylistBaseBox
                            .Text.Trim(),
                AutoScanOnStartup = AutoScanOnStartupToggle.IsOn,
            },
            Organizer =
                new OrganizerSettings
                {
                    Lyrics =
                        new LyricsSettings
                        {
                            Enabled = true,
                            OverrideExisting =
                                LyricsOverrideToggle
                                    .IsOn,
                            RefreshIntervalHours =
                                refreshHours,
                            StorageMode =
                                LyricsStorageModes
                                    .Normalize(
                                        storageMode),
                        },
                },
            Logging =
                _settings?.Logging
                ?? new LoggingSettings(),
            Ui = new UiSettings
            {
                Language =
                    NormalizeLanguage(
                        language),
                AcrylicEnabled =
                    AcrylicToggle.IsOn,
                AccentColor =
                    string.IsNullOrWhiteSpace(
                        _accentColor)
                        ? "system"
                        : _accentColor,
                Theme =
                    UiThemeModes.Normalize(
                        theme),
                AutoScrollLongTitles =
                    LongTextModes.Normalize(
                        longTextMode)
                    == LongTextModes.AutoScroll,
                LongTextMode =
                    LongTextModes.Normalize(
                        longTextMode),
                LyricsDisplayMode =
                    LyricsDisplayModes.Normalize(
                        _lyricsDisplayMode),
                LyricsEditorPath = LyricsEditorPathBox.Text.Trim(),
            },
        };
    }

    private void PopulateSettingsForm(
        MusicLoomSettings settings)
    {
        _loadingSettings = true;
        try
        {
            SourcePathBox.Text =
                settings.Path
                    .SourceDirectory;
            DestinationPathBox.Text =
                settings.Path
                    .DestinationDirectory;
            PlaylistSourcePathBox.Text =
                settings.Path
                    .SourcePlaylistDirectory;
            PlaylistBaseBox.Text =
                settings.Path
                    .DestinationPlaylistBase;
            AutoScanOnStartupToggle.IsOn =
                settings.Path.AutoScanOnStartup;
            LyricsOverrideToggle.IsOn =
                settings.Organizer.Lyrics
                    .OverrideExisting;

            LyricsRefreshHoursBox.Value =
                settings.Organizer.Lyrics
                        .RefreshIntervalHours
                    > 0
                        ? settings.Organizer
                            .Lyrics
                            .RefreshIntervalHours
                            * 3600.0
                        : 86_400;

            string storage =
                LyricsStorageModes.Normalize(
                    settings.Organizer.Lyrics
                        .StorageMode);
            LyricsStorageModeBox
                .SelectedIndex =
                    storage
                        == LyricsStorageModes
                            .Sidecar
                        ? 1
                        : 0;

            string language =
                NormalizeLanguage(
                    settings.Ui.Language);
            LanguageBox.SelectedIndex =
                language == "zh-CN"
                    ? 1
                    : 0;

            AcrylicToggle.IsOn =
                settings.Ui
                    .AcrylicEnabled;

            ThemeBox.SelectedIndex =
                UiThemeModes.Normalize(
                    settings.Ui.Theme) switch
                {
                    UiThemeModes.Light => 1,
                    UiThemeModes.Dark => 2,
                    _ => 0,
                };

            string longTextMode =
                LongTextModes.Normalize(
                    settings.Ui.LongTextMode,
                    settings.Ui.AutoScrollLongTitles);

            LongTextModeBox.SelectedIndex =
                longTextMode switch
                {
                    LongTextModes.Hover => 1,
                    LongTextModes.Wrap => 2,
                    _ => 0,
                };

            _lyricsDisplayMode =
                LyricsDisplayModes.Normalize(
                    settings.Ui.LyricsDisplayMode);
            LyricsEditorPathBox.Text = settings.Ui.LyricsEditorPath;

            _accentColor =
                string.IsNullOrWhiteSpace(
                    settings.Ui.AccentColor)
                    ? "system"
                    : settings.Ui.AccentColor;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private async void ChooseAccentButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        Color initial =
            AppearanceManager.TryParseColor(
                _accentColor,
                out Color custom)
                ? custom
                : Color.FromArgb(
                    255,
                    0,
                    120,
                    212);

        ColorPicker picker = new()
        {
            Color = initial,
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible =
                true,
            IsHexInputVisible = true,
            HorizontalAlignment =
                HorizontalAlignment.Stretch,
        };

        Button useColorButton = new()
        {
            Content =
                L(
                    "Use color",
                    "使用此颜色"),
            HorizontalAlignment =
                HorizontalAlignment.Right,
            Style =
                Application.Current.Resources[
                    "AccentButtonStyle"]
                as Style,
        };

        StackPanel content = new()
        {
            Spacing = 16,
        };
        content.Children.Add(
            picker);
        content.Children.Add(
            useColorButton);

        TaskCompletionSource<bool>
            completion =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

        useColorButton.Click +=
            (_, _) =>
            {
                completion.TrySetResult(
                    true);
                CloseMatchOverlay();
            };

        CloseMatchOverlay();
        ShowAppOverlay(
            L(
                "Accent color",
                "主题色"),
            content,
            maxWidth: 560,
            maxHeight: 620,
            onClosed: () =>
                completion.TrySetResult(
                    false));

        if (!await completion.Task)
        {
            return;
        }

        _accentColor =
            AppearanceManager.ToHex(
                picker.Color);

        if (_settings is not null)
        {
            ApplyAppearance(
                BuildSettingsFromForm());
        }

        ScheduleSettingsSave();
    }

    private void ResetAccentButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _accentColor = "system";

        if (_settings is not null)
        {
            ApplyAppearance(
                BuildSettingsFromForm());
        }

        ScheduleSettingsSave();
    }

    private void OpenSettingsButton_Click(
        object sender,
        RoutedEventArgs e) =>
        NavigateTo("settings");

    private void RefreshDatabaseSize()
    {
        DatabaseSizeText.Text =
            FormatBytes(
                LibraryDatabase
                    .GetStorageSizeBytes(
                        DatabasePath));
        DatabasePathText.Text =
            DatabasePath;
    }

    private async void ClearDatabaseButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        TextBlock message = new()
        {
            Text =
                L(
                    "This deletes the cached library, synchronization state and NetEase match cache. Settings in config.toml and your music files are kept.",
                    "这会删除音乐库缓存、同步状态和网易云匹配缓存；config.toml 设置和音乐文件会保留。"),
            TextWrapping =
                TextWrapping.WrapWholeWords,
        };

        Button confirmButton = new()
        {
            Content =
                L(
                    "Clear database",
                    "清空数据库"),
            HorizontalAlignment =
                HorizontalAlignment.Right,
        };
        ApplyDangerButtonStyle(
            confirmButton);

        StackPanel confirmationContent =
            new()
            {
                Spacing = 18,
            };
        confirmationContent.Children.Add(
            message);
        confirmationContent.Children.Add(
            confirmButton);

        TaskCompletionSource<bool>
            confirmation =
                new(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

        confirmButton.Click +=
            (_, _) =>
            {
                confirmation.TrySetResult(
                    true);
                CloseMatchOverlay();
            };

        CloseMatchOverlay();
        ShowAppOverlay(
            L(
                "Clear the entire local database?",
                "完全清空本地数据库？"),
            confirmationContent,
            maxWidth: 560,
            maxHeight: 300,
            onClosed: () =>
                confirmation.TrySetResult(
                    false));

        if (!await confirmation.Task)
        {
            return;
        }

        try
        {
            _settingsSaveCancellation
                ?.Cancel();

            Microsoft.Data.Sqlite
                .SqliteConnection
                .ClearAllPools();

            await LibraryDatabase.ClearAsync(
                DatabasePath);

            TryDeleteLegacyFile(
                LegacySnapshotPath);
            TryDeleteLegacyFile(
                LegacyStatePath);
            TryDeleteLegacyFile(
                LegacyStatePath + ".bak");

            _state =
                new StateDocument();
            _stateStore =
                new SqliteStateStore(
                    DatabasePath,
                    LegacyStatePath);
            _plan = null;
            _trackSyncStates.Clear();
            _liveNeteaseMatches.Clear();
            _manualPendingSources.Clear();
            _allLibraryItems.Clear();
            _playlists.Clear();

            LibraryList.ItemsSource = null;
            PlaylistsList.ItemsSource = null;
            PlaylistTracksList.ItemsSource = null;
            SyncButton.IsEnabled = false;
            HideDetailPane();
            ResetTrackDetails();
            UpdateLibraryEmptyState();
            RefreshDatabaseSize();

            ShowInfo(
                InfoBarSeverity.Success,
                L(
                    "Database cleared",
                    "数据库已清空"),
                L(
                    "MusicLoom's local cache was reset. Your config.toml settings were preserved.",
                    "MusicLoom 的本地缓存已重置；config.toml 设置已保留。"));
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not clear the local database.",
                    "无法清空本地数据库。"),
                exception);
        }
    }

    private void RefreshDiagnosticsConsole()
    {
        try
        {
            string text =
                File.Exists(
                    AppDiagnostics.LogPath)
                    ? File.ReadAllText(
                        AppDiagnostics.LogPath)
                    : string.Empty;

            const int MaxCharacters =
                120_000;

            if (text.Length
                > MaxCharacters)
            {
                text =
                    text[^MaxCharacters..];

                int newline =
                    text.IndexOf(
                        Environment.NewLine,
                        StringComparison.Ordinal);
                if (newline >= 0)
                {
                    text =
                        text[
                            (newline
                                + Environment
                                    .NewLine.Length)..];
                }
            }

            DiagnosticsConsole.Text =
                text;
            DiagnosticsConsole.SelectionStart =
                DiagnosticsConsole.Text.Length;
        }
        catch (Exception exception)
        {
            DiagnosticsConsole.Text =
                UserFacingErrors.Describe(
                    exception, IsChinese);
        }
    }

    private void AppDiagnostics_LineWritten(
        string line)
    {
        DispatcherQueue.TryEnqueue(
            () =>
                AppendDiagnosticLine(
                    line));
    }

    private void AppendDiagnosticLine(
        string line)
    {
        const int MaxCharacters =
            120_000;

        string current =
            DiagnosticsConsole.Text;

        string next =
            string.IsNullOrEmpty(current)
                ? line
                : current
                    + Environment.NewLine
                    + line;

        if (next.Length
            > MaxCharacters)
        {
            next =
                next[^MaxCharacters..];

            int newline =
                next.IndexOf(
                    Environment.NewLine,
                    StringComparison.Ordinal);
            if (newline >= 0)
            {
                next =
                    next[
                        (newline
                            + Environment
                                .NewLine.Length)..];
            }
        }

        DiagnosticsConsole.Text =
            next;
        DiagnosticsConsole.SelectionStart =
            next.Length;
    }

    private void OpenLogFolderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            AppDiagnostics.OpenLogDirectory();
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not open the log folder.",
                    "无法打开日志文件夹。"),
                exception);
        }
    }

    private void OpenLogFileButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            AppDiagnostics.OpenLogFile();
        }
        catch (Exception exception)
        {
            HandleUiException(
                L(
                    "Could not open the log file.",
                    "无法打开日志文件。"),
                exception);
        }
    }

    private void ClearLogButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        AppDiagnostics.ClearLog();
        DiagnosticsConsole.Text =
            string.Empty;

        ShowInfo(
            InfoBarSeverity.Success,
            L(
                "Log cleared",
                "日志已清空"),
            L(
                "The current diagnostic log was cleared.",
                "当前诊断日志已清空。"));
    }

    private void Navigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs
            args)
    {
        if (args.SelectedItemContainer
                ?.Tag
            is string tag)
        {
            ShowPage(tag);
        }
    }

    private void NavigateTo(
        string tag)
    {
        NavigationViewItem? target =
            Navigation.MenuItems
                .OfType<NavigationViewItem>()
                .FirstOrDefault(
                    item =>
                        string.Equals(
                            item.Tag?.ToString(),
                            tag,
                            StringComparison
                                .Ordinal));

        if (target is not null)
        {
            Navigation.SelectedItem =
                target;
        }

        ShowPage(tag);
    }

    private void ShowPage(
        string tag)
    {
        bool enteringPlaylists =
            tag == "playlists"
            && PlaylistsPage.Visibility != Visibility.Visible;

        LibraryPage.Visibility =
            tag == "library"
                ? Visibility.Visible
                : Visibility.Collapsed;
        PlaylistsPage.Visibility =
            tag == "playlists"
                ? Visibility.Visible
                : Visibility.Collapsed;
        SettingsPage.Visibility =
            tag == "settings"
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (enteringPlaylists)
        {
            RefreshPlaylistReadinessFromDisk();
        }
    }

    private void RefreshPlaylistReadinessFromDisk()
    {
        if (_settings is not MusicLoomSettings settings
            || _allLibraryItems.Count == 0)
        {
            return;
        }

        try
        {
            PopulatePlaylists(BuildPlaylists(settings));
        }
        catch (Exception exception)
        {
            AppDiagnostics.LogException(
                "Could not refresh playlist readiness.", exception);
        }
    }

    private void SetBusy(
        bool busy,
        string operationStatus)
    {
        _busy = busy;

        ScanButton.IsEnabled =
            !busy;
        AutoMatchButton.IsEnabled =
            !busy
            && _settings is MusicLoomSettings configured
            && !string.IsNullOrWhiteSpace(
                configured.Path.SourceDirectory)
            && !string.IsNullOrWhiteSpace(
                configured.Path.DestinationDirectory);
        SyncButton.IsEnabled =
            !busy
            && (_manualPendingSources.Count
                    > 0
                || _plan is
                    {
                        PendingCount: > 0,
                    });

        CancelButton.Visibility =
            busy
                ? Visibility.Visible
                : Visibility.Collapsed;
        CancelButton.IsEnabled =
            busy;
        PlaylistCancelButton.IsEnabled =
            busy;

        // Browsing/searching remains usable while a background sync runs.
        LibrarySearchBox.IsEnabled = true;
        LibraryFilterBox.IsEnabled = true;
        LibrarySortBox.IsEnabled = true;
        LibrarySortDirectionBox.IsEnabled = true;
        LibraryList.IsEnabled = true;
        PlaylistSearchBox.IsEnabled = true;
        PlaylistSortBox.IsEnabled = true;
        PlaylistSortDirectionBox.IsEnabled = true;
        PlaylistTracksList.IsEnabled = true;

        OpenMatchDialogButton.IsEnabled =
            !busy
            && _selectedLibraryItem
                is not null;
        UpdateBatchMatchActions();

        TrackSyncWarning? selectedWarning =
            _selectedLibraryItem is
                LibraryDisplayItem selected
                    ? GetStoredMatchWarning(
                        selected.Entry)
                    : null;

        ConfirmMatchButton.IsEnabled =
            !busy
            && selectedWarning
                is not null;

        bool selectedSuppressed =
            _selectedLibraryItem is
                LibraryDisplayItem selectedItem
            && _state is not null
            && _state.Tracks.TryGetValue(
                selectedItem.Entry.SourcePath,
                out TrackState? selectedState)
            && selectedState
                .NeteaseMatchSuppressed;

        SuppressMatchButton.IsEnabled =
            !busy
            && _selectedLibraryItem
                is not null
            && !selectedSuppressed;

        RefreshMatchedLyricsButton.IsEnabled =
            !busy
            && _selectedLibraryItem is
                LibraryDisplayItem selectedMatch
            && HasCachedMatch(
                selectedMatch.Entry.SourcePath);
        RenderCloudMatchPanels();

        LibraryProgressPanel.Visibility =
            busy
                ? Visibility.Visible
                : Visibility.Collapsed;
        PlaylistProgressPanel.Visibility =
            busy
                ? Visibility.Visible
                : Visibility.Collapsed;

        OperationProgressBar.Visibility =
            busy
                ? Visibility.Visible
                : Visibility.Collapsed;
        PlaylistOperationProgressBar.Visibility =
            busy
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (busy)
        {
            OperationProgressBar.IsIndeterminate =
                true;
            PlaylistOperationProgressBar.IsIndeterminate =
                true;
        }
        else
        {
            OperationProgressBar.IsIndeterminate =
                false;
            OperationProgressBar.Value = 0;
            PlaylistOperationProgressBar.IsIndeterminate =
                false;
            PlaylistOperationProgressBar.Value = 0;
        }

        SetOperationStatus(
            operationStatus);
    }

    private void SetOperationStatus(
        string status)
    {
        OperationStatusText.Text =
            status;
        PlaylistOperationStatusText.Text =
            status;
    }

    private void SetIndeterminateProgress()
    {
        LibraryProgressPanel.Visibility =
            Visibility.Visible;
        PlaylistProgressPanel.Visibility =
            Visibility.Visible;

        OperationProgressBar.Visibility =
            Visibility.Visible;
        PlaylistOperationProgressBar.Visibility =
            Visibility.Visible;

        OperationProgressBar.IsIndeterminate =
            true;
        PlaylistOperationProgressBar.IsIndeterminate =
            true;
    }

    private void SetProgress(
        int processed,
        int total)
    {
        LibraryProgressPanel.Visibility =
            Visibility.Visible;
        PlaylistProgressPanel.Visibility =
            Visibility.Visible;

        OperationProgressBar.Visibility =
            Visibility.Visible;
        PlaylistOperationProgressBar.Visibility =
            Visibility.Visible;

        if (total <= 0)
        {
            OperationProgressBar.IsIndeterminate =
                true;
            PlaylistOperationProgressBar.IsIndeterminate =
                true;
            return;
        }

        double value =
            Math.Clamp(
                processed * 100.0 / total,
                0,
                100);

        OperationProgressBar.IsIndeterminate =
            false;
        OperationProgressBar.Value =
            value;

        PlaylistOperationProgressBar.IsIndeterminate =
            false;
        PlaylistOperationProgressBar.Value =
            value;
    }

    private void ShowInfo(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        AppInfoBar.Severity = severity;
        AppInfoBar.Title = title;
        AppInfoBar.Message = message;
        AppInfoBar.IsOpen = true;
    }

    private void AddActivity(
        string category,
        string message)
    {
        AppDiagnostics.Log(
            $"{category} · {message}");
    }

    private void HandleUiException(
        string context,
        Exception exception)
    {
        string message =
            UserFacingErrors.Describe(
                exception, IsChinese);

        AddActivity(
            L("Error", "错误"),
            message);

        AppDiagnostics.LogException(
            context,
            exception);

        ShowInfo(
            InfoBarSeverity.Error,
            context,
            message);
    }

    private string StatusLabel(
        string kind) =>
        kind switch
        {
            "synced" =>
                L(
                    "Synced",
                    "已同步"),
            "error" =>
                L(
                    "Error",
                    "错误"),
            "warning" =>
                L(
                    "Warning",
                    "警告"),
            "syncing" =>
                L(
                    "Syncing",
                    "同步中"),
            _ =>
                L(
                    "Pending",
                    "待同步"),
        };

    private string FriendlyStage(
        string stage) =>
        stage switch
        {
            "state" =>
                L(
                    "Preparing",
                    "准备中"),
            "inspect-lyrics" =>
                L(
                    "Checking lyrics",
                    "检查歌词"),
            "recognize" =>
                L(
                    "Recognizing",
                    "识别歌曲"),
            "match" =>
                L(
                    "Checking match",
                    "核对匹配"),
            "fetch-lyrics" =>
                L(
                    "Fetching lyrics",
                    "获取歌词"),
            "write-lyrics" =>
                L(
                    "Writing lyrics",
                    "写入歌词"),
            "copy-audio" =>
                L(
                    "Copying audio",
                    "复制音频"),
            "copy-cover" =>
                L(
                    "Copying cover",
                    "复制封面"),
            "delete-cover" =>
                L(
                    "Removing stale cover",
                    "清理错误封面"),
            "write-playlist" =>
                L(
                    "Writing playlist",
                    "写入播放列表"),
            "write-local-lyrics" =>
                L(
                    "Writing local lyrics",
                    "写入本地歌词"),
            "cleanup-old-destination" =>
                L(
                    "Cleaning old destination",
                    "清理旧目标路径"),
            "complete" =>
                L(
                    "Complete",
                    "完成"),
            "failed" =>
                L(
                    "Failed",
                    "失败"),
            _ => stage,
        };

    private void ApplyLanguage()
    {
        LibraryNav.Content =
            L("Library", "音乐库");
        PlaylistsNav.Content =
            L("Playlists", "音乐列表");
        SettingsNav.Content =
            L("Settings", "设置");

        LibraryTitleText.Text =
            L(
                "Music library",
                "音乐库");
        LibrarySubtitleText.Text =
            L(
                "Browse, inspect and sync your local collection.",
                "浏览、检查并同步你的本地音乐收藏。");

        ScanButtonText.Text =
            L(
                "Refresh library",
                "刷新音乐库");
        AutoMatchButtonText.Text = L("Auto-match", "自动匹配");
        AutoMatchNeteaseItem.Text = L(
            "NetEase Music (audio fingerprint)",
            "网易云音乐（音频指纹）");
        AutoMatchQqItem.Text = L(
            "QQ Music (metadata search)", "QQ 音乐（元数据搜索）");
        AutoMatchKugouItem.Text = L(
            "KuGou Music (metadata search)", "酷狗音乐（元数据搜索）");
        SyncButtonText.Text =
            L(
                "Sync pending",
                "同步待处理");
        ConfirmSelectedMatchesText.Text =
            L(
                "Confirm selected matches",
                "批量确认匹配");
        ClearSelectedMatchesText.Text =
            L(
                "Clear selected matches",
                "批量置空匹配");
        CancelButtonText.Text =
            L("Cancel", "取消");
        PlaylistCancelButtonText.Text =
            L("Cancel", "取消");

        if (OperationStatusText.Text
            is "Ready."
            or "就绪。")
        {
            OperationStatusText.Text =
                L("Ready.", "就绪。");
        }

        LibrarySearchBox.PlaceholderText =
            L(
                "Search title, artist, album or path",
                "搜索歌曲、艺术家、专辑或路径");
        LibraryFilterAllItem.Content =
            L(
                "All statuses",
                "全部状态");
        LibraryFilterPendingItem.Content =
            L(
                "Pending",
                "待同步");
        LibraryFilterSyncedItem.Content =
            L(
                "Synced",
                "已同步");
        LibraryFilterWarningsItem.Content =
            L(
                "Warnings",
                "警告");
        LibraryFilterErrorsItem.Content =
            L(
                "Errors",
                "错误");
        LocalizeLibraryFacetBoxes();
        SortTitleItem.Content =
            L(
                "Sort by title",
                "按歌曲名排序");
        SortAlbumItem.Content =
            L(
                "Sort by album",
                "按专辑排序");
        SortArtistItem.Content =
            L(
                "Sort by artist",
                "按艺术家排序");
        LibrarySortAscendingItem.Content =
            L("Ascending", "顺序");
        LibrarySortDescendingItem.Content =
            L("Descending", "倒序");

        ToolTipService.SetToolTip(
            DetailPlayButton,
            L(
                "Play or pause this track",
                "播放或暂停此歌曲"));
        ToolTipService.SetToolTip(
            CloseDetailButton,
            L(
                "Close details",
                "关闭详情"));
        ToolTipService.SetToolTip(
            CoverButton,
            L(
                "Open artwork",
                "查看封面"));
        ToolTipService.SetToolTip(
            PendingSummaryIcon,
            L(
                "Pending",
                "待同步"));
        ToolTipService.SetToolTip(
            WarningSummaryIcon,
            L(
                "Warnings",
                "警告"));
        ToolTipService.SetToolTip(
            ErrorSummaryIcon,
            L(
                "Errors",
                "错误"));
        LyricsTab.Header =
            L("Lyrics", "歌词");
        MatchTab.Header = L("NetEase match", "网易云匹配");
        QqMatchTab.Header = L("QQ Music match", "QQ音乐匹配");
        KugouMatchTab.Header = L("KuGou Music match", "酷狗音乐匹配");
        NeteaseMatchPanel.Localize(L, MatchProviderKind.Netease);
        QqMatchPanel.Localize(L, MatchProviderKind.QqMusic);
        KugouMatchPanel.Localize(L, MatchProviderKind.KugouMusic);
        MatchDialogTitleText.Text =
            L(
                "Find NetEase match",
                "查找网易云匹配");
        ToolTipService.SetToolTip(
            MatchDialogCloseButton,
            L("Close", "关闭"));
        SyncTab.Header =
            L("Sync", "同步");
        TrackSyncLogHeadingText.Text =
            L(
                "Latest sync log",
                "最新同步日志");
        PropertiesTab.Header =
            L(
                "Properties",
                "属性");
        SourcePathHeadingText.Text =
            L("Source", "源文件");
        DestinationPathHeadingText.Text =
            L(
                "Destination",
                "目标文件");
        FormatHeadingText.Text =
            L("Format", "格式");
        DurationHeadingText.Text =
            L("Duration", "时长");
        BitrateHeadingText.Text =
            L("Bitrate", "比特率");
        SampleRateHeadingText.Text =
            L("Sample rate", "采样率");
        BitDepthHeadingText.Text =
            L("Bit depth", "位深");
        ChannelsHeadingText.Text =
            L("Channels", "声道");
        BitrateModeHeadingText.Text =
            L("Encoding", "编码模式");
        SizeHeadingText.Text =
            L(
                "Source size",
                "源文件大小");
        ModifiedHeadingText.Text =
            L(
                "Modified",
                "修改时间");
        CreatedHeadingText.Text =
            L(
                "Created",
                "创建时间");
        DestinationSizeHeadingText.Text =
            L(
                "Destination size",
                "目标文件大小");
        DestinationModifiedHeadingText.Text =
            L(
                "Destination modified",
                "目标修改时间");

        ContextPlayItem.Text =
            L("Play", "播放");
        ContextOpenSourceItem.Text =
            L(
                "Open source folder",
                "打开源文件位置");
        ContextOpenDestinationItem.Text =
            L(
                "Open destination folder",
                "打开目标文件位置");
        ContextCopySourceItem.Text =
            L(
                "Copy source path",
                "复制源文件路径");
        ContextCopyDestinationItem.Text =
            L(
                "Copy destination path",
                "复制目标文件路径");
        ContextFindMatchItem.Text =
            L(
                "Find NetEase match…",
                "查找网易云匹配……");
        ContextConfirmMatchItem.Text =
            L(
                "Confirm NetEase match",
                "确认网易云匹配");
        ContextClearMatchItem.Text =
            L(
                "Clear NetEase match",
                "置空网易云匹配");
        ContextConfirmSelectedMatchesItem.Text =
            L(
                "Confirm selected matches",
                "批量确认所选匹配");
        ContextClearSelectedMatchesItem.Text =
            L(
                "Clear selected matches",
                "批量置空所选匹配");
        ContextUpdateSelectedLyricsItem.Text =
            L(
                "Update selected lyrics",
                "批量更新所选歌词");

        PlaylistsTitleText.Text =
            L(
                "Playlists",
                "音乐列表");
        PlaylistsSubtitleText.Text =
            L(
                "Browse your MusicBee playlists.",
                "浏览你的 MusicBee 播放列表。");

        if (PlaylistsList.SelectedItem is null)
        {
            PlaylistTitleText.Text =
                L(
                    "Select a playlist",
                    "选择一个音乐列表");
        }

        SettingsTitleText.Text =
            L(
                "Settings",
                "设置");
        SettingsSubtitleText.Text =
            L(
                "Changes are saved automatically.",
                "更改会自动保存。");
        LibrarySettingsHeadingText.Text =
            L(
                "Library",
                "音乐库");
        LyricsSettingsHeadingText.Text =
            L(
                "Lyrics",
                "歌词");
        AppearanceSettingsHeadingText.Text =
            L(
                "Appearance",
                "外观");

        SourceLibrarySettingTitle.Text =
            L(
                "Source library",
                "源音乐库");
        DestinationLibrarySettingTitle.Text =
            L(
                "Destination library",
                "目标音乐库");
        PlaylistFolderSettingTitle.Text =
            L(
                "MusicBee playlist folder",
                "MusicBee 播放列表目录");
        PlaylistBaseSettingTitle.Text =
            L(
                "Playlist path on phone",
                "手机上的播放列表路径");
        DatabaseSettingTitle.Text =
            L(
                "LOCAL DATABASE",
                "本地数据库");
        ClearDatabaseButton.Content =
            L(
                "Clear entire database",
                "完全清空数据库");
        DatabasePathText.Text =
            DatabasePath;
        AutoScanSettingTitle.Text =
            L("Scan on startup", "启动时自动扫描");
        AutoScanSettingDescription.Text =
            L(
                "Refresh source and destination libraries once at startup.",
                "启动时自动扫描一次源音乐库与目标音乐库。");
        OverwriteLyricsSettingTitle.Text =
            L(
                "Overwrite existing lyrics",
                "覆盖已有歌词");
        OverwriteLyricsSettingDescription.Text =
            L(
                "Replace lyrics already present in the destination.",
                "替换目标文件中已经存在的歌词。");
        LyricsStorageSettingTitle.Text =
            L(
                "Lyrics storage",
                "歌词保存方式");
        LyricsStorageSettingDescription.Text =
            L(
                "Choose embedded tags or sidecar .lrc files.",
                "选择嵌入标签或独立 .lrc 文件。");
        LyricsStorageEmbeddedItem.Content =
            L(
                "Embedded in audio file",
                "嵌入音频文件");
        LyricsStorageSidecarItem.Content =
            L(
                "Sidecar .lrc file",
                "独立 .lrc 文件");
        LyricsCacheSettingTitle.Text =
            L(
                "Lyrics cache lifetime (seconds)",
                "歌词缓存有效期（秒）");
        LyricsCacheSettingDescription.Text =
            L(
                "How many seconds a result stays fresh before MusicLoom checks again.",
                "结果在 MusicLoom 再次检查前保持有效的秒数。");

        ThemeSettingTitle.Text =
            L(
                "Theme",
                "主题");
        ThemeSystemItem.Content =
            L(
                "Use system setting",
                "跟随系统");
        ThemeLightItem.Content =
            L(
                "Light",
                "浅色");
        ThemeDarkItem.Content =
            L(
                "Dark",
                "深色");
        LongTextSettingTitle.Text =
            L(
                "Long text display",
                "过长文本显示");
        LongTextAutoScrollItem.Content =
            L(
                "Auto-scroll",
                "自动滚动");
        LongTextHoverItem.Content =
            L(
                "Show on hover",
                "悬停显示");
        LongTextWrapItem.Content =
            L(
                "Wrap",
                "换行显示");

        LyricsOriginalOnlyItem.Content =
            L(
                "Original only",
                "仅原文");
        LyricsPreferTranslationItem.Content =
            L(
                "Prefer translation",
                "优先翻译");
        LyricsPreferOriginalItem.Content =
            L(
                "Prefer original",
                "优先原文");
        CopyAllLyricsButtonText.Text = L("Copy", "复制");
        ClearLyricsButtonText.Text = L("Clear", "清空");
        AdjustLyricsButtonText.Text = L("Adjust", "调整");
        EditLyricsButtonText.Text = L("Edit", "编辑");
        ToolTipService.SetToolTip(
            ImmersiveOpenButton,
            L("Open immersive player", "打开沉浸式播放器"));
        ImmersiveView.Localize(L);
         ToolTipService.SetToolTip(ClearLyricsButton,
            L("Clear all lyrics", "清空全部歌词"));
        ToolTipService.SetToolTip(AdjustLyricsButton,
            L("Set lyric offset", "设置歌词偏移量"));
        ToolTipService.SetToolTip(LibrarySplitter,
            L("Drag to resize panels", "拖动以调整面板宽度"));
        ToolTipService.SetToolTip(EditLyricsButton,
            L("Edit lyrics", "编辑歌词"));
        LyricsEditorSettingTitle.Text = L("Lyrics editor", "歌词编辑器");
        LyricsEditorSettingDescription.Text = L(
            "Use default editor or choose an application.",
            "使用系统默认编辑器或指定程序。");
        LyricsEditorPathBox.PlaceholderText = L("System default", "系统默认");
        BrowseLyricsEditorButton.Content = L("Browse…", "浏览……");
        ResetLyricsEditorButton.Content = L("Reset", "重置");
        CopyAllLyricsTextItem.Text =
            L(
                "Copy as text",
                "复制为文本");
        CopyAllLyricsLrcItem.Text =
            L(
                "Copy as LRC",
                "复制为 LRC");
        ToolTipService.SetToolTip(
            CopyAllLyricsButton,
            L(
                "Copy lyrics",
                "复制歌词"));

        AcrylicSettingTitle.Text =
            L(
                "Acrylic backdrop",
                "亚克力背景");
        AcrylicSettingDescription.Text =
            L(
                "Use desktop acrylic instead of the default Mica backdrop.",
                "使用桌面亚克力效果代替默认的 Mica 背景。");
        AccentSettingTitle.Text =
            L(
                "Accent color",
                "主题色");
        AccentSettingDescription.Text =
            L(
                "Customize MusicLoom's primary accent or follow Windows.",
                "自定义 MusicLoom 的主题色，或跟随 Windows。");
        ChooseAccentButton.Content =
            L("Choose…", "选择……");
        ResetAccentButton.Content =
            L(
                "Use system",
                "跟随系统");
        LanguageSettingTitle.Text =
            L(
                "Interface language",
                "界面语言");

        OpenSettingsButton.Content =
            L(
                "Open settings",
                "打开设置");

        CoverPlaceholderText.Text =
            L(
                "No artwork",
                "无封面");

        PlaylistSearchBox.PlaceholderText =
            L(
                "Search this playlist",
                "搜索当前音乐列表");
        PlaylistSortTitleItem.Content =
            L(
                "Sort by title",
                "按歌曲名排序");
        PlaylistSortAlbumItem.Content =
            L(
                "Sort by album",
                "按专辑排序");
        PlaylistSortArtistItem.Content =
            L(
                "Sort by artist",
                "按艺术家排序");
        PlaylistSortAscendingItem.Content =
            L("Ascending", "顺序");
        PlaylistSortDescendingItem.Content =
            L("Descending", "倒序");
        PlaylistPlayItem.Text =
            L("Play", "播放");
        ToolTipService.SetToolTip(
            PlaylistHeaderPlayButton,
            L(
                "Play playlist",
                "播放音乐列表"));
        PlaylistShowInLibraryItem.Text =
            L(
                "Show in library",
                "在音乐库中显示");

        BrowseSourceButton.Content =
            L("Browse", "浏览");
        OpenSourceButton.Content =
            L("Open", "打开");
        BrowseDestinationButton.Content =
            L("Browse", "浏览");
        OpenDestinationSettingButton.Content =
            L("Open", "打开");
        BrowsePlaylistButton.Content =
            L("Browse", "浏览");
        OpenPlaylistButton.Content =
            L("Open", "打开");

        LogsSettingsHeadingText.Text =
            L(
                "Logs",
                "日志");
        DiagnosticsLogSettingTitle.Text =
            L(
                "Diagnostic log",
                "诊断日志");
        DiagnosticsConsoleTitle.Text =
            L(
                "Live diagnostic console",
                "实时诊断控制台");
        OpenLogFileButton.Content =
            L(
                "Open log",
                "打开日志");
        OpenLogFolderButton.Content =
            L(
                "Open folder",
                "打开目录");
        ClearLogButton.Content =
            L(
                "Clear",
                "清空");
        DiagnosticsLogPathText.Text =
            AppDiagnostics.LogPath;

        if (_mediaPlayer.Source is null)
        {
            PlayerTitleText.Text =
                L(
                    "Nothing playing",
                    "未播放歌曲");
            PlayerMetadataText.Text =
                string.Empty;
        }

        ToolTipService.SetToolTip(
            PlayerArtworkButton,
            L(
                "Double-click to locate the playing track",
                "双击定位到正在播放的歌曲"));
        ToolTipService.SetToolTip(
            PlayerPreviousButton,
            L("Previous", "上一首"));
        ToolTipService.SetToolTip(
            PlayerNextButton,
            L("Next", "下一首"));

        RefreshLocalizedComboBoxSelections();

        RenderLyrics();
        RefreshCurrentMatchCard(
            _selectedLibraryItem);
        RenderCloudMatchPanels();
        RefreshTrackSyncTab(
            _selectedLibraryItem);
        ConfigureAuxiliaryLongText();
        ApplyLibraryFilter();
        ApplyPlaylistTrackFilter();
        UpdateLibraryEmptyState();
    }

    private void RefreshLocalizedComboBoxSelections()
    {
        ComboBox[] boxes =
        [
            LibraryFilterBox,
            LibraryLyricsFacetBox,
            LibraryNeteaseFacetBox,
            LibraryQqFacetBox,
            LibraryKugouFacetBox,
            LibrarySortBox,
            LibrarySortDirectionBox,
            LyricsModeBox,
            PlaylistSortBox,
            PlaylistSortDirectionBox,
            LyricsStorageModeBox,
            ThemeBox,
            LongTextModeBox,
        ];

        _refreshingLocalizedChoices =
            true;

        try
        {
            foreach (ComboBox box in boxes)
            {
                int index =
                    box.SelectedIndex;

                if (index < 0)
                {
                    continue;
                }

                box.SelectedIndex = -1;
                box.SelectedIndex = index;
            }
        }
        finally
        {
            _refreshingLocalizedChoices =
                false;
        }
    }

    private static string NormalizeLanguage(
        string? language)
    {
        if (string.Equals(
                language,
                "zh-CN",
                StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }

        if (string.Equals(
                language,
                "en-US",
                StringComparison.OrdinalIgnoreCase))
        {
            return "en-US";
        }

        return DetectSystemLanguage();
    }

    private static string DetectSystemLanguage()
    {
        string language =
            CultureInfo.CurrentUICulture.Name;

        return language.StartsWith(
            "zh",
            StringComparison.OrdinalIgnoreCase)
                ? "zh-CN"
                : "en-US";
    }

    private static string? FormatArtistsForDisplay(
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(
            "; ",
            value
                .Split(
                    ';',
                    StringSplitOptions
                        .RemoveEmptyEntries
                    | StringSplitOptions
                        .TrimEntries));
    }

    private static string MakeRelative(
        string root,
        string path)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return path;
        }

        try
        {
            return Path.GetRelativePath(
                Path.GetFullPath(root),
                Path.GetFullPath(path));
        }
        catch
        {
            return path;
        }
    }

    private static string FormatDuration(
        double milliseconds)
    {
        if (!double.IsFinite(milliseconds)
            || milliseconds <= 0)
        {
            return "—";
        }

        TimeSpan duration =
            TimeSpan.FromMilliseconds(
                milliseconds);

        return duration.TotalHours >= 1
            ? duration.ToString(
                @"h\:mm\:ss",
                CultureInfo.InvariantCulture)
            : duration.ToString(
                @"m\:ss",
                CultureInfo.InvariantCulture);
    }

    private static string FormatBytes(
        long bytes)
    {
        string[] suffixes =
            ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int index = 0;

        while (value >= 1024
            && index
                < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {suffixes[index]}";
    }

    private sealed record LiveNeteaseMatch(
        long TrackId,
        string? Name,
        string? Artist,
        string? Album,
        string? CoverUrl,
        TrackSyncWarning? Warning);

    private sealed record TrackSyncViewState(
        string Status,
        string Detail,
        DateTimeOffset UpdatedAt,
        bool IsError,
        string Kind);

    private sealed record TrackDetailPayload(
        byte[]? Artwork,
        string? Lyrics,
        string LyricsSource,
        string SourcePath,
        string? DestinationPath,
        string Format,
        string Duration,
        string Bitrate,
        string SampleRate,
        string BitDepth,
        string Channels,
        string BitrateMode,
        string SourceSize,
        string SourceModified,
        string SourceCreated,
        string DestinationSize,
        string DestinationModified);
}
