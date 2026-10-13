using System.Globalization;
using Microsoft.UI.Xaml.Data;

namespace MusicLoom.App;

/// <summary>
/// Player progress sliders store seconds but must show readable playback
/// timestamps instead of exposing raw numeric Slider values in the tooltip.
/// </summary>
public sealed class TimeSliderValueConverter : IValueConverter
{
    public object Convert(
        object value, Type targetType, object parameter, string language)
    {
        if (value is not double seconds
            || !double.IsFinite(seconds) || seconds < 0)
        {
            return "0:00";
        }

        TimeSpan time = TimeSpan.FromSeconds(Math.Floor(seconds));
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : $"{(long)time.TotalMinutes}:{time.Seconds:00}";
    }

    public object ConvertBack(
        object value, Type targetType, object parameter, string language)
    {
        if (value is not string text)
        {
            return 0d;
        }

        string[] pieces = text.Trim().Split(':');
        if (pieces.Length is < 2 or > 3)
        {
            return 0d;
        }

        double seconds = 0;
        foreach (string part in pieces)
        {
            if (!int.TryParse(part, NumberStyles.None,
                CultureInfo.InvariantCulture, out int section)
                || section < 0)
            {
                return 0d;
            }
            seconds = seconds * 60 + section;
        }
        return seconds;
    }
}
