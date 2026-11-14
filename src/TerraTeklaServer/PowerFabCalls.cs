using System.Collections.Concurrent;
using System.Diagnostics;

namespace TerraTeklaServer;

/// <summary>One PowerFab command as the bridge answered it.</summary>
public sealed record PowerFabReply(int Status, string Body, string? ContentType, string? Error);

/// <summary>
/// Every PowerFab command goes through here on its way to tekla-conversion-layer, which runs one
/// PowerFab command at a time. So that devices don't queue the same big read behind each other:
/// <list type="bullet">
/// <item>Identical reads asked for at the same time share one call (the second waits for the
/// first's answer instead of queueing its own).</item>
/// <item>A read's answer is reused for <see cref="ReadFresh"/> (the job list, which the admin board
/// reads on every live update, for <see cref="JobListFresh"/>). Any write through this server
/// (assign, load, ship...) throws every kept answer away first, so a read after a write is always
/// PowerFab's own.</item>
/// </list>
/// It also keeps the last calls with how long each took, for the admin board's Server page
/// (docs/SPEED.md).
/// </summary>
public sealed class PowerFabCalls
{
    public static TimeSpan ReadFresh { get; set; } = TimeSpan.FromSeconds(2);
    public static TimeSpan JobListFresh { get; set; } = TimeSpan.FromSeconds(60);

    const int Keep = 300;

    public sealed record Call(string At, string Command, long Ms, int Bytes, int Status, string? Error, string How, int Waiting, string? Device);

    sealed record Kept(PowerFabReply Reply, DateTime At, long Generation);

    readonly object sync = new();
    readonly Dictionary<string, Kept> kept = new();
    readonly Dictionary<string, (Task<PowerFabReply> Task, long Generation)> inFlight = new();
    readonly ConcurrentQueue<Call> calls = new();
    long generation;
    int running;

    /// <summary>Commands that only read. Anything else may change PowerFab.</summary>
    public static bool IsRead(string command) =>
        command.EndsWith("_Get", StringComparison.Ordinal)
        || command.StartsWith("Get", StringComparison.Ordinal)
        || command is "MaxTruck" or "ValTruck" or "ValJob" or "Ping" or "Version" or "ProductionControlLoad_IsDuplicateLoadNumber";

