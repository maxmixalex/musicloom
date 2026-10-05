using System.Net;
using System.Text.Json;

namespace MusicLoom.App;

internal static class UserFacingErrors
{
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
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
                DescribeJson(json),

            HttpRequestException http
                when http.StatusCode is HttpStatusCode status =>
                $"The online request failed with HTTP {(int)status} ({status}). " +
                "This track can be retried on the next sync.",

            HttpRequestException =>
                "The online request failed. Check the network connection; " +
                "the affected track remains pending for retry.",

            IOException io =>
                string.IsNullOrWhiteSpace(io.Message)
                    ? "A file operation failed."
                    : io.Message,

            InvalidDataException invalid =>
                invalid.Message,

            _ when !string.IsNullOrWhiteSpace(exception.Message) =>
                exception.Message,

            _ => "An unexpected error occurred. See the diagnostic log for details.",
        };
    }

    private static string DescribeJson(JsonException json)
    {
        if (json.LineNumber is long line)
        {
            return
                $"The JSON file is invalid near line {line + 1}. " +
                "See the diagnostic log for the parser details.";
        }

        return "The JSON file is invalid. See the diagnostic log for details.";
    }
}
