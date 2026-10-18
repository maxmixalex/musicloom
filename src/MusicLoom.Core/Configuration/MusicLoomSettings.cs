using System.Text.Json.Serialization;

namespace MusicLoom.Core.Configuration;

public sealed record MusicLoomSettings
{
    [JsonPropertyName("verbose")]
    public bool Verbose { get; init; } = true;

    [JsonPropertyName("path")]
    public PathSettings Path { get; init; } = new();

    [JsonPropertyName("organizer")]
    public OrganizerSettings Organizer { get; init; } = new();

    [JsonPropertyName("logging")]
    public LoggingSettings Logging { get; init; } = new();

    [JsonPropertyName("ui")]
    public UiSettings Ui { get; init; } = new();

    [JsonPropertyName("matching")]
    public MatchingSettings Matching { get; init; } = new();
}

/// <summary>
/// Maximum in-flight automatic match lookups/recognition tasks, shared by
/// NetEase, QQ, KuGou and Migu. This never controls file synchronization.
/// </summary>
public sealed record MatchingSettings
{
    public const int DefaultConcurrency = 4;
    public const int MaximumConcurrency = 12;

    [JsonPropertyName("maxConcurrency")]
    public int MaxConcurrency { get; init; } = DefaultConcurrency;

    public static int Normalize(int count) =>
        Math.Clamp(count, 1, MaximumConcurrency);
}

public sealed record PathSettings
{
    [JsonPropertyName("sourceDirectory")]
    public string SourceDirectory { get; init; } = string.Empty;

    [JsonPropertyName("destinationDirectory")]
    public string DestinationDirectory { get; init; } = string.Empty;

    [JsonPropertyName("sourcePlaylistDirectory")]
    public string SourcePlaylistDirectory { get; init; } = string.Empty;

    [JsonPropertyName("destinationPlaylistBase")]
    public string DestinationPlaylistBase { get; init; } = "/storage/emulated/0/Music/";

    [JsonPropertyName("autoScanOnStartup")]
    public bool AutoScanOnStartup { get; init; }
}

public sealed record OrganizerSettings
{
    [JsonPropertyName("lyrics")]
    public LyricsSettings Lyrics { get; init; } = new();
}

public sealed record LyricsSettings
{
    [JsonPropertyName("enabled")]
    // Keep the legacy field for CLI and JSON compatibility. Online
    // matching is a separate GUI command, not an enable/disable setting.
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("overrideExisting")]
    public bool OverrideExisting { get; init; }

    [JsonPropertyName("includeRomanization")]
    public bool IncludeRomanization { get; init; } = false;

    [JsonPropertyName("refreshIntervalHours")]
    public double RefreshIntervalHours { get; init; } = 24;

    [JsonPropertyName("storageMode")]
    public string StorageMode { get; init; } = LyricsStorageModes.Embedded;
}

public sealed record LoggingSettings
{
    [JsonPropertyName("level")]
    public string Level { get; init; } = "INFO";

    [JsonPropertyName("fmt")]
    public string Format { get; init; } = "{levelname} [{name}] {message}";

    [JsonPropertyName("datefmt")]
    public string DateFormat { get; init; } = "%Y-%m-%d %H:%M:%S";
}


public sealed record UiSettings
{
    [JsonPropertyName("language")]
    public string Language { get; init; } = "system";

    [JsonPropertyName("acrylicEnabled")]
    public bool AcrylicEnabled { get; init; }

    [JsonPropertyName("accentColor")]
    public string AccentColor { get; init; } = "system";

    [JsonPropertyName("theme")]
    public string Theme { get; init; } = UiThemeModes.System;

    // Kept for one-version migration from v3.
    [JsonPropertyName("autoScrollLongTitles")]
    public bool AutoScrollLongTitles { get; init; } = true;

    [JsonPropertyName("longTextMode")]
    public string? LongTextMode { get; init; }

    [JsonPropertyName("lyricsDisplayMode")]
    public string LyricsDisplayMode { get; init; } =
        LyricsDisplayModes.PreferOriginal;

    [JsonPropertyName("lyricsEditorPath")]
    public string LyricsEditorPath { get; init; } = string.Empty;
}


public static class LyricsStorageModes
{
    public const string Embedded = "embedded";
    public const string Sidecar = "sidecar";

    public static string Normalize(string? value) =>
        string.Equals(
            value,
            Sidecar,
            StringComparison.OrdinalIgnoreCase)
            ? Sidecar
            : Embedded;
}


public static class UiThemeModes
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    public static string Normalize(string? value) =>
        value?.ToLowerInvariant() switch
        {
            Light => Light,
            Dark => Dark,
            _ => System,
        };
}


public static class LongTextModes
{
    public const string AutoScroll = "autoScroll";
    public const string Hover = "hover";
    public const string Wrap = "wrap";

    public static string Normalize(
        string? value,
        bool legacyAutoScroll = true) =>
        value switch
        {
            AutoScroll => AutoScroll,
            Hover => Hover,
            Wrap => Wrap,
            _ => legacyAutoScroll
                ? AutoScroll
                : Hover,
        };
}

public static class LyricsDisplayModes
{
    public const string OriginalOnly = "originalOnly";
    public const string PreferTranslation = "preferTranslation";
    public const string PreferOriginal = "preferOriginal";

    public static string Normalize(string? value) =>
        value switch
        {
            OriginalOnly => OriginalOnly,
            PreferTranslation => PreferTranslation,
            _ => PreferOriginal,
        };
}
