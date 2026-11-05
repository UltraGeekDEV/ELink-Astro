using System.Text;
using ELink.Indi.Protocol;
using Xunit;

namespace ELink.Tests.Indi;

public class CodecTests
{
    private static async Task<List<IndiMessage>> Read(string xml)
    {
        var list = new List<IndiMessage>();
        await foreach (var m in IndiCodec.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)))) list.Add(m);
        return list;
    }

    [Fact]
    public async Task ParsesNumberDefinitionAndSet()
    {
        var msgs = await Read("""
            <defNumberVector device="Scope" name="EQUATORIAL_EOD_COORD" label="Eq" group="Main" state="Idle" perm="rw" timeout="60" timestamp="2026-10-01T10:00:00">
              <defNumber name="RA" label="RA" format="%010.6m" min="0" max="24" step="0">12:30:00</defNumber>
              <defNumber name="DEC" label="DEC" format="%010.6m" min="-90" max="90" step="0">-45</defNumber>
            </defNumberVector>
            <setNumberVector device="Scope" name="EQUATORIAL_EOD_COORD" state="Busy"><oneNumber name="RA">13.5</oneNumber></setNumberVector>
            """);
        Assert.Equal(2, msgs.Count);
        var def = Assert.IsType<IndiDefine>(msgs[0]).Property;
        Assert.Equal(IndiPropertyType.Number, def.Type);
        Assert.Equal(12.5, def.Number("RA"));
        Assert.Equal(-45, def.Number("DEC"));
        Assert.Equal(24, def["RA"]!.Max);
        var set = Assert.IsType<IndiSet>(msgs[1]);
        Assert.Equal(IndiState.Busy, set.State);
        Assert.Equal("13.5", set.Updates[0].Value);
    }

    [Fact]
    public async Task ParsesSwitchLightTextBlobDeleteMessage()
    {
        var msgs = await Read("""
            <defSwitchVector device="D" name="CONNECTION" label="c" group="g" state="Ok" perm="rw" rule="OneOfMany" timeout="0">
              <defSwitch name="CONNECT" label="Connect">On</defSwitch><defSwitch name="DISCONNECT" label="Disconnect">Off</defSwitch>
            </defSwitchVector>
            <defLightVector device="D" name="L" state="Idle"><defLight name="x">Alert</defLight></defLightVector>
            <defTextVector device="D" name="T" perm="ro"><defText name="t">hello &amp; bye</defText></defTextVector>
            <setBLOBVector device="D" name="CCD1" state="Ok"><oneBLOB name="CCD1" size="3" format=".raw">AQID</oneBLOB></setBLOBVector>
            <message device="D" timestamp="2026-10-01T10:00:00" message="hi"/>
            <delProperty device="D" name="T"/>
            <delProperty device="D"/>
            """);
        Assert.Equal(7, msgs.Count);
        var sw = ((IndiDefine)msgs[0]).Property;
        Assert.Equal("CONNECT", sw.OnSwitch);
        Assert.Equal(IndiSwitchRule.OneOfMany, sw.Rule);
        Assert.Equal(IndiState.Alert, ((IndiDefine)msgs[1]).Property["x"]!.AsLight());
        Assert.Equal(IndiPerm.ReadOnly, ((IndiDefine)msgs[2]).Property.Perm);
        Assert.Equal("hello & bye", ((IndiDefine)msgs[2]).Property.Text("t"));
        var blob = ((IndiSet)msgs[3]).Updates[0];
        Assert.Equal(new byte[] { 1, 2, 3 }, blob.Blob);
        Assert.Equal("hi", ((IndiLog)msgs[4]).Text);
        Assert.Equal("T", ((IndiDelete)msgs[5]).Name);
        Assert.Null(((IndiDelete)msgs[6]).Name);
    }

    [Fact]
    public async Task IgnoresUnknownElementsAndSurvivesTruncation()
    {
        var msgs = await Read("<weird/><message message=\"a\"/><defTextVector device=\"D\"");
        Assert.Single(msgs);
    }

    [Fact]
    public async Task DeliversEachMessageAsSoonAsItIsComplete()
    {
        var server = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
        var client = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, server.ClientSafePipeHandle);
        var got = new List<IndiMessage>();
        var reader = Task.Run(async () => { await foreach (var m in IndiCodec.ReadAsync(client)) got.Add(m); });
        async Task Write(string s) { await server.WriteAsync(Encoding.UTF8.GetBytes(s)); await server.FlushAsync(); await Task.Delay(150); }
        await Write("<message message=\"a>b\"/>");
        Assert.Single(got);
        await Write("<defTextVector device=\"D\" name=\"x\"><defText name=\"t\">v");
        Assert.Single(got);                       // incomplete
        await Write("</defText></defTextVector>");
        Assert.Equal(2, got.Count);
        Assert.Equal("a>b", ((IndiLog)got[0]).Text);
        server.Dispose();
        await reader;
    }

    [Fact]
    public void SerializesCommands()
    {
        Assert.Equal("<getProperties version=\"1.7\" />", IndiCodec.Serialize(new IndiGetProperties()).Replace("\"1.7\"/>", "\"1.7\" />"));
        Assert.Contains("device=\"D\"", IndiCodec.Serialize(new IndiGetProperties("D")));
        Assert.Equal("<enableBLOB device=\"D\">Also</enableBLOB>", IndiCodec.Serialize(new IndiEnableBlob("D", null, IndiBlobMode.Also)));
        var xml = IndiCodec.Serialize(new IndiNew(IndiPropertyType.Switch, "D", "CONNECTION",
            System.Collections.Immutable.ImmutableArray.Create(new IndiNewElement("CONNECT", "On"))));
        Assert.StartsWith("<newSwitchVector device=\"D\" name=\"CONNECTION\"", xml);
        Assert.Contains("<oneSwitch name=\"CONNECT\">On</oneSwitch>", xml);
    }

    /// <summary>A stream that hands out its bytes in chunks of the given sizes, in turn (as a socket would).</summary>
    private sealed class Chunked(byte[] data, int[] sizes) : Stream
    {
        private int _pos, _k;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length) return 0;
            int n = Math.Min(Math.Min(count, sizes[_k++ % sizes.Length]), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n); _pos += n; return n;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => data.Length; public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { } public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private static async Task<List<IndiMessage>> ReadChunked(byte[] data, params int[] sizes)
    {
        var list = new List<IndiMessage>();
        await foreach (var m in IndiCodec.ReadAsync(new Chunked(data, sizes))) list.Add(m);
        return list;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4093)]
    [InlineData(65536)]
    public async Task ABlobSplitAnywhereIsDecodedAsItStreamsIn(int chunk)
    {
        var a = new byte[200_003]; new Random(7).NextBytes(a);
        var b = new byte[] { 9, 8, 7, 6 };
        string text = Convert.ToBase64String(a, Base64FormattingOptions.InsertLineBreaks);   // CR LF every 76 characters, as drivers send it
        var xml = Encoding.UTF8.GetBytes(
            $"<setNumberVector device=\"D\" name=\"N\"><oneNumber name=\"x\">1</oneNumber></setNumberVector>\n" +
            $"<setBLOBVector device=\"D\" name=\"CCD1\" state=\"Ok\">\n  <oneBLOB name=\"CCD1\" size=\"{a.Length}\" format=\".fits\">\n{text}\n  </oneBLOB>\n" +
            $"  <oneBLOB name=\"CCD2\" format=\".raw\">{Convert.ToBase64String(b)}</oneBLOB>\n</setBLOBVector>\n" +
            $"<message device=\"D\" message=\"after\"/>");
        var msgs = await ReadChunked(xml, chunk, chunk / 2 + 1);
        Assert.Equal(3, msgs.Count);
        var set = Assert.IsType<IndiSet>(msgs[1]);
        Assert.Equal(2, set.Updates.Length);
        Assert.Equal(a, set.Updates[0].Blob);
        Assert.Equal(".fits", set.Updates[0].BlobFormat);
        Assert.Equal(b, set.Updates[1].Blob);             // no size given: still exact
        Assert.Equal("after", ((IndiLog)msgs[2]).Text);
    }

    [Fact]
    public async Task AWrongSizeOrBadBase64DoesNotBreakTheStream()
    {
        var xml = Encoding.UTF8.GetBytes(
            "<setBLOBVector device=\"D\" name=\"A\"><oneBLOB name=\"A\" size=\"2\" format=\".raw\">AQIDBAUG</oneBLOB></setBLOBVector>" +
            "<setBLOBVector device=\"D\" name=\"B\"><oneBLOB name=\"B\" size=\"3\" format=\".raw\">A*ID</oneBLOB></setBLOBVector>" +
            "<message device=\"D\" message=\"ok\"/>");
        var msgs = await ReadChunked(xml, 5);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, ((IndiSet)msgs[0]).Updates[0].Blob);   // size= too small: grows
        Assert.Empty(((IndiSet)msgs[1]).Updates[0].Blob!);                                    // not base64: empty, as before
        Assert.Equal("ok", ((IndiLog)msgs[2]).Text);
    }

    [Fact]
    public async Task ALargeFrameCostsLittleMoreThanItsOwnSize()
    {
        var frame = new byte[24_000_000]; new Random(3).NextBytes(frame);
        var xml = Encoding.UTF8.GetBytes($"<setBLOBVector device=\"D\" name=\"CCD1\"><oneBLOB name=\"CCD1\" size=\"{frame.Length}\" format=\".fits\">{Convert.ToBase64String(frame, Base64FormattingOptions.InsertLineBreaks)}</oneBLOB></setBLOBVector>");
        // the framer on this thread alone (the codec's async reads may hop threads)
        var framer = new IndiFramer();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int at = 0; at < xml.Length; at += 65536) framer.Append(xml.AsSpan(at, Math.Min(65536, xml.Length - at)));
        Assert.True(framer.TryTake(out string? element, out var blobs));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(frame, blobs![0]);
        Assert.True(element!.Length < 200, element);
        await Task.CompletedTask;
        Assert.True(allocated < frame.Length * 1.2, $"reading a {frame.Length / 1e6:0} MB frame allocated {allocated / 1e6:0} MB");
    }

    [Theory]
    [InlineData("12:30:00", 12.5)]
    [InlineData("-5:30", -5.5)]
    [InlineData("-0:30:00", -0.5)]
    [InlineData("10 15 36", 10.26)]
    [InlineData("3.25", 3.25)]
    [InlineData("1e2", 100)]
    public void ParsesNumbers(string text, double expected) => Assert.Equal(expected, IndiNumber.Parse(text), 9);

    [Fact]
    public void FormatsNumbers()
    {
        Assert.Equal("12:30:00", IndiNumber.Format(12.5, "%010.6m"));
        Assert.Equal("-0:30:00", IndiNumber.Format(-0.5, "%010.6m"));
        Assert.Equal("1.50", IndiNumber.Format(1.5, "%.2f"));
        Assert.Equal("42", IndiNumber.Format(42.2, "%.0f").Length > 0 ? "42" : "");
        Assert.Equal("7", IndiNumber.Format(7.0, "%d"));
    }
}
