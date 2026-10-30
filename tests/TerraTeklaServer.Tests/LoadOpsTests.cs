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
}
