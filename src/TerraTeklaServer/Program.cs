using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using TerraTeklaServer;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // A Windows service starts in System32, so anchor everything to the install folder.
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});
builder.Host.UseWindowsService(o => o.ServiceName = "TerraTeklaServer");
// Same keys as the environment variables in docs/SETUP.md. The Windows service reads this file, and
// the admin page's Settings write to it (SETTINGS_FILE only moves it, for tests).
var settingsPath = builder.Configuration["SETTINGS_FILE"] is { Length: > 0 } custom
    ? Path.GetFullPath(custom, AppContext.BaseDirectory)
    : Path.Combine(AppContext.BaseDirectory, "local.settings.json");
builder.Configuration.AddJsonFile(settingsPath, optional: true, reloadOnChange: false);
var settings = Settings.From(builder.Configuration);
var serverStats = new ServerStats();
builder.Logging.AddProvider(new StatsLoggerProvider(serverStats));

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(serverStats);
builder.Services.AddSingleton(new SettingsFile(settingsPath, builder.Configuration));
builder.Services.AddSingleton<AdminLock>();
builder.Services.AddSingleton<Updater>();
builder.Services.AddSingleton(settings.Photos);
builder.Services.AddSingleton<PhotoStore>();
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<Hub>();
builder.Services.AddHttpClient("conversion", c => c.Timeout = TimeSpan.FromSeconds(90));
var uploadLimit = settings.Photos.MaxBytes * settings.Photos.MaxFiles + 1024 * 1024;
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = uploadLimit);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(settings.CorsOrigins).WithHeaders("Authorization", "Content-Type", "X-Terra-Device", "X-Terra-Version", AdminLock.Header).WithMethods("GET", "POST", "PUT", "DELETE")));

var app = builder.Build();
var store = app.Services.GetRequiredService<Store>();
var hub = app.Services.GetRequiredService<Hub>();
var photos = app.Services.GetRequiredService<PhotoStore>();
var settingsFile = app.Services.GetRequiredService<SettingsFile>();
var adminLock = app.Services.GetRequiredService<AdminLock>();
var updater = app.Services.GetRequiredService<Updater>();
var version = typeof(Store).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

if (settings.AccessKey == "")
    app.Logger.LogWarning("ACCESS_KEY is empty: anyone who can reach this server can use it. Set it in local.settings.json.");
if (settings.ConversionUrl == "")
    app.Logger.LogWarning("CONVERSION_URL is empty: PowerFab can't be reached. Set it to tekla-conversion-layer's address.");
if (settings.ClientRoot == "" || !File.Exists(Path.Combine(settings.ClientRoot, "index.html")))
    app.Logger.LogWarning("CLIENT_ROOT ({Root}) has no index.html: the iPad app isn't being served.", settings.ClientRoot);

hub.Changed += () => hub.Broadcast(new { type = "devices", devices = DeviceList() });
// Tell every iPad a new app is on the server, so it can offer to reload.
updater.ClientUpdated += v => hub.Broadcast(new { type = "client-updated", version = v });

app.UseRequestStats(serverStats);
app.UseCors();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

// ---- The iPad app (terra-tekla-client, from CLIENT_ROOT) and the admin page (/admin) -------------

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
    ContentTypeProvider = ClientContentTypes(),
});
if (settings.ClientRoot != "" && Directory.Exists(settings.ClientRoot))
{
    var client = new PhysicalFileProvider(settings.ClientRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = client });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = client,
        ServeUnknownFileTypes = false,
        // The app's service worker keeps its own copy for offline use; the browser must always ask
        // this server, so a `git pull` of the client shows up on the next launch.
        OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
        ContentTypeProvider = ClientContentTypes(),
    });
}
else
{
    app.MapGet("/", () => Results.Content("""
        <!doctype html><meta charset="utf-8"><title>TERRA server</title>
        <body style="background:#121212;color:#f0f0f0;font:18px system-ui;padding:2rem">
        <h1>TERRA server is running</h1>
        <p>The iPad app isn't set up here yet: set <code>CLIENT_ROOT</code> to a copy of terra-tekla-client and restart.</p>
        <p><a style="color:#ffb000" href="admin/">Open the admin page</a></p>
        """, "text/html"));
}

// No key needed: only says the server is up. The admin page's details come from /api/status.
app.MapGet("/health", () => new { status = "ok", version });

// ---- Live connection --------------------------------------------------------------------------

