namespace TerraTeklaServer.Printing;

/// <summary>
/// Character widths of the PDF standard fonts the ticket uses (Helvetica, Helvetica-Bold, Courier),
/// from Adobe's font metrics, in 1/1000 of the font size. Arial and Courier New have the same
/// widths, so a ticket laid out with these comes out the same through Windows' printing.
/// Text is kept to Windows-1252 (what the PDF fonts can show); anything else prints as "?", except
/// a tick (✓), which is drawn as lines.
/// </summary>
public static class Fonts
{
    // ' ' (32) to '~' (126).
    static readonly short[] Helvetica =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    static readonly short[] HelveticaBold =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584,
    ];

    /// <summary>A tick's width. It is drawn as two strokes (<see cref="TickPoints"/>), not from a font.</summary>
    public const double TickWidth = 800;

    /// <summary>The three points of a tick drawn at <paramref name="x"/> on a baseline.</summary>
    public static (double X, double Y)[] TickPoints(double x, double baseline, double size)
    {
        var w = TickWidth * size / 1000;
        return [(x + w * 0.12, baseline - size * 0.38), (x + w * 0.4, baseline - size * 0.05), (x + w * 0.9, baseline - size * 0.72)];
    }

    public static bool IsTick(char c) => c is '✓' or '✔' or '☑';

    // Windows-1252 bytes 0x80-0x9F for the characters that have one.
    static readonly Dictionary<char, byte> Cp1252 = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88,
        ['‰'] = 0x89, ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93,
        ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97, ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B,
        ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    /// <summary>The character as it will print: itself, or "?" when the fonts can't show it.</summary>
    public static char Printable(char c) =>
        IsTick(c) || c is >= ' ' and <= '~' or >= ' ' and <= 'ÿ' || Cp1252.ContainsKey(c) ? c : c == '−' ? '-' : '?';

    /// <summary>The Windows-1252 byte for a printable character (see <see cref="Printable"/>).</summary>
    public static byte WinAnsi(char c)
    {
        c = Printable(c);
        if (c <= 'ÿ')
            return (byte)c;
        return Cp1252.TryGetValue(c, out var b) ? b : (byte)'?';
    }

    /// <summary>Text cut into runs of normal text and of ticks (drawn as lines).</summary>
    public static IEnumerable<(string Text, bool Tick)> Runs(string text)
    {
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
            if (i == text.Length || IsTick(text[i]) != IsTick(text[start]))
            {
                yield return (text[start..i], IsTick(text[start]));
                start = i;
            }
    }

    static double CharWidth(char c, TicketFont font)
    {
        c = Printable(c);
        if (IsTick(c))
            return TickWidth;
        if (font is TicketFont.Mark or TicketFont.MarkManual)
            return 600;
        var table = font == TicketFont.Bold ? HelveticaBold : Helvetica;
        if (c is >= ' ' and <= '~')
            return table[c - ' '];
        return c switch
        {
            '…' or '‰' or '—' => 1000,
            '–' => 556,
            '×' or '÷' or '±' => 584,
            '·' or '•' => font == TicketFont.Bold ? 350 : 278,
            '°' => 400,
            ' ' => 278,
            >= 'À' and <= 'Ö' or >= 'Ø' and <= 'Þ' => 722,
            _ => 556,
        };
    }

    public static double Width(string text, TicketFont font, double size) =>
        text.Sum(c => CharWidth(c, font)) * size / 1000;

    /// <summary>The text, cut short with "…" if it is wider than <paramref name="room"/>.</summary>
    public static string Fit(string text, TicketFont font, double size, double room)
    {
        if (Width(text, font, size) <= room)
            return text;
        var ell = Width("…", font, size);
        var w = 0.0;
        var i = 0;
        for (; i < text.Length; i++)
        {
            var cw = CharWidth(text[i], font) * size / 1000;
            if (w + cw + ell > room)
                break;
            w += cw;
        }
        return i == 0 ? "" : text[..i].TrimEnd() + "…";
    }

    /// <summary>Wraps text into lines no wider than <paramref name="room"/>, at spaces, or inside a word that is too long.</summary>
    public static List<string> Wrap(string text, TicketFont font, double size, double room)
    {
        var lines = new List<string>();
        if (text.Trim() == "")
            return lines;
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line == "" ? word : line + " " + word;
            if (Width(candidate, font, size) <= room)
            {
                line = candidate;
                continue;
            }
            if (line != "")
                lines.Add(line);
            line = word;
            // A word wider than the column is broken where it reaches the edge.
            while (Width(line, font, size) > room && line.Length > 1)
            {
                var cut = 1;
                while (cut < line.Length && Width(line[..(cut + 1)], font, size) <= room)
                    cut++;
                lines.Add(line[..cut]);
                line = line[cut..];
            }
        }
        if (line != "")
            lines.Add(line);
        return lines;
    }
}
