using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using MusicLoom.Core.Lyrics;

namespace MusicLoom.ProviderPoc;

/// <summary>
/// Timed Text Markup Language parser. Preserves source text and song/artist
/// credits exactly; it neither invents a translation nor discards title rows.
/// </summary>
public static class AppleTtmlCodec
{
    public static LrcDocument Parse(string ttml)
    {
        if (string.IsNullOrWhiteSpace(ttml)) return new LrcDocument();
        if (ttml.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("TTML exceeds the safe input limit.");
        using StringReader input = new(ttml);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 2 * 1024 * 1024,
        });
        XDocument tree = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        LrcDocument document = new();

        foreach (XElement line in tree.Descendants()
            .Where(static element => element.Name.LocalName == "p"))
        {
            XElement[] spans = line.Descendants()
                .Where(static element => element.Name.LocalName == "span")
                .ToArray();
            string original = string.Concat(line.Nodes().Select(node => node switch
            {
                XText text => text.Value,
                XElement element => element.Value,
                _ => "",
            }));
            if (original.Length == 0) continue;

            TimeSpan? start = TimeAttribute(line, "begin");
            TimeSpan? end = TimeAttribute(line, "end");
            List<LrcWord> words = [];
            foreach (XElement span in spans)
            {
                TimeSpan? wordStart = TimeAttribute(span, "begin");
                TimeSpan? wordEnd = TimeAttribute(span, "end");
                if (wordStart is null || span.Value.Length == 0) continue;
                TimeSpan from = wordStart.Value;
                words.Add(new LrcWord(from,
                    wordEnd is TimeSpan stop && stop >= from ? stop : from,
                    span.Value));
            }

            start ??= words.Count > 0 ? words[0].Start : null;
            if (start is null) continue; // No invented timestamp for plain text.
            if (end is TimeSpan last && words.Count > 0
                && words[^1].End == words[^1].Start && last >= words[^1].Start)
                words[^1] = words[^1] with { End = last };
            IReadOnlyList<LrcWord>? aligned =
                words.Count > 0
                && string.Concat(words.Select(static w => w.Text)) == original
                    ? words.ToArray() : null;

            document.Lines.Add(new LrcLine(start.Value, original, aligned));
        }
        return document;
    }

    private static TimeSpan? TimeAttribute(XElement element, string name)
    {
        string? raw = element.Attributes()
            .FirstOrDefault(x => x.Name.LocalName == name)?.Value;
        return TryParseClock(raw, out TimeSpan time) ? time : null;
    }

    public static bool TryParseClock(string? raw, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string value = raw.Trim().Replace(',', '.');
        if (value.EndsWith('s'))
        {
            if (!double.TryParse(value[..^1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double seconds)
                || !double.IsFinite(seconds) || seconds < 0)
                return false;
            time = TimeSpan.FromSeconds(seconds);
            return true;
        }
        string[] components = value.Split(':');
        if (components.Length is < 1 or > 3) return false;
        double count = 0;
        foreach (string part in components)
        {
            if (!double.TryParse(part, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double v)
                || !double.IsFinite(v) || v < 0)
                return false;
            count = count * 60 + v;
        }
        if (!double.IsFinite(count) || count > TimeSpan.MaxValue.TotalSeconds)
            return false;
        time = TimeSpan.FromSeconds(count);
        return true;
    }
}