// Opens with {"type":"hello","key":"...","deviceId":"...","kind":"ipad"|"admin","version":"0.13.0"}.
app.Map("/ws", async (HttpContext http) =>
{
    if (!http.WebSockets.IsWebSocketRequest)
        return Results.BadRequest("WebSocket only.");
    var socket = await http.WebSockets.AcceptWebSocketAsync();
    var hello = await ReadHelloAsync(socket, http.RequestAborted);
    if (hello is null || !KeyOk(LoadOps.Str(hello, "key")))
    {
        await socket.CloseAsync((WebSocketCloseStatus)4401, "The access key was refused.", CancellationToken.None);
        return Results.Empty;
    }
    var kind = LoadOps.Str(hello, "kind") == "admin" ? "admin" : "ipad";
    var deviceId = kind == "ipad" ? CleanDeviceId(LoadOps.Str(hello, "deviceId")) : null;
    Device? device = null;
    if (deviceId is not null)
        device = store.Touch(deviceId, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent, LoadOps.Str(hello, "version")).Device;
    await hub.RunAsync(socket, deviceId, kind, new { type = "welcome", device, devices = DeviceList(), serverVersion = version, photosEnabled = settings.Photos.Enabled }, http.RequestAborted);
    return Results.Empty;
});

// ---- API (every route needs the access key) ---------------------------------------------------

var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
{
    var header = context.HttpContext.Request.Headers.Authorization.ToString();
    var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
    return KeyOk(presented) ? await next(context) : Results.Problem("The access key was refused.", statusCode: 401);
});

// PowerFab, through tekla-conversion-layer. The app sends exactly what it used to send to the
// conversion layer; this server adds the conversion layer's key, which never leaves the server.
api.MapPost("/powerfab/{command}", async (string command, HttpContext http, IHttpClientFactory factory) =>
{
    if (settings.ConversionUrl == "")
        return Results.Problem("This server has no CONVERSION_URL, so PowerFab can't be reached.", statusCode: 502);
    if (!System.Text.RegularExpressions.Regex.IsMatch(command, "^[A-Za-z0-9_]{1,80}$"))
        return Results.Problem("That isn't a PowerFab command name.", statusCode: 400);
    Me(http);
    using var body = new StreamReader(http.Request.Body);
    var json = await body.ReadToEndAsync(http.RequestAborted);
    var reply = await PowerFabAsync(factory, command, json, http.RequestAborted);
    return reply.Error is { } error
        ? Results.Problem(error, statusCode: 502)
        : Results.Content(reply.Body, reply.ContentType ?? "application/json", Encoding.UTF8, reply.Status);
});

// The admin page's Jobs: every job PowerFab lets this server's API user reach (the same list the iPads
// pick from), with what this server has for each and where its photos go.
api.MapGet("/jobs/powerfab", async (IHttpClientFactory factory, CancellationToken ct) =>
{
    var mine = store.Jobs().ToDictionary(j => j.Job, StringComparer.OrdinalIgnoreCase);
    List<(string Number, string Description, string Location)> found = [];
    string? error = null;
    if (settings.ConversionUrl == "")
        error = "This server has no CONVERSION_URL, so PowerFab can't be reached.";
    else
    {
        var reply = await PowerFabAsync(factory, "ProductionControlJob_Get", "{}", ct);
        error = reply.Error ?? (reply.Status >= 400 ? $"PowerFab answered {reply.Status}: {Trim(reply.Body, 300)}" : null);
        if (error is null)
        {
            try
            {
                found = PowerFabJobs(JsonNode.Parse(reply.Body));
            }
            catch (JsonException ex)
            {
                error = $"PowerFab's job list couldn't be read: {ex.Message}";
            }
        }
    }
    var rows = found.Select(j => (j.Number, j.Description, j.Location, InPowerFab: true))
        .Concat(mine.Keys.Where(k => !found.Any(f => f.Number.Equals(k, StringComparison.OrdinalIgnoreCase))).Select(k => (Number: k, Description: "", Location: "", InPowerFab: false)))
        .OrderByDescending(j => j.Number, NumericOrder.Instance)
        .Select(j =>
        {
            mine.TryGetValue(j.Number, out var m);
            string? folder = null, rule = null, folderError = null;
            if (settings.Photos.Enabled)
            {
                try
                {
                    (folder, rule) = photos.JobFolder(j.Number);
                }
                catch (Exception ex) when (ex is BadNameException or PhotoRulesException)
                {
                    folderError = ex.Message;
                }
            }
            return new
            {
                job = j.Number, description = j.Description, location = j.Location, inPowerFab = j.InPowerFab,
                loads = m.Loads, pieces = m.Pieces, lastAt = m.LastAt, photoFolder = folder, photoRule = rule, photoError = folderError,
            };
        });
    return Results.Json(new { ok = error is null, error, fetchedAt = DateTime.UtcNow, photosEnabled = settings.Photos.Enabled, jobs = rows });
});

