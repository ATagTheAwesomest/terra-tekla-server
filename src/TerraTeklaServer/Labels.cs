using System.Globalization;
using System.Net;
using System.Text;

namespace TerraTeklaServer;

// Piece labels on the Avery 5160 sheet (3 x 10, 2-5/8 in x 1 in), made by the server so the admin
// board's Labels tab can print them without PowerFab. Each label: a Code 128 barcode of the piece's
// code at the top with the value under it, then the mark and sequence, the description, and
// "Qty n of N", the weight and the job. The sheet is positioned in inches, so it prints at 100 %
// on the label stock. See docs/LABELS.md.

/// <summary>What one label prints. <c>Index</c> of <c>Of</c>: this piece among the pieces of its mark and sequence.</summary>
public sealed record PieceLabel(string Code, string Mark, string Sequence, int Index, int Of, string? Description, double? Weight, string Job);

public static class Labels
{
    // Code 128 bar and space widths for values 0-105 (START B is 104), the checksum is the weighted
    // sum mod 103, and STOP is 106. Taken from the standard's table.
    static readonly string[] Patterns = ["212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213", "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132", "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211", "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313", "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331", "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111", "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214", "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111", "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141", "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141", "114131", "311141", "411131", "211412", "211214", "211232"];
    const string Stop = "2331112";
    const int StartB = 104;

    public const int PerSheet = 30;

