using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace TerraTeklaServer.Printing;

/// <summary>A printer the scan found. <paramref name="Sources"/>: windows, mdns (Bonjour) and/or scan (port probe).</summary>
public sealed record FoundPrinter(
    string Kind, string Address, string Name, string? Model, string? Location, string? Ip,
    List<string> Sources, bool? TakesPdf, string? State, string? Note);

public sealed record ScanResult(List<FoundPrinter> Printers, List<string> Networks, List<string> Notes, long Ms);

/// <summary>
/// Looks for printers three ways at once: the printers installed on Windows; Bonjour / mDNS
/// (_ipp, _ipps and _pdl-datastream services, which most network printers announce); and a probe of
/// every address on the server's own networks (/24, or up to a /22) for IPP (631) and raw (9100)
/// ports. IPP printers found are then asked their name, model and whether they take PDF.
/// </summary>
public sealed class PrinterDiscovery(IHttpClientFactory http, ILogger<PrinterDiscovery> log)
{
    static readonly string[] Services = ["_ipp._tcp.local", "_ipps._tcp.local", "_pdl-datastream._tcp.local"];
    static readonly string[] IppPaths = ["/ipp/print", "/ipp", "/ipp/printer", "/"];
    const int MaxHosts = 1022;

    public async Task<ScanResult> ScanAsync(CancellationToken ct)
    {
        var started = Environment.TickCount64;
        var notes = new List<string>();
        var networks = LocalNetworks();
        if (networks.Count == 0)
            notes.Add("The server has no IPv4 network to search.");

        var windows = Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows())
                return new List<FoundPrinter>();
            try
            {
                return WindowsPrinting.Installed().Select(p => new FoundPrinter("windows", p.Name, p.Name, null, null, null, ["windows"], null,
                    p.IsDefault ? "Windows default printer" : null, "Installed on the server: printed through its own Windows driver.")).ToList();
            }
            catch (Exception ex)
            {
                lock (notes) notes.Add($"Couldn't list the Windows printers: {ex.Message}");
                return [];
            }
        }, ct);
        var mdns = MdnsAsync(networks, TimeSpan.FromSeconds(3), notes, ct);
        var probe = ProbeAsync(networks, ct);
        await Task.WhenAll(windows, mdns, probe);

        var found = new List<FoundPrinter>(windows.Result);
        var byIp = new Dictionary<string, List<FoundPrinter>>();
        void Add(FoundPrinter p)
        {
            var same = found.FirstOrDefault(f => f.Kind == p.Kind && SameAddress(f.Address, p.Address));
            if (same is not null)
            {
                foreach (var s in p.Sources.Where(s => !same.Sources.Contains(s)))
                    same.Sources.Add(s);
                return;
            }
            found.Add(p);
        }
        // A printer announced as both ipp and ipps is listed once, as ipp.
        foreach (var p in mdns.Result.Where(p => !(p.Address.StartsWith("ipps:") && mdns.Result.Any(o => o.Ip == p.Ip && o.Address.StartsWith("ipp:")))))
            Add(p);

        // Probe hits that Bonjour didn't announce: ask port 631 for IPP, list 9100 as raw.
        var asked = await Task.WhenAll(probe.Result.Where(h => h.Ipp && !found.Any(f => f.Kind == "ipp" && f.Ip == h.Ip))
            .Select(h => IppAtAsync(h.Ip, ct)));
        foreach (var p in asked.Where(p => p is not null))
            Add(p!);
        foreach (var h in probe.Result)
        {
            if (h.Ipp && !found.Any(f => f.Ip == h.Ip && f.Kind == "ipp"))
                Add(new FoundPrinter("ipp", $"ipp://{h.Ip}/ipp/print", h.Ip, null, null, h.Ip, ["scan"], null, null, "Port 631 is open but it didn't answer as a printer at the usual addresses."));
            if (h.Raw)
            {
                var named = found.FirstOrDefault(f => f.Ip == h.Ip);
                Add(new FoundPrinter("raw", $"{h.Ip}:9100", named?.Name ?? h.Ip, named?.Model, named?.Location, h.Ip, ["scan"], null, null,
                    "Raw port 9100: only for printers that print PDF sent straight to them."));
            }
        }

        // Fill in IPP details for the Bonjour ones (name, model, PDF).
        var details = await Task.WhenAll(found.Select(async f =>
        {
            if (f.Kind != "ipp" || f.State is not null)
                return f;
            var check = await CheckIppAsync(f.Address, ct);
            return check is null ? f : f with
            {
                Model = f.Model ?? check.Model,
                Location = f.Location ?? check.Location,
                TakesPdf = check.TakesPdf ?? f.TakesPdf,
                State = check.State,
            };
        }));

        var order = new Dictionary<string, int> { ["windows"] = 0, ["ipp"] = 1, ["raw"] = 2 };
        var list = details.OrderBy(f => order[f.Kind]).ThenBy(f => IpKey(f.Ip)).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new ScanResult(list, networks.Select(n => $"{n.Network}/{n.Prefix}").ToList(), notes, Environment.TickCount64 - started);
    }

    static bool SameAddress(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    static string IpKey(string? ip) => IPAddress.TryParse(ip, out var a) && a.AddressFamily == AddressFamily.InterNetwork
        ? string.Concat(a.GetAddressBytes().Select(x => x.ToString("D3"))) : ip ?? "";

    async Task<PrinterCheck?> CheckIppAsync(string uri, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var r = await Ipp.GetAttributesAsync(http.CreateClient(PrintService.HttpClientName), uri, timeout.Token);
            if (!r.Ok)
                return null;
            var formats = r.Texts("document-format-supported").ToArray();
            var state = r.Int("printer-state") switch { 3 => "idle", 4 => "printing", 5 => "stopped", _ => "ready" };
            return new PrinterCheck(true, null, r.Text("printer-info") ?? r.Text("printer-name"), r.Text("printer-make-and-model"), r.Text("printer-location"), state,
                formats.Length == 0 ? null : formats.Contains("application/pdf", StringComparer.OrdinalIgnoreCase), formats);
        }
        catch (Exception ex) when (ex is PrinterException or HttpRequestException or OperationCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // Tries the usual IPP paths on a host whose port 631 is open.
    async Task<FoundPrinter?> IppAtAsync(string ip, CancellationToken ct)
    {
        foreach (var path in IppPaths)
        {
            var uri = $"ipp://{ip}{path}";
            if (await CheckIppAsync(uri, ct) is { } c)
                return new FoundPrinter("ipp", uri, c.Name is { Length: > 0 } n ? n : ip, c.Model, c.Location, ip, ["scan"], c.TakesPdf, c.State, null);
        }
        return null;
    }

    // ---- The server's networks -----------------------------------------------------------------

    public sealed record LocalNet(IPAddress Self, IPAddress Network, int Prefix);

    public static List<LocalNet> LocalNetworks()
    {
        var list = new List<LocalNet>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(u.Address))
                        continue;
                    var b = u.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254)
                        continue; // no DHCP answer: not a real network
                    var prefix = u.PrefixLength is >= 22 and <= 30 ? u.PrefixLength : 24; // bigger networks: the server's own /24
                    var net = Network(u.Address, prefix);
                    if (!list.Any(n => n.Network.Equals(net) && n.Prefix == prefix))
                        list.Add(new LocalNet(u.Address, net, prefix));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // nothing to search
        }
        return list;
    }

    public static IPAddress Network(IPAddress ip, int prefix)
    {
        var v = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        var mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, v & mask);
        return new IPAddress(bytes);
    }

    /// <summary>Every host address on a network (not the network and broadcast addresses), at most <see cref="MaxHosts"/>.</summary>
    public static IEnumerable<IPAddress> Hosts(IPAddress network, int prefix)
    {
        var start = BinaryPrimitives.ReadUInt32BigEndian(network.GetAddressBytes());
        var count = (int)Math.Min((1L << (32 - prefix)) - 2, MaxHosts);
        for (var i = 1; i <= count; i++)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, start + (uint)i);
            yield return new IPAddress(bytes);
        }
    }

    // ---- Port probe ------------------------------------------------------------------------------

    sealed record Hit(string Ip, bool Ipp, bool Raw);

    async Task<List<Hit>> ProbeAsync(List<LocalNet> networks, CancellationToken ct)
    {
        var hosts = networks.SelectMany(n => Hosts(n.Network, n.Prefix).Where(h => !h.Equals(n.Self))).Distinct().ToList();
        using var gate = new SemaphoreSlim(128);
        var results = await Task.WhenAll(hosts.Select(async host =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var ipp = OpenAsync(host, 631, ct);
                var raw = OpenAsync(host, 9100, ct);
                return new Hit(host.ToString(), await ipp, await raw);
            }
            finally
            {
                gate.Release();
            }
        }));
        return results.Where(h => h.Ipp || h.Raw).ToList();
    }

    public static async Task<bool> OpenAsync(IPAddress host, int port, CancellationToken ct, int ms = 400)
    {
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ms);
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    // ---- Bonjour / mDNS --------------------------------------------------------------------------

    async Task<List<FoundPrinter>> MdnsAsync(List<LocalNet> networks, TimeSpan wait, List<string> notes, CancellationToken ct)
    {
        var records = new MdnsRecords();
        var query = MdnsQuery(Services);
        var group = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
        var tasks = networks.Select(n => n.Self).Distinct().Select(async self =>
        {
            try
            {
                // From a random port, so printers answer straight back to it (RFC 6762 6.7) and
                // Windows' own mDNS service on 5353 doesn't matter.
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(self, 0));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, self.GetAddressBytes());
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                await socket.SendToAsync(query, SocketFlags.None, group, ct);
                using var until = CancellationTokenSource.CreateLinkedTokenSource(ct);
                until.CancelAfter(wait);
                var buffer = new byte[9000];
                // A second query after a moment catches printers that missed the first.
                var again = Task.Delay(800, until.Token).ContinueWith(_ => socket.SendToAsync(query, SocketFlags.None, group),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default).Unwrap();
                while (!until.IsCancellationRequested)
                {
                    try
                    {
                        var r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), until.Token);
                        lock (records)
                            records.Read(buffer.AsSpan(0, r.ReceivedBytes), ((IPEndPoint)r.RemoteEndPoint).Address);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (SocketException)
                    {
                        // a bad packet or ICMP noise: keep listening
                    }
                }
                try { await again; } catch (Exception) { /* the second query is only a nice-to-have */ }
            }
            catch (SocketException ex)
            {
                lock (notes)
                    notes.Add($"Bonjour search on {self} didn't run: {ex.Message}");
                log.LogDebug(ex, "mDNS on {Address}", self);
            }
        });
        await Task.WhenAll(tasks);
        lock (records)
            return records.Printers();
    }

    /// <summary>An mDNS query for the PTR records of these services, asking for unicast answers.</summary>
    public static byte[] MdnsQuery(IEnumerable<string> services)
    {
        var s = new MemoryStream();
        var list = services.ToList();
        s.Write([0, 0, 0, 0, 0, (byte)list.Count, 0, 0, 0, 0, 0, 0]);
        foreach (var name in list)
        {
            foreach (var label in name.Split('.'))
            {
                var b = Encoding.UTF8.GetBytes(label);
                s.WriteByte((byte)b.Length);
                s.Write(b);
            }
            s.WriteByte(0);
            s.Write([0, 12, 0x80, 1]); // PTR, IN with the "unicast answer please" bit
        }
        return s.ToArray();
    }

    /// <summary>What mDNS answers said: services, their hosts and ports, TXT values and addresses.</summary>
    public sealed class MdnsRecords
    {
        readonly Dictionary<string, HashSet<string>> ptr = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, (string Target, int Port)> srv = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Dictionary<string, string>> txt = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> a = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> from = new(StringComparer.OrdinalIgnoreCase); // instance -> address the answer came from

        public void Read(ReadOnlySpan<byte> p, IPAddress sender)
        {
            if (p.Length < 12)
                return;
            int qd = BinaryPrimitives.ReadUInt16BigEndian(p[4..]), an = BinaryPrimitives.ReadUInt16BigEndian(p[6..]),
                ns = BinaryPrimitives.ReadUInt16BigEndian(p[8..]), ar = BinaryPrimitives.ReadUInt16BigEndian(p[10..]);
            var i = 12;
            try
            {
                for (var q = 0; q < qd; q++)
                {
                    ReadName(p, ref i);
                    i += 4;
                }
                for (var r = 0; r < an + ns + ar && i + 10 <= p.Length; r++)
                {
                    var name = ReadName(p, ref i);
                    var type = BinaryPrimitives.ReadUInt16BigEndian(p[i..]);
                    var len = BinaryPrimitives.ReadUInt16BigEndian(p[(i + 8)..]);
                    i += 10;
                    if (i + len > p.Length)
                        return;
                    var data = i;
                    switch (type)
                    {
                        case 12: // PTR
                        {
                            var j = data;
                            var target = ReadName(p, ref j);
                            if (!ptr.TryGetValue(name, out var set))
                                ptr[name] = set = new(StringComparer.OrdinalIgnoreCase);
                            set.Add(target);
                            from.TryAdd(target, sender.ToString());
                            break;
                        }
                        case 33: // SRV
                        {
                            var port = BinaryPrimitives.ReadUInt16BigEndian(p[(data + 4)..]);
                            var j = data + 6;
                            srv[name] = (ReadName(p, ref j), port);
                            from.TryAdd(name, sender.ToString());
                            break;
                        }
                        case 16: // TXT
                        {
                            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            var j = data;
                            while (j < data + len)
                            {
                                var l = p[j++];
                                if (j + l > data + len)
                                    break;
                                var kv = Encoding.UTF8.GetString(p.Slice(j, l));
                                j += l;
                                var eq = kv.IndexOf('=');
                                if (eq > 0)
                                    values[kv[..eq]] = kv[(eq + 1)..];
                            }
                            txt[name] = values;
                            break;
                        }
                        case 1 when len == 4: // A
                            a[name] = new IPAddress(p.Slice(data, 4)).ToString();
                            break;
                    }
                    i = data + len;
                }
            }
            catch (IndexOutOfRangeException)
            {
                // a cut-off packet: keep what was read
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        static string ReadName(ReadOnlySpan<byte> p, ref int i)
        {
            var labels = new List<string>();
            var pos = i;
            var jumped = false;
            for (var hops = 0; hops < 64; hops++)
            {
                var len = p[pos];
                if (len == 0)
                {
                    pos++;
                    break;
                }
                if ((len & 0xC0) == 0xC0)
                {
                    var target = ((len & 0x3F) << 8) | p[pos + 1];
                    if (!jumped)
                        i = pos + 2;
                    jumped = true;
                    pos = target;
                    continue;
                }
                labels.Add(Encoding.UTF8.GetString(p.Slice(pos + 1, len)));
                pos += 1 + len;
            }
            if (!jumped)
                i = pos;
            return string.Join('.', labels);
        }

        public List<FoundPrinter> Printers()
        {
            var list = new List<FoundPrinter>();
            foreach (var service in Services)
            {
                if (!ptr.TryGetValue(service, out var instances))
                    continue;
                foreach (var instance in instances)
                {
                    if (!srv.TryGetValue(instance, out var s))
                        continue;
                    var ip = a.TryGetValue(s.Target, out var addr) ? addr : from.TryGetValue(instance, out var f) ? f : null;
                    if (ip is null)
                        continue;
                    txt.TryGetValue(instance, out var t);
                    t ??= [];
                    var label = instance.EndsWith("." + service, StringComparison.OrdinalIgnoreCase) ? instance[..^(service.Length + 1)] : instance;
                    label = label.Replace("\\032", " ").Replace("\\", "");
                    var name = t.TryGetValue("ty", out var ty) && ty != "" ? ty : label;
                    var location = t.TryGetValue("note", out var note) && note != "" ? note : null;
                    var pdl = t.TryGetValue("pdl", out var formats) ? formats : null;
                    bool? pdf = pdl is null ? null : pdl.Contains("application/pdf", StringComparison.OrdinalIgnoreCase);
                    if (service.StartsWith("_pdl"))
                    {
                        list.Add(new FoundPrinter("raw", $"{ip}:{s.Port}", label, ty, location, ip, ["mdns"], pdf, null,
                            "Raw port: only for printers that print PDF sent straight to them."));
                        continue;
                    }
                    var rp = t.TryGetValue("rp", out var path) && path != "" ? path.TrimStart('/') : "ipp/print";
                    var scheme = service.StartsWith("_ipps") ? "ipps" : "ipp";
                    var port = s.Port is 631 or 0 ? "" : $":{s.Port}";
                    list.Add(new FoundPrinter("ipp", $"{scheme}://{ip}{port}/{rp}", label, ty, location, ip, ["mdns"], pdf, null, null));
                }
            }
            return list;
        }
    }
}
