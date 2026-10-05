using System.Threading.Channels;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Imaging;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

/// <summary>Colour planes of frames, for measuring focus one colour at a time.</summary>
public static class FocusMeasure
{
    /// <summary>One colour of a raw colour frame, with nothing interpolated across colours (each 2x2 cell is a pixel): what a
    /// per-colour focus measures. Frames that are already RGB give their plane.</summary>
    public static FitsImage ColourPlane(FitsImage img, string channel)
    {
        int c = "RGB".IndexOf(channel, StringComparison.OrdinalIgnoreCase);
        if (c < 0 || channel.Length != 1) throw new InvalidOperationException($"channel {channel} is not R, G or B");
        if (img.Channels == 3)
            return FitsImage.FromPlanar(img.Width, img.Height, 1, img.Data.AsSpan(c * img.Width * img.Height, img.Width * img.Height).ToArray(), img.Header, img.Range);
        string pattern = Debayer.PatternOf(img) ?? throw new InvalidOperationException("focusing one colour needs a colour camera's frames (no Bayer pattern in them)");
        var s = Debayer.SuperPixel(img.Data, img.Width, img.Height, pattern);
        return FitsImage.FromPlanar(s.Width, s.Height, 1, s.Data.AsSpan(c * s.Width * s.Height, s.Width * s.Height).ToArray(), img.Header, img.Range);
    }

    /// <summary>What a frame says about focus: its stars as a whole, or for a colour frame colour by colour.</summary>
    public static FocusAssistSample Measure(FitsImage img)
    {
        var sample = new FocusAssistSample();
        bool colour = img.Channels == 3 || Debayer.PatternOf(img) is not null;
        if (!colour)
        {
            var r = StarField.Detect(img);
            sample.Hfr = r.MedianHfr; sample.Stars = r.Count; sample.Elongation = r.MedianElongation;
            sample.Peak = r.Stars.Count > 0 ? Math.Min(1, r.Stars.Max(s => s.Peak) / Math.Max(img.Range, 1e-9)) : double.NaN;
            return sample;
        }
        var results = "RGB".Select(c => StarField.Detect(ColourPlane(img, c.ToString()))).ToArray();
        sample.HfrRed = results[0].MedianHfr; sample.HfrGreen = results[1].MedianHfr; sample.HfrBlue = results[2].MedianHfr;
        var hfrs = results.Select(x => x.MedianHfr).Where(v => !double.IsNaN(v)).ToList();
        sample.Hfr = hfrs.Count > 0 ? hfrs.Average() : double.NaN;
        sample.Stars = results[1].Count;
        var el = results.Select(x => x.MedianElongation).Where(v => !double.IsNaN(v)).ToList();
        sample.Elongation = el.Count > 0 ? el.Average() : double.NaN;
        var peaks = results.Where(x => x.Stars.Count > 0).Select(x => x.Stars.Max(s => s.Peak)).ToList();
        sample.Peak = peaks.Count > 0 ? Math.Min(1, peaks.Max() / Math.Max(img.Range, 1e-9)) : double.NaN;
        return sample;
    }
}

/// <summary>A helper for focusing by hand: takes short frames over and over, measures each (star size, roundness, colour by colour for
/// colour cameras) and publishes the latest and a history, so the numbers can be watched while the focus knob is turned. It moves
/// nothing and is only a shooter id to the rest of the system; it can be left running.</summary>
public sealed class FocusAssistService : IAsyncDisposable
{
    private const int History = 120;
    private readonly TypeSafeEVentNode _node;
    private readonly CommandSet _commands;
    private readonly StatePublisher<FocusAssistState> _publisher;
    private readonly object _gate = new();
    private readonly List<FocusAssistSample> _samples = new();
    private string _phase = "Idle", _message = "", _shooter = "", _trend = "";
    private int _frames;
    private double _best = double.NaN;
    private CancellationTokenSource? _cts;
    private Task _run = Task.CompletedTask;

    public FocusAssistService(TypeSafeEVentNode node)
    {
        _node = node;
        _commands = new CommandSet(node);
        _publisher = new(node, FocusAssistIds.State, FocusAssistIds.GetState, Build);
    }

    public async Task StartAsync()
    {
        await _commands.AddAsync<FocusAssistRequest, CommandResult>(FocusAssistIds.Start, StartRunAsync, "take frames over and over and measure their focus, for focusing by hand");
        await _commands.AddAsync<NOTESVoid, CommandResult>(FocusAssistIds.Stop, _ => StopAsync(), "stop the focus helper");
        await _commands.AddAsync<NOTESVoid, CommandResult>(FocusAssistIds.Reset, async _ => { lock (_gate) { _samples.Clear(); _frames = 0; _best = double.NaN; _trend = ""; } await _publisher.PublishAsync(); return CommandResult.Success(); }, "forget the history");
        await _publisher.StartAsync();
    }

