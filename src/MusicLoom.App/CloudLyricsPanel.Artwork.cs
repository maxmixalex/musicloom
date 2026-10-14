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
        if (rows.All(static row => row.Artwork is not null))
        {
            return;
        }

        _artworkLoadCancellation = new CancellationTokenSource();
        _ = LoadArtworkAsync(rows, _artworkLoadCancellation.Token);
    }

    private async Task LoadArtworkAsync(
        CloudCandidateRow[] rows, CancellationToken token)
    {
        // A search should become interactive as soon as the song list is
        // available. Enrich only missing covers, at most three concurrent
        // first-party provider metadata requests, deduplicated by album.
        using SemaphoreSlim limit = new(3, 3);
        var groups = rows
            .Where(static row => row.Artwork is null)
            .GroupBy(static row =>
                row.Candidate.Source == CloudLyricsSource.KugouMusic
                    ? "kg:" + (row.Candidate.AlbumId is { Length: > 0 } id
                        ? id : row.Candidate.Hash ?? row.Candidate.Id)
                    : "qq:" + (row.Candidate.AlbumId ?? row.Candidate.Id))
            .ToArray();

        Task[] requests = groups.Select(async group =>
        {
            bool acquired = false;
            try
            {
                await limit.WaitAsync(token);
                acquired = true;
                string? image = await _client.ResolveArtworkAsync(
                    group.First().Candidate, token);
                token.ThrowIfCancellationRequested();

                // A previous modal/search may have been closed or replaced.
                if (_disposed || image is null
                    || !ReferenceEquals(CandidatesList.ItemsSource, rows))
                {
                    return;
                }

                foreach (CloudCandidateRow row in group)
                {
                    row.ApplyArtwork(image);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                // Artwork is optional; never hide search results or prevent
                // lyric preview/adoption when provider image metadata fails.
                AppDiagnostics.LogException(
                    "Could not retrieve QQ/KuGou album artwork.", error);
            }
            finally
            {
                if (acquired)
                {
                    limit.Release();
                }
            }
        }).ToArray();

        await Task.WhenAll(requests);
    }
}
