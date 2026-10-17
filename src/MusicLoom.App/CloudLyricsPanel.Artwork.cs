using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

public sealed partial class CloudLyricsPanel
{
    // Keep artwork requests completely separate from the search/preview
    // cancellation token. Selecting the first result triggers lyric preview
    // and must not cancel album-cover loading for the other results.
    private CancellationTokenSource? _artworkLoadCancellation;

    private void CancelArtworkLoad()
    {
        _artworkLoadCancellation?.Cancel();
        _artworkLoadCancellation?.Dispose();
        _artworkLoadCancellation = null;
    }

    private void BeginArtworkLoading(CloudCandidateRow[] rows)
    {
        CancelArtworkLoad();
        if (rows.All(static row => row.Artwork is not null
            && (row.Candidate.Source != CloudLyricsSource.MiguMusic
                || row.Candidate.DurationMilliseconds > 0)))
            return;

        _artworkLoadCancellation = new CancellationTokenSource();
        _ = LoadArtworkAsync(rows, _artworkLoadCancellation.Token);
    }

    private async Task LoadArtworkAsync(
        CloudCandidateRow[] rows, CancellationToken token)
    {
        // Display search results immediately. Fill missing Migu durations
        // from provider-owned details/song-info/listen in the background;
        // QQ/KuGou artwork is still grouped by album to reduce requests.
        // At most 3 first-party HTTP requests are in flight concurrently.
        using SemaphoreSlim limit = new(3, 3);
        var groups = rows
            .Where(static row => row.Artwork is null
                || (row.Candidate.Source == CloudLyricsSource.MiguMusic
                    && row.Candidate.DurationMilliseconds <= 0))
            .GroupBy(static row =>
                row.Candidate.Source == CloudLyricsSource.MiguMusic
                    ? "migu-track:" + row.Candidate.Id
                    : CloudLyricsSourceNames.Slug(row.Candidate.Source) + ":"
                      + (row.Candidate.AlbumId ?? row.Candidate.Hash
                          ?? row.Candidate.Id))
            .ToArray();

        Task[] requests = groups.Select(async group =>
        {
            bool acquired = false;
            try
            {
                await limit.WaitAsync(token);
                acquired = true;
                CloudCandidateRow exemplar = group.First();

                if (exemplar.Candidate.Source == CloudLyricsSource.MiguMusic)
                {
                    CloudLyricsCandidate enriched = await _client
                        .EnrichMiguCandidateAsync(exemplar.Candidate, token);
                    token.ThrowIfCancellationRequested();
                    if (_disposed
                        || !ReferenceEquals(CandidatesList.ItemsSource, rows))
                        return;
                    foreach (CloudCandidateRow row in group)
                        row.ApplyMetadata(enriched);
                }
                else
                {
                    string? image = await _client.ResolveArtworkAsync(
                        exemplar.Candidate, token);
                    token.ThrowIfCancellationRequested();
                    if (_disposed || image is null
                        || !ReferenceEquals(CandidatesList.ItemsSource, rows))
                        return;
                    foreach (CloudCandidateRow row in group)
                        row.ApplyArtwork(image);
                }
            }
            catch (OperationCanceledException)
            {
                // Closing/replacing the search cancels enrichment.
            }
            catch (Exception error)
            {
                // External metadata is optional and never blocks search,
                // timed lyric preview or explicit match adoption.
                AppDiagnostics.LogException(
                    "Could not enrich provider match candidate.", error);
            }
            finally
            {
                if (acquired)
                    limit.Release();
            }
        }).ToArray();
        await Task.WhenAll(requests);
    }
}
