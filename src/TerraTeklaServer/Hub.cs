using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TerraTeklaServer;

/// <summary>
/// The live connections (GET /ws, a WebSocket): every iPad and every open admin page. Whenever a load,
/// a device or the history changes, every connection is sent the change at once, which is how a scan
/// on one iPad shows on the other straight away. Messages are JSON, listed in docs/SETUP.md.
/// </summary>
public sealed class Hub(ILogger<Hub> log)
{
    sealed record Connection(string Id, string? DeviceId, string Kind, WebSocket Socket, DateTime Since, SemaphoreSlim SendLock);

    readonly ConcurrentDictionary<string, Connection> connections = new();

    /// <summary>Fires when a device connects or disconnects, so the device list can be sent again.</summary>
    public event Action? Changed;

    /// <summary>Device ids with at least one open connection, and since when.</summary>
    public Dictionary<string, DateTime> Online() => connections.Values
        .Where(c => c.DeviceId is not null)
        .GroupBy(c => c.DeviceId!)
        .ToDictionary(g => g.Key, g => g.Min(c => c.Since));

    public int AdminCount => connections.Values.Count(c => c.Kind == "admin");

    /// <summary>Runs one connection until it closes. The first message has already been checked.</summary>
    public async Task RunAsync(WebSocket socket, string? deviceId, string kind, object welcome, CancellationToken ct)
    {
        var conn = new Connection(Guid.NewGuid().ToString("N"), deviceId, kind, socket, DateTime.UtcNow, new SemaphoreSlim(1, 1));
        connections[conn.Id] = conn;
        Changed?.Invoke();
        try
        {
            await SendAsync(conn, welcome);
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                // The only thing a client sends after hello is "ping" to keep proxies from closing an idle line.
                if (result.MessageType == WebSocketMessageType.Text && result.EndOfMessage && result.Count < 64
                    && Encoding.UTF8.GetString(buffer, 0, result.Count).Contains("\"ping\""))
                    await SendAsync(conn, new { type = "pong", at = DateTime.UtcNow });
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            connections.TryRemove(conn.Id, out _);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                {
                }
            }
            Changed?.Invoke();
        }
    }

    /// <summary>Sends a message to every connection; one that fails is dropped.</summary>
    public void Broadcast(object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json.Options);
        foreach (var conn in connections.Values)
            _ = SendBytesAsync(conn, bytes);
    }

    Task SendAsync(Connection conn, object message) => SendBytesAsync(conn, JsonSerializer.SerializeToUtf8Bytes(message, Json.Options));

    async Task SendBytesAsync(Connection conn, byte[] bytes)
    {
        await conn.SendLock.WaitAsync();
        try
        {
            if (conn.Socket.State == WebSocketState.Open)
                await conn.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Dropping connection {Id}", conn.Id);
            connections.TryRemove(conn.Id, out _);
        }
        finally
        {
            conn.SendLock.Release();
        }
    }
}
