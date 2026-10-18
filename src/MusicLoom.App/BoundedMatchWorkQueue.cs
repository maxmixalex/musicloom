namespace MusicLoom.App;

/// <summary>
/// Bounded independent recognition/lookup workers. The GUI reads results in
/// source-list order and is the ONLY writer to SQLite and StateDocument.
/// The count is maximum simultaneous tasks, not permanent native threads.
/// </summary>
internal sealed class BoundedMatchWorkQueue<T> : IAsyncDisposable
{
    private readonly Func<int, CancellationToken, Task<T>> _worker;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationToken _ct;
    private readonly Dictionary<int, Task<T>> _pending = [];
    private bool _disposed;

    public BoundedMatchWorkQueue(
        Func<int, CancellationToken, Task<T>> worker,
        CancellationToken ct, int concurrency)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _ct = ct;
        _slots = new SemaphoreSlim(
            Math.Clamp(concurrency, 1, 12), Math.Clamp(concurrency, 1, 12));
    }

    public void Prefetch(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pending.ContainsKey(index))
            _pending.Add(index, RunAsync(index));
    }

    public async Task<T> TakeAsync(int index)
    {
        Prefetch(index);
        Task<T> task = _pending[index];
        try { return await task; }
        finally { _pending.Remove(index); }
    }

    private async Task<T> RunAsync(int index)
    {
        await _slots.WaitAsync(_ct);
        try
        {
            _ct.ThrowIfCancellationRequested();
            // Audio decoding can be synchronously expensive before its
            // first await. Run it off the WinUI dispatcher.
            return await Task.Run(() => _worker(index, _ct), _ct);
        }
        finally { _slots.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await Task.WhenAll(_pending.Values); }
        catch (Exception) { /* Speculative failures are observed here. */ }
        _slots.Dispose();
    }
}
