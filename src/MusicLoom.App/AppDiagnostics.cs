using System.Diagnostics;
using System.Text;

namespace MusicLoom.App;

internal static class AppDiagnostics
{
    private const long RotateAtBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();

    public static event Action<string>? LineWritten;

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
            string line =
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}";

            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(
                    LogPath,
                    line + Environment.NewLine,
                    Encoding.UTF8);
            }

            LineWritten?.Invoke(line);
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

    public static void OpenLogFile()
    {
        Directory.CreateDirectory(LogDirectory);

        if (!File.Exists(LogPath))
        {
            File.WriteAllText(
                LogPath,
                string.Empty,
                Encoding.UTF8);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = LogPath,
            UseShellExecute = true,
        });
    }

    public static void ClearLog()
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.WriteAllText(
                    LogPath,
                    string.Empty,
                    Encoding.UTF8);
            }
        }
        catch
        {
        }
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
