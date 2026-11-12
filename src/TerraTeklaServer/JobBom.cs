using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace TerraTeklaServer;

// A job's BOM: the big workbook made by the BOM script (one per job), put in the job's slot on the
// admin board. It adds what PowerFab doesn't say about an assembly: its transmittal (TL1282, or
// TLR1361 for a revision), due date, paint / notes, drawing and revision. Read only: nothing here
// changes how a job scans or ships. See docs/JOB-BOM.md.

/// <summary>What the BOM says about one assembly (main mark) in one transmittal.</summary>
public sealed record BomRow(
    string Mark, string? Transmittal, string? Sequence, string? Drawing, string? Rev, string? Revises,
    string? Due, string? Notes, string? Description, int? Qty, string? Length, double? Weight, bool Exception);

/// <summary>About a job's BOM file: when it came, from whom, and what is in it.</summary>
public sealed record BomInfo(
    string FileName, string UploadedAt, string? UploadedBy, int Assemblies, int Lines, List<string> Transmittals,
    string? BomJob, string? Project, string? Sheet);

public sealed class BomException(string message) : Exception(message);

public static class BomReader
{
    // Column names, tried in order (case, spaces and punctuation ignored).
    static readonly string[] MarkCols = ["assembly", "mainmark", "mainmk", "assemblymark", "mark"];
    static readonly string[] TlCols = ["transmittal", "tl", "tlno", "tlnumber"];
    static readonly string[] SeqCols = ["sequence", "seq"];
    static readonly string[] DrawingCols = ["drawing", "dwg", "drawingnumber"];
    static readonly string[] RevCols = ["rev"];
    static readonly string[] RevisesCols = ["revises"];
    static readonly string[] DueCols = ["duedate", "due"];
    static readonly string[] NotesCols = ["paintnotes", "paint", "notes"];
    static readonly string[] DescCols = ["asmdescription", "assemblydescription", "description"];
    static readonly string[] QtyCols = ["asmqty", "assemblyqty", "qty"];
    static readonly string[] LengthCols = ["asmlength", "assemblylength", "length"];
    static readonly string[] WeightCols = ["asmwteaclbs", "asmwtealbs", "asmwteachlbs", "asmwtea", "assemblyweight", "weight"];
    static readonly string[] ExceptionCols = ["exception"];

    public const int MaxRows = 500_000;

    /// <summary>
    /// Reads a BOM: an .xlsx workbook (its "Master BOM" sheet, or the first sheet with an Assembly
    /// column) or a CSV / tab-separated copy of that sheet. One row per assembly and transmittal;
    /// the first line of an assembly gives its details (the BOM repeats them on each part line).
    /// </summary>
    public static (List<BomRow> Rows, BomInfo Info) Read(byte[] bytes, string fileName)
    {
        List<string[]> table;
        string? sheet = null, bomJob = null, project = null;
        if (bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K')
        {
            var book = Xlsx.Open(bytes);
            sheet = book.SheetNames.FirstOrDefault(n => Key(n) == "masterbom")
                ?? book.SheetNames.FirstOrDefault(n => HeaderRow(book.Sheet(n)) >= 0)
                ?? throw new BomException("No sheet in this workbook has an Assembly (or Main Mark) column. The BOM script's \"Master BOM\" sheet is the one to use.");
            table = book.Sheet(sheet);
            if (book.SheetNames.FirstOrDefault(n => Key(n) == "settings") is { } settings)
                foreach (var row in book.Sheet(settings))
                {
                    if (row.Length < 2) continue;
                    var k = Key(row[0]);
                    if (k is "ourjob" or "ourjobno" or "ourjobnumber") bomJob = Clean(row[1]);
                    if (k.StartsWith("projecttitle") || k == "projectname") project = Clean(row[1]);
                }
        }
        else
        {
            var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
            table = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim() != "")
                .Select(l => Csv.Split(l, l.Contains('\t') ? '\t' : ',')).ToList();
        }

        var head = HeaderRow(table);
        if (head < 0)
            throw new BomException("Couldn't find the header line: it needs an Assembly (or Main Mark) column.");
        var names = table[head].Select(Key).ToArray();
        int Col(string[] wanted) => wanted.Select(w => Array.IndexOf(names, w)).FirstOrDefault(i => i >= 0, -1);
        int mark = Col(MarkCols), tl = Col(TlCols), seq = Col(SeqCols), dwg = Col(DrawingCols), rev = Col(RevCols),
            revises = Col(RevisesCols), due = Col(DueCols), notes = Col(NotesCols), desc = Col(DescCols),
            qty = Col(QtyCols), len = Col(LengthCols), wt = Col(WeightCols), exc = Col(ExceptionCols);

        var rows = new Dictionary<(string, string), BomRow>();
        var order = new List<(string, string)>();
        var lines = 0;
        foreach (var r in table.Skip(head + 1))
        {
            string? At(int i) => i >= 0 && i < r.Length ? Clean(r[i]) : null;
            var m = At(mark);
            if (m is null) continue;
            m = CodeList.CleanMark(m);
            if (m.Length > 100 || Key(m) is "total" or "totals") continue;
            lines++;
            if (lines > MaxRows) throw new BomException($"More than {MaxRows:N0} lines.");
            var t = At(tl) ?? "";
            var key = (m.ToUpperInvariant(), t.ToUpperInvariant());
            var isException = At(exc) is { } e && e != "";
            if (rows.TryGetValue(key, out var have))
            {
                if (isException && !have.Exception) rows[key] = have with { Exception = true };
                continue;
            }
            rows[key] = new BomRow(m, t == "" ? null : t, At(seq), At(dwg), At(rev), At(revises), At(due), At(notes), At(desc),
                int.TryParse(At(qty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) ? q
                    : double.TryParse(At(qty), NumberStyles.Float, CultureInfo.InvariantCulture, out var qd) ? (int)Math.Round(qd) : null,
                At(len),
                double.TryParse(At(wt), NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? Math.Round(w, 2) : null,
                isException);
            order.Add(key);
        }
        if (rows.Count == 0)
            throw new BomException("The BOM has a header line but no assemblies under it.");
        var list = order.Select(k => rows[k]).ToList();
        var tls = list.Select(r => r.Transmittal).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, NumericOrder.Instance).ToList();
        var info = new BomInfo(Path.GetFileName(fileName), DateTime.UtcNow.ToString("o"), null,
            list.Select(r => r.Mark).Distinct(StringComparer.OrdinalIgnoreCase).Count(), lines, tls, bomJob, project, sheet);
        return (list, info);
    }

    static int HeaderRow(List<string[]> table)
    {
        for (var i = 0; i < Math.Min(table.Count, 30); i++)
            if (table[i].Select(Key).Any(k => MarkCols.Contains(k)) && table[i].Count(c => Clean(c) is not null) >= 3)
                return i;
        return -1;
    }

    static string Key(string? name) => new string((name ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Splits a CSV line, with "double quoted" values that may hold the separator ("" is a quote).</summary>
public static class Csv
{
    public static string[] Split(string line, char sep)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cur.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == sep) { parts.Add(cur.ToString().Trim()); cur.Clear(); }
            else cur.Append(ch);
        }
        parts.Add(cur.ToString().Trim());
        return parts.ToArray();
    }
}

/// <summary>
/// Just enough of an .xlsx reader for a BOM: sheet names, and each sheet's cells as text (the
/// values Excel saved, so formulas read as their results). Numbers are written plainly (52031000,
/// 7035.8); dates stored as numbers stay numbers.
/// </summary>
public sealed class Xlsx
{
    static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    readonly ZipArchive zip;
    readonly List<string> shared = [];
    readonly Dictionary<string, string> paths = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SheetNames { get; } = [];

