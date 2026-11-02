using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TerraTeklaServer.Tests;

public sealed class PhotoBrowseTests : IDisposable
{
    const string Key = "test-key-0123456789";
    static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xD9];
    readonly string dataDir = Path.Combine(Path.GetTempPath(), $"terra-test-{Guid.NewGuid():N}");
    string PhotoRoot => Path.Combine(dataDir, "photos");
    readonly List<WebApplicationFactory<Program>> factories = [];

    WebApplicationFactory<Program> Server(bool isPublic = true)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ACCESS_KEY", Key);
            b.UseSetting("DATA_DIR", dataDir);
            b.UseSetting("CLIENT_ROOT", "");
            b.UseSetting("CONVERSION_URL", "");
            b.UseSetting("SETTINGS_FILE", Path.Combine(dataDir, "local.settings.json"));
            b.UseSetting("PHOTOS_ENABLED", "true");
            b.UseSetting("PHOTOS_ROOT", PhotoRoot);
            b.UseSetting("PHOTOS_RULES", Path.Combine(dataDir, "no-rules.json"));
            if (!isPublic)
                b.UseSetting("PHOTOS_PUBLIC", "false");
        });
        factories.Add(factory);
        return factory;
    }

    public void Dispose()
    {
        foreach (var f in factories)
            f.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    static HttpClient Keyed(WebApplicationFactory<Program> server, string? admin = null)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        client.DefaultRequestHeaders.Add("X-Terra-Device", "ipad-0000-a");
        if (admin is not null)
            client.DefaultRequestHeaders.Add("X-Terra-Admin", admin);
        return client;
    }

    static async Task Upload(HttpClient client, string job, string load, int count)
    {
        using var form = new MultipartFormDataContent();
        for (var i = 0; i < count; i++)
            form.Add(new ByteArrayContent(Jpeg), "file", $"p{i}.jpg");
        var reply = await client.PostAsync($"/api/photos/{job}/{load}", form);
        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);
    }

    [Fact]
    public async Task Anyone_can_look_through_jobs_loads_and_photos_but_only_the_key_shows_where()
    {
        var server = Server();
        var ipad = Keyed(server);
        await Upload(ipad, "1001", "8", 2);
        await Upload(ipad, "1002", "3", 1);
        // A photo put in a job's folder by hand (or from before this server) shows too.
        Directory.CreateDirectory(Path.Combine(PhotoRoot, "1003", "5"));
        await File.WriteAllBytesAsync(Path.Combine(PhotoRoot, "1003", "5", "old.jpg"), Jpeg);
        Directory.CreateDirectory(Path.Combine(PhotoRoot, "1003", "5", ".deleted"));

        var anyone = server.CreateClient();
        var jobs = await anyone.GetFromJsonAsync<JsonObject>("/api/browse");
        Assert.False((bool)jobs!["keyed"]!);
        var list = jobs["jobs"]!.AsArray();
        Assert.Equal(["1003", "1002", "1001"], list.Select(j => (string)j!["job"]!));
        Assert.Equal(2, (int)list.First(j => (string)j!["job"]! == "1001")!["photos"]!);
        Assert.All(list, j => Assert.Null(j!["folder"]));

        var loads = await anyone.GetFromJsonAsync<JsonObject>("/api/browse/1001");
        Assert.Equal("8", (string)loads!["loads"]!.AsArray().Single()!["load"]!);
        var files = await anyone.GetFromJsonAsync<JsonObject>("/api/browse/1001/8");
        var names = files!["files"]!.AsArray().Select(f => (string)f!["name"]!).ToList();
        Assert.Equal(2, names.Count);
        Assert.All(files["files"]!.AsArray(), f => Assert.Null(f!["path"]));
        var picture = await anyone.GetAsync($"/api/browse/1001/8/{Uri.EscapeDataString(names[0])}");
        Assert.Equal(HttpStatusCode.OK, picture.StatusCode);
        Assert.Equal("image/jpeg", picture.Content.Headers.ContentType?.MediaType);

        // Looking is all: the other photo routes still need the key.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anyone.GetAsync("/api/photos/1001/8")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anyone.DeleteAsync($"/api/admin/photos/1001/8/{Uri.EscapeDataString(names[0])}")).StatusCode);

        // With the key: where everything is stored.
        var mine = await ipad.GetFromJsonAsync<JsonObject>("/api/browse/1001/8");
        Assert.True((bool)mine!["keyed"]!);
        Assert.Equal(Path.Combine(PhotoRoot, "1001", "8"), (string?)mine["folder"]);
        Assert.Equal(Path.Combine(PhotoRoot, "1001", "8", names[0]), (string?)mine["files"]![0]!["path"]);
        Assert.Equal(Path.Combine(PhotoRoot, "1003"), (string?)(await ipad.GetFromJsonAsync<JsonObject>("/api/browse"))!["jobs"]!.AsArray().First()!["folder"]);
    }

    [Fact]
    public async Task Only_the_admin_code_deletes_a_photo()
    {
        var server = Server();
        var ipad = Keyed(server);
        await Upload(ipad, "1001", "8", 2);
        var name = (string)(await ipad.GetFromJsonAsync<JsonObject>("/api/browse/1001/8"))!["files"]![0]!["name"]!;
        var path = $"/api/admin/photos/1001/8/{Uri.EscapeDataString(name)}";

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await ipad.DeleteAsync($"/api/photos/1001/8/{Uri.EscapeDataString(name)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ipad.DeleteAsync(path)).StatusCode);

        var code = File.ReadAllText(AdminLock.CodeFilePath(dataDir)).Split('\n')[0].Trim();
        var unlocked = await ipad.PostAsJsonAsync("/api/admin/unlock", new { pin = code });
        var token = (string)(await unlocked.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        Assert.Equal(HttpStatusCode.OK, (await Keyed(server, token).DeleteAsync(path)).StatusCode);

        Assert.Single((await server.CreateClient().GetFromJsonAsync<JsonObject>("/api/browse/1001/8"))!["files"]!.AsArray());
        Assert.True(File.Exists(Path.Combine(PhotoRoot, "1001", "8", ".deleted", name)));
    }

    [Fact]
    public async Task The_explorer_can_be_kept_to_key_holders()
    {
        var server = Server(isPublic: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.CreateClient().GetAsync("/api/browse")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Keyed(server).GetAsync("/api/browse")).StatusCode);
    }
}
