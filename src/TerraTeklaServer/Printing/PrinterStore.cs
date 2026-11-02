using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraTeklaServer.Printing;

/// <summary>A printer setting the admin board refused.</summary>
public sealed class BadPrinterException(string message) : Exception(message);

/// <summary>
/// A printer the server knows. <paramref name="Kind"/>:
/// <c>windows</c> (a printer installed on the Windows server; <paramref name="Address"/> is its name as
/// Windows lists it), <c>ipp</c> (a network printer that takes PDF over IPP; an ipp:// or ipps://
/// address) or <c>raw</c> (a network printer's port 9100, sent PDF; host or host:port).
/// Only printers with <paramref name="Allowed"/> on are offered to the iPads.
/// </summary>
public sealed record PrinterEntry(
    string Id,
    string Name,
    string Kind,
    string Address,
    bool Allowed,
    string Paper = "letter",
    string? Location = null,
    string? Model = null,
    DateTime? AddedAt = null);

/// <summary>The printers list, kept in DATA_DIR\printers.json (changed on the admin board's Settings).</summary>
public sealed class PrinterStore(Settings settings)
{
    public static readonly string[] Kinds = ["windows", "ipp", "raw"];
    static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly object gate = new();
    public string Path => System.IO.Path.Combine(settings.DataDir, "printers.json");

    public IReadOnlyList<PrinterEntry> All()
    {
        lock (gate)
        {
            if (!File.Exists(Path))
                return [];
            try
            {
                return JsonSerializer.Deserialize<List<PrinterEntry>>(File.ReadAllText(Path), Options) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    public IReadOnlyList<PrinterEntry> Allowed() => All().Where(p => p.Allowed).ToList();

    public PrinterEntry? Find(string id) => All().FirstOrDefault(p => p.Id == id);

    /// <summary>Checks and saves the whole list (the admin board sends it all). Returns it as saved.</summary>
    public IReadOnlyList<PrinterEntry> Save(IEnumerable<PrinterEntry> printers)
    {
        var clean = new List<PrinterEntry>();
        var old = All().ToDictionary(p => p.Id);
        foreach (var p in printers)
        {
            var kind = (p.Kind ?? "").Trim().ToLowerInvariant();
            if (!Kinds.Contains(kind))
                throw new BadPrinterException($"\"{p.Kind}\" isn't a printer kind (windows, ipp or raw).");
            var address = CleanAddress(kind, p.Address);
            var name = (p.Name ?? "").Trim();
            if (name == "")
                name = address;
            if (name.Length > 80)
                throw new BadPrinterException("A printer's name can be at most 80 characters.");
            var paper = (p.Paper ?? "").Trim().ToLowerInvariant() is { Length: > 0 } pp ? pp : "letter";
            if (!Printing.Paper.Names.Contains(paper))
                throw new BadPrinterException($"\"{p.Paper}\" isn't a paper size ({string.Join(", ", Printing.Paper.Names)}).");
            var id = p.Id is { Length: > 0 and <= 40 } given && given.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? given : Guid.NewGuid().ToString("N")[..12];
            if (clean.Any(c => c.Id == id))
                id = Guid.NewGuid().ToString("N")[..12];
            if (clean.Any(c => c.Kind == kind && c.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
                throw new BadPrinterException($"{address} is in the list twice.");
            var location = p.Location?.Trim() is { Length: > 0 } loc ? loc[..Math.Min(loc.Length, 80)] : null;
            var model = p.Model?.Trim() is { Length: > 0 } m ? m[..Math.Min(m.Length, 120)] : null;
            clean.Add(new PrinterEntry(id, name, kind, address, p.Allowed, paper, location, model,
                old.TryGetValue(id, out var was) ? was.AddedAt : p.AddedAt ?? DateTime.UtcNow));
        }
        if (clean.Count > 100)
            throw new BadPrinterException("At most 100 printers.");
        lock (gate)
        {
            Directory.CreateDirectory(settings.DataDir);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(clean, Options));
            File.Move(temp, Path, overwrite: true);
        }
        return clean;
    }

    /// <summary>A printer address in the form the server uses for that kind, or <see cref="BadPrinterException"/>.</summary>
    public static string CleanAddress(string kind, string? address)
    {
        var a = (address ?? "").Trim();
        if (a == "")
            throw new BadPrinterException("Every printer needs an address.");
        switch (kind)
        {
            case "windows":
                if (a.Length > 256)
                    throw new BadPrinterException("That Windows printer name is too long.");
                return a;
            case "ipp":
                if (!a.Contains("://"))
                    a = $"ipp://{a}";
                if (!Uri.TryCreate(a, UriKind.Absolute, out var u) || u.Scheme is not ("ipp" or "ipps" or "http" or "https") || u.Host == "")
                    throw new BadPrinterException($"\"{address}\" isn't an IPP printer address, e.g. ipp://192.168.1.50/ipp/print.");
                var scheme = u.Scheme is "https" or "ipps" ? "ipps" : "ipp";
                var path = u.AbsolutePath is "/" or "" ? "/ipp/print" : u.AbsolutePath;
                return $"{scheme}://{u.Host}{(u.Port is 631 or -1 ? "" : $":{u.Port}")}{path}";
            case "raw":
                if (a.StartsWith("socket://", StringComparison.OrdinalIgnoreCase))
                    a = a[9..];
                a = a.TrimEnd('/');
                if (!Uri.TryCreate($"tcp://{a}", UriKind.Absolute, out var r) || r.Host == "" || r.AbsolutePath is not ("/" or ""))
                    throw new BadPrinterException($"\"{address}\" isn't a printer address, e.g. 192.168.1.50 or 192.168.1.50:9100.");
                return $"{r.Host}:{(r.Port > 0 ? r.Port : 9100)}";
            default:
                throw new BadPrinterException($"\"{kind}\" isn't a printer kind.");
        }
    }
}