// This device: who it is (made the first time it is seen) and renaming it.
api.MapGet("/me", (HttpContext http) => Results.Json(Me(http, required: true)));
api.MapPut("/me", (HttpContext http, DeviceUpdate update) =>
{
    var me = Me(http, required: true)!;
    return DeviceChange(me.Id, update, me);
});

// A quick "is everything up" for the admin page's front page. The details (folders, database, load,
// errors) are in /api/admin/stats, behind the admin code.
api.MapGet("/status", async (IHttpClientFactory http) => new
{
    version,
    accessKeySet = settings.AccessKey != "",
    client = new { served = File.Exists(Path.Combine(settings.ClientRoot, "index.html")), version = ClientVersion() },
    photosEnabled = settings.Photos.Enabled,
    conversion = await ConversionHealth(http, settings.ConversionUrl),
    adminPinSet = adminLock.PinSet,
});

api.MapGet("/devices", () => DeviceList());
api.MapPut("/devices/{id}", (string id, DeviceUpdate update, HttpContext http) =>
    DeviceChange(id, update, Me(http) ?? AdminDevice));

api.MapGet("/jobs", () => store.Jobs().Select(j => new { job = j.Job, loads = j.Loads, pieces = j.Pieces, lastAt = j.LastAt }));
api.MapGet("/jobs/{job}/loads", async (string job) => await store.ForJobAsync(job));
api.MapGet("/loads/{loadId}", async (string loadId, string? job, string? number) =>
    await store.GetAsync(loadId, job ?? "", number ?? ""));

// One change to a load's stacks. Body: {"op": "...", "opId": "...", "job": "...", "loadNumber": "...", ...}
// (the operations are listed in docs/SETUP.md). Answers the load as it now is, and sends it to every
// other connected iPad.
api.MapPost("/loads/{loadId}/ops", async (string loadId, JsonObject op, HttpContext http) =>
{
    var me = Me(http, required: true)!;
    try
    {
        var (state, changed, activity) = await store.ApplyAsync(loadId, LoadOps.Str(op, "job"), LoadOps.Str(op, "loadNumber"), op, me);
        if (changed)
            hub.Broadcast(new { type = "load", state, by = me.Id, op = LoadOps.Str(op, "op") });
        if (activity.Count > 0)
            hub.Broadcast(new { type = "activity", items = activity });
        return Results.Json(new { state, changed }, Json.Options);
    }
    catch (OpException ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
});

api.MapGet("/activity", (string? device, string? job, string? load, string? mark, long? before, int? limit) =>
    store.History(device, job, load, mark, before, limit ?? 100));

// Admin page "Docs": the setup guide and the photo guide that shipped with this build.
api.MapGet("/docs/{name}", (string name) => Doc(name) is { } text
    ? Results.Text(text, "text/markdown; charset=utf-8")
    : Results.NotFound());

// ---- Admin code, settings and server stats (docs/SETUP.md, "Admin page") --------------------
// Everything under /api/admin needs the access key like the rest of /api, and all but the routes
// that check the code itself also need X-Terra-Admin: a token from entering the admin code, used
// from the same IP address (the iPad app and the admin page share it).

var adminOpen = api.MapGroup("/admin");
adminOpen.MapGet("/lock", (HttpContext http) => new
{
    pinSet = adminLock.PinSet,
    unlocked = adminLock.Valid(AdminToken(http), AdminLock.ClientIp(http)),
    until = adminLock.Until(AdminToken(http), AdminLock.ClientIp(http)),
    lockMinutes = (int)adminLock.Duration.TotalMinutes,
    retryAfter = adminLock.RetryAfter,
});
// The first code, while none is set. After that it can only be changed with the current one.
adminOpen.MapPost("/setup", (PinRequest body, HttpContext http) =>
{
    try
    {
        var (token, until) = adminLock.Setup(body.Pin ?? "", AdminLock.ClientIp(http));
        store.Log(Me(http) ?? AdminDevice, "admin", "Set the admin code");
        return Results.Json(new { token, until });
    }
    catch (BadPinException ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: 409);
    }
});
adminOpen.MapPost("/unlock", (PinRequest body, HttpContext http) =>
{
    if (!adminLock.PinSet)
        return Results.Problem("No admin code is set yet.", statusCode: 409);
    if (adminLock.RetryAfter is > 0 and var wait)
        return TooManyTries(wait);
    if (adminLock.Unlock(body.Pin ?? "", AdminLock.ClientIp(http)) is { } session)
        return Results.Json(new { token = session.Token, until = session.Until });
    return adminLock.RetryAfter is > 0 and var blocked ? TooManyTries(blocked) : Results.Problem("That code is wrong.", statusCode: 403, extensions: new Dictionary<string, object?> { ["wrongPin"] = true });
});
adminOpen.MapPost("/lock", (HttpContext http) =>
{
    adminLock.Lock(AdminToken(http));
    return Results.NoContent();
});

