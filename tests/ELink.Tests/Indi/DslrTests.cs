using System.Collections.Concurrent;
using System.Diagnostics;
using ELink.Compose;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Imaging;
using ELink.Indi.Client;
using ELink.IndiBridge;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;
using Event.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.Tests.Indi;

/// <summary>A DSLR the way INDI's gphoto/canon/nikon drivers present one: ISO as CCD_ISO, a choice of FITS or the
/// camera's native raw (CCD_TRANSFER_FORMAT), and a sensor it does not know (CCD_INFO zeros until told). There is no
/// DSLR simulator, so a small fake driver plays it under the real indiserver: the whole protocol path is real.</summary>
public class DslrTests : IAsyncLifetime
{
    private const string Driver = """
        #!/usr/bin/env python3
        # A fake DSLR INDI driver: CCD_ISO, CCD_TRANSFER_FORMAT, writable CCD_INFO, bulb exposures, Bayer FITS or native frames.
        import sys, re, threading, time, base64, struct
        DEV = "Fake DSLR"
        st = {"connected": False, "iso": "ISO100", "fmt": "FORMAT_NATIVE", "maxx": 0, "maxy": 0, "pix": 0.0, "bits": 0}
        lock = threading.Lock()
        def out(s):
            with lock:
                sys.stdout.write(s); sys.stdout.flush()
        def sw(kind, name, elems, state="Idle", rule="OneOfMany"):
            tag = "defSwitchVector" if kind == "def" else "setSwitchVector"
            one = "defSwitch" if kind == "def" else "oneSwitch"
            extra = f' label="{name}" group="Main" perm="rw" rule="{rule}"' if kind == "def" else ""
            body = "".join(f'<{one} name="{n}"' + (f' label="{l}"' if kind == "def" else "") + f'>{"On" if on else "Off"}</{one}>' for n, l, on in elems)
            out(f'<{tag} device="{DEV}" name="{name}" state="{state}"{extra}>{body}</{tag}>\n')
        def num(kind, name, elems, state="Idle", perm="rw"):
            tag = "defNumberVector" if kind == "def" else "setNumberVector"
            one = "defNumber" if kind == "def" else "oneNumber"
            extra = f' label="{name}" group="Main" perm="{perm}"' if kind == "def" else ""
            fmt = ' format="%g" min="0" max="100000" step="1"' if kind == "def" else ""
            body = "".join(f'<{one} name="{n}"{fmt}>{v}</{one}>' for n, v in elems)
            out(f'<{tag} device="{DEV}" name="{name}" state="{state}"{extra}>{body}</{tag}>\n')
        def isos(kind): sw(kind, "CCD_ISO", [(n, n[3:], st["iso"] == n) for n in ("ISO100", "ISO400", "ISO800", "ISO1600")])
        def fmts(kind): sw(kind, "CCD_TRANSFER_FORMAT", [("FORMAT_FITS", "FITS", st["fmt"] == "FORMAT_FITS"), ("FORMAT_NATIVE", "Native", st["fmt"] == "FORMAT_NATIVE")])
        def info(kind): num(kind, "CCD_INFO", [("CCD_MAX_X", st["maxx"]), ("CCD_MAX_Y", st["maxy"]), ("CCD_PIXEL_SIZE", st["pix"]), ("CCD_PIXEL_SIZE_X", st["pix"]), ("CCD_PIXEL_SIZE_Y", st["pix"]), ("CCD_BITSPERPIXEL", st["bits"])])
        def define_main():
            out(f'<defTextVector device="{DEV}" name="DRIVER_INFO" label="Driver" group="General" state="Idle" perm="ro"><defText name="DRIVER_NAME">Fake DSLR</defText><defText name="DRIVER_EXEC">fake_dslr</defText><defText name="DRIVER_VERSION">1.0</defText><defText name="DRIVER_INTERFACE">2</defText></defTextVector>\n')
            sw("def", "CONNECTION", [("CONNECT", "Connect", st["connected"]), ("DISCONNECT", "Disconnect", not st["connected"])])
        def define_camera():
            num("def", "CCD_EXPOSURE", [("CCD_EXPOSURE_VALUE", 0)])
            sw("def", "CCD_ABORT_EXPOSURE", [("ABORT", "Abort", False)], rule="AtMostOne")
            isos("def"); fmts("def"); info("def")
            out(f'<defBLOBVector device="{DEV}" name="CCD1" label="Image" group="Main" state="Idle" perm="ro"><defBLOB name="CCD1" label="Image"/></defBLOBVector>\n')
        def fits(iso):
            w, h = 64, 48
            cards = ["SIMPLE  =                    T", "BITPIX  =                   16", "NAXIS   =                    2", f"NAXIS1  = {w:20d}", f"NAXIS2  = {h:20d}",
                     "BZERO   =                32768", "BAYERPAT= 'RGGB    '", f"ISOSPEED= {iso:20d}", "END"]
            head = "".join(c.ljust(80) for c in cards).ljust(2880).encode()
            data = b"".join(struct.pack(">h", (1000 + (x % 2) * 500 + (y % 2) * 250) - 32768) for y in range(h) for x in range(w))
            return head + data + b"\0" * ((2880 - len(data) % 2880) % 2880)
        def expose(seconds):
            num("set", "CCD_EXPOSURE", [("CCD_EXPOSURE_VALUE", seconds)], "Busy")
            time.sleep(seconds)
            if st["fmt"] == "FORMAT_FITS": blob, fmt = fits(int(st["iso"][3:])), ".fits"
            else: blob, fmt = b"CR2 raw data that no FITS reader understands", ".cr2"
            out(f'<setBLOBVector device="{DEV}" name="CCD1" state="Ok"><oneBLOB name="CCD1" size="{len(blob)}" format="{fmt}">{base64.b64encode(blob).decode()}</oneBLOB></setBLOBVector>\n')
            num("set", "CCD_EXPOSURE", [("CCD_EXPOSURE_VALUE", 0)], "Ok")
        buf = ""
        while True:
            chunk = sys.stdin.readline()
            if not chunk: break
            buf += chunk
            while True:
                m = re.search(r"<getProperties[^>]*/>|<(new\w+Vector)[^>]*>.*?</\1>|<enableBLOB[^>]*>.*?</enableBLOB>|<enableBLOB[^>]*/>", buf, re.S)
                if not m: break
                msg = m.group(0); buf = buf[m.end():]
                if msg.startswith("<getProperties"):
                    define_main()
                    if st["connected"]: define_camera()
                    continue
                name = re.search(r'name="([^"]+)"', msg).group(1)
                ones = dict(re.findall(r'<one\w+ name="([^"]+)"[^>]*>\s*([^<]*?)\s*</one\w+>', msg))
                if name == "CONNECTION":
                    on = ones.get("CONNECT") == "On"
                    if on and not st["connected"]: st["connected"] = True; sw("set", "CONNECTION", [("CONNECT", "", True), ("DISCONNECT", "", False)], "Ok"); define_camera()
                    elif not on: st["connected"] = False; sw("set", "CONNECTION", [("CONNECT", "", False), ("DISCONNECT", "", True)], "Ok")
                elif name == "CCD_ISO":
                    st["iso"] = [k for k, v in ones.items() if v == "On"][0]; isos("set")
                elif name == "CCD_TRANSFER_FORMAT":
                    st["fmt"] = [k for k, v in ones.items() if v == "On"][0]; fmts("set")
                elif name == "CCD_INFO":
                    st["maxx"] = int(float(ones.get("CCD_MAX_X", st["maxx"]))); st["maxy"] = int(float(ones.get("CCD_MAX_Y", st["maxy"])))
                    st["pix"] = float(ones.get("CCD_PIXEL_SIZE", st["pix"])); info("set")
                elif name == "CCD_EXPOSURE":
                    threading.Thread(target=expose, args=(float(ones["CCD_EXPOSURE_VALUE"]),), daemon=True).start()
        """;

