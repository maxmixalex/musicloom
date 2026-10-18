using MusicLoom.App;
using MusicLoom.Core.Lyrics;
using MusicLoom.LyricsProviders;

internal static class ProviderMatchPersistenceRegression
{
    public static void Run()
    {
        string db = Path.Combine(Path.GetTempPath(),
            "musicloom-provider-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            SqliteProviderMatchStore store = new(db);
            const string path = @"C:\Music\mytrack.flac";
            const string otherPath = @"C:\Music\another.flac";

            if (store.GetAsync(path, CloudLyricsSource.QqMusic)
                .GetAwaiter().GetResult() is not null)
                throw new InvalidOperationException("An unused provider has a fake match.");

            CloudLyricsCandidate qq = new(
                CloudLyricsSource.QqMusic, "32521", "Remote QQ Song",
                "Remote Singer", "QQ album", 187_000,
                SongMid: "003lghpv0jfFXG",
                CoverUrl: "https://y.gtimg.cn/cover.jpg",
                AlbumId: "643", MediaMid: "001mediaM1D1abc");
            CloudLyricsCandidate kugou = new(
                CloudLyricsSource.KugouMusic, "323", "KuGou Song",
                "KuGou singer", "KuGou album", 188_000,
                Hash: "A4ED13E2A4ED13E2A4ED13E2A4ED13E2",
                AlbumId: "551");
            store.SaveMatchAsync(path, qq, confirmed: true)
                .GetAwaiter().GetResult();
            store.SaveMatchAsync(path, kugou, confirmed: false)
                .GetAwaiter().GetResult();

            StoredProviderMatch? qqMatch = store.GetAsync(
                path, CloudLyricsSource.QqMusic).GetAwaiter().GetResult();
            StoredProviderMatch? kgMatch = store.GetAsync(
                path, CloudLyricsSource.KugouMusic).GetAwaiter().GetResult();
            if (qqMatch?.Candidate != qq || !qqMatch.Confirmed
                || kgMatch?.Candidate != kugou || kgMatch.Confirmed)
                throw new InvalidOperationException(
                    "Provider match metadata was lost or cross-contaminated.");

            // Repairing only the missing KuGou artwork must preserve
            // confirmation and must never overwrite another provider.
            const string restoredCover =
                "https://imge.kugou.com/stdmusic/300/restored.jpg";
            if (!store.FillMissingCoverAsync(path, CloudLyricsSource.KugouMusic,
                    kugou.Id, restoredCover).GetAwaiter().GetResult()
                || store.FillMissingCoverAsync(path, CloudLyricsSource.QqMusic,
                    qq.Id, restoredCover).GetAwaiter().GetResult()
                || store.FillMissingCoverAsync(path, CloudLyricsSource.KugouMusic,
                    "other-id", restoredCover).GetAwaiter().GetResult()
                || store.FillMissingCoverAsync(path, CloudLyricsSource.KugouMusic,
                    kugou.Id, "https://imge.kugou.com/second.jpg")
                    .GetAwaiter().GetResult())
                throw new InvalidOperationException(
                    "Missing-artwork compare-and-update changed another match.");
            StoredProviderMatch? repaired =
                store.GetAsync(path, CloudLyricsSource.KugouMusic)
                    .GetAwaiter().GetResult();
            if (repaired?.Candidate?.CoverUrl != restoredCover
                || repaired.Confirmed || repaired.Candidate.Id != kugou.Id)
                throw new InvalidOperationException(
                    "Cover recovery overwrote provider match identity or confirmation.");

            store.ConfirmAsync(path, CloudLyricsSource.KugouMusic)
                .GetAwaiter().GetResult();
            if (store.GetAsync(path, CloudLyricsSource.KugouMusic)
                .GetAwaiter().GetResult()?.Confirmed != true)
                throw new InvalidOperationException(
                    "Confirming a provider match was not durable.");

            // A provider-specific explicit empty state must be durable,
            // and must not change either a second provider or canonical LRC.
            SqliteLyricsCache lyrics = new(db);
            LibraryLyricsRecord canonical = new(path,
                LibraryLyricsOrigin.Embedded, "[00:01.00]local", null,
                "embedded:a", DateTimeOffset.UtcNow);
            lyrics.SaveLibraryAsync(canonical).GetAwaiter().GetResult();
            store.SuppressAsync(path, CloudLyricsSource.QqMusic)
                .GetAwaiter().GetResult();

            StoredProviderMatch? empty = store.GetAsync(
                path, CloudLyricsSource.QqMusic).GetAwaiter().GetResult();
            if (empty is not { Suppressed: true, Candidate: null }
                || store.GetAsync(path, CloudLyricsSource.KugouMusic)
                   .GetAwaiter().GetResult()?.Candidate !=
                       kugou with { CoverUrl = restoredCover }
                || lyrics.GetLibraryAsync(path).GetAwaiter().GetResult()?.Text
                   != canonical.Text)
                throw new InvalidOperationException(
                    "QQ explicit empty state erased KuGou or canonical lyrics.");

            if (store.FillMissingCoverAsync(path, CloudLyricsSource.QqMusic,
                    qq.Id, restoredCover).GetAwaiter().GetResult())
                throw new InvalidOperationException(
                    "Late artwork recovery revived an explicitly cleared match.");

            SqliteProviderMatchStore reopened = new(db);
            if (reopened.GetAsync(path, CloudLyricsSource.QqMusic)
                    .GetAwaiter().GetResult() is not { Suppressed: true }
                || reopened.GetAsync(otherPath, CloudLyricsSource.QqMusic)
                    .GetAwaiter().GetResult() is not null)
                throw new InvalidOperationException(
                    "Empty state did not survive reopening or leaked to another track.");

            reopened.SaveMatchAsync(path, qq with { Title = "Replacement" }, true)
                .GetAwaiter().GetResult();
            if (reopened.GetAsync(path, CloudLyricsSource.QqMusic)
                    .GetAwaiter().GetResult() is not
                    { Suppressed: false, Candidate.Title: "Replacement" })
                throw new InvalidOperationException(
                    "Explicit re-match did not replace suppression.");
        }
        finally
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(db + suffix); } catch (IOException) { }
            }
        }
    }
}
