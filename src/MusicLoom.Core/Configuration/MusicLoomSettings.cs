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
}

public sealed record OrganizerSettings
{
    [JsonPropertyName("lyrics")]
    public LyricsSettings Lyrics { get; init; } = new();
}

public sealed record LyricsSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("overrideExisting")]
    public bool OverrideExisting { get; init; }

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
