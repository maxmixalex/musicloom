using System.Text.Json;

namespace MusicLoom.Core.Configuration;

public static class SettingsLoader
{
    public static async Task<MusicLoomSettings> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(path);
        MusicLoomSettings? settings = await JsonSerializer.DeserializeAsync(
            stream,
            MusicLoomJsonContext.Default.MusicLoomSettings,
            cancellationToken);

        return settings ?? throw new InvalidDataException(
            $"Settings file '{path}' is empty or invalid.");
    }

    public static async Task SaveAsync(
        string path,
        MusicLoomSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary =
            path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);

            await JsonSerializer.SerializeAsync(
                stream,
                settings,
                MusicLoomJsonContext.Default.MusicLoomSettings,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
            }
        }
    }
}
