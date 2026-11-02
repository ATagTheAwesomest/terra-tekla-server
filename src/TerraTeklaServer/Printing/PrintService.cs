using System.Net.Sockets;
using System.Text;

namespace TerraTeklaServer.Printing;

/// <summary>One print job, for the admin board's list of recent jobs.</summary>
public sealed record PrintJobRecord(
    DateTime At, string PrinterId, string PrinterName, string By, string? Job, string? Load, string Title,
    int Pages, int Copies, bool Ok, string? Error, string? PrinterJobId);

/// <summary>What a printer said when asked (the admin board's Check button and the scan).</summary>
public sealed record PrinterCheck(bool Ok, string? Error, string? Name, string? Model, string? Location, string? State, bool? TakesPdf, string[] Formats);

/// <summary>
/// Prints tickets: lays them out (<see cref="TicketLayout"/>) and sends them to a printer of any kind.
/// Windows printers are drawn through Windows' printing (the printer's own driver), so any printer
/// installed on the server works. IPP and raw printers are sent a PDF, which the printer itself must
/// be able to print.
/// </summary>
public sealed class PrintService(IHttpClientFactory http, ILogger<PrintService> log)
{
    public const string HttpClientName = "printers";
    public const int MaxCopies = 10;

    readonly object gate = new();
    readonly LinkedList<PrintJobRecord> recent = new();

    public IReadOnlyList<PrintJobRecord> Recent()
    {
        lock (gate)
            return recent.ToList();
    }

    void Remember(PrintJobRecord job)
    {
        lock (gate)
        {
            recent.AddFirst(job);
            while (recent.Count > 50)
                recent.RemoveLast();
        }
    }

    /// <summary>A PDF of the ticket (preview, and what IPP and raw printers get).</summary>
    public static (byte[] Pdf, int Pages) Pdf(Ticket ticket, string? paper)
    {
        var (w, h) = Paper.Size(paper);
        var pages = TicketLayout.Lay(ticket, w, h);
        return (PdfWriter.Write(pages, JobName(ticket)), pages.Count);
    }

    public static string JobName(Ticket t) =>
        string.Join(" ", new[] { t.Title, t.Job is null ? null : $"job {t.Job}", t.Load is null ? null : $"load {t.Load}" }.Where(x => x is not null));

    /// <summary>Prints the ticket. Throws <see cref="PrinterException"/> with a message for the iPad when it can't.</summary>
    public async Task<PrintJobRecord> PrintAsync(PrinterEntry printer, Ticket ticket, int copies, string by, CancellationToken ct)
    {
        copies = Math.Clamp(copies, 1, MaxCopies);
        var name = JobName(ticket);
        try
        {
            var (pages, jobId) = printer.Kind switch
            {
                "windows" => await PrintWindowsAsync(printer, ticket, copies, name, ct),
                "ipp" => await PrintIppAsync(printer, ticket, copies, name, by, ct),
                "raw" => await PrintRawAsync(printer, ticket, copies, ct),
                _ => throw new PrinterException($"Unknown printer kind {printer.Kind}."),
            };
            var job = new PrintJobRecord(DateTime.UtcNow, printer.Id, printer.Name, by, ticket.Job, ticket.Load, ticket.Title, pages, copies, true, null, jobId);
            Remember(job);
            log.LogInformation("Printed {Name} ({Pages} pages x {Copies}) on {Printer} for {By}.", name, pages, copies, printer.Name, by);
            return job;
        }
        catch (PrinterException ex)
        {
            Remember(new PrintJobRecord(DateTime.UtcNow, printer.Id, printer.Name, by, ticket.Job, ticket.Load, ticket.Title, 0, copies, false, ex.Message, null));
            log.LogWarning("Printing {Name} on {Printer} failed: {Error}", name, printer.Name, ex.Message);
            throw;
        }
    }

    static Task<(int, string?)> PrintWindowsAsync(PrinterEntry printer, Ticket ticket, int copies, string name, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PrinterException("Windows printers can only be used when the server runs on Windows.");
        // Windows' printing is synchronous; keep it off the request thread.
        return Task.Run(() => OperatingSystem.IsWindows()
            ? (WindowsPrinting.Print(printer.Address, ticket, copies, name), (string?)null)
            : throw new PrinterException("Windows printers can only be used when the server runs on Windows."), ct);
    }

