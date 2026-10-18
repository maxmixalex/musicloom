using MusicLoom.App;
using MusicLoom.LyricsProviders;

internal static class ProviderMetadataSearchPrefetchRegression
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        int current = 0, peak = 0, requested = 0;
        await using ProviderMetadataSearchPrefetch scheduler = new(
            async (query, token) =>
            {
                int active = Interlocked.Increment(ref current);
                int previous;
                do
                {
                    previous = peak;
                    if (previous >= active) break;
                } while (Interlocked.CompareExchange(
                    ref peak, active, previous) != previous);
                Interlocked.Increment(ref requested);
                try
                {
                    await Task.Delay(30, token);
                    return [new CloudLyricsCandidate(
                        CloudLyricsSource.QqMusic,
                        query, query, "Artist", "Album", 120000)];
                }
                finally { Interlocked.Decrement(ref current); }
            }, CancellationToken.None, maxConcurrency: 4);

        Task<IReadOnlyList<CloudLyricsCandidate>> one =
            scheduler.Prefetch("artist\u001Ftitle", "Artist Title");
        Task<IReadOnlyList<CloudLyricsCandidate>> two =
            scheduler.Prefetch("artist\u001Ftitle", "Artist Title");
        if (!ReferenceEquals(one, two))
            throw new InvalidOperationException(
                "QQ prefetch repeated identical metadata request.");

        Task<IReadOnlyList<CloudLyricsCandidate>>[] many =
            Enumerable.Range(0, 13)
                .Select(i => scheduler.Prefetch($"key-{i}", $"Song {i}"))
                .ToArray();
        await Task.WhenAll(many.Append(one));
        if (requested != 14 || peak is < 2 or > 4 || current != 0)
            throw new InvalidOperationException(
                $"QQ concurrency/dedup failure: requests={requested}, peak={peak}");
        if ((await scheduler.GetAsync("artist\u001Ftitle", "Artist Title"))
            [0].Title != "Artist Title" || requested != 14)
            throw new InvalidOperationException("QQ cached result was re-fetched.");
    }
}
