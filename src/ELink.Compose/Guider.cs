using System.Globalization;
using System.Threading.Channels;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Core.Astro;
using ELink.Imaging;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Compose;

/// <summary>A guider: loops a guide camera (any Shooter), follows a dozen stars, and keeps them where they should be.
/// Output is either timed pulses to a GuidePort (calibrated here, as PHD2 or Ekos do) or, for devices that close the
/// loop themselves, a "you are here, should be here" GuideCorrection. Every frame also fires the correction as an event,
/// whichever the output. Dithering moves the lock point; settling is when the error stays small long enough.
/// A smart scope owns one and drives it between its own slews and exposures.</summary>
public sealed class Guider : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly GuiderDefinition _def;
    private readonly string _id;
    private readonly CommandSet _commands;
    private readonly StatePublisher<GuiderState> _publisher;
    private readonly object _gate = new();
    private readonly Channel<ShotEvent> _frames = Channel.CreateUnbounded<ShotEvent>();
    private readonly Action<ShotEvent> _onShot;
    private readonly RemoteState<MountState>? _mount;
    private readonly Random _random = new();
    private readonly List<(double Ra, double Dec)> _history = new();

    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private string _phase = "Idle", _message = "";
    private PulseCalibration? _calibration;
    private TanWcs? _map;              // sky orientation of the guide camera (absolute when solved)
    private bool _mapAbsolute;
    private double _scale = double.NaN;
    private (double X, double Y) _target; // dither offset: where the stars should be, relative to the lock
    private double _settlePixels = 1.5, _settleSeconds = 10;
    private DateTime? _settledSince;
    private bool _settled;
    private int _frameCount, _stars, _dithers;
    private double _errorPixels = double.NaN;
    private string _orientNote = "";

    public Guider(TypeSafeEVentNode node, GuiderDefinition definition)
    {
        _node = node; _def = definition; _id = definition.Id.Text;
        _commands = new CommandSet(node);
        _publisher = new(node, GuiderIds.State(_id), GuiderIds.GetState(_id), BuildState);
        _onShot = s => _frames.Writer.TryWrite(s);
        if (definition.MountId.Text != "")
            _mount = new RemoteState<MountState>(node, EquipmentIds.State(DeviceKinds.Mount, definition.MountId.Text), EquipmentIds.GetState(DeviceKinds.Mount, definition.MountId.Text));
    }

    public string Id => _id;
    private bool PulseOutput => _def.Output.Text != "Correction";

    public async Task StartAsync()
    {
        await _node.HookEventAsync(ShooterIds.Shot(_def.ShooterId.Text), _onShot, $"guider {_id} frames");
        if (_mount is not null) await _mount.StartAsync();
        await _commands.AddAsync<GuideStartRequest, CommandResult>(GuiderIds.Start(_id), StartGuidingAsync, "start guiding (calibrating first if needed)");
        await _commands.AddAsync<NOTESVoid, CommandResult>(GuiderIds.Stop(_id), async _ => { await StopGuidingAsync(); return CommandResult.Success(); }, "stop guiding");
        await _commands.AddAsync<DitherRequest, CommandResult>(GuiderIds.Dither(_id), DitherAsync, "move the lock point and/or wait until settled");
        await _commands.AddAsync<NOTESVoid, CommandResult>(GuiderIds.ClearCalibration(_id), async _ => { lock (_gate) _calibration = null; await Publish(); return CommandResult.Success(); }, "forget the calibration");
        await _publisher.StartAsync();
    }

    private GuiderState BuildState()
    {
        lock (_gate)
        {
            var s = new GuiderState
            {
                Phase = _phase, Message = _message, Calibrated = _calibration is not null, Settled = _settled, Frames = _frameCount, Stars = _stars,
                Dithers = _dithers, ErrorPixels = _errorPixels, PixelScaleArcsec = _scale, Output = PulseOutput ? "Pulse" : "Correction",
                Calibration = _calibration is { } c
                    ? FormattableString.Invariant($"RA {c.RaRate * 1000:0.0} px/s, Dec {c.DecRate * 1000:0.0} px/s, axes at {c.AxisAngleDegrees:0}°") +
                      (double.IsNaN(c.DecDegrees) ? "" : FormattableString.Invariant($", at Dec {c.DecDegrees:0}°")) + (c.PierSide is "East" or "West" ? $", pier {c.PierSide}" : "")
                    : "",
            };
            if (_history.Count > 0 && !double.IsNaN(_history[0].Ra))
            {
                double ra = Math.Sqrt(_history.Average(h => h.Ra * h.Ra)), dec = Math.Sqrt(_history.Average(h => h.Dec * h.Dec));
                s.RmsRaArcsec = ra; s.RmsDecArcsec = dec; s.RmsTotalArcsec = Math.Sqrt(ra * ra + dec * dec);
            }
            return s;
        }
    }

    private async Task Publish() { try { await _publisher.PublishAsync(); } catch (ObjectDisposedException) { } }

    private async Task Set(string phase, string message)
    {
        lock (_gate) { _phase = phase; _message = message; }
        await Publish();
    }

    // ---- commands ----------------------------------------------------------------------------------------------

    private async Task<CommandResult> StartGuidingAsync(GuideStartRequest r)
    {
        if (PulseOutput && _def.GuidePortId.Text == "") return CommandResult.Fail($"guider {_id}: Pulse output needs a GuidePortId");
        if (!PulseOutput && _def.TargetId.Text == "") return CommandResult.Fail($"guider {_id}: Correction output needs a TargetId");
        lock (_gate) if (_run is not null && !r.Recalibrate.Value) return CommandResult.Success();
        await StopGuidingAsync();
        lock (_gate)
        {
            if (r.Recalibrate.Value) _calibration = null;
            _run = new CancellationTokenSource();
            _phase = "Acquiring"; _message = ""; _settled = false; _settledSince = null; _frameCount = 0; _history.Clear(); _target = (0, 0);
            var ct = _run.Token;
            _loop = Task.Run(() => RunAsync(ct));
        }
        await Publish();
        return CommandResult.Success();
    }

    public async Task StopGuidingAsync()
    {
        CancellationTokenSource? run; Task loop;
        lock (_gate) { run = _run; _run = null; loop = _loop; }
        if (run is null) return;
        run.Cancel();
        try { await loop.WaitAsync(TimeSpan.FromSeconds(Math.Max(10, _def.ExposureSeconds.Value + 10))); } catch { }
        run.Dispose();
        lock (_gate) { _phase = "Idle"; _settled = false; }
        await Publish();
    }

    private async Task<CommandResult> DitherAsync(DitherRequest r)
    {
        var until = DateTime.UtcNow.AddSeconds(Math.Clamp(r.TimeoutSeconds.Value, 1, 3600));
        // a scope may ask to settle while the guider is still acquiring or calibrating: wait for guiding first
        while (true)
        {
            string phase; lock (_gate) phase = _phase;
            if (phase == "Guiding") break;
            if (phase is "Idle" or "Error") return CommandResult.Fail($"guider {_id} is not guiding ({phase}{(_message != "" ? ": " + _message : "")})");
            if (DateTime.UtcNow > until) return CommandResult.Fail($"guider {_id} did not start guiding in time ({phase})");
            await Task.Delay(100);
        }
        lock (_gate)
        {
            _settlePixels = Math.Max(0.1, r.SettlePixels.Value); _settleSeconds = Math.Max(0, r.SettleSeconds.Value);
            if (r.Pixels.Value > 0)
            {
                double p = r.Pixels.Value;
                if (r.RaOnly.Value && _calibration is { } c) { double k = (_random.NextDouble() * 2 - 1) * p; _target = (c.RaX / c.RaRate * k, c.RaY / c.RaRate * k); }
                else _target = ((_random.NextDouble() * 2 - 1) * p, (_random.NextDouble() * 2 - 1) * p);
                _dithers++;
            }
            _settled = false; _settledSince = null;
        }
        await Publish();
        while (DateTime.UtcNow < until)
        {
            bool settled; string phase; lock (_gate) { settled = _settled; phase = _phase; }
            if (settled) return CommandResult.Success();
            if (phase is "Idle" or "Error") return CommandResult.Fail($"guiding stopped: {phase}");
            await Task.Delay(100);
        }
        double err; lock (_gate) err = _errorPixels;
        return CommandResult.Fail(FormattableString.Invariant($"guiding did not settle within {r.TimeoutSeconds.Value:0} s (error {err:0.0} px)"));
    }

    // ---- the loop ----------------------------------------------------------------------------------------------

    private sealed record Frame(FitsImage Image, IReadOnlyList<Star> Stars, DateTime MidUtc, byte[] Fits);

    private async Task<Frame> ShootAsync(CancellationToken ct)
    {
        while (_frames.Reader.TryRead(out _)) { }   // only frames taken from now on
        double seconds = _def.ExposureSeconds.Value;
        var start = DateTime.UtcNow;
        var r = await Commands.CallAsync(_node, ShooterIds.Expose(_def.ShooterId.Text), new ShooterExposure { Seconds = seconds, FrameType = "Light" });
        if (!r.Ok.Value) throw new InvalidOperationException("guide camera: " + r.Error.Text);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds + 60));
        ShotEvent shot;
        try { shot = await _frames.Reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("no frame from the guide camera"); }
        var img = FitsImage.Parse(shot.Data.Data);
        var stars = StarField.Detect(img, 8, 80).Stars;
        return new Frame(img, stars, start.AddSeconds(seconds / 2), shot.Data.Data);
    }

    private double Dec => _mount?.Latest is { Connected.Value: true } m ? m.DecDegrees.Value : double.NaN;
    private string Pier => _mount?.Latest is { Connected.Value: true } m ? m.PierSide.Text : "Unknown";

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var first = await ShootAsync(ct);
            StarTracker? tracker = null;
            for (int tries = 0; tracker is null; tries++)
            {
                tracker = StarTracker.Acquire(first.Stars, first.Image.Width, first.Image.Height, first.Image.Range * 0.9);
                if (tracker is not null) break;
                if (tries >= 4) throw new InvalidOperationException("no guide star: check the guide camera's focus and exposure");
                await Set("Acquiring", "no usable stars yet");
                first = await ShootAsync(ct);
            }
            lock (_gate) _stars = tracker.Lock.Count;
            await Orient(first, ct);

            if (PulseOutput)
            {
                bool flipped; lock (_gate) flipped = _calibration is { } c && c.PierSide is "East" or "West" && Pier is "East" or "West" && c.PierSide != Pier;
                if (flipped) lock (_gate) _calibration = null;   // after a meridian flip: calibrate again rather than guess the mount's Dec behaviour
                bool need; lock (_gate) need = _calibration is null;
                if (need) tracker = await CalibrateAsync(tracker, ct);
            }
            else
            {
                bool oriented; lock (_gate) oriented = _map is not null;
                string note; lock (_gate) note = _orientNote;
                if (!oriented) throw new InvalidOperationException("Correction output needs to know the guide camera's orientation: put a plate solver on the mesh or give CameraAngleDegrees and the pixel scale" + (note != "" ? $" ({note})" : ""));
            }
            await GuideAsync(tracker, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { await Set("Error", ex.Message); }
    }

    /// <summary>Pixel scale and orientation: from a plate solve (absolute), else the definition, else the FITS header.</summary>
    private async Task Orient(Frame f, CancellationToken ct)
    {
        double scale = _def.PixelScaleArcsec.Value > 0 ? _def.PixelScaleArcsec.Value : HeaderScale(f.Image);
        TanWcs? map = null; bool absolute = false; string why = "";
        if (_def.SolveOrientation.Value)
        {
            try
            {
                var answers = await _node.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve, new SolveRequest
                {
                    Image = new ELink.Contracts.RawBytes(f.Fits), HintRaHours = _mount?.Latest?.RaHours.Value ?? double.NaN, HintDecDegrees = Dec, HintRadiusDegrees = 10,
                    ScaleLowArcsecPerPixel = scale > 0 ? scale * 0.8 : 0, ScaleHighArcsecPerPixel = scale > 0 ? scale * 1.25 : 0, TimeoutSeconds = 60,
                }, TimeSpan.FromSeconds(90), ct);
                if (answers?.FirstOrDefault() is { Solved.Value: true } s)
                {
                    map = s.HasWcs.Value
                        ? new TanWcs(s.WcsCrVal1.Value, s.WcsCrVal2.Value, s.WcsCrPix1.Value - 1, s.WcsCrPix2.Value - 1, s.WcsCd11.Value, s.WcsCd12.Value, s.WcsCd21.Value, s.WcsCd22.Value)
                        : TanWcs.Centered(s.RaHours.Value * 15, s.DecDegrees.Value, double.IsNaN(s.PositionAngle.Value) ? 0 : s.PositionAngle.Value, s.PixelScale.Value, f.Image.Width, f.Image.Height);
                    absolute = true; scale = map.PixelScaleArcsec;
                }
                else why = answers is { Count: > 0 } ? "the guide frame did not solve: " + answers[0].Message.Text : "no plate solver on the mesh";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { why = "solving the guide frame failed: " + ex.Message; }
        }
        if (map is null && !double.IsNaN(_def.CameraAngleDegrees.Value) && scale > 0)
        {
            var m = _mount?.Latest;
            double ra = m is { Connected.Value: true } ? m.RaHours.Value * 15 : 0, dec = m is { Connected.Value: true } ? m.DecDegrees.Value : 0;
            map = TanWcs.Centered(ra, dec, _def.CameraAngleDegrees.Value, scale, f.Image.Width, f.Image.Height);
        }
        lock (_gate) { _map = map; _mapAbsolute = absolute; _scale = scale > 0 ? scale : double.NaN; _orientNote = why; }
        await Publish();
    }

    private static double HeaderScale(FitsImage img)
    {
        double s = img.GetDouble("SCALE");
        if (s > 0) return s;
        double fl = img.GetDouble("FOCALLEN"), px = img.GetDouble("PIXSIZE1", img.GetDouble("XPIXSZ")), bin = img.GetDouble("XBINNING", 1);
        return fl > 0 && px > 0 ? 206.265 * px * bin / fl : double.NaN;
    }

    private async Task<(double X, double Y)> MeasureAsync(StarTracker tracker, (double X, double Y) expect, CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            var f = await ShootAsync(ct);
            if (tracker.Measure(f.Stars, expect.X, expect.Y, 15) is { } m) return (m.Dx, m.Dy);
        }
        throw new InvalidOperationException("lost the guide stars");
    }

    private Task PulseAsync(string direction, double ms, CancellationToken ct) =>
        ms < 1 ? Task.CompletedTask : PulseOrThrow(direction, (int)Math.Round(ms), ct);

    private async Task PulseOrThrow(string direction, int ms, CancellationToken ct)
    {
        var r = await Commands.CallAsync(_node, GuidePortIds.Pulse(_def.GuidePortId.Text), new GuidePulse { Direction = direction, Milliseconds = ms });
        ct.ThrowIfCancellationRequested();
        if (!r.Ok.Value) throw new InvalidOperationException("guide port: " + r.Error.Text);
    }

    /// <summary>West, back east, clear Dec backlash, north, back south: how many pixels per millisecond each axis moves the stars.</summary>
    private async Task<StarTracker> CalibrateAsync(StarTracker tracker, CancellationToken ct)
    {
        int step = Math.Max(50, _def.CalibrationStepMs.Value);
        double distance = Math.Max(5, _def.CalibrationPixels.Value);
        const int maxSteps = 30;

        async Task<((double X, double Y) Rate, (double X, double Y) End)> Leg(string direction, (double X, double Y) from)
        {
            var pos = from; int total = 0;
            for (int i = 0; i < maxSteps && Dist(pos, from) < distance; i++)
            {
                await Set("Calibrating", FormattableString.Invariant($"{direction}: {Dist(pos, from):0.0} of {distance:0} px"));
                await PulseAsync(direction, step, ct); total += step;
                pos = await MeasureAsync(tracker, pos, ct);
            }
            if (Dist(pos, from) < 3) throw new InvalidOperationException($"calibration failed: the stars did not move on {direction} pulses (check the guide port and that the mount is tracking)");
            return (((pos.X - from.X) / total, (pos.Y - from.Y) / total), pos);
        }
        async Task<(double X, double Y)> Back(string direction, (double X, double Y) rate, (double X, double Y) pos, (double X, double Y) home)
        {
            // return by the measured rate, in chunks the tracker can follow
            double ms = Dist(pos, home) / Math.Max(1e-6, Math.Sqrt(rate.X * rate.X + rate.Y * rate.Y));
            while (ms > 1)
            {
                double chunk = Math.Min(ms, step * 3);
                await PulseAsync(direction, chunk, ct); ms -= chunk;
                pos = (pos.X - rate.X * chunk, pos.Y - rate.Y * chunk);
            }
            return await MeasureAsync(tracker, pos, ct);
        }

        var start = await MeasureAsync(tracker, (0, 0), ct);
        var (ra, west) = await Leg("West", start);
        var home = await Back("East", ra, west, start);
        // take up Dec backlash before measuring: pulse north until the stars start to follow
        var before = home;
        for (int i = 0; i < 10; i++)
        {
            await Set("Calibrating", "clearing Dec backlash");
            await PulseAsync("North", step, ct);
            home = await MeasureAsync(tracker, home, ct);
            if (Dist(home, before) > 1.5) break;
        }
        var (dec, north) = await Leg("North", home);
        await Back("South", dec, north, start);

        var cal = new PulseCalibration(ra.X, ra.Y, dec.X, dec.Y, Dec, Pier);
        string warn = Math.Abs(cal.AxisAngleDegrees - 90) > 20 ? FormattableString.Invariant($"; the axes are {cal.AxisAngleDegrees:0}° apart, not 90°: a poor calibration (backlash, wind?)") : "";
        lock (_gate) _calibration = cal;
        await Set("Calibrating", "calibrated" + warn);
        // re-lock where the stars are now: the calibration legs never come back exactly
        var f = await ShootAsync(ct);
        var again = StarTracker.Acquire(f.Stars, f.Image.Width, f.Image.Height, f.Image.Range * 0.9) ?? tracker;
        lock (_gate) _stars = again.Lock.Count;
        return again;
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private async Task GuideAsync(StarTracker tracker, CancellationToken ct)
    {
        await Set("Guiding", "");
        (double X, double Y) expect = (0, 0);
        int lost = 0;
        while (!ct.IsCancellationRequested)
        {
            var f = await ShootAsync(ct);
            // where they were expected, else a wider search: a gust or a snag can throw them tens of pixels
            var m = tracker.Measure(f.Stars, expect.X, expect.Y, 15) ?? tracker.Measure(f.Stars, expect.X, expect.Y, 80);
            if (m is null)
            {
                lost++;
                await Set(lost >= 3 ? "Lost" : "Guiding", $"guide stars not found ({lost})");
                if (lost >= 6 && StarTracker.Acquire(f.Stars, f.Image.Width, f.Image.Height, f.Image.Range * 0.9) is { } re)
                {
                    tracker = re; expect = (0, 0); lost = 0;
                    lock (_gate) { _target = (0, 0); _settled = false; _settledSince = null; }
                    await Set("Guiding", "re-acquired new guide stars");
                }
                continue;
            }
            if (lost > 0) { lost = 0; await Set("Guiding", ""); }
            expect = (m.Value.Dx, m.Value.Dy);

            PulseCalibration? cal; TanWcs? map; bool absolute; double scale; (double X, double Y) target; bool settling;
            lock (_gate) { cal = _calibration; map = _map; absolute = _mapAbsolute; scale = _scale; target = _target; settling = !_settled; }
            double ex = m.Value.Dx - target.X, ey = m.Value.Dy - target.Y;

            // the error on the sky: the move the pointing needs
            double moveE = double.NaN, moveN = double.NaN;
            double isRa = double.NaN, isDec = double.NaN, shouldRa = double.NaN, shouldDec = double.NaN;
            if (map is not null)
            {
                double cx = (f.Image.Width - 1) / 2.0, cy = (f.Image.Height - 1) / 2.0;
                // the image moved by the star shift: the centre pixel now sees what was at centre - shift
                var (ira, idec) = map.PixelToSky(cx - m.Value.Dx, cy - m.Value.Dy);
                var (sra, sdec) = map.PixelToSky(cx - target.X, cy - target.Y);
                var (e, n) = Gnomonic.FromSky(ira / 15, idec, sra / 15, sdec);
                moveE = e * 3600; moveN = n * 3600;
                if (absolute) { isRa = ira / 15; isDec = idec; shouldRa = sra / 15; shouldDec = sdec; }
            }
            else if (cal is not null && !double.IsNaN(scale))
            {
                var (eastPx, southPx) = cal.Components(ex, ey);
                moveE = eastPx * scale; moveN = -southPx * scale;   // stars went east: the pointing went west, so move east
            }

            // pulses
            int raMs = 0, decMs = 0;
            double raErr = double.NaN, decErr = double.NaN;
            if (cal is not null)
            {
                var (eastPx, southPx) = cal.Components(ex, ey);
                if (!double.IsNaN(scale)) { raErr = eastPx * scale; decErr = southPx * scale; }
                if (PulseOutput)
                {
                    var (tRa, tDec) = cal.PulsesFor(-ex, -ey, Dec);
                    tRa *= _def.RaAggressiveness.Value; tDec *= _def.DecAggressiveness.Value;
                    if (Math.Abs(eastPx) < _def.MinMovePixels.Value) tRa = 0;
                    if (Math.Abs(southPx) < _def.MinMovePixels.Value) tDec = 0;
                    tDec = _def.DecMode.Text switch { "Off" => 0, "North" => Math.Max(0, tDec), "South" => Math.Min(0, tDec), _ => tDec };
                    int max = Math.Max(10, _def.MaxPulseMs.Value);
                    raMs = (int)Math.Round(Math.Clamp(tRa, -max, max)); decMs = (int)Math.Round(Math.Clamp(tDec, -max, max));
                }
            }
            else if (!double.IsNaN(moveE)) { raErr = -moveE; decErr = moveN; }

            double err = Math.Sqrt(ex * ex + ey * ey);
            int frame;
            lock (_gate)
            {
                frame = ++_frameCount; _stars = m.Value.Matched; _errorPixels = err;
                if (!double.IsNaN(raErr)) { _history.Add((raErr, decErr)); if (_history.Count > 50) _history.RemoveAt(0); }
                var now = DateTime.UtcNow;
                if (err <= _settlePixels) { _settledSince ??= now; _settled = (now - _settledSince.Value).TotalSeconds >= _settleSeconds; }
                else { _settledSince = null; _settled = false; }
            }

            string utc = f.MidUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var correction = new GuideCorrection
            {
                GuiderId = _id, Frame = frame, Utc = utc, IsRaHours = isRa, IsDecDegrees = isDec, ShouldRaHours = shouldRa, ShouldDecDegrees = shouldDec,
                MoveEastArcsec = double.IsNaN(moveE) ? 0 : moveE, MoveNorthArcsec = double.IsNaN(moveN) ? 0 : moveN, Settling = settling,
            };
            try
            {
                await _node.FireEventAsync(GuiderIds.Step(_id), new GuideStep
                {
                    Frame = frame, Utc = utc, ErrorXPixels = ex, ErrorYPixels = ey, RaArcsec = raErr, DecArcsec = decErr, RaPulseMs = raMs, DecPulseMs = decMs,
                    Stars = m.Value.Matched, Settling = settling,
                });
                if (!double.IsNaN(moveE)) await _node.FireEventAsync(GuiderIds.Correction(_id), correction);
            }
            catch (ObjectDisposedException) { return; }
            await Publish();

            if (PulseOutput)
            {
                await Task.WhenAll(
                    PulseAsync(raMs >= 0 ? "West" : "East", Math.Abs(raMs), ct),
                    PulseAsync(decMs >= 0 ? "North" : "South", Math.Abs(decMs), ct));
            }
            else if (!double.IsNaN(moveE))
            {
                var r = await Commands.CallAsync(_node, GuideTargetIds.Correct(_def.TargetId.Text), correction);
                if (!r.Ok.Value) await Set("Guiding", "guide target: " + r.Error.Text);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopGuidingAsync();
        try { _node.UnhookEvent(ShooterIds.Shot(_def.ShooterId.Text), _onShot); } catch (ObjectDisposedException) { }
        _mount?.Dispose();
        _commands.Dispose(); _publisher.Dispose();
    }
}
