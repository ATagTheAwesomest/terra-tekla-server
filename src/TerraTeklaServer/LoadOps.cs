using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraTeklaServer;

/// <summary>Who is changing a load, and the revision the change gets.</summary>
public sealed record OpContext(string DeviceId, string DeviceName, long Rev, DateTime Now)
{
    public string NowIso => Now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}

/// <summary>One line of the activity history an operation produced.</summary>
public sealed record ActivityItem(string Kind, string Text, string? Mark = null, string? StackId = null, string? StackName = null);

/// <summary>A request the app sent that can't be applied (bad operation, shipped load...).</summary>
public sealed class OpException(string message) : Exception(message);

/// <summary>
/// The operations the app sends to change a load's stacks (POST /api/loads/{id}/ops). Every operation
/// carries an <c>opId</c>; one already applied is skipped, so the app can resend after a dropped
/// connection. Operations name stacks and rows by the ids the app made up, so two iPads changing the
/// same load at once never step on each other's rows. The list and every field are in docs/SETUP.md.
/// </summary>
public static class LoadOps
{
    const int RecentOpsKept = 300;
    /// <summary>How long a scan waiting on PowerFab is protected from being undone by a stale read.</summary>
    public static readonly TimeSpan PendingProtection = TimeSpan.FromMinutes(2);
    static readonly TimeSpan RemovalKept = TimeSpan.FromMinutes(10);

