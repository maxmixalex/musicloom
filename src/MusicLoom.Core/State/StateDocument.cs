using MusicLoom.Core.Configuration;

namespace MusicLoom.Core.State;

public enum RecognitionState
{
    Unknown,
    Matched,
    NoMatch,
}

public enum LyricsState
{
    Unknown,
    Written,
    Existing,
    NoLyrics,
    Unsupported,
}

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

    // Captured only after a track finishes its current recipe. Null values are
    // intentionally treated as a legacy state without drift protection so an
    // upgrade never forces an otherwise unchanged library to rematerialize.
    public long? DestinationSize { get; init; }
    public long? DestinationLastWriteUtcTicks { get; init; }

    public RecognitionState Recognition { get; init; }
    public DateTimeOffset? RecognitionCheckedAt { get; init; }
    public long? NeteaseSongId { get; init; }
    public string? NeteaseTrackName { get; init; }
    public string? NeteaseTrackArtist { get; init; }
    public string? NeteaseTrackAlbum { get; init; }
    public string? NeteaseCoverUrl { get; init; }

    // Explicit user decisions are distinct from a temporary automatic
    // recognition result. Suppressed matches must never be auto-recognized
    // again until the user selects a match, while confirmed mismatches are
    // allowed to proceed without warning.
    public bool NeteaseMatchSuppressed { get; init; }
    public bool NeteaseMatchConfirmed { get; init; }

    public LyricsState Lyrics { get; init; }
    public DateTimeOffset? LyricsCheckedAt { get; init; }
    public string? LyricsFingerprint { get; init; }

    public bool MetadataCaptured { get; init; }
    public int MetadataSchemaVersion { get; init; }
    public bool TitleCaptured { get; init; }
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public bool HasEmbeddedArt { get; init; }
    public DateTimeOffset ProcessedAt { get; init; }
}

public static class SyncRecipe
{
    // v4 adds explicit enrichment outcome state and transactional executor
    // semantics while keeping source metadata cache validity independent.
    public const string CurrentVersion = "4";

    public static bool IsCompatibleForLocalSync(
        TrackState track,
        MusicLoomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(settings);

        if (string.Equals(
                track.RecipeVersion,
                For(settings),
                StringComparison.Ordinal))
        {
            return true;
        }

        // Earlier releases coupled the NetEase-fetch setting to the
        // entire synchronization recipe. Removing that retired switch
        // must not schedule thousands of audio rewrites for tracks that
        // have never had a NetEase match or managed remote lyrics.
        if (!settings.Organizer.Lyrics.Enabled
            || track.NeteaseSongId is not null
            || track.LyricsFingerprint is not null)
        {
            return false;
        }

        MusicLoomSettings oldOfflineSettings = settings with
        {
            Organizer = settings.Organizer with
            {
                Lyrics = settings.Organizer.Lyrics with
                {
                    Enabled = false,
                },
            },
        };

        return string.Equals(
            track.RecipeVersion,
            For(oldOfflineSettings),
            StringComparison.Ordinal);
    }

    public static string For(MusicLoomSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool lyricsEnabled = settings.Organizer.Lyrics.Enabled;
        bool overrideExisting =
            lyricsEnabled && settings.Organizer.Lyrics.OverrideExisting;

        string storage =
            LyricsStorageModes.Normalize(
                settings.Organizer.Lyrics.StorageMode);

        return FormattableString.Invariant(
            $"{CurrentVersion};lyrics={(lyricsEnabled ? 1 : 0)};override={(overrideExisting ? 1 : 0)};storage={storage}");
    }
}
