using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MusicLoom.LyricsProviders;

/// <summary>
/// Non-commercial client-style request envelopes adapted to current
/// community-documented QQ/KuGou endpoints. No paid API keys or accounts.
/// Signed client requests can break when providers change their protocols.
/// </summary>
public sealed partial class CloudLyricsClient
{
    private const string KugouSalt = "LnT6xpN3khm36zse0QzvmgTZ3waWdRSA";
    private readonly SemaphoreSlim _qqSessionGate = new(1, 1);
    private bool _qqSessionReady;
    private string _qqUid = "0";
    private string _qqSid = "";
    private string _qqUserIp = "";

    private async Task EnsureQqSessionAsync(CancellationToken ct)
    {
        if (_qqSessionReady)
        {
            return;
        }

        await _qqSessionGate.WaitAsync(ct);
        try
        {
            if (_qqSessionReady)
            {
                return;
            }

            string raw = await PostQqAsync(
                BuildQqPacket("GetSession", "music.getSession.session",
                    """{"caller":0,"uid":"0","vkey":0}""",
                    uid: "0", sid: "", userIp: ""),
                ct);
            using JsonDocument json = JsonDocument.Parse(raw);
            JsonElement data = QqData(json.RootElement);
            if (!Find(data, "session", out JsonElement session))
            {
                throw new InvalidDataException(
                    "QQ Music session is missing from client response.");
            }

            _qqUid = Str(session, "uid");
            _qqSid = Str(session, "sid");
            _qqUserIp = Str(session, "userip");
            if (_qqUid.Length == 0 || _qqSid.Length == 0)
            {
                throw new InvalidDataException(
                    "QQ Music session did not provide uid/sid.");
            }

            _qqSessionReady = true;
        }
        finally
        {
            _qqSessionGate.Release();
        }
    }

    private async Task<string> QqRequestAsync(
        string method, string module, string param,
        CancellationToken ct)
    {
        await EnsureQqSessionAsync(ct);
        return await PostQqAsync(
            BuildQqPacket(method, module, param,
                _qqUid, _qqSid, _qqUserIp),
            ct);
    }

    private async Task<string> PostQqAsync(
        string json, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, QqApi);
        request.Content =
            new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.UserAgent.ParseAdd("okhttp/3.14.9");
        request.Headers.TryAddWithoutValidation("Cookie", "tmeLoginType=-1;");
        return await GetBodyAsync(request, ct);
    }

    internal static string BuildQqPacket(
        string method, string module, string param,
        string uid = "0", string sid = "", string userIp = "")
    {
        // Compact literal envelope avoids reflection-based serializer
        // dependencies in the NativeAOT regression harness.
        return "{\"comm\":{\"ct\":11,\"cv\":\"1003006\","
            + "\"v\":\"1003006\",\"os_ver\":\"15\","
            + "\"phonetype\":\"24122RKC7C\",\"rom\":\"Android15\","
            + "\"tmeAppID\":\"qqmusiclight\",\"nettype\":\"NETWORK_WIFI\","
            + "\"udid\":\"0\",\"uid\":" + J(uid)
            + ",\"sid\":" + J(sid)
            + ",\"userip\":" + J(userIp)
            + "},\"request\":{\"method\":" + J(method)
            + ",\"module\":" + J(module)
            + ",\"param\":" + param + "}}";
    }

    private static string J(string value) =>
        "\"" + JsonEncodedText.Encode(value).ToString() + "\"";

    internal static JsonElement QqData(JsonElement root)
    {
        if (Find(root, "request", out JsonElement request))
        {
            int code = Int(root, "code");
            int subcode = Int(request, "code");
            if (code != 0 || subcode != 0)
            {
                throw new InvalidDataException(
                    $"QQ Music client API returned code {code}/{subcode}.");
            }
            if (Find(request, "data", out JsonElement data))
            {
                return data;
            }
        }

        if (Find(root, "music.search.SearchCgiService",
                out JsonElement legacySearch)
            && Find(legacySearch, "data", out JsonElement legacyData))
        {
            return legacyData;
        }
        throw new InvalidDataException(
            "Unrecognized QQ Music client response.");
    }

    internal static string KugouSignature(
        IReadOnlyDictionary<string, string> values,
        string? body = null)
    {
        string canonical = string.Concat(values
            .OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => item.Key + "=" + item.Value));
        byte[] bytes = Encoding.UTF8.GetBytes(
            KugouSalt + canonical + (body ?? "") + KugouSalt);
        return Convert.ToHexString(MD5.HashData(bytes))
            .ToLowerInvariant();
    }

    internal static string BuildKugouUrl(
        string baseUrl,
        IReadOnlyDictionary<string, string> parameters)
    {
        return baseUrl + "?" + string.Join("&", parameters
            .Select(item => Uri.EscapeDataString(item.Key) + "="
                + Uri.EscapeDataString(item.Value)));
    }

    private async Task<string> KugouRequestAsync(
        string endpoint,
        string module,
        IReadOnlyDictionary<string, string> provided,
        CancellationToken ct)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string mid = Convert.ToHexString(MD5.HashData(
            Encoding.UTF8.GetBytes(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    .ToString(CultureInfo.InvariantCulture))))
            .ToLowerInvariant();

        Dictionary<string, string> parameters;
        if (module == "Lyric")
        {
            parameters = new(StringComparer.Ordinal)
            {
                ["appid"] = "3116",
                ["clientver"] = "11070",
            };
        }
        else
        {
            parameters = new(StringComparer.Ordinal)
            {
                ["userid"] = "0",
                ["appid"] = "3116",
                ["token"] = "",
                ["clienttime"] = now.ToString(CultureInfo.InvariantCulture),
                ["iscorrection"] = "1",
                ["uuid"] = "-",
                ["mid"] = mid,
                ["dfid"] = "-",
                ["clientver"] = "11070",
                ["platform"] = "AndroidFilter",
            };
        }

        foreach ((string key, string value) in provided)
        {
            parameters[key] = value;
        }
        parameters["signature"] = KugouSignature(parameters);
        string url = BuildKugouUrl(endpoint, parameters);

        using HttpRequestMessage request =
            new(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Android14-1070-11070-201-0-" + module + "-wifi");
        request.Headers.TryAddWithoutValidation("KG-Rec", "1");
        request.Headers.TryAddWithoutValidation("KG-RC", "1");
        request.Headers.TryAddWithoutValidation(
            "KG-CLIENTTIMEMS",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(
                CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("mid", mid);
        if (endpoint.Contains("complexsearch.kugou.com",
                StringComparison.Ordinal))
        {
            request.Headers.TryAddWithoutValidation(
                "x-router", "complexsearch.kugou.com");
        }

        string response = await GetBodyAsync(request, ct);
        using JsonDocument json = JsonDocument.Parse(response);
        int code = Int(json.RootElement, "error_code");
        if (code is not 0 and not 200)
        {
            throw new InvalidDataException(
                $"KuGou client API returned {code}.");
        }
        return response;
    }
}
