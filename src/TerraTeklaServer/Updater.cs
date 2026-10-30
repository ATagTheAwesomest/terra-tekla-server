using System.Diagnostics;

namespace TerraTeklaServer;

/// <summary>An update can't run here (not Windows, a script or setting missing, one already running).</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Runs the update scripts when asked from the admin page or the iPad app (behind the admin code):
/// <list type="bullet">
/// <item><b>iPad app</b>: update-client.ps1 (shipped next to the exe) gets the latest
/// terra-tekla-client into CLIENT_ROOT. The server keeps running; its output is kept to show.</item>
/// <item><b>Server</b>: deploy.ps1 from the server's git clone (<c>SERVER_REPO</c>) pulls, builds,
/// stops this service, copies the build, starts it again and runs update-client.ps1. It runs on its
/// own (this process is stopped half way), writing to DATA_DIR\deploy-last.log, which the next
/// start shows.</item>
/// </list>
/// Windows only, one at a time.
/// </summary>
public sealed class Updater(Settings settings, SettingsFile file, ILogger<Updater> log)
{
    public const string ServiceName = "TerraTeklaServer";
    readonly object gate = new();
    Run? client;

    public sealed class Run
    {
        public DateTime StartedAt { get; init; }
        public DateTime? FinishedAt { get; set; }
        public int? ExitCode { get; set; }
        public List<string> Output { get; } = [];
        public string? VersionBefore { get; init; }
        public string? VersionAfter { get; set; }
        public bool Running => FinishedAt is null;
    }

    /// <summary>Raised when an iPad app update finished and changed the version.</summary>
    public event Action<string?>? ClientUpdated;

    public string ClientScript => Path.Combine(AppContext.BaseDirectory, "update-client.ps1");

    public string? ServerRepo => file.Current("SERVER_REPO") is { Length: > 0 } repo ? repo.Trim() : null;

    public string DeployLog => Path.Combine(settings.DataDir, "deploy-last.log");

    public object Status(Func<string?> clientVersion)
    {
        Run? c;
        lock (gate)
            c = client;
        var deploy = new FileInfo(DeployLog);
        return new
        {
            supported = OperatingSystem.IsWindows(),
            service = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService(),
            clientRoot = settings.ClientRoot,
            clientVersion = clientVersion(),
            clientScript = File.Exists(ClientScript),
            serverRepo = ServerRepo,
            serverScript = ServerRepo is { } repo && File.Exists(Path.Combine(repo, "windows", "deploy.ps1")),
            client = c is null ? null : new
            {
                startedAt = c.StartedAt, finishedAt = c.FinishedAt, exitCode = c.ExitCode, running = c.Running,
                versionBefore = c.VersionBefore, versionAfter = c.VersionAfter,
                output = Lines(c),
            },
            server = deploy.Exists ? new
            {
                startedAt = deploy.CreationTimeUtc,
                changedAt = deploy.LastWriteTimeUtc,
                output = ReadTail(deploy.FullName, 120),
            } : null,
        };
    }

