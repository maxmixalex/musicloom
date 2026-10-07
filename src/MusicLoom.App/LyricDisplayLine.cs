using Microsoft.UI.Xaml;
using MusicLoom.Core.Configuration;

namespace MusicLoom.App;

internal sealed record LyricDisplayLine(
    string Original,
    string? Translation,
    string DisplayMode,
    TimeSpan? Timestamp = null)
{
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
}
