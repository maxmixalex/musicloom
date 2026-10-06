using System.Text;

namespace MusicLoom.Core.Lyrics;

public sealed class SidecarLrcLyricsWriter : ITrackLyricsWriter
{
    public ValueTask<LyricsProbeResult> ProbeAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string sidecar = GetSidecarPath(path);
        bool present =
            File.Exists(sidecar)
            && new FileInfo(sidecar).Length > 0;

        return ValueTask.FromResult(
            new LyricsProbeResult(
                present
                    ? LyricsProbeStatus.Present
                    : LyricsProbeStatus.Missing));
    }

    public async ValueTask<LyricsWriteResult> WriteAsync(
        string path,
        LrcDocument lyrics,
        bool overrideExisting,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string sidecar = GetSidecarPath(path);
        if (File.Exists(sidecar) && !overrideExisting)
        {
            return new LyricsWriteResult(
                LyricsWriteStatus.SkippedExisting,
                "A sidecar LRC file already exists.");
        }

        string? directory = Path.GetDirectoryName(sidecar);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary =
            sidecar + ".musicloom-" + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await File.WriteAllTextAsync(
                temporary,
                lyrics.ToString(),
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            File.Move(
                temporary,
                sidecar,
                overwrite: true);
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

        return new LyricsWriteResult(
            LyricsWriteStatus.Written);
    }

    public static string GetSidecarPath(string audioPath) =>
        Path.ChangeExtension(audioPath, ".lrc");
}
