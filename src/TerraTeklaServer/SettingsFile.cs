using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraTeklaServer;

/// <summary>A settings change that would stop the server from starting, or isn't a setting.</summary>
public sealed class BadSettingException(string message) : Exception(message);

/// <summary>One setting the admin page can change. See docs/SETUP.md for each key.</summary>
/// <param name="Kind">text, secret, bool, number, path, url, list or types (how the page edits it).</param>
/// <param name="Restart">Whether it takes effect only after the server restarts.</param>
public sealed record SettingInfo(string Key, string Group, string Label, string Kind, bool Restart, string Help, string? Default = null);

/// <summary>
/// local.settings.json next to the exe: the file the server reads its settings from at startup.
/// The admin page's Settings change it here; other keys in it (Kestrel, Logging...) are kept as they
/// are. Each save first copies the old file to local.settings.json.bak.
/// </summary>
public sealed class SettingsFile(string path, IConfiguration running)
{
    public static readonly SettingInfo[] Editable =
    [
        new("CONVERSION_URL", "PowerFab bridge", "Conversion layer address", "url", true,
            "tekla-conversion-layer's address as this server reaches it, e.g. http://127.0.0.1:8090."),
        new("CONVERSION_KEY", "PowerFab bridge", "Conversion layer key", "secret", true,
            "The conversion layer's API_TOKEN. Only this server holds it; the iPads never see it."),
        new("ACCESS_KEY", "iPads & access", "Access key", "secret", true,
            "The key every iPad and this page must give. Changing it signs every iPad out until it is entered there too."),
        new("CLIENT_ROOT", "iPads & access", "iPad app folder", "path", true,
            "A git clone of terra-tekla-client (the folder with index.html)."),
        new("PHOTOS_ENABLED", "Photos", "Take photos from the iPads", "bool", true, "Off: the photo routes answer 403.", "false"),
        new("PHOTOS_ROOT", "Photos", "Photo base folder", "path", true,
            "Full path. Jobs go in <this>\\{job} unless a folder rule says otherwise. Required when photos are on."),
        new("PHOTOS_MAX_MB", "Photos", "Largest photo (MB)", "number", true, "Largest single photo.", "25"),
        new("PHOTOS_MAX_PER_UPLOAD", "Photos", "Most photos per upload", "number", true, "Most photos in one upload.", "20"),
        new("PHOTOS_TYPES", "Photos", "File types taken", "types", true, "Read from each file's first bytes, not its name.", "jpg,png,heic,webp,pdf"),
        new("PHOTOS_RULES", "Photos", "Folder rules file", "path", true, "The rules file. A relative path is next to the exe.", "photo-rules.json"),
        new("Urls", "Network", "Listen address", "text", true,
            "Where the server listens, e.g. http://0.0.0.0:8095. A wrong value stops the server from starting.", "http://localhost:5000"),
        new("CORS_ORIGINS", "Network", "Other web addresses allowed", "list", true,
            "Comma separated. Only needed while working on the app from another address."),
        new("ADMIN_LOCK_MINUTES", "Admin", "Stay unlocked for (minutes)", "number", false,
            "After the admin code is entered, that device (from that IP address) stays unlocked this long.", "30"),
        new("SERVER_REPO", "Updates", "Server git clone", "path", false,
            "The terra-tekla-server git clone deploy.ps1 runs from, e.g. C:\\src\\terra-tekla-server. Needed for Update server."),
    ];

    static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    readonly object gate = new();

    public string Path => path;

    /// <summary>The file's top-level values as text (objects such as Kestrel are left out).</summary>
    public Dictionary<string, string> Read()
    {
        lock (gate)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, node) in Load())
                if (node is JsonValue v)
                    values[key] = v.ToString();
            return values;
        }
    }

    /// <summary>A setting as the file has it now, or else as the server started with it.</summary>
    public string? Current(string key) => Read().TryGetValue(key, out var v) ? v : running[key];

    /// <summary>A setting as the running server started with it.</summary>
    public string? Running(string key) => running[key];

    /// <summary>Sets keys in the file (null removes one) and saves it.</summary>
    public void Write(IReadOnlyDictionary<string, string?> changes)
    {
        lock (gate)
        {
            var json = Load();
            foreach (var (key, value) in changes)
            {
                var existing = json.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
                if (value is null)
                    json.Remove(existing);
                else
                    json[existing] = value;
            }
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
            Directory.CreateDirectory(dir);
            if (File.Exists(path))
                File.Copy(path, path + ".bak", overwrite: true);
            var temp = System.IO.Path.Combine(dir, $".settings-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>
    /// Checks a set of changes from the admin page and returns them cleaned. Throws
    /// <see cref="BadSettingException"/> for anything that isn't an editable setting or would stop
    /// the server from starting (the same checks it runs at startup).
    /// </summary>
    public Dictionary<string, string?> Validate(IReadOnlyDictionary<string, string?> changes)
    {
        var clean = new Dictionary<string, string?>();
        foreach (var (key, raw) in changes)
        {
            var info = Editable.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                ?? throw new BadSettingException($"{key} can't be changed from the admin page.");
            var value = raw?.Trim();
            switch (info.Kind)
            {
                case "secret" when value is null:
                    continue; // left blank: keep the one that is set
                case "bool":
                    if (value is not ("true" or "false"))
                        throw new BadSettingException($"{info.Label} must be on or off.");
                    break;
                case "number":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || n <= 0 || n > 100_000)
                        throw new BadSettingException($"{info.Label} must be a number above 0.");
                    break;
                case "url":
                    if (value is { Length: > 0 } && !(Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"))
                        throw new BadSettingException($"{info.Label} must be an http:// or https:// address.");
                    value = value?.TrimEnd('/');
                    break;
            }
            if (info.Key == "Urls")
            {
                var urls = (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (urls.Length == 0)
                    throw new BadSettingException("Listen address can't be empty.");
                foreach (var url in urls)
                    if (!Uri.TryCreate(url.Replace("://*", "://0.0.0.0").Replace("://+", "://0.0.0.0"), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                        throw new BadSettingException($"Listen address \"{url}\" isn't an http:// or https:// address with a port.");
            }
            clean[info.Key] = value is "" && info.Default is null ? "" : value is "" ? null : value;
        }

        // What the server would start with: the running values, then the file, then these changes.
        var merged = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Editable)
            merged[s.Key] = running[s.Key];
        foreach (var key in new[] { "DATA_DIR" })
            merged[key] = running[key];
        foreach (var (k, v) in Read())
            merged[k] = v;
        foreach (var (k, v) in clean)
            merged[k] = v;
        try
        {
            Settings.From(new ConfigurationBuilder().AddInMemoryCollection(merged).Build());
        }
        catch (InvalidOperationException ex)
        {
            throw new BadSettingException(ex.Message);
        }
        return clean;
    }

    JsonObject Load()
    {
        if (!File.Exists(path))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), null, DocOptions) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new BadSettingException($"{path} isn't valid JSON, so it wasn't changed: {ex.Message}");
        }
    }
}
