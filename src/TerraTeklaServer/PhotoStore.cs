using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraTeklaServer;

/// <summary>A name sent by the app (job, load or piece mark) that can't be used as a folder or file name.</summary>
public sealed class BadNameException(string message) : Exception(message);

/// <summary>The rules file can't be read or a rule doesn't make a usable folder.</summary>
public sealed class PhotoRulesException(string message) : Exception(message);

/// <summary>An uploaded file is too big or not a type the server takes.</summary>
public sealed class BadPhotoException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>The photo settings (PHOTOS_* in local.settings.json, see docs/PHOTOS.md).</summary>
public sealed record PhotoSettings(bool Enabled, string Root, string RulesPath, long MaxBytes, int MaxFiles, string[] Types)
{
    public static readonly string[] AllTypes = ["jpg", "png", "heic", "webp", "pdf"];

    public static PhotoSettings From(IConfiguration config)
    {
        var enabled = bool.TryParse(config["PHOTOS_ENABLED"], out var on) && on;
        var root = (config["PHOTOS_ROOT"] ?? "").Trim();
        var rules = (config["PHOTOS_RULES"] ?? "").Trim();
        if (rules == "")
            rules = "photo-rules.json";
        if (!Path.IsPathRooted(rules))
            rules = Path.Combine(AppContext.BaseDirectory, rules);

        var maxMb = double.TryParse(config["PHOTOS_MAX_MB"], System.Globalization.CultureInfo.InvariantCulture, out var mb) ? mb : 25;
        var maxFiles = int.TryParse(config["PHOTOS_MAX_PER_UPLOAD"], out var n) ? n : 20;
        var types = (config["PHOTOS_TYPES"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.TrimStart('.').ToLowerInvariant())
            .Select(t => t is "jpeg" ? "jpg" : t is "heif" ? "heic" : t)
            .Distinct()
            .ToArray();
        if (types.Length == 0)
            types = AllTypes;

        var settings = new PhotoSettings(enabled, root, rules, (long)(maxMb * 1024 * 1024), maxFiles, types);
        if (!enabled)
            return settings;

        if (root == "" || !Path.IsPathRooted(root))
            throw new InvalidOperationException("PHOTOS_ENABLED is on, so PHOTOS_ROOT must be set to a full folder path, e.g. D:\\TerraPhotos.");
        if (settings.MaxBytes < 1 || maxFiles < 1)
            throw new InvalidOperationException("PHOTOS_MAX_MB and PHOTOS_MAX_PER_UPLOAD must be above 0.");
        var unknown = types.Except(AllTypes).ToArray();
        if (unknown.Length > 0)
            throw new InvalidOperationException($"PHOTOS_TYPES has unknown types: {string.Join(", ", unknown)}. Known: {string.Join(", ", AllTypes)}.");
        return settings;
    }
}

/// <summary>
/// Saves photos the app sends for a job's load into
/// <c>&lt;job folder&gt;\&lt;load folder&gt;\</c>. The job folder comes from the first rule in the
/// rules file whose regex matches the job number, else the file's <c>default</c>, else
/// <c>PHOTOS_ROOT\{job}</c>. See docs/PHOTOS.md.
/// </summary>
public sealed class PhotoStore(PhotoSettings settings, ILogger<PhotoStore> log)
{
    static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    static readonly char[] BadChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    static readonly Regex Reserved = new(@"^(CON|PRN|AUX|NUL|COM\d|LPT\d)(\..*)?$", RegexOptions.IgnoreCase);
    static readonly Regex Placeholder = new(@"\{([A-Za-z0-9_]+)\}");

    readonly object rulesLock = new();
    (DateTime Stamp, FileRules Rules)? cached;

    public PhotoSettings Settings => settings;

    /// <summary>
    /// A job, load or mark as it may be used for one folder or file name: 1 to 64 characters, none of
    /// <c>&lt; &gt; : " / \ | ? *</c> or control characters, not "." or "..", no leading or trailing
    /// space and no trailing dot, not a Windows device name. Anything else is refused, not repaired.
    /// </summary>
    public static string CleanName(string? value, string what)
    {
        var name = value ?? "";
        if (name.Length == 0 || name.Trim().Length == 0)
            throw new BadNameException($"{what} is required.");
        if (name.Length > 64)
            throw new BadNameException($"{what} is longer than 64 characters.");
        if (name.IndexOfAny(BadChars) >= 0 || name.Any(char.IsControl))
            throw new BadNameException($"{what} \"{name}\" has a character that can't go in a folder name (< > : \" / \\ | ? * or a control character).");
        if (name != name.Trim() || name.EndsWith('.') || name.All(c => c == '.'))
            throw new BadNameException($"{what} \"{name}\" can't start or end with a space or end with a dot.");
        if (Reserved.IsMatch(name))
            throw new BadNameException($"{what} \"{name}\" is a reserved Windows name.");
        return name;
    }

