using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TerraTeklaServer.Tests;

/// <summary>The admin code, and the settings and stats it locks.</summary>
public sealed class AdminTests : IDisposable
{
    const string Key = "test-key-0123456789";
    readonly string dataDir = Path.Combine(Path.GetTempPath(), $"terra-admin-{Guid.NewGuid():N}");
    readonly string settingsPath;
    readonly WebApplicationFactory<Program> factory;

    public AdminTests()
    {
        Directory.CreateDirectory(dataDir);
        settingsPath = Path.Combine(dataDir, "local.settings.json");
        File.WriteAllText(settingsPath, """
            {
              // kept as it is
              "Kestrel": { "Limits": { "MaxConcurrentConnections": 100 } },
              "PHOTOS_MAX_MB": "25"
            }
            """);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ACCESS_KEY", Key);
            b.UseSetting("DATA_DIR", dataDir);
            b.UseSetting("CLIENT_ROOT", "");
            b.UseSetting("CONVERSION_URL", "");
            b.UseSetting("SETTINGS_FILE", settingsPath);
            b.UseSetting("PHOTOS_ENABLED", "true");
            b.UseSetting("PHOTOS_ROOT", Path.Combine(dataDir, "photos"));
            b.UseSetting("PHOTOS_RULES", Path.Combine(dataDir, "photo-rules.json"));
        });
    }

    public void Dispose()
    {
        factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    HttpClient Client(string? admin = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        if (admin is not null)
            client.DefaultRequestHeaders.Add("X-Terra-Admin", admin);
        return client;
    }

    // The code the server made up at startup, from DATA_DIR\admin-code.txt.
    async Task<string> MadeUpCode()
    {
        await Client().GetAsync("/health"); // starts the server
        return File.ReadAllText(AdminLock.CodeFilePath(dataDir)).Split('\n')[0].Trim();
    }

    // Changes the made-up code to `pin` and returns an unlocked token.
    async Task<string> Setup(string pin = "2468")
    {
        var code = await MadeUpCode();
        var unlocked = await Client().PostAsJsonAsync("/api/admin/unlock", new { pin = code });
        var token = (string)(await unlocked.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        var reply = await Client(token).PutAsJsonAsync("/api/admin/pin", new { pin });
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        return (string)(await reply.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
    }

    [Fact]
    public async Task Settings_and_stats_are_locked_on_the_server()
    {
        foreach (var path in new[] { "/api/admin/settings", "/api/admin/stats", "/api/admin/photo-rules", "/api/admin/backup" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Client().GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Client("made-up-token").GetAsync(path)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().PutAsJsonAsync("/api/admin/settings", new { PHOTOS_MAX_MB = "5" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().PostAsync("/api/admin/restart", null)).StatusCode);
        // And still the access key first.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/admin/lock")).StatusCode);
    }

    [Fact]
    public async Task The_server_makes_up_the_first_code_and_keeps_only_its_hash()
    {
        var code = await MadeUpCode();
        Assert.Matches("^[0-9]{6}$", code);
        var lockState = await Client().GetFromJsonAsync<JsonObject>("/api/admin/lock");
        Assert.True((bool)lockState!["pinSet"]!);
        // Nobody can pick the first code any more.
        Assert.NotEqual(HttpStatusCode.OK, (await Client().PostAsJsonAsync("/api/admin/setup", new { pin = "1111" })).StatusCode);

        var file = File.ReadAllText(settingsPath);
        Assert.DoesNotContain(code, file);
        Assert.Contains("pbkdf2-sha256$", file);
        Assert.Contains("MaxConcurrentConnections", file);

        // Changing it writes the new code down in its place.
        var token = await Setup("2468");
        Assert.StartsWith("2468", File.ReadAllText(AdminLock.CodeFilePath(dataDir)));
        Assert.Equal(HttpStatusCode.OK, (await Client(token).GetAsync("/api/admin/stats")).StatusCode);
    }

    [Fact]
    public async Task Only_the_admin_board_renames_an_ipad_and_its_number_stays()
    {
        var ipad = factory.CreateClient();
        ipad.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        ipad.DefaultRequestHeaders.Add("X-Terra-Device", "ipad-0000-a");
        await ipad.GetAsync("/api/me");
        var admin = Client(await Setup());
        var renamed = await admin.PutAsJsonAsync("/api/admin/devices/ipad-0000-a", new { name = "Yard iPad", color = "#2bd96b" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/devices/ipad-0000-a", new { color = "green" })).StatusCode);
        var me = await ipad.GetFromJsonAsync<JsonObject>("/api/me");
        Assert.Equal("Yard iPad", (string?)me!["name"]);
        Assert.Equal("#2bd96b", (string?)me["color"]);
        Assert.Equal(1, (int)me["number"]!);
        var history = await ipad.GetFromJsonAsync<JsonArray>("/api/activity?device=admin");
        Assert.Contains(history!, h => (string?)h!["kind"] == "device-rename");
    }

    [Fact]
    public void Devices_from_before_numbers_are_numbered_in_the_order_they_were_first_seen()
    {
        var path = Path.Combine(dataDir, "old.db");
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE devices (id TEXT PRIMARY KEY, name TEXT NOT NULL, color TEXT NOT NULL, created_at TEXT NOT NULL, last_seen_at TEXT NOT NULL, last_ip TEXT, user_agent TEXT, app_version TEXT);
                INSERT INTO devices VALUES ('second-device', 'iPad 2', '#2f9bff', '2026-10-30T10:00:00Z', '2026-10-30T10:00:00Z', NULL, NULL, NULL);
                INSERT INTO devices VALUES ('first-device', 'iPad 1', '#ffb000', '2026-10-29T10:00:00Z', '2026-10-29T10:00:00Z', NULL, NULL, NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        var oldDir = Path.Combine(dataDir, "old");
        Directory.CreateDirectory(oldDir);
        File.Move(path, Path.Combine(oldDir, "terra.db"));
        var store = new Store(new Settings("", "", "", "", oldDir, [], PhotoSettings.From(new ConfigurationBuilder().Build())));
        Assert.Equal(1, store.GetDevice("first-device")!.Number);
        Assert.Equal(2, store.GetDevice("second-device")!.Number);
        Assert.Equal(3, store.Touch("third-device", null, null, null).Device.Number);
    }

    [Fact]
    public async Task A_new_random_code_needs_an_unlocked_device_and_is_written_down()
    {
        var token = await Setup("2468");
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().PostAsync("/api/admin/pin/generate", null)).StatusCode);

        var made = await (await Client(token).PostAsync("/api/admin/pin/generate", null)).Content.ReadFromJsonAsync<JsonObject>();
        var code = (string)made!["code"]!;
        Assert.Matches("^[0-9]{6}$", code);
        Assert.StartsWith(code, File.ReadAllText(AdminLock.CodeFilePath(dataDir)));
        Assert.Equal(HttpStatusCode.OK, (await Client((string)made["token"]!).GetAsync("/api/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(token).GetAsync("/api/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().PostAsJsonAsync("/api/admin/unlock", new { pin = "2468" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsJsonAsync("/api/admin/unlock", new { pin = code })).StatusCode);
    }

    [Fact]
    public async Task The_code_unlocks_and_wrong_codes_get_blocked()
    {
        await Setup("2468");
        var wrong = await Client().PostAsJsonAsync("/api/admin/unlock", new { pin = "1111" });
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);

        var right = await Client().PostAsJsonAsync("/api/admin/unlock", new { pin = "2468" });
        var token = (string)(await right.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        Assert.Equal(HttpStatusCode.OK, (await Client(token).GetAsync("/api/admin/settings")).StatusCode);

        await Client(token).PostAsync("/api/admin/lock", null);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(token).GetAsync("/api/admin/settings")).StatusCode);

        for (var i = 0; i < 5; i++)
            await Client().PostAsJsonAsync("/api/admin/unlock", new { pin = "0000" });
        // Blocked now, even for the right code.
        Assert.Equal((HttpStatusCode)429, (await Client().PostAsJsonAsync("/api/admin/unlock", new { pin = "2468" })).StatusCode);
    }

    [Fact]
    public async Task Changing_the_code_needs_an_unlocked_device_and_signs_out_others()
    {
        var first = await Setup("2468");
        var other = (string)(await (await Client().PostAsJsonAsync("/api/admin/unlock", new { pin = "2468" })).Content.ReadFromJsonAsync<JsonObject>())!["token"]!;

        Assert.Equal(HttpStatusCode.Forbidden, (await Client().PutAsJsonAsync("/api/admin/pin", new { pin = "13579" })).StatusCode);
        var changed = await Client(first).PutAsJsonAsync("/api/admin/pin", new { pin = "13579" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var token = (string)(await changed.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;

        Assert.Equal(HttpStatusCode.Forbidden, (await Client(other).GetAsync("/api/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(token).GetAsync("/api/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().PostAsJsonAsync("/api/admin/unlock", new { pin = "2468" })).StatusCode);
    }

    [Fact]
    public async Task Settings_are_checked_then_saved_to_the_settings_file()
    {
        var admin = Client(await Setup());
        var view = await admin.GetFromJsonAsync<JsonObject>("/api/admin/settings");
        var key = view!["settings"]!.AsArray().First(s => (string?)s!["key"] == "ACCESS_KEY")!;
        Assert.Null((string?)key["value"]); // secrets never come back
        Assert.True((bool)key["set"]!);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/settings", new { PHOTOS_MAX_MB = "lots" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/settings", new { PHOTOS_ROOT = "" })).StatusCode); // photos are on
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/settings", new { DATA_DIR = "elsewhere" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/settings", new { Urls = "not a url" })).StatusCode);

        var saved = await admin.PutAsJsonAsync("/api/admin/settings", new Dictionary<string, string?>
        {
            ["PHOTOS_MAX_MB"] = "40",
            ["CONVERSION_URL"] = "http://127.0.0.1:8090/",
            ["ACCESS_KEY"] = null, // blank: keep
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var after = await saved.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Contains("PHOTOS_MAX_MB", after!["restartNeeded"]!.AsArray().Select(n => (string?)n));

        var file = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        Assert.Equal("40", (string?)file["PHOTOS_MAX_MB"]);
        Assert.Equal("http://127.0.0.1:8090", (string?)file["CONVERSION_URL"]);
        Assert.False(file.ContainsKey("ACCESS_KEY"));
        Assert.NotNull(file["Kestrel"]);
        Assert.True(File.Exists(settingsPath + ".bak"));
    }

    [Fact]
    public async Task Photo_folder_rules_are_tested_and_saved_from_the_page()
    {
        var admin = Client(await Setup());
        var root = Path.Combine(dataDir, "photos");
        var rules = new
        {
            @default = (string?)null,
            loadFolder = "Load {load}",
            rules = new[] { new { match = "^(?<year>\\d{2})\\d{3}$", folder = Path.Combine(root, "20{year}", "{job}") } },
        };

        var test = await admin.PostAsJsonAsync("/api/admin/photo-rules/test", new { rules.@default, rules.loadFolder, rules.rules, job = "26101", load = "3" });
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        var where = await test.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(Path.Combine(root, "2026", "26101", "Load 3"), (string?)where!["loadFolder"]);

        var bad = new { @default = (string?)null, loadFolder = "{load}", rules = new[] { new { match = "^\\d+$", folder = Path.Combine(root, "{year}") } } };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/photo-rules", bad)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/photo-rules", new { loadFolder = "{load}", rules = new[] { new { match = "([", folder = root } } })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/photo-rules", rules)).StatusCode);
        var jobs = await admin.GetFromJsonAsync<JsonObject>("/api/admin/photo-rules");
        Assert.Single(jobs!["rules"]!.AsArray());
        Assert.True(File.Exists(Path.Combine(dataDir, "photo-rules.json")));
    }

    [Fact]
    public async Task Stats_say_how_the_server_is_doing()
    {
        var admin = Client(await Setup());
        var stats = await admin.GetFromJsonAsync<JsonObject>("/api/admin/stats");
        Assert.NotNull(stats!["process"]!["uptimeSeconds"]);
        Assert.NotNull(stats["database"]!["data"]!["counts"]!["loads"]);
        Assert.True((long)stats["requests"]!["totalRequests"]! > 0);
        Assert.False((bool)stats["conversion"]!["ok"]!);

        var backup = await admin.GetAsync("/api/admin/backup");
        Assert.Equal(HttpStatusCode.OK, backup.StatusCode);
        var head = (await backup.Content.ReadAsByteArrayAsync())[..15];
        Assert.Equal("SQLite format 3", System.Text.Encoding.ASCII.GetString(head));

        // Not the Windows service here, so a restart is refused rather than just exiting.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/restart", null)).StatusCode);
    }

    [Fact]
    public void An_unlock_only_works_from_the_address_that_entered_the_code_and_runs_out()
    {
        var file = new SettingsFile(settingsPath, new ConfigurationBuilder().Build());
        var adminLock = new AdminLock(file, Microsoft.Extensions.Logging.Abstractions.NullLogger<AdminLock>.Instance);
        var (token, until) = adminLock.Setup("2468", "10.0.0.5");
        Assert.True(adminLock.Valid(token, "10.0.0.5"));
        Assert.False(adminLock.Valid(token, "10.0.0.6"));
        Assert.InRange(until - DateTime.UtcNow, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30)); // 30 min by default

        file.Write(new Dictionary<string, string?> { ["ADMIN_LOCK_MINUTES"] = "1" });
        var other = adminLock.Unlock("2468", "10.0.0.6")!.Value;
        Assert.InRange(other.Until - DateTime.UtcNow, TimeSpan.FromSeconds(50), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Updates_are_behind_the_code_and_only_run_on_windows()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().PostAsync("/api/admin/update/client", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().PostAsync("/api/admin/update/server", null)).StatusCode);
        var admin = Client(await Setup());
        var status = await admin.GetFromJsonAsync<JsonObject>("/api/admin/update");
        Assert.Equal(OperatingSystem.IsWindows(), (bool)status!["supported"]!);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/update/client", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/update/server", null)).StatusCode); // not the service here
    }

    [Fact]
    public async Task The_admin_page_installs_as_an_app()
    {
        var manifest = await factory.CreateClient().GetAsync("/admin/manifest.webmanifest");
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal("application/manifest+json", manifest.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync("/admin/sw.js")).StatusCode);
    }

    [Fact]
    public async Task The_powerfab_job_list_says_when_powerfab_cant_be_reached()
    {
        var jobs = await Client().GetFromJsonAsync<JsonObject>("/api/jobs/powerfab");
        Assert.False((bool)jobs!["ok"]!);
        Assert.Contains("CONVERSION_URL", (string?)jobs["error"]);
    }
}
