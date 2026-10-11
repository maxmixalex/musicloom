using System.Runtime.InteropServices;

namespace MusicLoom.App;

/// <summary>
/// Retries temporary clipboard contention without blocking the UI thread.
/// Persistent contention and unrelated COM errors propagate to the caller.
/// </summary>
internal static class ClipboardAccessRetry
{
    internal const int ClipboardBusyHresult = unchecked((int)0x800401D0);

    internal static async Task RunAsync(
        Action operation,
        Func<TimeSpan, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (COMException exception) when (
                exception.HResult == ClipboardBusyHresult
                && attempt < 3)
            {
                TimeSpan pause =
                    TimeSpan.FromMilliseconds(40 * (attempt + 1));
                await (delay is null
                    ? Task.Delay(pause)
                    : delay(pause));
            }
        }
    }
}
