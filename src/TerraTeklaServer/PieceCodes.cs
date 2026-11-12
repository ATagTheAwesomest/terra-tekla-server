using System.Text.RegularExpressions;

namespace TerraTeklaServer;

// Piece codes: on some jobs every piece of an assembly carries its own long unique code (a mark with
// 40 pieces has 40 codes). Scanning a code loads, or truck checks, that one piece. Codes are kept here
// only (PowerFab doesn't know them), per job: code -> mark (and sequence when known). They come from
// a list pasted on the admin board, from an API later, or are paired on an iPad (a code not on the
// list is scanned, then its piece's mark). See docs/PIECE-CODES.md.

/// <summary>One piece code of a job.</summary>
public sealed record PieceCode(long Id, string Code, string Mark, string? Sequence, string Source, string AddedAt, string? AddedBy);

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
public sealed record CodeLine(string Code, string Mark, string? Sequence);

public static class CodeList
{
    /// <summary>
    /// Reads a pasted list: one code per line, "code, mark" or "code, mark, sequence", separated by
    /// commas, tabs or semicolons (a spreadsheet copy works). A first line naming the columns is
    /// skipped, and so are blank lines. Returns the lines read and what was wrong with the others.
    /// </summary>
    public static (List<CodeLine> Lines, List<string> Errors) Parse(string text)
    {
        var lines = new List<CodeLine>();
        var errors = new List<string>();
        var n = 0;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            n++;
            var line = raw.Trim().TrimEnd('\r');
            if (line == "")
                continue;
            var parts = line.Split(line.Contains('\t') ? '\t' : line.Contains(';') ? ';' : ',').Select(p => p.Trim().Trim('"').Trim()).ToArray();
            if (n == 1 && parts.Any(p => p.Equals("code", StringComparison.OrdinalIgnoreCase)) && parts.Any(p => p.Equals("mark", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (parts.Length < 2 || parts[0] == "" || parts[1] == "")
            {
                errors.Add($"Line {n}: needs a code and a mark (\"{Short(line)}\").");
                continue;
            }
            if (parts[0].Length > LoadOps.MaxCodeLength || parts[1].Length > 100)
            {
                errors.Add($"Line {n}: too long.");
                continue;
            }
            lines.Add(new CodeLine(parts[0], CleanMark(parts[1]), parts.Length > 2 && parts[2] != "" ? parts[2] : null));
        }
        return (lines, errors);
    }

    /// <summary>A mark as the app writes it: trimmed, upper case, no trailing dashes.</summary>
    public static string CleanMark(string mark) => mark.Trim().ToUpperInvariant().TrimEnd('-');

    static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
