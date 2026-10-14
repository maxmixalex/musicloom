using Microsoft.UI.Xaml.Media;
using Windows.Media.Playback;

namespace MusicLoom.App;

public sealed partial class MainWindow
{
    private bool _karaokeFrameRenderingSubscribed;

    /// <summary>
    /// Word fills update on the compositor's frame cadence for smooth
    /// karaoke motion, but derive their progress from MediaPlayer.Position.
    /// No second clock is created, so pause/resume and seeking remain exact.
    /// Subscribe only when timed lyrics are actually being played.
    /// </summary>
    private void RefreshKaraokeFrameRendering()
    {
        bool needsFrames = _activePlaybackLyric is
            { IsActive: true } line
            && (line.PrimaryWords is { Count: > 0 }
                || line.SecondaryWords is { Count: > 0 })
            && _mediaPlayer.Source is not null
            && _mediaPlayer.PlaybackSession.PlaybackState
                == MediaPlaybackState.Playing
            && !_suppressPlaybackLyricFollow
            && !_splitDragging;

        if (needsFrames == _karaokeFrameRenderingSubscribed)
        {
            return;
        }

        if (needsFrames)
        {
            CompositionTarget.Rendering += KaraokeCompositionFrame;
        }
        else
        {
            CompositionTarget.Rendering -= KaraokeCompositionFrame;
        }
        _karaokeFrameRenderingSubscribed = needsFrames;
    }

    private void KaraokeCompositionFrame(object? sender, object? args)
    {
        if (_activePlaybackLyric is { IsActive: true } line
            && !_suppressPlaybackLyricFollow
            && !_splitDragging)
        {
            line.PlaybackPosition = _mediaPlayer.PlaybackSession.Position;
        }
    }

    private void StopKaraokeFrameRendering()
    {
        if (_karaokeFrameRenderingSubscribed)
        {
            CompositionTarget.Rendering -= KaraokeCompositionFrame;
            _karaokeFrameRenderingSubscribed = false;
        }
    }
}