    private IndiServerProcess _indi = null!;
    private string _dir = null!;

    public async Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "elink-dslr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "fake_dslr");
        await File.WriteAllTextAsync(path, Driver.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _indi = new IndiServerProcess(ELink.Testing.TestPorts.Next(), path);
        Assert.True(await _indi.WaitListeningAsync());
    }

    public Task DisposeAsync()
    {
        _indi.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
        return Task.CompletedTask;
    }

    private static async Task<bool> Eventually(Func<bool> cond, int ms = 30000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    [Fact]
    public async Task ADslrInATrainGetsItsSensorTakesIsoAndDeliversFits()
    {
        using var node = ElinkNode.Create("DS-" + Guid.NewGuid().ToString("N")[..6], ELink.Testing.TestPorts.Next());
        var dir = new DeviceDirectory(node); await dir.StartAsync();
        await using var link = new IndiServerLink(node, dir, "sim", "127.0.0.1", _indi.Port); await link.StartAsync();
        await using var compose = new CompositionHost(node); await compose.StartAsync();
        await using var c = new IndiClient("127.0.0.1", _indi.Port); await c.ConnectAsync();
        await c.WaitForAsync("Fake DSLR", "CONNECTION", _ => true, TimeSpan.FromSeconds(30));
        await c.SetSwitchAsync("Fake DSLR", "CONNECTION", "CONNECT");
        await c.WaitForAsync("Fake DSLR", "CCD_ISO", _ => true, TimeSpan.FromSeconds(30));

        using var cam = new RemoteState<CameraState>(node, EquipmentIds.State(DeviceKinds.Camera, "Fake_DSLR"), EquipmentIds.GetState(DeviceKinds.Camera, "Fake_DSLR"));
        await cam.StartAsync();
        Assert.True(await Eventually(() => cam.Latest is { Connected.Value: true, IsoChoices.Count: 4 }), "the DSLR as an ELink camera");
        Assert.Equal(new[] { "100", "400", "800", "1600" }, cam.Latest!.IsoChoices.Select(x => x.Text).ToArray());
        Assert.Equal("Native", cam.Latest.TransferFormat.Text);
        Assert.Equal(0, cam.Latest.PixelSizeUm.Value);          // the driver does not know its sensor

        // the train knows the sensor (an APS-C body) and hands it over, with the lens' focal length
        var train = new ImagingTrainDefinition { Id = "dslr", Label = "200 mm lens + DSLR", FocalLengthMm = 200 };
        train.Cameras.Add(new TrainCamera { CameraId = "Fake_DSLR", Role = "Imaging", PixelSizeUm = 4.3, SensorWidth = 5184, SensorHeight = 3456 });
        Assert.True((await Commands.CallAsync(node, ScopeIds.DefineTrain, train)).Ok.Value);
        await c.WaitForAsync("Fake DSLR", "CCD_INFO", p => Math.Abs(p.Number("CCD_PIXEL_SIZE") - 4.3) < 1e-6 && p.Number("CCD_MAX_X") == 5184, TimeSpan.FromSeconds(30));
        var ts = Assert.Single((await node.CallFunctionAsync<NOTESVoid, TrainState>(TrainIds.GetState("dslr"), NOTESVoid.Void))!);
        Assert.Equal(206.265 * 4.3 / 200, ts.Cameras[0].PixelScaleArcsec.Value, 3);   // 4.43"/px
        Assert.Equal(5184 * 206.265 * 4.3 / 200 / 3600, ts.Cameras[0].FieldWidthDegrees.Value, 3);

        // an exposure at ISO 800: the camera switches ISO and to FITS first, and the frame is a Bayer FITS ELink can use
        var shots = new ConcurrentQueue<ShotEvent>();
        await node.HookEventAsync(ShooterIds.Shot("dslr"), (ShotEvent s) => shots.Enqueue(s));
        var r = await Commands.CallAsync(node, ShooterIds.Expose("dslr"), new ShooterExposure { Seconds = 0.5, Iso = "800" });
        Assert.True(r.Ok.Value, r.Error.Text);
        Assert.True(await Eventually(() => shots.Count == 1), "the frame");
        var shot = shots.Single();
        Assert.Equal(".fits", shot.Format.Text);
        var fits = FitsImage.Parse(shot.Data.Data);
        Assert.Equal("800", fits.Get("ISOSPEED"));
        Assert.Equal("RGGB", Debayer.PatternOf(fits));          // raw colour: the live stack debayers it
        Assert.True(await Eventually(() => cam.Latest is { Iso.Text: "800", TransferFormat.Text: "FITS" }));

        var bad = await Commands.CallAsync(node, ShooterIds.Expose("dslr"), new ShooterExposure { Seconds = 0.1, Iso = "3200" });
        Assert.False(bad.Ok.Value);
        Assert.Contains("1600", bad.Error.Text);               // says what it offers
    }
}
