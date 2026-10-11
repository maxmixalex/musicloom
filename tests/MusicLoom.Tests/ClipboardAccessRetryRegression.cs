using System.Runtime.InteropServices;
using MusicLoom.App;

internal static class ClipboardAccessRetryRegression
{
    public static void Run()
    {
        int attempts = 0;
        int pauses = 0;
        Task NoWait(TimeSpan _)
        {
            pauses++;
            return Task.CompletedTask;
        }

        ClipboardAccessRetry.RunAsync(
            () =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw new COMException(
                        "Clipboard is busy.",
                        ClipboardAccessRetry.ClipboardBusyHresult);
                }
            },
            NoWait).GetAwaiter().GetResult();

        if (attempts != 3 || pauses != 2)
        {
            throw new InvalidOperationException(
                "Transient clipboard contention was not retried correctly.");
        }

        attempts = 0;
        pauses = 0;
        bool exhausted = false;
        try
        {
            ClipboardAccessRetry.RunAsync(
                () =>
                {
                    attempts++;
                    throw new COMException(
                        "Clipboard remains busy.",
                        ClipboardAccessRetry.ClipboardBusyHresult);
                },
                NoWait).GetAwaiter().GetResult();
        }
        catch (COMException exception) when (
            exception.HResult == ClipboardAccessRetry.ClipboardBusyHresult)
        {
            exhausted = true;
        }

        if (!exhausted || attempts != 4 || pauses != 3)
        {
            throw new InvalidOperationException(
                "Persistent clipboard contention was not bounded.");
        }

        attempts = 0;
        bool unrelatedError = false;
        try
        {
            ClipboardAccessRetry.RunAsync(
                () =>
                {
                    attempts++;
                    throw new COMException(
                        "Unrelated COM failure.",
                        unchecked((int)0x80004005));
                },
                _ => throw new InvalidOperationException(
                    "Unexpected retry.")).GetAwaiter().GetResult();
        }
        catch (COMException exception) when (
            exception.HResult == unchecked((int)0x80004005))
        {
            unrelatedError = true;
        }

        if (!unrelatedError || attempts != 1)
        {
            throw new InvalidOperationException(
                "Unrelated COM failures must not be retried.");
        }
    }
}
