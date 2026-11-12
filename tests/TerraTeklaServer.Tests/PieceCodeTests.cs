using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TerraTeklaServer.Tests;

/// <summary>Piece codes (docs/PIECE-CODES.md), batched operations, and writing only what changed.</summary>
public sealed class PieceCodeTests : IDisposable
{
    const string Key = "test-key-0123456789";
    readonly string dataDir = Path.Combine(Path.GetTempPath(), $"terra-codes-{Guid.NewGuid():N}");
    readonly WebApplicationFactory<Program> factory;
    static readonly DateTime T0 = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    long rev = 100;

    public PieceCodeTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ACCESS_KEY", Key);
            b.UseSetting("DATA_DIR", dataDir);
            b.UseSetting("CLIENT_ROOT", "");
            b.UseSetting("CONVERSION_URL", "");
            b.UseSetting("SETTINGS_FILE", Path.Combine(dataDir, "local.settings.json"));
        });
    }

    public void Dispose()
    {
        factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    HttpClient Client(string? device = "ipad-0000-a", string? admin = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        if (device is not null)
            client.DefaultRequestHeaders.Add("X-Terra-Device", device);
        if (admin is not null)
            client.DefaultRequestHeaders.Add("X-Terra-Admin", admin);
        return client;
    }

    async Task<string> AdminToken()
    {
        await Client().GetAsync("/health");
        var code = File.ReadAllText(AdminLock.CodeFilePath(dataDir)).Split('\n')[0].Trim();
        var unlocked = await Client(null).PostAsJsonAsync("/api/admin/unlock", new { pin = code });
        return (string)(await unlocked.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
    }

    OpContext Ctx(Func<string, string?>? elsewhere = null) => new("dev-a", "dev-a", ++rev, T0, elsewhere);

    static JsonObject Op(string name, object fields)
    {
        var op = System.Text.Json.JsonSerializer.SerializeToNode(fields)!.AsObject();
        op["op"] = name;
        op["opId"] ??= Guid.NewGuid().ToString("N");
        return op;
    }

    // ---- Operations ----------------------------------------------------------------------

    [Fact]
    public void A_code_is_one_piece_so_it_can_be_loaded_once()
    {
        var s = LoadOps.NewState("L1", "J1", "8");
        LoadOps.Apply(s, Op("beginScan", new { entryId = "e1", mark = "1001-C1", code = "ABC123456789X" }), Ctx());
        Assert.Equal("ABC123456789X", s.Stacks[0].Items[0].Code);

        var again = Assert.Throws<OpException>(() => LoadOps.Apply(s, Op("beginScan", new { entryId = "e2", mark = "1001-C1", code = "abc123456789x" }), Ctx()));
        Assert.Contains("already on this load", again.Message);
        var elsewhere = Assert.Throws<OpException>(() => LoadOps.Apply(s, Op("beginScan", new { entryId = "e3", mark = "1001-C1", code = "OTHER1234567" }), Ctx(_ => "9")));
        Assert.Contains("already on load 9", elsewhere.Message);
        // The same scan sent again (same row) is fine: nothing happens.
        var (changed, _) = LoadOps.Apply(s, Op("beginScan", new { entryId = "e1", mark = "1001-C1", code = "ABC123456789X" }), Ctx(_ => "9"));
        Assert.False(changed);
        Assert.Single(s.Stacks[0].Items);
    }

    [Fact]
    public void A_truck_check_scan_of_a_code_counts_once()
    {
        var s = LoadOps.NewState("L1", "J1", "8");
        Assert.True(LoadOps.Apply(s, Op("addCheck", new { scanId = "c1", mark = "1001-C1", code = "ABC123456789X", qty = 5 }), Ctx()).Changed);
        Assert.Equal(1, s.TruckCheck!.Scans[0].Qty);
        Assert.False(LoadOps.Apply(s, Op("addCheck", new { scanId = "c2", mark = "1001-C1", code = "ABC123456789X" }), Ctx()).Changed);
        Assert.Single(s.TruckCheck.Scans);
    }

    [Fact]
    public void A_powerfab_read_with_fewer_pieces_takes_the_rows_without_a_code_first()
    {
        var s = LoadOps.NewState("L1", "J1", "8");
        foreach (var (id, code) in new[] { ("e1", "CODE00000001"), ("e2", (string?)null), ("e3", "CODE00000003") })
        {
            LoadOps.Apply(s, Op("beginScan", new { entryId = id, mark = "1001-C1", code }), Ctx());
            LoadOps.Apply(s, Op("finishScan", new { entryId = id, mark = "1001-C1", piece = new { mark = "1001-C1", sequence = "1" } }), Ctx());
        }
        LoadOps.Apply(s, Op("sync", new { knownRev = rev, pieces = new[] { new { mark = "1001-C1", sequence = "1", n = 2 } } }), Ctx());
        Assert.Equal(["e1", "e3"], s.Stacks.SelectMany(st => st.Items).Select(e => e.Id));
    }

    [Fact]
    public void A_pasted_list_reads_commas_tabs_and_a_header()
    {
        var (lines, errors) = CodeList.Parse("Code,Mark,Sequence\nABC123456789,1001-c1-,1001\n\nDEF123456789\t1001-C2\nbad line\n");
        Assert.Equal([new CodeLine("ABC123456789", "1001-C1", "1001"), new CodeLine("DEF123456789", "1001-C2", null)], lines);
        Assert.Single(errors);
        Assert.Contains("Line 5", errors[0]);
        Assert.Throws<OpException>(() => PieceCodeSettings.CheckPattern("(unclosed"));
        Assert.Equal(PieceCodeSettings.DefaultPattern, PieceCodeSettings.CheckPattern(" "));
    }

    // ---- Through the API --------------------------------------------------------------------

    [Fact]
    public async Task Codes_are_set_up_on_the_admin_board_and_paired_on_the_ipads()
    {
        var ipad = Client();
        // Off until switched on: pairing is refused, and settings need the admin code.
        Assert.Equal(HttpStatusCode.Forbidden, (await ipad.PostAsJsonAsync("/api/jobs/J1/codes", new { code = "ABC123456789", mark = "1001-C1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ipad.PutAsJsonAsync("/api/admin/jobs/J1/codes/settings", new { enabled = true })).StatusCode);

        var admin = Client(null, await AdminToken());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/jobs/J1/codes/settings", new { enabled = true, pattern = "([" })).StatusCode);
        (await admin.PutAsJsonAsync("/api/admin/jobs/J1/codes/settings", new { enabled = true, pattern = "", learn = true })).EnsureSuccessStatusCode();
        var import = await (await admin.PostAsJsonAsync("/api/admin/jobs/J1/codes/import", new { text = "code,mark\nABC123456789,1001-C1\nDEF123456789,1001-C1" })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(2, (int)import!["added"]!);

        var read = await ipad.GetFromJsonAsync<JsonObject>("/api/jobs/j1/codes");
        Assert.True((bool)read!["settings"]!["enabled"]!);
        Assert.Equal(PieceCodeSettings.DefaultPattern, (string)read["settings"]!["pattern"]!);
        Assert.Equal(2, read["codes"]!.AsArray().Count);
        var last = (long)read["last"]!;

        // An iPad pairs a new one; a code already paired with another mark is refused.
        (await ipad.PostAsJsonAsync("/api/jobs/J1/codes", new { code = "GHI123456789", mark = "1001-c2" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await ipad.PostAsJsonAsync("/api/jobs/J1/codes", new { code = "abc123456789", mark = "1001-C2" })).StatusCode);
        var since = await ipad.GetFromJsonAsync<JsonObject>($"/api/jobs/J1/codes?since={last}");
        var fresh = Assert.Single(since!["codes"]!.AsArray());
        Assert.Equal("1001-C2", (string)fresh!["mark"]!);
        Assert.Equal("paired", (string)fresh["source"]!);

        // Loaded on load 8, the same code can't go on load 9; looking it up says where it is.
        (await ipad.PostAsJsonAsync("/api/loads/L8/ops", new { op = "beginScan", opId = "o1", job = "J1", loadNumber = "8", entryId = "e1", mark = "1001-C1", code = "ABC123456789" })).EnsureSuccessStatusCode();
        var refused = await ipad.PostAsJsonAsync("/api/loads/L9/ops", new { op = "beginScan", opId = "o2", job = "J1", loadNumber = "9", entryId = "e2", mark = "1001-C1", code = "abc123456789" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("already on load 8", await refused.Content.ReadAsStringAsync());
        var where = await ipad.GetFromJsonAsync<JsonObject>("/api/jobs/J1/codes/ABC123456789");
        Assert.Equal("8", (string)where!["load"]!["loadNumber"]!);

        Assert.Equal(3, (int)(await (await admin.DeleteAsync("/api/admin/jobs/J1/codes")).Content.ReadFromJsonAsync<JsonObject>())!["removed"]!);
    }

    [Fact]
    public async Task A_batch_applies_in_order_and_skips_what_the_load_refuses()
    {
        var ipad = Client();
        var reply = await ipad.PostAsJsonAsync("/api/loads/L1/ops/batch", new
        {
            job = "J1",
            loadNumber = "8",
            ops = new object[]
            {
                new { op = "beginScan", opId = "b1", entryId = "e1", mark = "1001-C1", code = "CODE00000001" },
                new { op = "beginScan", opId = "b2", entryId = "e2", mark = "1001-C1", code = "CODE00000001" },
                new { op = "finishScan", opId = "b3", entryId = "e1", mark = "1001-C1", piece = new { mark = "1001-C1", sequence = "1", weight = 50 } },
                new { op = "addCheck", opId = "b4", scanId = "c1", mark = "1001-C1", code = "CODE00000001" },
            },
        });
        reply.EnsureSuccessStatusCode();
        var body = await reply.Content.ReadFromJsonAsync<JsonObject>();
        var results = body!["results"]!.AsArray();
        Assert.Null(results[0]!["error"]);
        Assert.Contains("already on this load", (string)results[1]!["error"]!);
        Assert.True((bool)results[2]!["changed"]!);
        Assert.Single(body["state"]!["stacks"]![0]!["items"]!.AsArray());

        // Written: a fresh server reads the same thing back.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var store = new Store(new Settings("", "", "", "", dataDir, [], new PhotoSettings(false, "", "", 1, 1, [])));
        var load = await store.GetAsync("L1", "J1", "8");
        var row = Assert.Single(load.Stacks[0].Items);
        Assert.Equal("CODE00000001", row.Code);
        Assert.Equal("CODE00000001", load.TruckCheck!.Scans.Single().Code);
    }

    [Fact]
    public async Task Only_what_changed_is_written_and_reads_back_the_same()
    {
        var store = new Store(new Settings("", "", "", "", dataDir, [], new PhotoSettings(false, "", "", 1, 1, [])));
        var dev = new Device("dev-a", "User 1", "#ffb000", "", "", null, null, null, 1);
        async Task Apply(string name, object fields) => await store.ApplyAsync("L1", "J1", "8", Op(name, fields), dev);
        for (var i = 0; i < 30; i++)
        {
            await Apply("beginScan", new { entryId = $"e{i}", mark = $"M-{i % 4}" });
            await Apply("finishScan", new { entryId = $"e{i}", mark = $"M-{i % 4}", piece = new { mark = $"M-{i % 4}", sequence = "1", weight = i } });
        }
        await Apply("addStack", new { stackId = "s2", name = "Second" });
        await Apply("moveEntryTo", new { entryId = "e3", stackId = "s2", index = 0 });
        await Apply("moveEntryTo", new { entryId = "e10", stackId = "first-L1", index = 0 });
        await Apply("removeEntry", new { entryIds = new[] { "e5", "e6" } });
        await Apply("markRemoving", new { entryId = "e7" });
        await Apply("addCheck", new { scanId = "c1", mark = "M-1" });
        await Apply("addCheck", new { scanId = "c2", mark = "M-2" });
        await Apply("removeCheck", new { scanId = "c1" });
        await Apply("rename", new { stackId = "s2", name = "Renamed" });
        var mine = await store.GetAsync("L1", "J1", "8");

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var fresh = await new Store(new Settings("", "", "", "", dataDir, [], new PhotoSettings(false, "", "", 1, 1, []))).GetAsync("L1", "J1", "8");
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(mine, Json.Options), System.Text.Json.JsonSerializer.Serialize(fresh, Json.Options));
        Assert.Equal(["e10", "e0", "e1", "e2", "e4", "e7"], fresh.Stacks[0].Items.Take(6).Select(e => e.Id));
        Assert.True(fresh.Stacks[0].Items.Single(e => e.Id == "e7").Removing);
    }

    [Fact]
    public async Task Load_replies_are_compressed_when_the_app_accepts_it()
    {
        var ipad = Client();
        ipad.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        var reply = await ipad.GetAsync("/api/loads/L1?job=J1&number=8");
        reply.EnsureSuccessStatusCode();
        Assert.Contains("gzip", reply.Content.Headers.ContentEncoding);
    }
}
