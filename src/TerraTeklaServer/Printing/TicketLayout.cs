namespace TerraTeklaServer.Printing;

/// <summary>The fonts a ticket uses. On paper: Helvetica / Arial, and Courier for piece marks.</summary>
public enum TicketFont
{
    Regular,
    Bold,
    /// <summary>Piece marks: bold monospace.</summary>
    Mark,
    /// <summary>A piece typed on the keypad: bold italic monospace (and underlined).</summary>
    MarkManual,
}

/// <summary>Something drawn on a page. Units are points (1/72 inch), from the page's top-left corner.</summary>
public abstract record DrawOp;

/// <summary>Text with its baseline at <paramref name="Y"/>.</summary>
public sealed record TextOp(double X, double Y, string Text, TicketFont Font, double Size, bool White = false, bool Underline = false) : DrawOp;

public sealed record LineOp(double X1, double Y1, double X2, double Y2, double Width) : DrawOp;

/// <summary>A filled black rectangle (table headers).</summary>
public sealed record RectOp(double X, double Y, double W, double H) : DrawOp;

public sealed record LaidPage(double Width, double Height, IReadOnlyList<DrawOp> Ops);

/// <summary>Paper sizes in points.</summary>
public static class Paper
{
    public static (double W, double H) Size(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "a4" => (595.28, 841.89),
        "legal" => (612, 1008),
        _ => (612, 792), // letter
    };

    public static readonly string[] Names = ["letter", "a4", "legal"];
}

/// <summary>
/// Lays a <see cref="Ticket"/> out on pages, like css/print.css in the app: a black title rule, a
/// summary list, one bordered table per stack with a black header row that repeats on each page, and
/// the sign-off lines. Every row stays whole on one page. Each page gets "Page n of N" at the bottom.
/// The same pages go to a PDF (IPP and raw printers) and to Windows' printing (installed printers).
/// </summary>
public static class TicketLayout
{
    public const double Margin = 34; // 12 mm, as the app's @page
    const double TableSize = 10;
    const double PadX = 4, PadY = 2.5;
    const double Rule = 0.8;
    const double FooterRoom = 18;

    public static IReadOnlyList<LaidPage> Lay(Ticket ticket, double pageW, double pageH)
    {
        var l = new Layouter(pageW, pageH);
        l.Draw(ticket);
        return l.Finish(ticket);
    }

    // Leading for a font size, and where the baseline sits inside one line.
    static double LineH(double size) => Math.Round(size * 1.25, 2);
    static double Base(double size) => (LineH(size) - size) / 2 + size * 0.78;

    sealed class Layouter(double pageW, double pageH)
    {
        readonly List<List<DrawOp>> pages = [];
        List<DrawOp> ops = null!;
        double y;
        double Left => Margin;
        double Right => pageW - Margin;
        double Width => Right - Left;
        double Bottom => pageH - Margin - FooterRoom;

        void NewPage()
        {
            ops = [];
            pages.Add(ops);
            y = Margin;
        }

        bool Fits(double h) => y + h <= Bottom || y <= Margin + 0.01; // a page always takes at least one thing

        void Text(double x, double top, string text, TicketFont font, double size, bool white = false, bool underline = false)
        {
            if (text != "")
                ops.Add(new TextOp(x, top + Base(size), text, font, size, white, underline));
        }

        public void Draw(Ticket t)
        {
            NewPage();
            // Title, upper case, with a 2 pt rule under it.
            Text(Left, y, t.Title.ToUpperInvariant(), TicketFont.Bold, 20);
            y += LineH(20) + 1;
            ops.Add(new LineOp(Left, y, Right, y, 2));
            y += 9;

            // Summary: bold labels, values beside them.
            if (t.Summary.Count > 0)
            {
                const double size = 11;
                var labelW = t.Summary.Max(p => Fonts.Width(p.Label, TicketFont.Bold, size)) + 12;
                foreach (var (label, value) in t.Summary)
                {
                    var lines = Fonts.Wrap(value, TicketFont.Regular, size, Width - labelW);
                    var h = LineH(size) * Math.Max(1, lines.Count) + 1;
                    if (!Fits(h))
                        NewPage();
                    Text(Left, y, label, TicketFont.Bold, size);
                    for (var i = 0; i < lines.Count; i++)
                        Text(Left + labelW, y + i * LineH(size), lines[i], TicketFont.Regular, size);
                    y += h;
                }
                y += 10;
            }

            foreach (var s in t.Sections)
                Section(s);

            if (t.Signoff.Count > 0)
            {
                const double size = 11;
                var h = 18 + t.Signoff.Count * (LineH(size) + 10);
                if (!Fits(h))
                    NewPage();
                else
                    y += 8;
                foreach (var line in t.Signoff)
                {
                    y += 10;
                    Text(Left, y, line, TicketFont.Regular, size);
                    y += LineH(size);
                }
            }
        }

