using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Net.Sockets;

namespace ELink.Indi.Protocol;

/// <summary>Reads and writes the INDI XML wire protocol (v1.7) over a byte stream.</summary>
public static class IndiCodec
{
    public const string ProtocolVersion = "1.7";

    // ---- reading -------------------------------------------------------------------------------------------

    public static async IAsyncEnumerable<IndiMessage> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var framer = new IndiFramer();
        var readerSettings = new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Prohibit };
        var chunk = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException) { yield break; }
            if (n == 0) yield break;
            framer.Append(chunk.AsSpan(0, n));
            while (framer.TryTake(out string? xml))
            {
                IndiMessage? message = null;
                try
                {
                    using var xr = XmlReader.Create(new StringReader(xml!), readerSettings);
                    message = Parse(XElement.Load(xr));
                }
                catch (XmlException) { /* malformed element: skip it, keep the connection */ }
                if (message is not null) yield return message;
            }
        }
    }

    public static IndiMessage? Parse(XElement e)
    {
        string tag = e.Name.LocalName;
        switch (tag)
        {
            case "defTextVector": return ParseDefine(e, IndiPropertyType.Text, "defText");
            case "defNumberVector": return ParseDefine(e, IndiPropertyType.Number, "defNumber");
            case "defSwitchVector": return ParseDefine(e, IndiPropertyType.Switch, "defSwitch");
            case "defLightVector": return ParseDefine(e, IndiPropertyType.Light, "defLight");
            case "defBLOBVector": return ParseDefine(e, IndiPropertyType.Blob, "defBLOB");
            case "setTextVector": return ParseSet(e, IndiPropertyType.Text, "oneText");
            case "setNumberVector": return ParseSet(e, IndiPropertyType.Number, "oneNumber");
            case "setSwitchVector": return ParseSet(e, IndiPropertyType.Switch, "oneSwitch");
            case "setLightVector": return ParseSet(e, IndiPropertyType.Light, "oneLight");
            case "setBLOBVector": return ParseSet(e, IndiPropertyType.Blob, "oneBLOB");
            case "delProperty":
                return new IndiDelete(Attr(e, "device") ?? "", Attr(e, "name"), Attr(e, "message"));
            case "message":
                return new IndiLog(Attr(e, "device"), ParseTime(Attr(e, "timestamp")), Attr(e, "message") ?? "");
            default:
                return null; // unknown element: ignore, the protocol is open-ended
        }
    }

    private static IndiDefine ParseDefine(XElement e, IndiPropertyType type, string childTag)
    {
        var elements = ImmutableArray.CreateBuilder<IndiElement>();
        foreach (var c in e.Elements().Where(x => x.Name.LocalName == childTag))
        {
            string name = Attr(c, "name") ?? "";
            string label = Attr(c, "label") ?? name;
            string value = type == IndiPropertyType.Blob ? "" : c.Value.Trim();
            elements.Add(type == IndiPropertyType.Number
                ? new IndiElement(name, label, value, Attr(c, "format"), Num(c, "min"), Num(c, "max"), Num(c, "step"))
                : new IndiElement(name, label, value));
        }
        var name0 = Attr(e, "name") ?? "";
        var property = new IndiProperty(
            Attr(e, "device") ?? "", name0, Attr(e, "label") ?? name0, Attr(e, "group") ?? "",
            type, IndiEnums.ParseState(Attr(e, "state")),
            type is IndiPropertyType.Light ? IndiPerm.ReadOnly : IndiEnums.ParsePerm(Attr(e, "perm")),
            IndiEnums.ParseRule(Attr(e, "rule")),
            Num(e, "timeout"), ParseTime(Attr(e, "timestamp")), elements.ToImmutable());
        return new IndiDefine(property, Attr(e, "message"));
    }

    private static IndiSet ParseSet(XElement e, IndiPropertyType type, string childTag)
    {
        var updates = ImmutableArray.CreateBuilder<IndiElementUpdate>();
        foreach (var c in e.Elements().Where(x => x.Name.LocalName == childTag))
        {
            string name = Attr(c, "name") ?? "";
            if (type == IndiPropertyType.Blob)
            {
                byte[] data;
                try { data = Convert.FromBase64String(c.Value); } catch (FormatException) { data = Array.Empty<byte>(); }
                updates.Add(new IndiElementUpdate(name, Attr(c, "size") ?? data.Length.ToString(CultureInfo.InvariantCulture), data, Attr(c, "format")));
            }
            else
            {
                updates.Add(new IndiElementUpdate(name, c.Value.Trim()));
            }
        }
        string? state = Attr(e, "state");
        return new IndiSet(type, Attr(e, "device") ?? "", Attr(e, "name") ?? "",
            state is null ? null : IndiEnums.ParseState(state),
            Attr(e, "timeout") is { } t && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var to) ? to : null,
            ParseTime(Attr(e, "timestamp")), Attr(e, "message"), updates.ToImmutable());
    }

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

    private static double Num(XElement e, string name) =>
        Attr(e, name) is { } s ? IndiNumber.Parse(s) is var v && !double.IsNaN(v) ? v : 0 : 0;

    private static DateTime? ParseTime(string? s) =>
        s is not null && DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;

    // ---- writing -------------------------------------------------------------------------------------------

    public static string Serialize(IndiCommand command)
    {
        XElement e = command switch
        {
            IndiGetProperties g => Opt(new XElement("getProperties", new XAttribute("version", ProtocolVersion)), ("device", g.Device), ("name", g.Name)),
            IndiEnableBlob b => Opt(new XElement("enableBLOB", new XAttribute("device", b.Device), b.Mode.ToWire()), ("name", b.Name)),
            IndiNew n => SerializeNew(n),
            _ => throw new ArgumentException("unknown command " + command.GetType().Name),
        };
        return e.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement SerializeNew(IndiNew n)
    {
        (string vector, string one) = n.Type switch
        {
            IndiPropertyType.Text => ("newTextVector", "oneText"),
            IndiPropertyType.Number => ("newNumberVector", "oneNumber"),
            IndiPropertyType.Switch => ("newSwitchVector", "oneSwitch"),
            IndiPropertyType.Blob => ("newBLOBVector", "oneBLOB"),
            _ => throw new ArgumentException("lights are read-only"),
        };
        var root = new XElement(vector, new XAttribute("device", n.Device), new XAttribute("name", n.Name),
            new XAttribute("timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)));
        foreach (var el in n.Elements)
        {
            var child = new XElement(one, new XAttribute("name", el.Name));
            if (n.Type == IndiPropertyType.Blob && el.Blob is not null)
            {
                child.Add(new XAttribute("size", el.Blob.Length), new XAttribute("format", el.BlobFormat ?? ""), Convert.ToBase64String(el.Blob));
            }
            else child.Value = el.Value;
            root.Add(child);
        }
        return root;
    }

    private static XElement Opt(XElement e, params (string name, string? value)[] attrs)
    {
        foreach (var (n, v) in attrs) if (v is not null) e.SetAttributeValue(n, v);
        return e;
    }

    /// <summary>Decompresses a ".z" (zlib) BLOB; other formats are returned as-is.</summary>
    public static byte[] Decompress(byte[] data, string? format)
    {
        if (format is null || !format.EndsWith(".z", StringComparison.Ordinal)) return data;
        using var input = new MemoryStream(data);
        using var z = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }
}
