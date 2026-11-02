using System.Text.Json.Nodes;

namespace TerraTeklaServer.Printing;

/// <summary>A ticket the iPad sent that can't be printed (missing parts, far too big).</summary>
public sealed class BadTicketException(string message) : Exception(message);

/// <summary>How a table column is drawn: like the app's printed ticket (css/print.css).</summary>
public enum ColumnKind
{
    /// <summary>Wraps, takes the room that is left.</summary>
    Text,
    /// <summary>Right aligned, as narrow as its widest value.</summary>
    Number,
    /// <summary>Bold monospace piece mark, as narrow as its widest value.</summary>
    Mark,
    /// <summary>A tick box, centred, 40 pt.</summary>
    Check,
}

public sealed record TicketColumn(string Label, ColumnKind Kind);

/// <summary>A table row. <paramref name="Manual"/>: typed on the keypad, so the mark is underlined.</summary>
public sealed record TicketRow(IReadOnlyList<string> Cells, bool Manual);

/// <summary>A footer cell; <paramref name="Span"/> columns wide.</summary>
public sealed record TicketFootCell(string Text, int Span);

public sealed record TicketSection(string? Heading, string? Note, IReadOnlyList<TicketColumn> Columns, IReadOnlyList<TicketRow> Rows, IReadOnlyList<TicketFootCell>? Foot);

/// <summary>
/// A shipping ticket as the iPad app lays it out (js/export.js, ticketModel): a title, a summary of
/// label/value pairs, tables (one per stack), and sign-off lines. The server only draws it, so the
/// paper matches what the iPad prints through AirPrint.
/// </summary>
public sealed record Ticket(
    string Title,
    IReadOnlyList<(string Label, string Value)> Summary,
    IReadOnlyList<TicketSection> Sections,
    IReadOnlyList<string> Signoff,
    string? Job = null,
    string? Load = null,
    string? Footer = null)
{
    public const int MaxRows = 5000;
    const int MaxText = 300;

    /// <summary>Reads the JSON the app sends (see docs/PRINTING.md, "The ticket").</summary>
    public static Ticket Parse(JsonNode? node)
    {
        if (node is not JsonObject o)
            throw new BadTicketException("No ticket was sent.");
        var title = Text(o["title"]) is { Length: > 0 } t ? t : "Shipping Ticket";

        var summary = new List<(string, string)>();
        if (o["summary"] is JsonArray pairs)
            foreach (var p in pairs)
                if (p is JsonArray { Count: >= 2 } pair)
                    summary.Add((Text(pair[0]), Text(pair[1])));

        var sections = new List<TicketSection>();
        var rowCount = 0;
        if (o["sections"] is JsonArray list)
            foreach (var s in list.OfType<JsonObject>())
            {
                var columns = (s["columns"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(c => new TicketColumn(Text(c["label"]), KindOf(Text(c["kind"])))).ToList();
                if (columns.Count is 0 or > 12)
                    throw new BadTicketException("Every table on the ticket needs 1 to 12 columns.");
                var rows = new List<TicketRow>();
                foreach (var r in (s["rows"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    if (++rowCount > MaxRows)
                        throw new BadTicketException($"The ticket has more than {MaxRows} rows.");
                    var cells = (r["cells"] as JsonArray ?? []).Select(Text).ToList();
                    while (cells.Count < columns.Count)
                        cells.Add("");
                    rows.Add(new TicketRow(cells.Take(columns.Count).ToList(), r["manual"]?.GetValueKind() == System.Text.Json.JsonValueKind.True));
                }
                List<TicketFootCell>? foot = null;
                if (s["foot"] is JsonArray f && f.Count > 0)
                {
                    foot = f.OfType<JsonObject>().Select(c => new TicketFootCell(Text(c["text"]), Math.Clamp(c["span"]?.GetValueKind() == System.Text.Json.JsonValueKind.Number ? (int)c["span"]! : 1, 1, columns.Count))).ToList();
                    // A footer wider than the table would draw outside it.
                    var used = 0;
                    foot = foot.TakeWhile(c => (used += c.Span) <= columns.Count).ToList();
                }
                sections.Add(new TicketSection(Opt(s["heading"]), Opt(s["note"]), columns, rows, foot));
            }

        var signoff = (o["signoff"] as JsonArray ?? []).Select(Text).Where(x => x != "").Take(10).ToList();
        if (summary.Count == 0 && sections.Count == 0)
            throw new BadTicketException("The ticket is empty.");
        return new Ticket(title, summary, sections, signoff, Opt(o["job"]), Opt(o["load"]), Opt(o["footer"]));
    }

    static ColumnKind KindOf(string kind) => kind switch
    {
        "num" or "number" => ColumnKind.Number,
        "mark" => ColumnKind.Mark,
        "check" => ColumnKind.Check,
        _ => ColumnKind.Text,
    };

    static string Text(JsonNode? node)
    {
        var s = node switch
        {
            null => "",
            JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
            JsonValue v => v.ToJsonString(),
            _ => "",
        };
        s = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return s.Length > MaxText ? s[..MaxText] : s;
    }

    static string? Opt(JsonNode? node) => Text(node) is { Length: > 0 } s ? s : null;

    /// <summary>A one-page ticket for the admin board's "Print a test page".</summary>
    public static Ticket TestPage(string printer, string by, DateTime at) => new(
        "Test page",
        [("Printer", printer), ("Sent by", by), ("Sent", at.ToString("yyyy-MM-dd HH:mm")), ("Server", Environment.MachineName)],
        [new TicketSection("A sample stack", "3 pcs · 1,234 lbs",
            [new("#", ColumnKind.Number), new("Mark", ColumnKind.Mark), new("Seq", ColumnKind.Text), new("Description", ColumnKind.Text), new("Weight (lbs)", ColumnKind.Number), new("✓", ColumnKind.Check)],
            [
                new(["1", "1001-C1", "1", "Column W10x33 with a description long enough to wrap onto a second line in the table", "500", "✓"], false),
                new(["2", "1002-B1", "1", "Beam W12x26", "634", ""], false),
                new(["3", "1099-X1-", "1", "Typed on the keypad (underlined)", "100", ""], true),
            ],
            [new("Stack subtotal", 4), new("1234", 1), new("", 1)])],
        ["If this page came out, the TERRA server can print here."],
        Footer: "TERRA test page");
}