        void Section(TicketSection s)
        {
            var widths = ColumnWidths(s);
            var headH = LineH(TableSize) + 2 * PadY;
            var headingH = s.Heading is null && s.Note is null ? 0 : LineH(13) + 4;
            var rows = s.Rows.Select(r => Row(s, r, widths)).ToList();
            var first = rows.Count > 0 ? rows[0].H : 0;
            // The heading stays with the table's header and first row.
            if (!Fits(headingH + headH + first))
                NewPage();
            Heading(s, continued: false);
            Header(s, widths);
            foreach (var row in rows)
            {
                if (!Fits(row.H))
                {
                    NewPage();
                    Heading(s, continued: true);
                    Header(s, widths);
                }
                DrawRow(row, widths);
            }
            if (s.Foot is { Count: > 0 } foot)
            {
                var cells = new List<(double X, double W, List<string> Lines, ColumnKind Kind)>();
                var col = 0;
                foreach (var c in foot)
                {
                    var x = Left + widths.Take(col).Sum();
                    var w = widths.Skip(col).Take(c.Span).Sum();
                    var kind = c.Span == 1 ? s.Columns[col].Kind : ColumnKind.Text;
                    cells.Add((x, w, Fonts.Wrap(c.Text, TicketFont.Bold, TableSize, w - 2 * PadX), kind));
                    col += c.Span;
                }
                var h = cells.Max(c => Math.Max(1, c.Lines.Count)) * LineH(TableSize) + 2 * PadY;
                if (!Fits(h))
                {
                    NewPage();
                    Heading(s, continued: true);
                    Header(s, widths);
                }
                Borders(cells.Select(c => (c.X, c.W)).ToList(), h);
                foreach (var c in cells)
                    for (var i = 0; i < c.Lines.Count; i++)
                        Cell(c.Lines[i], c.Kind, TicketFont.Bold, c.X, c.W, y + PadY + i * LineH(TableSize), false);
                y += h;
            }
            y += 12;
        }

        void Heading(TicketSection s, bool continued)
        {
            if (s.Heading is null && s.Note is null)
                return;
            var heading = (s.Heading ?? "") + (continued ? " (continued)" : "");
            if (s.Note is { } note)
            {
                var nw = Fonts.Width(note, TicketFont.Regular, 13);
                Text(Right - nw, y, note, TicketFont.Regular, 13);
                heading = Fonts.Fit(heading, TicketFont.Bold, 13, Width - nw - 12);
            }
            Text(Left, y, heading, TicketFont.Bold, 13);
            y += LineH(13) + 4;
        }

        void Header(TicketSection s, double[] widths)
        {
            var h = LineH(TableSize) + 2 * PadY;
            ops.Add(new RectOp(Left, y, Width, h));
            var x = Left;
            for (var i = 0; i < s.Columns.Count; i++)
            {
                var label = Fonts.Fit(s.Columns[i].Label, TicketFont.Bold, TableSize, widths[i] - 2 * PadX);
                Cell(label, s.Columns[i].Kind, TicketFont.Bold, x, widths[i], y + PadY, false, white: true, header: true);
                x += widths[i];
            }
            Borders(Spans(widths), h);
            y += h;
        }

        sealed record LaidRow(List<List<string>> Cells, bool Manual, double H);

        LaidRow Row(TicketSection s, TicketRow r, double[] widths)
        {
            var cells = new List<List<string>>();
            for (var i = 0; i < s.Columns.Count; i++)
            {
                var kind = s.Columns[i].Kind;
                var font = FontFor(kind, r.Manual);
                var room = widths[i] - 2 * PadX;
                cells.Add(kind == ColumnKind.Text ? Fonts.Wrap(r.Cells[i], font, TableSize, room) : [Fonts.Fit(r.Cells[i], font, TableSize, room)]);
            }
            var lines = Math.Max(1, cells.Max(c => c.Count));
            return new LaidRow(cells, r.Manual, lines * LineH(TableSize) + 2 * PadY);
        }

        static TicketFont FontFor(ColumnKind kind, bool manual) =>
            kind == ColumnKind.Mark ? manual ? TicketFont.MarkManual : TicketFont.Mark : TicketFont.Regular;

        void DrawRow(LaidRow row, double[] widths)
        {
            Borders(Spans(widths), row.H);
            var x = Left;
            for (var i = 0; i < widths.Length; i++)
            {
                var kind = currentKinds[i];
                for (var j = 0; j < row.Cells[i].Count; j++)
                    Cell(row.Cells[i][j], kind, FontFor(kind, row.Manual), x, widths[i], y + PadY + j * LineH(TableSize), row.Manual && kind == ColumnKind.Mark);
                x += widths[i];
            }
            y += row.H;
        }

