using System.Net;
using System.Text.Json;
using MusicLoom.Core.Synchronization;

namespace MusicLoom.App;

/// <summary>
/// All user-facing diagnostics are localized at the presentation boundary.
/// Core exception messages remain stable English technical data for logs.
/// </summary>
internal static class UserFacingErrors
{
    public static string DescribeSyncError(
        TrackSyncError error,
        bool chinese)
    {
        ArgumentNullException.ThrowIfNull(error);
        return TranslateCoreMessage(error.Message, chinese);
    }

    public static string Describe(
        Exception exception,
        bool chinese = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!chinese)
        {
            return DescribeEnglish(exception);
        }

        return exception switch
        {
            FileNotFoundException file =>
                string.IsNullOrWhiteSpace(file.FileName)
                    ? "找不到所需文件。"
                    : $"找不到文件：{file.FileName}",
            DirectoryNotFoundException =>
                "找不到所需目录，请检查源音乐库和目标音乐库路径。",
            UnauthorizedAccessException =>
                "访问被拒绝。请检查源音乐库读取权限、目标目录与数据库写入权限。",
            JsonException json =>
                json.LineNumber is long line
                    ? $"JSON 文件格式错误，约在第 {line + 1} 行。详情见诊断日志。"
                    : "JSON 文件格式错误，详情见诊断日志。",
            HttpRequestException http when http.StatusCode is HttpStatusCode code =>
                $"在线请求失败：HTTP {(int)code}（{code}）。可稍后重试。",
            HttpRequestException =>
                "在线请求失败。请检查网络，受影响的歌曲可稍后重试。",
            InvalidDataException =>
                "数据无效或格式无法识别。详细原因已记录在诊断日志中。",
            IOException =>
                "文件操作失败，可能由访问权限、锁定或磁盘问题引起。详情见诊断日志。",
            _ => TranslateCoreMessage(exception.Message, true),
        };
    }

    public static string TranslateCoreMessage(
        string? message,
        bool chinese)
    {
        if (!chinese)
        {
            return string.IsNullOrWhiteSpace(message)
                ? "An unexpected error occurred. See the diagnostic log."
                : message;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return "发生异常。详细原因已记录在诊断日志中。";
        }

        if (message.StartsWith(
            "A sidecar LRC file already exists",
            StringComparison.Ordinal))
        {
            return "目标 LRC 已存在；当前设置禁止自动覆盖。";
        }

        if (message.StartsWith(
            "The plan entry is missing a destination path",
            StringComparison.Ordinal))
        {
            return "同步计划缺少目标路径或歌曲元数据。";
        }

        if (message.StartsWith(
            "Canonical lyrics were not updated",
            StringComparison.Ordinal))
        {
            return "规范歌词未能写入数据库，请查看诊断日志。";
        }

        // Local scan details are status descriptions, not exceptions.
        // They must remain specific and localized rather than degrading
        // into a generic error message in the track-details pane.
        string? translatedPlanDetail = message switch
        {
            "Source file changed." => "源音频文件发生变化。",
            "Destination file changed." => "目标音频文件发生变化。",
            "Destination file is missing." => "目标音频文件不存在。",
            "Destination path changed." => "目标文件位置发生变化。",
            "Processing recipe changed." => "同步处理设置发生变化。",
            "Enrichment retry is due." => "需要重新检查歌词或匹配信息。",
            "Capturing source quick hash." => "正在补充源文件校验信息。",
            "Not present in MusicLoom state." => "数据库中尚无此歌曲的同步记录。",
            "Refreshing metadata state." => "正在刷新歌曲元数据记录。",
            "Source or destination requires revalidation." =>
                "源文件或目标文件需要重新校验。",
            "Enrichment requires synchronization." =>
                "歌曲的歌词或匹配信息需要同步。",
            "Materialize canonical cached lyrics." =>
                "需要将数据库中的规范歌词写入目标文件。",
            _ => null,
        };
        if (translatedPlanDetail is not null)
        {
            return translatedPlanDetail;
        }

        if (message.StartsWith(
                "Metadata could not be read; copying to the destination root.",
                StringComparison.Ordinal))
        {
            return "无法读取元数据，将文件复制到目标目录根目录。详见诊断日志。";
        }

        // Never leak a raw English exception into a Chinese UI. Preserve
        // its full technical message in AppDiagnostics instead.
        return "操作未完成。详细原因已记录在诊断日志中。";
    }

    private static string DescribeEnglish(Exception exception) =>
        exception switch
        {
            FileNotFoundException file =>
                string.IsNullOrWhiteSpace(file.FileName)
                    ? "A required file could not be found."
                    : $"File not found: {file.FileName}",
            DirectoryNotFoundException directory =>
                string.IsNullOrWhiteSpace(directory.Message)
                    ? "A required directory could not be found."
                    : directory.Message,
            UnauthorizedAccessException =>
                "Access was denied. Check that MusicLoom can read the source " +
                "and write the destination/state location.",
            JsonException json =>
                json.LineNumber is long line
                    ? $"The JSON file is invalid near line {line + 1}. " +
                      "See the diagnostic log for parser details."
                    : "The JSON file is invalid. See the diagnostic log.",
            HttpRequestException http when http.StatusCode is HttpStatusCode status =>
                $"The online request failed with HTTP {(int)status} ({status}). " +
                "This track can be retried on the next sync.",
            HttpRequestException =>
                "The online request failed. Check the network connection; " +
                "the affected track remains pending for retry.",
            IOException io =>
                string.IsNullOrWhiteSpace(io.Message)
                    ? "A file operation failed."
                    : io.Message,
            InvalidDataException invalid => invalid.Message,
            _ when !string.IsNullOrWhiteSpace(exception.Message) =>
                exception.Message,
            _ => "An unexpected error occurred. See the diagnostic log for details.",
        };
}
