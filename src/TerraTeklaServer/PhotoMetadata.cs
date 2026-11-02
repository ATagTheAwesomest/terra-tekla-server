using System.Buffers.Binary;
using System.Globalization;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace TerraTeklaServer;

/// <summary>Who took a photo and of what, written into the photo file itself.</summary>
/// <param name="User">The iPad's fixed number, "User 3".</param>
/// <param name="Name">The iPad's name as set on the admin board (may be the same as <paramref name="User"/>).</param>
public sealed record PhotoTag(string User, string Name, string DeviceId, string Job, string Load, string? Mark, DateTime TakenAt)
{
    /// <summary>"User 3", or "User 3 (Yard iPad)" when the iPad has its own name.</summary>
    public string Who => Name == "" || Name.Equals(User, StringComparison.OrdinalIgnoreCase) ? User : $"{User} ({Name})";

    public string Description => $"Job {Job} load {Load}{(string.IsNullOrEmpty(Mark) ? "" : $", {Mark}")}. Taken by {Who} with the TERRA app.";
}

/// <summary>
/// Writes <see cref="PhotoTag"/> into a JPEG the app sent, so the file says who took it wherever it
/// is copied: Windows Explorer (Properties › Details: Authors, Comments, Title), Photoshop,
/// Lightroom, exiftool. Two standard blocks are added, each only when the photo doesn't have one
/// already (nothing the camera wrote is changed or removed):
/// <list type="bullet">
/// <item>EXIF: Artist, ImageDescription, DateTime, and Windows' XPAuthor / XPComment.</item>
/// <item>XMP: dc:creator, dc:description, xmp:CreateDate, plus terra:User, terra:DeviceName,
/// terra:DeviceId, terra:Job, terra:Load, terra:Mark.</item>
/// </list>
/// The app's own camera makes plain JPEGs with neither, so they always get both. Other file types
/// (PNG, HEIC, WebP, PDF) are saved as sent; the database still records who took them.
/// </summary>
public static class PhotoMetadata
{
    const string XmpId = "http://ns.adobe.com/xap/1.0/\0";
    public const string TerraNs = "https://github.com/ATagTheAwesomest/terra-tekla-server/ns/photo/1.0/";

