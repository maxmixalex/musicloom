using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MusicLoom.Core.Lyrics;

namespace MusicLoom.LyricsProviders;

/// <summary>
/// Non-official QQ QRC and KuGou KRC containers. The transport/cipher
/// formats are documented by community projects such as LDDC; this is an
/// independent implementation and requires no commercial cloud service.
/// </summary>
public static partial class TimedLyricsCodec
{
    private static readonly byte[] QrcKey =
        Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL");

    private static readonly byte[] KrcKey =
    [
        0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47,
        0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69,
    ];

    private const int MaxPlaintextBytes = 4 * 1024 * 1024;

    /// <summary>Decrypt the hex-encoded, 3DES-ECB/zlib cloud QRC.</summary>
    public static string DecryptQrc(string encryptedHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptedHex);
        byte[] ciphertext = Convert.FromHexString(encryptedHex.Trim());
        if (ciphertext.Length == 0 || ciphertext.Length % 8 != 0)
        {
            throw new InvalidDataException("Invalid QRC ciphertext length.");
        }

        using TripleDES cipher = TripleDES.Create();
        cipher.Key = QrcKey;
        cipher.Mode = CipherMode.ECB;
        byte[] plain = cipher.DecryptEcb(ciphertext, PaddingMode.None);
        return Inflate(plain);
    }

    /// <summary>Decrypt a binary KRC1 with fixed-key XOR and zlib.</summary>
    public static string DecryptKrc(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5
            || data[0] != (byte)'k'
            || data[1] != (byte)'r'
            || data[2] != (byte)'c'
            || data[3] != (byte)'1')
        {
            throw new InvalidDataException("Invalid KuGou KRC header.");
        }

        byte[] compressed = data[4..].ToArray();
        for (int i = 0; i < compressed.Length; i++)
        {
            compressed[i] ^= KrcKey[i % KrcKey.Length];
        }

        return Inflate(compressed);
    }

    private static string Inflate(byte[] compressed)
    {
        using MemoryStream data = new(compressed, writable: false);
        using ZLibStream zlib = new(data, CompressionMode.Decompress);
        using MemoryStream output = new();
        byte[] buffer = new byte[16_384];

        while (true)
        {
            int read = zlib.Read(buffer);
            if (read == 0)
            {
                break;
            }
            if (output.Length + read > MaxPlaintextBytes)
            {
                throw new InvalidDataException(
                    "Decoded lyrics exceed the safe maximum size.");
            }
            output.Write(buffer, 0, read);
        }

        return new UTF8Encoding(false, true).GetString(output.ToArray());
    }

    /// <summary>
    /// Parse QQ's LyricContent XML into an enhanced LRC document, retaining
    /// word start/end times. Ordinary QRC plain LRC remains supported.
    /// </summary>
    public static LrcDocument ParseQrc(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        string? embedded = ExtractQrcAttribute(plaintext);
        if (embedded is null)
        {
            return LrcDocument.Parse(plaintext);
        }

        LrcDocument result = new();
        foreach (string raw in embedded.Split(['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (TryHeader(line, out string? name, out string? value))
            {
                result.Metadata[name!] = value!;
                continue;
            }
            Match match = TimedLineRegex().Match(line);
            if (!match.Success
                || !TryInteger(match.Groups["start"], out int start)
                || !TryInteger(match.Groups["duration"], out int duration))
            {
                continue;
            }
            string content = match.Groups["text"].Value;
            List<LrcWord> words = [];
            int endOfLast = 0;
            foreach (Match word in QrcWordRegex().Matches(content))
            {
                if (!TryInteger(word.Groups["start"], out int wordStart)
                    || !TryInteger(word.Groups["duration"], out int wordLength))
                {
                    continue;
                }
                string text = word.Groups["text"].Value;
                int begin = Math.Max(0, wordStart);
                int end = checked(begin + Math.Max(0, wordLength));
                words.Add(new LrcWord(
                    TimeSpan.FromMilliseconds(begin),
                    TimeSpan.FromMilliseconds(end),
                    text));
                endOfLast = word.Index + word.Length;
            }

            // QQ timestamps are absolute (not relative to line start).
            string trailing = content[endOfLast..];
            if (words.Count > 0 && trailing.Length > 0)
            {
                words[^1] = words[^1] with
                {
                    Text = words[^1].Text + trailing,
                };
            }
            string plain = words.Count > 0
                ? string.Concat(words.Select(static w => w.Text))
                : content;
            if (plain.Length == 0)
            {
                continue;
            }

            result.Lines.Add(new LrcLine(
                TimeSpan.FromMilliseconds(Math.Max(0, start)),
                plain,
                words.Count > 0 ? words : null));
        }
        return result;
    }

    /// <summary>
    /// Parse KuGou's [absolute,lineDuration] and
    /// &lt;relativeStart,wordDuration,flag&gt; markers. Embedded language
    /// JSON can supply romanization and aligned translations.
    /// </summary>
    public static LrcDocument ParseKrc(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        LrcDocument result = new();
        string? language = null;
        foreach (string raw in plaintext.Split(['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (TryHeader(line, out string? name, out string? value))
            {
                if (name!.Equals("language", StringComparison.OrdinalIgnoreCase))
                {
                    language = value;
                }
                else
                {
                    result.Metadata[name] = value!;
                }
                continue;
            }
            Match timed = TimedLineRegex().Match(line);
            if (!timed.Success
                || !TryInteger(timed.Groups["start"], out int start)
                || !TryInteger(timed.Groups["duration"], out int duration))
            {
                continue;
            }

            List<LrcWord> words = [];
            string original = timed.Groups["text"].Value;
            int rawCursor = 0;
            foreach (Match mark in KrcWordRegex().Matches(original))
            {
                if (!TryInteger(mark.Groups["start"], out int relative)
                    || !TryInteger(mark.Groups["duration"], out int length))
                {
                    continue;
                }
                // Preserve any literal text before the first marker.
                string prefix = original[rawCursor..mark.Index];
                string text = prefix + mark.Groups["text"].Value;
                int begin = checked(start + relative);
                int end = checked(begin + Math.Max(0, length));
                words.Add(new LrcWord(
                    TimeSpan.FromMilliseconds(Math.Max(0, begin)),
                    TimeSpan.FromMilliseconds(Math.Max(0, end)),
                    text));
                rawCursor = mark.Index + mark.Length;
            }
            if (words.Count > 0 && rawCursor < original.Length)
            {
                words[^1] = words[^1] with
                {
                    Text = words[^1].Text + original[rawCursor..],
                };
            }
            string textLine = words.Count > 0
                ? string.Concat(words.Select(static word => word.Text))
                : original;
            if (textLine.Length == 0)
            {
                continue;
            }

            result.Lines.Add(new LrcLine(
                TimeSpan.FromMilliseconds(Math.Max(0, start)),
                textLine,
                words.Count > 0 ? words : null));
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            AddKrcLanguages(result, language);
        }
        // Original, romanization, translation retain stable ordering for
        // the existing bilingual LrcPresentation projection.
        return result;
    }

    private static void AddKrcLanguages(LrcDocument result, string base64)
    {
        try
        {
            using JsonDocument payload = JsonDocument.Parse(
                Convert.FromBase64String(base64));
            if (!payload.RootElement.TryGetProperty(
                    "content", out JsonElement languages)
                || languages.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            LrcLine[] originals = [.. result.Lines];
            List<(int Kind, JsonElement Lines)> source = [];
            foreach (JsonElement lang in languages.EnumerateArray())
            {
                if (!lang.TryGetProperty("type", out JsonElement type)
                    || !type.TryGetInt32(out int kind)
                    || !lang.TryGetProperty("lyricContent", out JsonElement data)
                    || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                source.Add((kind, data));
            }

            int nonempty = 0;
            for (int i = 0; i < originals.Length; i++)
            {
                LrcLine original = originals[i];
                foreach ((int kind, JsonElement lines) in source)
                {
                    // For the romanization case KuGou skips empty lyric
                    // lines, while translation arrays retain line indices.
                    int index = kind == 0 ? nonempty : i;
                    if (index >= lines.GetArrayLength()
                        || lines[index].ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    string translation = kind switch
                    {
                        0 => string.Concat(lines[index].EnumerateArray()
                            .Where(static word => word.ValueKind == JsonValueKind.String)
                            .Select(static word => word.GetString())),
                        1 => lines[index].GetArrayLength() > 0
                            && lines[index][0].ValueKind == JsonValueKind.String
                            ? lines[index][0].GetString() ?? "" : "",
                        _ => "",
                    };
                    if (!string.IsNullOrWhiteSpace(translation)
                        && !string.Equals(translation, original.Text,
                            StringComparison.Ordinal))
                    {
                        result.Lines.Add(new LrcLine(
                            original.Timestamp, translation));
                    }
                }
                if (!string.IsNullOrWhiteSpace(original.Text))
                {
                    nonempty++;
                }
            }

            LrcLine[] reordered = result.Lines
                .OrderBy(static line => line.Timestamp)
                .ToArray();
            result.Lines.Clear();
            result.Lines.AddRange(reordered);
        }
        catch (JsonException)
        {
            // Malformed auxiliary data must not destroy good original lyrics.
        }
        catch (FormatException)
        {
            // Malformed base64 in the optional language tag.
        }
    }

    private static string? ExtractQrcAttribute(string xml)
    {
        if (!xml.Contains("<Lyric_1", StringComparison.Ordinal))
        {
            return null;
        }
        try
        {
            XDocument parsed = XDocument.Parse(xml);
            return parsed.Descendants("Lyric_1")
                .FirstOrDefault()?.Attribute("LyricContent")?.Value;
        }
        catch (System.Xml.XmlException)
        {
            // Some QQ responses contain standalone partial XML fragments.
            Match fallback = QrcAttributeRegex().Match(xml);
            return fallback.Success
                ? WebUtility.HtmlDecode(fallback.Groups["content"].Value)
                : null;
        }
    }

    private static bool TryHeader(
        string line, out string? name, out string? value)
    {
        name = value = null;
        Match match = HeaderRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }
        name = match.Groups["name"].Value;
        value = match.Groups["value"].Value;
        return true;
    }

    private static bool TryInteger(Group group, out int value) =>
        int.TryParse(group.Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out value);

    [GeneratedRegex(
        @"^\[(?<start>\d+),(?<duration>\d+)\](?<text>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimedLineRegex();

    [GeneratedRegex(
        @"(?<text>.*?)\((?<start>\d+),(?<duration>\d+)\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex QrcWordRegex();

    [GeneratedRegex(
        @"<(?<start>\d+),(?<duration>\d+),\d+>(?<text>[^<]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex KrcWordRegex();

    [GeneratedRegex(
        @"^\[(?<name>[a-zA-Z]+):(?<value>.*)\]$",
        RegexOptions.CultureInvariant)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(
        "LyricContent=\"(?<content>.*?)\"",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex QrcAttributeRegex();
}
