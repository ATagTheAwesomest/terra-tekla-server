using System.Buffers.Binary;
using System.Text;

namespace TerraTeklaServer.Printing;

/// <summary>A printer answered with an error, or couldn't be reached.</summary>
public sealed class PrinterException(string message) : Exception(message);

/// <summary>An IPP answer: its status code and attributes (all groups together).</summary>
public sealed record IppResponse(int Status, Dictionary<string, List<object>> Attributes)
{
    public bool Ok => Status < 0x0100;

    public string? Text(string name) => Attributes.TryGetValue(name, out var v) && v.Count > 0 ? v[0]?.ToString() : null;

    public IEnumerable<string> Texts(string name) => Attributes.TryGetValue(name, out var v) ? v.Select(x => x?.ToString() ?? "") : [];

    public int? Int(string name) => Attributes.TryGetValue(name, out var v) && v.Count > 0 && v[0] is int i ? i : null;
}

/// <summary>
/// Just enough of IPP (RFC 8010/8011) to ask a network printer what it is and send it a PDF:
/// Get-Printer-Attributes and Print-Job, over http (ipp://) or https (ipps://).
/// </summary>
public static class Ipp
{
    public const int PrintJob = 0x0002;
    public const int GetPrinterAttributes = 0x000B;

    const byte OperationTag = 0x01, JobTag = 0x02, EndTag = 0x03;
    const byte Integer = 0x21, Boolean = 0x22, Enum = 0x23, Keyword = 0x44, Uri = 0x45, Charset = 0x47, Language = 0x48, MimeType = 0x49, Name = 0x42;

    /// <summary>The http(s) address to post to for an ipp:// or ipps:// printer address.</summary>
    public static Uri HttpUri(string printerUri)
    {
        var u = new Uri(printerUri);
        var scheme = u.Scheme is "ipps" or "https" ? "https" : "http";
        var port = u.IsDefaultPort || u.Port <= 0 ? 631 : u.Port;
        return new UriBuilder(scheme, u.Host, port, u.AbsolutePath).Uri;
    }

    public static byte[] Request(int operation, int requestId, string printerUri, Action<RequestBuilder>? operationAttrs = null, Action<RequestBuilder>? jobAttrs = null)
    {
        var b = new RequestBuilder();
        b.Bytes(1, 1);
        b.Short(operation);
        b.Int(requestId);
        b.Byte(OperationTag);
        b.Attr(Charset, "attributes-charset", "utf-8");
        b.Attr(Language, "attributes-natural-language", "en");
        b.Attr(Uri, "printer-uri", printerUri);
        operationAttrs?.Invoke(b);
        if (jobAttrs is not null)
        {
            b.Byte(JobTag);
            jobAttrs(b);
        }
        b.Byte(EndTag);
        return b.ToArray();
    }

