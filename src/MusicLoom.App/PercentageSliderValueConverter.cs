using Microsoft.UI.Xaml.Data;

namespace MusicLoom.App;

public sealed class PercentageSliderValueConverter :
    IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        string language)
    {
        if (value is double number
            && double.IsFinite(number))
        {
            return FormattableString.Invariant(
                $"{number:0}%");
        }

        return "0%";
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        string language)
    {
        if (value is string text
            && double.TryParse(
                text.Trim().TrimEnd('%'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double number))
        {
            return number;
        }

        return 0d;
    }
}
