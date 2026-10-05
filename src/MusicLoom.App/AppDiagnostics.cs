using System.Diagnostics;
using System.Text;

namespace MusicLoom.App;

internal static class AppDiagnostics
{
    private const long RotateAtBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string DataDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "MusicLoom");

    public static string LogDirectory { get; } =
        Path.Combine(DataDirectory, "logs");

    public static string LogPath { get; } =
        Path.Combine(LogDirectory, "musicloom.log");

    public static void Initialize()
    {
        Directory.CreateDirectory(LogDirectory);
        RotateIfNeeded();
        Log("MusicLoom started.");
    }

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(
                    LogPath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never become an application failure.
        }
    }

    public static void LogException(
        string context,
        Exception exception)
    {
        Log(
            $"{context}{Environment.NewLine}" +
            exception);
    }

    public static void OpenLogDirectory()
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = LogDirectory,
            UseShellExecute = true,
        });
    }

    private static void RotateIfNeeded()
    {
        try
        {
            FileInfo file = new(LogPath);
            if (!file.Exists || file.Length < RotateAtBytes)
            {
                return;
            }

            string previous =
                Path.Combine(
                    LogDirectory,
                    "musicloom.previous.log");
            File.Move(
                LogPath,
                previous,
                overwrite: true);
        }
        catch
        {
        }
    }
}