    async Task<(int, string?)> PrintIppAsync(PrinterEntry printer, Ticket ticket, int copies, string name, string by, CancellationToken ct)
    {
        var (pdf, pages) = Pdf(ticket, printer.Paper);
        var reply = await Ipp.PrintPdfAsync(http.CreateClient(HttpClientName), printer.Address, pdf, name, Clean(by), copies, ct);
        if (!reply.Ok)
            throw new PrinterException($"{printer.Name} didn't take the job: {Ipp.StatusText(reply)}.");
        return (pages, reply.Int("job-id")?.ToString());
    }

    static async Task<(int, string?)> PrintRawAsync(PrinterEntry printer, Ticket ticket, int copies, CancellationToken ct)
    {
        var (pdf, pages) = Pdf(ticket, printer.Paper);
        var (host, port) = HostPort(printer.Address);
        using var tcp = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token);
            await using var stream = tcp.GetStream();
            await stream.WriteAsync(RawJob(pdf, copies), timeout.Token);
            await stream.FlushAsync(timeout.Token);
            tcp.Client.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new PrinterException($"Couldn't send to {printer.Name} at {printer.Address}: {ex.Message}");
        }
        return (pages, null);
    }

    /// <summary>
    /// What goes to a raw (port 9100) printer: the PDF inside a PJL job that switches the printer to
    /// its PDF interpreter. Printers without one print garbage or nothing (use IPP or Windows then).
    /// </summary>
    public static byte[] RawJob(byte[] pdf, int copies)
    {
        const string Uel = "\u001B%-12345X";
        var head = Encoding.ASCII.GetBytes($"{Uel}@PJL JOB NAME=\"TERRA\"\r\n@PJL SET COPIES={copies}\r\n@PJL ENTER LANGUAGE=PDF\r\n");
        var tail = Encoding.ASCII.GetBytes($"{Uel}@PJL EOJ\r\n{Uel}");
        return [.. head, .. pdf, .. tail];
    }

    public static (string Host, int Port) HostPort(string address)
    {
        var i = address.LastIndexOf(':');
        return i > 0 && int.TryParse(address[(i + 1)..], out var p) ? (address[..i], p) : (address, 9100);
    }

    static string Clean(string s)
    {
        var t = new string(s.Where(c => c >= ' ' && c != 127).ToArray()).Trim();
        return t == "" ? "terra" : t[..Math.Min(t.Length, 60)];
    }

    /// <summary>Asks a printer what it is and whether it is ready.</summary>
    public async Task<PrinterCheck> CheckAsync(string kind, string address, CancellationToken ct)
    {
        try
        {
            switch (kind)
            {
                case "windows":
                    if (!OperatingSystem.IsWindows())
                        return new PrinterCheck(false, "Windows printers can only be used when the server runs on Windows.", null, null, null, null, null, []);
                    return WindowsPrinting.Check(address);
                case "ipp":
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(6));
                    var r = await Ipp.GetAttributesAsync(http.CreateClient(HttpClientName), address, timeout.Token);
                    if (!r.Ok)
                        return new PrinterCheck(false, $"It answered, but {Ipp.StatusText(r)}.", null, null, null, null, null, []);
                    var formats = r.Texts("document-format-supported").ToArray();
                    var state = r.Int("printer-state") switch { 3 => "idle", 4 => "printing", 5 => "stopped", _ => null };
                    var reasons = r.Texts("printer-state-reasons").Where(x => x != "none").ToList();
                    if (reasons.Count > 0)
                        state = $"{state ?? "?"} ({string.Join(", ", reasons)})";
                    var pdf = formats.Length == 0 ? (bool?)null : formats.Contains("application/pdf", StringComparer.OrdinalIgnoreCase);
                    return new PrinterCheck(true, pdf == false ? "It can't print PDF files over IPP: install it on the Windows server and use the Windows printer instead." : null,
                        r.Text("printer-info") ?? r.Text("printer-name"), r.Text("printer-make-and-model"), r.Text("printer-location"), state, pdf, formats);
                }
                case "raw":
                {
                    var (host, port) = HostPort(address);
                    using var tcp = new TcpClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(4));
                    await tcp.ConnectAsync(host, port, timeout.Token);
                    return new PrinterCheck(true, null, null, null, null, "port open", null, []);
                }
                default:
                    return new PrinterCheck(false, $"Unknown printer kind {kind}.", null, null, null, null, null, []);
            }
        }
        catch (Exception ex) when (ex is PrinterException or SocketException or IOException or OperationCanceledException or HttpRequestException && !ct.IsCancellationRequested)
        {
            var why = ex is OperationCanceledException ? "it didn't answer in time" : ex.Message;
            return new PrinterCheck(false, $"Couldn't reach {address}: {why}", null, null, null, null, null, []);
        }
    }
}
