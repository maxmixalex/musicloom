using MusicLoom.Core.Enrichment;
using MusicLoom.Core.Scanning;
using MusicLoom.Metadata;
using MusicLoom.Netease;

namespace MusicLoom.App;

public sealed partial class MainWindow
{
    private async Task<TrackRecognitionResult>
        RecognizeNeteaseWithMetadataFallbackAsync(
            NeteaseTrackProvider recognizer,
            NeteaseClient client,
            PlanEntry entry,
            CancellationToken token)
    {
        try
        {
            TrackRecognitionResult fingerprint = await recognizer
                .RecognizeAsync(entry.SourcePath, token);
            if (fingerprint.Status == TrackRecognitionStatus.Matched)
                return fingerprint;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            AppDiagnostics.LogException(
                "NetEase fingerprint unavailable; attempting metadata fallback.",
                failure);
        }

        TrackMetadata? local = entry.Metadata;
        if (local is null
            || string.IsNullOrWhiteSpace(local.Title)
            || string.IsNullOrWhiteSpace(local.Artist))
            return TrackRecognitionResult.NoMatch();

        try
        {
            IReadOnlyList<NeteaseMatchCandidate> results =
                await client.SearchSongsAsync(
                    local.Title, local.Artist, local.Album,
                    cancellationToken: token);
            // Do not silently bind a similar-looking title by rank alone.
            // A fingerprint miss may only fall back to ONE exact text match.
            var exact = results.Where(candidate =>
                CanonicalMatchKey(candidate.Name) == CanonicalMatchKey(local.Title)
                && CanonicalMatchKey(candidate.Artist) == CanonicalMatchKey(local.Artist))
                .ToArray();
            if (exact.Length != 1)
                return TrackRecognitionResult.NoMatch();

            NeteaseMatchCandidate chosen = exact[0];
            if (chosen.DurationMs is long remoteDuration && remoteDuration > 0)
            {
                try
                {
                    double localDuration = AtlTrackInspector.ReadDurationMs(
                        entry.SourcePath);
                    if (double.IsFinite(localDuration) && localDuration > 0
                        && Math.Abs(localDuration - remoteDuration) > 12_000)
                        return TrackRecognitionResult.NoMatch();
                }
                catch (Exception)
                {
                    // Exact unique title+artist is still usable when a
                    // duration cannot be decoded from the source.
                }
            }

            return TrackRecognitionResult.Matched(
                chosen.TrackId, chosen.Name, chosen.Artist,
                chosen.Album, chosen.CoverUrl);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            AppDiagnostics.LogException(
                "NetEase metadata fallback unavailable.", failure);
            return TrackRecognitionResult.NoMatch();
        }
    }
}
