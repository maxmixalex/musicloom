using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MusicLoom.Core.Configuration;
using Windows.UI;

namespace MusicLoom.App;

internal static class AppearanceManager
{
    private static bool _capturedDefaults;
    private static object? _systemAccentColor;
    private static object? _accentDefaultBrush;
    private static object? _accentSecondaryBrush;
    private static object? _accentTertiaryBrush;

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

        ResourceDictionary resources =
            Application.Current.Resources;

        CaptureDefaults(resources);

        if (TryParseColor(
                settings.AccentColor,
                out Color accent))
        {
            resources["SystemAccentColor"] =
                accent;
            resources["AccentFillColorDefaultBrush"] =
                new SolidColorBrush(accent);
            resources["AccentFillColorSecondaryBrush"] =
                new SolidColorBrush(
                    WithAlpha(accent, 230));
            resources["AccentFillColorTertiaryBrush"] =
                new SolidColorBrush(
                    WithAlpha(accent, 205));
        }
        else
        {
            Restore(
                resources,
                "SystemAccentColor",
                _systemAccentColor);
            Restore(
                resources,
                "AccentFillColorDefaultBrush",
                _accentDefaultBrush);
            Restore(
                resources,
                "AccentFillColorSecondaryBrush",
                _accentSecondaryBrush);
            Restore(
                resources,
                "AccentFillColorTertiaryBrush",
                _accentTertiaryBrush);
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

    private static void CaptureDefaults(
        ResourceDictionary resources)
    {
        if (_capturedDefaults)
        {
            return;
        }

        resources.TryGetValue(
            "SystemAccentColor",
            out _systemAccentColor);
        resources.TryGetValue(
            "AccentFillColorDefaultBrush",
            out _accentDefaultBrush);
        resources.TryGetValue(
            "AccentFillColorSecondaryBrush",
            out _accentSecondaryBrush);
        resources.TryGetValue(
            "AccentFillColorTertiaryBrush",
            out _accentTertiaryBrush);

        _capturedDefaults = true;
    }

    private static void Restore(
        ResourceDictionary resources,
        string key,
        object? value)
    {
        if (value is not null)
        {
            resources[key] = value;
        }
    }

    private static Color WithAlpha(
        Color color,
        byte alpha) =>
        Color.FromArgb(
            alpha,
            color.R,
            color.G,
            color.B);
}