    Xlsx(ZipArchive zip) => this.zip = zip;

    public static Xlsx Open(byte[] bytes)
    {
        try
        {
            var x = new Xlsx(new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read));
            x.Load();
            return x;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or NullReferenceException)
        {
            throw new BomException("That file isn't an Excel workbook this server can read (.xlsx).");
        }
    }

    XDocument Doc(string path)
    {
        var entry = zip.GetEntry(path) ?? throw new BomException($"The workbook is missing {path}.");
        using var s = entry.Open();
        return XDocument.Load(s);
    }

    void Load()
    {
        var rels = Doc("xl/_rels/workbook.xml.rels").Root!.Elements(PkgRel + "Relationship")
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);
        foreach (var s in Doc("xl/workbook.xml").Root!.Element(Main + "sheets")!.Elements(Main + "sheet"))
        {
            var name = (string)s.Attribute("name")!;
            var target = rels[(string)s.Attribute(Rel + "id")!];
            target = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            SheetNames.Add(name);
            paths[name] = target;
        }
        if (zip.GetEntry("xl/sharedStrings.xml") is not null)
            foreach (var si in Doc("xl/sharedStrings.xml").Root!.Elements(Main + "si"))
                shared.Add(string.Concat(si.Descendants(Main + "t").Select(t => t.Value)));
    }

    readonly Dictionary<string, List<string[]>> sheets = new(StringComparer.OrdinalIgnoreCase);

    public List<string[]> Sheet(string name)
    {
        if (sheets.TryGetValue(name, out var have)) return have;
        var rows = new List<string[]>();
        foreach (var row in Doc(paths[name]).Root!.Element(Main + "sheetData")?.Elements(Main + "row") ?? [])
        {
            var cells = new List<string>();
            foreach (var c in row.Elements(Main + "c"))
            {
                var col = Column((string?)c.Attribute("r")) ?? cells.Count;
                while (cells.Count < col) cells.Add("");
                cells.Add(Value(c));
            }
            var at = (int?)row.Attribute("r") is { } r ? r - 1 : rows.Count;
            while (rows.Count < at) rows.Add([]);
            rows.Add(cells.ToArray());
        }
        sheets[name] = rows;
        return rows;
    }

    string Value(XElement c)
    {
        var type = (string?)c.Attribute("t");
        if (type == "inlineStr")
            return string.Concat(c.Descendants(Main + "t").Select(t => t.Value));
        var v = c.Element(Main + "v")?.Value ?? "";
        if (type == "s")
            return int.TryParse(v, out var i) && i >= 0 && i < shared.Count ? shared[i] : "";
        if (type is null or "n" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d.ToString("0.############", CultureInfo.InvariantCulture);
        return v;
    }

    // "AB12" -> 27 (0-based column).
    static int? Column(string? cellRef)
    {
        if (string.IsNullOrEmpty(cellRef)) return null;
        var n = 0;
        foreach (var ch in cellRef)
        {
            if (!char.IsAsciiLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n == 0 ? null : n - 1;
    }
}