    /// <summary>The folder a job's files go in, and which rule picked it ("default" when none did).</summary>
    public (string Folder, string Rule) JobFolder(string job) => JobFolder(job, LoadRules());

    /// <summary>The folder a job's files would go in under a given set of rules (the admin page's test).</summary>
    public (string Folder, string Rule) JobFolder(string job, FileRules rules)
    {
        job = CleanName(job, "Job");
        foreach (var rule in rules.Rules)
        {
            Match match;
            try
            {
                match = rule.Regex.Match(job);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new PhotoRulesException($"Rule \"{rule.Match}\" took too long to match job {job}.");
            }
            if (match.Success)
                return (Fill(rule.Folder, job, null, match, $"rule \"{rule.Match}\""), rule.Match);
        }
        return (Fill(rules.Default ?? Path.Combine(settings.Root, "{job}"), job, null, null, "default"), "default");
    }

    /// <summary>The folder a load's files go in: the job folder plus the load folder.</summary>
    public string LoadFolder(string job, string load) => LoadFolder(job, load, LoadRules());

    public string LoadFolder(string job, string load, FileRules rules)
    {
        job = CleanName(job, "Job");
        load = CleanName(load, "Load");
        var jobFolder = JobFolder(job, rules).Folder;
        var loadName = CleanName(Fill(rules.LoadFolder, job, load, null, "loadFolder", folder: false), "Load folder");
        return Inside(jobFolder, Path.Combine(jobFolder, loadName));
    }

    /// <summary>Checks a file's size and type from its first bytes and returns its extension.</summary>
    public string CheckFile(long length, ReadOnlySpan<byte> head, string what)
    {
        if (length == 0)
            throw new BadPhotoException($"{what} is empty.", 400);
        if (length > settings.MaxBytes)
            throw new BadPhotoException($"{what} is {length / 1048576.0:0.#} MB; the limit is {settings.MaxBytes / 1048576.0:0.#} MB (PHOTOS_MAX_MB).", 413);
        var type = Sniff(head);
        if (type is null || !settings.Types.Contains(type))
            throw new BadPhotoException($"{what} isn't one of the file types this server takes: {string.Join(", ", settings.Types)}.", 415);
        return type;
    }

