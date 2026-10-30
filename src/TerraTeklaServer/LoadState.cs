using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TerraTeklaServer;

// The stacks of one PowerFab load, as every iPad sees them. PowerFab owns the pieces (what is loaded on
// the load); this owns what PowerFab doesn't track: the stacks, their names and order, which piece row
// is in which stack in scan order, who scanned it, and the shared truck check.
//
// Shapes match terra-tekla-client js/stack-store.js and js/check.js, so the app can apply the same
// operations locally while it waits for the server (it does, see the app's stack-store.js) and the
// server's answer simply replaces its guess.

public sealed class LoadState
{
    public string LoadId { get; set; } = "";
    public string Job { get; set; } = "";
    public string LoadNumber { get; set; } = "";
    /// <summary>The server-wide revision of the last change to this load.</summary>
    public long Rev { get; set; }
    public string UpdatedAt { get; set; } = "";
    public List<Stack> Stacks { get; set; } = [];
    public TruckCheck? TruckCheck { get; set; }
    /// <summary>Ids of the last operations applied, so a resent operation is applied once.</summary>
    public List<string> RecentOps { get; set; } = [];
    /// <summary>Pieces taken off recently, so a PowerFab read made before that doesn't put them back.</summary>
    [JsonIgnore]
    public List<Removal> Removed { get; set; } = [];

    public LoadState Clone()
    {
        var copy = JsonSerializer.Deserialize<LoadState>(JsonSerializer.Serialize(this, Json.Options), Json.Options)!;
        copy.Removed = [.. Removed];
        return copy;
    }
}

public sealed class Stack
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? CreatedBy { get; set; }
    public List<Entry> Items { get; set; } = [];
}

/// <summary>
/// One piece row. The PowerFab details the app shows (weight, description, partNumber, shape...) ride
/// along in <see cref="Details"/> and come back out as plain fields.
/// </summary>
public sealed class Entry
{
    public string Id { get; set; } = "";
    public string Mark { get; set; } = "";
    public string Sequence { get; set; } = "";
    public bool Manual { get; set; }
    /// <summary>When it was scanned; null for a piece loaded in PowerFab itself.</summary>
    public string? ScannedAt { get; set; }
    /// <summary>Device id of whoever scanned it; null for a piece loaded in PowerFab itself.</summary>
    public string? ScannedBy { get; set; }
    /// <summary>Scanned, and PowerFab is still being asked to load it.</summary>
    public bool Pending { get; set; }
    /// <summary>Being taken off the load in PowerFab.</summary>
    public bool Removing { get; set; }
    public string AddedAt { get; set; } = "";
    public long Rev { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Details { get; set; }
}

public sealed class TruckCheck
{
    public string StartedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public List<CheckScan> Scans { get; set; } = [];
}

public sealed class CheckScan
{
    public string Id { get; set; } = "";
    public string Mark { get; set; } = "";
    public bool Manual { get; set; }
    public string ScannedAt { get; set; } = "";
    public string? ScannedBy { get; set; }
}

public sealed record Removal(string Mark, string Sequence, long Rev, DateTime At);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
