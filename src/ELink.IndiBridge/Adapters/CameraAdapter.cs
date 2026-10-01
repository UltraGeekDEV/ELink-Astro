using System.Globalization;
using ELink.Contracts;
using ELink.Contracts.Equipment;
using ELink.Indi.Client;
using ELink.Indi.Protocol;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.IndiBridge.Adapters;

/// <summary>INDI CCD driver to the ELink Camera contract (primary imaging sensor, property CCD1).</summary>
public sealed class CameraAdapter(AdapterContext ctx) : IndiDeviceAdapter<CameraState>(ctx)
{
    public override string Kind => DeviceKinds.Camera;

    private double _lastExposure;
    private string _lastFrameType = "Light";
    private bool _blobEnabled;

    protected override CameraState BuildState()
    {
        var s = new CameraState { Connected = Connected };
        if (!Connected) { s.Phase = "Disconnected"; return s; }
        var exp = P("CCD_EXPOSURE");
        s.Phase = exp?.State switch { IndiState.Busy => "Exposing", IndiState.Alert => "Error", _ => "Idle" };
        s.ExposureRemaining = exp?.State == IndiState.Busy ? exp.Number("CCD_EXPOSURE_VALUE") : 0.0;
        if (P("CCD_TEMPERATURE") is { } temp) s.Temperature = temp.Number("CCD_TEMPERATURE_VALUE");
        s.HasCooler = P("CCD_COOLER") is not null || P("CCD_TEMPERATURE")?.Writable == true;
        if (P("CCD_INFO") is { } info)
        {
            s.SensorWidth = (int)info.Number("CCD_MAX_X"); s.SensorHeight = (int)info.Number("CCD_MAX_Y");
            s.PixelSizeUm = info.Number("CCD_PIXEL_SIZE"); s.BitsPerPixel = (int)info.Number("CCD_BITSPERPIXEL");
        }
        if (P("CCD_BINNING") is { } bin) { s.BinX = (int)bin.Number("HOR_BIN"); s.BinY = (int)bin.Number("VER_BIN"); }
        if (P("CCD_GAIN") is { } gain) s.Gain = gain.Number("GAIN");
        if (exp?.State == IndiState.Alert) s.Message = "the camera reported an exposure error";
        return s;
    }

    protected override async Task RegisterCommandsAsync()
    {
        await RegisterCommandAsync<ExposeRequest, CommandResult>("Expose", ExposeAsync, "start an exposure; the finished frame arrives on the .Frame event");
        await RegisterCommandAsync<NOTESVoid, CommandResult>("AbortExposure", _ => AbortAsync(), "abort the running exposure");
        await RegisterCommandAsync<BinaryConvertibleDouble, CommandResult>("SetTemperature", SetTemperatureAsync, "cooling target in °C");
    }

    private async Task<CommandResult> ExposeAsync(ExposeRequest r)
    {
        if (!Connected) return CommandResult.Fail("camera not connected");
        if (Need("CCD_EXPOSURE", "CCD_EXPOSURE_VALUE") is { } missing) return missing;
        double seconds = r.Seconds.Value;
        if (!(seconds >= 0)) return CommandResult.Fail("invalid exposure time");
        string frameElement = r.FrameType.Text switch
        {
            "Light" or "" => "FRAME_LIGHT", "Dark" => "FRAME_DARK", "Bias" => "FRAME_BIAS", "Flat" => "FRAME_FLAT",
            _ => "",
        };
        if (frameElement == "") return CommandResult.Fail($"unknown frame type {r.FrameType.Text}");
        if (P("CCD_EXPOSURE")?.State == IndiState.Busy) return CommandResult.Fail("an exposure is already running");

        return await Send(async () =>
        {
            // frames must come to us as BLOBs, not be saved by the driver
            if (P("UPLOAD_MODE")?.Has("UPLOAD_CLIENT") == true && P("UPLOAD_MODE")!.OnSwitch != "UPLOAD_CLIENT")
                await SetSwitch("UPLOAD_MODE", "UPLOAD_CLIENT");
            await EnsureBlobsAsync();
            if (P("CCD_FRAME_TYPE")?.Has(frameElement) == true) await SetSwitch("CCD_FRAME_TYPE", frameElement);
            if (r.BinX.Value > 0 && P("CCD_BINNING") is not null)
                await Client.SetNumbersAsync(Device, "CCD_BINNING", new[] { ("HOR_BIN", (double)r.BinX.Value), ("VER_BIN", (double)(r.BinY.Value > 0 ? r.BinY.Value : r.BinX.Value)) });
            if (!double.IsNaN(r.Gain.Value) && P("CCD_GAIN") is not null) await SetNumber("CCD_GAIN", "GAIN", r.Gain.Value);
            _lastExposure = seconds; _lastFrameType = r.FrameType.Text == "" ? "Light" : r.FrameType.Text;
            await SetNumber("CCD_EXPOSURE", "CCD_EXPOSURE_VALUE", seconds);
        });
    }

    private async Task EnsureBlobsAsync()
    {
        if (_blobEnabled) return;
        await Client.EnableBlobAsync(Device, IndiBlobMode.Also);
        _blobEnabled = true;
    }

    private async Task<CommandResult> AbortAsync()
    {
        if (Need("CCD_ABORT_EXPOSURE", "ABORT") is { } missing) return missing;
        return await Send(() => SetSwitch("CCD_ABORT_EXPOSURE", "ABORT"));
    }

    private async Task<CommandResult> SetTemperatureAsync(BinaryConvertibleDouble t)
    {
        if (Need("CCD_TEMPERATURE", "CCD_TEMPERATURE_VALUE") is { } missing) return missing;
        if (P("CCD_TEMPERATURE")!.Perm == IndiPerm.ReadOnly) return CommandResult.Fail("this camera cannot cool");
        return await Send(() => SetNumber("CCD_TEMPERATURE", "CCD_TEMPERATURE_VALUE", t.Value));
    }

    protected override async Task OnPropertyChangedAsync(IndiChange change)
    {
        if (change is not PropertyUpdated { Property: { Name: "CCD1", Type: IndiPropertyType.Blob } p }) return;
        var e = p["CCD1"];
        if (e?.Blob is not { Length: > 0 } blob) return;
        // INDI may deliver zlib-compressed frames (".fits.z"); consumers always get the plain file.
        string format = e.BlobFormat ?? "";
        byte[] data = IndiCodec.Decompress(blob, format);
        if (format.EndsWith(".z", StringComparison.Ordinal)) format = format[..^2];
        try
        {
            await Node.FireEventAsync(EquipmentIds.Command(Kind, Id, "Frame"), new FrameEvent
            {
                Device = Device, Format = format, ExposureSeconds = _lastExposure, FrameType = _lastFrameType,
                Timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), Data = new RawBytes(data),
            });
        }
        catch (ObjectDisposedException) { }
    }
}
