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

    [Fact]
    public async Task Every_photo_says_which_ipad_user_took_it_in_the_file_and_the_explorer()
    {
        var server = Server();
        var ipad = Keyed(server);
        await Upload(ipad, "1001", "8", 1);

        var listed = (await server.CreateClient().GetFromJsonAsync<JsonObject>("/api/browse/1001/8"))!["files"]!.AsArray().Single()!;
        Assert.Equal("User 1", (string?)listed["user"]);
        Assert.Equal("User 1", (string?)listed["by"]);
        Assert.False((bool)listed["byGuess"]!);
        var keyedList = (await ipad.GetFromJsonAsync<JsonObject>("/api/photos/1001/8"))!["files"]!.AsArray().Single()!;
        Assert.Equal("User 1", (string?)keyedList["user"]);
        var recent = (await ipad.GetFromJsonAsync<JsonArray>("/api/photos/recent"))!.Single()!;
        Assert.Equal("User 1", (string?)recent["user"]);

        // Written into the JPEG itself: EXIF Artist and XMP, readable once the file is copied elsewhere.
        var saved = Directory.GetFiles(Path.Combine(PhotoRoot, "1001", "8")).Single();
        var bytes = await File.ReadAllBytesAsync(saved);
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Contains("Exif\0\0II*", text);
        Assert.Contains("<terra:User>User 1</terra:User>", text);
        Assert.Contains("<dc:creator><rdf:Seq><rdf:li>User 1</rdf:li>", text);
        Assert.Equal(("User 1", "User 1"), PhotoMetadata.ReadUser(saved));
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, bytes[^2..]);
    }

    [Fact]
    public async Task Photos_from_before_show_who_took_them_as_far_as_can_be_told()
    {
        var server = Server();
        var folder = Path.Combine(PhotoRoot, "1003", "5");
        Directory.CreateDirectory(folder);
        // From an older server: the iPad's name is only in the file name.
        await File.WriteAllBytesAsync(Path.Combine(folder, "20261028-101500_1001-C1_Yard iPad.jpg"), Jpeg);
        await File.WriteAllBytesAsync(Path.Combine(folder, "20261028-101501_load_Yard iPad (2).jpg"), Jpeg);
        // Copied in by hand: nothing to go on.
        await File.WriteAllBytesAsync(Path.Combine(folder, "old.jpg"), Jpeg);
        // Stamped by this server, but the database has no record of it (moved from another job).
        var tag = new PhotoTag("User 4", "Shop iPad", "dev-x", "1003", "5", null, DateTime.Now);
        await File.WriteAllBytesAsync(Path.Combine(folder, "moved.jpg"), PhotoMetadata.StampJpeg(Jpeg, tag));

        var files = (await server.CreateClient().GetFromJsonAsync<JsonObject>("/api/browse/1003/5"))!["files"]!.AsArray()
            .ToDictionary(f => (string)f!["name"]!);
        Assert.Equal("Yard iPad", (string?)files["20261028-101500_1001-C1_Yard iPad.jpg"]!["by"]);
        Assert.True((bool)files["20261028-101500_1001-C1_Yard iPad.jpg"]!["byGuess"]!);
        Assert.Equal("Yard iPad", (string?)files["20261028-101501_load_Yard iPad (2).jpg"]!["by"]);
        Assert.Null((string?)files["old.jpg"]!["by"]);
        Assert.Equal("User 4", (string?)files["moved.jpg"]!["user"]);
        Assert.Equal("Shop iPad", (string?)files["moved.jpg"]!["by"]);
    }

    [Fact]
    public void A_camera_photo_keeps_its_own_exif_and_gets_only_the_xmp()
    {
        // SOI, APP1 Exif (camera's), DQT..., EOI.
        byte[] exif = [0xFF, 0xE1, 0, 10, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, 1, 2];
        byte[] jpeg = [0xFF, 0xD8, .. exif, 0xFF, 0xDB, 0, 3, 7, 0xFF, 0xD9];
        var tag = new PhotoTag("User 2", "Yard <iPad>", "dev-y", "1001", "8", "1001-C1", DateTime.Now);
        var stamped = PhotoMetadata.StampJpeg(jpeg, tag);
        var text = System.Text.Encoding.Latin1.GetString(stamped);
        Assert.Equal(1, text.Split("Exif\0\0").Length - 1);
        Assert.Contains("User 2 (Yard &lt;iPad&gt;)", text);
        // The camera's EXIF is untouched and still first; XMP follows it; the image data is unchanged.
        Assert.Equal(exif, stamped[2..14]);
        Assert.Equal(0xE1, stamped[15]);
        Assert.Equal(jpeg[14..], stamped[^jpeg[14..].Length..]);
        // Not a JPEG: left alone.
        byte[] png = [0x89, 0x50, 0x4E, 0x47];
        Assert.Same(png, PhotoMetadata.StampJpeg(png, tag));
    }
}
