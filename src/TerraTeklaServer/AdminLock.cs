using System.Security.Cryptography;
using System.Text;

namespace TerraTeklaServer;

/// <summary>A code that is too short, too long or not all digits.</summary>
public sealed class BadPinException(string message) : Exception(message);

/// <summary>
/// The admin code that locks the admin page's Server (stats) and Settings. The code is 4 to 8 digits,
/// kept only as a salted PBKDF2 hash (<c>ADMIN_PIN_HASH</c> in local.settings.json). The server makes
/// up the first one itself (<see cref="EnsureCode"/>), so nobody who merely reaches the server can pick
/// it. The current code is also written to DATA_DIR\admin-code.txt (made up or changed), for whoever
/// runs the server. Entering it gives
/// the device a random token (header <c>X-Terra-Admin</c>) that is good for <c>ADMIN_LOCK_MINUTES</c>
/// (30 by default) from the IP address that entered the code, and no other. The iPad app and the
/// admin page share it, since they come from the same server. Wrong codes lock everyone out for a
/// while, longer each time, so the code can't be guessed by trying them all.
/// </summary>
public sealed class AdminLock(SettingsFile file, ILogger<AdminLock> log)
{
    public const string Header = "X-Terra-Admin";
    const int Iterations = 120_000;
    const int FreeTries = 5;

    readonly object gate = new();
    readonly Dictionary<string, (string Ip, DateTime Until)> sessions = new(); // token hash -> where from, good until (UTC)
    string? hash = (file.Read().TryGetValue("ADMIN_PIN_HASH", out var h) ? h : null) is { Length: > 0 } stored ? stored : null;
    int failures;
    DateTime blockedUntil = DateTime.MinValue;
    string? codeFile; // DATA_DIR\admin-code.txt: the current code, for whoever runs the server

    /// <summary>Where the current code is written, each time it is made up or changed.</summary>
    public static string CodeFilePath(string dataDir) => Path.Combine(dataDir, "admin-code.txt");

    public bool PinSet
    {
        get { lock (gate) return hash is not null; }
    }

    /// <summary>How long entering the code unlocks a device for.</summary>
    public TimeSpan Duration => TimeSpan.FromMinutes(Math.Clamp(int.TryParse(file.Current("ADMIN_LOCK_MINUTES"), out var m) ? m : 30, 1, 24 * 60));

    /// <summary>Seconds until codes are taken again after too many wrong ones, else 0.</summary>
    public int RetryAfter
    {
        get { lock (gate) return Math.Max(0, (int)Math.Ceiling((blockedUntil - DateTime.UtcNow).TotalSeconds)); }
    }

    /// <summary>
    /// At startup: when no code is set, makes one up (6 random digits), keeps its hash and writes the
    /// code itself to <paramref name="file"/> for whoever runs the server. Returns the new code, or
    /// null when one was already set.
    /// </summary>
    public string? EnsureCode(string file)
    {
        codeFile = file;
        lock (gate)
        {
            if (hash is not null)
                return null;
            var code = NewCode();
            Save(code, "made up by the server");
            log.LogWarning("No admin code was set, so the server made one up. It is in {File}.", file);
            return code;
        }
    }

    /// <summary>A new random code in place of the current one. Every other session is locked out.</summary>
    public (string Code, string Token, DateTime Until) Generate(string ip)
    {
        var code = NewCode();
        var session = Change(code, ip);
        return (code, session.Token, session.Until);
    }

    static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>Sets the first code. Only works while none is set. Returns a session token.</summary>
    public (string Token, DateTime Until) Setup(string pin, string ip)
    {
        Check(pin);
        lock (gate)
        {
            if (hash is not null)
                throw new InvalidOperationException("An admin code is already set. Change it from Settings, with the current code.");
            Save(pin, $"set from {ip}");
            log.LogWarning("The admin code was set for the first time (from {Ip}).", ip);
            return NewSession(ip);
        }
    }

