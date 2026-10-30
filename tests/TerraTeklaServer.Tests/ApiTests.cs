using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TerraTeklaServer.Tests;

public sealed class ApiTests : IDisposable
{
    const string Key = "test-key-0123456789";
    readonly string dataDir = Path.Combine(Path.GetTempPath(), $"terra-test-{Guid.NewGuid():N}");
    readonly WebApplicationFactory<Program> factory;

    public ApiTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ACCESS_KEY", Key);
            b.UseSetting("DATA_DIR", dataDir);
            b.UseSetting("CLIENT_ROOT", "");
            b.UseSetting("CONVERSION_URL", "");
        });
    }

    public void Dispose()
    {
        factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    HttpClient Client(string? device = "ipad-0000-a")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        if (device is not null)
            client.DefaultRequestHeaders.Add("X-Terra-Device", device);
        return client;
    }

    static object ScanOp(string op, string entryId, string mark, object? piece = null) => new
    {
        op, opId = Guid.NewGuid().ToString("N"), job = "J1", loadNumber = "8", entryId, mark, piece,
    };

    [Fact]
    public async Task Health_needs_no_key_but_the_api_does()
    {
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/devices")).StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/devices")).StatusCode);
    }

    [Fact]
    public async Task A_new_device_gets_a_name_and_colour_and_can_change_them()
    {
        var a = Client("ipad-0000-a");
        var me = await a.GetFromJsonAsync<JsonObject>("/api/me");
        Assert.Equal("iPad 1", (string?)me!["name"]);
        Assert.StartsWith("#", (string?)me["color"]);

        var renamed = await a.PutAsJsonAsync("/api/me", new { name = "Yard iPad", color = "#2bd96b" });
        renamed.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PutAsJsonAsync("/api/me", new { color = "green" })).StatusCode);

        var devices = await a.GetFromJsonAsync<JsonArray>("/api/devices");
        var device = devices!.Single()!;
        Assert.Equal("Yard iPad", (string?)device["name"]);
        Assert.Equal("#2bd96b", (string?)device["color"]);
        var history = await a.GetFromJsonAsync<JsonArray>("/api/activity?device=ipad-0000-a");
        Assert.Contains(history!, h => (string?)h!["kind"] == "device-rename");
    }

    [Fact]
    public async Task Ops_need_the_device_header()
    {
        var response = await Client(device: null).PostAsJsonAsync("/api/loads/L1/ops", ScanOp("beginScan", "e1", "1001-C1"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Stacks_are_kept_on_the_server_with_who_scanned_each_piece()
    {
        var a = Client("ipad-0000-a");
        await a.PostAsJsonAsync("/api/loads/L1/ops", ScanOp("beginScan", "e1", "1001-C1"));
        var reply = await a.PostAsJsonAsync("/api/loads/L1/ops", ScanOp("finishScan", "e1", "1001-C1", new { mark = "1001-C1", sequence = "1", weight = 1055 }));
        reply.EnsureSuccessStatusCode();

        // Another iPad reads the same load.
        var load = await Client("ipad-0000-b").GetFromJsonAsync<JsonObject>("/api/loads/L1?job=J1&number=8");
        var row = load!["stacks"]![0]!["items"]![0]!;
        Assert.Equal("1001-C1", (string?)row["mark"]);
        Assert.Equal("ipad-0000-a", (string?)row["scannedBy"]);
        Assert.Equal(1055, (int)row["weight"]!);

        var jobs = await a.GetFromJsonAsync<JsonArray>("/api/jobs");
        Assert.Equal(1, (int)jobs!.Single()!["pieces"]!);
        var history = await a.GetFromJsonAsync<JsonArray>("/api/activity?load=L1");
        Assert.Contains(history!, h => (string?)h!["kind"] == "scan" && (string?)h["deviceName"] == "iPad 1");
    }

    [Fact]
    public async Task A_bad_operation_answers_400()
    {
        var response = await Client().PostAsJsonAsync("/api/loads/L1/ops", new { op = "explode", opId = "x1", job = "J1", loadNumber = "8" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_change_on_one_ipad_is_sent_live_to_the_other()
    {
        var socket = await Connect("ipad-0000-b", Key);
        var welcome = await Receive(socket, "welcome");
        Assert.Equal("ipad-0000-b", (string?)welcome["device"]!["id"]);

        await Client("ipad-0000-a").PostAsJsonAsync("/api/loads/L1/ops", ScanOp("beginScan", "e1", "1001-C1"));
        var load = await Receive(socket, "load");
        Assert.Equal("ipad-0000-a", (string?)load["by"]);
        Assert.Equal("1001-C1", (string?)load["state"]!["stacks"]![0]!["items"]![0]!["mark"]);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task The_live_connection_refuses_a_wrong_key()
    {
        var socket = await Connect("ipad-0000-b", "wrong");
        var buffer = new byte[256];
        var result = await socket.ReceiveAsync(buffer, new CancellationTokenSource(5000).Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(4401, (int)result.CloseStatus!);
    }

    [Fact]
    public async Task Powerfab_calls_answer_502_without_a_conversion_url()
    {
        var response = await Client().PostAsJsonAsync("/api/powerfab/GetLoads", new { job = "J1" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Photos_are_refused_while_off()
    {
        var info = await Client().GetFromJsonAsync<JsonObject>("/api/photos");
        Assert.False((bool)info!["enabled"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().GetAsync("/api/photos/J1/8")).StatusCode);
    }

    [Fact]
    public async Task The_docs_ship_with_the_build()
    {
        var setup = await Client().GetStringAsync("/api/docs/setup");
        Assert.Contains("# TERRA server", setup);
        Assert.Contains("PHOTOS_ROOT", await Client().GetStringAsync("/api/docs/photos"));
    }

    async Task<WebSocket> Connect(string deviceId, string key)
    {
        var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri(factory.Server.BaseAddress, "/ws"), CancellationToken.None);
        var hello = new JsonObject { ["type"] = "hello", ["key"] = key, ["deviceId"] = deviceId, ["kind"] = "ipad" }.ToJsonString();
        await socket.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, true, CancellationToken.None);
        return socket;
    }

    static async Task<JsonObject> Receive(WebSocket socket, string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var json = JsonNode.Parse(message.ToArray())!.AsObject();
            if ((string?)json["type"] == type)
                return json;
        }
    }
}
