using System.Text.Json.Nodes;

namespace TerraTeklaServer.Tests;

public class LoadOpsTests
{
    static readonly DateTime T0 = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    long rev = 100;

    OpContext Ctx(string device = "dev-a", DateTime? at = null) => new(device, device, ++rev, at ?? T0);

    static JsonObject Op(string name, object? fields = null)
    {
        var op = fields is null ? new JsonObject() : System.Text.Json.JsonSerializer.SerializeToNode(fields)!.AsObject();
        op["op"] = name;
        op["opId"] ??= Guid.NewGuid().ToString("N");
        return op;
    }

    static LoadState NewLoad() => LoadOps.NewState("L1", "J1", "8");

    static IEnumerable<Entry> Rows(LoadState s) => s.Stacks.SelectMany(st => st.Items);

    void Scan(LoadState s, string entryId, string mark, string sequence = "1", string device = "dev-a")
    {
        LoadOps.Apply(s, Op("beginScan", new { entryId, mark }), Ctx(device));
        LoadOps.Apply(s, Op("finishScan", new { entryId, mark, piece = new { mark, sequence, weight = 100 } }), Ctx(device));
    }

    static JsonObject SyncOp(long knownRev, params (string Mark, string Seq, int N)[] pieces) => Op("sync", new
    {
        knownRev,
        pieces = pieces.Select(p => new { mark = p.Mark, sequence = p.Seq, n = p.N, weight = 100 }).ToArray(),
    });

    [Fact]
    public void A_scan_is_tagged_with_who_made_it_and_gets_powerfab_details()
    {
        var s = NewLoad();
        var (changed, _) = LoadOps.Apply(s, Op("beginScan", new { entryId = "e1", mark = "1001-C1" }), Ctx("ipad-yard"));
        Assert.True(changed);
        Assert.True(Rows(s).Single().Pending);

        var (_, activity) = LoadOps.Apply(s, Op("finishScan", new { entryId = "e1", mark = "1001-C1", piece = new { mark = "1001-C1", sequence = "2", weight = 1055 } }), Ctx("ipad-yard"));
        var row = Rows(s).Single();
        Assert.False(row.Pending);
        Assert.Equal("ipad-yard", row.ScannedBy);
        Assert.Equal("2", row.Sequence);
        Assert.Equal(1055, row.Details!["weight"].GetInt32());
        Assert.Equal("scan", activity.Single().Kind);
    }

    [Fact]
    public void A_resent_operation_is_applied_once()
    {
        var s = NewLoad();
        var add = Op("addStack", new { stackId = "s2" });
        Assert.True(LoadOps.Apply(s, add, Ctx()).Changed);
        var again = Op("addStack", new { stackId = "s3" });
        again["opId"] = add["opId"]!.GetValue<string>();
        Assert.False(LoadOps.Apply(s, again, Ctx()).Changed);
        Assert.Equal(2, s.Stacks.Count);
    }

    [Fact]
    public void A_refused_scan_leaves_no_row_and_is_logged()
    {
        var s = NewLoad();
        LoadOps.Apply(s, Op("beginScan", new { entryId = "e1", mark = "1001-C1" }), Ctx());
        var (_, activity) = LoadOps.Apply(s, Op("failScan", new { entryId = "e1", mark = "1001-C1", title = "Too many", message = "All 3 are loaded" }), Ctx());
        Assert.Empty(Rows(s));
        Assert.Equal("refused", activity.Single().Kind);
        Assert.Contains("Too many", activity.Single().Text);
    }

    [Fact]
    public void Sync_leaves_a_scan_still_waiting_on_powerfab()
    {
        var s = NewLoad();
        var before = rev;
        LoadOps.Apply(s, Op("beginScan", new { entryId = "e1", mark = "1001-C1" }), Ctx("dev-b"));
        // Another iPad's read, made before the scan reached PowerFab, has nothing loaded.
        LoadOps.Apply(s, SyncOp(before), Ctx("dev-a"));
        Assert.Single(Rows(s));
    }

    [Fact]
    public void Sync_leaves_a_row_changed_after_the_read_started()
    {
        var s = NewLoad();
        var before = rev;
        Scan(s, "e1", "1001-C1");
        LoadOps.Apply(s, SyncOp(before), Ctx());
        Assert.Single(Rows(s));
    }

    [Fact]
    public void Sync_takes_off_a_row_powerfab_no_longer_has()
    {
        var s = NewLoad();
        Scan(s, "e1", "1001-C1");
        Scan(s, "e2", "1002-B1");
        var (_, activity) = LoadOps.Apply(s, SyncOp(rev, ("1001-C1", "1", 1)), Ctx());
        Assert.Equal(["1001-C1"], Rows(s).Select(r => r.Mark));
        Assert.Equal("sync-remove", activity.Single().Kind);
    }

