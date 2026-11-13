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

    /// <summary>
    /// A copy to change, so a refused operation leaves this one as it was. Field by field (a load can
    /// have thousands of rows, and this runs on every change); a row's PowerFab details are shared,
    /// since an operation only ever replaces them.
    /// </summary>
    public LoadState Clone() => new()
    {
        LoadId = LoadId,
        Job = Job,
        LoadNumber = LoadNumber,
        Rev = Rev,
        UpdatedAt = UpdatedAt,
        Stacks = Stacks.ConvertAll(st => new Stack
        {
            Id = st.Id,
            Name = st.Name,
            CreatedBy = st.CreatedBy,
            Pallet = st.Pallet is { } p ? new Pallet { Mark = p.Mark, Photos = p.Photos, Manual = p.Manual } : null,
            Items = st.Items.ConvertAll(e => e.Copy()),
        }),
        TruckCheck = TruckCheck is { } c ? new TruckCheck
        {
            StartedAt = c.StartedAt,
            UpdatedAt = c.UpdatedAt,
            Scans = c.Scans.ConvertAll(x => new CheckScan { Id = x.Id, Mark = x.Mark, Manual = x.Manual, ScannedAt = x.ScannedAt, ScannedBy = x.ScannedBy, Qty = x.Qty, Code = x.Code }),
        } : null,
        RecentOps = [.. RecentOps],
        Removed = [.. Removed],
    };
}

public sealed class Stack
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? CreatedBy { get; set; }
    /// <summary>Set when the stack is a pallet: many of one mark, scanned once with a quantity.</summary>
    public Pallet? Pallet { get; set; }
    public List<Entry> Items { get; set; } = [];
}

/// <summary>
/// A pallet: a stack of one mark that was scanned once with a quantity, instead of piece by piece.
/// It still has one row per piece (so totals, sync, export and the truck check work the same as any
/// stack); the app shows it as one line, "mark × quantity". A pallet needs photos of the stack
/// (<see cref="Photos"/> is how many were taken when it was made).
/// </summary>
public sealed class Pallet
{
    public string Mark { get; set; } = "";
    public int Photos { get; set; }
    /// <summary>The mark was typed on the keypad, not scanned.</summary>
    public bool Manual { get; set; }
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
    /// <summary>
    /// The piece's own code (jobs with piece codes: every piece of a mark has a different one), when it
    /// was loaded by scanning that code. Null for a piece loaded by its mark. See docs/PIECE-CODES.md.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }
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

    public Entry Copy() => (Entry)MemberwiseClone();
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
    /// <summary>How many pieces of the mark the scan checks off: 1, or a whole pallet's count.</summary>
    public int Qty { get; set; } = 1;
    /// <summary>The piece code scanned (jobs with piece codes), which checks off that exact piece.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; set; }
}

public sealed record Removal(string Mark, string Sequence, long Rev, DateTime At);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