    // Fields of a piece that are the row's own, not PowerFab details.
    static readonly HashSet<string> OwnFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "mark", "sequence", "manual", "scannedAt", "scannedBy", "pending", "removing", "addedAt", "rev", "n",
    };

    /// <summary>A load's first stack id. The app uses the same one for a load it hasn't heard about yet.</summary>
    public static string DefaultStackId(string loadId) => $"first-{loadId}";

    public static LoadState NewState(string loadId, string job, string loadNumber) => new()
    {
        LoadId = loadId,
        Job = job,
        LoadNumber = loadNumber,
        Stacks = [new Stack { Id = DefaultStackId(loadId), Name = $"Stack {loadNumber} - 1" }],
    };

    /// <summary>
    /// Applies one operation to <paramref name="s"/>. Returns the history lines it made; an empty list
    /// with <c>changed</c> false means nothing changed (a repeat, or nothing to do).
    /// </summary>
    public static (bool Changed, List<ActivityItem> Activity) Apply(LoadState s, JsonObject op, OpContext ctx)
    {
        var name = Str(op, "op");
        var opId = Str(op, "opId");
        if (opId != "" && s.RecentOps.Contains(opId))
            return (false, []);

        var activity = new List<ActivityItem>();
        var changed = name switch
        {
            "addStack" => AddStack(s, op, ctx, activity),
            "rename" => Rename(s, op, activity),
            "removeStack" => RemoveStack(s, op, activity),
            "moveEntryTo" => MoveEntryTo(s, op, activity),
            "beginScan" => BeginScan(s, op, ctx),
            "finishScan" => FinishScan(s, op, ctx, activity),
            "failScan" => FailScan(s, op, activity),
            "refused" => Log(activity, "refused", RefusedText(op), Str(op, "mark")),
            "markRemoving" => SetRemoving(s, op, true),
            "unmarkRemoving" => SetRemoving(s, op, false),
            "removeEntry" => RemoveEntry(s, op, ctx, activity),
            "sync" => Sync(s, op, ctx, activity),
            "startCheck" => StartCheck(s, ctx),
            "resetCheck" => ResetCheck(s, ctx, activity),
            "addCheck" => AddCheck(s, op, ctx, activity),
            "removeCheck" => RemoveCheck(s, op, ctx, activity),
            "log" => Log(activity, LogKind(op), Str(op, "text"), Str(op, "mark")),
            "clear" => Clear(s, ctx, activity),
            _ => throw new OpException($"Unknown operation \"{name}\"."),
        };

        if (changed)
        {
            s.Rev = ctx.Rev;
            s.UpdatedAt = ctx.NowIso;
        }
        if (opId != "" && (changed || activity.Count > 0))
        {
            s.RecentOps.Add(opId);
            if (s.RecentOps.Count > RecentOpsKept)
                s.RecentOps.RemoveRange(0, s.RecentOps.Count - RecentOpsKept);
        }
        s.Removed.RemoveAll(r => ctx.Now - r.At > RemovalKept);
        return (changed, activity);
    }

    // ---- Stacks ------------------------------------------------------------------

    static bool AddStack(LoadState s, JsonObject op, OpContext ctx, List<ActivityItem> activity)
    {
        var id = Required(op, "stackId");
        if (s.Stacks.Any(st => st.Id == id))
            return false;
        var wanted = Str(op, "name").Trim();
        var name = wanted != "" && !NameTaken(s, wanted) ? wanted : NextStackName(s);
        var stack = new Stack { Id = id, Name = name, CreatedBy = ctx.DeviceId };
        var after = s.Stacks.FindIndex(st => st.Id == Str(op, "afterStackId"));
        s.Stacks.Insert(after < 0 ? s.Stacks.Count : after + 1, stack);
        activity.Add(new("stack-add", $"Added {name}", StackId: id, StackName: name));
        return true;
    }

    static bool Rename(LoadState s, JsonObject op, List<ActivityItem> activity)
    {
        var stack = FindStack(s, Required(op, "stackId"));
        var name = string.Join(' ', Str(op, "name").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (stack is null || name == "" || name == stack.Name)
            return false;
        if (name.Length > 60)
            name = name[..60];
        activity.Add(new("stack-rename", $"Renamed {stack.Name} to {name}", StackId: stack.Id, StackName: name));
        stack.Name = name;
        return true;
    }

    /// <summary>The app takes the stack's pieces off the load in PowerFab first.</summary>
    static bool RemoveStack(LoadState s, JsonObject op, List<ActivityItem> activity)
    {
        var stack = FindStack(s, Required(op, "stackId"));
        if (stack is null)
            return false;
        s.Stacks.Remove(stack);
        if (s.Stacks.Count == 0)
            s.Stacks.Add(new Stack { Id = $"{stack.Id}-new", Name = $"Stack {s.LoadNumber} - 1" });
        activity.Add(new("stack-delete", $"Deleted {stack.Name}{(stack.Items.Count > 0 ? $" ({stack.Items.Count} pieces still in it)" : "")}", StackId: stack.Id, StackName: stack.Name));
        return true;
    }

    /// <summary>
    /// Moves a row so it sits at <c>index</c> of a stack, counting rows as they are before the move
    /// (the same as the app's moveEntryTo). Sent again, it finds the row already there and does nothing.
    /// </summary>
    static bool MoveEntryTo(LoadState s, JsonObject op, List<ActivityItem> activity)
    {
        var hit = FindEntry(s, Required(op, "entryId"));
        var target = FindStack(s, Required(op, "stackId"));
        if (hit is null || target is null)
            return false;
        var (from, fromIndex, entry) = hit.Value;
        var at = Math.Clamp(Int(op, "index"), 0, target.Items.Count);
        if (from == target && fromIndex < at)
            at--;
        if (from == target && fromIndex == at)
            return false;
        from.Items.RemoveAt(fromIndex);
        target.Items.Insert(at, entry);
        activity.Add(from == target
            ? new("move", $"Moved {entry.Mark} to row {at + 1} of {target.Name}", entry.Mark, target.Id, target.Name)
            : new("move", $"Moved {entry.Mark} from {from.Name} to {target.Name}", entry.Mark, target.Id, target.Name));
        return true;
    }

    // ---- Scanning -----------------------------------------------------------------

    /// <summary>
    /// A scan the app is about to send to PowerFab: a row in the scanning iPad's active stack, shown
    /// on every iPad as "loading" straight away. finishScan or failScan follows.
    /// </summary>
    static bool BeginScan(LoadState s, JsonObject op, OpContext ctx)
    {
        var id = Required(op, "entryId");
        if (FindEntry(s, id) is not null)
            return false;
        var stack = FindStack(s, Str(op, "stackId")) ?? s.Stacks[^1];
        stack.Items.Add(new Entry
        {
            Id = id,
            Mark = Required(op, "mark"),
            Manual = Bool(op, "manual"),
            ScannedAt = ctx.NowIso,
            ScannedBy = ctx.DeviceId,
            Pending = true,
            AddedAt = ctx.NowIso,
            Rev = ctx.Rev,
        });
        return true;
    }

    /// <summary>PowerFab loaded the piece: the row gets the piece's details (sequence, weight...).</summary>
    static bool FinishScan(LoadState s, JsonObject op, OpContext ctx, List<ActivityItem> activity)
    {
        var id = Required(op, "entryId");
        var piece = op["piece"] as JsonObject ?? [];
        var hit = FindEntry(s, id);
        if (hit is null)
        {
            // The begin never arrived (it is sent first, so this is rare): add the row now.
            BeginScan(s, new JsonObject { ["entryId"] = id, ["mark"] = Str(op, "mark"), ["manual"] = Bool(op, "manual"), ["stackId"] = Str(op, "stackId") }, ctx);
            hit = FindEntry(s, id);
            if (hit is null)
                return false;
        }
        var (stack, _, entry) = hit.Value;
        if (!entry.Pending && entry.Details is not null)
            return false;
        entry.Pending = false;
        entry.Rev = ctx.Rev;
        entry.Sequence = Str(piece, "sequence");
        entry.Details = DetailsOf(piece);
        activity.Add(new("scan", $"Loaded {entry.Mark}{(entry.Sequence != "" ? $" (seq {entry.Sequence})" : "")} into {stack.Name}{(entry.Manual ? ", typed on keypad" : "")}", entry.Mark, stack.Id, stack.Name));
        return true;
    }

    static bool FailScan(LoadState s, JsonObject op, List<ActivityItem> activity)
    {
        var hit = FindEntry(s, Required(op, "entryId"));
        var mark = hit?.Entry.Mark ?? Str(op, "mark");
        activity.Add(new("refused", RefusedText(op), mark, hit?.Stack.Id, hit?.Stack.Name));
        if (hit is null)
            return false;
        hit.Value.Stack.Items.RemoveAt(hit.Value.Index);
        return true;
    }

    static string RefusedText(JsonObject op)
    {
        var title = Str(op, "title");
        var message = Str(op, "message");
        return $"Not added: {Str(op, "mark")}. {title}{(title != "" && message != "" ? ": " : "")}{message}".Trim();
    }

    static bool SetRemoving(LoadState s, JsonObject op, bool on)
    {
        var hit = FindEntry(s, Required(op, "entryId"));
        if (hit is null || hit.Value.Entry.Removing == on)
            return false;
        hit.Value.Entry.Removing = on;
        return true;
    }

    /// <summary>PowerFab took the piece off the load: the row goes.</summary>
    static bool RemoveEntry(LoadState s, JsonObject op, OpContext ctx, List<ActivityItem> activity)
    {
        var hit = FindEntry(s, Required(op, "entryId"));
        if (hit is null)
            return false;
        var (stack, index, entry) = hit.Value;
        stack.Items.RemoveAt(index);
        s.Removed.Add(new Removal(entry.Mark, entry.Sequence, ctx.Rev, ctx.Now));
        activity.Add(new("unload", $"Took {entry.Mark} off the load (from {stack.Name})", entry.Mark, stack.Id, stack.Name));
        return true;
    }

    // ---- Bringing the stacks in line with PowerFab -------------------------------------

    /// <summary>
    /// Makes the stacks hold exactly the pieces PowerFab has loaded on the load. The app sends what
    /// it read (<c>pieces</c>: mark, sequence, n loaded and not returned, details) and the revision of
    /// this load it had when it started reading (<c>knownRev</c>). A row changed after that revision,
    /// a scan still waiting on PowerFab, or a row being taken off is never removed: the read may simply
    /// be older than that change. Likewise a piece taken off after that revision isn't put back.
    /// Pieces PowerFab has loaded that no row stands for go into a stack at the bottom: the last
    /// stack if it is empty, else a new one.
    /// </summary>
    static bool Sync(LoadState s, JsonObject op, OpContext ctx, List<ActivityItem> activity)
    {
        var knownRev = op["knownRev"]?.GetValueKind() == JsonValueKind.Number ? op["knownRev"]!.GetValue<long>() : 0;
        var want = new Dictionary<string, (int N, JsonObject Piece)>();
        foreach (var node in op["pieces"] as JsonArray ?? [])
        {
            if (node is not JsonObject piece)
                continue;
            var n = Int(piece, "n");
            var mark = Str(piece, "mark");
            if (n <= 0 || mark == "")
                continue;
            var key = Key(mark, Str(piece, "sequence"));
            want[key] = (want.TryGetValue(key, out var w) ? w.N + n : n, piece);
        }

        bool Protected(Entry e) =>
            e.Removing || e.Rev > knownRev || (e.Pending && DateTime.TryParse(e.AddedAt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) && ctx.Now - at < PendingProtection);

        // Scans still waiting on PowerFab don't know their sequence yet: they count for their mark.
        var pending = new Dictionary<string, int>(StringComparer.Ordinal);
        var have = new Dictionary<string, List<(Stack Stack, Entry Entry)>>();
        var stale = new List<(Stack Stack, Entry Entry)>();
        foreach (var stack in s.Stacks)
            foreach (var entry in stack.Items)
            {
                if (entry.Pending)
                {
                    if (Protected(entry))
                        pending[entry.Mark] = pending.GetValueOrDefault(entry.Mark) + 1;
                    else
                        stale.Add((stack, entry));
                    continue;
                }
                var key = Key(entry.Mark, entry.Sequence);
                if (!have.TryGetValue(key, out var list))
                    have[key] = list = [];
                list.Add((stack, entry));
            }

        var removed = 0;
        var removedMarks = new List<string>();
        // A scan whose app never said how PowerFab answered (closed mid-scan) is dropped once the
        // protection runs out; if PowerFab did load it, it comes back below as an unscanned row.
        foreach (var (stack, entry) in stale)
        {
            stack.Items.Remove(entry);
            removed++;
            removedMarks.Add(entry.Mark);
        }
        foreach (var (key, rows) in have)
        {
            var extra = rows.Count - (want.TryGetValue(key, out var w) ? w.N : 0);
            for (var i = rows.Count - 1; i >= 0 && extra > 0; i--)
            {
                if (Protected(rows[i].Entry))
                    continue;
                rows[i].Stack.Items.Remove(rows[i].Entry);
                removedMarks.Add(rows[i].Entry.Mark);
                removed++;
                extra--;
            }
        }

        Stack? target = null;
        var added = 0;
        foreach (var (key, (n, piece)) in want)
        {
            var mark = Str(piece, "mark");
            var missing = n - (have.TryGetValue(key, out var rows) ? rows.Count : 0);
            missing -= s.Removed.Count(r => r.Rev > knownRev && Key(r.Mark, r.Sequence) == key);
            var waiting = Math.Min(Math.Max(missing, 0), pending.GetValueOrDefault(mark));
            pending[mark] = pending.GetValueOrDefault(mark) - waiting;
            missing -= waiting;
            for (; missing > 0; missing--)
            {
                if (target is null)
                {
                    var last = s.Stacks[^1];
                    if (last.Items.Count == 0)
                        target = last;
                    else
                    {
                        target = new Stack { Id = $"auto-{ctx.Rev}-{s.Stacks.Count}", Name = NextStackName(s) };
                        s.Stacks.Add(target);
                    }
                }
                target.Items.Add(new Entry
                {
                    Id = $"pf-{ctx.Rev}-{added}",
                    Mark = mark,
                    Sequence = Str(piece, "sequence"),
                    AddedAt = ctx.NowIso,
                    Rev = ctx.Rev,
                    Details = DetailsOf(piece),
                });
                added++;
            }
        }

        if (added > 0)
            activity.Add(new("auto-stack", $"{added} piece{(added == 1 ? "" : "s")} loaded in PowerFab itself put in {target!.Name}", StackId: target.Id, StackName: target.Name));
        if (removed > 0)
            activity.Add(new("sync-remove", $"{removed} piece{(removed == 1 ? "" : "s")} PowerFab no longer has loaded taken off the stacks: {string.Join(", ", removedMarks.Distinct())}"));
        return added > 0 || removed > 0;
    }

    // ---- Shared truck check ------------------------------------------------------------

    static bool StartCheck(LoadState s, OpContext ctx)
    {
        if (s.TruckCheck is not null)
            return false;
        s.TruckCheck = new TruckCheck { StartedAt = ctx.NowIso, UpdatedAt = ctx.NowIso };
        return true;
    }

    static bool ResetCheck(LoadState s, OpContext ctx, List<ActivityItem> activity)
    {
        s.TruckCheck = new TruckCheck { StartedAt = ctx.NowIso, UpdatedAt = ctx.NowIso };
        activity.Add(new("check-reset", "Cleared the truck check"));
        return true;
    }

    static bool AddCheck(LoadState s, JsonObject op, OpContext ctx, List<ActivityItem> activity)
    {
        var id = Required(op, "scanId");
        s.TruckCheck ??= new TruckCheck { StartedAt = ctx.NowIso };
        if (s.TruckCheck.Scans.Any(x => x.Id == id))
            return false;
        var mark = Required(op, "mark");
        s.TruckCheck.Scans.Add(new CheckScan { Id = id, Mark = mark, Manual = Bool(op, "manual"), ScannedAt = ctx.NowIso, ScannedBy = ctx.DeviceId });
        s.TruckCheck.UpdatedAt = ctx.NowIso;
        activity.Add(new("check", $"Truck check: {mark}", mark));
        return true;
    }

    static bool RemoveCheck(LoadState s, JsonObject op, OpContext ctx, List<ActivityItem> activity)
    {
        var id = Required(op, "scanId");
        var scan = s.TruckCheck?.Scans.FirstOrDefault(x => x.Id == id);
        if (scan is null)
            return false;
        s.TruckCheck!.Scans.Remove(scan);
        s.TruckCheck.UpdatedAt = ctx.NowIso;
        activity.Add(new("check-undo", $"Truck check: removed the check of {scan.Mark}", scan.Mark));
        return true;
    }

    // ---- Other ---------------------------------------------------------------------

    /// <summary>The load was deleted in PowerFab: its stacks go, its history stays.</summary>
    static bool Clear(LoadState s, OpContext ctx, List<ActivityItem> activity)
    {
        s.Stacks = [new Stack { Id = DefaultStackId(s.LoadId), Name = $"Stack {s.LoadNumber} - 1" }];
        s.TruckCheck = null;
        activity.Add(new("load-delete", $"Deleted load {s.LoadNumber}"));
        return true;
    }

    // History only, for things done in PowerFab the stacks don't hold: ship, unship, return,
    // create load, a full truck check. Any kind the app sends is kept as it is.
    static string LogKind(JsonObject op)
    {
        var kind = Str(op, "kind");
        return kind == "" ? "note" : kind.Length > 30 ? kind[..30] : kind;
    }

    static bool Log(List<ActivityItem> activity, string kind, string text, string mark)
    {
        activity.Add(new(kind, text.Length > 500 ? text[..500] : text, mark == "" ? null : mark));
        return false;
    }

    // ---- Helpers --------------------------------------------------------------------

    static string Key(string mark, string sequence) => $"{mark}|{sequence}";

    static bool NameTaken(LoadState s, string name) => s.Stacks.Any(st => st.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Next unused "Stack &lt;load&gt; - N", as the app names them.</summary>
    public static string NextStackName(LoadState s)
    {
        var n = s.Stacks.Count + 1;
        while (NameTaken(s, $"Stack {s.LoadNumber} - {n}"))
            n++;
        return $"Stack {s.LoadNumber} - {n}";
    }

    static Stack? FindStack(LoadState s, string id) => id == "" ? null : s.Stacks.FirstOrDefault(st => st.Id == id);

    static (Stack Stack, int Index, Entry Entry)? FindEntry(LoadState s, string id)
    {
        foreach (var stack in s.Stacks)
        {
            var i = stack.Items.FindIndex(e => e.Id == id);
            if (i >= 0)
                return (stack, i, stack.Items[i]);
        }
        return null;
    }

    static Dictionary<string, JsonElement> DetailsOf(JsonObject piece)
    {
        var details = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in piece)
            if (!OwnFields.Contains(k) && v is not null)
                details[k] = JsonSerializer.SerializeToElement(v);
        return details;
    }

    public static string Str(JsonObject o, string key) => o[key] is JsonValue v
        ? v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.Number => v.ToJsonString(),
            _ => "",
        }
        : "";

    static string Required(JsonObject o, string key)
    {
        var value = Str(o, key);
        if (value == "")
            throw new OpException($"\"{key}\" is required for {Str(o, "op")}.");
        if (value.Length > 200)
            throw new OpException($"\"{key}\" is too long.");
        return value;
    }

    static int Int(JsonObject o, string key) => int.TryParse(Str(o, key), out var n) ? n : 0;

    static bool Bool(JsonObject o, string key) => o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.True;
}
