using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MusicLoom.ProviderPoc;

/// <summary>Small bounded, cancellable HTTP / JSON primitives for stand-alone PoCs.</summary>
internal static class ProviderProtocol
{
    public static HttpClient CreateHttpClient() =>
        new(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseCookies = false,
        })
        {
            Timeout = TimeSpan.FromSeconds(12),
        };

    public static JsonElement Get(JsonElement node, string name)
    {
        if (node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty(name, out JsonElement item))
            return item;
        return default;
    }

    public static JsonElement Path(JsonElement node, params string[] names)
    {
        foreach (string name in names) node = Get(node, name);
        return node;
    }

    public static IEnumerable<JsonElement> Array(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in node.EnumerateArray())
                yield return item;
    }

    public static string Text(JsonElement node, params string[] names)
    {
        foreach (string name in names)
        {
            JsonElement v = Get(node, name);
            if (v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
            if (v.ValueKind == JsonValueKind.Number)
                return v.GetRawText();
        }
        return "";
    }

    public static int Number(JsonElement node, params string[] names)
    {
        string v = Text(node, names);
        return int.TryParse(v, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int result)
                ? result : 0;
    }

    public static JsonElement First(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array && node.GetArrayLength() > 0)
            return node[0];
        return default;
    }

    public static async Task<byte[]> ReadBoundedAsync(
        HttpClient http, HttpRequestMessage request, int maxBytes,
        CancellationToken ct)
    {
        using HttpResponseMessage response = await http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException("Provider response exceeds the configured size limit.");
        await using Stream input = await response.Content.ReadAsStreamAsync(ct);
        using MemoryStream output = new();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > maxBytes)
                throw new InvalidDataException("Provider response exceeds the configured size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public static async Task<JsonDocument> JsonAsync(
        HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        byte[] payload = await ReadBoundedAsync(http, request, 2 * 1024 * 1024, ct);
        return JsonDocument.Parse(payload);
    }

    public static string DecodeText(byte[] raw)
    {
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            raw = raw[3..];
        try
        {
            return new UTF8Encoding(false, true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            // Do not replace bytes with '?' or silently destroy multilingual lyrics.
            // A subsequent opt-in GB18030 decoding strategy requires actual fixtures.
            throw new InvalidDataException(
                "Provider lyric is not valid UTF-8; legacy encoding needs a verified decoder.");
        }
    }

    public static bool ProviderHttpsUrl(string? text, string rootDomain,
        out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? candidate)
            || candidate.Scheme != Uri.UriSchemeHttps
            || !candidate.IsDefaultPort || candidate.UserInfo.Length > 0
            || candidate.HostNameType != UriHostNameType.Dns)
            return false;
        string host = candidate.IdnHost;
        if (!host.Equals(rootDomain, StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith("." + rootDomain, StringComparison.OrdinalIgnoreCase))
            return false;
        uri = candidate;
        return true;
    }

    public static void RequireSameProvider(ProviderTrack track, ExternalProvider expected)
    {
        if (track.Provider != expected)
            throw new ArgumentException("Track belongs to a different provider.", nameof(track));
    }
}