var admin = api.MapGroup("/admin").AddEndpointFilter(async (context, next) =>
    adminLock.Valid(AdminToken(context.HttpContext), AdminLock.ClientIp(context.HttpContext))
        ? await next(context)
        : Results.Problem(adminLock.PinSet ? "Locked: enter the admin code." : "Set an admin code first.", statusCode: 403,
            extensions: new Dictionary<string, object?> { ["locked"] = true, ["pinSet"] = adminLock.PinSet }));

admin.MapPut("/pin", (PinChange body, HttpContext http) =>
{
    if (adminLock.RetryAfter is > 0 and var wait)
        return TooManyTries(wait);
    try
    {
        if (adminLock.Change(body.Current ?? "", body.Pin ?? "", AdminLock.ClientIp(http)) is not { } session)
            return Results.Problem("The current code is wrong.", statusCode: 403, extensions: new Dictionary<string, object?> { ["wrongPin"] = true });
        store.Log(Me(http) ?? AdminDevice, "admin", "Changed the admin code");
        return Results.Json(new { token = session.Token, until = session.Until });
    }
    catch (BadPinException ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
});

admin.MapGet("/settings", () => SettingsView());
admin.MapPut("/settings", (Dictionary<string, string?> changes) =>
{
    try
    {
        var clean = settingsFile.Validate(changes);
        var changed = clean.Where(c => settingsFile.Current(c.Key) != c.Value).ToDictionary(c => c.Key, c => c.Value);
        if (changed.Count > 0)
        {
            settingsFile.Write(changed);
            var labels = changed.Keys.Select(k => SettingsFile.Editable.First(s => s.Key == k).Label);
            var item = store.Log(AdminDevice, "settings", $"Changed settings: {string.Join(", ", labels)}");
            hub.Broadcast(new { type = "activity", items = new[] { item } });
            app.Logger.LogWarning("Settings changed from the admin page: {Keys}", string.Join(", ", changed.Keys));
        }
        return Results.Json(SettingsView());
    }
    catch (BadSettingException ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
});

// Whether an address answers like tekla-conversion-layer, before it is saved.
admin.MapPost("/test-conversion", async (UrlRequest body, IHttpClientFactory factory) =>
{
    var url = (body.Url ?? "").Trim().TrimEnd('/');
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        return Results.Problem("That isn't an http:// or https:// address.", statusCode: 400);
    return Results.Json(await ConversionHealth(factory, url));
});

admin.MapGet("/photo-rules", () => RulesView());
admin.MapPut("/photo-rules", (RulesRequest body) =>
{
    try
    {
        var saved = photos.SaveRules(body.Default, body.LoadFolder, (body.Rules ?? []).Select(r => (r.Match ?? "", r.Folder ?? "")).ToList());
        var item = store.Log(AdminDevice, "settings", $"Changed the photo folder rules ({saved.Rules.Count} rule{(saved.Rules.Count == 1 ? "" : "s")})");
        hub.Broadcast(new { type = "activity", items = new[] { item } });
        return Results.Json(RulesView());
    }
    catch (PhotoRulesException ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
});
// Where a job's (and load's) photos would go under the rules on screen, saved or not.
admin.MapPost("/photo-rules/test", (RulesTest body) =>
{
    try
    {
        var rules = body.Rules is null ? photos.Rules() : PhotoStore.FileRules.Parse(JsonSerializer.Serialize(new
        {
            @default = string.IsNullOrWhiteSpace(body.Default) ? null : body.Default,
            loadFolder = body.LoadFolder,
            rules = body.Rules.Select(r => new { match = r.Match, folder = r.Folder }),
        }), "The rules");
        rules.Validate();
        var (folder, rule) = photos.JobFolder(body.Job ?? "", rules);
        var load = string.IsNullOrWhiteSpace(body.Load) ? "8" : body.Load.Trim();
        return Results.Json(new { job = body.Job, folder, rule, load, loadFolder = photos.LoadFolder(body.Job ?? "", load, rules) });
    }
    catch (Exception ex) when (ex is BadNameException or PhotoRulesException)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
});

admin.MapGet("/stats", async (IHttpClientFactory factory) =>
{
    var db = new FileInfo(settings.DatabasePath);
    var wal = new FileInfo(settings.DatabasePath + "-wal");
    string? rulesError = null;
    var ruleCount = 0;
    try
    {
        ruleCount = photos.Rules().Rules.Count;
    }
    catch (PhotoRulesException ex)
    {
        rulesError = ex.Message;
    }
    var online = hub.Online();
    return Results.Json(new
    {
        version,
        process = serverStats.Process(),
        requests = serverStats.Snapshot(),
        connections = new { ipads = online.Count, admins = hub.AdminCount, devices = store.Devices().Count },
        database = new
        {
            path = settings.DatabasePath,
            bytes = db.Exists ? db.Length : 0,
            walBytes = wal.Exists ? wal.Length : 0,
            data = store.Stats(),
        },
        disks = new[] { ("Database", (string?)settings.DataDir), ("Photos", settings.Photos.Enabled ? settings.Photos.Root : null) }
            .Select(d => new { name = d.Item1, disk = ServerStats.Disk(d.Item2) })
            .Where(d => d.disk is not null),
        conversion = await ConversionHealth(factory, settings.ConversionUrl),
        client = new { root = settings.ClientRoot, served = File.Exists(Path.Combine(settings.ClientRoot, "index.html")), version = ClientVersion() },
        photos = new { enabled = settings.Photos.Enabled, root = settings.Photos.Root, rulesPath = settings.Photos.RulesPath, rules = ruleCount, rulesError },
        accessKeySet = settings.AccessKey != "",
        restartNeeded = RestartNeeded().Count > 0,
    }, Json.Options);
});

// Updates: the iPad app (update-client.ps1) and the server itself (deploy.ps1). See Updater.cs.
admin.MapGet("/update", () => Results.Json(updater.Status(ClientVersion), Json.Options));
admin.MapPost("/update/client", (HttpContext http) =>
{
    try
    {
        updater.UpdateClient(ClientVersion);
        var item = store.Log(Me(http) ?? AdminDevice, "admin", "Started an iPad app update on the server");
        hub.Broadcast(new { type = "activity", items = new[] { item } });
        return Results.Json(updater.Status(ClientVersion), Json.Options, statusCode: 202);
    }
    catch (UpdateException ex)
    {
        return Results.Problem(ex.Message, statusCode: 409);
    }
});
admin.MapPost("/update/server", (HttpContext http) =>
{
    try
    {
        updater.UpdateServer();
        var item = store.Log(Me(http) ?? AdminDevice, "admin", "Started a server update (deploy.ps1)");
        hub.Broadcast(new { type = "activity", items = new[] { item } });
        return Results.Json(new { started = true }, statusCode: 202);
    }
    catch (UpdateException ex)
    {
        return Results.Problem(ex.Message, statusCode: 409);
    }
});

// A copy of the database to keep somewhere else.
admin.MapGet("/backup", () =>
{
    var temp = Path.Combine(Path.GetTempPath(), $"terra-backup-{Guid.NewGuid():N}.db");
    store.Backup(temp);
    app.Logger.LogInformation("Database backup downloaded from the admin page");
    var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.DeleteOnClose);
    return Results.File(stream, "application/vnd.sqlite3", $"terra-backup-{DateTime.Now:yyyyMMdd-HHmm}.db");
});

// Restarts the service so changed settings take effect. The process exits with an error code and
// Windows starts it again (the service's "restart after a crash", set by install-service.ps1).
admin.MapPost("/restart", (HttpContext http) =>
{
    if (!Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
        return Results.Problem("This server isn't running as the Windows service, so nothing would start it again. Restart it by hand.", statusCode: 409);
    var item = store.Log(Me(http) ?? AdminDevice, "admin", "Restarted the server");
    hub.Broadcast(new { type = "activity", items = new[] { item } });
    app.Logger.LogWarning("Restarting: asked for on the admin page.");
    _ = Task.Run(async () =>
    {
        await Task.Delay(1500);
        Environment.Exit(1);
    });
    return Results.Json(new { restarting = true }, statusCode: 202);
});

// ---- Photos (docs/PHOTOS.md) -----------------------------------------------------------------

api.MapGet("/photos", () => new
{
    enabled = settings.Photos.Enabled,
    maxBytes = settings.Photos.MaxBytes,
    maxFiles = settings.Photos.MaxFiles,
    types = settings.Photos.Types,
});

api.MapGet("/photos/recent", (string? device, int? limit) => settings.Photos.Enabled ? Results.Json(store.RecentPhotos(device, limit ?? 60)) : PhotosDisabled());

api.MapGet("/photos/{job}/{load}", (string job, string load) => PhotosAsync(() =>
{
    var meta = store.Photos(job, load);
    var devices = store.Devices().ToDictionary(d => d.Id);
    var files = photos.List(job, load).Select(f =>
    {
        meta.TryGetValue(f.Name, out var m);
        var device = m?.DeviceId is { } id && devices.TryGetValue(id, out var d) ? d : null;
        return new
        {
            name = f.Name,
            bytes = f.Bytes,
            modified = f.Modified,
            mark = m?.Mark,
            by = device?.Name ?? m?.DeviceName,
            byId = m?.DeviceId,
            color = device?.Color,
        };
    });
    return Task.FromResult(Results.Json(new { job, load, files }));
}));

api.MapGet("/photos/{job}/{load}/{name}", (string job, string load, string name, HttpResponse response) => PhotosAsync(() =>
{
    var file = photos.Find(job, load, name);
    // A saved file is only ever treated as the type its name says, and never kept by a shared cache.
    response.Headers.XContentTypeOptions = "nosniff";
    response.Headers.CacheControl = "private, max-age=3600";
    return Task.FromResult(Results.File(file.FullName, PhotoStore.ContentType(file.Name), enableRangeProcessing: true));
}));

// Deletes a photo: it is moved to the load's .deleted folder, not erased.
api.MapDelete("/photos/{job}/{load}/{name}", (string job, string load, string name, HttpContext http) => PhotosAsync(() =>
{
    var me = Me(http) ?? AdminDevice;
    var movedTo = photos.Delete(job, load, name);
    store.PhotoDeleted(job, load, name);
    var item = store.Log(me, "photo-delete", $"Deleted photo {name}", job, null, load);
    hub.Broadcast(new { type = "activity", items = new[] { item } });
    hub.Broadcast(new { type = "photos", job, load });
    return Task.FromResult(Results.Json(new { job, load, deleted = name, movedTo }));
}));

api.MapPost("/photos/{job}/{load}", async (string job, string load, HttpContext http, CancellationToken ct) =>
{
    if (!settings.Photos.Enabled)
        return PhotosDisabled();
    var me = Me(http, required: true)!;
    var request = http.Request;
    if (!request.HasFormContentType)
        return Results.Problem("Send the photos as multipart/form-data, each in a field named \"file\".", statusCode: 400);
    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync(ct);
    }
    catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
    {
        return Results.Problem($"The upload is too big or broken: {ex.Message}", statusCode: 413);
    }

    var uploads = form.Files.GetFiles("file");
    if (uploads.Count == 0)
        return Results.Problem("No photos. Send each one in a multipart field named \"file\".", statusCode: 400);
    if (uploads.Count > settings.Photos.MaxFiles)
        return Results.Problem($"{uploads.Count} photos in one upload; the limit is {settings.Photos.MaxFiles} (PHOTOS_MAX_PER_UPLOAD).", statusCode: 413);
    var mark = form["mark"].ToString().Trim();

    return await PhotosAsync(async () =>
    {
        // Every file is checked (and the folder worked out) before any is saved.
        photos.LoadFolder(job, load);
        if (mark != "")
            PhotoStore.CleanName(mark, "Mark");
        var types = new List<string>();
        foreach (var upload in uploads)
        {
            var head = new byte[16];
            await using (var s = upload.OpenReadStream())
                head = head[..await s.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct)];
            types.Add(photos.CheckFile(upload.Length, head, upload.FileName is { Length: > 0 } n ? $"\"{n}\"" : "A photo"));
        }

        var saved = new List<object>();
        for (var i = 0; i < uploads.Count; i++)
        {
            await using var s = uploads[i].OpenReadStream();
            var name = await photos.SaveAsync(job, load, mark, me.Name, types[i], s, ct);
            store.AddPhoto(job, load, name, mark == "" ? null : mark, me, uploads[i].Length);
            saved.Add(new { name, bytes = uploads[i].Length });
        }
        var item = store.Log(me, "photo", $"Took {saved.Count} photo{(saved.Count == 1 ? "" : "s")} of {(mark == "" ? "the whole load" : mark)}", job, null, load, mark == "" ? null : mark);
        hub.Broadcast(new { type = "activity", items = new[] { item } });
        hub.Broadcast(new { type = "photos", job, load });
        return Results.Json(new { job, load, saved }, statusCode: 201);
    });
}).WithMetadata(new RequestSizeLimitAttribute(uploadLimit)).DisableAntiforgery();

app.Run();

// ---- Helpers ------------------------------------------------------------------------------------

bool KeyOk(string presented) => settings.AccessKey == "" || CryptographicOperations.FixedTimeEquals(
    SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
    SHA256.HashData(Encoding.UTF8.GetBytes(settings.AccessKey)));

// The device making a request, from its X-Terra-Device header (made up by the app once and kept on
// the iPad). Seeing it also records when it was last seen.
Device? Me(HttpContext http, bool required = false)
{
    var id = CleanDeviceId(http.Request.Headers["X-Terra-Device"].ToString());
    if (id is null)
        return required ? throw new BadHttpRequestException("This needs the device's X-Terra-Device header.", 400) : null;
    var (device, created) = store.Touch(id, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent, http.Request.Headers["X-Terra-Version"]);
    if (created)
        hub.Broadcast(new { type = "devices", devices = DeviceList() });
    return device;
}

static string? CleanDeviceId(string? id) =>
    id is { Length: >= 8 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? id : null;

object[] DeviceList()
{
    var online = hub.Online();
    return store.Devices().Select(d => (object)new
    {
        d.Id, d.Name, d.Color, d.CreatedAt, d.LastSeenAt, d.LastIp, d.UserAgent, d.AppVersion,
        online = online.ContainsKey(d.Id),
        onlineSince = online.TryGetValue(d.Id, out var since) ? since : (DateTime?)null,
    }).ToArray();
}

IResult DeviceChange(string id, DeviceUpdate update, Device by)
{
    try
    {
        var device = store.UpdateDevice(id, update.Name, update.Color, by);
        hub.Broadcast(new { type = "devices", devices = DeviceList() });
        return Results.Json(device);
    }
    catch (OpException ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.Problem(ex.Message, statusCode: 404);
    }
}

static async Task<JsonObject?> ReadHelloAsync(WebSocket socket, CancellationToken ct)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(10));
    var buffer = new byte[2048];
    try
    {
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        if (result.MessageType != WebSocketMessageType.Text || !result.EndOfMessage)
            return null;
        return JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)) as JsonObject is { } hello
            && LoadOps.Str(hello, "type") == "hello" ? hello : null;
    }
    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or JsonException)
    {
        return null;
    }
}

