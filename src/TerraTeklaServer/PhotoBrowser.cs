namespace TerraTeklaServer;

/// <summary>A job that has photos, for the photo explorer.</summary>
public sealed record PhotoJob(string Job, string Description, int Loads, int Photos, DateTime? LastAt, string Folder);

/// <summary>A load that has photos.</summary>
public sealed record PhotoLoad(string Load, int Photos, DateTime? LastAt, string Folder);

/// <summary>
/// The photo explorer: which jobs and loads have photos, read from the photo folders themselves, so
/// photos from before this server (or copied in by hand) show too. A job is looked for when PowerFab
/// lists it, the database has a photo or stack for it, or PHOTOS_ROOT has its folder (the default
/// layout). Its loads are the folders in its job folder that fit the load folder pattern, plus any
/// the database knows. The job overview is kept for a short while, since walking every job folder
/// costs a little and anyone may ask for it.
/// </summary>
public sealed class PhotoBrowser(PhotoStore photos, Store store, ILogger<PhotoBrowser> log)
{
    static readonly TimeSpan OverviewFor = TimeSpan.FromSeconds(20);
    static readonly TimeSpan PowerFabJobsFor = TimeSpan.FromMinutes(10);
    static readonly TimeSpan PowerFabFailureFor = TimeSpan.FromMinutes(1);

    readonly SemaphoreSlim gate = new(1, 1);
    (DateTime At, List<PhotoJob> Jobs)? overview;
    (DateTime Until, Dictionary<string, string> Jobs)? powerFab; // job -> description

    /// <summary>Set at startup: reads PowerFab's job list (number, description), or null when it can't.</summary>
    public Func<CancellationToken, Task<List<(string Number, string Description)>?>>? ReadPowerFabJobs { get; set; }

    /// <summary>Forget the overview (a photo was taken or deleted).</summary>
    public void Changed() => overview = null;

    public async Task<List<PhotoJob>> JobsAsync(CancellationToken ct)
    {
        if (overview is { } o && DateTime.UtcNow - o.At < OverviewFor)
            return o.Jobs;
        await gate.WaitAsync(ct);
        try
        {
            if (overview is { } again && DateTime.UtcNow - again.At < OverviewFor)
                return again.Jobs;
            var descriptions = await PowerFabJobsAsync(ct);
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            candidates.UnionWith(descriptions.Keys);
            candidates.UnionWith(store.PhotoJobs());
            candidates.UnionWith(store.Jobs().Select(j => j.Job));
            try
            {
                candidates.UnionWith(photos.JobsUnderRoot());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PhotoRulesException)
            {
                log.LogWarning("Couldn't look through PHOTOS_ROOT: {Error}", ex.Message);
            }

            var jobs = new List<PhotoJob>();
            foreach (var job in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var loads = Loads(job);
                var count = loads.Sum(l => l.Photos);
                if (count == 0)
                    continue;
                descriptions.TryGetValue(job, out var description);
                jobs.Add(new PhotoJob(job, description ?? "", loads.Count(l => l.Photos > 0), count, loads.Max(l => l.LastAt), FolderOf(job)));
            }
            jobs.Sort((a, b) => NumericOrder.Instance.Compare(b.Job, a.Job));
            overview = (DateTime.UtcNow, jobs);
            return jobs;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A job's loads that have photos, newest load number first.</summary>
    public List<PhotoLoad> Loads(string job)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (load, folder) in photos.LoadsIn(job))
                found[load] = folder;
            foreach (var load in store.PhotoLoads(job))
                if (!found.ContainsKey(load))
                    found[load] = photos.LoadFolder(job, load);
        }
        catch (Exception ex) when (ex is BadNameException or PhotoRulesException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var loads = new List<PhotoLoad>();
        foreach (var (load, folder) in found)
        {
            var (count, last) = Count(folder);
            if (count > 0)
                loads.Add(new PhotoLoad(load, count, last, folder));
        }
        loads.Sort((a, b) => NumericOrder.Instance.Compare(b.Load, a.Load));
        return loads;
    }

    public string FolderOf(string job)
    {
        try
        {
            return photos.JobFolder(job).Folder;
        }
        catch (Exception ex) when (ex is BadNameException or PhotoRulesException)
        {
            return "";
        }
    }

    static (int Count, DateTime? Last) Count(string folder)
    {
        var dir = new DirectoryInfo(folder);
        if (!dir.Exists)
            return (0, null);
        int count = 0;
        DateTime? last = null;
        try
        {
            foreach (var f in dir.EnumerateFiles())
            {
                if (f.Name.StartsWith('.'))
                    continue;
                count++;
                if (last is null || f.LastWriteTimeUtc > last)
                    last = f.LastWriteTimeUtc;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return (count, last);
    }

    async Task<Dictionary<string, string>> PowerFabJobsAsync(CancellationToken ct)
    {
        if (powerFab is { } cached && DateTime.UtcNow < cached.Until)
            return cached.Jobs;
        List<(string Number, string Description)>? list = null;
        try
        {
            list = ReadPowerFabJobs is null ? null : await ReadPowerFabJobs(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Couldn't read PowerFab's job list for the photo explorer: {Error}", ex.Message);
        }
        var jobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (number, description) in list ?? [])
            jobs[number] = description;
        // A failure is kept for a shorter while (with whatever was known before), so PowerFab isn't asked on every look.
        if (list is null && powerFab is { } old)
            jobs = old.Jobs;
        powerFab = (DateTime.UtcNow + (list is null ? PowerFabFailureFor : PowerFabJobsFor), jobs);
        return jobs;
    }
}
