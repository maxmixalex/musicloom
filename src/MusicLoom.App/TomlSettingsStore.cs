using System.Globalization;
using System.Text;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Planning;

namespace MusicLoom.App;

internal static class TomlSettingsStore
{
    public static async Task<MusicLoomSettings> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return new MusicLoomSettings();
        }

        string text =
            await File.ReadAllTextAsync(
                path,
                Encoding.UTF8,
                cancellationToken);

        Dictionary<string, string> values =
            Parse(text);

        MusicLoomSettings defaults =
            new();

        string sourceDirectory =
            ReadString(
                values,
                "path.source_directory",
                defaults.Path.SourceDirectory);
        string destinationDirectory =
            ReadString(
                values,
                "path.destination_directory",
                defaults.Path.DestinationDirectory);
        string sourcePlaylistDirectory =
            ReadString(
                values,
                "path.source_playlist_directory",
                defaults.Path.SourcePlaylistDirectory);
        string destinationPlaylistBase =
            PlaylistMapper.NormalizeDestinationBase(
                ReadString(
                    values,
                    "path.destination_playlist_base",
                    defaults.Path.DestinationPlaylistBase));

        bool autoScroll =
            ReadBool(
                values,
                "ui.auto_scroll_long_titles",
                defaults.Ui.AutoScrollLongTitles);

        string? longTextMode =
            ReadNullableString(
                values,
                "ui.long_text_mode");

        return new MusicLoomSettings
        {
            Verbose =
                ReadBool(
                    values,
                    "verbose",
                    defaults.Verbose),
            Path =
                new PathSettings
                {
                    SourceDirectory =
                        sourceDirectory,
                    DestinationDirectory =
                        destinationDirectory,
                    SourcePlaylistDirectory =
                        sourcePlaylistDirectory,
                    AutoScanOnStartup =
                        ReadBool(
                            values,
                            "path.auto_scan_on_startup",
                            defaults.Path.AutoScanOnStartup),
                    DestinationPlaylistBase =
                        string.IsNullOrWhiteSpace(
                            destinationPlaylistBase)
                            ? defaults.Path
                                .DestinationPlaylistBase
                            : destinationPlaylistBase,
                },
            Organizer =
                new OrganizerSettings
                {
                    Lyrics =
                        new LyricsSettings
                        {
                            // Ignore the retired online-fetch setting.
                            Enabled = true,
                            OverrideExisting =
                                ReadBool(
                                    values,
                                    "organizer.lyrics.override_existing",
                                    defaults.Organizer
                                        .Lyrics.OverrideExisting),
                            IncludeRomanization =
                                ReadBool(values,
                                    "organizer.lyrics.include_romanization",
                                    defaults.Organizer.Lyrics.IncludeRomanization),
                            RefreshIntervalHours =
                                ReadDouble(
                                    values,
                                    "organizer.lyrics.refresh_interval_hours",
                                    defaults.Organizer
                                        .Lyrics.RefreshIntervalHours),
                            StorageMode =
                                LyricsStorageModes.Normalize(
                                    ReadString(
                                        values,
                                        "organizer.lyrics.storage_mode",
                                        defaults.Organizer
                                            .Lyrics.StorageMode)),
                        },
                },
            Logging =
                new LoggingSettings
                {
                    Level =
                        ReadString(
                            values,
                            "logging.level",
                            defaults.Logging.Level),
                    Format =
                        ReadString(
                            values,
                            "logging.format",
                            defaults.Logging.Format),
                    DateFormat =
                        ReadString(
                            values,
                            "logging.date_format",
                            defaults.Logging.DateFormat),
                },
            Ui =
                new UiSettings
                {
                    Language =
                        ReadString(
                            values,
                            "ui.language",
                            defaults.Ui.Language),
                    AcrylicEnabled =
                        ReadBool(
                            values,
                            "ui.acrylic_enabled",
                            defaults.Ui.AcrylicEnabled),
                    AccentColor =
                        ReadString(
                            values,
                            "ui.accent_color",
                            defaults.Ui.AccentColor),
                    Theme =
                        UiThemeModes.Normalize(
                            ReadString(
                                values,
                                "ui.theme",
                                defaults.Ui.Theme)),
                    AutoScrollLongTitles =
                        autoScroll,
                    LongTextMode =
                        longTextMode is null
                            ? null
                            : LongTextModes.Normalize(
                                longTextMode,
                                autoScroll),
                    LyricsEditorPath =
                        ReadString(values, "ui.lyrics_editor_path",
                            defaults.Ui.LyricsEditorPath),
                    LyricsDisplayMode =
                        LyricsDisplayModes.Normalize(
                            ReadString(
                                values,
                                "ui.lyrics_display_mode",
                                defaults.Ui
                                    .LyricsDisplayMode)),
                },
        };
    }

    public static async Task SaveAsync(
        string path,
        MusicLoomSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(
            settings);

        string? directory =
            Path.GetDirectoryName(path);

        if (!string.IsNullOrWhiteSpace(
                directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        StringBuilder builder = new();

        builder.Append("verbose = ")
            .AppendLine(
                Bool(settings.Verbose));

        builder.AppendLine();
        builder.AppendLine("[path]");
        AppendString(
            builder,
            "source_directory",
            settings.Path.SourceDirectory);
        AppendString(
            builder,
            "destination_directory",
            settings.Path.DestinationDirectory);
        AppendString(
            builder,
            "source_playlist_directory",
            settings.Path.SourcePlaylistDirectory);
        AppendString(
            builder,
            "destination_playlist_base",
            PlaylistMapper.NormalizeDestinationBase(
                settings.Path.DestinationPlaylistBase));
        builder.Append("auto_scan_on_startup = ")
            .AppendLine(Bool(settings.Path.AutoScanOnStartup));

        builder.AppendLine();
        builder.AppendLine("[organizer.lyrics]");
        builder.Append("override_existing = ")
            .AppendLine(
                Bool(
                    settings.Organizer.Lyrics.OverrideExisting));
        builder.Append("include_romanization = ")
            .AppendLine(Bool(settings.Organizer.Lyrics.IncludeRomanization));
        builder.Append("refresh_interval_hours = ")
            .AppendLine(
                settings.Organizer.Lyrics
                    .RefreshIntervalHours
                    .ToString(
                        "0.################",
                        CultureInfo.InvariantCulture));
        AppendString(
            builder,
            "storage_mode",
            LyricsStorageModes.Normalize(
                settings.Organizer.Lyrics.StorageMode));

        builder.AppendLine();
        builder.AppendLine("[logging]");
        AppendString(
            builder,
            "level",
            settings.Logging.Level);
        AppendString(
            builder,
            "format",
            settings.Logging.Format);
        AppendString(
            builder,
            "date_format",
            settings.Logging.DateFormat);

        builder.AppendLine();
        builder.AppendLine("[ui]");
        AppendString(
            builder,
            "language",
            settings.Ui.Language);
        builder.Append("acrylic_enabled = ")
            .AppendLine(
                Bool(
                    settings.Ui.AcrylicEnabled));
        AppendString(
            builder,
            "accent_color",
            settings.Ui.AccentColor);
        AppendString(
            builder,
            "theme",
            UiThemeModes.Normalize(
                settings.Ui.Theme));
        builder.Append("auto_scroll_long_titles = ")
            .AppendLine(
                Bool(
                    settings.Ui.AutoScrollLongTitles));
        AppendString(
            builder,
            "long_text_mode",
            LongTextModes.Normalize(
                settings.Ui.LongTextMode,
                settings.Ui.AutoScrollLongTitles));
        AppendString(
            builder,
            "lyrics_display_mode",
            LyricsDisplayModes.Normalize(
                settings.Ui.LyricsDisplayMode));
        AppendString(builder, "lyrics_editor_path",
            settings.Ui.LyricsEditorPath);

        string temporaryPath =
            path
            + ".tmp."
            + Guid.NewGuid().ToString(
                "N");

        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                builder.ToString(),
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier:
                        false),
                cancellationToken);

            File.Move(
                temporaryPath,
                path,
                overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(
                        temporaryPath))
                {
                    File.Delete(
                        temporaryPath);
                }
            }
            catch
            {
            }
        }
    }

    private static Dictionary<string, string>
        Parse(
            string text)
    {
        Dictionary<string, string> values =
            new(
                StringComparer.OrdinalIgnoreCase);

        string section =
            string.Empty;

        foreach (string rawLine
            in text.Split(
                ['\r', '\n'],
                StringSplitOptions.None))
        {
            string line =
                StripComment(rawLine)
                    .Trim();

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[')
                && line.EndsWith(']')
                && line.Length > 2)
            {
                section =
                    line[1..^1]
                        .Trim();
                continue;
            }

            int equals =
                FindEquals(line);

            if (equals <= 0)
            {
                continue;
            }

            string key =
                line[..equals]
                    .Trim();
            string value =
                line[(equals + 1)..]
                    .Trim();

            string fullKey =
                string.IsNullOrWhiteSpace(
                    section)
                    ? key
                    : section + "." + key;

            values[fullKey] =
                value;
        }

        return values;
    }

    private static int FindEquals(
        string line)
    {
        bool inString = false;
        bool escaped = false;

        for (int i = 0;
            i < line.Length;
            i++)
        {
            char character =
                line[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString
                && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString
                && character == '=')
            {
                return i;
            }
        }

        return -1;
    }

    private static string StripComment(
        string line)
    {
        bool inString = false;
        bool escaped = false;

        for (int i = 0;
            i < line.Length;
            i++)
        {
            char character =
                line[i];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString
                && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString
                && character == '#')
            {
                return line[..i];
            }
        }

        return line;
    }

    private static string ReadString(
        IReadOnlyDictionary<string, string> values,
        string key,
        string fallback)
    {
        string? value =
            ReadNullableString(
                values,
                key);

        return value
            ?? fallback;
    }

    private static string? ReadNullableString(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        if (!values.TryGetValue(
                key,
                out string? raw))
        {
            return null;
        }

        raw = raw.Trim();

        if (raw.Length >= 2
            && raw[0] == '"'
            && raw[^1] == '"')
        {
            return Unescape(
                raw[1..^1]);
        }

        return raw;
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool fallback) =>
        values.TryGetValue(
            key,
            out string? raw)
        && bool.TryParse(
            raw.Trim(),
            out bool value)
                ? value
                : fallback;

    private static double ReadDouble(
        IReadOnlyDictionary<string, string> values,
        string key,
        double fallback) =>
        values.TryGetValue(
            key,
            out string? raw)
        && double.TryParse(
            raw.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double value)
        && double.IsFinite(value)
                ? value
                : fallback;

    private static void AppendString(
        StringBuilder builder,
        string key,
        string? value)
    {
        builder.Append(key)
            .Append(" = \"")
            .Append(
                Escape(
                    value
                    ?? string.Empty))
            .AppendLine("\"");
    }

    private static string Escape(
        string value) =>
        value
            .Replace(
                "\\",
                "\\\\",
                StringComparison.Ordinal)
            .Replace(
                "\"",
                "\\\"",
                StringComparison.Ordinal)
            .Replace(
                "\r",
                "\\r",
                StringComparison.Ordinal)
            .Replace(
                "\n",
                "\\n",
                StringComparison.Ordinal)
            .Replace(
                "\t",
                "\\t",
                StringComparison.Ordinal);

    private static string Unescape(
        string value)
    {
        StringBuilder builder = new();
        bool escaped = false;

        foreach (char character
            in value)
        {
            if (!escaped)
            {
                if (character == '\\')
                {
                    escaped = true;
                }
                else
                {
                    builder.Append(
                        character);
                }

                continue;
            }

            builder.Append(
                character switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\\' => '\\',
                    '"' => '"',
                    _ => character,
                });

            escaped = false;
        }

        if (escaped)
        {
            builder.Append('\\');
        }

        return builder.ToString();
    }

    private static string Bool(
        bool value) =>
        value ? "true" : "false";
}