    /// <summary>The type of a file from its first bytes: jpg, png, heic, webp, pdf or null.</summary>
    public static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
            return "jpg";
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "png";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8))
            return "webp";
        if (b.Length >= 5 && b[..5].SequenceEqual("%PDF-"u8))
            return "pdf";
        if (b.Length >= 12 && b[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = System.Text.Encoding.ASCII.GetString(b[8..12]);
            if (brand is "heic" or "heix" or "hevc" or "hevx" or "mif1" or "msf1" or "heim" or "heis")
                return "heic";
        }
        return null;
    }

    /// <summary>Saves one checked file in the load's folder under a new name and returns that name.</summary>
    /// The name is <c>&lt;date-time&gt;_&lt;mark or "load"&gt;_&lt;user&gt;.&lt;ext&gt;</c>, so a folder of photos
    /// shows who took each one in Explorer too.
    public async Task<string> SaveAsync(string job, string load, string? mark, string? user, string type, Stream content, CancellationToken ct)
    {
        var folder = LoadFolder(job, load);
        var label = string.IsNullOrWhiteSpace(mark) ? "load" : CleanName(mark.Trim(), "Mark");
        Directory.CreateDirectory(folder);

        var who = UserLabel(user);
        var stem = $"{DateTime.Now:yyyyMMdd-HHmmss}_{label}{(who == "" ? "" : $"_{who}")}";
        var temp = Path.Combine(folder, $".upload-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(file, ct);

            // Never overwrite: two photos in the same second get "(2)", "(3)"...
            for (var n = 1; ; n++)
            {
                var name = n == 1 ? $"{stem}.{type}" : $"{stem} ({n}).{type}";
                var path = Inside(folder, Path.Combine(folder, name));
                try
                {
                    File.Move(temp, path, overwrite: false);
                    log.LogInformation("Saved {File} for job {Job} load {Load} in {Folder}", name, job, load, folder);
                    return name;
                }
                catch (IOException) when (File.Exists(path) && n < 1000)
                {
                }
            }
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>
    /// A device name made safe for a file name: characters a file name can't hold become "-",
    /// "_" (which splits the name's parts) becomes a space, at most 40 characters. Unlike job,
    /// load and mark it is cleaned, not refused, since people name their iPads whatever they like.
    /// </summary>
    public static string UserLabel(string? user)
    {
        var chars = (user ?? "").Select(c => BadChars.Contains(c) || char.IsControl(c) ? '-' : c == '_' ? ' ' : c).ToArray();
        var name = new string(chars).Trim();
        if (name.Length > 40)
            name = name[..40].Trim();
        return name.TrimEnd('.').Trim();
    }

    /// <summary>The files already saved for a load, oldest first.</summary>
    public IReadOnlyList<SavedFile> List(string job, string load)
    {
        var folder = new DirectoryInfo(LoadFolder(job, load));
        if (!folder.Exists)
            return [];
        return folder.EnumerateFiles()
            .Where(f => !f.Name.StartsWith('.'))
            .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new SavedFile(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToList();
    }

    /// <summary>
    /// One saved file of a load, by the name <see cref="List"/> gave it. The name must be a plain file
    /// name (the same checks as job and load, and not starting with "."), and the file must be there.
    /// </summary>
    public FileInfo Find(string job, string load, string name)
    {
        var folder = LoadFolder(job, load);
        name = CleanName(name, "File name");
        if (name.StartsWith('.'))
            throw new BadNameException($"File name \"{name}\" can't start with a dot.");
        var file = new FileInfo(Inside(folder, Path.Combine(folder, name)));
        if (!file.Exists)
            throw new FileNotFoundException($"There is no file {name} for job {job} load {load}.");
        return file;
    }

    /// <summary>
    /// Deletes a saved file by moving it into the load's <c>.deleted</c> folder, so a mistake can be
    /// undone on the server by moving it back. Returns where it went (relative to the load folder).
    /// </summary>
    public string Delete(string job, string load, string name)
    {
        var file = Find(job, load, name);
        var trash = Directory.CreateDirectory(Path.Combine(file.DirectoryName!, DeletedFolder)).FullName;
        var stem = Path.GetFileNameWithoutExtension(file.Name);
        for (var n = 1; ; n++)
        {
            var target = Path.Combine(trash, n == 1 ? file.Name : $"{stem} ({n}){file.Extension}");
            if (File.Exists(target))
                continue;
            file.MoveTo(target);
            log.LogInformation("Deleted {File} for job {Job} load {Load} (moved to {Target})", name, job, load, target);
            return Path.Combine(DeletedFolder, Path.GetFileName(target));
        }
    }

    /// <summary>The folder inside each load folder that deleted files are moved to.</summary>
    public const string DeletedFolder = ".deleted";

    /// <summary>The Content-Type a saved file is sent with.</summary>
    public static string ContentType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".heic" => "image/heic",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };

    // Fills {job}, {load} and the rule's regex groups ({1}, {name}) into a folder template.
    static string Fill(string template, string job, string? load, Match? match, string source, bool folder = true)
    {
        var filled = Placeholder.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            if (key.Equals("job", StringComparison.OrdinalIgnoreCase))
                return job;
            if (key.Equals("load", StringComparison.OrdinalIgnoreCase) && load is not null)
                return load;
            if (match is not null && match.Groups[key] is { Success: true } g)
                return g.Value;
            throw new PhotoRulesException($"The {source} folder \"{template}\" uses {{{key}}}, which isn't {{job}}{(load is null ? "" : ", {load}")} or a group in its regex.");
        });
        if (!folder)
            return filled;
        if (!Path.IsPathRooted(filled))
            throw new PhotoRulesException($"The {source} folder \"{template}\" must be a full path (C:\\..., D:\\..., or \\\\server\\share\\...).");
        var full = Path.GetFullPath(filled);
        // A group or {job} can't add ".." because names are checked, but the template itself could.
        if (filled.Split('/', '\\').Contains(".."))
            throw new PhotoRulesException($"The {source} folder \"{template}\" can't contain \"..\".");
        return full;
    }

    // Makes sure `path` is inside `folder` once both are resolved.
    static string Inside(string folder, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new BadNameException("That name would put the file outside its folder.");
        return full;
    }

    /// <summary>The rules file as it is now (throws <see cref="PhotoRulesException"/> when it is broken).</summary>
    public FileRules Rules() => LoadRules();

    /// <summary>
    /// Writes the rules file from the admin page, after checking every regex and that each folder only
    /// uses placeholders it can fill. The old file (comments and all) is kept as photo-rules.json.bak.
    /// Takes effect at once: the file is re-read when it changes.
    /// </summary>
    public FileRules SaveRules(string? defaultFolder, string? loadFolder, IReadOnlyList<(string Match, string Folder)> rules)
    {
        var json = new System.Text.Json.Nodes.JsonObject();
        if (!string.IsNullOrWhiteSpace(defaultFolder))
            json["default"] = defaultFolder.Trim();
        json["loadFolder"] = string.IsNullOrWhiteSpace(loadFolder) ? "{load}" : loadFolder.Trim();
        json["rules"] = new System.Text.Json.Nodes.JsonArray(rules.Select(r => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
        {
            ["match"] = r.Match.Trim(),
            ["folder"] = r.Folder.Trim(),
        }).ToArray());
        // Relaxed escaping keeps regexes readable in the file ("<" rather than "\u003C").
        var text = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var parsed = FileRules.Parse(text, "The rules");
        parsed.Validate();

        lock (rulesLock)
        {
            var path = settings.RulesPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
                File.Copy(path, path + ".bak", overwrite: true);
            var temp = path + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
            cached = null;
        }
        log.LogInformation("Photo folder rules saved from the admin page ({Count} rules)", parsed.Rules.Count);
        return parsed;
    }

    // The rules file, re-read whenever it changes on disk. No file means no rules.
    FileRules LoadRules()
    {
        var info = new FileInfo(settings.RulesPath);
        if (!info.Exists)
            return FileRules.Empty;
        lock (rulesLock)
        {
            if (cached is { } c && c.Stamp == info.LastWriteTimeUtc)
                return c.Rules;
            var rules = FileRules.Parse(File.ReadAllText(info.FullName), info.FullName);
            cached = (info.LastWriteTimeUtc, rules);
            return rules;
        }
    }

    /// <summary>The parsed rules file.</summary>
    public sealed record FileRules(string? Default, string LoadFolder, IReadOnlyList<FileRule> Rules)
    {
        public static readonly FileRules Empty = new(null, "{load}", []);

        public static FileRules Parse(string json, string source)
        {
            RulesJson? raw;
            try
            {
                raw = JsonSerializer.Deserialize<RulesJson>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });
            }
            catch (JsonException ex)
            {
                throw new PhotoRulesException($"{source} isn't valid JSON: {ex.Message}");
            }

            var rules = new List<FileRule>();
            foreach (var (r, i) in (raw?.Rules ?? []).Select((r, i) => (r, i + 1)))
            {
                if (string.IsNullOrWhiteSpace(r.Match) || string.IsNullOrWhiteSpace(r.Folder))
                    throw new PhotoRulesException($"{source}: rule {i} needs both \"match\" and \"folder\".");
                Regex regex;
                try
                {
                    regex = new Regex(r.Match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                }
                catch (ArgumentException ex)
                {
                    throw new PhotoRulesException($"{source}: rule {i} \"match\" isn't a valid regex: {ex.Message}");
                }
                rules.Add(new FileRule(r.Match, r.Folder.Trim(), regex));
            }
            var loadFolder = string.IsNullOrWhiteSpace(raw?.LoadFolder) ? "{load}" : raw.LoadFolder.Trim();
            return new FileRules(string.IsNullOrWhiteSpace(raw?.Default) ? null : raw.Default.Trim(), loadFolder, rules);
        }

        /// <summary>
        /// Checks each folder only uses placeholders it can fill: {job} and the rule's regex groups in a
        /// rule, {job} in the default, {job} and {load} in the load folder.
        /// </summary>
        public void Validate()
        {
            static void Only(string template, string where, IEnumerable<string> allowed, bool rooted)
            {
                var names = allowed.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in Placeholder.Matches(template))
                    if (!names.Contains(m.Groups[1].Value))
                        throw new PhotoRulesException($"{where} uses {{{m.Groups[1].Value}}}, which isn't {string.Join(", ", names.Select(n => $"{{{n}}}"))}.");
                if (rooted && !(template.StartsWith('{') || Path.IsPathRooted(template) || template.StartsWith(@"\\") || Regex.IsMatch(template, @"^[A-Za-z]:[\\/]")))
                    throw new PhotoRulesException($"{where} must be a full path (C:\\..., D:\\..., or \\\\server\\share\\...).");
            }
            foreach (var (rule, i) in Rules.Select((r, i) => (r, i + 1)))
                Only(rule.Folder, $"Rule {i}'s folder", rule.Regex.GetGroupNames().Where(g => g != "0").Append("job"), rooted: true);
            if (Default is not null)
                Only(Default, "The default folder", ["job"], rooted: true);
            Only(LoadFolder, "The load folder", ["job", "load"], rooted: false);
            if (LoadFolder.IndexOfAny(['/', '\\']) >= 0)
                throw new PhotoRulesException("The load folder is one folder name: it can't have / or \\ in it.");
        }

        sealed record RulesJson(string? Default, string? LoadFolder, List<RuleJson>? Rules);
        sealed record RuleJson(string? Match, string? Folder);
    }

    public sealed record FileRule(string Match, string Folder, Regex Regex);
}

public sealed record SavedFile(string Name, long Bytes, DateTime Modified);