        List<(double X, double W)> Spans(double[] widths)
        {
            var list = new List<(double, double)>();
            var x = Left;
            foreach (var w in widths)
            {
                list.Add((x, w));
                x += w;
            }
            return list;
        }

        // A row's cell borders: top, bottom and every cell's sides.
        void Borders(List<(double X, double W)> cells, double h)
        {
            ops.Add(new LineOp(Left, y, Right, y, Rule));
            ops.Add(new LineOp(Left, y + h, Right, y + h, Rule));
            foreach (var (x, w) in cells)
                ops.Add(new LineOp(x, y, x, y + h, Rule));
            ops.Add(new LineOp(Right, y, Right, y + h, Rule));
        }

        void Cell(string text, ColumnKind kind, TicketFont font, double x, double w, double top, bool underline, bool white = false, bool header = false)
        {
            if (text == "")
                return;
            var tw = Fonts.Width(text, font, TableSize);
            var tx = kind switch
            {
                ColumnKind.Number => x + w - PadX - tw,
                ColumnKind.Check => x + (w - tw) / 2,
                _ => x + PadX,
            };
            Text(tx, top, text, font, TableSize, white, underline);
        }

        ColumnKind[] currentKinds = [];

        double[] ColumnWidths(TicketSection s)
        {
            currentKinds = s.Columns.Select(c => c.Kind).ToArray();
            var n = s.Columns.Count;
            var natural = new double[n];
            for (var i = 0; i < n; i++)
            {
                var kind = s.Columns[i].Kind;
                var w = Fonts.Width(s.Columns[i].Label, TicketFont.Bold, TableSize);
                foreach (var r in s.Rows)
                    w = Math.Max(w, Fonts.Width(r.Cells[i], FontFor(kind, r.Manual), TableSize));
                natural[i] = w + 2 * PadX + 1;
                if (kind == ColumnKind.Check)
                    natural[i] = Math.Max(natural[i], 40);
                else if (kind != ColumnKind.Text)
                    natural[i] = Math.Min(natural[i], Width * 0.35);
            }
            var widths = (double[])natural.Clone();
            var text = Enumerable.Range(0, n).Where(i => s.Columns[i].Kind == ColumnKind.Text).ToList();
            var fixedW = Enumerable.Range(0, n).Where(i => !text.Contains(i)).Sum(i => widths[i]);
            if (text.Count == 0)
            {
                // Like a 100 % wide HTML table: the spare room is shared out.
                var scale = Width / widths.Sum();
                for (var i = 0; i < n; i++)
                    widths[i] *= scale;
                return widths;
            }
            const double minText = 40;
            var room = Width - fixedW;
            if (room < minText * text.Count)
            {
                // Narrow the fixed columns until the text columns get their minimum (their values get cut short with "…").
                var scale = (Width - minText * text.Count) / fixedW;
                for (var i = 0; i < n; i++)
                    if (!text.Contains(i))
                        widths[i] *= scale;
                room = minText * text.Count;
            }
            // Text columns: a short one keeps its natural width; the rest share what is left by how wide they want to be.
            var wanting = new List<int>(text);
            while (true)
            {
                var share = room / wanting.Count;
                var small = wanting.Where(i => natural[i] <= share).ToList();
                if (small.Count == 0 || small.Count == wanting.Count)
                    break;
                foreach (var i in small)
                {
                    widths[i] = natural[i];
                    room -= natural[i];
                    wanting.Remove(i);
                }
            }
            var total = wanting.Sum(i => natural[i]);
            foreach (var i in wanting)
                widths[i] = room * natural[i] / total;
            return widths;
        }

        public IReadOnlyList<LaidPage> Finish(Ticket t)
        {
            var label = t.Footer ?? string.Join(" · ", new[] { t.Title, t.Job is null ? null : $"Job {t.Job}", t.Load is null ? null : $"Load {t.Load}" }.Where(x => x is not null));
            var result = new List<LaidPage>();
            for (var i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                var baseY = pageH - Margin;
                page.Add(new TextOp(Left, baseY, Fonts.Fit(label, TicketFont.Regular, 8, Width - 80), TicketFont.Regular, 8));
                var no = $"Page {i + 1} of {pages.Count}";
                page.Add(new TextOp(Right - Fonts.Width(no, TicketFont.Regular, 8), baseY, no, TicketFont.Regular, 8));
                result.Add(new LaidPage(pageW, pageH, page));
            }
            return result;
        }
    }
}
