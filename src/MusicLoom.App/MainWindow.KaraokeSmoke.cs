using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using MusicLoom.Core.Configuration;
using Windows.UI;

namespace MusicLoom.App;

public sealed partial class MainWindow
{
    private void InstallKaraokeSmokeIfRequested()
    {
        string? marker = Environment.GetEnvironmentVariable(
            "MUSICLOOM_KARAOKE_SMOKE_MARKER");
        if (string.IsNullOrWhiteSpace(marker))
        {
            return;
        }

        if (Navigation.Content is not Grid root)
        {
            AppDiagnostics.Log(
                "Karaoke smoke failed: navigation content is not a Grid.");
            return;
        }

        const string sample =
            "[00:01.000]<00:01.000>Hel<00:01.400>lo<00:02.000>\n"
            + "[00:01.000]你好";
        LyricDisplayLine[] lines = BuildLyricLines(
            sample, LyricsDisplayModes.PreferTranslation);
        if (lines.Length != 1)
        {
            AppDiagnostics.Log(
                "Karaoke smoke failed: bilingual projection has incorrect row count.");
            return;
        }

        LyricDisplayLine current = lines[0];
        StackPanel host = new()
        {
            MaxWidth = 480,
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetRow(host, 1);

        TextBlock translated = new()
        {
            FontSize = 17,
            Foreground = new SolidColorBrush(
                Color.FromArgb(255, 255, 255, 255)),
        };
        TextBlock original = new()
        {
            FontSize = 19,
            Foreground = new SolidColorBrush(
                Color.FromArgb(255, 255, 255, 255)),
        };
        KaraokeLyricText.SetIsSecondary(original, true);
        KaraokeLyricText.SetLine(translated, current);
        KaraokeLyricText.SetLine(original, current);
        host.Children.Add(translated);
        host.Children.Add(original);

        bool validated = false;
        host.Loaded += (_, _) =>
        {
            if (validated)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (translated.Text != "你好"
                        || original.Inlines.Count != 2
                        || current.SecondaryWords is not { Count: 2 })
                    {
                        throw new InvalidOperationException(
                            "The secondary original lyric lost timed words.");
                    }

                    current.PlaybackPosition =
                        TimeSpan.FromMilliseconds(1200);
                    current.IsActive = true;
                    if (original.Inlines[0] is not Run first
                        || first.Foreground is not LinearGradientBrush partial
                        || partial.GradientStops.Count != 4
                        || partial.GradientStops[1].Offset < 0.48
                        || partial.GradientStops[1].Offset > 0.52)
                    {
                        throw new InvalidOperationException(
                            "Karaoke did not render a halfway word highlight.");
                    }

                    current.PlaybackPosition =
                        TimeSpan.FromMilliseconds(1750);
                    if (first.Foreground is not SolidColorBrush)
                    {
                        throw new InvalidOperationException(
                            "Completed words did not become fully highlighted.");
                    }

                    current.PlaybackPosition =
                        TimeSpan.FromMilliseconds(1150);
                    if (first.Foreground is not LinearGradientBrush)
                    {
                        throw new InvalidOperationException(
                            "Seeking backward did not restore partial highlighting.");
                    }

                    current.IsActive = false;
                    if (first.Foreground is LinearGradientBrush)
                    {
                        throw new InvalidOperationException(
                            "An inactive line retained its karaoke fill.");
                    }

                    validated = true;
                    string? dir = Path.GetDirectoryName(marker);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.WriteAllText(marker,
                        "WinUI karaoke source/translation projection, active word fill, seek and reset passed.");
                }
                catch (Exception error)
                {
                    AppDiagnostics.LogException(
                        "WinUI karaoke rendering smoke failed.", error);
                }
            });
        };

        root.Children.Add(host);
    }
}
