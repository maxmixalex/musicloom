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

        return settings ?? throw new InvalidDataException($"Settings file '{path}' is empty or invalid.");
    }
}
