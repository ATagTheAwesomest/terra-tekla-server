using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace TerraTeklaServer.Tests;

public sealed class VolumeTests(ITestOutputHelper output) : IDisposable
{
    readonly string dataDir = Path.Combine(Path.GetTempPath(), $"terra-vol-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    Store NewStore() => new(new Settings("", "", "", "", dataDir, [], new PhotoSettings(false, "", "", 1, 1, [])));

    static readonly Device Dev = new("dev-a", "User 1", "#ffb000", "", "", null, null, null, 1);

    static JsonObject Op(string name, object fields)
    {
        var op = System.Text.Json.JsonSerializer.SerializeToNode(fields)!.AsObject();
        op["op"] = name;
        op["opId"] ??= Guid.NewGuid().ToString("N");
        return op;
    }

    /// <summary>
    /// A truck of single-piece codes: thousands of rows, scanned fast. Every scan writes only its own
    /// row, so the last scans cost about what the first ones did.
    /// </summary>
    [Fact]
    public async Task Thousands_of_scans_stay_fast()
    {
        var store = NewStore();
        var times = new List<long>();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 2000; i++)
        {
            var mark = $"1001-C{i % 15}";
            var code = $"CODE{i:D10}";
            await store.ApplyManyAsync("L1", "J1", "8", [
                Op("beginScan", new { entryId = $"e{i}", mark, code }),
                Op("finishScan", new { entryId = $"e{i}", mark, piece = new { mark, sequence = "1001", weight = 1234.5, description = "COLUMN W12X40", partNumber = "P1", shape = "W12X40" } }),
            ], Dev);
            if (i % 500 == 499) { times.Add(sw.ElapsedMilliseconds); output.WriteLine($"{i + 1} scans: {sw.ElapsedMilliseconds} ms (last 500)"); sw.Restart(); }
        }
        for (var i = 0; i < 2000; i++)
            await store.ApplyAsync("L1", "J1", "8", Op("addCheck", new { scanId = $"c{i}", mark = $"1001-C{i % 15}", code = $"CODE{i:D10}" }), Dev);
        output.WriteLine($"2000 checks: {sw.ElapsedMilliseconds} ms");
        var load = await store.GetAsync("L1", "J1", "8");
        Assert.Equal(2000, load.Stacks.Sum(st => st.Items.Count));
        Assert.Equal(2000, load.TruckCheck!.Scans.Count);
        // Generous, so a slow test machine passes: before writing only what changed, the last 500
        // scans took over ten times as long as the first.
        Assert.True(times[^1] < times[0] * 4 + 1000, $"Scans slowed down as the load grew: {string.Join(", ", times)} ms per 500");
    }
}
