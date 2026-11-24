using System.Text.Json;

namespace TerraTeklaServer;

/// <summary>What the Labels tab asks to print: one mark (and sequence), or chosen codes, or the whole job.
/// <c>Positions</c>: the free positions (1-30) on the first sheet to fill, in order; empty = a new sheet.</summary>
public sealed record LabelRequest(string? Mark, string? Seq, List<string>? Codes, List<int>? Positions);

/// <summary>The positions (1-30) already used on the part-used label sheet in the printer.</summary>
public sealed record LabelSheetState(List<int> Used);

// Routes under /api/admin (admin code required) for the Labels tab. See docs/LABELS.md.
public static class LabelsApi
{
    const string SheetKey = "labels.sheet";

    public static void Map(RouteGroupBuilder admin, Store store)
    {
        // The job's marks with how many pieces (codes) each has, for the picker.
        admin.MapGet("/jobs/{job}/labels/marks", (string job) =>
        {
            var codes = store.Codes(job);
            var rows = codes
                .GroupBy(c => (Mark: CodeList.CleanMark(c.Mark), Seq: c.Sequence?.Trim() ?? ""))
                .OrderBy(g => g.Key.Mark, NumericOrder.Instance).ThenBy(g => g.Key.Seq, NumericOrder.Instance)
                .Select(g => new { mark = g.Key.Mark, sequence = g.Key.Seq, count = g.Count() });
            var labels = Labels.For(job, codes, store.Bom(job));
            return Results.Json(new
            {
                job,
                marks = rows,
                total = labels.Count,
                missingWeight = labels.Count(l => l.Weight is null),
                missingDescription = labels.Count(l => string.IsNullOrWhiteSpace(l.Description)),
            }, Json.Options);
        });

        // The labels a pick would print (one mark, or chosen codes), to show and tick before printing.
        admin.MapGet("/jobs/{job}/labels", (string job, string? mark, string? seq) =>
            Results.Json(Labels.For(job, store.Codes(job), store.Bom(job), mark, seq).Select(View), Json.Options));

        // The printable page. Codes not on the job's list are refused by name, so a typo shows.
        admin.MapPost("/jobs/{job}/labels/sheet", (string job, LabelRequest body) =>
        {
            var positions = body.Positions ?? [];
            if (positions.Any(p => p is < 1 or > Labels.PerSheet))
                return Results.Problem($"A label position is 1 to {Labels.PerSheet}.", statusCode: 400);
            var codes = store.Codes(job);
            List<string>? only = null;
            if (body.Codes is { Count: > 0 })
            {
                only = body.Codes.Select(c => c.Trim()).Where(c => c != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var known = new HashSet<string>(codes.Select(c => c.Code), StringComparer.OrdinalIgnoreCase);
                var unknown = only.Where(c => !known.Contains(c)).ToList();
                if (unknown.Count > 0)
                    return Results.Problem($"Not on job {job}'s code list: {string.Join(", ", unknown.Take(10))}{(unknown.Count > 10 ? $" and {unknown.Count - 10} more" : "")}.", statusCode: 400);
            }
            var mark = string.IsNullOrWhiteSpace(body.Mark) ? null : body.Mark;
            var seq = mark is null ? null : body.Seq;
            var labels = Labels.For(job, codes, store.Bom(job), mark, seq, only);
            if (labels.Count == 0)
                return Results.Problem("Nothing to print: this job has no piece codes for that pick. Add them on the job's page (Piece codes).", statusCode: 404);
            var title = $"Labels {job}" + (mark is null ? "" : $" {CodeList.CleanMark(mark)}") + (only is null ? "" : $" ({labels.Count})");
            return Results.Content(Labels.Sheet(job, labels, positions, title), "text/html; charset=utf-8");
        });

        // The part-used sheet, remembered between prints (and between PCs).
        admin.MapGet("/labels/sheet-state", () => Results.Json(ReadState(store), Json.Options));

        admin.MapPut("/labels/sheet-state", (LabelSheetState body) =>
        {
            var used = (body.Used ?? []).Where(p => p is >= 1 and <= Labels.PerSheet).Distinct().Order().ToList();
            // A full sheet is used up: the next print starts on a new one.
            if (used.Count == Labels.PerSheet) used = [];
            store.SetMeta(SheetKey, used.Count == 0 ? null : JsonSerializer.Serialize(used));
            return Results.Json(new LabelSheetState(used), Json.Options);
        });
    }

    static LabelSheetState ReadState(Store store)
    {
        try
        {
            return new LabelSheetState(store.Meta(SheetKey) is { } json ? JsonSerializer.Deserialize<List<int>>(json) ?? [] : []);
        }
        catch (JsonException)
        {
            return new LabelSheetState([]);
        }
    }

    static object View(PieceLabel l) => new { code = l.Code, mark = l.Mark, sequence = l.Sequence, index = l.Index, of = l.Of, description = l.Description, weight = l.Weight };
}
