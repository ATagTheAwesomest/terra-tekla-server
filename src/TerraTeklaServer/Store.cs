using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace TerraTeklaServer;

public sealed record Device(string Id, string Name, string Color, string CreatedAt, string LastSeenAt, string? LastIp, string? UserAgent, string? AppVersion);

public sealed record Activity(long Id, string At, string? DeviceId, string? DeviceName, string? DeviceColor, string Kind, string? Job,
    string? LoadId, string? LoadNumber, string? StackId, string? StackName, string? Mark, string Text);

public sealed record PhotoMeta(long Id, string Job, string LoadNumber, string FileName, string? Mark, string? DeviceId, string? DeviceName, long Bytes, string TakenAt);

/// <summary>
/// The SQLite database (DATA_DIR\terra.db): devices, every load's stacks and rows, the shared truck
/// checks, photos and the activity history. The schema is in docs/SETUP.md. Loads in use are also kept
/// in memory; every change goes through <see cref="ApplyAsync"/> one at a time and is written before
/// it is answered or sent to the other iPads.
/// </summary>
public sealed class Store
{
    public static readonly string[] Palette = ["#ffb000", "#2f9bff", "#2bd96b", "#ff5c8a", "#b57cff", "#00d1c1", "#ff7a2f", "#e6e14a"];

    readonly string connectionString;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Dictionary<string, LoadState> loads = new();
    long rev;

    public Store(Settings settings)
    {
        Directory.CreateDirectory(settings.DataDir);
        connectionString = new SqliteConnectionStringBuilder { DataSource = settings.DatabasePath, Pooling = true }.ToString();
        using var db = Open();
        Exec(db, Schema);
        rev = long.TryParse(Scalar(db, "SELECT value FROM meta WHERE key = 'rev'") as string, out var r) ? r : 0;
    }