    /// <summary>
    /// Labels for a job's codes, optionally one mark (and one sequence), or only the codes in
    /// <paramref name="only"/> (reprinting torn labels). Ordered by mark, then sequence, then code.
    /// Index/Of count all the pieces of that mark and sequence, so a reprint still says "3 of 12".
    /// A piece without a description or weight of its own takes them from the job's BOM, if any.
    /// </summary>
    public static List<PieceLabel> For(string job, IEnumerable<PieceCode> codes, IReadOnlyList<BomRow>? bom = null,
        string? mark = null, string? sequence = null, IReadOnlyCollection<string>? only = null)
    {
        var wanted = only is null ? null : new HashSet<string>(only.Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);
        var byMark = (bom ?? []).GroupBy(r => CodeList.CleanMark(r.Mark), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var groups = codes
            .GroupBy(c => (Mark: CodeList.CleanMark(c.Mark), Seq: c.Sequence?.Trim() ?? ""))
            .OrderBy(g => g.Key.Mark, NumericOrder.Instance)
            .ThenBy(g => g.Key.Seq, NumericOrder.Instance);
        var labels = new List<PieceLabel>();
        foreach (var g in groups)
        {
            if (mark is not null && !g.Key.Mark.Equals(CodeList.CleanMark(mark), StringComparison.OrdinalIgnoreCase)) continue;
            if (sequence is not null && g.Key.Seq != sequence.Trim()) continue;
            var fromBom = byMark.TryGetValue(g.Key.Mark, out var rows)
                ? rows.FirstOrDefault(r => (r.Sequence?.Trim() ?? "") == g.Key.Seq) ?? rows[0]
                : null;
            var members = g.OrderBy(c => c.Code, NumericOrder.Instance).ToList();
            for (var i = 0; i < members.Count; i++)
            {
                var c = members[i];
                if (wanted is not null && !wanted.Contains(c.Code)) continue;
                labels.Add(new PieceLabel(c.Code, g.Key.Mark, g.Key.Seq, i + 1, members.Count,
                    c.Description ?? fromBom?.Description, c.Weight ?? fromBom?.Weight, job));
            }
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
        sb.Append($"<svg class=\"bars\" viewBox=\"0 0 {widths.Sum()} 10\" preserveAspectRatio=\"none\" shape-rendering=\"crispEdges\" role=\"img\" aria-label=\"{Esc(text)}\">");
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
    /// Where each label goes: sheet number (0-based) and position (1-30, row by row). The first
    /// sheet uses only <paramref name="first"/> (positions still free on a part-used sheet, in the
    /// order to fill them; null or empty = the whole sheet); later sheets are whole new ones.
    /// </summary>
    public static List<(int Sheet, int Position)> Place(int count, IReadOnlyList<int>? first)
    {
        var start = first is { Count: > 0 }
            ? first.Where(p => p is >= 1 and <= PerSheet).Distinct().Order().ToList()
            : Enumerable.Range(1, PerSheet).ToList();
        var spots = new List<(int, int)>();
        var sheet = 0;
        var queue = new Queue<int>(start);
        while (spots.Count < count)
        {
            if (queue.Count == 0)
            {
                sheet++;
                queue = new Queue<int>(Enumerable.Range(1, PerSheet));
            }
            spots.Add((sheet, queue.Dequeue()));
        }
        return spots;
    }

    /// <summary>The printable page: one letter-size sheet per <c>section</c>, the labels where <see cref="Place"/> puts them.</summary>
    public static string Sheet(string job, IReadOnlyList<PieceLabel> labels, IReadOnlyList<int>? first, string title)
    {
        var spots = Place(labels.Count, first);
        var sheets = spots.Count == 0 ? 0 : spots[^1].Sheet + 1;
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>{Esc(title)}</title><style>{Css}</style></head><body>");
        sb.Append($"<p class=\"noprint bar\"><b>{labels.Count} label{(labels.Count == 1 ? "" : "s")}</b> for job {Esc(job)} on {sheets} Avery 5160 sheet{(sheets == 1 ? "" : "s")}. "
            + "Print at <b>100 %</b> (Scale: Actual size / Default, not Fit to page), Margins: None. "
            + "<button type=\"button\" onclick=\"print()\">Print</button></p>");
        for (var s = 0; s < sheets; s++)
        {
            sb.Append("<section class=\"sheet\">");
            for (var i = 0; i < labels.Count; i++)
            {
                if (spots[i].Sheet != s) continue;
                var l = labels[i];
                var slot = spots[i].Position - 1;
                var col = slot % 3;
                var row = slot / 3;
                sb.Append($"<div class=\"lab\" data-pos=\"{slot + 1}\" style=\"left:{Inch(0.1875 + col * 2.75)};top:{Inch(0.5 + row * 1.0)}\">");
                sb.Append(BarcodeSvg(l.Code));
                sb.Append($"<div class=\"val\">{Esc(l.Code)}</div>");
                sb.Append($"<div class=\"mk\"><b>{Esc(l.Mark)}</b><span>SEQ {Esc(l.Sequence == "" ? "—" : l.Sequence)}</span></div>");
                sb.Append($"<div class=\"desc\">{Esc(l.Description ?? "")}</div>");
                var weight = l.Weight is { } w ? $"{Math.Round(w).ToString("#,0", CultureInfo.InvariantCulture)} lbs" : "— lbs";
                sb.Append($"<div class=\"foot\"><span>QTY {l.Index} of {l.Of}</span><span>{weight}</span><span>JOB {Esc(l.Job)}</span></div>");
                sb.Append("</div>");
            }
            sb.Append("</section>");
        }
        sb.Append("</body></html>");
        return sb.ToString();
    }

    // Each label is 2.625 x 1 in; the content sits inside a 0.1 in margin so a slightly off printer
    // doesn't cut it. Barcode 2.2 in wide leaves the quiet zone each side.
    const string Css = """
        @page { size: letter; margin: 0; }
        * { box-sizing: border-box; }
        html, body { margin: 0; background: #fff; color: #000; font-family: Arial, Helvetica, sans-serif; }
        .bar { margin: 8px 12px; font-size: 14px; line-height: 1.5; }
        .bar button { font: inherit; font-weight: 700; margin-left: 8px; padding: 4px 16px; }
        .sheet { position: relative; width: 8.5in; height: 11in; page-break-after: always; break-after: page; overflow: hidden; }
        .sheet:last-of-type { page-break-after: auto; break-after: auto; }
        @media screen { .sheet { outline: 1px solid #ccc; margin: 0 auto 12px; } .lab { outline: 1px dashed #ddd; } }
        .lab { position: absolute; width: 2.625in; height: 1in; padding: 0.06in 0.1in 0; overflow: hidden; }
        .bars { display: block; width: 2.2in; height: 0.33in; margin: 0 auto 0.01in; }
        .bars rect { fill: #000; }
        .val { font: 7pt/0.11in "Courier New", monospace; text-align: center; height: 0.11in; letter-spacing: 0.02in; }
        .mk { display: flex; justify-content: space-between; align-items: baseline; height: 0.19in; line-height: 0.19in; white-space: nowrap; overflow: hidden; }
        .mk b { font-size: 12pt; }
        .mk span { font-size: 9pt; font-weight: 700; }
        .desc, .foot { font-size: 7pt; line-height: 0.12in; height: 0.12in; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .foot { display: flex; justify-content: space-between; font-weight: 700; }
        @media print { .noprint { display: none; } }
        """;

    static string Inch(double v) => v.ToString("0.0000", CultureInfo.InvariantCulture) + "in";

    static string Esc(string? s) => WebUtility.HtmlEncode(s ?? "");
}
