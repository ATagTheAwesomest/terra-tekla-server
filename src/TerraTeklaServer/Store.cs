using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace TerraTeklaServer;

/// <summary>
/// An iPad (or other device running the app). <c>Number</c> is fixed when it is first seen ("User 1",
/// "User 2"...) and never changes or gets reused, so anything per device (such as its own PowerFab
/// login later) can hang off it. The name and colour are set on the admin board only.
/// </summary>
public sealed record Device(string Id, string Name, string Color, string CreatedAt, string LastSeenAt, string? LastIp, string? UserAgent, string? AppVersion, int Number)
{
    public string User => $"User {Number}";
}

public sealed record Activity(long Id, string At, string? DeviceId, string? DeviceName, string? DeviceColor, string Kind, string? Job,
    string? LoadId, string? LoadNumber, string? StackId, string? StackName, string? Mark, string Text);

public sealed record PhotoMeta(long Id, string Job, string LoadNumber, string FileName, string? Mark, string? DeviceId, string? DeviceName, long Bytes, string TakenAt, int? DeviceNumber = null)
{
    /// <summary>The iPad's fixed "User N", when the photo was taken by a known iPad.</summary>
    public string? User => DeviceNumber is { } n ? $"User {n}" : null;
}

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
        MigrateDeviceNumbers(db);
        AddColumn(db, "stacks", "pallet", "TEXT");
        AddColumn(db, "check_scans", "qty", "INTEGER NOT NULL DEFAULT 1");
        AddColumn(db, "photos", "device_number", "INTEGER");
        AddColumn(db, "entries", "code", "TEXT");
        AddColumn(db, "check_scans", "code", "TEXT");
        Exec(db, IndexesAfterColumns);
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
        CREATE TABLE IF NOT EXISTS device_aliases (
            old_id TEXT PRIMARY KEY, device_id TEXT NOT NULL, merged_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS device_aliases_device ON device_aliases (device_id);
        CREATE INDEX IF NOT EXISTS entries_load ON entries (load_id, stack_id, position);
        CREATE INDEX IF NOT EXISTS check_scans_load ON check_scans (load_id, position);
        CREATE TABLE IF NOT EXISTS piece_codes (
            id INTEGER PRIMARY KEY AUTOINCREMENT, job TEXT NOT NULL COLLATE NOCASE, code TEXT NOT NULL COLLATE NOCASE,
            mark TEXT NOT NULL, sequence TEXT, source TEXT NOT NULL, added_at TEXT NOT NULL, added_by TEXT);
        CREATE UNIQUE INDEX IF NOT EXISTS piece_codes_code ON piece_codes (job, code);
        CREATE INDEX IF NOT EXISTS piece_codes_mark ON piece_codes (job, mark COLLATE NOCASE);
        CREATE TABLE IF NOT EXISTS job_settings (
            job TEXT PRIMARY KEY COLLATE NOCASE, piece_codes TEXT, updated_at TEXT NOT NULL);
        """;

    // Indexes on columns added by AddColumn (databases made before them get the column first).
    const string IndexesAfterColumns = """
        CREATE INDEX IF NOT EXISTS entries_code ON entries (code COLLATE NOCASE) WHERE code IS NOT NULL;
        """;

    // Databases from before device numbers: add the column and number the devices in the order they
    // were first seen.
    static void MigrateDeviceNumbers(SqliteConnection db)
    {
        var columns = Rows(db, "PRAGMA table_info(devices)", r => r.GetString(1));
        if (!columns.Contains("number"))
            Exec(db, "ALTER TABLE devices ADD COLUMN number INTEGER");
        var unnumbered = Rows(db, "SELECT id FROM devices WHERE number IS NULL ORDER BY created_at, id", r => r.GetString(0));
        foreach (var id in unnumbered)
            Exec(db, "UPDATE devices SET number = (SELECT COALESCE(MAX(number), 0) + 1 FROM devices) WHERE id = $id", ("$id", id));
        Exec(db, "CREATE UNIQUE INDEX IF NOT EXISTS devices_number ON devices (number)");
    }

    // A column added after the first release, for databases made before it.
    static void AddColumn(SqliteConnection db, string table, string column, string type)
    {
        if (!Rows(db, $"PRAGMA table_info({table})", r => r.GetString(1)).Contains(column))
            Exec(db, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        // NORMAL is safe with WAL: an app crash loses nothing; only a power cut can lose the last moment of writes.
        Exec(db, "PRAGMA busy_timeout = 5000; PRAGMA synchronous = NORMAL;");
        return db;
    }

    // ---- Loads -------------------------------------------------------------------------

    /// <summary>
    /// Applies one operation from a device to a load and writes the result. Returns the load as it now
    /// is, whether it changed, and the history lines written. A refused operation throws
    /// <see cref="OpException"/> and changes nothing.
    /// </summary>
    public async Task<(LoadState State, bool Changed, List<Activity> Activity)> ApplyAsync(
        string loadId, string job, string loadNumber, JsonObject op, Device device)
    {
        var (state, changed, activity, results) = await ApplyManyAsync(loadId, job, loadNumber, [op], device);
        if (results[0].Error is { } error)
            throw new OpException(error);
        return (state, changed, activity);
    }

    /// <summary>The outcome of one operation of a batch: applied (and whether it changed anything), or refused.</summary>
    public sealed record OpResult(string OpId, bool Changed, string? Error);

    /// <summary>The most operations one batch can carry.</summary>
    public const int MaxBatch = 500;

    /// <summary>
    /// Applies several operations to one load, in order, in one write: how an iPad sends what it has
    /// queued (fast scanning, or catching up after being offline). An operation the load refuses is
    /// skipped and reported; the rest still apply.
    /// </summary>
    public async Task<(LoadState State, bool Changed, List<Activity> Activity, List<OpResult> Results)> ApplyManyAsync(
        string loadId, string job, string loadNumber, IReadOnlyList<JsonObject> ops, Device device)
    {
        if (ops.Count > MaxBatch)
            throw new OpException($"At most {MaxBatch} operations at once.");
        await gate.WaitAsync();
        try
        {
            using var db = Open();
            var known = Exists(db, loadId);
            var state = LoadOrNew(db, loadId, job, loadNumber);
            if (job != "")
                state.Job = job;
            if (loadNumber != "")
                state.LoadNumber = loadNumber;
            var work = state.Clone();
            var now = DateTime.UtcNow;
            var nextRev = rev;
            var anyChanged = false;
            var results = new List<OpResult>(ops.Count);
            var lines = new List<(OpContext Ctx, ActivityItem Item)>();
            Func<string, string?> codeElsewhere = code => CodeOnOtherLoad(db, work.Job, loadId, code);
            for (var i = 0; i < ops.Count; i++)
            {
                var ctx = new OpContext(device.Id, device.Name, nextRev + 1, now, codeElsewhere);
                try
                {
                    var (changed, items) = LoadOps.Apply(work, ops[i], ctx);
                    if (changed)
                    {
                        nextRev = ctx.Rev;
                        anyChanged = true;
                    }
                    lines.AddRange(items.Select(a => (ctx, a)));
                    results.Add(new OpResult(LoadOps.Str(ops[i], "opId"), changed, null));
                }
                catch (OpException ex)
                {
                    results.Add(new OpResult(LoadOps.Str(ops[i], "opId"), false, ex.Message));
                    // An operation checks before it changes anything, but in case one didn't: start
                    // again from the load as it was and apply the ones that went through.
                    work = state.Clone();
                    nextRev = rev;
                    anyChanged = false;
                    lines.Clear();
                    for (var j = 0; j < i; j++)
                    {
                        if (results[j].Error is not null)
                            continue;
                        var again = new OpContext(device.Id, device.Name, nextRev + 1, now, codeElsewhere);
                        var (changed, items) = LoadOps.Apply(work, ops[j], again);
                        if (changed)
                        {
                            nextRev = again.Rev;
                            anyChanged = true;
                        }
                        lines.AddRange(items.Select(a => (again, a)));
                    }
                }
            }

            using var tx = db.BeginTransaction();
            if (anyChanged)
            {
                rev = nextRev;
                Exec(db, "INSERT INTO meta (key, value) VALUES ('rev', $v) ON CONFLICT (key) DO UPDATE SET value = $v", ("$v", rev.ToString()));
            }
            if (anyChanged || lines.Count > 0 || !known)
                Save(db, known ? state : null, work);
            var written = lines.Select(l => AddActivity(db, l.Ctx.NowIso, device, l.Item.Kind, work.Job, work.LoadId, work.LoadNumber, l.Item.StackId, l.Item.StackName, l.Item.Mark, l.Item.Text)).ToList();
            tx.Commit();
            loads[loadId] = work;
            return (work, anyChanged, written, results);
        }
        catch
        {
            // The database write failed: what is in memory may be ahead of it, so read it again next time.
            loads.Remove(loadId);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The load number of another load of the job that has a piece with this code, or null.</summary>
    static string? CodeOnOtherLoad(SqliteConnection db, string job, string loadId, string code) =>
        Scalar(db, """
            SELECT l.load_number FROM entries e JOIN loads l ON l.load_id = e.load_id
            WHERE e.code = $code COLLATE NOCASE AND e.load_id <> $load AND l.job = $job COLLATE NOCASE LIMIT 1
            """, ("$code", code), ("$load", loadId), ("$job", job)) as string;

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
        s.Stacks = Rows(db, "SELECT id, name, created_by, pallet FROM stacks WHERE load_id = $id ORDER BY position",
            r => new Stack
            {
                Id = r.GetString(0),
                Name = r.GetString(1),
                CreatedBy = r.IsDBNull(2) ? null : r.GetString(2),
                Pallet = r.IsDBNull(3) ? null : JsonSerializer.Deserialize<Pallet>(r.GetString(3), Json.Options),
            }, ("$id", loadId));
        var byId = s.Stacks.ToDictionary(st => st.Id);
        foreach (var (stackId, entry) in Rows(db, """
            SELECT stack_id, id, mark, sequence, manual, pending, removing, scanned_at, scanned_by, added_at, rev, details, code
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
            Code = r.IsDBNull(12) ? null : r.GetString(12),
        }), ("$id", loadId)))
        {
            if (byId.TryGetValue(stackId, out var stack))
                stack.Items.Add(entry);
        }
        if (s.Stacks.Count == 0)
            s.Stacks.Add(new Stack { Id = LoadOps.DefaultStackId(loadId), Name = $"Stack {s.LoadNumber} - 1" });
        if (s.TruckCheck is not null)
            s.TruckCheck.Scans = Rows(db, "SELECT id, mark, manual, scanned_at, scanned_by, qty, code FROM check_scans WHERE load_id = $id ORDER BY position",
                r => new CheckScan { Id = r.GetString(0), Mark = r.GetString(1), Manual = r.GetInt64(2) != 0, ScannedAt = r.GetString(3), ScannedBy = r.IsDBNull(4) ? null : r.GetString(4), Qty = (int)Math.Max(1, r.GetInt64(5)), Code = r.IsDBNull(6) ? null : r.GetString(6) },
                ("$id", loadId));
        return s;
    }

    /// <summary>
    /// Writes a load: only what changed since <paramref name="before"/> (the load as the database has
    /// it), or everything when <paramref name="before"/> is null. A scan touches one row, so a load
    /// with thousands of pieces costs no more to scan into than an empty one.
    /// </summary>
    static void Save(SqliteConnection db, LoadState? before, LoadState s)
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

        // Stacks: a handful per load, written again whenever any of them changed.
        static string StackKey(Stack st) => $"{st.Id}\u0001{st.Name}\u0001{st.CreatedBy}\u0001{(st.Pallet is null ? "" : JsonSerializer.Serialize(st.Pallet, Json.Options))}";
        if (before is null || !before.Stacks.Select(StackKey).SequenceEqual(s.Stacks.Select(StackKey)))
        {
            Exec(db, "DELETE FROM stacks WHERE load_id = $id", ("$id", s.LoadId));
            for (var si = 0; si < s.Stacks.Count; si++)
            {
                var st = s.Stacks[si];
                Exec(db, "INSERT INTO stacks (id, load_id, position, name, created_by, pallet) VALUES ($id, $load, $pos, $name, $by, $pallet)",
                    ("$id", st.Id), ("$load", s.LoadId), ("$pos", si), ("$name", st.Name), ("$by", st.CreatedBy),
                    ("$pallet", st.Pallet is null ? null : JsonSerializer.Serialize(st.Pallet, Json.Options)));
            }
        }

        // Rows: the ones added, changed or moved are written, the ones gone are deleted.
        var had = new Dictionary<string, (string StackId, int Position, Entry Entry)>();
        if (before is null)
            Exec(db, "DELETE FROM entries WHERE load_id = $id", ("$id", s.LoadId));
        else
            foreach (var st in before.Stacks)
                for (var i = 0; i < st.Items.Count; i++)
                    had[st.Items[i].Id] = (st.Id, i, st.Items[i]);
        using (var upsert = Prepare(db, """
            INSERT INTO entries (id, load_id, stack_id, position, mark, sequence, manual, pending, removing, scanned_at, scanned_by, added_at, rev, details, code)
            VALUES ($id, $load, $stack, $pos, $mark, $seq, $manual, $pending, $removing, $sat, $sby, $added, $rev, $details, $code)
            ON CONFLICT (load_id, id) DO UPDATE SET stack_id = $stack, position = $pos, mark = $mark, sequence = $seq, manual = $manual,
                pending = $pending, removing = $removing, scanned_at = $sat, scanned_by = $sby, added_at = $added, rev = $rev, details = $details, code = $code
            """, "$id", "$load", "$stack", "$pos", "$mark", "$seq", "$manual", "$pending", "$removing", "$sat", "$sby", "$added", "$rev", "$details", "$code"))
        {
            foreach (var st in s.Stacks)
                for (var i = 0; i < st.Items.Count; i++)
                {
                    var e = st.Items[i];
                    if (had.Remove(e.Id, out var old) && old.StackId == st.Id && old.Position == i && SameRow(old.Entry, e))
                        continue;
                    Run(upsert, e.Id, s.LoadId, st.Id, i, e.Mark, e.Sequence, e.Manual ? 1 : 0, e.Pending ? 1 : 0, e.Removing ? 1 : 0,
                        e.ScannedAt, e.ScannedBy, e.AddedAt, e.Rev, e.Details is null ? null : JsonSerializer.Serialize(e.Details), e.Code);
                }
        }
        if (had.Count > 0)
        {
            using var delete = Prepare(db, "DELETE FROM entries WHERE load_id = $load AND id = $id", "$load", "$id");
            foreach (var id in had.Keys)
                Run(delete, s.LoadId, id);
        }

        // Truck check scans: the same.
        var hadScans = new Dictionary<string, (int Position, CheckScan Scan)>();
        if (before is null)
            Exec(db, "DELETE FROM check_scans WHERE load_id = $id", ("$id", s.LoadId));
        else
            for (var i = 0; i < (before.TruckCheck?.Scans.Count ?? 0); i++)
                hadScans[before.TruckCheck!.Scans[i].Id] = (i, before.TruckCheck.Scans[i]);
        var scans = s.TruckCheck?.Scans ?? [];
        using (var upsert = Prepare(db, """
            INSERT INTO check_scans (id, load_id, position, mark, manual, scanned_at, scanned_by, qty, code)
            VALUES ($id, $load, $pos, $mark, $manual, $at, $by, $qty, $code)
            ON CONFLICT (load_id, id) DO UPDATE SET position = $pos, mark = $mark, manual = $manual, scanned_at = $at, scanned_by = $by, qty = $qty, code = $code
            """, "$id", "$load", "$pos", "$mark", "$manual", "$at", "$by", "$qty", "$code"))
        {
            for (var i = 0; i < scans.Count; i++)
            {
                var c = scans[i];
                if (hadScans.Remove(c.Id, out var old) && old.Position == i && old.Scan.Mark == c.Mark && old.Scan.Qty == c.Qty
                    && old.Scan.Code == c.Code && old.Scan.ScannedBy == c.ScannedBy && old.Scan.ScannedAt == c.ScannedAt && old.Scan.Manual == c.Manual)
                    continue;
                Run(upsert, c.Id, s.LoadId, i, c.Mark, c.Manual ? 1 : 0, c.ScannedAt, c.ScannedBy, Math.Max(1, c.Qty), c.Code);
            }
        }
        if (hadScans.Count > 0)
        {
            using var delete = Prepare(db, "DELETE FROM check_scans WHERE load_id = $load AND id = $id", "$load", "$id");
            foreach (var id in hadScans.Keys)
                Run(delete, s.LoadId, id);
        }
    }

    // Whether a row is unchanged. Its details only ever change together with its revision.
    static bool SameRow(Entry a, Entry b) =>
        a.Rev == b.Rev && a.Mark == b.Mark && a.Sequence == b.Sequence && a.Manual == b.Manual && a.Pending == b.Pending
        && a.Removing == b.Removing && a.ScannedAt == b.ScannedAt && a.ScannedBy == b.ScannedBy && a.AddedAt == b.AddedAt && a.Code == b.Code;

    // ---- Piece codes ---------------------------------------------------------------------

    /// <summary>A job's piece code settings (off for a job never set up).</summary>
    public PieceCodeSettings CodeSettings(string job)
    {
        using var db = Open();
        var json = Scalar(db, "SELECT piece_codes FROM job_settings WHERE job = $job", ("$job", job)) as string;
        return json is null ? PieceCodeSettings.Off : JsonSerializer.Deserialize<PieceCodeSettings>(json, Json.Options) ?? PieceCodeSettings.Off;
    }

    /// <summary>Every job with piece codes switched on, or with codes on file.</summary>
    public List<(string Job, PieceCodeSettings Settings, int Codes)> CodeJobs()
    {
        using var db = Open();
        var counts = Rows(db, "SELECT job, COUNT(*) FROM piece_codes GROUP BY job", r => (r.GetString(0), r.GetInt32(1)))
            .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);
        var settings = Rows(db, "SELECT job, piece_codes FROM job_settings WHERE piece_codes IS NOT NULL", r => (r.GetString(0), r.GetString(1)))
            .ToDictionary(x => x.Item1, x => JsonSerializer.Deserialize<PieceCodeSettings>(x.Item2, Json.Options) ?? PieceCodeSettings.Off, StringComparer.OrdinalIgnoreCase);
        return settings.Keys.Union(counts.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(job => (job, settings.GetValueOrDefault(job) ?? PieceCodeSettings.Off, counts.GetValueOrDefault(job)))
            .OrderBy(x => x.job, NumericOrder.Instance).ToList();
    }

    public PieceCodeSettings SetCodeSettings(string job, PieceCodeSettings wanted, Device by)
    {
        var settings = wanted with { Pattern = PieceCodeSettings.CheckPattern(wanted.Pattern) };
        using var db = Open();
        var before = CodeSettings(job);
        Exec(db, """
            INSERT INTO job_settings (job, piece_codes, updated_at) VALUES ($job, $json, $at)
            ON CONFLICT (job) DO UPDATE SET piece_codes = $json, updated_at = $at
            """, ("$job", job), ("$json", JsonSerializer.Serialize(settings, Json.Options)), ("$at", DateTime.UtcNow.ToString("o")));
        if (before != settings)
            AddActivity(db, DateTime.UtcNow.ToString("o"), by, "codes-settings", job, null, null, null, null, null,
                settings.Enabled
                    ? $"Piece codes on for job {job} (pattern {settings.Pattern}; codes not on the list are {(settings.Learn ? "paired on the iPad" : "refused")})"
                    : $"Piece codes off for job {job}");
        return settings;
    }

    public int CodeCount(string job)
    {
        using var db = Open();
        return Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM piece_codes WHERE job = $job", ("$job", job)) ?? 0);
    }

    /// <summary>A job's codes added after <paramref name="since"/> (a code's id), oldest first.</summary>
    public List<PieceCode> Codes(string job, long since = 0, int limit = 100_000)
    {
        using var db = Open();
        return Rows(db, "SELECT id, code, mark, sequence, source, added_at, added_by FROM piece_codes WHERE job = $job AND id > $since ORDER BY id LIMIT $limit",
            ReadCode, ("$job", job), ("$since", since), ("$limit", Math.Clamp(limit, 1, 100_000)));
    }

    public PieceCode? FindCode(string job, string code)
    {
        using var db = Open();
        return Rows(db, "SELECT id, code, mark, sequence, source, added_at, added_by FROM piece_codes WHERE job = $job AND code = $code",
            ReadCode, ("$job", job), ("$code", code.Trim())).FirstOrDefault();
    }

    static PieceCode ReadCode(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), N(r, 3), r.GetString(4), r.GetString(5), N(r, 6));

    /// <summary>
    /// Adds codes to a job. A code already there with the same mark is left as it is (its sequence is
    /// filled in if it had none). One already there with another mark is a conflict, left as it is,
    /// unless <paramref name="overwrite"/> (the admin board's import) moves it to the new mark.
    /// A changed or new code gets a new id, so the iPads' "since" read picks it up.
    /// </summary>
    public CodesAdded AddCodes(string job, IReadOnlyList<CodeLine> lines, string source, Device by, bool overwrite)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var now = DateTime.UtcNow.ToString("o");
        int added = 0, updated = 0, unchanged = 0;
        var conflicts = new List<CodeConflict>();
        using var find = Prepare(db, "SELECT mark, sequence FROM piece_codes WHERE job = $job AND code = $code", "$job", "$code");
        using var delete = Prepare(db, "DELETE FROM piece_codes WHERE job = $job AND code = $code", "$job", "$code");
        using var insert = Prepare(db, "INSERT INTO piece_codes (job, code, mark, sequence, source, added_at, added_by) VALUES ($job, $code, $mark, $seq, $source, $at, $by)",
            "$job", "$code", "$mark", "$seq", "$source", "$at", "$by");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var code = line.Code.Trim();
            var mark = CodeList.CleanMark(line.Mark);
            if (code == "" || mark == "" || !seen.Add(code))
                continue;
            find.Parameters[0].Value = job;
            find.Parameters[1].Value = code;
            string? oldMark = null, oldSeq = null;
            using (var r = find.ExecuteReader())
                if (r.Read())
                {
                    oldMark = r.GetString(0);
                    oldSeq = N(r, 1);
                }
            if (oldMark is not null)
            {
                var sameMark = oldMark.Equals(mark, StringComparison.OrdinalIgnoreCase);
                if (!sameMark && !overwrite)
                {
                    conflicts.Add(new CodeConflict(code, oldMark, mark));
                    continue;
                }
                if (sameMark && (line.Sequence is null || line.Sequence == oldSeq))
                {
                    unchanged++;
                    continue;
                }
                Run(delete, job, code);
                Run(insert, job, code, mark, line.Sequence ?? (sameMark ? oldSeq : null), source, now, by.Id);
                updated++;
                continue;
            }
            Run(insert, job, code, mark, line.Sequence, source, now, by.Id);
            added++;
        }
        var count = added + updated;
        if (count > 0 || conflicts.Count > 0)
            AddActivity(db, now, by, source == "paired" ? "code-pair" : "codes-import", job, null, null, null, null,
                source == "paired" && lines.Count == 1 ? lines[0].Mark : null,
                source == "paired" && lines.Count == 1 && count == 1
                    ? $"Paired code {lines[0].Code.Trim()} with {CodeList.CleanMark(lines[0].Mark)}"
                    : $"Piece codes for job {job}: {added} added, {updated} changed, {unchanged} already there{(conflicts.Count > 0 ? $", {conflicts.Count} paired with another mark (left as they were)" : "")}");
        tx.Commit();
        var fresh = count == 0 ? [] : Rows(db, "SELECT id, code, mark, sequence, source, added_at, added_by FROM piece_codes WHERE job = $job AND added_at = $at ORDER BY id",
            ReadCode, ("$job", job), ("$at", now));
        return new CodesAdded(added, updated, unchanged, conflicts, fresh);
    }

    /// <summary>Removes every code of a job (rows already loaded keep the code they were scanned with).</summary>
    public int ClearCodes(string job, Device by)
    {
        using var db = Open();
        var n = Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM piece_codes WHERE job = $job", ("$job", job)) ?? 0);
        Exec(db, "DELETE FROM piece_codes WHERE job = $job", ("$job", job));
        if (n > 0)
            AddActivity(db, DateTime.UtcNow.ToString("o"), by, "codes-clear", job, null, null, null, null, null, $"Removed all {n} piece codes of job {job}");
        return n;
    }

    /// <summary>Where a code is now: the load and mark of the row that has it, if any.</summary>
    public (string LoadId, string LoadNumber, string Mark)? CodeOnLoad(string job, string code)
    {
        using var db = Open();
        return Rows(db, """
            SELECT e.load_id, l.load_number, e.mark FROM entries e JOIN loads l ON l.load_id = e.load_id
            WHERE e.code = $code COLLATE NOCASE AND l.job = $job COLLATE NOCASE LIMIT 1
            """, r => (r.GetString(0), r.GetString(1), r.GetString(2)), ("$code", code.Trim()), ("$job", job)).Select(x => ((string, string, string)?)x).FirstOrDefault();
    }

    // ---- Devices -----------------------------------------------------------------------

    /// <summary>
    /// Records that a device was seen, creating it with a starting name and colour the first time.
    /// Returns it and whether it is new.
    /// </summary>
    /// An id that was merged into another device (an old install of the same iPad) is that device:
    /// it is never made again.
    public (Device Device, bool Created) Touch(string id, string? ip, string? userAgent, string? appVersion)
    {
        using var db = Open();
        var now = DateTime.UtcNow.ToString("o");
        id = Resolve(db, id);
        var existing = GetDevice(db, id);
        if (existing is null)
        {
            // One statement, so two new iPads at once can't get the same number (and the same iPad's
            // first requests arriving together make it once).
            Exec(db, """
                INSERT INTO devices (id, number, name, color, created_at, last_seen_at, last_ip, user_agent, app_version)
                SELECT $id, n, 'User ' || n, '', $now, $now, $ip, $ua, $v FROM (SELECT COALESCE(MAX(number), 0) + 1 AS n FROM devices) WHERE true
                ON CONFLICT (id) DO NOTHING
                """, ("$id", id), ("$now", now), ("$ip", ip), ("$ua", Trim(userAgent, 300)), ("$v", Trim(appVersion, 30)));
            var made = GetDevice(db, id)!;
            if (made.Color == "")
            {
                Exec(db, "UPDATE devices SET color = $color WHERE id = $id", ("$id", id), ("$color", Palette[(made.Number - 1) % Palette.Length]));
                made = made with { Color = Palette[(made.Number - 1) % Palette.Length] };
            }
            return (made, true);
        }
        Exec(db, "UPDATE devices SET last_seen_at = $now, last_ip = $ip, user_agent = COALESCE($ua, user_agent), app_version = COALESCE($v, app_version) WHERE id = $id",
            ("$id", id), ("$now", now), ("$ip", ip), ("$ua", Trim(userAgent, 300)), ("$v", Trim(appVersion, 30)));
        return (existing with { LastSeenAt = now, LastIp = ip }, false);
    }

    public Device? GetDevice(string id)
    {
        using var db = Open();
        return GetDevice(db, Resolve(db, id));
    }

    /// <summary>The device id an id stands for now: itself, or the device it was merged into.</summary>
    public string ResolveDeviceId(string id)
    {
        using var db = Open();
        return Resolve(db, id);
    }

    /// <summary>The device an id stands for now: itself, or the device it was merged into.</summary>
    static string Resolve(SqliteConnection db, string id) =>
        Scalar(db, "SELECT device_id FROM device_aliases WHERE old_id = $id", ("$id", id)) as string ?? id;

    /// <summary>Every device's old ids (merged into it), by device id.</summary>
    public Dictionary<string, List<string>> Aliases()
    {
        using var db = Open();
        return Rows(db, "SELECT device_id, old_id FROM device_aliases ORDER BY merged_at", r => (Id: r.GetString(0), Old: r.GetString(1)))
            .GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.Select(a => a.Old).ToList());
    }

    /// <summary>
    /// The same iPad seen as two devices (Safari and the home screen app, or the app installed again,
    /// each start with their own storage, so their own id): <paramref name="fromId"/> becomes
    /// <paramref name="intoId"/>. Its scans, stacks, truck check scans, history and photo records move
    /// to that device, its id is kept as an alias (an app still using it is that device from now on)
    /// and its record goes. The device kept keeps its name, colour and number.
    /// </summary>
    public async Task<Device> MergeDeviceAsync(string fromId, string intoId, Device by)
    {
        await gate.WaitAsync();
        try
        {
            using var db = Open();
            fromId = Resolve(db, fromId);
            intoId = Resolve(db, intoId);
            var from = GetDevice(db, fromId) ?? throw new KeyNotFoundException($"No device {fromId}.");
            var into = GetDevice(db, intoId) ?? throw new KeyNotFoundException($"No device {intoId}.");
            if (from.Id == into.Id)
                throw new OpException("That is the same device.");
            var now = DateTime.UtcNow.ToString("o");
            using var tx = db.BeginTransaction();
            (string, object?)[] ids = [("$from", from.Id), ("$into", into.Id)];
            Exec(db, "UPDATE activity SET device_id = $into WHERE device_id = $from", ids);
            Exec(db, "UPDATE photos SET device_id = $into, device_number = $num WHERE device_id = $from", [.. ids, ("$num", into.Number)]);
            Exec(db, "UPDATE stacks SET created_by = $into WHERE created_by = $from", ids);
            Exec(db, "UPDATE entries SET scanned_by = $into WHERE scanned_by = $from", ids);
            Exec(db, "UPDATE check_scans SET scanned_by = $into WHERE scanned_by = $from", ids);
            Exec(db, "UPDATE device_aliases SET device_id = $into WHERE device_id = $from", ids);
            Exec(db, "INSERT INTO device_aliases (old_id, device_id, merged_at) VALUES ($from, $into, $now) ON CONFLICT (old_id) DO UPDATE SET device_id = $into, merged_at = $now",
                [.. ids, ("$now", now)]);
            // The kept device was seen when either was; its app version and address are the newest one's.
            if (string.CompareOrdinal(from.LastSeenAt, into.LastSeenAt) > 0)
                Exec(db, "UPDATE devices SET last_seen_at = $seen, last_ip = $ip, user_agent = COALESCE($ua, user_agent), app_version = COALESCE($v, app_version) WHERE id = $into",
                    [.. ids, ("$seen", from.LastSeenAt), ("$ip", from.LastIp), ("$ua", from.UserAgent), ("$v", from.AppVersion)]);
            if (string.CompareOrdinal(from.CreatedAt, into.CreatedAt) < 0)
                Exec(db, "UPDATE devices SET created_at = $at WHERE id = $into", [.. ids, ("$at", from.CreatedAt)]);
            Exec(db, "DELETE FROM devices WHERE id = $from", ids);
            AddActivity(db, now, by, "device-merge", null, null, null, null, null, null,
                $"Merged {from.Name} ({from.User}) into {into.Name} ({into.User}): the same iPad");
            tx.Commit();
            // Loads in memory still name the old id: read them again from the database when next used.
            loads.Clear();
            return GetDevice(db, into.Id)!;
        }
        finally
        {
            gate.Release();
        }
    }

    static Device? GetDevice(SqliteConnection db, string id) =>
        Rows(db, "SELECT id, name, color, created_at, last_seen_at, last_ip, user_agent, app_version, number FROM devices WHERE id = $id", ReadDevice, ("$id", id)).FirstOrDefault();

    public List<Device> Devices()
    {
        using var db = Open();
        return Rows(db, "SELECT id, name, color, created_at, last_seen_at, last_ip, user_agent, app_version, number FROM devices ORDER BY last_seen_at DESC", ReadDevice);
    }

    static Device ReadDevice(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? 0 : r.GetInt32(8));

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

    /// <param name="takenAt">When the iPad took it (kept in taken_at, so "newest first" is by when it was taken, not uploaded).</param>
    public void AddPhoto(string job, string loadNumber, string fileName, string? mark, Device device, long bytes, DateTime? takenAt = null)
    {
        using var db = Open();
        Exec(db, "INSERT INTO photos (job, load_number, file_name, mark, device_id, device_name, device_number, bytes, taken_at) VALUES ($job, $load, $file, $mark, $dev, $name, $num, $bytes, $at)",
            ("$job", job), ("$load", loadNumber), ("$file", fileName), ("$mark", mark), ("$dev", device.Id), ("$name", device.Name), ("$num", device.Number), ("$bytes", bytes), ("$at", (takenAt ?? DateTime.UtcNow).ToUniversalTime().ToString("o")));
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
        return Rows(db, "SELECT p.id, p.job, p.load_number, p.file_name, p.mark, p.device_id, p.device_name, p.bytes, p.taken_at, COALESCE(p.device_number, d.number) FROM photos p LEFT JOIN devices d ON d.id = p.device_id WHERE p.job = $job AND p.load_number = $load AND p.deleted_at IS NULL",
                ReadPhoto,
                ("$job", job), ("$load", loadNumber))
            .GroupBy(p => p.FileName).ToDictionary(g => g.Key, g => g.Last());
    }

    /// <summary>Every job, and every load of a job, the database has a photo for (the photo explorer's starting points).</summary>
    public List<string> PhotoJobs()
    {
        using var db = Open();
        return Rows(db, "SELECT DISTINCT job FROM photos WHERE deleted_at IS NULL", r => r.GetString(0));
    }

    public List<string> PhotoLoads(string job)
    {
        using var db = Open();
        return Rows(db, "SELECT DISTINCT load_number FROM photos WHERE job = $job AND deleted_at IS NULL", r => r.GetString(0), ("$job", job));
    }

    /// <summary>Everything the database knows about every photo not deleted, by "job|load|file" (any case).</summary>
    public Dictionary<string, PhotoMeta> AllPhotos()
    {
        using var db = Open();
        var all = new Dictionary<string, PhotoMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Rows(db, "SELECT p.id, p.job, p.load_number, p.file_name, p.mark, p.device_id, p.device_name, p.bytes, p.taken_at, COALESCE(p.device_number, d.number) FROM photos p LEFT JOIN devices d ON d.id = p.device_id WHERE p.deleted_at IS NULL ORDER BY p.id", ReadPhoto))
            all[$"{p.Job}|{p.LoadNumber}|{p.FileName}"] = p;
        return all;
    }

    static PhotoMeta ReadPhoto(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), N(r, 4), N(r, 5), N(r, 6), r.GetInt64(7), r.GetString(8), r.IsDBNull(9) ? null : r.GetInt32(9));

    public List<PhotoMeta> RecentPhotos(string? deviceId, int limit)
    {
        using var db = Open();
        return Rows(db, $"SELECT p.id, p.job, p.load_number, p.file_name, p.mark, p.device_id, p.device_name, p.bytes, p.taken_at, COALESCE(p.device_number, d.number) FROM photos p LEFT JOIN devices d ON d.id = p.device_id WHERE p.deleted_at IS NULL {(deviceId is null ? "" : "AND p.device_id = $dev")} ORDER BY p.taken_at DESC, p.id DESC LIMIT $limit",
            ReadPhoto,
            ("$dev", deviceId), ("$limit", Math.Clamp(limit, 1, 200)));
    }

    // ---- Admin page: Server view ------------------------------------------------------

    /// <summary>
    /// What the database holds and how busy the iPads have been: row counts, scans and refusals per
    /// day for the last 14 days, what kinds of things happened in the last 7, and each iPad's scans.
    /// Days are the server's local days.
    /// </summary>
    public object Stats()
    {
        using var db = Open();
        long Count(string sql) => Convert.ToInt64(Scalar(db, sql) ?? 0L);
        var since14 = DateTime.UtcNow.Date.AddDays(-14).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var since7 = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var today = DateTime.Today.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        return new
        {
            counts = new
            {
                devices = Count("SELECT COUNT(*) FROM devices"),
                jobs = Count("SELECT COUNT(DISTINCT job) FROM loads"),
                loads = Count("SELECT COUNT(*) FROM loads"),
                stacks = Count("SELECT COUNT(*) FROM stacks"),
                pieces = Count("SELECT COUNT(*) FROM entries"),
                checkScans = Count("SELECT COUNT(*) FROM check_scans"),
                activity = Count("SELECT COUNT(*) FROM activity"),
                photos = Count("SELECT COUNT(*) FROM photos WHERE deleted_at IS NULL"),
                photoBytes = Count("SELECT COALESCE(SUM(bytes), 0) FROM photos WHERE deleted_at IS NULL"),
            },
            today = new
            {
                scans = Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM activity WHERE kind = 'scan' AND at >= $t", ("$t", today)) ?? 0L),
                refused = Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM activity WHERE kind = 'refused' AND at >= $t", ("$t", today)) ?? 0L),
                photos = Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM photos WHERE taken_at >= $t", ("$t", today)) ?? 0L),
                checks = Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM activity WHERE kind = 'check' AND at >= $t", ("$t", today)) ?? 0L),
            },
            days = Rows(db, """
                SELECT date(at, 'localtime') AS day,
                       SUM(kind = 'scan'), SUM(kind = 'refused'), SUM(kind = 'check'), SUM(kind = 'photo')
                FROM activity WHERE at >= $since GROUP BY day ORDER BY day
                """, r => new { day = r.GetString(0), scans = r.GetInt64(1), refused = r.GetInt64(2), checks = r.GetInt64(3), photos = r.GetInt64(4) },
                ("$since", since14)),
            kinds = Rows(db, "SELECT kind, COUNT(*) FROM activity WHERE at >= $since GROUP BY kind ORDER BY COUNT(*) DESC",
                r => new { kind = r.GetString(0), count = r.GetInt64(1) }, ("$since", since7)),
            byDevice = Rows(db, """
                SELECT device_id, MAX(device_name), SUM(at >= $today), COUNT(*), MAX(at)
                FROM activity WHERE kind = 'scan' AND at >= $since AND device_id IS NOT NULL
                GROUP BY device_id ORDER BY COUNT(*) DESC
                """, r => new { deviceId = r.GetString(0), deviceName = N(r, 1), today = r.GetInt64(2), week = r.GetInt64(3), lastAt = N(r, 4) },
                ("$since", since7), ("$today", today)),
        };
    }

    /// <summary>A consistent copy of the whole database, taken while it is in use.</summary>
    public void Backup(string targetPath)
    {
        using var db = Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = targetPath, Pooling = false }.ToString());
        target.Open();
        db.BackupDatabase(target);
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

    // A statement run many times with different values (in the order its parameters are named).
    static SqliteCommand Prepare(SqliteConnection db, string sql, params string[] names)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var name in names)
            cmd.Parameters.Add(new SqliteParameter { ParameterName = name });
        return cmd;
    }

    static void Run(SqliteCommand cmd, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
            cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
        cmd.ExecuteNonQuery();
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
