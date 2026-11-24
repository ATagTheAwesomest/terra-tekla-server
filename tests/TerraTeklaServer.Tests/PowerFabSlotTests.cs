using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace TerraTeklaServer.Tests;

/// <summary>Writing piece codes into PowerFab's instance location (PowerFabSlot.cs), against a fake bridge.</summary>
public sealed class PowerFabSlotTests : IDisposable
{
    const string Key = "test-key-0123456789";
    readonly string dataDir = Path.Combine(Path.GetTempPath(), $"terra-slot-{Guid.NewGuid():N}");
    readonly WebApplicationFactory<Program> factory;
    readonly FakeBridge bridge = new();

    // GetAssemblies as PowerFab answers it: 10121601 once in 1012, 40134515 x3 in 4013,
    // 55311000 in two sequences.
    const string Assemblies = """
        {"ProductionControlID":"90","JobNumber":"26418","Assembly":[
          {"MainMark":"10121601","AssemblyQuantity":"1","TotalQuantity":"1","AssemblySequence":{"Sequence":"1012","Quantity":"1"}},
          {"MainMark":"40134515","AssemblyQuantity":"3","TotalQuantity":"3","AssemblySequence":{"Sequence":"4013","Quantity":"3"}},
          {"MainMark":"55311000","AssemblyQuantity":"1","TotalQuantity":"3","AssemblySequence":[{"Sequence":"5531","Quantity":"1"},{"Sequence":"5532","Quantity":"2"}]}
        ]}
        """;