async Task<object> ConversionHealth(IHttpClientFactory factory, string url)
{
    if (url == "")
        return new { ok = false, error = "CONVERSION_URL is not set", ms = 0L };
    var watch = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var client = factory.CreateClient("conversion");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var health = await client.GetFromJsonAsync<JsonObject>($"{url}/health", cts.Token);
        return new { ok = true, health, ms = watch.ElapsedMilliseconds };
    }
    catch (Exception ex)
    {
        return new { ok = false, error = ex.Message, ms = watch.ElapsedMilliseconds };
    }
}

// One PowerFab command through tekla-conversion-layer, with this server's CONVERSION_KEY. Error is set
// when the bridge couldn't be reached or refused the key (both 502 for the caller).
async Task<(int Status, string Body, string? ContentType, string? Error)> PowerFabAsync(IHttpClientFactory factory, string command, string json, CancellationToken ct)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.ConversionUrl}/api/powerfab/{command}")
    {
        Content = new StringContent(json == "" ? "{}" : json, Encoding.UTF8, "application/json"),
    };
    request.Headers.Authorization = new("Bearer", settings.ConversionKey);
    try
    {
        using var reply = await factory.CreateClient("conversion").SendAsync(request, ct);
        var text = await reply.Content.ReadAsStringAsync(ct);
        // The conversion layer's own 401 means CONVERSION_KEY is wrong: not the iPad's fault.
        if (reply.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return (502, "", null, "The PowerFab bridge refused this server's key (CONVERSION_KEY).");
        return ((int)reply.StatusCode, text, reply.Content.Headers.ContentType?.ToString(), null);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
    {
        return (502, "", null, $"Can’t reach the PowerFab bridge (tekla-conversion-layer): {ex.Message}");
    }
}

// Every ProductionControlJob in a PowerFab reply, wherever it sits (one object or a list), as the app's
// js/api.js parseJobs reads them.
static List<(string Number, string Description, string Location)> PowerFabJobs(JsonNode? reply)
{
    var jobs = new List<(string, string, string)>();
    void Walk(JsonNode? node)
    {
        if (node is JsonArray array)
            foreach (var n in array)
                Walk(n);
        else if (node is JsonObject obj)
            foreach (var (key, value) in obj)
            {
                if (key == "ProductionControlJob")
                    foreach (var job in value is JsonArray list ? list.ToArray() : [value])
                        if (job is JsonObject j && Text(j["JobNumber"]) is { Length: > 0 } number)
                            jobs.Add((number, Text(j["JobDescription"]), Text(j["JobLocation"])));
                Walk(value);
            }
    }
    Walk(reply);
    return jobs.DistinctBy(j => j.Item1, StringComparer.OrdinalIgnoreCase).ToList();
}

static string Text(JsonNode? node) => (node switch
{
    JsonValue v => v.ToString(),
    JsonObject o when o["#text"] is JsonValue t => t.ToString(),
    _ => "",
}).Trim();

static string Trim(string text, int max) => text.Length > max ? text[..max] + "…" : text;

static string? AdminToken(HttpContext http) => http.Request.Headers[AdminLock.Header].ToString() is { Length: > 0 } t ? t : null;

static IResult TooManyTries(int seconds) => Results.Problem($"Too many wrong codes. Try again in {seconds} s.", statusCode: 429,
    extensions: new Dictionary<string, object?> { ["retryAfter"] = seconds });

// Settings whose saved value differs from what the server is running with.
List<string> RestartNeeded() => SettingsFile.Editable
    .Where(s => s.Restart && (settingsFile.Current(s.Key) ?? "") != (settingsFile.Running(s.Key) ?? ""))
    .Select(s => s.Key).ToList();

object SettingsView()
{
    var pending = RestartNeeded();
    return new
    {
        file = settingsFile.Path,
        dataDir = settings.DataDir,
        canRestart = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService(),
        restartNeeded = pending,
        lockMinutes = (int)adminLock.Duration.TotalMinutes,
        photoTypes = PhotoSettings.AllTypes,
        settings = SettingsFile.Editable.Select(s =>
        {
            var value = settingsFile.Current(s.Key);
            var secret = s.Kind == "secret";
            return new
            {
                key = s.Key, group = s.Group, label = s.Label, kind = s.Kind, restart = s.Restart, help = s.Help, @default = s.Default,
                value = secret ? null : value ?? s.Default ?? "",
                set = !string.IsNullOrEmpty(value),
                pending = pending.Contains(s.Key),
            };
        }),
    };
}

object RulesView()
{
    var path = settings.Photos.RulesPath;
    try
    {
        var rules = photos.Rules();
        return new
        {
            path, exists = File.Exists(path), error = (string?)null, photosEnabled = settings.Photos.Enabled, root = settings.Photos.Root,
            @default = rules.Default, loadFolder = rules.LoadFolder,
            rules = rules.Rules.Select(r => new { match = r.Match, folder = r.Folder }),
        };
    }
    catch (PhotoRulesException ex)
    {
        return new
        {
            path, exists = File.Exists(path), error = (string?)ex.Message, photosEnabled = settings.Photos.Enabled, root = settings.Photos.Root,
            @default = (string?)null, loadFolder = "{load}", rules = Array.Empty<object>().Select(_ => new { match = "", folder = "" }),
        };
    }
}

string? ClientVersion()
{
    try
    {
        var text = File.ReadAllText(Path.Combine(settings.ClientRoot, "version.js"));
        return System.Text.RegularExpressions.Regex.Match(text, @"TERRA_VERSION\s*=\s*['""]([^'""]+)").Groups[1].Value is { Length: > 0 } v ? v : null;
    }
    catch (Exception)
    {
        return null;
    }
}

static string? Doc(string name)
{
    var resource = name.ToLowerInvariant() switch
    {
        "setup" => "docs.SETUP.md",
        "photos" => "docs.PHOTOS.md",
        _ => null,
    };
    if (resource is null)
        return null;
    using var stream = typeof(Store).Assembly.GetManifestResourceStream(resource);
    return stream is null ? null : new StreamReader(stream).ReadToEnd();
}

async Task<IResult> PhotosAsync(Func<Task<IResult>> action)
{
    if (!settings.Photos.Enabled)
        return PhotosDisabled();
    try
    {
        return await action();
    }
    catch (BadNameException ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
    catch (BadPhotoException ex)
    {
        return Results.Problem(ex.Message, statusCode: ex.StatusCode);
    }
    catch (FileNotFoundException ex)
    {
        return Results.Problem(ex.Message, statusCode: 404);
    }
    catch (PhotoRulesException ex)
    {
        return Results.Problem($"The server's photo settings are wrong: {ex.Message}", statusCode: 500);
    }
}

static IResult PhotosDisabled() =>
    Results.Problem("This server doesn't take photos (PHOTOS_ENABLED is off).", statusCode: 403);

static Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider ClientContentTypes()
{
    var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    types.Mappings[".webmanifest"] = "application/manifest+json";
    types.Mappings[".mjs"] = "text/javascript";
    return types;
}

public partial class Program
{
    /// <summary>The admin page's changes are made as this pseudo device (it has no X-Terra-Device).</summary>
    static readonly Device AdminDevice = new("admin", "Admin page", "#9a9a9a", "", "", null, null, null);
}

public sealed record DeviceUpdate(string? Name, string? Color);
public sealed record PinRequest(string? Pin);
public sealed record PinChange(string? Current, string? Pin);
public sealed record UrlRequest(string? Url);
public sealed record RuleRequest(string? Match, string? Folder);
public sealed record RulesRequest(string? Default, string? LoadFolder, List<RuleRequest>? Rules);
public sealed record RulesTest(string? Default, string? LoadFolder, List<RuleRequest>? Rules, string? Job, string? Load);
