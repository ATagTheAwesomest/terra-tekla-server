using System.Text.Json.Nodes;

namespace TerraTeklaServer;

// Writing a job's piece codes (barcodes) into PowerFab, so PowerFab has them too (docs/PIECE-CODES.md,
// "Writing codes into PowerFab").
//
// PowerFab's API has no command that edits an item's own fields (cost code, remark, category,
// reference number, pay category, user fields): its 252 commands were all listed and tried
// (tekla-conversion-layer docs/powerfab). The one per-piece value it lets a program write is an
// instance's location: SetInstanceLocation with JobNumber, MainMark, Sequence, InstanceNumbers and
// InstanceLocation. A mark with 9 pieces in a sequence has instances 1 to 9, and each gets one code.
//
// Which instance gets which code: the codes of a mark and sequence in code order (C9227621Q1 before
// C9227622Q1) take the lowest free instance numbers. An instance written once keeps its code, so a
// later import only fills the instances still free.

/// <summary>A mark in one sequence and how many pieces PowerFab has of it there (from GetAssemblies).</summary>
public sealed record SlotAssembly(string MainMark, string Sequence, int Quantity);

/// <summary>One code to write, or written: which instance of its mark and sequence holds it.</summary>
public sealed record SlotWrite(string Code, string MainMark, string Sequence, int Instance);

/// <summary>A code that can't be written, and why.</summary>
public sealed record SlotSkip(string Code, string Mark, string? Sequence, string Reason);

/// <summary>A code written into PowerFab, as this server remembers it.</summary>
public sealed record SlotWritten(string Code, string MainMark, string Sequence, int Instance, string WrittenAt, string? WrittenBy);

/// <summary>What writing a job's codes would do: what to write, what is already there, what can't be.</summary>
public sealed record SlotPlan(List<SlotWrite> Write, List<SlotWritten> Done, List<SlotSkip> Skipped);

public static class PowerFabSlot
{
    /// <summary>The PowerFab command that writes the slot.</summary>
    public const string Command = "SetInstanceLocation";

    /// <summary>The slot's name as PowerFab shows it.</summary>
    public const string Name = "Instance location";

    /// <summary>At most this many codes are written per press, so one request doesn't run for minutes.</summary>
    public const int MaxPerRun = 1000;

    /// <summary>The request that puts <paramref name="value"/> ("" clears it) on one instance.</summary>
    public static string Request(string job, string mainMark, string sequence, int instance, string value) =>
        new JsonObject
        {
            ["JobNumber"] = job,
            ["MainMark"] = mainMark,
            ["Sequence"] = sequence,
            ["InstanceNumbers"] = instance.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["InstanceLocation"] = value,
        }.ToJsonString();

    /// <summary>
    /// Every mark and sequence in a GetAssemblies reply with its quantity there. Reads Assembly
    /// wherever it sits, as one object or a list, and AssemblySequence the same way.
    /// </summary>
    public static List<SlotAssembly> Assemblies(JsonNode? reply)
    {
        var found = new Dictionary<(string, string), SlotAssembly>();
        void Walk(JsonNode? node)
        {
            if (node is JsonArray array)
                foreach (var n in array)
                    Walk(n);
            else if (node is JsonObject obj)
                foreach (var (key, value) in obj)
                {
                    if (key == "Assembly")
                        foreach (var a in List(value))
                            Add(a as JsonObject);
                    else
                        Walk(value);
                }
        }
        void Add(JsonObject? a)
        {
            var mark = Text(a?["MainMark"]).Trim();
            if (a is null || mark == "")
                return;
            foreach (var s in List(a["AssemblySequence"]))
            {
                var seq = Text(s?["Sequence"]).Trim();
                if (seq == "" || !double.TryParse(Text(s?["Quantity"]), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var qty))
                    continue;
                var key = (mark.ToUpperInvariant(), seq);
                found[key] = found.TryGetValue(key, out var had) ? had with { Quantity = had.Quantity + (int)qty } : new SlotAssembly(mark, seq, (int)qty);
            }
        }
        Walk(reply);
        return found.Values.ToList();
    }

