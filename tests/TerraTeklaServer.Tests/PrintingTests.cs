using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using TerraTeklaServer.Printing;

namespace TerraTeklaServer.Tests;

/// <summary>Printing through the server: the ticket layout, the PDF, IPP and raw printers, and the routes.</summary>
public sealed class PrintingTests : IDisposable
{
    const string Key = "test-key-0123456789";
    readonly string dataDir = Path.Combine(Path.GetTempPath(), $"terra-print-{Guid.NewGuid():N}");
    readonly WebApplicationFactory<Program> factory;

    public PrintingTests()
    {
        Directory.CreateDirectory(dataDir);
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ACCESS_KEY", Key);
            b.UseSetting("DATA_DIR", dataDir);
            b.UseSetting("CLIENT_ROOT", "");
            b.UseSetting("CONVERSION_URL", "");
            b.UseSetting("SETTINGS_FILE", Path.Combine(dataDir, "local.settings.json"));
        });
    }

    public void Dispose()
    {
        factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDir, true); } catch (IOException) { }
    }

    HttpClient Client(string? admin = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        client.DefaultRequestHeaders.Add("X-Terra-Device", "ipad-print-0001");
        if (admin is not null)
            client.DefaultRequestHeaders.Add("X-Terra-Admin", admin);
        return client;
    }

    async Task<string> Unlock()
    {
        await Client().GetAsync("/health");
        var code = File.ReadAllText(AdminLock.CodeFilePath(dataDir)).Split('\n')[0].Trim();
        var reply = await Client().PostAsJsonAsync("/api/admin/unlock", new { pin = code });
        return (string)(await reply.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
    }

    // A ticket as js/export.js ticketModel() sends it: one stack, a typed mark, and a long list.
    static JsonObject TicketJson(int rows = 3) => new()
    {
        ["title"] = "Shipping Ticket",
        ["job"] = "EPM-001",
        ["load"] = "12",
        ["summary"] = new JsonArray(new JsonArray("Load", "12"), new JsonArray("Job", "EPM-001"), new JsonArray("Total weight", "18,450 lbs")),
        ["sections"] = new JsonArray(new JsonObject
        {
            ["heading"] = "Stack 12 - 1",
            ["note"] = $"{rows} pcs · 1,234 lbs",
            ["columns"] = new JsonArray(
                new JsonObject { ["label"] = "#", ["kind"] = "num" },
                new JsonObject { ["label"] = "Mark", ["kind"] = "mark" },
                new JsonObject { ["label"] = "Seq", ["kind"] = "text" },
                new JsonObject { ["label"] = "Description", ["kind"] = "text" },
                new JsonObject { ["label"] = "Weight (lbs)", ["kind"] = "num" },
                new JsonObject { ["label"] = "✓", ["kind"] = "check" }),
            ["rows"] = new JsonArray(Enumerable.Range(1, rows).Select(i => (JsonNode)new JsonObject
            {
                ["cells"] = new JsonArray($"{i}", i == 2 ? "1099-X1-" : $"10{i:D2}-C1", "1", i == 1 ? "A description long enough that it has to wrap onto a second line of the table, and maybe a third one too" : "Beam", "500", i == 1 ? "✓" : ""),
                ["manual"] = i == 2,
            }).ToArray()),
            ["foot"] = new JsonArray(new JsonObject { ["text"] = "Stack subtotal", ["span"] = 4 }, new JsonObject { ["text"] = "1234" }, new JsonObject { ["text"] = "" }),
        }),
        ["signoff"] = new JsonArray("Loaded by: ______", "Checked by: ______", "Driver: ______"),
    };

    // ---- Layout and PDF ------------------------------------------------------------------------

    [Fact]
    public void A_long_ticket_breaks_into_pages_with_the_header_again_and_page_numbers()
    {
        var ticket = Ticket.Parse(TicketJson(120));
        var (w, h) = Paper.Size("letter");
        var pages = TicketLayout.Lay(ticket, w, h);
        Assert.True(pages.Count >= 3, $"{pages.Count} pages");
        foreach (var (page, i) in pages.Select((p, i) => (p, i)))
        {
            var texts = page.Ops.OfType<TextOp>().ToList();
            Assert.Contains(texts, t => t.Text == $"Page {i + 1} of {pages.Count}");
            // Nothing below the bottom margin but the page number line.
            Assert.All(texts.Where(t => !t.Text.StartsWith("Page ") && !t.Text.StartsWith("Shipping Ticket ·")), t => Assert.True(t.Y < h - TicketLayout.Margin, $"{t.Text} at {t.Y}"));
            Assert.All(page.Ops.OfType<RectOp>(), r => Assert.True(r.Y + r.H <= h - TicketLayout.Margin));
            // Every page has the table's black header row with its labels in white.
            Assert.Contains(texts, t => t.Text == "Mark" && t.White);
            Assert.All(texts, t => Assert.True(t.X >= TicketLayout.Margin - 0.01 && t.X + Fonts.Width(t.Text, t.Font, t.Size) <= w - TicketLayout.Margin + 0.5, $"{t.Text} at x {t.X}"));
        }
        Assert.Contains(pages[1].Ops.OfType<TextOp>(), t => t.Text == "Stack 12 - 1 (continued)");
        Assert.Contains(pages[^1].Ops.OfType<TextOp>(), t => t.Text == "Driver: ______");
        // The typed mark is underlined, in the slanted mark font.
        Assert.Contains(pages[0].Ops.OfType<TextOp>(), t => t.Text == "1099-X1-" && t.Underline && t.Font == TicketFont.MarkManual);
    }

    [Fact]
    public void Text_wraps_at_spaces_and_cuts_long_words_and_values()
    {
        var lines = Fonts.Wrap("one two three four five six", TicketFont.Regular, 10, 50);
        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(Fonts.Width(l, TicketFont.Regular, 10) <= 50));
        Assert.Equal("one two three four five six", string.Join(" ", lines));
        var broken = Fonts.Wrap(new string('W', 40), TicketFont.Bold, 10, 60);
        Assert.Equal(new string('W', 40), string.Concat(broken));
        Assert.EndsWith("…", Fonts.Fit("1001-C1-VERY-LONG-MARK", TicketFont.Mark, 10, 50));
        Assert.Equal("short", Fonts.Fit("short", TicketFont.Mark, 10, 500));
        Assert.Equal('?', Fonts.Printable((char)0x4E00));
        Assert.Equal(0xD7, Fonts.WinAnsi('×'));
    }

    [Fact]
    public void The_pdf_is_well_formed()
    {
        var (pdf, pages) = PrintService.Pdf(Ticket.Parse(TicketJson(80)), "a4");
        var text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains($"/Count {pages}", text);
        Assert.Contains("/MediaBox [0 0 595.28 841.89]", text);
        // The cross-reference table points at each object.
        var startxref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        Assert.StartsWith("xref", text[startxref..]);
        var rows = text[startxref..].Split('\n').Skip(3).TakeWhile(l => l.EndsWith(" n ")).ToList();
        for (var i = 0; i < rows.Count; i++)
            Assert.StartsWith($"{i + 1} 0 obj", text[int.Parse(rows[i][..10])..]);
        var dump = Environment.GetEnvironmentVariable("TERRA_PDF_OUT");
        if (dump is { Length: > 0 })
        {
            File.WriteAllBytes(dump, pdf);
            File.WriteAllBytes(Path.ChangeExtension(dump, ".test.pdf"), PrintService.Pdf(Ticket.TestPage("Shop printer", "Admin page", DateTime.Now), "letter").Pdf);
        }
    }

    [Fact]
    public void Bad_tickets_are_refused()
    {
        Assert.Throws<BadTicketException>(() => Ticket.Parse(null));
        Assert.Throws<BadTicketException>(() => Ticket.Parse(new JsonObject { ["title"] = "x" }));
        var noColumns = TicketJson();
        ((JsonObject)noColumns["sections"]![0]!)["columns"] = new JsonArray();
        Assert.Throws<BadTicketException>(() => Ticket.Parse(noColumns));
        Assert.Throws<BadTicketException>(() => Ticket.Parse(TicketJson(Ticket.MaxRows + 1)));
    }

    // ---- Printer addresses and the list ---------------------------------------------------------

    [Theory]
    [InlineData("ipp", "192.168.1.50", "ipp://192.168.1.50/ipp/print")]
    [InlineData("ipp", "ipp://192.168.1.50:631/ipp/print", "ipp://192.168.1.50/ipp/print")]
    [InlineData("ipp", "https://printer.local:443/ipp/print", "ipps://printer.local:443/ipp/print")]
    [InlineData("ipp", "ipp://10.0.0.5:8631/printers/shop", "ipp://10.0.0.5:8631/printers/shop")]
    [InlineData("raw", "192.168.1.50", "192.168.1.50:9100")]
    [InlineData("raw", "socket://192.168.1.50:9101", "192.168.1.50:9101")]
    [InlineData("windows", " Shop HP LaserJet ", "Shop HP LaserJet")]
    public void Printer_addresses_are_cleaned(string kind, string address, string expected) =>
        Assert.Equal(expected, PrinterStore.CleanAddress(kind, address));

    [Theory]
    [InlineData("ipp", "")]
    [InlineData("ipp", "ftp://x/y")]
    [InlineData("raw", "192.168.1.50/path")]
    [InlineData("lpd", "x")]
    public void Bad_printer_addresses_are_refused(string kind, string address) =>
        Assert.Throws<BadPrinterException>(() => PrinterStore.CleanAddress(kind, address));

    [Fact]
    public async Task Printer_settings_need_the_admin_code_and_ipads_only_see_allowed_printers()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().GetAsync("/api/admin/printers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().PutAsJsonAsync("/api/admin/printers", new { printers = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().PostAsync("/api/admin/printers/scan", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/printers")).StatusCode);

        var admin = Client(await Unlock());
        var bad = await admin.PutAsJsonAsync("/api/admin/printers", new { printers = new[] { new { kind = "ipp", address = "ftp://nope", allowed = true } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var saved = await admin.PutAsJsonAsync("/api/admin/printers", new
        {
            printers = new object[]
            {
                new { name = "Shop floor", kind = "ipp", address = "192.168.1.50", allowed = true, location = "Bay 2" },
                new { name = "Office", kind = "windows", address = "Office HP", allowed = false },
            },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var list = (await saved.Content.ReadFromJsonAsync<JsonObject>())!["printers"]!.AsArray();
        Assert.Equal("ipp://192.168.1.50/ipp/print", (string)list[0]!["address"]!);
        Assert.Equal("letter", (string)list[0]!["paper"]!);
        Assert.True(File.Exists(Path.Combine(dataDir, "printers.json")));

        var offered = (await Client().GetFromJsonAsync<JsonObject>("/api/printers"))!["printers"]!.AsArray();
        Assert.Single(offered);
        Assert.Equal("Shop floor", (string)offered[0]!["name"]!);
        Assert.Null(offered[0]!["address"]); // the iPads don't need printer addresses

        // A printer that isn't allowed can't be printed on from an iPad.
        var office = (string)list[1]!["id"]!;
        var refused = await Client().PostAsJsonAsync("/api/print", new { printerId = office, ticket = TicketJson() });
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    [Fact]
    public async Task The_pdf_route_answers_a_pdf()
    {
        var reply = await Client().PostAsJsonAsync("/api/print/pdf", new { ticket = TicketJson(), paper = "letter" });
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal("application/pdf", reply.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(await reply.Content.ReadAsByteArrayAsync())[..5]);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsJsonAsync("/api/print/pdf", new { ticket = new { } })).StatusCode);
    }

    // ---- IPP --------------------------------------------------------------------------------------

    [Fact]
    public void Ipp_requests_and_answers_read_back()
    {
        var request = Ipp.Request(Ipp.PrintJob, 7, "ipp://p/ipp/print", b => b.NameAttr("job-name", "Ticket"), b => b.IntAttr("copies", 2));
        var parsed = Ipp.Parse(request);
        Assert.Equal(Ipp.PrintJob, parsed.Status); // a request's operation sits where an answer's status does
        Assert.Equal("ipp://p/ipp/print", parsed.Text("printer-uri"));
        Assert.Equal("Ticket", parsed.Text("job-name"));
        Assert.Equal(2, parsed.Int("copies"));
        var multi = Ipp.Parse(Ipp.Request(Ipp.GetPrinterAttributes, 1, "ipp://p/", b => b.Keywords("requested-attributes", "a", "b", "c")));
        Assert.Equal(["a", "b", "c"], multi.Texts("requested-attributes"));
        Assert.Equal(new Uri("http://p:631/ipp/print"), Ipp.HttpUri("ipp://p/ipp/print"));
        Assert.Equal(new Uri("https://p:8443/x"), Ipp.HttpUri("ipps://p:8443/x"));
    }

    // A pretend IPP printer on this machine: answers Get-Printer-Attributes and keeps what Print-Job sends.
    sealed class FakeIppPrinter : IAsyncDisposable
    {
        readonly WebApplication app;
        public List<(IppResponse Request, byte[] Document)> Jobs { get; } = [];
        public bool TakesPdf { get; init; } = true;
        public string Uri { get; private set; } = "";

        public FakeIppPrinter()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            app = builder.Build();
            app.MapPost("/ipp/print", async (HttpContext http) =>
            {
                using var ms = new MemoryStream();
                await http.Request.Body.CopyToAsync(ms);
                var body = ms.ToArray();
                var req = Ipp.Parse(body);
                var op = req.Status;
                // Find the end-of-attributes tag the parser stopped at: the document follows it.
                var docStart = FindEnd(body);
                var answer = new Ipp.RequestBuilder();
                answer.Bytes(1, 1);
                if (op == Ipp.GetPrinterAttributes)
                {
                    answer.Short(0);
                    answer.Int(1);
                    answer.Byte(0x01);
                    answer.Attr(0x47, "attributes-charset", "utf-8");
                    answer.Byte(0x04);
                    answer.Attr(0x42, "printer-name", "fake");
                    answer.Attr(0x41, "printer-make-and-model", "Fake LaserJet 9000");
                    answer.Attr(0x41, "printer-location", "Test bench");
                    answer.IntAttr("printer-state", 3);
                    answer.Attr(0x49, "document-format-supported", "application/octet-stream");
                    if (TakesPdf)
                        answer.Attr(0x49, "", "application/pdf");
                }
                else
                {
                    Jobs.Add((req, body[docStart..]));
                    answer.Short(TakesPdf ? 0 : 0x040A);
                    answer.Int(2);
                    answer.Byte(0x01);
                    answer.Attr(0x47, "attributes-charset", "utf-8");
                    answer.Byte(0x02);
                    answer.IntAttr("job-id", 42);
                }
                answer.Byte(0x03);
                return Results.Bytes(answer.ToArray(), "application/ipp");
            });
        }

        static int FindEnd(byte[] body)
        {
            // Walk the attributes the same way the parser does.
            var i = 8;
            while (i < body.Length)
            {
                var tag = body[i++];
                if (tag == 0x03)
                    return i;
                if (tag < 0x10)
                    continue;
                var nameLen = (body[i] << 8) | body[i + 1];
                i += 2 + nameLen;
                var valueLen = (body[i] << 8) | body[i + 1];
                i += 2 + valueLen;
            }
            return body.Length;
        }

        public async Task StartAsync()
        {
            await app.StartAsync();
            var url = new System.Uri(app.Urls.First());
            Uri = $"ipp://127.0.0.1:{url.Port}/ipp/print";
        }

        public async ValueTask DisposeAsync() => await app.DisposeAsync();
    }

    [Fact]
    public async Task An_ipad_prints_a_ticket_on_an_ipp_printer()
    {
        await using var printer = new FakeIppPrinter();
        await printer.StartAsync();
        var admin = Client(await Unlock());

        var check = await (await admin.PostAsJsonAsync("/api/admin/printers/check", new { kind = "ipp", address = printer.Uri })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True((bool)check!["ok"]!);
        Assert.Equal("Fake LaserJet 9000", (string)check["model"]!);
        Assert.True((bool)check["takesPdf"]!);

        var saved = await (await admin.PutAsJsonAsync("/api/admin/printers", new { printers = new[] { new { name = "Yard", kind = "ipp", address = printer.Uri, allowed = true } } }))
            .Content.ReadFromJsonAsync<JsonObject>();
        var id = (string)saved!["printers"]![0]!["id"]!;

        var reply = await Client().PostAsJsonAsync("/api/print", new { printerId = id, copies = 2, ticket = TicketJson() });
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var result = await reply.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("42", (string)result!["jobId"]!);
        Assert.Equal(1, (int)result["pages"]!);

        var (request, document) = Assert.Single(printer.Jobs);
        Assert.Equal("application/pdf", request.Text("document-format"));
        Assert.Equal("Shipping Ticket job EPM-001 load 12", request.Text("job-name"));
        Assert.Equal(2, request.Int("copies"));
        Assert.StartsWith("%PDF-1.4", Encoding.ASCII.GetString(document[..8]));

        // The admin board lists it, and it is in the activity history.
        var recent = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/printers"))!["recent"]!.AsArray();
        Assert.True((bool)recent[0]!["ok"]!);
        var activity = await Client().GetFromJsonAsync<JsonArray>("/api/activity?job=EPM-001");
        Assert.Contains(activity!, a => ((string)a!["text"]!).Contains("on Yard (2 copies)"));

        // And a test page from the admin board.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/printers/{id}/test", null)).StatusCode);
        Assert.Equal(2, printer.Jobs.Count);
    }

    [Fact]
    public async Task A_printer_that_cant_print_pdf_gives_the_ipad_a_reason()
    {
        await using var printer = new FakeIppPrinter { TakesPdf = false };
        await printer.StartAsync();
        var admin = Client(await Unlock());
        var saved = await (await admin.PutAsJsonAsync("/api/admin/printers", new { printers = new[] { new { name = "Old", kind = "ipp", address = printer.Uri, allowed = true } } }))
            .Content.ReadFromJsonAsync<JsonObject>();
        var reply = await Client().PostAsJsonAsync("/api/print", new { printerId = (string)saved!["printers"]![0]!["id"]!, ticket = TicketJson() });
        Assert.Equal(HttpStatusCode.BadGateway, reply.StatusCode);
        Assert.Contains("can't print PDF", (string)(await reply.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!);
    }

    [Fact]
    public async Task A_raw_printer_gets_the_pdf_inside_a_pjl_job()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var ms = new MemoryStream();
            await client.GetStream().CopyToAsync(ms);
            return ms.ToArray();
        });
        var admin = Client(await Unlock());
        var saved = await (await admin.PutAsJsonAsync("/api/admin/printers", new { printers = new[] { new { name = "Raw", kind = "raw", address = $"127.0.0.1:{port}", allowed = true } } }))
            .Content.ReadFromJsonAsync<JsonObject>();
        var reply = await Client().PostAsJsonAsync("/api/print", new { printerId = (string)saved!["printers"]![0]!["id"]!, copies = 3, ticket = TicketJson() });
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var bytes = Encoding.Latin1.GetString(await received.WaitAsync(TimeSpan.FromSeconds(10)));
        listener.Stop();
        Assert.StartsWith("\u001B%-12345X@PJL", bytes);
        Assert.Contains("@PJL SET COPIES=3", bytes);
        Assert.Contains("@PJL ENTER LANGUAGE=PDF\r\n%PDF-1.4", bytes);
        Assert.EndsWith("\u001B%-12345X", bytes);
    }

    [Fact]
    public async Task A_windows_printer_off_windows_says_why()
    {
        if (OperatingSystem.IsWindows())
            return; // on Windows it would really print
        var admin = Client(await Unlock());
        var saved = await (await admin.PutAsJsonAsync("/api/admin/printers", new { printers = new[] { new { kind = "windows", address = "Some printer", allowed = true } } }))
            .Content.ReadFromJsonAsync<JsonObject>();
        var reply = await Client().PostAsJsonAsync("/api/print", new { printerId = (string)saved!["printers"]![0]!["id"]!, ticket = TicketJson() });
        Assert.Equal(HttpStatusCode.BadGateway, reply.StatusCode);
        Assert.Contains("only be used when the server runs on Windows", (string)(await reply.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!);
    }

    // ---- Discovery ----------------------------------------------------------------------------------

    [Fact]
    public void Networks_are_searched_host_by_host()
    {
        Assert.Equal(IPAddress.Parse("192.168.1.0"), PrinterDiscovery.Network(IPAddress.Parse("192.168.1.77"), 24));
        Assert.Equal(IPAddress.Parse("10.4.0.0"), PrinterDiscovery.Network(IPAddress.Parse("10.4.3.9"), 22));
        var hosts = PrinterDiscovery.Hosts(IPAddress.Parse("192.168.1.0"), 24).ToList();
        Assert.Equal(254, hosts.Count);
        Assert.Equal("192.168.1.1", hosts[0].ToString());
        Assert.Equal("192.168.1.254", hosts[^1].ToString());
        Assert.Equal(1022, PrinterDiscovery.Hosts(IPAddress.Parse("10.4.0.0"), 22).Count());
    }

    // Builds an mDNS answer like a printer sends: PTR, SRV, TXT and A records, with name compression.
    static byte[] MdnsAnswer()
    {
        var s = new MemoryStream();
        void U16(int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        void Name(params string[] labels)
        {
            foreach (var l in labels)
            {
                var b = Encoding.UTF8.GetBytes(l);
                s.WriteByte((byte)b.Length);
                s.Write(b);
            }
            s.WriteByte(0);
        }
        s.Write([0, 0, 0x84, 0, 0, 0, 0, 4, 0, 0, 0, 0]); // answer, 4 records
        var service = (int)s.Position;
        Name("_ipp", "_tcp", "local");
        U16(12); U16(1); s.Write([0, 0, 0x11, 0x94]);
        var label = Encoding.UTF8.GetBytes("Shop HP");
        U16(1 + label.Length + 2);
        var instance = (int)s.Position;
        s.WriteByte((byte)label.Length); s.Write(label); U16(0xC000 | service);
        // SRV for the instance (pointer to it), port 631, target printer1.local
        U16(0xC000 | instance); U16(33); U16(0x8001); s.Write([0, 0, 0, 120]);
        var target = Encoding.UTF8.GetBytes("printer1");
        U16(6 + 1 + target.Length + 1 + 5 + 1);
        U16(0); U16(0); U16(631);
        var host = (int)s.Position;
        Name("printer1", "local");
        // TXT
        U16(0xC000 | instance); U16(16); U16(0x8001); s.Write([0, 0, 0, 120]);
        var txt = new[] { "rp=ipp/print", "ty=HP LaserJet M507", "note=Bay 2", "pdl=application/pdf,image/urf" }.Select(Encoding.UTF8.GetBytes).ToList();
        U16(txt.Sum(t => t.Length + 1));
        foreach (var t in txt) { s.WriteByte((byte)t.Length); s.Write(t); }
        // A
        U16(0xC000 | host); U16(1); U16(0x8001); s.Write([0, 0, 0, 120]);
        U16(4); s.Write([192, 168, 1, 50]);
        return s.ToArray();
    }

    [Fact]
    public void Bonjour_answers_become_printers()
    {
        var records = new PrinterDiscovery.MdnsRecords();
        records.Read(MdnsAnswer(), IPAddress.Parse("192.168.1.50"));
        var p = Assert.Single(records.Printers());
        Assert.Equal("ipp", p.Kind);
        Assert.Equal("ipp://192.168.1.50/ipp/print", p.Address);
        Assert.Equal("Shop HP", p.Name);
        Assert.Equal("HP LaserJet M507", p.Model);
        Assert.Equal("Bay 2", p.Location);
        Assert.True(p.TakesPdf);
        // Junk doesn't throw.
        records.Read(new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 9, 0, 0, 0, 0, 0xC0 }, IPAddress.Loopback);
        var query = PrinterDiscovery.MdnsQuery(["_ipp._tcp.local"]);
        Assert.Equal(1, query[5]);
        Assert.Equal(0x80, query[^2]); // asks for a unicast answer
    }
}