    public sealed class RequestBuilder
    {
        readonly MemoryStream s = new();
        public void Byte(byte v) => s.WriteByte(v);
        public void Bytes(params byte[] v) => s.Write(v);
        public void Short(int v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, (short)v); s.Write(b); }
        public void Int(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); s.Write(b); }

        public void Attr(byte tag, string name, string value)
        {
            var v = Encoding.UTF8.GetBytes(value);
            Head(tag, name, v.Length);
            s.Write(v);
        }

        public void IntAttr(string name, int value)
        {
            Head(Integer, name, 4);
            Int(value);
        }

        public void Keywords(string name, params string[] values)
        {
            for (var i = 0; i < values.Length; i++)
                Attr(Keyword, i == 0 ? name : "", values[i]);
        }

        public void NameAttr(string name, string value) => Attr(Name, name, value);
        public void MimeAttr(string name, string value) => Attr(MimeType, name, value);

        void Head(byte tag, string name, int valueLength)
        {
            var n = Encoding.UTF8.GetBytes(name);
            Byte(tag);
            Short(n.Length);
            s.Write(n);
            Short(valueLength);
        }

        public byte[] ToArray() => s.ToArray();
    }

    public static IppResponse Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            throw new PrinterException("The printer's IPP answer was too short.");
        var status = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var attrs = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        var i = 8;
        string? last = null;
        while (i < data.Length)
        {
            var tag = data[i++];
            if (tag == EndTag)
                break;
            if (tag < 0x10)
                continue; // a group starts
            if (i + 2 > data.Length)
                break;
            var nameLen = BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
            i += 2;
            if (i + nameLen + 2 > data.Length)
                break;
            var name = Encoding.UTF8.GetString(data.Slice(i, nameLen));
            i += nameLen;
            var valueLen = BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
            i += 2;
            if (i + valueLen > data.Length)
                break;
            var raw = data.Slice(i, valueLen);
            i += valueLen;
            object value = tag switch
            {
                Integer or Enum when valueLen == 4 => BinaryPrimitives.ReadInt32BigEndian(raw),
                Boolean when valueLen == 1 => raw[0] != 0,
                >= 0x40 and <= 0x4F or 0x35 or 0x36 => Encoding.UTF8.GetString(raw),
                _ => Convert.ToHexString(raw),
            };
            if (name != "")
                last = name;
            if (last is null)
                continue;
            if (!attrs.TryGetValue(last, out var list))
                attrs[last] = list = [];
            list.Add(value);
        }
        return new IppResponse(status, attrs);
    }

    public static string StatusText(IppResponse r)
    {
        var why = r.Status switch
        {
            0x0400 => "it didn't understand the request",
            0x0401 or 0x0403 => "it refused (it may want a user name or password, or IPP printing may be off)",
            0x0402 => "it wants a login",
            0x0406 => "that printer address wasn't found on it",
            0x040A => "it can't print PDF files (install it on the Windows server and use the Windows printer instead)",
            0x040B => "it can't use one of the options sent",
            0x0500 => "it had an internal error",
            0x0501 => "it doesn't support that",
            0x0506 => "it isn't taking jobs right now",
            0x0507 => "it is busy; try again in a moment",
            _ => $"status 0x{r.Status:X4}",
        };
        return r.Text("status-message") is { Length: > 0 } msg ? $"{why} ({msg})" : why;
    }

    /// <summary>Posts an IPP request and reads the answer.</summary>
    public static async Task<IppResponse> SendAsync(HttpClient http, string printerUri, byte[] request, byte[]? document, CancellationToken ct)
    {
        var body = document is null ? request : [.. request, .. document];
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ipp");
        HttpResponseMessage reply;
        try
        {
            reply = await http.PostAsync(HttpUri(printerUri), content, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new PrinterException($"Couldn't reach the printer at {printerUri}. Is it on and on the network? ({ex.Message.TrimEnd('.')})");
        }
        using (reply)
        {
            if (!reply.IsSuccessStatusCode)
                throw new PrinterException($"The printer at {printerUri} answered HTTP {(int)reply.StatusCode}. Check the address (the part after the port, often /ipp/print).");
            return Parse(await reply.Content.ReadAsByteArrayAsync(ct));
        }
    }

    public static readonly string[] InfoAttributes =
    [
        "printer-name", "printer-info", "printer-make-and-model", "printer-location", "printer-state", "printer-state-reasons",
        "document-format-supported", "printer-is-accepting-jobs", "media-default",
    ];

    public static Task<IppResponse> GetAttributesAsync(HttpClient http, string printerUri, CancellationToken ct) =>
        SendAsync(http, printerUri, Request(GetPrinterAttributes, 1, printerUri, b =>
        {
            b.NameAttr("requesting-user-name", "terra");
            b.Keywords("requested-attributes", InfoAttributes);
        }), null, ct);

    public static Task<IppResponse> PrintPdfAsync(HttpClient http, string printerUri, byte[] pdf, string jobName, string user, int copies, CancellationToken ct) =>
        SendAsync(http, printerUri, Request(PrintJob, 2, printerUri,
            b =>
            {
                b.NameAttr("requesting-user-name", user);
                b.NameAttr("job-name", jobName);
                b.MimeAttr("document-format", "application/pdf");
            },
            copies > 1 ? b => b.IntAttr("copies", copies) : null), pdf, ct);
}
