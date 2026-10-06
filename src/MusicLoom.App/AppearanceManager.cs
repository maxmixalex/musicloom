using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MusicLoom.Core.Configuration;
using Windows.UI;

namespace MusicLoom.App;

internal static class AppearanceManager
{
    private static bool _capturedDefaults;
    private static readonly Dictionary<string, object?>
        Defaults = new(StringComparer.Ordinal);

    private static readonly string[] AccentKeys =
    [
        "SystemAccentColor",
        "SystemAccentColorLight1",
        "SystemAccentColorLight2",
        "SystemAccentColorLight3",
        "SystemAccentColorDark1",
        "SystemAccentColorDark2",
        "SystemAccentColorDark3",
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
        "AccentTextFillColorPrimaryBrush",
        "AccentTextFillColorSecondaryBrush",
        "AccentTextFillColorTertiaryBrush",
        "ProgressBarForegroundThemeBrush",
    ];

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
            Color light1 = Blend(accent, Color.FromArgb(255, 255, 255, 255), 0.20);
            Color light2 = Blend(accent, Color.FromArgb(255, 255, 255, 255), 0.38);
            Color light3 = Blend(accent, Color.FromArgb(255, 255, 255, 255), 0.58);
            Color dark1 = Blend(accent, Color.FromArgb(255, 0, 0, 0), 0.16);
            Color dark2 = Blend(accent, Color.FromArgb(255, 0, 0, 0), 0.30);
            Color dark3 = Blend(accent, Color.FromArgb(255, 0, 0, 0), 0.46);

            resources["SystemAccentColor"] = accent;
            resources["SystemAccentColorLight1"] = light1;
            resources["SystemAccentColorLight2"] = light2;
            resources["SystemAccentColorLight3"] = light3;
            resources["SystemAccentColorDark1"] = dark1;
            resources["SystemAccentColorDark2"] = dark2;
            resources["SystemAccentColorDark3"] = dark3;

            resources["AccentFillColorDefaultBrush"] =
                new SolidColorBrush(accent);
            resources["AccentFillColorSecondaryBrush"] =
                new SolidColorBrush(WithAlpha(accent, 230));
            resources["AccentFillColorTertiaryBrush"] =
                new SolidColorBrush(WithAlpha(accent, 205));
            resources["AccentTextFillColorPrimaryBrush"] =
                new SolidColorBrush(accent);
            resources["AccentTextFillColorSecondaryBrush"] =
                new SolidColorBrush(dark1);
            resources["AccentTextFillColorTertiaryBrush"] =
                new SolidColorBrush(dark2);
            resources["ProgressBarForegroundThemeBrush"] =
                new SolidColorBrush(accent);
        }
        else
        {
            foreach ((string key, object? value)
                in Defaults)
            {
                if (value is not null)
                {
                    resources[key] = value;
                }
            }
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

        foreach (string key in AccentKeys)
        {
            resources.TryGetValue(
                key,
                out object? value);
            Defaults[key] = value;
        }

        _capturedDefaults = true;
    }

    private static Color Blend(
        Color first,
        Color second,
        double amount)
    {
        amount =
            Math.Clamp(amount, 0, 1);

        byte Mix(byte a, byte b) =>
            (byte)Math.Round(
                a + (b - a) * amount);

        return Color.FromArgb(
            255,
            Mix(first.R, second.R),
            Mix(first.G, second.G),
            Mix(first.B, second.B));
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
