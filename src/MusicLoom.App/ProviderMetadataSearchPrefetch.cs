using MusicLoom.LyricsProviders;

namespace MusicLoom.App;

/// <summary>
/// Look-ahead requests for QQ metadata matching. Only HTTP reads run ahead;
/// canonical lyrics, explicit empty matches and SQLite writes remain serial.
/// </summary>
internal sealed class ProviderMetadataSearchPrefetch : IAsyncDisposable
{
    private readonly Func<string, CancellationToken,
        Task<IReadOnlyList<CloudLyricsCandidate>>> _search;
    private readonly CancellationToken _ct;
    private readonly SemaphoreSlim _gate;
    private readonly Dictionary<string, Task<IReadOnlyList<CloudLyricsCandidate>>>
        _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _keys = new();
    private readonly List<Task> _started = [];

    internal ProviderMetadataSearchPrefetch(
        Func<string, CancellationToken,
            Task<IReadOnlyList<CloudLyricsCandidate>>> search,
        CancellationToken token, int maxConcurrency = 4)
    {
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _ct = token;
        _gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    internal Task<IReadOnlyList<CloudLyricsCandidate>> Prefetch(
        string key, string query)
    {
        if (_cache.TryGetValue(key, out var existing))
            return existing;

        // Only retain 256 most recent completed results. In-flight entries
        // are not evicted, preserving deduplication during prefetch.
        while (_cache.Count >= 256 && _keys.Count > 0)
        {
            string oldest = _keys.Peek();
            if (_cache.TryGetValue(oldest, out var running)
                && !running.IsCompleted)
                break;
            _keys.Dequeue();
            _cache.Remove(oldest);
        }

        Task<IReadOnlyList<CloudLyricsCandidate>> task = ExecuteAsync(query);
        _cache.Add(key, task);
        _keys.Enqueue(key);
        _started.Add(task);
        return task;
    }

    internal Task<IReadOnlyList<CloudLyricsCandidate>> GetAsync(
        string key, string query) => Prefetch(key, query);

    private async Task<IReadOnlyList<CloudLyricsCandidate>> ExecuteAsync(
        string query)
    {
        await _gate.WaitAsync(_ct);
        try
        {
            _ct.ThrowIfCancellationRequested();
            return await _search(query, _ct);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        // Observe speculative failures, including canceled look-ahead.
        // Individual requested song failures are reported by the caller.
        try { await Task.WhenAll(_started); }
        catch (Exception) { }
        _gate.Dispose();
    }
}