    /// <summary>Checks a code. Returns a session token, or null when it is wrong or tries are blocked.</summary>
    public (string Token, DateTime Until)? Unlock(string pin, string ip)
    {
        lock (gate)
        {
            if (hash is null || DateTime.UtcNow < blockedUntil)
                return null;
            if (Verify(pin, hash))
            {
                failures = 0;
                return NewSession(ip);
            }
            failures++;
            if (failures >= FreeTries)
            {
                // 30 s, 60 s, 2 min... up to 15 min.
                var wait = TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, failures - FreeTries), 900));
                blockedUntil = DateTime.UtcNow + wait;
                log.LogWarning("{Count} wrong admin codes in a row (last from {Ip}): codes are refused for {Seconds} s.", failures, ip, (int)wait.TotalSeconds);
            }
            return null;
        }
    }

    /// <summary>
    /// Changes the code. Only for a device that is unlocked already (the route sits behind the admin
    /// code), so the current code isn't asked again. Every other session is locked out.
    /// </summary>
    public (string Token, DateTime Until) Change(string pin, string ip)
    {
        Check(pin);
        lock (gate)
        {
            Save(pin, $"changed from {ip}");
            sessions.Clear();
            log.LogWarning("The admin code was changed (from {Ip}).", ip);
            return NewSession(ip);
        }
    }

    /// <summary>When a token stops working, if it is a live session from this IP address; else null.</summary>
    public DateTime? Until(string? token, string ip)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        var key = TokenKey(token);
        lock (gate)
        {
            var now = DateTime.UtcNow;
            foreach (var old in sessions.Where(s => s.Value.Until <= now).Select(s => s.Key).ToList())
                sessions.Remove(old);
            return sessions.TryGetValue(key, out var s) && s.Ip == ip ? s.Until : null;
        }
    }

    public bool Valid(string? token, string ip) => Until(token, ip) is not null;

    public void Lock(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return;
        lock (gate)
            sessions.Remove(TokenKey(token));
    }

    public static void Check(string? pin)
    {
        if (pin is null || pin.Length is < 4 or > 8 || !pin.All(char.IsAsciiDigit))
            throw new BadPinException("The admin code must be 4 to 8 digits.");
    }

    /// <summary>"pbkdf2-sha256$iterations$salt$hash", salt and hash in base64.</summary>
    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string? pin, string stored)
    {
        var parts = stored.Split('$');
        if (pin is null || parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations))
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(key, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    void Save(string code, string how)
    {
        var newHash = Hash(code);
        file.Write(new Dictionary<string, string?> { ["ADMIN_PIN_HASH"] = newHash });
        hash = newHash;
        if (codeFile is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(codeFile)!);
            File.WriteAllText(codeFile, $"{code}\r\n\r\nThe TERRA server's admin code, {how} on {DateTime.Now:yyyy-MM-dd HH:mm}.\r\n"
                + "Change it on the admin board (Settings > Admin code); this file follows.\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Couldn't write the admin code to {File}.", codeFile);
        }
    }

    (string, DateTime) NewSession(string ip)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var until = DateTime.UtcNow + Duration;
        sessions[TokenKey(token)] = (ip, until);
        return (token, until);
    }

    /// <summary>
    /// The address a request came from. Behind a Cloudflare Tunnel (or another proxy on this computer)
    /// every request arrives from 127.0.0.1, so then the address the proxy names is used. Those
    /// headers are only believed from this computer itself.
    /// </summary>
    public static string ClientIp(HttpContext http)
    {
        var remote = http.Connection.RemoteIpAddress;
        if (remote is not null && System.Net.IPAddress.IsLoopback(remote))
        {
            var cf = http.Request.Headers["CF-Connecting-IP"].ToString().Trim();
            if (cf != "")
                return cf;
            var forwarded = http.Request.Headers["X-Forwarded-For"].ToString().Split(',')[0].Trim();
            if (forwarded != "")
                return forwarded;
        }
        return remote?.MapToIPv4().ToString() ?? "unknown";
    }

    // Only a hash of each token is kept, so a memory dump doesn't hand out sessions.
    static string TokenKey(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
