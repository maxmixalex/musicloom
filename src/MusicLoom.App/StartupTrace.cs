namespace MusicLoom.App;

internal static class StartupTrace
{
    private const string TraceEnvironmentVariable =
        "MUSICLOOM_STARTUP_TRACE";

    public static void Mark(string phase)
    {
        try
        {
            string? path =
                Environment.GetEnvironmentVariable(
                    TraceEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string? directory =
                Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:O}] {phase}{Environment.NewLine}");
        }
        catch
        {
            // Startup diagnostics must never become a startup failure.
        }
    }
}