    public const string Schema = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS devices (
            id TEXT PRIMARY KEY, name TEXT NOT NULL, color TEXT NOT NULL,
            created_at TEXT NOT NULL, last_seen_at TEXT NOT NULL,
            last_ip TEXT, user_agent TEXT, app_version TEXT);
        CREATE TABLE IF NOT EXISTS loads (
            load_id TEXT PRIMARY KEY, job TEXT NOT NULL, load_number TEXT NOT NULL,
            rev INTEGER NOT NULL, updated_at TEXT NOT NULL,
            check_started_at TEXT, check_updated_at TEXT,
            recent_ops TEXT NOT NULL DEFAULT '[]', removed TEXT NOT NULL DEFAULT '[]');
        CREATE INDEX IF NOT EXISTS loads_job ON loads (job);
        CREATE TABLE IF NOT EXISTS stacks (
            id TEXT NOT NULL, load_id TEXT NOT NULL, position INTEGER NOT NULL,
            name TEXT NOT NULL, created_by TEXT, PRIMARY KEY (load_id, id));
        CREATE TABLE IF NOT EXISTS entries (
            id TEXT NOT NULL, load_id TEXT NOT NULL, stack_id TEXT NOT NULL, position INTEGER NOT NULL,
            mark TEXT NOT NULL, sequence TEXT NOT NULL, manual INTEGER NOT NULL,
            pending INTEGER NOT NULL, removing INTEGER NOT NULL,
            scanned_at TEXT, scanned_by TEXT, added_at TEXT NOT NULL, rev INTEGER NOT NULL,
            details TEXT, PRIMARY KEY (load_id, id));
        CREATE INDEX IF NOT EXISTS entries_mark ON entries (mark);
        CREATE INDEX IF NOT EXISTS entries_scanned_by ON entries (scanned_by);
        CREATE TABLE IF NOT EXISTS check_scans (
            id TEXT NOT NULL, load_id TEXT NOT NULL, position INTEGER NOT NULL,
            mark TEXT NOT NULL, manual INTEGER NOT NULL, scanned_at TEXT NOT NULL, scanned_by TEXT,
            PRIMARY KEY (load_id, id));
        CREATE TABLE IF NOT EXISTS activity (
            id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL,
            device_id TEXT, device_name TEXT, kind TEXT NOT NULL,
            job TEXT, load_id TEXT, load_number TEXT, stack_id TEXT, stack_name TEXT, mark TEXT, text TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS activity_device ON activity (device_id, id);
        CREATE INDEX IF NOT EXISTS activity_load ON activity (load_id, id);
        CREATE INDEX IF NOT EXISTS activity_job ON activity (job, id);
        CREATE TABLE IF NOT EXISTS photos (
            id INTEGER PRIMARY KEY AUTOINCREMENT, job TEXT NOT NULL, load_number TEXT NOT NULL,
            file_name TEXT NOT NULL, mark TEXT, device_id TEXT, device_name TEXT,
            bytes INTEGER NOT NULL, taken_at TEXT NOT NULL, deleted_at TEXT);
        CREATE INDEX IF NOT EXISTS photos_load ON photos (job, load_number);
        """;

    SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        Exec(db, "PRAGMA busy_timeout = 5000;");
        return db;
    }

    // ---- Loads -------------------------------------------------------------------------

    /// <summary>
    /// Applies one operation from a device to a load and writes the result. Returns the load as it now
    /// is, whether it changed, and the history lines written.
    /// </summary>
    public async Task<(LoadState State, bool Changed, List<Activity> Activity)> ApplyAsync(
        string loadId, string job, string loadNumber, JsonObject op, Device device)
    {
        await gate.WaitAsync();
        try
        {
            using var db = Open();
            var state = LoadOrNew(db, loadId, job, loadNumber);
            if (job != "")
                state.Job = job;
            if (loadNumber != "")
                state.LoadNumber = loadNumber;
            var work = state.Clone();
            var ctx = new OpContext(device.Id, device.Name, rev + 1, DateTime.UtcNow);
            var (changed, items) = LoadOps.Apply(work, op, ctx);

            using var tx = db.BeginTransaction();
            if (changed)
            {
                rev = ctx.Rev;
                Exec(db, "INSERT INTO meta (key, value) VALUES ('rev', $v) ON CONFLICT (key) DO UPDATE SET value = $v", ("$v", rev.ToString()));
            }
            if (changed || items.Count > 0 || !Exists(db, loadId))
                Save(db, work);
            var written = items.Select(a => AddActivity(db, ctx.NowIso, device, a.Kind, work.Job, work.LoadId, work.LoadNumber, a.StackId, a.StackName, a.Mark, a.Text)).ToList();
            tx.Commit();
            loads[loadId] = work;
            return (work, changed, written);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A load's stacks, or a fresh one-stack load when the server hasn't seen it.</summary>
    public async Task<LoadState> GetAsync(string loadId, string job, string loadNumber)
    {
        await gate.WaitAsync();
        try
        {
            using var db = Open();
            return LoadOrNew(db, loadId, job, loadNumber).Clone();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Every load of a job the server has stacks for.</summary>
    public async Task<List<LoadState>> ForJobAsync(string job)
    {
        await gate.WaitAsync();
        try
        {
            using var db = Open();
            var ids = Rows(db, "SELECT load_id FROM loads WHERE job = $job COLLATE NOCASE", r => r.GetString(0), ("$job", job));
            return ids.Select(id => LoadOrNew(db, id, job, "").Clone())
                .OrderBy(l => l.LoadNumber, NumericOrder.Instance).ToList();
        }
        finally
        {
            gate.Release();
        }
    }

    public List<(string Job, int Loads, int Pieces, string? LastAt)> Jobs()
    {
        using var db = Open();
        return Rows(db, """
            SELECT l.job, COUNT(*), (SELECT COUNT(*) FROM entries e JOIN loads l2 ON l2.load_id = e.load_id WHERE l2.job = l.job),
                   (SELECT MAX(at) FROM activity a WHERE a.job = l.job)
            FROM loads l GROUP BY l.job ORDER BY 4 DESC
            """, r => (r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3)));
    }

    LoadState LoadOrNew(SqliteConnection db, string loadId, string job, string loadNumber)
    {
        if (loads.TryGetValue(loadId, out var cached))
            return cached;
        var state = Read(db, loadId) ?? LoadOps.NewState(loadId, job, loadNumber);
        loads[loadId] = state;
        return state;
    }

    static bool Exists(SqliteConnection db, string loadId) =>
        Scalar(db, "SELECT 1 FROM loads WHERE load_id = $id", ("$id", loadId)) is not null;

    static LoadState? Read(SqliteConnection db, string loadId)
    {
        LoadState? s = null;
        using (var cmd = Command(db, "SELECT job, load_number, rev, updated_at, check_started_at, check_updated_at, recent_ops, removed FROM loads WHERE load_id = $id", ("$id", loadId)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read())
                return null;
            s = new LoadState
            {
                LoadId = loadId,
                Job = r.GetString(0),
                LoadNumber = r.GetString(1),
                Rev = r.GetInt64(2),
                UpdatedAt = r.GetString(3),
                TruckCheck = r.IsDBNull(4) ? null : new TruckCheck { StartedAt = r.GetString(4), UpdatedAt = r.IsDBNull(5) ? r.GetString(4) : r.GetString(5) },
                RecentOps = JsonSerializer.Deserialize<List<string>>(r.GetString(6)) ?? [],
                Removed = JsonSerializer.Deserialize<List<Removal>>(r.GetString(7)) ?? [],
            };
        }
        s.Stacks = Rows(db, "SELECT id, name, created_by FROM stacks WHERE load_id = $id ORDER BY position",
            r => new Stack { Id = r.GetString(0), Name = r.GetString(1), CreatedBy = r.IsDBNull(2) ? null : r.GetString(2) }, ("$id", loadId));
        var byId = s.Stacks.ToDictionary(st => st.Id);
        foreach (var (stackId, entry) in Rows(db, """
            SELECT stack_id, id, mark, sequence, manual, pending, removing, scanned_at, scanned_by, added_at, rev, details
            FROM entries WHERE load_id = $id ORDER BY position
            """, r => (r.GetString(0), new Entry
        {
            Id = r.GetString(1),
            Mark = r.GetString(2),
            Sequence = r.GetString(3),
            Manual = r.GetInt64(4) != 0,
            Pending = r.GetInt64(5) != 0,
            Removing = r.GetInt64(6) != 0,
            ScannedAt = r.IsDBNull(7) ? null : r.GetString(7),
            ScannedBy = r.IsDBNull(8) ? null : r.GetString(8),
            AddedAt = r.GetString(9),
            Rev = r.GetInt64(10),
            Details = r.IsDBNull(11) ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.GetString(11)),
        }), ("$id", loadId)))
        {
            if (byId.TryGetValue(stackId, out var stack))
                stack.Items.Add(entry);
        }
        if (s.Stacks.Count == 0)
            s.Stacks.Add(new Stack { Id = LoadOps.DefaultStackId(loadId), Name = $"Stack {s.LoadNumber} - 1" });
        if (s.TruckCheck is not null)
            s.TruckCheck.Scans = Rows(db, "SELECT id, mark, manual, scanned_at, scanned_by FROM check_scans WHERE load_id = $id ORDER BY position",
                r => new CheckScan { Id = r.GetString(0), Mark = r.GetString(1), Manual = r.GetInt64(2) != 0, ScannedAt = r.GetString(3), ScannedBy = r.IsDBNull(4) ? null : r.GetString(4) },
                ("$id", loadId));
        return s;
    }

    // Writes a load's whole state (it is small: a few hundred rows at most).
    static void Save(SqliteConnection db, LoadState s)
    {
        Exec(db, """
            INSERT INTO loads (load_id, job, load_number, rev, updated_at, check_started_at, check_updated_at, recent_ops, removed)
            VALUES ($id, $job, $num, $rev, $at, $cs, $cu, $ops, $rm)
            ON CONFLICT (load_id) DO UPDATE SET job = $job, load_number = $num, rev = $rev, updated_at = $at,
                check_started_at = $cs, check_updated_at = $cu, recent_ops = $ops, removed = $rm
            """,
            ("$id", s.LoadId), ("$job", s.Job), ("$num", s.LoadNumber), ("$rev", s.Rev),
            ("$at", s.UpdatedAt == "" ? DateTime.UtcNow.ToString("o") : s.UpdatedAt),
            ("$cs", s.TruckCheck?.StartedAt), ("$cu", s.TruckCheck?.UpdatedAt),
            ("$ops", JsonSerializer.Serialize(s.RecentOps)), ("$rm", JsonSerializer.Serialize(s.Removed)));
        foreach (var table in new[] { "stacks", "entries", "check_scans" })
            Exec(db, $"DELETE FROM {table} WHERE load_id = $id", ("$id", s.LoadId));
        for (var si = 0; si < s.Stacks.Count; si++)
        {
            var st = s.Stacks[si];
            Exec(db, "INSERT INTO stacks (id, load_id, position, name, created_by) VALUES ($id, $load, $pos, $name, $by)",
                ("$id", st.Id), ("$load", s.LoadId), ("$pos", si), ("$name", st.Name), ("$by", st.CreatedBy));
            for (var i = 0; i < st.Items.Count; i++)
            {
                var e = st.Items[i];
                Exec(db, """
                    INSERT INTO entries (id, load_id, stack_id, position, mark, sequence, manual, pending, removing, scanned_at, scanned_by, added_at, rev, details)
                    VALUES ($id, $load, $stack, $pos, $mark, $seq, $manual, $pending, $removing, $sat, $sby, $added, $rev, $details)
                    """,
                    ("$id", e.Id), ("$load", s.LoadId), ("$stack", st.Id), ("$pos", i), ("$mark", e.Mark), ("$seq", e.Sequence),
                    ("$manual", e.Manual ? 1 : 0), ("$pending", e.Pending ? 1 : 0), ("$removing", e.Removing ? 1 : 0),
                    ("$sat", e.ScannedAt), ("$sby", e.ScannedBy), ("$added", e.AddedAt), ("$rev", e.Rev),
                    ("$details", e.Details is null ? null : JsonSerializer.Serialize(e.Details)));
            }
        }
        for (var i = 0; i < (s.TruckCheck?.Scans.Count ?? 0); i++)
        {
            var c = s.TruckCheck!.Scans[i];
            Exec(db, "INSERT INTO check_scans (id, load_id, position, mark, manual, scanned_at, scanned_by) VALUES ($id, $load, $pos, $mark, $manual, $at, $by)",
                ("$id", c.Id), ("$load", s.LoadId), ("$pos", i), ("$mark", c.Mark), ("$manual", c.Manual ? 1 : 0), ("$at", c.ScannedAt), ("$by", c.ScannedBy));
        }
    }

    // ---- Devices -----------------------------------------------------------------------

    /// <summary>
    /// Records that a device was seen, creating it with a starting name and colour the first time.
    /// Returns it and whether it is new.
    /// </summary>
    public (Device Device, bool Created) Touch(string id, string? ip, string? userAgent, string? appVersion)
    {
        using var db = Open();
        var now = DateTime.UtcNow.ToString("o");
        var existing = GetDevice(db, id);
        if (existing is null)
        {
            var count = Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM devices"));
            var name = $"iPad {count + 1}";
            Exec(db, "INSERT INTO devices (id, name, color, created_at, last_seen_at, last_ip, user_agent, app_version) VALUES ($id, $name, $color, $now, $now, $ip, $ua, $v)",
                ("$id", id), ("$name", name), ("$color", Palette[count % Palette.Length]), ("$now", now), ("$ip", ip), ("$ua", Trim(userAgent, 300)), ("$v", Trim(appVersion, 30)));
            return (GetDevice(db, id)!, true);
        }
        Exec(db, "UPDATE devices SET last_seen_at = $now, last_ip = $ip, user_agent = COALESCE($ua, user_agent), app_version = COALESCE($v, app_version) WHERE id = $id",
            ("$id", id), ("$now", now), ("$ip", ip), ("$ua", Trim(userAgent, 300)), ("$v", Trim(appVersion, 30)));
        return (existing with { LastSeenAt = now, LastIp = ip }, false);
    }

    public Device? GetDevice(string id)
    {
        using var db = Open();
        return GetDevice(db, id);
    }

    static Device? GetDevice(SqliteConnection db, string id) =>
        Rows(db, "SELECT id, name, color, created_at, last_seen_at, last_ip, user_agent, app_version FROM devices WHERE id = $id", ReadDevice, ("$id", id)).FirstOrDefault();

    public List<Device> Devices()
    {
        using var db = Open();
        return Rows(db, "SELECT id, name, color, created_at, last_seen_at, last_ip, user_agent, app_version FROM devices ORDER BY last_seen_at DESC", ReadDevice);
    }

    static Device ReadDevice(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7));

    /// <summary>Sets a device's name and/or colour; null leaves that one as it is.</summary>
    public Device UpdateDevice(string id, string? name, string? color, Device by)
    {
        name = name is null ? null : string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (name is not null && (name.Length is 0 or > 40))
            throw new OpException("A device name is 1 to 40 characters.");
        if (color is not null && !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$"))
            throw new OpException("A colour is #rrggbb, like #ffb000.");
        using var db = Open();
        var before = GetDevice(db, id) ?? throw new KeyNotFoundException($"No device {id}.");
        Exec(db, "UPDATE devices SET name = COALESCE($name, name), color = COALESCE($color, color) WHERE id = $id",
            ("$id", id), ("$name", name), ("$color", color?.ToLowerInvariant()));
        var after = GetDevice(db, id)!;
        if (after.Name != before.Name)
            AddActivity(db, DateTime.UtcNow.ToString("o"), by, "device-rename",
                null, null, null, null, null, null, by.Id == id ? $"Renamed this device from {before.Name} to {after.Name}" : $"Renamed {before.Name} to {after.Name}");
        return after;
    }

    // ---- Activity ----------------------------------------------------------------------

    public Activity Log(Device? device, string kind, string text, string? job = null, string? loadId = null, string? loadNumber = null, string? mark = null)
    {
        using var db = Open();
        return AddActivity(db, DateTime.UtcNow.ToString("o"), device, kind, job, loadId, loadNumber, null, null, mark, text);
    }

    static Activity AddActivity(SqliteConnection db, string at, Device? device, string kind, string? job, string? loadId, string? loadNumber,
        string? stackId, string? stackName, string? mark, string text)
    {
        Exec(db, """
            INSERT INTO activity (at, device_id, device_name, kind, job, load_id, load_number, stack_id, stack_name, mark, text)
            VALUES ($at, $dev, $name, $kind, $job, $load, $num, $stack, $sname, $mark, $text)
            """,
            ("$at", at), ("$dev", device?.Id), ("$name", device?.Name), ("$kind", kind), ("$job", job), ("$load", loadId),
            ("$num", loadNumber), ("$stack", stackId), ("$sname", stackName), ("$mark", mark), ("$text", text));
        var id = Convert.ToInt64(Scalar(db, "SELECT last_insert_rowid()"));
        return new Activity(id, at, device?.Id, device?.Name, device?.Color, kind, job, loadId, loadNumber, stackId, stackName, mark, text);
    }

    /// <summary>Newest first. Every filter is optional; <paramref name="before"/> pages back by id.</summary>
    public List<Activity> History(string? deviceId = null, string? job = null, string? loadId = null, string? mark = null, long? before = null, int limit = 100)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();
        void Add(string sql, string name, object? value)
        {
            if (value is null || value as string == "")
                return;
            where.Add(sql);
            args.Add((name, value));
        }
        Add("a.device_id = $dev", "$dev", deviceId);
        Add("a.job = $job COLLATE NOCASE", "$job", job);
        Add("a.load_id = $load", "$load", loadId);
        Add("a.mark = $mark COLLATE NOCASE", "$mark", mark);
        Add("a.id < $before", "$before", before);
        args.Add(("$limit", Math.Clamp(limit, 1, 500)));
        using var db = Open();
        return Rows(db, $"""
            SELECT a.id, a.at, a.device_id, COALESCE(d.name, a.device_name), d.color, a.kind, a.job, a.load_id, a.load_number, a.stack_id, a.stack_name, a.mark, a.text
            FROM activity a LEFT JOIN devices d ON d.id = a.device_id
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY a.id DESC LIMIT $limit
            """, r => new Activity(r.GetInt64(0), r.GetString(1), N(r, 2), N(r, 3), N(r, 4), r.GetString(5), N(r, 6), N(r, 7), N(r, 8), N(r, 9), N(r, 10), N(r, 11), r.GetString(12)),
            [.. args]);
    }

    // ---- Photos ------------------------------------------------------------------------

    public void AddPhoto(string job, string loadNumber, string fileName, string? mark, Device device, long bytes)
    {
        using var db = Open();
        Exec(db, "INSERT INTO photos (job, load_number, file_name, mark, device_id, device_name, bytes, taken_at) VALUES ($job, $load, $file, $mark, $dev, $name, $bytes, $at)",
            ("$job", job), ("$load", loadNumber), ("$file", fileName), ("$mark", mark), ("$dev", device.Id), ("$name", device.Name), ("$bytes", bytes), ("$at", DateTime.UtcNow.ToString("o")));
    }

    public void PhotoDeleted(string job, string loadNumber, string fileName)
    {
        using var db = Open();
        Exec(db, "UPDATE photos SET deleted_at = $at WHERE job = $job AND load_number = $load AND file_name = $file AND deleted_at IS NULL",
            ("$job", job), ("$load", loadNumber), ("$file", fileName), ("$at", DateTime.UtcNow.ToString("o")));
    }

    /// <summary>What the database knows about a load's photos (who took them, of what), by file name.</summary>
    public Dictionary<string, PhotoMeta> Photos(string job, string loadNumber)
    {
        using var db = Open();
        return Rows(db, "SELECT id, job, load_number, file_name, mark, device_id, device_name, bytes, taken_at FROM photos WHERE job = $job AND load_number = $load AND deleted_at IS NULL",
                r => new PhotoMeta(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), N(r, 4), N(r, 5), N(r, 6), r.GetInt64(7), r.GetString(8)),
                ("$job", job), ("$load", loadNumber))
            .GroupBy(p => p.FileName).ToDictionary(g => g.Key, g => g.Last());
    }

    public List<PhotoMeta> RecentPhotos(string? deviceId, int limit)
    {
        using var db = Open();
        return Rows(db, $"SELECT id, job, load_number, file_name, mark, device_id, device_name, bytes, taken_at FROM photos WHERE deleted_at IS NULL {(deviceId is null ? "" : "AND device_id = $dev")} ORDER BY id DESC LIMIT $limit",
            r => new PhotoMeta(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), N(r, 4), N(r, 5), N(r, 6), r.GetInt64(7), r.GetString(8)),
            ("$dev", deviceId), ("$limit", Math.Clamp(limit, 1, 200)));
    }

    // ---- SQL helpers -------------------------------------------------------------------

    static string? N(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    static string? Trim(string? s, int max) => s is null ? null : s.Length > max ? s[..max] : s;

    static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    static void Exec(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(db, sql, args);
        cmd.ExecuteNonQuery();
    }

    static object? Scalar(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(db, sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    static List<T> Rows<T>(SqliteConnection db, string sql, Func<SqliteDataReader, T> read, params (string, object?)[] args)
    {
        using var cmd = Command(db, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read())
            list.Add(read(r));
        return list;
    }
}

/// <summary>Sorts "2" before "10", as load numbers read.</summary>
public sealed class NumericOrder : IComparer<string>
{
    public static readonly NumericOrder Instance = new();

    public int Compare(string? a, string? b) =>
        decimal.TryParse(a, out var x) && decimal.TryParse(b, out var y) ? x.CompareTo(y) : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
}