    /// <summary>The JPEG with the tag added, or the same bytes when it isn't a JPEG it can read.</summary>
    public static byte[] StampJpeg(byte[] jpeg, PhotoTag tag)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return jpeg;
        // Walk the APPn segments at the start (they hold the metadata) and note what is there.
        int pos = 2, afterApp0 = 2, afterApps = 2;
        bool hasExif = false, hasXmp = false;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF)
        {
            var marker = jpeg[pos + 1];
            if (marker < 0xE0 || marker > 0xEF)
                break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            if (length < 2 || pos + 2 + length > jpeg.Length)
                return jpeg;
            var body = jpeg.AsSpan(pos + 4, length - 2);
            if (marker == 0xE1 && body.StartsWith("Exif\0\0"u8))
                hasExif = true;
            if (marker == 0xE1 && body.StartsWith(Encoding.ASCII.GetBytes(XmpId)))
                hasXmp = true;
            pos += 2 + length;
            if (marker == 0xE0 && afterApp0 == afterApps)
                afterApp0 = pos;
            afterApps = pos;
        }

        var output = new MemoryStream(jpeg.Length + 4096);
        // EXIF goes first (straight after SOI, or after a JFIF APP0), as readers expect.
        var exifAt = hasExif ? -1 : afterApp0;
        var xmpAt = hasXmp ? -1 : afterApps;
        var written = 0;
        void CopyTo(int end)
        {
            output.Write(jpeg, written, end - written);
            written = end;
        }
        if (exifAt >= 0)
        {
            CopyTo(exifAt);
            Segment(output, 0xE1, [.. "Exif\0\0"u8, .. Exif(tag)]);
        }
        if (xmpAt >= 0)
        {
            CopyTo(xmpAt);
            Segment(output, 0xE1, [.. Encoding.ASCII.GetBytes(XmpId), .. Encoding.UTF8.GetBytes(Xmp(tag))]);
        }
        CopyTo(jpeg.Length);
        return output.ToArray();
    }

    static void Segment(Stream output, byte marker, byte[] body)
    {
        if (body.Length + 2 > 0xFFFF)
            return;
        output.WriteByte(0xFF);
        output.WriteByte(marker);
        output.WriteByte((byte)((body.Length + 2) >> 8));
        output.WriteByte((byte)((body.Length + 2) & 0xFF));
        output.Write(body);
    }

    // A little-endian TIFF block with one IFD: ImageDescription, DateTime, Artist, XPComment, XPAuthor.
    static byte[] Exif(PhotoTag tag)
    {
        var entries = new List<(ushort Tag, ushort Type, byte[] Value)>
        {
            (0x010E, 2, Ascii(tag.Description)),
            (0x0132, 2, Ascii(tag.TakenAt.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture))),
            (0x013B, 2, Ascii(tag.Who)),
            (0x9C9C, 1, Encoding.Unicode.GetBytes(tag.Description + "\0")),
            (0x9C9D, 1, Encoding.Unicode.GetBytes(tag.Who + "\0")),
        };
        var ifdSize = 2 + entries.Count * 12 + 4;
        var data = new MemoryStream();
        var tiff = new byte[8 + ifdSize];
        "II*\0"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), (ushort)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var (t, type, value) = entries[i];
            var at = 10 + i * 12;
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), t);
            BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 4), (uint)value.Length);
            if (value.Length <= 4)
                value.CopyTo(tiff.AsSpan(at + 8));
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(at + 8), (uint)(tiff.Length + data.Length));
                data.Write(value);
                if (data.Length % 2 == 1)
                    data.WriteByte(0); // values start on a word boundary
            }
        }
        return [.. tiff, .. data.ToArray()];
    }

    // EXIF ASCII fields are 7-bit: anything else becomes "?".
    static byte[] Ascii(string text) => [.. text.Select(c => c is >= ' ' and <= '~' ? (byte)c : (byte)'?'), 0];

    static string Xmp(PhotoTag tag)
    {
        static string X(string? v) => SecurityElement.Escape(v ?? "") ?? "";
        return $"""
            <?xpacket begin="{'﻿'}" id="W5M0MpCehiHzreSzNTczkc9d"?>
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
             <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
              <rdf:Description rdf:about=""
                xmlns:dc="http://purl.org/dc/elements/1.1/"
                xmlns:xmp="http://ns.adobe.com/xap/1.0/"
                xmlns:terra="{TerraNs}">
               <dc:creator><rdf:Seq><rdf:li>{X(tag.Who)}</rdf:li></rdf:Seq></dc:creator>
               <dc:description><rdf:Alt><rdf:li xml:lang="x-default">{X(tag.Description)}</rdf:li></rdf:Alt></dc:description>
               <xmp:CreateDate>{tag.TakenAt.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)}</xmp:CreateDate>
               <xmp:CreatorTool>TERRA</xmp:CreatorTool>
               <terra:User>{X(tag.User)}</terra:User>
               <terra:DeviceName>{X(tag.Name)}</terra:DeviceName>
               <terra:DeviceId>{X(tag.DeviceId)}</terra:DeviceId>
               <terra:Job>{X(tag.Job)}</terra:Job>
               <terra:Load>{X(tag.Load)}</terra:Load>
               <terra:Mark>{X(tag.Mark)}</terra:Mark>
              </rdf:Description>
             </rdf:RDF>
            </x:xmpmeta>
            <?xpacket end="w"?>
            """;
    }

    static readonly Regex TerraUser = new(@"<terra:User>([^<]*)</terra:User>", RegexOptions.CultureInvariant);
    static readonly Regex TerraName = new(@"<terra:DeviceName>([^<]*)</terra:DeviceName>", RegexOptions.CultureInvariant);

    /// <summary>
    /// The user a JPEG this server stamped says took it ("User 3", "Yard iPad"), read from its XMP, or
    /// null. Only the start of the file is read.
    /// </summary>
    public static (string User, string Name)? ReadUser(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var head = new byte[Math.Min(file.Length, 256 * 1024)];
            file.ReadExactly(head);
            if (head.Length < 4 || head[0] != 0xFF || head[1] != 0xD8)
                return null;
            var text = Encoding.UTF8.GetString(head);
            var user = TerraUser.Match(text);
            if (!user.Success)
                return null;
            var name = TerraName.Match(text);
            return (WebUnescape(user.Groups[1].Value), name.Success ? WebUnescape(name.Groups[1].Value) : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static string WebUnescape(string s) => System.Net.WebUtility.HtmlDecode(s);

    static readonly Regex SavedName = new(@"^\d{8}-\d{6}_(?<rest>.+?)(?: \(\d+\))?\.[A-Za-z0-9]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The iPad name in a file name this server made, "20261102-101500_1001-C1_Yard iPad.jpg"
    /// (see <see cref="PhotoStore.SaveAsync"/>), for photos the database has no record of (copied in, or
    /// from before it). Null when the name doesn't have one. A guess: a mark with "_" in it and no
    /// user part would read wrongly, which is why it is only the last fallback.
    /// </summary>
    public static string? UserFromFileName(string name)
    {
        var m = SavedName.Match(name);
        if (!m.Success)
            return null;
        var rest = m.Groups["rest"].Value;
        var cut = rest.LastIndexOf('_');
        return cut > 0 && cut < rest.Length - 1 ? rest[(cut + 1)..] : null;
    }
}
