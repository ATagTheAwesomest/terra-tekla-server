using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TerraTeklaServer.Printing;

/// <summary>
/// Printers installed on the Windows server, printed through their own drivers (GDI), so any
/// printer Windows can print to works, PDF support or not. The service runs as LocalSystem by
/// default, which sees printers installed for the whole machine (Settings › Printers as an
/// administrator, or Print Management), not ones added only for one Windows user.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsPrinting
{
    /// <summary>The printers Windows has installed, and which is the default.</summary>
    public static List<(string Name, bool IsDefault)> Installed()
    {
        var list = new List<(string, bool)>();
        string? defaultName = null;
        try { defaultName = new PrinterSettings().PrinterName; } catch (Exception) { /* no default */ }
        foreach (string name in PrinterSettings.InstalledPrinters)
            list.Add((name, name == defaultName));
        return list;
    }

    public static PrinterCheck Check(string name)
    {
        var settings = new PrinterSettings { PrinterName = name };
        if (!settings.IsValid)
            return new PrinterCheck(false, $"Windows has no printer called \"{name}\" for the account the TERRA service runs as. Install it for all users (as an administrator) on the server.", null, null, null, null, null, []);
        var paper = settings.DefaultPageSettings.PaperSize;
        return new PrinterCheck(true, null, name, null, null, $"installed · {paper.PaperName}", null, []);
    }

    /// <summary>Prints the ticket on the printer's default paper. Returns the page count.</summary>
    public static int Print(string printerName, Ticket ticket, int copies, string jobName)
    {
        using var doc = new PrintDocument();
        doc.PrinterSettings.PrinterName = printerName;
        if (!doc.PrinterSettings.IsValid)
            throw new PrinterException($"Windows has no printer called \"{printerName}\" for the account the TERRA service runs as.");
        doc.DocumentName = jobName;
        doc.PrintController = new StandardPrintController(); // no "Printing..." window: this is a service
        var times = 1;
        if (copies > 1)
        {
            if (doc.PrinterSettings.MaximumCopies >= copies)
                doc.PrinterSettings.Copies = (short)copies;
            else
                times = copies; // the driver can't do copies itself
        }

        var ps = doc.DefaultPageSettings;
        double w = ps.PaperSize.Width * 0.72, h = ps.PaperSize.Height * 0.72; // hundredths of an inch -> points
        if (ps.Landscape)
            (w, h) = (h, w);
        if (w < 200 || h < 200)
            (w, h) = Paper.Size("letter");
        var pages = TicketLayout.Lay(ticket, w, h);

        using var fonts = new GdiFonts();
        var index = 0;
        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            g.PageUnit = GraphicsUnit.Point;
            // Drawing starts at the printable area's corner; move back to the paper's.
            g.TranslateTransform(-(float)(e.PageSettings.HardMarginX * 0.72), -(float)(e.PageSettings.HardMarginY * 0.72));
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Draw(g, pages[index], fonts);
            index++;
            e.HasMorePages = index < pages.Count;
        };
        for (var i = 0; i < times; i++)
        {
            index = 0;
            try
            {
                doc.Print();
            }
            catch (Exception ex) when (ex is InvalidPrinterException or System.ComponentModel.Win32Exception or ExternalException)
            {
                throw new PrinterException($"Windows couldn't print on \"{printerName}\": {ex.Message}");
            }
        }
        return pages.Count;
    }

    static void Draw(Graphics g, LaidPage page, GdiFonts fonts)
    {
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
        foreach (var op in page.Ops)
        {
            switch (op)
            {
                case RectOp r:
                    g.FillRectangle(Brushes.Black, (float)r.X, (float)r.Y, (float)r.W, (float)r.H);
                    break;
                case LineOp l:
                    using (var pen = new Pen(Color.Black, (float)l.Width))
                        g.DrawLine(pen, (float)l.X1, (float)l.Y1, (float)l.X2, (float)l.Y2);
                    break;
                case TextOp t:
                    var brush = t.White ? Brushes.White : Brushes.Black;
                    var x = t.X;
                    foreach (var (run, tick) in Fonts.Runs(t.Text))
                    {
                        if (tick)
                        {
                            using var pen = new Pen(t.White ? Color.White : Color.Black, (float)(t.Size * 0.12)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
                            for (var k = 0; k < run.Length; k++)
                                g.DrawLines(pen, Fonts.TickPoints(x + k * Fonts.TickWidth * t.Size / 1000, t.Y, t.Size).Select(p => new PointF((float)p.X, (float)p.Y)).ToArray());
                        }
                        else
                        {
                            var font = fonts.Get(t.Font, t.Size);
                            var family = font.FontFamily;
                            var ascent = font.Size * family.GetCellAscent(font.Style) / family.GetEmHeight(font.Style);
                            g.DrawString(new string(run.Select(Fonts.Printable).ToArray()), font, brush, (float)x, (float)(t.Y - ascent), format);
                        }
                        x += Fonts.Width(run, t.Font, t.Size);
                    }
                    if (t.Underline)
                        using (var pen = new Pen(Color.Black, 0.6f))
                            g.DrawLine(pen, (float)t.X, (float)(t.Y + 1.5), (float)x, (float)(t.Y + 1.5));
                    break;
            }
        }
    }

    // Arial and Courier New have the same character widths as the PDF fonts the layout measures with.
    sealed class GdiFonts : IDisposable
    {
        readonly Dictionary<(string, FontStyle, double), Font> cache = new();

        public Font Get(TicketFont font, double size) => font switch
        {
            TicketFont.Bold => Get("Arial", FontStyle.Bold, size),
            TicketFont.Mark => Get("Courier New", FontStyle.Bold, size),
            TicketFont.MarkManual => Get("Courier New", FontStyle.Bold | FontStyle.Italic, size),
            _ => Get("Arial", FontStyle.Regular, size),
        };

        public Font Get(string family, FontStyle style, double size)
        {
            if (!cache.TryGetValue((family, style, size), out var f))
                cache[(family, style, size)] = f = new Font(family, (float)size, style, GraphicsUnit.Point);
            return f;
        }

        public void Dispose()
        {
            foreach (var f in cache.Values)
                f.Dispose();
        }
    }
}
