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

            string theme = UiThemeModes.Normalize(settings.Theme);
            bool dark = theme == UiThemeModes.Dark
                || (theme is not UiThemeModes.Light
                    && window.Content is FrameworkElement root
                    && root.ActualTheme == ElementTheme.Dark);
            // Fluent surfaces are theme-aware, but accent text was
            // previously always assigned dark shades. On a dark surface
            // those shades became nearly invisible. Respect the user's
            // chosen accent for buttons, only toning text for contrast.
            Color readable = EnsureAccentTextContrast(accent, dark);
            Color readableSecondary = Blend(readable,
                dark ? Color.FromArgb(255, 255, 255, 255)
                     : Color.FromArgb(255, 0, 0, 0), 0.12);
            Color readableTertiary = Blend(readable,
                dark ? Color.FromArgb(255, 255, 255, 255)
                     : Color.FromArgb(255, 0, 0, 0), 0.22);

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
                new SolidColorBrush(WithAlpha(accent, dark ? (byte)207 : (byte)230));
            resources["AccentFillColorTertiaryBrush"] =
                new SolidColorBrush(WithAlpha(accent, dark ? (byte)162 : (byte)205));
            resources["AccentTextFillColorPrimaryBrush"] =
                new SolidColorBrush(readable);
            resources["AccentTextFillColorSecondaryBrush"] =
                new SolidColorBrush(readableSecondary);
            resources["AccentTextFillColorTertiaryBrush"] =
                new SolidColorBrush(readableTertiary);
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

    private static Color EnsureAccentTextContrast(
        Color accent, bool dark)
    {
        // Match at least 4.5:1 against a neutral Fluent card surface.
        // This changes text only, not the user's selected accent color.
        Color background = dark
            ? Color.FromArgb(255, 30, 30, 34)
            : Color.FromArgb(255, 250, 250, 250);
        Color target = dark
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(255, 0, 0, 0);

        static double Channel(byte value)
        {
            double x = value / 255.0;
            return x <= 0.04045
                ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color) =>
            0.2126 * Channel(color.R)
            + 0.7152 * Channel(color.G)
            + 0.0722 * Channel(color.B);

        double surface = Luminance(background);
        for (int step = 0; step <= 20; step++)
        {
            Color candidate = Blend(accent, target, step / 20d);
            double value = Luminance(candidate);
            double ratio = (Math.Max(value, surface) + 0.05)
                / (Math.Min(value, surface) + 0.05);
            if (ratio >= 4.5)
                return candidate;
        }

        return target;
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
