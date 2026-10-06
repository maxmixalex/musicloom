using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MusicLoom.Core.Configuration;
using Windows.UI;

namespace MusicLoom.App;

internal static class AppearanceManager
{
    public static void Apply(
        Window window,
        UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);

        window.SystemBackdrop =
            settings.AcrylicEnabled
                ? new DesktopAcrylicBackdrop()
                : new MicaBackdrop();

        if (TryParseColor(
                settings.AccentColor,
                out Color accent))
        {
            ResourceDictionary resources =
                Application.Current.Resources;

            resources["SystemAccentColor"] = accent;
            resources["AccentFillColorDefaultBrush"] =
                new SolidColorBrush(accent);
            resources["AccentFillColorSecondaryBrush"] =
                new SolidColorBrush(WithAlpha(accent, 230));
            resources["AccentFillColorTertiaryBrush"] =
                new SolidColorBrush(WithAlpha(accent, 205));
        }
    }

    public static bool TryParseColor(
        string? value,
        out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value)
            || string.Equals(
                value,
                "system",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string text = value.Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length != 6
            || !uint.TryParse(
                text,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out uint rgb))
        {
            return false;
        }

        color = Color.FromArgb(
            255,
            (byte)(rgb >> 16),
            (byte)(rgb >> 8),
            (byte)rgb);
        return true;
    }

    public static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color WithAlpha(
        Color color,
        byte alpha) =>
        Color.FromArgb(
            alpha,
            color.R,
            color.G,
            color.B);
}
