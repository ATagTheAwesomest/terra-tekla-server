using System.Text.RegularExpressions;

namespace TerraTeklaServer;

// Piece codes: on some jobs every piece of an assembly carries its own long unique code (a mark with
// 40 pieces has 40 codes). Scanning a code loads, or truck checks, that one piece. Codes are kept here
// only (PowerFab doesn't know them), per job: code -> mark (and sequence when known). They come from
// a list pasted on the admin board, from an API later, or are paired on an iPad (a code not on the
// list is scanned, then its piece's mark). See docs/PIECE-CODES.md.

/// <summary>One piece code of a job.</summary>
public sealed record PieceCode(long Id, string Code, string Mark, string? Sequence, string Source, string AddedAt, string? AddedBy, string? Status = null);

/// <summary>
/// A job's piece code settings. <c>Pattern</c> is a regex that tells a code the app hasn't seen
/// apart from a mark (a code it has is always a code); a named group <c>code</c> picks the code out
/// of a longer scan (a QR code holding a link, say). <c>Learn</c>: a code not on the list is paired on
/// the iPad (scan the code, then its mark); off, it is refused.
/// </summary>
public sealed record PieceCodeSettings(bool Enabled, string Pattern, bool Learn)
{
    /// <summary>Until real codes are known: 12 or more letters and digits, no dashes or spaces (marks have dashes).</summary>
    public const string DefaultPattern = "^[A-Za-z0-9]{12,}$";

    public static readonly PieceCodeSettings Off = new(false, DefaultPattern, true);

    /// <summary>Checks a pattern; throws <see cref="OpException"/> with the reason when it can't be used.</summary>
    public static string CheckPattern(string? pattern)
    {
        pattern = (pattern ?? "").Trim();
        if (pattern == "")
            return DefaultPattern;
        if (pattern.Length > 300)
            throw new OpException("A code pattern is at most 300 characters.");
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException ex)
        {
            throw new OpException($"The code pattern isn't a valid regex: {ex.Message}");
        }
        return pattern;
    }
}

/// <summary>What adding codes did.</summary>
public sealed record CodesAdded(int Added, int Updated, int Unchanged, List<CodeConflict> Conflicts, List<PieceCode> Codes);

/// <summary>A code already paired with another mark.</summary>
public sealed record CodeConflict(string Code, string Mark, string Wanted);

/// <summary>One line of a pasted or imported code list.</summary>
/// <summary>One line of a pasted or imported code list. <c>Status</c> is the piece's status in the list
/// it came from (for example "Fabrication Complete"), shown when it is scanned; null keeps the one on file.</summary>
public sealed record CodeLine(string Code, string Mark, string? Sequence, string? Status = null);

public static class CodeList
{
    // Column names an export may use, tried in order (case and spaces ignored).
    static readonly string[] CodeColumns = ["barcode", "piececode", "code", "serial", "serialnumber", "tag", "rfid", "uid"];
    static readonly string[] MarkColumns = ["assembly", "assemblymark", "mainmk", "mainmark", "mark", "piecemk", "piecemark"];
    static readonly string[] SequenceColumns = ["sequence", "seq"];
    static readonly string[] StatusColumns = ["status"];

    /// <summary>
    /// Reads a code list. Either an export with a first line naming its columns (for example the
    /// facility portal's "Status, Sequence, Assembly, Barcode, ..."): the code, mark, sequence and
    /// status columns are found by name and the rest is ignored. Or a plain list, one code per line,
    /// "code, mark" or "code, mark, sequence". Commas, tabs or semicolons separate values (a
    /// spreadsheet copy works); a value in double quotes may hold any of them. Blank lines are
    /// skipped. Returns the lines read and what was wrong with the others.
    /// </summary>
    public static (List<CodeLine> Lines, List<string> Errors) Parse(string text)
    {
        var lines = new List<CodeLine>();
        var errors = new List<string>();
        var rows = (text ?? "").TrimStart('\uFEFF').Split('\n');
        int code = 0, mark = 1, seq = 2, status = -1;
        var n = 0;
        var first = true;
        foreach (var raw in rows)
        {
            n++;
            var line = raw.TrimEnd('\r');
            if (line.Trim() == "")
                continue;
            var sep = line.Contains('\t') ? '\t' : line.Contains(',') ? ',' : line.Contains(';') ? ';' : ',';
            var parts = Split(line, sep);
            if (first)
            {
                first = false;
                var names = parts.Select(Key).ToArray();
                int Find(string[] wanted) => wanted.Select(w => Array.IndexOf(names, w)).FirstOrDefault(i => i >= 0, -1);
                var c = Find(CodeColumns);
                var m = Find(MarkColumns);
                if (c >= 0 && m >= 0)
                {
                    (code, mark, seq, status) = (c, m, Find(SequenceColumns), Find(StatusColumns));
                    continue;
                }
                if (c >= 0 || m >= 0)
                {
                    errors.Add("The first line names columns, but not both a code column (Barcode, Code, ...) and a mark column (Assembly, Mark, ...).");
                    return (lines, errors);
                }
            }
            string At(int i) => i >= 0 && i < parts.Length ? parts[i] : "";
            var (cv, mv, sv, st) = (At(code), At(mark), At(seq), At(status));
            if (cv == "" || mv == "")
            {
                errors.Add($"Line {n}: needs a code and a mark (\"{Short(line.Trim())}\").");
                continue;
            }
            if (cv.Length > LoadOps.MaxCodeLength || mv.Length > 100 || sv.Length > 50 || st.Length > 100)
            {
                errors.Add($"Line {n}: too long.");
                continue;
            }
            lines.Add(new CodeLine(cv, CleanMark(mv), sv == "" ? null : sv, st == "" ? null : st));
        }
        return (lines, errors);
    }

    /// <summary>
    /// A regex that fits every code read, when they all have one shape (for example C9227621Q1 ->
    /// <c>^C\d{7}Q\d$</c>), so a code not on the list yet is still told apart from a mark. Null when
    /// the codes have no one shape.
    /// </summary>
    public static string? SuggestPattern(IEnumerable<CodeLine> lines)
    {
        string? shape = null;
        foreach (var l in lines)
        {
            var s = Shape(l.Code.Trim());
            if (shape is null) shape = s;
            else if (shape != s) return null;
        }
        return shape is null ? null : "^" + shape + "$";
    }

    // Runs of digits become \d{n}, a lone letter stays itself, other letters become [A-Za-z]{n}.
    static string Shape(string code)
    {
        var sb = new System.Text.StringBuilder();
        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            var j = i;
            if (char.IsAsciiDigit(c))
            {
                while (j < code.Length && char.IsAsciiDigit(code[j])) j++;
                sb.Append(j - i == 1 ? "\\d" : $"\\d{{{j - i}}}");
            }
            else if (char.IsAsciiLetter(c))
            {
                while (j < code.Length && char.IsAsciiLetter(code[j])) j++;
                sb.Append(j - i == 1 ? char.ToUpperInvariant(c).ToString() : $"[A-Za-z]{{{j - i}}}");
            }
            else
            {
                j++;
                sb.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
            }
            i = j;
        }
        return sb.ToString();
    }

    static string Key(string name) => new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Splits one line, with "double quoted" values that may hold the separator ("" is a quote).</summary>
    static string[] Split(string line, char sep)
    {
        var parts = new List<string>();
        var cur = new System.Text.StringBuilder();
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

    /// <summary>A mark as the app writes it: trimmed, upper case, no trailing dashes.</summary>
    public static string CleanMark(string mark) => mark.Trim().ToUpperInvariant().TrimEnd('-');

    static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
