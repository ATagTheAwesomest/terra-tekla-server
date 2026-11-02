using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace TerraTeklaServer.Printing;

/// <summary>
/// Writes laid-out pages as a PDF 1.4 file using only the standard fonts every PDF reader and
/// printer has (Helvetica and Courier; ticks are drawn as lines), so nothing is embedded and files stay small.
/// </summary>
public static class PdfWriter
{
    // Resource name, base font.
    static readonly (string Name, string Base)[] FontTable =
    [
        ("F1", "Helvetica"), ("F2", "Helvetica-Bold"), ("F3", "Courier-Bold"), ("F4", "Courier-BoldOblique"),
    ];

    static string FontName(TicketFont font) => font switch
    {
        TicketFont.Bold => "F2",
        TicketFont.Mark => "F3",
        TicketFont.MarkManual => "F4",
        _ => "F1",
    };

    public static byte[] Write(IReadOnlyList<LaidPage> pages, string title)
    {
        if (pages.Count == 0)
            throw new ArgumentException("No pages.", nameof(pages));
        var objects = new List<byte[]>(); // object n is objects[n - 1]
        int Add(byte[] body)
        {
            objects.Add(body);
            return objects.Count;
        }
        int AddText(string body) => Add(Encoding.ASCII.GetBytes(body));

        // 1 catalog, 2 pages, 3 info, then fonts, then each page and its content.
        AddText("<< /Type /Catalog /Pages 2 0 R >>");
        AddText(""); // pages, filled in below
        AddText($"<< /Title {PdfString(title)} /Producer (TERRA server) /CreationDate (D:{DateTime.UtcNow:yyyyMMddHHmmss}Z) >>");
        var fontRefs = new StringBuilder("<< ");
        foreach (var (name, baseFont) in FontTable)
        {
            var id = AddText($"<< /Type /Font /Subtype /Type1 /BaseFont /{baseFont} /Encoding /WinAnsiEncoding >>");
            fontRefs.Append($"/{name} {id} 0 R ");
        }
        fontRefs.Append(">>");

        var kids = new List<int>();
        foreach (var page in pages)
        {
            var content = Deflate(Content(page));
            var contentId = Add([.. Encoding.ASCII.GetBytes($"<< /Length {content.Length} /Filter /FlateDecode >>\nstream\n"), .. content, .. "\nendstream"u8.ToArray()]);
            kids.Add(AddText($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(page.Width)} {N(page.Height)}] /Resources << /Font {fontRefs} >> /Contents {contentId} 0 R >>"));
        }
        objects[1] = Encoding.ASCII.GetBytes($"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>");

        using var pdf = new MemoryStream();
        void Raw(string s) => pdf.Write(Encoding.ASCII.GetBytes(s));
        Raw("%PDF-1.4\n");
        pdf.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        var offsets = new long[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = pdf.Position;
            Raw($"{i + 1} 0 obj\n");
            pdf.Write(objects[i]);
            Raw("\nendobj\n");
        }
        var xref = pdf.Position;
        Raw($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
            Raw($"{o:D10} 00000 n \n");
        Raw($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /Info 3 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    static byte[] Content(LaidPage page)
    {
        var s = new MemoryStream();
        void W(string text) => s.Write(Encoding.ASCII.GetBytes(text));
        double Y(double y) => page.Height - y;
        foreach (var op in page.Ops)
        {
            switch (op)
            {
                case RectOp r:
                    W($"0 g {N(r.X)} {N(Y(r.Y + r.H))} {N(r.W)} {N(r.H)} re f\n");
                    break;
                case LineOp l:
                    W($"0 G {N(l.Width)} w 0 J {N(l.X1)} {N(Y(l.Y1))} m {N(l.X2)} {N(Y(l.Y2))} l S\n");
                    break;
                case TextOp t:
                    W(t.White ? "1 g\n" : "0 g\n");
                    var x = t.X;
                    foreach (var (run, tick) in Fonts.Runs(t.Text))
                    {
                        if (tick)
                        {
                            // Drawn as lines, so it looks the same whatever fonts the printer has.
                            for (var k = 0; k < run.Length; k++)
                            {
                                var pts = Fonts.TickPoints(x + k * Fonts.TickWidth * t.Size / 1000, t.Y, t.Size);
                                W($"{N(t.Size * 0.12)} w 1 J 1 j {(t.White ? "1 G" : "0 G")} {N(pts[0].X)} {N(Y(pts[0].Y))} m {N(pts[1].X)} {N(Y(pts[1].Y))} l {N(pts[2].X)} {N(Y(pts[2].Y))} l S 0 J 0 j 0 G\n");
                            }
                        }
                        else
                        {
                            W($"BT /{FontName(t.Font)} {N(t.Size)} Tf {N(x)} {N(Y(t.Y))} Td (");
                            foreach (var c in run)
                            {
                                var b = Fonts.WinAnsi(c);
                                if (b is (byte)'(' or (byte)')' or (byte)'\\')
                                    s.WriteByte((byte)'\\');
                                s.WriteByte(b);
                            }
                            W(") Tj ET\n");
                        }
                        x += Fonts.Width(run, t.Font, t.Size);
                    }
                    if (t.Underline)
                        W($"0 G 0.6 w {N(t.X)} {N(Y(t.Y + 1.5))} m {N(x)} {N(Y(t.Y + 1.5))} l S\n");
                    W("0 g\n");
                    break;
            }
        }
        return s.ToArray();
    }

    static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return output.ToArray();
    }

    static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    static string PdfString(string text)
    {
        var sb = new StringBuilder("(");
        foreach (var c in text)
        {
            var b = (char)Fonts.WinAnsi(c);
            if (b is '(' or ')' or '\\')
                sb.Append('\\');
            sb.Append(b is >= ' ' and <= '~' ? b : '?');
        }
        return sb.Append(')').ToString();
    }
}
