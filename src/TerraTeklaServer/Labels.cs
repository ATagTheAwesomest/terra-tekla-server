using System.Globalization;
using System.Net;
using System.Text;

namespace TerraTeklaServer;

// Piece labels on the Avery 5160 sheet (3 x 10, 2-5/8 in x 1 in), made by the server so the admin
// board can print them (GET /api/admin/jobs/{job}/labels). Each label: a Code 128 barcode of the
// piece's code at the top, the value under it, then the sequence and mark, "Qty n of N", the weight
// and the job. The sheet is positioned in inches, so it prints at 100 % on the label stock.

/// <summary>What one label prints. <c>Index</c> of <c>Of</c>: this piece among the pieces of its mark and sequence.</summary>
public sealed record PieceLabel(string Code, string Mark, string Sequence, int Index, int Of, string? Description, double? Weight, string Job);

public static class Labels
{
    // Code 128 bar and space widths for values 0-105 (START B is 104, START C 105), the checksum is
    // the weighted sum mod 103, and STOP is 106. Taken from the standard's table.
    static readonly string[] Patterns = ["212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213", "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132", "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211", "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313", "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331", "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111", "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214", "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111", "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141", "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141", "114131", "311141", "411131", "211412", "211214", "211232"];
    const string Stop = "2331112";
    const int StartB = 104;

    public const int PerSheet = 30;

    /// <summary>
    /// Labels for a job's codes, optionally for one mark (and one sequence). Ordered by mark, then
    /// sequence, then code. Index/Of count the pieces of that mark and sequence.
    /// </summary>
    public static List<PieceLabel> For(string job, IEnumerable<PieceCode> codes, string? mark = null, string? sequence = null)
    {
        var groups = codes
            .GroupBy(c => (Mark: CodeList.CleanMark(c.Mark), Seq: c.Sequence?.Trim() ?? ""))
            .OrderBy(g => g.Key.Mark, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.Seq, StringComparer.Ordinal);
        var labels = new List<PieceLabel>();
        foreach (var g in groups)
        {
            if (mark is not null && !g.Key.Mark.Equals(CodeList.CleanMark(mark), StringComparison.OrdinalIgnoreCase)) continue;
            if (sequence is not null && g.Key.Seq != sequence.Trim()) continue;
            var members = g.OrderBy(c => c.Code, StringComparer.Ordinal).ToList();
            for (var i = 0; i < members.Count; i++)
                labels.Add(new PieceLabel(members[i].Code, g.Key.Mark, g.Key.Seq, i + 1, members.Count, members[i].Description, members[i].Weight, job));
        }
        return labels;
    }

    /// <summary>The module widths of a Code 128 (set B) symbol for <paramref name="text"/>, bars first.</summary>
    public static List<int> Modules(string text)
    {
        var values = new List<int> { StartB };
        foreach (var ch in text)
            values.Add(ch is >= ' ' and <= '~' ? ch - 32 : '?' - 32);
        var sum = StartB;
        for (var i = 1; i < values.Count; i++) sum += i * values[i];
        values.Add(sum % 103);
        var widths = new List<int>();
        foreach (var v in values)
            foreach (var d in Patterns[v]) widths.Add(d - '0');
        foreach (var d in Stop) widths.Add(d - '0');
        return widths;
    }

    /// <summary>An inline SVG of the barcode's bars (the value is printed as text under it).</summary>
    public static string BarcodeSvg(string text)
    {
        var widths = Modules(text);
        var sb = new StringBuilder();
        sb.Append($"<svg class=\"bars\" viewBox=\"0 0 {widths.Sum()} 10\" preserveAspectRatio=\"none\" role=\"img\" aria-label=\"{Esc(text)}\">");
        var x = 0;
        for (var i = 0; i < widths.Count; i++)
        {
            if (i % 2 == 0) sb.Append($"<rect x=\"{x}\" y=\"0\" width=\"{widths[i]}\" height=\"10\"/>");
            x += widths[i];
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>
    /// The printable document. <paramref name="skip"/> are positions (0-based) on the first sheet that are
    /// already used: the labels start at the first free one, so a half-used sheet isn't wasted.
    /// </summary>
    public static string Sheet(string job, IReadOnlyList<PieceLabel> labels, ISet<int> skip, string title)
    {
        var slots = new List<PieceLabel?>();
        for (var s = 0; s < PerSheet; s++)
            if (skip.Contains(s)) slots.Add(null);
        var queue = new Queue<PieceLabel>(labels);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>{Esc(title)}</title><style>{Css}</style></head><body>");
        sb.Append($"<p class=\"noprint bar\">{labels.Count} label{(labels.Count == 1 ? "" : "s")} for job {Esc(job)} · Avery 5160 · print at 100 %, no fit to page <button onclick=\"print()\">Print</button></p>");
        var first = true;
        while (queue.Count > 0)
        {
            var used = first ? skip : new HashSet<int>();
            sb.Append("<section class=\"sheet\">");
            for (var slot = 0; slot < PerSheet; slot++)
            {
                if (used.Contains(slot)) continue;
                if (queue.Count == 0) break;
                var l = queue.Dequeue();
                var col = slot % 3;
                var row = slot / 3;
                sb.Append($"<div class=\"lab\" style=\"left:{Inch(0.1875 + col * 2.75)};top:{Inch(0.5 + row * 1.0)}\">");
                sb.Append(BarcodeSvg(l.Code));
                sb.Append($"<div class=\"val\">{Esc(l.Code)}</div>");
                sb.Append($"<div class=\"desc\">{Esc(l.Description ?? "")}</div>");
                sb.Append($"<div class=\"row\"><b>{Esc(l.Sequence)}</b> <span>{Esc(l.Mark)}</span></div>");
                var weight = l.Weight is { } kg ? Math.Round(kg).ToString(CultureInfo.InvariantCulture) + "#" : "—";
                sb.Append($"<div class=\"row\"><span>Qty {l.Index} of {l.Of}</span> <span>W {weight}</span> <span>JOB {Esc(l.Job)}</span></div>");
                sb.Append("</div>");
            }
            sb.Append("</section>");
            first = false;
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    const string Css = """
        @page { size: letter; margin: 0; }
        * { box-sizing: border-box; }
        html, body { margin: 0; background: #fff; color: #000; font-family: Arial, Helvetica, sans-serif; }
        .bar { margin: 8px 12px; font-size: 14px; }
        .bar button { font: inherit; margin-left: 8px; }
        .sheet { position: relative; width: 8.5in; height: 11in; page-break-after: always; overflow: hidden; }
        .sheet:last-of-type { page-break-after: auto; }
        .lab { position: absolute; width: 2.625in; height: 1in; padding: 0.07in 0.12in 0 0.12in; overflow: hidden; }
        .bars { display: block; width: 2.4in; height: 0.36in; }
        .bars rect { fill: #000; }
        .val { font: 7pt/1 "Courier New", monospace; text-align: center; height: 0.1in; }
        .desc, .row { font-size: 7.5pt; line-height: 0.125in; height: 0.125in; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .row span + span { margin-left: 0.08in; }
        .row b { font-size: 8pt; }
        @media print { .noprint { display: none; } }
        """;

    static string Inch(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture) + "in";

    static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");
}
