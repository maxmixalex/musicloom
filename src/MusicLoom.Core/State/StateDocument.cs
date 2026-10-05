using MusicLoom.Core.Configuration;

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

    // This is the output recipe that completed successfully for this track.
    // It is deliberately independent from whether the cached source metadata
    // is still usable.
    public string RecipeVersion { get; init; } = string.Empty;

    public string? DestinationPath { get; init; }
    public long? NeteaseSongId { get; init; }
    public string? LyricsFingerprint { get; init; }
    public bool MetadataCaptured { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public bool HasEmbeddedArt { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
}

public static class SyncRecipe
{
    // v3 separates source/metadata cache validity from completion of the
    // configured output recipe.
    public const string CurrentVersion = "3";

    public static string For(MusicLoomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool lyricsEnabled = settings.Organizer.Lyrics.Enabled;
        bool overrideExisting =
            lyricsEnabled && settings.Organizer.Lyrics.OverrideExisting;

        return FormattableString.Invariant(
            $"{CurrentVersion};lyrics={(lyricsEnabled ? 1 : 0)};override={(overrideExisting ? 1 : 0)}");
    }
}
