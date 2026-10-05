namespace MusicLoom.Core.State;

public sealed record StateDocument
{
    public int SchemaVersion { get; init; } = 1;
    public string RecipeVersion { get; init; } = SyncRecipe.CurrentVersion;
    public Dictionary<string, TrackState> Tracks { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record TrackState
{
    public required string SourcePath { get; init; }
    public long SourceSize { get; init; }
    public long SourceLastWriteUtcTicks { get; init; }
    public ulong QuickHash { get; init; }
    public string RecipeVersion { get; init; } = SyncRecipe.CurrentVersion;
    public string? DestinationPath { get; init; }
    public long? NeteaseSongId { get; init; }
    public string? LyricsFingerprint { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
}

public static class SyncRecipe
{
    public const string CurrentVersion = "1";
}
