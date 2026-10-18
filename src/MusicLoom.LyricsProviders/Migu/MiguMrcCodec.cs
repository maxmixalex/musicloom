using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MusicLoom.Core.Lyrics;

namespace MusicLoom.LyricsProviders.Migu;

/// <summary>
/// Independent format implementation based on observed MRC blocks and the
/// published MIT protocol example. Not copied from Lyrimuse GPL source.
/// Hex words are signed 64-bit, processed with wraparound and arithmetic shifts.
/// </summary>
public static partial class MiguMrcCodec
{
    private const long Delta = 0x9E3779B9;
    private static readonly long[] Key =
    [
        27303562373562475L, 18014862372307051L,
        22799692160172081L, 34058940340699235L,
    ];

    public static string Decrypt(string cipherHex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cipherHex);
        cipherHex = cipherHex.Trim();
        if (cipherHex.Length is < 32 or > 2_097_152
            || cipherHex.Length % 16 != 0)
            throw new InvalidDataException("Invalid Migu MRC block length.");
        int count = cipherHex.Length / 16;
        long[] state = new long[count];
        for (int i = 0; i < count; i++)
            state[i] = unchecked((long)ulong.Parse(
                cipherHex.AsSpan(i * 16, 16),
                NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        long counter = unchecked((6L + 52L / count) * Delta);
        long previous = state[0];
        while (counter != 0)
        {
            long selector = (counter >> 2) & 3;
            for (int i = count - 1; i > 0; i--)
            {
                long neighbour = state[i - 1];
                state[i] = unchecked(state[i]
                    - Mix(counter, previous, neighbour, i, selector));
                previous = state[i];
            }
            long last = state[count - 1];
            state[0] = unchecked(state[0]
                - Mix(counter, previous, last, 0, selector));
            previous = state[0];
            counter = unchecked(counter - Delta);
        }

        byte[] bytes = new byte[count * 8];
        for (int i = 0; i < count; i++)
            BinaryPrimitives.WriteInt64LittleEndian(
                bytes.AsSpan(i * 8, 8), state[i]);
        string decoded = new UnicodeEncoding(false, false, true).GetString(bytes);
        // Remove only unmistakable block padding AFTER a terminal newline.
        // Blind TrimEnd('0') would silently delete a legitimate final lyric
        // digit (e.g. a song ending in "2020"). No guessed text cleanup.
        decoded = decoded.TrimEnd('\0');
        int lastLine = decoded.LastIndexOf('\n');
        if (lastLine >= 0 && decoded[(lastLine + 1)..].All(
            static ch => ch == '0'))
            return decoded[..(lastLine + 1)];
        return decoded;
    }

    private static long Mix(long count, long right, long left, int index, long selector)
    {
        unchecked
        {
            long a = ((left >> 5) ^ (right << 2))
                   + ((right >> 3) ^ (left << 4));
            long b = (count ^ right)
                   + (Key[(int)((index & 3) ^ selector)] ^ left);
            return a ^ b;
        }
    }

    /// <summary>
    /// Keep ALL timed rows (including [0,0] title, performer and credits).
    /// Original lines and words are not filtered based on their text.
    /// </summary>
    public static LrcDocument Parse(string mrcPlaintext)
    {
        LrcDocument output = new();
        foreach (string raw in mrcPlaintext.Replace("\r\n", "\n")
            .Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length == 0) continue;
            Match header = TimedLine().Match(raw);
            if (!header.Success)
            {
                LrcDocument embedded = LrcDocument.Parse(raw);
                foreach ((string key, string value) in embedded.Metadata)
                    output.Metadata[key] = value;
                continue;
            }
            if (!int.TryParse(header.Groups["start"].Value, out int start)
                || !int.TryParse(header.Groups["duration"].Value, out _))
                continue;

            string body = header.Groups["body"].Value;
            List<LrcWord> words = [];
            int previousEnd = 0;
            foreach (Match match in WordMark().Matches(body))
            {
                if (!int.TryParse(match.Groups["start"].Value, out int wordStart)
                    || !int.TryParse(match.Groups["duration"].Value, out int duration))
                    continue;
                string glyph = body[previousEnd..match.Index];
                if (glyph.Length > 0)
                {
                    int bounded = Math.Max(0, wordStart);
                    words.Add(new LrcWord(
                        TimeSpan.FromMilliseconds(bounded),
                        TimeSpan.FromMilliseconds(
                            (long)bounded + Math.Max(0, duration)),
                        glyph));
                }
                previousEnd = match.Index + match.Length;
            }
            string tail = body[previousEnd..];
            if (tail.Length > 0 && words.Count > 0)
            {
                LrcWord last = words[^1];
                words[^1] = last with { Text = last.Text + tail };
            }
            string plain = words.Count > 0
                ? string.Concat(words.Select(static word => word.Text))
                : body;
            output.Lines.Add(new LrcLine(
                TimeSpan.FromMilliseconds(Math.Max(0, start)),
                plain, words.Count == 0 ? null : words.ToArray()));
        }
        return output;
    }

    [GeneratedRegex(@"^\[(?<start>\d+),(?<duration>\d+)\](?<body>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TimedLine();

    [GeneratedRegex(@"\((?<start>\d+),(?<duration>\d+)\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex WordMark();
}
