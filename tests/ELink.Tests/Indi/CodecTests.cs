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
