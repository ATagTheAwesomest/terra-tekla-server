using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TerraTeklaServer.Tests;

/// <summary>Piece labels (docs/LABELS.md): the barcode, what each label says, where it goes on the sheet, and the routes.</summary>
public sealed class LabelTests : IDisposable
{
    const string Key = "test-key-0123456789";
    readonly string dataDir = Path.Combine(Path.GetTempPath(), $"terra-labels-{Guid.NewGuid():N}");
    readonly WebApplicationFactory<Program> factory;

    public LabelTests()
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

    async Task<HttpClient> Admin()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        await client.GetAsync("/health");
        var code = File.ReadAllText(AdminLock.CodeFilePath(dataDir)).Split('\n')[0].Trim();
        var unlocked = await client.PostAsJsonAsync("/api/admin/unlock", new { pin = code });
        client.DefaultRequestHeaders.Add("X-Terra-Admin", (string)(await unlocked.Content.ReadFromJsonAsync<JsonObject>())!["token"]!);
        return client;
    }

    static PieceCode Code(string code, string mark, string? seq, string? desc = null, double? weight = null) =>
        new(0, code, mark, seq, "import", "2026-11-24T00:00:00Z", null, null, desc, weight);

    [Fact]
    public void Code128_has_start_checksum_and_stop()
    {
        // "PJJ123C": START B 104 + 48·1 + 42·2 + 42·3 + 17·4 + 18·5 + 19·6 + 35·7 = 879, mod 103 = 55.
        var m = Labels.Modules("PJJ123C");
        Assert.Equal(11 * 9 + 13, m.Sum());
        Assert.Equal(6 * 9 + 7, m.Count);
        Assert.Equal([2, 1, 1, 2, 1, 4], m.Take(6)); // START B
        Assert.Equal([3, 1, 1, 3, 2, 1], m.Skip(6 * 8).Take(6)); // checksum 55
        Assert.Equal([2, 3, 3, 1, 1, 1, 2], m.TakeLast(7)); // STOP
        for (var i = 0; i < 9; i++)
            Assert.Equal(11, m.Skip(i * 6).Take(6).Sum());
    }

    [Fact]
    public void Labels_count_pieces_per_mark_and_sequence_and_fall_back_to_the_bom()
    {
        var codes = new[]
        {
            Code("C3", "1001-B1", "2", "W12X26", 410),
            Code("C1", "1001-b1", "2"),
            Code("C2", "1001-B1", "2"),
            Code("C9", "1001-B1", "3"),
            Code("C5", "1000-C1", "2", "HSS 6X6", 900),
        };
        var bom = new List<BomRow> { new("1001-B1", "TL1", "2", null, null, null, null, null, "BEAM W12X26", 3, null, 405.5, false) };

        var all = Labels.For("26102", codes, bom);
        Assert.Equal(["C5", "C1", "C2", "C3", "C9"], all.Select(l => l.Code));
        var c2 = all.Single(l => l.Code == "C2");
        Assert.Equal(("1001-B1", "2", 2, 3, "BEAM W12X26", 405.5), (c2.Mark, c2.Sequence, c2.Index, c2.Of, c2.Description, c2.Weight));
        var c3 = all.Single(l => l.Code == "C3");
        Assert.Equal(("W12X26", 410.0), (c3.Description, c3.Weight)); // its own description and weight win
        Assert.Equal((1, 1), (all.Single(l => l.Code == "C9").Index, all.Single(l => l.Code == "C9").Of));

        Assert.Equal(["C1", "C2", "C3"], Labels.For("26102", codes, bom, mark: "1001-b1-", sequence: "2").Select(l => l.Code));
        // A reprint of one piece still says which of how many it is.
        var reprint = Assert.Single(Labels.For("26102", codes, bom, only: ["c3"]));
        Assert.Equal((3, 3), (reprint.Index, reprint.Of));
    }

    [Fact]
    public void Labels_fill_the_free_positions_then_new_sheets()
    {
        Assert.Equal([(0, 1), (0, 2), (0, 3)], Labels.Place(3, null));
        Assert.Equal([(0, 4), (0, 30), (1, 1), (1, 2)], Labels.Place(4, [30, 4]));
        var many = Labels.Place(65, Enumerable.Range(21, 10).ToList());
        Assert.Equal((0, 21), many[0]);
        Assert.Equal((1, 1), many[10]);
        Assert.Equal((2, 25), many[^1]);

        var html = Labels.Sheet("26102", [new PieceLabel("C1<", "1001-B1", "2", 1, 3, "W12X26 & co", 405.5, "26102")], [7], "Labels");
        Assert.Contains("data-pos=\"7\"", html);
        Assert.Contains("C1&lt;", html);
        Assert.Contains("W12X26 &amp; co", html);
        Assert.Contains("QTY 1 of 3", html);
        Assert.Contains("406 lbs", html);
        Assert.Contains("JOB 26102", html);
        Assert.Equal(1, html.Split("class=\"sheet\"").Length - 1);
    }

    [Fact]
    public async Task Prints_a_pick_and_remembers_the_part_used_sheet()
    {
        var admin = await Admin();
        var text = "Barcode,Assembly,Sequence,Description,Weight (lbs)\nC0000001Q1,1001-B1,2,W12X26,405.5\nC0000002Q1,1001-B1,2,W12X26,405.5\nC0000003Q1,1002-C1,2,HSS 6X6,900\n";
        (await admin.PostAsJsonAsync("/api/admin/jobs/26102/codes/import", new { text })).EnsureSuccessStatusCode();

        var marks = await admin.GetFromJsonAsync<JsonObject>("/api/admin/jobs/26102/labels/marks");
        Assert.Equal(3, (int)marks!["total"]!);
        Assert.Equal(0, (int)marks["missingWeight"]!);
        Assert.Equal(2, marks["marks"]!.AsArray().Count);

        var pieces = await admin.GetFromJsonAsync<JsonArray>("/api/admin/jobs/26102/labels?mark=1001-B1&seq=2");
        Assert.Equal(["C0000001Q1", "C0000002Q1"], pieces!.Select(p => (string)p!["code"]!));
        Assert.Equal("W12X26", (string)pieces![0]!["description"]!);

        var sheet = await admin.PostAsJsonAsync("/api/admin/jobs/26102/labels/sheet", new { mark = "1001-B1", seq = "2", positions = new[] { 5, 6, 7 } });
        sheet.EnsureSuccessStatusCode();
        Assert.StartsWith("text/html", sheet.Content.Headers.ContentType!.ToString());
        var html = await sheet.Content.ReadAsStringAsync();
        Assert.Contains("data-pos=\"5\"", html);
        Assert.Contains("data-pos=\"6\"", html);
        Assert.DoesNotContain("data-pos=\"7\"", html);
        Assert.Contains("QTY 2 of 2", html);

        var chosen = await admin.PostAsJsonAsync("/api/admin/jobs/26102/labels/sheet", new { codes = new[] { "c0000003q1", "C0000003Q1" } });
        Assert.Equal(1, (await chosen.Content.ReadAsStringAsync()).Split("class=\"lab\"").Length - 1);

        var typo = await admin.PostAsJsonAsync("/api/admin/jobs/26102/labels/sheet", new { codes = new[] { "C0000003Q1", "NOPE" } });
        Assert.Equal(HttpStatusCode.BadRequest, typo.StatusCode);
        Assert.Contains("NOPE", await typo.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/jobs/26102/labels/sheet", new { positions = new[] { 31 } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/admin/jobs/26102/labels/sheet", new { mark = "9999-X1" })).StatusCode);

        // The sheet in the printer: remembered, and a full one starts over.
        Assert.Empty((await admin.GetFromJsonAsync<JsonObject>("/api/admin/labels/sheet-state"))!["used"]!.AsArray());
        (await admin.PutAsJsonAsync("/api/admin/labels/sheet-state", new { used = new[] { 3, 1, 2, 2, 40 } })).EnsureSuccessStatusCode();
        Assert.Equal([1, 2, 3], (await admin.GetFromJsonAsync<JsonObject>("/api/admin/labels/sheet-state"))!["used"]!.AsArray().Select(n => (int)n!));
        (await admin.PutAsJsonAsync("/api/admin/labels/sheet-state", new { used = Enumerable.Range(1, 30) })).EnsureSuccessStatusCode();
        Assert.Empty((await admin.GetFromJsonAsync<JsonObject>("/api/admin/labels/sheet-state"))!["used"]!.AsArray());

        // Admin code required.
        var plain = factory.CreateClient();
        plain.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/admin/labels/sheet-state")).StatusCode);
    }
}