    [Fact]
    public void Sync_doesnt_count_a_waiting_scan_twice()
    {
        var s = NewLoad();
        Scan(s, "e1", "1001-C1");
        LoadOps.Apply(s, Op("beginScan", new { entryId = "e2", mark = "1001-C1" }), Ctx());
        // PowerFab already has the second one loaded, but its scan hasn't finished on the iPad yet.
        LoadOps.Apply(s, SyncOp(rev, ("1001-C1", "1", 2)), Ctx());
        Assert.Equal(2, Rows(s).Count());
    }

    [Fact]
    public void Sync_puts_pieces_loaded_in_powerfab_into_a_new_stack_at_the_bottom()
    {
        var s = NewLoad();
        Scan(s, "e1", "1001-C1");
        var (_, activity) = LoadOps.Apply(s, SyncOp(rev, ("1001-C1", "1", 1), ("2001-C1", "3", 2)), Ctx());
        Assert.Equal(2, s.Stacks.Count);
        Assert.Equal(["2001-C1", "2001-C1"], s.Stacks[1].Items.Select(e => e.Mark));
        Assert.Null(s.Stacks[1].Items[0].ScannedBy);
        Assert.Equal("auto-stack", activity.Single().Kind);
    }

    [Fact]
    public void A_stale_read_doesnt_put_back_a_piece_just_taken_off()
    {
        var s = NewLoad();
        Scan(s, "e1", "1001-C1");
        var readStarted = rev;
        LoadOps.Apply(s, Op("removeEntry", new { entryId = "e1" }), Ctx("dev-b"));
        // A read that started before the removal still lists it.
        LoadOps.Apply(s, SyncOp(readStarted, ("1001-C1", "1", 1)), Ctx("dev-a"));
        Assert.Empty(Rows(s));
        // A read after it that still has it (PowerFab really has it loaded) puts it back.
        LoadOps.Apply(s, SyncOp(rev, ("1001-C1", "1", 1)), Ctx("dev-a"));
        Assert.Single(Rows(s));
    }

    [Fact]
    public void A_row_being_taken_off_is_left_alone_by_sync()
    {
        var s = NewLoad();
        Scan(s, "e1", "1001-C1");
        LoadOps.Apply(s, Op("markRemoving", new { entryId = "e1" }), Ctx());
        LoadOps.Apply(s, SyncOp(rev), Ctx());
        Assert.True(Rows(s).Single().Removing);
    }

    [Fact]
    public void A_scan_that_never_finished_is_dropped_after_the_protection_runs_out()
    {
        var s = NewLoad();
        LoadOps.Apply(s, Op("beginScan", new { entryId = "e1", mark = "1001-C1" }), Ctx());
        LoadOps.Apply(s, SyncOp(rev), Ctx(at: T0 + LoadOps.PendingProtection + TimeSpan.FromSeconds(1)));
        Assert.Empty(Rows(s));
    }

    [Fact]
    public void Moving_a_row_puts_it_at_the_given_place()
    {
        var s = NewLoad();
        Scan(s, "e1", "A");
        Scan(s, "e2", "B");
        Scan(s, "e3", "C");
        LoadOps.Apply(s, Op("addStack", new { stackId = "s2" }), Ctx());
        LoadOps.Apply(s, Op("moveEntryTo", new { entryId = "e3", stackId = "s2", index = 0 }), Ctx());
        LoadOps.Apply(s, Op("moveEntryTo", new { entryId = "e1", stackId = "s2", index = 1 }), Ctx());
        Assert.Equal(["C", "A"], s.Stacks[1].Items.Select(e => e.Mark));
        Assert.Equal(["B"], s.Stacks[0].Items.Select(e => e.Mark));
    }

    [Fact]
    public void Deleting_the_last_stack_leaves_an_empty_one()
    {
        var s = NewLoad();
        LoadOps.Apply(s, Op("removeStack", new { stackId = LoadOps.DefaultStackId("L1") }), Ctx());
        Assert.Single(s.Stacks);
        Assert.Empty(s.Stacks[0].Items);
    }

    [Fact]
    public void The_shared_truck_check_records_who_checked_each_piece()
    {
        var s = NewLoad();
        LoadOps.Apply(s, Op("addCheck", new { scanId = "c1", mark = "1001-C1" }), Ctx("dev-b"));
        Assert.Equal("dev-b", s.TruckCheck!.Scans.Single().ScannedBy);
        LoadOps.Apply(s, Op("removeCheck", new { scanId = "c1" }), Ctx("dev-a"));
        Assert.Empty(s.TruckCheck.Scans);
    }

    [Fact]
    public void An_unknown_operation_is_refused()
    {
        Assert.Throws<OpException>(() => LoadOps.Apply(NewLoad(), Op("explode"), Ctx()));
    }

    // ---- Pallets ---------------------------------------------------------------------

    static string[] Ids(string prefix, int n) => [.. Enumerable.Range(1, n).Select(i => $"{prefix}-{i}")];