    /// <summary>
    /// What writing <paramref name="codes"/> would do, given PowerFab's <paramref name="assemblies"/>
    /// and the codes already <paramref name="written"/>.
    /// </summary>
    public static SlotPlan Plan(IEnumerable<PieceCode> codes, IReadOnlyList<SlotAssembly> assemblies, IReadOnlyList<SlotWritten> written)
    {
        var byMark = assemblies
            .GroupBy(a => CodeList.CleanMark(a.MainMark), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var done = written.ToDictionary(w => w.Code, StringComparer.OrdinalIgnoreCase);
        // Instances already holding a code, per mark and sequence.
        var taken = new Dictionary<(string, string), HashSet<int>>();
        HashSet<int> Taken(string mark, string seq)
        {
            var key = (CodeList.CleanMark(mark).ToUpperInvariant(), seq);
            if (!taken.TryGetValue(key, out var set))
                taken[key] = set = [];
            return set;
        }

        var plan = new SlotPlan([], [], []);
        var todo = new List<PieceCode>();
        foreach (var code in codes.OrderBy(c => c.Code, StringComparer.OrdinalIgnoreCase))
        {
            // Written before for the same mark (and sequence, when the list gives one): already there.
            if (done.TryGetValue(code.Code, out var w)
                && CodeList.CleanMark(w.MainMark).Equals(CodeList.CleanMark(code.Mark), StringComparison.OrdinalIgnoreCase)
                && (code.Sequence is null || code.Sequence.Trim() == w.Sequence))
            {
                plan.Done.Add(w);
                Taken(w.MainMark, w.Sequence).Add(w.Instance);
            }
            else
                todo.Add(code);
        }

        foreach (var code in todo)
        {
            var mark = CodeList.CleanMark(code.Mark);
            var seq = code.Sequence?.Trim();
            if (!byMark.TryGetValue(mark, out var sequences))
            {
                plan.Skipped.Add(new(code.Code, mark, seq, $"PowerFab has no mark {mark} on this job."));
                continue;
            }
            SlotAssembly? target;
            if (string.IsNullOrEmpty(seq))
            {
                if (sequences.Count > 1)
                {
                    plan.Skipped.Add(new(code.Code, mark, seq, $"No sequence on the list, and {mark} is in sequences {string.Join(", ", sequences.Select(s => s.Sequence))}."));
                    continue;
                }
                target = sequences[0];
            }
            else
            {
                target = sequences.FirstOrDefault(s => s.Sequence == seq);
                if (target is null)
                {
                    plan.Skipped.Add(new(code.Code, mark, seq, $"PowerFab has {mark} in sequence {string.Join(", ", sequences.Select(s => s.Sequence))}, not {seq}."));
                    continue;
                }
            }
            var used = Taken(target.MainMark, target.Sequence);
            var instance = Enumerable.Range(1, Math.Max(target.Quantity, 0)).FirstOrDefault(i => !used.Contains(i));
            if (instance == 0)
            {
                plan.Skipped.Add(new(code.Code, mark, target.Sequence, $"More codes than the {target.Quantity} piece{(target.Quantity == 1 ? "" : "s")} of {mark} in sequence {target.Sequence}."));
                continue;
            }
            used.Add(instance);
            plan.Write.Add(new(code.Code, target.MainMark, target.Sequence, instance));
        }
        return plan;
    }

    static IEnumerable<JsonNode?> List(JsonNode? node) => node is JsonArray a ? a : node is null ? Array.Empty<JsonNode?>() : new[] { node };

    static string Text(JsonNode? node) => node switch
    {
        JsonValue v => v.ToString(),
        JsonObject o when o["#text"] is JsonValue t => t.ToString(),
        _ => "",
    };
}
