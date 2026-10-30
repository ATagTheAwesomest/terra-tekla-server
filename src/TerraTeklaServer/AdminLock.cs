using System.Security.Cryptography;
using System.Text;

namespace TerraTeklaServer;

/// <summary>A code that is too short, too long or not all digits.</summary>
public sealed class BadPinException(string message) : Exception(message);

/// <summary>
/// The admin code that locks the admin page's Server (stats) and Settings. The code is 4 to 8 digits,
/// kept only as a salted PBKDF2 hash (<c>ADMIN_PIN_HASH</c> in local.settings.json). Entering it gives
/// the browser a random token (header <c>X-Terra-Admin</c>) that stays good while it is used, until
/// <c>ADMIN_LOCK_MINUTES</c> pass without a request. Wrong codes lock everyone out for a while, longer
/// each time, so the code can't be guessed by trying them all.
/// </summary>
public sealed class AdminLock(SettingsFile file, ILogger<AdminLock> log)
{
    public const string Header = "X-Terra-Admin";
    const int Iterations = 120_000;
    const int FreeTries = 5;

    readonly object gate = new();
    readonly Dictionary<string, DateTime> sessions = new(); // token hash -> last used (UTC)
    string? hash = (file.Read().TryGetValue("ADMIN_PIN_HASH", out var h) ? h : null) is { Length: > 0 } stored ? stored : null;
    int failures;
    DateTime blockedUntil = DateTime.MinValue;

    public bool PinSet
    {
        get { lock (gate) return hash is not null; }
    }

    public TimeSpan Idle => TimeSpan.FromMinutes(Math.Clamp(int.TryParse(file.Current("ADMIN_LOCK_MINUTES"), out var m) ? m : 15, 1, 24 * 60));

    /// <summary>Seconds until codes are taken again after too many wrong ones, else 0.</summary>
    public int RetryAfter
    {
        get { lock (gate) return Math.Max(0, (int)Math.Ceiling((blockedUntil - DateTime.UtcNow).TotalSeconds)); }
    }

    /// <summary>Sets the first code. Only works while none is set. Returns a session token.</summary>
    public string Setup(string pin)
    {
        Check(pin);
        lock (gate)
        {
            if (hash is not null)
                throw new InvalidOperationException("An admin code is already set. Change it from Settings, with the current code.");
            Save(Hash(pin));
            log.LogWarning("The admin code was set for the first time.");
            return NewSession();
        }
    }

    /// <summary>Checks a code. Returns a session token, or null when it is wrong or tries are blocked.</summary>
    public string? Unlock(string pin)
    {
        lock (gate)
        {
            if (hash is null || DateTime.UtcNow < blockedUntil)
                return null;
            if (Verify(pin, hash))
            {
                failures = 0;
                return NewSession();
            }
            failures++;
            if (failures >= FreeTries)
            {
                // 30 s, 60 s, 2 min... up to 15 min.
                var wait = TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, failures - FreeTries), 900));
                blockedUntil = DateTime.UtcNow + wait;
                log.LogWarning("{Count} wrong admin codes in a row: codes are refused for {Seconds} s.", failures, (int)wait.TotalSeconds);
            }
            return null;
        }
    }

    /// <summary>Changes the code (the current one is needed). Every other session is locked out.</summary>
    public string? Change(string current, string pin)
    {
        Check(pin);
        lock (gate)
        {
            if (Unlock(current) is null)
                return null;
            Save(Hash(pin));
            sessions.Clear();
            log.LogWarning("The admin code was changed.");
            return NewSession();
        }
    }

    /// <summary>Whether a token is a live session; using it keeps it alive.</summary>
    public bool Valid(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;
        var key = TokenKey(token);
        lock (gate)
        {
            var now = DateTime.UtcNow;
            foreach (var old in sessions.Where(s => now - s.Value > Idle).Select(s => s.Key).ToList())
                sessions.Remove(old);
            if (!sessions.ContainsKey(key))
                return false;
            sessions[key] = now;
            return true;
        }
    }

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

    void Save(string newHash)
    {
        file.Write(new Dictionary<string, string?> { ["ADMIN_PIN_HASH"] = newHash });
        hash = newHash;
    }

    string NewSession()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        sessions[TokenKey(token)] = DateTime.UtcNow;
        return token;
    }

    // Only a hash of each token is kept, so a memory dump doesn't hand out sessions.
    static string TokenKey(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