    /// <summary>
    /// Runs <paramref name="command"/> through <paramref name="send"/> (the bridge), sharing or
    /// reusing a read as described above.
    /// </summary>
    public async Task<PowerFabReply> RunAsync(string command, string json, string? device, Func<CancellationToken, Task<PowerFabReply>> send, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        if (!IsRead(command))
        {
            // A write: nothing read before it may be used after it.
            lock (sync)
            {
                generation++;
                kept.Clear();
            }
            var waiting = Interlocked.Increment(ref running) - 1;
            try
            {
                var reply = await send(ct);
                Record(command, watch.ElapsedMilliseconds, reply, "write", waiting, device);
                return reply;
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        var key = command + "\n" + json;
        Task<PowerFabReply> task;
        TaskCompletionSource<PowerFabReply>? mine = null;
        long started = 0;
        string how;
        lock (sync)
        {
            var fresh = command == "ProductionControlJob_Get" ? JobListFresh : ReadFresh;
            if (kept.TryGetValue(key, out var k) && k.Generation == generation && DateTime.UtcNow - k.At < fresh)
            {
                Record(command, 0, k.Reply, "reused", 0, device);
                return k.Reply;
            }
            if (inFlight.TryGetValue(key, out var f) && f.Generation == generation)
            {
                task = f.Task;
                how = "shared";
            }
            else
            {
                started = generation;
                // Registered before the call starts, so one answered at once is never left behind.
                mine = new TaskCompletionSource<PowerFabReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                task = mine.Task;
                inFlight[key] = (task, started);
                how = "read";
            }
        }
        // Not tied to this request: another device may be waiting on the same answer.
        if (mine is not null)
            _ = ReadAsync(key, started, send, mine);
        var waitingNow = Volatile.Read(ref running) - (how == "read" ? 1 : 0);
        var answer = await task.WaitAsync(ct);
        Record(command, watch.ElapsedMilliseconds, answer, how, Math.Max(0, waitingNow), device);
        return answer;
    }

    async Task ReadAsync(string key, long started, Func<CancellationToken, Task<PowerFabReply>> send, TaskCompletionSource<PowerFabReply> answer)
    {
        Interlocked.Increment(ref running);
        PowerFabReply? reply = null;
        Exception? failed = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            reply = await send(timeout.Token);
        }
        catch (Exception ex)
        {
            failed = ex;
        }
        finally
        {
            Interlocked.Decrement(ref running);
            lock (sync)
            {
                if (reply is { Error: null, Status: >= 200 and < 300 } && started == generation)
                    kept[key] = new Kept(reply, DateTime.UtcNow, started);
                if (inFlight.TryGetValue(key, out var f) && f.Task == answer.Task)
                    inFlight.Remove(key);
            }
        }
        if (failed is not null)
            answer.SetException(failed);
        else
            answer.SetResult(reply!);
    }

    void Record(string command, long ms, PowerFabReply reply, string how, int waiting, string? device)
    {
        calls.Enqueue(new Call(DateTime.UtcNow.ToString("o"), command, ms, reply.Body.Length, reply.Status, reply.Error, how, waiting, device));
        while (calls.Count > Keep && calls.TryDequeue(out _))
        {
        }
    }

    /// <summary>PowerFab calls going on right now (reads shared between devices count once).</summary>
    public int Running => Volatile.Read(ref running);

    /// <summary>
    /// For the admin board: per command over the kept calls (count, how many were shared or reused,
    /// average and slowest time, average reply size), and the newest calls.
    /// </summary>
    public object Snapshot(int newest = 40)
    {
        var list = calls.ToArray();
        return new
        {
            running = Running,
            kept = list.Length,
            since = list.Length > 0 ? list[0].At : null,
            commands = list.GroupBy(c => c.Command).Select(g =>
            {
                var real = g.Where(c => c.How is "read" or "write").ToList();
                return new
                {
                    command = g.Key,
                    calls = g.Count(),
                    toPowerFab = real.Count,
                    sharedOrReused = g.Count() - real.Count,
                    averageMs = real.Count == 0 ? 0 : (long)real.Average(c => c.Ms),
                    slowestMs = real.Count == 0 ? 0 : real.Max(c => c.Ms),
                    averageBytes = real.Count == 0 ? 0 : (long)real.Average(c => c.Bytes),
                    errors = g.Count(c => c.Error is not null || c.Status >= 400),
                };
            }).OrderByDescending(c => c.averageMs * c.toPowerFab).ToList(),
            newest = list.Reverse().Take(newest).ToList(),
        };
    }
}

/// <summary>
/// How long scans took on the devices (the app's js/scan-timing.js sends its last scans): kept in
/// memory for the admin board's Server page.
/// </summary>
public sealed class ScanTimes
{
    const int Keep = 300;

    public sealed record Step(string Name, long Ms);

    public sealed record Scan(string? Id, string? Mark, string? Kind, string? At, int Ahead, long WaitMs, long? TotalMs, bool? Ok, string? Error, List<Step>? Steps);

    public sealed record Kept(string DeviceId, string DeviceName, string ReceivedAt, Scan Scan);

    public sealed record Batch(List<Scan>? Scans);

    readonly ConcurrentQueue<Kept> scans = new();

    public int Add(Device device, IEnumerable<Scan> list)
    {
        var n = 0;
        var now = DateTime.UtcNow.ToString("o");
        foreach (var s in list.Take(100))
        {
            var clean = s with
            {
                Mark = Trim(s.Mark, 80),
                Kind = Trim(s.Kind, 20),
                At = Trim(s.At, 40),
                Error = Trim(s.Error, 200),
                Steps = s.Steps?.Take(12).Select(x => x with { Name = Trim(x.Name, 40) ?? "" }).ToList(),
            };
            scans.Enqueue(new Kept(device.Id, device.Name, now, clean));
            n++;
        }
        while (scans.Count > Keep && scans.TryDequeue(out _))
        {
        }
        return n;
    }

    static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    public object Snapshot(int newest = 40)
    {
        var list = scans.ToArray();
        var done = list.Where(k => k.Scan.TotalMs is not null).ToList();
        return new
        {
            kept = list.Length,
            devices = done.GroupBy(k => k.DeviceId).Select(g => new
            {
                device = g.Last().DeviceName,
                scans = g.Count(),
                averageMs = (long)g.Average(k => k.Scan.TotalMs ?? 0),
                slowestMs = g.Max(k => k.Scan.TotalMs ?? 0),
                averageWaitMs = (long)g.Average(k => k.Scan.WaitMs),
                last = g.Last().Scan.At,
            }).ToList(),
            newest = list.Reverse().Take(newest).Select(k => new { device = k.DeviceName, k.Scan.Mark, k.Scan.At, k.Scan.Ahead, k.Scan.WaitMs, k.Scan.TotalMs, k.Scan.Ok, k.Scan.Error, k.Scan.Steps }).ToList(),
        };
    }
}
