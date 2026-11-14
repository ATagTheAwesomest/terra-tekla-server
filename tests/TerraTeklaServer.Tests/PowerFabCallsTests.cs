namespace TerraTeklaServer.Tests;

/// <summary>
/// PowerFab reads shared between devices (PowerFabCalls.cs): the bridge runs one command at a time, so
/// two devices reading the same thing must not queue two calls, and nothing read before a write may be
/// used after it.
/// </summary>
public sealed class PowerFabCallsTests
{
    sealed class Bridge
    {
        public int Calls;
        public TaskCompletionSource? Hold;
        public string Answer = "a";

        public async Task<PowerFabReply> SendAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Hold is { } h)
                await h.Task.WaitAsync(ct);
            return new PowerFabReply(200, Answer, "application/json", null);
        }
    }

    const string Loads = "ProductionControlLoad_Get";
    const string Body = """{"ProductionControlID":13}""";

    [Fact]
    public async Task Reads_asked_for_at_once_share_one_call()
    {
        var calls = new PowerFabCalls();
        var bridge = new Bridge { Hold = new() };
        var a = calls.RunAsync(Loads, Body, "iPad", bridge.SendAsync, default);
        var b = calls.RunAsync(Loads, Body, "Handheld", bridge.SendAsync, default);
        Assert.Equal(1, calls.Running);
        bridge.Hold.SetResult();
        Assert.Equal("a", (await a).Body);
        Assert.Equal("a", (await b).Body);
        Assert.Equal(1, bridge.Calls);
    }

    [Fact]
    public async Task A_read_is_reused_for_a_moment_but_never_after_a_write()
    {
        var calls = new PowerFabCalls();
        var bridge = new Bridge();
        await calls.RunAsync(Loads, Body, null, bridge.SendAsync, default);
        await calls.RunAsync(Loads, Body, null, bridge.SendAsync, default);
        Assert.Equal(1, bridge.Calls);

        // Another job's read is its own.
        await calls.RunAsync(Loads, """{"ProductionControlID":14}""", null, bridge.SendAsync, default);
        Assert.Equal(2, bridge.Calls);

        await calls.RunAsync("ProductionControlLoadMaterial_Add", "{}", null, bridge.SendAsync, default);
        Assert.Equal(3, bridge.Calls);
        bridge.Answer = "b";
        Assert.Equal("b", (await calls.RunAsync(Loads, Body, null, bridge.SendAsync, default)).Body);
        Assert.Equal(4, bridge.Calls);
    }

    [Fact]
    public async Task A_read_that_started_before_a_write_is_not_kept_or_shared_after_it()
    {
        var calls = new PowerFabCalls();
        var bridge = new Bridge { Hold = new() };
        var before = calls.RunAsync(Loads, Body, null, bridge.SendAsync, default);
        var write = calls.RunAsync("ProductionControlLoadMaterial_Load", "{}", null, bridge.SendAsync, default);
        // Asked for after the write: its own call, not the one already going.
        var after = calls.RunAsync(Loads, Body, null, bridge.SendAsync, default);
        bridge.Hold.SetResult();
        await Task.WhenAll(before, write, after);
        Assert.Equal(3, bridge.Calls);
        bridge.Hold = null;
        await calls.RunAsync(Loads, Body, null, bridge.SendAsync, default);
        Assert.Equal(3, bridge.Calls); // the read after the write was kept
    }

    [Fact]
    public async Task Failed_reads_are_not_kept()
    {
        var calls = new PowerFabCalls();
        var n = 0;
        Task<PowerFabReply> Fail(CancellationToken _) { n++; return Task.FromResult(new PowerFabReply(502, "", null, "down")); }
        await calls.RunAsync(Loads, Body, null, Fail, default);
        await calls.RunAsync(Loads, Body, null, Fail, default);
        Assert.Equal(2, n);
    }

    [Theory]
    [InlineData("ProductionControlLoad_Get", true)]
    [InlineData("ProductionControlLoadRemaining_Get", true)]
    [InlineData("MaxTruck", true)]
    [InlineData("GetAssemblies", true)]
    [InlineData("ProductionControlLoad_IsDuplicateLoadNumber", true)]
    [InlineData("ProductionControlLoadMaterial_Add", false)]
    [InlineData("ProductionControlLoad_Ship", false)]
    [InlineData("ProductionControlLoad_Insert", false)]
    public void Reads_and_writes(string command, bool read) => Assert.Equal(read, PowerFabCalls.IsRead(command));

    [Fact]
    public void Scan_times_are_kept_per_device()
    {
        var times = new ScanTimes();
        var dev = new Device("d1", "Handheld", "#fff", "", "", null, null, null, 2);
        times.Add(dev, [new ScanTimes.Scan("1", "1001-C1", "scan", "2026-11-13T20:00:00Z", 2, 900, 1400, true, null, [new("PowerFab assign", 300)])]);
        var json = System.Text.Json.JsonSerializer.Serialize(times.Snapshot());
        Assert.Contains("Handheld", json);
        Assert.Contains("1400", json);
    }
}
