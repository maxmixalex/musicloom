using MusicLoom.App;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Scanning;
using MusicLoom.Core.State;
using MusicLoom.Core.Synchronization;
using MusicLoom.LyricsProviders;

internal static class ProviderMatchReviewPolicyRegression
{
    public static void Run()
    {
        const string source = @"C:\Music\Deadman.flac";
        PlanEntry deadman = new(source, @"D:\Music\Deadman.flac",
            PlanEntryStatus.Unchanged, new TrackMetadata(
                Artist:"蔡徐坤", AlbumArtist:"蔡徐坤", Album:"Deadman",
                HasEmbeddedArt:false, Title:"Deadman"));
        StoredProviderMatch qq = new(
            source, CloudLyricsSource.QqMusic,
            new CloudLyricsCandidate(
                CloudLyricsSource.QqMusic, "577361786",
                "Deadman", "蔡徐坤", "Deadman", 235000),
            Suppressed:false, Confirmed:false);
        StateDocument state = new();
        Dictionary<CloudLyricsSource, StoredProviderMatch> providers = new()
        {
            [CloudLyricsSource.QqMusic] = qq,
        };

        // Crucially, an old confirmed=false record does NOT automatically
        // imply review. It may be an exact safe automatic match.
        Check(!ProviderMatchReviewPolicy.NeedsReview(deadman, qq),
            "An exact old QQ auto-match unexpectedly requests confirmation.");
        Check(LibraryMatchStatusResolver.GetKind(deadman, state, providers)
            == "synced", "A matching QQ record must not override Synced.");
        Check(ProviderMatchReviewPolicy.IsReady(deadman, qq),
            "A safe unconfirmed automatic match should be no-review-required.");
        Check(!qq.Confirmed, "Automatic review must not forge user approval.");

        // A live release is NOT equivalent to the studio album even when
        // the song title and performing artist happen to match.
        StoredProviderMatch live = qq with
        {
            Candidate = qq.Candidate! with
            {
                Album = "Deadman (Live in Moscow)",
            },
        };
        providers[CloudLyricsSource.QqMusic] = live;
        Check(ProviderMatchReviewPolicy.NeedsReview(deadman, live),
            "Different album variants must request review.");
        Check(LibraryMatchStatusResolver.GetKind(deadman, state, providers)
            == "warning",
            "Any suspicious provider should give the track a warning.");

        providers[CloudLyricsSource.QqMusic] = qq;
        StoredProviderMatch badKugou = qq with
        {
            Provider = CloudLyricsSource.KugouMusic,
            Candidate = qq.Candidate! with
            {
                Source = CloudLyricsSource.KugouMusic,
                Artist = "Unrelated Artist",
            },
        };
        providers[CloudLyricsSource.KugouMusic] = badKugou;
        Check(LibraryMatchStatusResolver.GetKind(deadman, state, providers)
            == "warning", "A second provider review must propagate to row.");
        Check(ProviderMatchReviewPolicy.NeedsReview(deadman, badKugou),
            "The original NetEase artist compatibility rule must be reused.");
        providers[CloudLyricsSource.KugouMusic] =
            badKugou with { Confirmed = true };
        Check(LibraryMatchStatusResolver.GetKind(deadman, state, providers)
            == "synced", "Explicit confirmation must clear warning.");

        // No match and explicit no-match cannot become warnings.
        providers[CloudLyricsSource.KugouMusic] =
            badKugou with { Suppressed = true, Candidate = null };
        Check(LibraryMatchStatusResolver.GetKind(deadman, state, providers)
            == "synced", "Suppression is not an unconfirmed match.");
        providers[CloudLyricsSource.KugouMusic] = badKugou with
        {
            Candidate = qq.Candidate! with
            {
                Source = CloudLyricsSource.KugouMusic,
                Title = "Different Title",
            },
        };
        Check(ProviderMatchReviewPolicy.NeedsReview(deadman,
                providers[CloudLyricsSource.KugouMusic]),
            "Title differences must follow NetEase shared warning rule.");

        // NetEase stays on the original review pathway.
        state.Tracks[source] = new TrackState
        {
            SourcePath = source,
            NeteaseSongId = 42,
            NeteaseTrackName = "Deadman",
            NeteaseTrackArtist = "Somebody else",
        };
        Check(LibraryMatchStatusResolver.GetKind(deadman, state,
            new Dictionary<CloudLyricsSource, StoredProviderMatch>())
            == "warning", "Existing NetEase warning was changed.");
        Check(SyncExecutor.DetectMetadataWarning(source,
                "Deadman", "蔡徐坤", "Deadman", "蔡徐坤") is null,
            "Common NetEase metadata detector misclassified exact matches.");
        Check(SyncExecutor.MetadataValuesEquivalent(
                "　Deadman！", "deadman"),
            "Shared normalization no longer handles Unicode punctuation.");

        // Error remains higher priority than matching warnings.
        PlanEntry errored = deadman with { Status = PlanEntryStatus.Error };
        Check(LibraryMatchStatusResolver.GetKind(errored, state, providers)
            == "error", "Matching warning masked a real synchronization error.");
    }

    private static void Check(bool good, string message)
    {
        if (!good) throw new InvalidOperationException(message);
    }
}
