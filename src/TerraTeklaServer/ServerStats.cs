using System.Collections.Concurrent;
using System.Diagnostics;

namespace TerraTeklaServer;

public sealed record LogLine(string At, string Level, string Category, string Message);

/// <summary>
/// What the admin page's Server view shows about how the server is doing: requests and errors per
/// minute for the last hour, slow requests, PowerFab bridge calls, and the last warnings and errors
/// from the log. Kept in memory only, so it starts empty after a restart.
/// </summary>
public sealed class ServerStats
{
    public readonly DateTime StartedAt = DateTime.UtcNow;

    // One bucket per minute for the last hour.
    readonly Minute[] minutes = Enumerable.Range(0, 60).Select(_ => new Minute()).ToArray();
    readonly ConcurrentQueue<LogLine> logs = new();
    long totalRequests;
    long totalErrors;

    sealed class Minute
    {
        public long Stamp = -1;
        public int Requests;
        public int Errors;
        public int Bridge;
        public int BridgeErrors;
        public long Milliseconds;
        public long BridgeMilliseconds;
    }

    /// <summary>Counts one finished request (status 500 and up count as errors).</summary>
    public void Request(int status, long ms, bool bridge)
    {
        Interlocked.Increment(ref totalRequests);
        if (status >= 500)
            Interlocked.Increment(ref totalErrors);
        var m = Bucket();
        lock (m)
        {
            m.Requests++;
            m.Milliseconds += ms;
            if (status >= 500)
                m.Errors++;
            if (bridge)
            {
                m.Bridge++;
                m.BridgeMilliseconds += ms;
                if (status >= 500)
                    m.BridgeErrors++;
            }
        }
    }

    public void Log(LogLine line)
    {
        logs.Enqueue(line);
        while (logs.Count > 100 && logs.TryDequeue(out _))
        {
        }
    }

    public object Snapshot()
    {
        var now = MinuteNow();
        var series = new List<object>();
        int requests = 0, errors = 0, bridge = 0, bridgeErrors = 0;
        long ms = 0, bridgeMs = 0;
        for (var i = 59; i >= 0; i--)
        {
            var stamp = now - i;
            var m = minutes[(int)(stamp % 60)];
            lock (m)
            {
                var live = m.Stamp == stamp;
                series.Add(new { minute = DateTime.UnixEpoch.AddMinutes(stamp), requests = live ? m.Requests : 0, errors = live ? m.Errors : 0 });
                if (!live)
                    continue;
                requests += m.Requests;
                errors += m.Errors;
                bridge += m.Bridge;
                bridgeErrors += m.BridgeErrors;
                ms += m.Milliseconds;
                bridgeMs += m.BridgeMilliseconds;
            }
        }
        return new
        {
            totalRequests = Interlocked.Read(ref totalRequests),
            totalErrors = Interlocked.Read(ref totalErrors),
            lastHour = new
            {
                requests,
                errors,
                averageMs = requests == 0 ? 0 : ms / requests,
                bridgeCalls = bridge,
                bridgeErrors,
                bridgeAverageMs = bridge == 0 ? 0 : bridgeMs / bridge,
            },
            perMinute = series,
            log = logs.Reverse().Take(50).ToArray(),
        };
    }

    static long MinuteNow() => (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMinutes;

    Minute Bucket()
    {
        var stamp = MinuteNow();
        var m = minutes[(int)(stamp % 60)];
        lock (m)
        {
            if (m.Stamp != stamp)
            {
                m.Stamp = stamp;
                m.Requests = m.Errors = m.Bridge = m.BridgeErrors = 0;
                m.Milliseconds = m.BridgeMilliseconds = 0;
            }
        }
        return m;
    }

    /// <summary>The server process itself: memory, CPU time, threads, and the computer it runs on.</summary>
    public object Process()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return new
        {
            startedAt = StartedAt,
            uptimeSeconds = (long)(DateTime.UtcNow - StartedAt).TotalSeconds,
            memoryBytes = p.WorkingSet64,
            managedBytes = GC.GetTotalMemory(false),
            cpuSeconds = Math.Round(p.TotalProcessorTime.TotalSeconds, 1),
            threads = p.Threads.Count,
            machine = Environment.MachineName,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            dotnet = Environment.Version.ToString(),
            processors = Environment.ProcessorCount,
            service = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService(),
        };
    }

    /// <summary>Free and total space on the drive holding a folder, or null when it can't be read.</summary>
    public static object? Disk(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root))
                return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? new { folder, drive = drive.Name, freeBytes = drive.AvailableFreeSpace, totalBytes = drive.TotalSize } : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>Keeps the last warnings and errors from the log for the admin page's Server view.</summary>
public sealed class StatsLoggerProvider(ServerStats stats) : ILoggerProvider
{
    public ILogger CreateLogger(string category) => new Logger(stats, category);

    public void Dispose()
    {
    }

    sealed class Logger(ServerStats stats, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format)
        {
            if (!IsEnabled(level))
                return;
            var message = format(state, ex);
            if (ex is not null)
                message += $" ({ex.GetType().Name}: {ex.Message})";
            stats.Log(new LogLine(DateTime.UtcNow.ToString("o"), level.ToString(), category.Split('.').Last(), message));
        }
    }
}

/// <summary>Times every request for <see cref="ServerStats"/>.</summary>
public static class StatsMiddleware
{
    public static IApplicationBuilder UseRequestStats(this IApplicationBuilder app, ServerStats stats) => app.Use(async (http, next) =>
    {
        // The live connection stays open for hours; it isn't a request worth timing.
        if (http.Request.Path.StartsWithSegments("/ws"))
        {
            await next(http);
            return;
        }
        var watch = Stopwatch.StartNew();
        var bridge = http.Request.Path.StartsWithSegments("/api/powerfab");
        try
        {
            await next(http);
        }
        catch
        {
            // A browser that went away mid-request isn't a server error.
            stats.Request(http.RequestAborted.IsCancellationRequested ? 499 : 500, watch.ElapsedMilliseconds, bridge);
            throw;
        }
        stats.Request(http.Response.StatusCode, watch.ElapsedMilliseconds, bridge);
    });
}