    public PowerFabSlotTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ACCESS_KEY", Key);
            b.UseSetting("DATA_DIR", dataDir);
            b.UseSetting("CLIENT_ROOT", "");
            b.UseSetting("CONVERSION_URL", "http://bridge.test");
            b.UseSetting("SETTINGS_FILE", Path.Combine(dataDir, "local.settings.json"));
            b.ConfigureServices(s => s.AddHttpClient("conversion").ConfigurePrimaryHttpMessageHandler(() => bridge));
        });
    }

    public void Dispose()
    {
        factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    sealed class FakeBridge : HttpMessageHandler
    {
        public readonly List<(string Command, JsonObject Body)> Calls = [];
        public string? Refuse;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var command = request.RequestUri!.Segments[^1];
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            lock (Calls) Calls.Add((command, body));
            if (command == "GetAssemblies")
                return new(HttpStatusCode.OK) { Content = new StringContent(Assemblies) };
            if (Refuse is not null && body["InstanceLocation"]?.ToString() == Refuse)
                return new(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("""{"detail":"Instance not found."}""") };
            return new(HttpStatusCode.OK) { Content = new StringContent("""{"Successful":"1"}""") };
        }
    }

    async Task<HttpClient> Admin()
    {
        var plain = factory.CreateClient();
        plain.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        await plain.GetAsync("/health");
        var code = File.ReadAllText(AdminLock.CodeFilePath(dataDir)).Split('\n')[0].Trim();
        var unlocked = await plain.PostAsJsonAsync("/api/admin/unlock", new { pin = code });
        var token = (string)(await unlocked.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        plain.DefaultRequestHeaders.Add("X-Terra-Admin", token);
        return plain;
    }

    static PieceCode Code(string code, string mark, string? seq = null) => new(0, code, mark, seq, "import", "", null);

    [Fact]
    public void Reads_assemblies_with_one_or_several_sequences()
    {
        var a = PowerFabSlot.Assemblies(JsonNode.Parse(Assemblies));
        Assert.Equal(4, a.Count);
        Assert.Contains(new SlotAssembly("40134515", "4013", 3), a);
        Assert.Contains(new SlotAssembly("55311000", "5532", 2), a);
    }

    [Fact]
    public void Plan_gives_each_code_its_own_piece_and_says_why_others_cant_go()
    {
        var a = PowerFabSlot.Assemblies(JsonNode.Parse(Assemblies));
        var plan = PowerFabSlot.Plan([
            Code("C0000003Q1", "40134515", "4013"),
            Code("C0000001Q1", "40134515"),
            Code("C0000002Q1", "40134515"),
            Code("C0000004Q1", "40134515"),       // a 4th code for 3 pieces
            Code("C0000005Q1", "55311000"),       // two sequences, none given
            Code("C0000006Q1", "55311000", "5532"),
            Code("C0000007Q1", "99999999"),       // not in PowerFab
            Code("C0000008Q1", "10121601", "9999"), // wrong sequence
        ], a, []);

        Assert.Equal([("C0000001Q1", 1), ("C0000002Q1", 2), ("C0000003Q1", 3), ("C0000006Q1", 1)],
            plan.Write.Select(w => (w.Code, w.Instance)).ToList());
        Assert.Equal(4, plan.Skipped.Count);
        Assert.Contains("More codes than the 3 pieces", plan.Skipped.Single(s => s.Code == "C0000004Q1").Reason);
        Assert.Contains("sequences 5531, 5532", plan.Skipped.Single(s => s.Code == "C0000005Q1").Reason);
        Assert.Contains("no mark 99999999", plan.Skipped.Single(s => s.Code == "C0000007Q1").Reason);
        Assert.Contains("not 9999", plan.Skipped.Single(s => s.Code == "C0000008Q1").Reason);
    }

    [Fact]
    public void A_written_code_keeps_its_piece_and_new_codes_fill_the_free_ones()
    {
        var a = PowerFabSlot.Assemblies(JsonNode.Parse(Assemblies));
        var plan = PowerFabSlot.Plan([Code("C0000001Q1", "40134515"), Code("C0000009Q1", "40134515")], a,
            [new SlotWritten("C0000009Q1", "40134515", "4013", 1, "", null)]);
        Assert.Single(plan.Done);
        Assert.Equal(new SlotWrite("C0000001Q1", "40134515", "4013", 2), Assert.Single(plan.Write));
    }

    [Fact]
    public async Task Preview_then_write_then_clear_through_the_bridge()
    {
        var admin = await Admin();
        var import = await admin.PostAsJsonAsync("/api/admin/jobs/26418/codes/import", new
        {
            text = "Status,Sequence,Assembly,Barcode\nNot started,1012,10121601,C9227621Q1\nNot started,4013,40134515,C9227623Q1\nNot started,4013,40134515,C9227622Q1\n",
            overwrite = false,
        });
        import.EnsureSuccessStatusCode();

        // Preview reads PowerFab only.
        var preview = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/jobs/26418/codes/powerfab"))!;
        Assert.Equal(3, (int)preview["writeCount"]!);
        Assert.All(bridge.Calls, c => Assert.Equal("GetAssemblies", c.Command));

        // A stale count is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/jobs/26418/codes/powerfab", new { expected = 2 })).StatusCode);

        bridge.Refuse = "C9227621Q1";
        var run = (await (await admin.PostAsJsonAsync("/api/admin/jobs/26418/codes/powerfab", new { expected = 3 })).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(2, (int)run["written"]!);
        Assert.Single(run["failed"]!.AsArray());
        var writes = bridge.Calls.Where(c => c.Command == "SetInstanceLocation").Select(c => c.Body).ToList();
        Assert.Equal(3, writes.Count);
        var w = writes.Single(b => b["InstanceLocation"]!.ToString() == "C9227622Q1");
        Assert.Equal("26418", w["JobNumber"]!.ToString());
        Assert.Equal("40134515", w["MainMark"]!.ToString());
        Assert.Equal("4013", w["Sequence"]!.ToString());
        Assert.Equal("1", w["InstanceNumbers"]!.ToString());

        // Again: only the refused one is left.
        bridge.Refuse = null;
        preview = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/jobs/26418/codes/powerfab"))!;
        Assert.Equal(1, (int)preview["writeCount"]!);
        Assert.Equal(2, (int)preview["doneCount"]!);

        // Clear blanks the pieces written and forgets them.
        bridge.Calls.Clear();
        var cleared = (await (await admin.DeleteAsync("/api/admin/jobs/26418/codes/powerfab")).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(2, (int)cleared["cleared"]!);
        Assert.All(bridge.Calls, c => Assert.Equal("", c.Body["InstanceLocation"]!.ToString()));
        preview = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/jobs/26418/codes/powerfab"))!;
        Assert.Equal(3, (int)preview["writeCount"]!);
    }

    [Fact]
    public async Task Needs_the_admin_code()
    {
        var plain = factory.CreateClient();
        plain.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        Assert.NotEqual(HttpStatusCode.OK, (await plain.GetAsync("/api/admin/jobs/26418/codes/powerfab")).StatusCode);
        Assert.Empty(bridge.Calls);
    }
}
