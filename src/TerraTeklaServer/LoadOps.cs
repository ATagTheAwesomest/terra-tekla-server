using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraTeklaServer;

/// <summary>
/// Who is changing a load, and the revision the change gets. <c>CodeElsewhere</c> answers which other
/// load of the job already has a piece with a code (its load number), or null: a piece code is one
/// piece, so it can be on one load only.
/// </summary>
public sealed record OpContext(string DeviceId, string DeviceName, long Rev, DateTime Now, Func<string, string?>? CodeElsewhere = null)
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
        "id", "mark", "sequence", "manual", "code", "scannedAt", "scannedBy", "pending", "removing", "addedAt", "rev", "n",
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
        if (op["pallet"] is JsonObject pallet)
            stack.Pallet = new Pallet
            {
                Mark = Required(pallet, "mark"),
                Photos = Math.Max(0, Int(pallet, "photos")),
                Manual = Bool(pallet, "manual"),
            };
        var after = s.Stacks.FindIndex(st => st.Id == Str(op, "afterStackId"));
        s.Stacks.Insert(after < 0 ? s.Stacks.Count : after + 1, stack);
        activity.Add(stack.Pallet is { } p
            ? new("pallet-add", $"Started pallet {name} of {p.Mark} with {p.Photos} photo{(p.Photos == 1 ? "" : "s")} of it{(p.Manual ? ", mark typed on keypad" : "")}", p.Mark, id, name)
            : new("stack-add", $"Added {name}", StackId: id, StackName: name));
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
        if (from != target && target.Pallet is { } pallet && !entry.Mark.Equals(pallet.Mark, StringComparison.OrdinalIgnoreCase))
            throw new OpException($"{target.Name} is a pallet of {pallet.Mark}, so {entry.Mark} can't go in it.");
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
    /// A pallet sends every piece's row at once, in <c>entryIds</c>. A scan of a piece code (jobs with
    /// piece codes) sends <c>code</c> with its one <c>entryId</c>: a code is one piece, so it is refused
    /// when a row on this load or any other load of the job already has it.
    static bool BeginScan(LoadState s, JsonObject op, OpContext ctx)
    {
        var ids = EntryIds(op);
        var mark = Required(op, "mark");
        var code = PieceCode(op);
        if (code is not null)
        {
            if (ids.Count != 1)
                throw new OpException("A piece code is one piece: send it with one entryId.");
            if (FindEntry(s, ids[0]) is null)
            {
                if (FindCode(s, code) is { } here)
                    throw new OpException($"Code {code} is already on this load ({here.Entry.Mark} in {here.Stack.Name}).");
                if (ctx.CodeElsewhere?.Invoke(code) is { } other)
                    throw new OpException($"Code {code} is already on load {other}.");
            }
        }
        var stack = FindStack(s, Str(op, "stackId")) ?? LastPlainStack(s);
        if (stack.Pallet is { } pallet && !mark.Equals(pallet.Mark, StringComparison.OrdinalIgnoreCase))
            throw new OpException($"{stack.Name} is a pallet of {pallet.Mark}, so {mark} can't go in it.");
        var added = false;
        foreach (var id in ids)
        {
            if (FindEntry(s, id) is not null)
                continue;
            stack.Items.Add(new Entry
            {
                Id = id,
                Mark = mark,
                Manual = Bool(op, "manual"),
                Code = code,
                ScannedAt = ctx.NowIso,
                ScannedBy = ctx.DeviceId,
                Pending = true,
                AddedAt = ctx.NowIso,
                Rev = ctx.Rev,
            });
            added = true;
        }
        return added;
    }

    /// <summary>PowerFab loaded the piece: the row gets the piece's details (sequence, weight...).</summary>
    static bool FinishScan(LoadState s, JsonObject op, OpContext ctx, List<ActivityItem> activity)
    {
        var piece = op["piece"] as JsonObject ?? [];
        if (op["entryIds"] is JsonArray)
            return FinishMany(s, EntryIds(op), piece, ctx, activity);
        var id = Required(op, "entryId");
        var hit = FindEntry(s, id);
        if (hit is null)
        {
            // The begin never arrived (it is sent first, so this is rare): add the row now.
            BeginScan(s, new JsonObject { ["entryId"] = id, ["mark"] = Str(op, "mark"), ["manual"] = Bool(op, "manual"), ["stackId"] = Str(op, "stackId"), ["code"] = Str(op, "code") }, ctx);
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
        activity.Add(new("scan", $"Loaded {entry.Mark}{(entry.Code is { } c ? $" code {c}" : "")}{(entry.Sequence != "" ? $" (seq {entry.Sequence})" : "")} into {stack.Name}{(entry.Manual ? ", typed on keypad" : "")}", entry.Mark, stack.Id, stack.Name));
        return true;
    }

    /// <summary>Several rows PowerFab loaded in one go (a pallet): one history line for them all.</summary>
    static bool FinishMany(LoadState s, List<string> ids, JsonObject piece, OpContext ctx, List<ActivityItem> activity)
    {
        var done = new List<(Stack Stack, Entry Entry)>();
        foreach (var id in ids)
        {
            if (FindEntry(s, id) is not { } hit || (!hit.Entry.Pending && hit.Entry.Details is not null))
                continue;
            hit.Entry.Pending = false;
            hit.Entry.Rev = ctx.Rev;
            hit.Entry.Sequence = Str(piece, "sequence");
            hit.Entry.Details = DetailsOf(piece);
            done.Add((hit.Stack, hit.Entry));
        }
        if (done.Count == 0)
            return false;
        var (stack, entry) = done[0];
        activity.Add(new("scan", $"Loaded {done.Count} × {entry.Mark}{(entry.Sequence != "" ? $" (seq {entry.Sequence})" : "")} into {stack.Name}{(entry.Manual ? ", typed on keypad" : "")}", entry.Mark, stack.Id, stack.Name));
        return true;
    }

    static bool FailScan(LoadState s, JsonObject op, List<ActivityItem> activity)
    {
        if (op["entryIds"] is JsonArray)
        {
            var gone = 0;
            (Stack Stack, int Index, Entry Entry)? first = null;
            foreach (var id in EntryIds(op))
            {
                if (FindEntry(s, id) is not { } h)
                    continue;
                first ??= h;
                h.Stack.Items.RemoveAt(h.Index);
                gone++;
            }
            activity.Add(new("refused", gone > 1 ? $"{RefusedText(op)} ({gone} pieces)" : RefusedText(op), first?.Entry.Mark ?? Str(op, "mark"), first?.Stack.Id, first?.Stack.Name));
            return gone > 0;
        }
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
        var changed = false;
        foreach (var id in EntryIds(op))
        {
            var hit = FindEntry(s, id);
            if (hit is null || hit.Value.Entry.Removing == on)
                continue;
            hit.Value.Entry.Removing = on;
            changed = true;
        }
        return changed;
    }

    /// <summary>PowerFab took the piece off the load: the row goes.</summary>
    static bool RemoveEntry(LoadState s, JsonObject op, OpContext ctx, List<ActivityItem> activity)
    {
        var gone = new List<(Stack Stack, Entry Entry)>();
        foreach (var id in EntryIds(op))
        {
            if (FindEntry(s, id) is not { } hit)
                continue;
            var (stack, index, entry) = hit;
            stack.Items.RemoveAt(index);
            s.Removed.Add(new Removal(entry.Mark, entry.Sequence, ctx.Rev, ctx.Now));
            gone.Add((stack, entry));
        }
        if (gone.Count == 0)
            return false;
        var (from, first) = gone[0];
        activity.Add(new("unload", $"Took {(gone.Count > 1 ? $"{gone.Count} × " : "")}{first.Mark} off the load (from {from.Name})", first.Mark, from.Id, from.Name));
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
            // Rows without a piece code go first: a row with one stands for a piece that was scanned.
            foreach (var coded in new[] { false, true })
                for (var i = rows.Count - 1; i >= 0 && extra > 0; i--)
                {
                    if ((rows[i].Entry.Code is not null) != coded || Protected(rows[i].Entry))
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
                    if (last.Items.Count == 0 && last.Pallet is null)
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
        // A piece code checks off one exact piece, once: the same code again (another iPad got there
        // first) changes nothing.
        var code = PieceCode(op);
        if (code is not null && s.TruckCheck.Scans.Any(x => code.Equals(x.Code, StringComparison.OrdinalIgnoreCase)))
            return false;
        // A pallet is checked with one scan for all its pieces.
        var qty = code is null ? Math.Clamp(Int(op, "qty"), 1, MaxPallet) : 1;
        s.TruckCheck.Scans.Add(new CheckScan { Id = id, Mark = mark, Manual = Bool(op, "manual"), ScannedAt = ctx.NowIso, ScannedBy = ctx.DeviceId, Qty = qty, Code = code });
        s.TruckCheck.UpdatedAt = ctx.NowIso;
        activity.Add(new("check", qty > 1 ? $"Truck check: pallet of {qty} × {mark}" : code is not null ? $"Truck check: {mark} code {code}" : $"Truck check: {mark}", mark));
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

    /// <summary>The most pieces one pallet (one operation) can have.</summary>
    public const int MaxPallet = 2000;

    /// <summary>The rows an operation is about: <c>entryIds</c> (a pallet's pieces) or the one <c>entryId</c>.</summary>
    static List<string> EntryIds(JsonObject op)
    {
        if (op["entryIds"] is not JsonArray list)
            return [Required(op, "entryId")];
        if (list.Count == 0)
            throw new OpException($"\"entryIds\" is empty for {Str(op, "op")}.");
        if (list.Count > MaxPallet)
            throw new OpException($"At most {MaxPallet} pieces at once.");
        var ids = new List<string>(list.Count);
        foreach (var node in list)
        {
            var id = node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";
            if (id == "" || id.Length > 200)
                throw new OpException($"\"entryIds\" has an empty or too long id for {Str(op, "op")}.");
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>The longest piece code accepted.</summary>
    public const int MaxCodeLength = 200;

    /// <summary>An operation's <c>code</c> (a piece code), trimmed; null when it has none.</summary>
    static string? PieceCode(JsonObject op)
    {
        var code = Str(op, "code").Trim();
        if (code == "")
            return null;
        if (code.Length > MaxCodeLength)
            throw new OpException($"A piece code is at most {MaxCodeLength} characters.");
        return code;
    }

    static (Stack Stack, Entry Entry)? FindCode(LoadState s, string code)
    {
        foreach (var stack in s.Stacks)
            foreach (var entry in stack.Items)
                if (code.Equals(entry.Code, StringComparison.OrdinalIgnoreCase))
                    return (stack, entry);
        return null;
    }

    static bool NameTaken(LoadState s, string name) => s.Stacks.Any(st => st.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Next unused "Stack &lt;load&gt; - N", as the app names them.</summary>
    public static string NextStackName(LoadState s)
    {
        var n = s.Stacks.Count + 1;
        while (NameTaken(s, $"Stack {s.LoadNumber} - {n}"))
            n++;
        return $"Stack {s.LoadNumber} - {n}";
    }

    /// <summary>Where a scan for a stack that is gone lands: the last stack that isn't a pallet (a new one if all are).</summary>
    static Stack LastPlainStack(LoadState s)
    {
        if (s.Stacks.LastOrDefault(st => st.Pallet is null) is { } plain)
            return plain;
        var stack = new Stack { Id = $"plain-{s.Stacks.Count}-{s.Rev}", Name = NextStackName(s) };
        s.Stacks.Add(stack);
        return stack;
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
