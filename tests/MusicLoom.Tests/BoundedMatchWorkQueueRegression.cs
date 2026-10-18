using MusicLoom.App;

internal static class BoundedMatchWorkQueueRegression
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        int active = 0, peak = 0, total = 0;
        await using BoundedMatchWorkQueue<int> queue = new(
            async (index, token) =>
            {
                int value = Interlocked.Increment(ref active);
                int previous;
                do
                {
                    previous = peak;
                    if (previous >= value) break;
                } while (Interlocked.CompareExchange(
                    ref peak, value, previous) != previous);
                try
                {
                    await Task.Delay(28, token);
                    Interlocked.Increment(ref total);
                    return index * 3;
                }
                finally { Interlocked.Decrement(ref active); }
            }, CancellationToken.None, concurrency: 3);
        for (int i = 0; i < 9; i++) queue.Prefetch(i);
        for (int i = 0; i < 9; i++)
            if (await queue.TakeAsync(i) != i * 3)
                throw new InvalidOperationException(
                    "Automatic matching prefetch changed song order.");
        if (peak is < 2 or > 3 || total != 9 || active != 0)
            throw new InvalidOperationException(
                $"Worker concurrency exceeded the setting: {peak}/{total}");
    }
}
