using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Lyrics;

namespace MusicLoom.App;

internal sealed class LyricDisplayLine :
    INotifyPropertyChanged
{
    private bool _isActive;
    private TimeSpan _playbackPosition;

    public LyricDisplayLine(
        string original,
        string? translation,
        string displayMode,
        TimeSpan? timestamp = null,
        IReadOnlyList<LrcWord>? originalWords = null,
        IReadOnlyList<LrcWord>? translationWords = null)
    {
        Original = original;
        Translation = translation;
        DisplayMode = displayMode;
        Timestamp = timestamp;
        OriginalWords = originalWords;
        TranslationWords = translationWords;
    }

    public string Original { get; }
    public string? Translation { get; }
    public string DisplayMode { get; }
    public TimeSpan? Timestamp { get; }

    /// <summary>
    /// Verified original and translated QRC word timelines are independent.
    /// Switching the display language only swaps which one is primary.
    /// </summary>
    public IReadOnlyList<LrcWord>? OriginalWords { get; }
    public IReadOnlyList<LrcWord>? TranslationWords { get; }

    private bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);

    public IReadOnlyList<LrcWord>? PrimaryWords =>
        LyricsDisplayModes.Normalize(DisplayMode)
            == LyricsDisplayModes.PreferTranslation && HasTranslation
                ? TranslationWords : OriginalWords;

    public IReadOnlyList<LrcWord>? SecondaryWords =>
        LyricsDisplayModes.Normalize(DisplayMode) switch
        {
            LyricsDisplayModes.PreferTranslation when HasTranslation =>
                OriginalWords,
            LyricsDisplayModes.PreferOriginal when HasTranslation =>
                TranslationWords,
            _ => null,
        };

    /// <summary>
    /// Position provided by the shared MediaPlayer tick (not a second
    /// clock). Seeking backward immediately resets every word fraction.
    /// </summary>
    public TimeSpan PlaybackPosition
    {
        get => _playbackPosition;
        set
        {
            if (_playbackPosition == value)
            {
                return;
            }
            _playbackPosition = value;
            if (IsActive
                && (PrimaryWords is { Count: > 0 }
                    || SecondaryWords is { Count: > 0 }))
            {
                OnPropertyChanged();
            }
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            OnPropertyChanged();
            OnPropertyChanged(
                nameof(PrimaryFontSize));
            OnPropertyChanged(
                nameof(SecondaryFontSize));
            OnPropertyChanged(nameof(ImmersiveOpacity));
            OnPropertyChanged(nameof(ImmersiveFontSize));
        }
    }

    public double ImmersiveOpacity => IsActive ? 1.0 : 0.48;
    public double ImmersiveFontSize => IsActive ? 28.0 : 22.0;

    public double PrimaryFontSize =>
        IsActive ? 20 : 16;

    public double SecondaryFontSize =>
        IsActive ? 15 : 13;

    public string TimestampText
    {
        get
        {
            if (Timestamp is not TimeSpan value)
            {
                return string.Empty;
            }

            int totalMinutes =
                (int)Math.Floor(
                    value.TotalMinutes);

            return FormattableString.Invariant(
                $"{totalMinutes:00}:{value.Seconds:00}");
        }
    }

    public string TimestampLrcTag
    {
        get
        {
            if (Timestamp is not TimeSpan value)
            {
                return string.Empty;
            }

            long totalMilliseconds =
                (long)Math.Max(
                    0,
                    value.TotalMilliseconds);
            long minutes =
                totalMilliseconds / 60_000;
            long seconds =
                totalMilliseconds / 1_000 % 60;
            long milliseconds =
                totalMilliseconds % 1_000;

            return FormattableString.Invariant(
                $"[{minutes:00}:{seconds:00}.{milliseconds:000}]");
        }
    }

    public string PrimaryText =>
        LyricsDisplayModes.Normalize(
            DisplayMode)
        == LyricsDisplayModes.PreferTranslation
        && !string.IsNullOrWhiteSpace(
            Translation)
            ? Translation!
            : Original;

    public string? SecondaryText =>
        LyricsDisplayModes.Normalize(
            DisplayMode) switch
        {
            LyricsDisplayModes.OriginalOnly =>
                null,
            LyricsDisplayModes.PreferTranslation
                when !string.IsNullOrWhiteSpace(
                    Translation) =>
                Original,
            LyricsDisplayModes.PreferOriginal
                when !string.IsNullOrWhiteSpace(
                    Translation) =>
                Translation,
            _ => null,
        };

    public Visibility SecondaryVisibility =>
        string.IsNullOrWhiteSpace(
            SecondaryText)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public event PropertyChangedEventHandler?
        PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(
                propertyName));
}