    LoadState WithPallet(int qty = 40, string mark = "CL-1")
    {
        var s = NewLoad();
        LoadOps.Apply(s, Op("addStack", new { stackId = "p1", name = $"Pallet {mark}", pallet = new { mark, photos = 3 } }), Ctx());
        LoadOps.Apply(s, Op("beginScan", new { entryIds = Ids("p1", qty), mark, stackId = "p1" }), Ctx());
        return s;
    }

    [Fact]
    public void A_pallet_is_one_mark_scanned_once_with_a_row_per_piece()
    {
        var s = WithPallet();
        var pallet = s.Stacks.Single(st => st.Id == "p1");
        Assert.Equal("CL-1", pallet.Pallet!.Mark);
        Assert.Equal(3, pallet.Pallet.Photos);
        Assert.Equal(40, pallet.Items.Count);
        Assert.All(pallet.Items, e => Assert.True(e.Pending));

        // PowerFab had 30 left in sequence 1 and 10 in sequence 2: two finishes, two history lines.
        var (_, a1) = LoadOps.Apply(s, Op("finishScan", new { entryIds = Ids("p1", 30), piece = new { mark = "CL-1", sequence = "1", weight = 12.5 } }), Ctx());
        var (_, a2) = LoadOps.Apply(s, Op("finishScan", new { entryIds = Ids("p1", 40).Skip(30).ToArray(), piece = new { mark = "CL-1", sequence = "2", weight = 12.5 } }), Ctx());
        Assert.All(pallet.Items, e => Assert.False(e.Pending));
        Assert.Equal(30, pallet.Items.Count(e => e.Sequence == "1"));
        Assert.Contains("Loaded 30 × CL-1 (seq 1) into Pallet CL-1", a1.Single().Text);
        Assert.Contains("10 × CL-1", a2.Single().Text);
        Assert.Equal(500, pallet.Items.Sum(e => e.Details!["weight"].GetDouble()));
    }

    [Fact]
    public void A_pallet_takes_only_its_own_mark()
    {
        var s = WithPallet();
        Assert.Throws<OpException>(() => LoadOps.Apply(s, Op("beginScan", new { entryId = "x", mark = "1001-C1", stackId = "p1" }), Ctx()));
        Scan(s, "b1", "1001-C1");
        Assert.Throws<OpException>(() => LoadOps.Apply(s, Op("moveEntryTo", new { entryId = "b1", stackId = "p1", index = 0 }), Ctx()));
        // One more of its own mark is fine.
        Assert.True(LoadOps.Apply(s, Op("beginScan", new { entryId = "p1-41", mark = "CL-1", stackId = "p1" }), Ctx()).Changed);
        Assert.Equal(41, s.Stacks.Single(st => st.Id == "p1").Items.Count);
    }

    [Fact]
    public void Part_of_a_pallet_comes_off_in_one_operation()
    {
        var s = WithPallet(10);
        LoadOps.Apply(s, Op("finishScan", new { entryIds = Ids("p1", 10), piece = new { mark = "CL-1", sequence = "1", weight = 5 } }), Ctx());
        var take = Ids("p1", 10).Skip(6).ToArray();
        Assert.True(LoadOps.Apply(s, Op("markRemoving", new { entryIds = take }), Ctx()).Changed);
        var (changed, activity) = LoadOps.Apply(s, Op("removeEntry", new { entryIds = take }), Ctx());
        Assert.True(changed);
        Assert.Equal(6, Rows(s).Count());
        Assert.Contains("Took 4 × CL-1 off the load", activity.Single().Text);

        // PowerFab refusing the rest: they all go with one history line.
        var (_, refused) = LoadOps.Apply(s, Op("failScan", new { entryIds = Ids("p1", 6), mark = "CL-1", title = "No", message = "Nope" }), Ctx());
        Assert.Empty(Rows(s));
        Assert.Contains("(6 pieces)", refused.Single().Text);
    }

    [Fact]
    public void Pieces_loaded_in_powerfab_never_go_into_an_empty_pallet()
    {
        var s = NewLoad();
        LoadOps.Apply(s, Op("addStack", new { stackId = "p1", pallet = new { mark = "CL-1", photos = 3 } }), Ctx());
        LoadOps.Apply(s, SyncOp(rev, ("1001-C1", "1", 1)), Ctx());
        Assert.Empty(s.Stacks.Single(st => st.Id == "p1").Items);
        Assert.Equal("1001-C1", s.Stacks[^1].Items.Single().Mark);
    }

    [Fact]
    public void One_truck_check_scan_can_check_a_whole_pallet()
    {
        var s = WithPallet(12);
        var (_, activity) = LoadOps.Apply(s, Op("addCheck", new { scanId = "c1", mark = "CL-1", qty = 12 }), Ctx());
        Assert.Equal(12, s.TruckCheck!.Scans.Single().Qty);
        Assert.Contains("pallet of 12 × CL-1", activity.Single().Text);
        LoadOps.Apply(s, Op("addCheck", new { scanId = "c2", mark = "1001-C1" }), Ctx());
        Assert.Equal(1, s.TruckCheck.Scans[1].Qty);
    }
}