    private FocusAssistState Build()
    {
        lock (_gate)
        {
            var s = new FocusAssistState { Phase = _phase, Message = _message, ShooterId = _shooter, Frames = _frames, BestHfr = _best, Trend = _trend };
            foreach (var x in _samples) s.Samples.Add(x);
            return s;
        }
    }

    private async Task<CommandResult> StartRunAsync(FocusAssistRequest r)
    {
        if (r.ShooterId.Text == "") return CommandResult.Fail("ShooterId is required");
        if (!(r.ExposureSeconds.Value > 0)) return CommandResult.Fail("the exposure must be longer than zero");
        await StopAsync();
        var cts = new CancellationTokenSource();
        lock (_gate) { _cts = cts; _phase = "Running"; _message = "taking the first frame"; _shooter = r.ShooterId.Text; _samples.Clear(); _frames = 0; _best = double.NaN; _trend = ""; }
        _run = Task.Run(() => LoopAsync(r, cts.Token));
        await _publisher.PublishAsync();
        return CommandResult.Success();
    }

    private async Task<CommandResult> StopAsync()
    {
        CancellationTokenSource? cts; Task run;
        lock (_gate) { cts = _cts; _cts = null; run = _run; }
        if (cts is null) return CommandResult.Success();
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        try { await run.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        cts.Dispose();
        lock (_gate) { if (_phase == "Running") { _phase = "Idle"; _message = "stopped"; } }
        await _publisher.PublishAsync();
        return CommandResult.Success();
    }

    private async Task LoopAsync(FocusAssistRequest r, CancellationToken ct)
    {
        string shooter = r.ShooterId.Text;
        var shots = Channel.CreateUnbounded<ShotEvent>();
        Action<ShotEvent> onShot = s => shots.Writer.TryWrite(s);
        try
        {
            await _node.HookEventAsync(ShooterIds.Shot(shooter), onShot, "focus helper frames");
            while (!ct.IsCancellationRequested)
            {
                while (shots.Reader.TryRead(out _)) { }
                var expose = await Commands.CallAsync(_node, ShooterIds.Expose(shooter), new ShooterExposure { Seconds = r.ExposureSeconds.Value, FrameType = "Light", Filter = r.Filter.Text });
                if (!expose.Ok.Value) { await Fail(expose.Error.Text); return; }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(r.ExposureSeconds.Value + 90));
                ShotEvent shot;
                try { shot = await shots.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { await Fail("the frame did not arrive in time"); return; }
                if (!shot.Format.Text.StartsWith(".fit", StringComparison.OrdinalIgnoreCase)) { await Fail($"the focus helper needs FITS frames, the shooter delivered {shot.Format.Text}"); return; }
                var sample = await Task.Run(() => FocusMeasure.Measure(FitsImage.Parse(shot.Data.Data)), ct);
                lock (_gate)
                {
                    _samples.Add(sample); if (_samples.Count > History) _samples.RemoveAt(0);
                    _frames++;
                    if (!double.IsNaN(sample.Hfr.Value) && (double.IsNaN(_best) || sample.Hfr.Value < _best)) _best = sample.Hfr.Value;
                    _trend = TrendOf(_samples);
                    _message = double.IsNaN(sample.Hfr.Value) ? "no stars in the frame: check the exposure and the pointing" : sample.Peak.Value > 0.95 ? "stars are saturated: shorten the exposure" : "";
                }
                await _publisher.PublishAsync();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await Fail(ex.Message); }
        finally { try { _node.UnhookEvent(ShooterIds.Shot(shooter), onShot); } catch (ObjectDisposedException) { } }
    }

    /// <summary>The last three frames against the three before: sharper | softer | steady (within 4 %).</summary>
    private static string TrendOf(List<FocusAssistSample> s)
    {
        var h = s.Select(x => x.Hfr.Value).Where(v => !double.IsNaN(v)).ToList();
        if (h.Count < 6) return "";
        double now = h.Skip(h.Count - 3).Average(), before = h.Skip(h.Count - 6).Take(3).Average();
        return now < before * 0.96 ? "sharper" : now > before * 1.04 ? "softer" : "steady";
    }

    private async Task Fail(string why)
    {
        lock (_gate) { _phase = "Error"; _message = why; _cts = null; }
        try { await _publisher.PublishAsync(); } catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(); } catch (ObjectDisposedException) { }
        _commands.Dispose(); _publisher.Dispose();
    }
}