    /// <summary>Starts update-client.ps1 and returns at once; <see cref="Status"/> follows it.</summary>
    public void UpdateClient(Func<string?> clientVersion)
    {
        if (!OperatingSystem.IsWindows())
            throw new UpdateException("Updates run only on the Windows server.");
        if (settings.ClientRoot == "")
            throw new UpdateException("There is no iPad app folder (CLIENT_ROOT) to update. Set it in Settings first.");
        if (!File.Exists(ClientScript))
            throw new UpdateException($"{ClientScript} is missing. Run deploy.ps1 once by hand to put it there.");
        lock (gate)
        {
            if (client is { Running: true })
                throw new UpdateException("The iPad app is already being updated.");
            client = new Run { StartedAt = DateTime.UtcNow, VersionBefore = clientVersion() };
        }
        var run = client;
        var process = new Process { StartInfo = PowerShell("-File", Quote(ClientScript), "-ClientRoot", Quote(settings.ClientRoot)), EnableRaisingEvents = true };
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.OutputDataReceived += (_, e) => Add(run, e.Data);
        process.ErrorDataReceived += (_, e) => Add(run, e.Data);
        process.Exited += (_, _) =>
        {
            process.WaitForExit(); // drains the output
            lock (gate)
            {
                run.ExitCode = process.ExitCode;
                run.VersionAfter = clientVersion();
                run.FinishedAt = DateTime.UtcNow;
            }
            process.Dispose();
            if (run.ExitCode == 0)
                log.LogInformation("iPad app updated: {Before} -> {After}", run.VersionBefore, run.VersionAfter);
            else
                log.LogWarning("iPad app update failed (exit {Code}): {Last}", run.ExitCode, Lines(run).LastOrDefault());
            if (run.ExitCode == 0)
                ClientUpdated?.Invoke(run.VersionAfter);
        };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            lock (gate)
            {
                run.ExitCode = -1;
                run.FinishedAt = DateTime.UtcNow;
                run.Output.Add($"Couldn't start PowerShell: {ex.Message}");
            }
            throw new UpdateException($"Couldn't start PowerShell: {ex.Message}");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    /// <summary>
    /// Starts deploy.ps1 on its own. It stops and restarts this service, so the reply is all the
    /// caller gets from this process; the log shows how it went after the restart.
    /// </summary>
    public void UpdateServer()
    {
        if (!OperatingSystem.IsWindows())
            throw new UpdateException("Updates run only on the Windows server.");
        if (!Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
            throw new UpdateException("This server isn't running as the Windows service, so deploy.ps1 has no service to update. Run it by hand.");
        if (ServerRepo is not { } repo)
            throw new UpdateException("SERVER_REPO isn't set: set it in Settings to the server's git clone (e.g. C:\\src\\terra-tekla-server).");
        var script = Path.Combine(repo, "windows", "deploy.ps1");
        if (!File.Exists(script))
            throw new UpdateException($"{script} is missing. Is SERVER_REPO the terra-tekla-server git clone?");
        lock (gate)
        {
            if (client is { Running: true })
                throw new UpdateException("The iPad app is being updated; try again when it's done.");
            if (File.Exists(DeployLog) && DateTime.UtcNow - File.GetLastWriteTimeUtc(DeployLog) < TimeSpan.FromMinutes(2)
                && !ReadTail(DeployLog, 3).Any(l => l.StartsWith("=== Finished", StringComparison.Ordinal)))
                throw new UpdateException("A server update is already running.");
        }

        Directory.CreateDirectory(settings.DataDir);
        File.WriteAllText(DeployLog, $"=== Server update started {DateTime.Now:yyyy-MM-dd HH:mm:ss} from the admin page{Environment.NewLine}");
        // Everything deploy.ps1 prints goes to the log, then a last line with how it ended.
        var command = $"& {{ try {{ & {PsQuote(script)} -InstallDir {PsQuote(AppContext.BaseDirectory.TrimEnd('\\', '/'))} -ServiceName {ServiceName} *>&1 | Out-File -Append -Encoding utf8 {PsQuote(DeployLog)}; $code = $LASTEXITCODE }} "
            + $"catch {{ $_ | Out-File -Append -Encoding utf8 {PsQuote(DeployLog)}; $code = 1 }}; "
            + $"('=== Finished ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ', exit ' + $code) | Out-File -Append -Encoding utf8 {PsQuote(DeployLog)} }}";
        var start = PowerShell("-Command", Quote(command));
        start.WorkingDirectory = repo;
        Process.Start(start)?.Dispose();
        log.LogWarning("Server update started from the admin page: {Script}", script);
    }

    static ProcessStartInfo PowerShell(params string[] args) => new()
    {
        FileName = "powershell.exe",
        Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass " + string.Join(' ', args),
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    static string Quote(string arg) => "\"" + arg.Replace("\"", "\\\"") + "\"";

    static string PsQuote(string arg) => "'" + arg.Replace("'", "''") + "'";

    void Add(Run run, string? line)
    {
        if (line is null)
            return;
        lock (gate)
        {
            run.Output.Add(line);
            if (run.Output.Count > 300)
                run.Output.RemoveAt(0);
        }
    }

    string[] Lines(Run run)
    {
        lock (gate)
            return run.Output.ToArray();
    }

    static string[] ReadTail(string path, int lines)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new StreamReader(stream).ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l != "").TakeLast(lines).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
    }
}
