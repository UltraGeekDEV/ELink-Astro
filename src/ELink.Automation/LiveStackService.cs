using System.Globalization;
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

/// <summary>Live stacking into a fixed virtual field. Shot events are queued as they arrive (the shooter is never held up),
/// a worker registers each frame on the sky (its own WCS, a plate solve over the mesh, or the pointing it was taken at)
/// and resamples it into the stack at the requested pixel scale.</summary>
public sealed class LiveStackService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly CommandSet _commands;
    private readonly StatePublisher<LiveStackState> _publisher;
    private readonly object _gate = new();
    private readonly Dictionary<string, Action<ShotEvent>> _hooks = new();
    private readonly HashSet<(string, string, long)> _seen = new();
    private Channel<Item> _queue = Channel.CreateBounded<Item>(1);
    private CancellationTokenSource _run = new();
    private Task _worker = Task.CompletedTask;

    private LiveStackRequest? _request;
    /// <summary>One stack per filter (or one in all, when filters are not kept apart).</summary>
    private sealed class FilterStack
    {
        public required LiveStacker Stack;
        public required string Filter;
        public float[]? Pedestal;          // sky level per channel of its first frame, added back on output
        public double Exposure;
    }
    private readonly Dictionary<string, FilterStack> _stacks = new();
    private double _scale = double.NaN;     // all stacks share one grid once the scale is known
    private FilterStack? Primary => _stacks.Values.OrderByDescending(s => s.Stack.Frames).FirstOrDefault();
    private string _phase = "Idle", _message = "", _last = "";
    private int _rejected, _pending, _generation;
    private double _exposure, _lastScale = double.NaN;

    private sealed record Item(byte[] Fits, string Source, double RaHours, double DecDegrees, double Seconds, int Generation, string Filter);

    public LiveStackService(TypeSafeEVentNode node)
    {
        _node = node;
        _commands = new CommandSet(node);
        _publisher = new(node, LiveStackIds.State, LiveStackIds.GetState, BuildState);
    }

    public async Task StartAsync()
    {
        await _commands.AddAsync<LiveStackRequest, CommandResult>(LiveStackIds.Start, StartStackAsync, "start a live stack of a field at a pixel scale");
        await _commands.AddAsync<NOTESVoid, CommandResult>(LiveStackIds.Stop, async _ => { await UnhookAllAsync("Stopped", "stopped; the stack is kept"); return CommandResult.Success(); }, "stop taking frames");
        await _commands.AddAsync<NOTESVoid, CommandResult>(LiveStackIds.Reset, ResetAsync, "empty the stack");
        await _commands.AddAsync<LiveStackFrame, CommandResult>(LiveStackIds.Add, AddFrameAsync, "add a FITS frame to the stack");
        await _commands.AddAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage, r => Task.FromResult(GetImage(r)), "the stacked image as FITS");
        await _publisher.StartAsync();
    }

    private LiveStackState BuildState()
    {
        lock (_gate)
        {
            var s = new LiveStackState
            {
                Phase = _phase, Label = _request?.Label.Text ?? "", Message = _message, FramesStacked = _stacks.Values.Sum(x => x.Stack.Frames),
                FramesRejected = _rejected, FramesPending = _pending, Width = Primary?.Stack.Width ?? 0, Height = Primary?.Stack.Height ?? 0,
                PixelScaleArcsec = Primary?.Stack.Wcs.PixelScaleArcsec ?? 0, Channels = Primary?.Stack.Channels ?? 0, CoveragePercent = Primary is { } p ? Math.Round(p.Stack.Coverage() * 100, 2) : 0,
                TotalExposureSeconds = _exposure, LastFrame = _last,
            };
            foreach (var x in _stacks.Values.OrderBy(x => x.Filter)) s.Filters.Add($"{(x.Filter == "" ? "(none)" : x.Filter)}: {x.Stack.Frames}");
            return s;
        }
    }

    private Task Publish() => _publisher.PublishAsync();

    private static readonly string[] Interpolations = ["Bicubic", "Bilinear", "Nearest"];
    private static readonly string[] Registrations = ["Auto", "Solve", "Pointing"];

    private async Task<CommandResult> StartStackAsync(LiveStackRequest r)
    {
        if (!(r.FovWidthDegrees.Value > 0) || !(r.FovHeightDegrees.Value > 0) || r.FovWidthDegrees.Value > 60 || r.FovHeightDegrees.Value > 60)
            return CommandResult.Fail("the field must be between 0 and 60 degrees on a side");
        if (r.PixelScaleArcsec.Value < 0 || double.IsNaN(r.PixelScaleArcsec.Value)) return CommandResult.Fail("the pixel scale must be positive (or 0 for the frames' own)");
        if (!Interpolations.Contains(r.Interpolation.Text)) return CommandResult.Fail("Interpolation is Bicubic, Bilinear or Nearest");
        if (!Registrations.Contains(r.Registration.Text)) return CommandResult.Fail("Registration is Auto, Solve or Pointing");
        if (!Enum.TryParse<DebayerMode>(r.Debayer.Text, out _) || int.TryParse(r.Debayer.Text, out _)) return CommandResult.Fail("Debayer is Interpolated, SuperPixel or None");
        if (r.BayerPattern.Text.Trim() != "" && Debayer.Normalize(r.BayerPattern.Text) is null) return CommandResult.Fail("BayerPattern is RGGB, BGGR, GRBG, GBRG, or empty for the frame's own BAYERPAT");
        if (r.Registration.Text == "Pointing" && !(r.FramePixelScaleArcsec.Value > 0)) return CommandResult.Fail("Pointing registration needs the frames' pixel scale");
        if (r.Center.Epoch.Text is not ("J2000" or "")) return CommandResult.Fail("the field centre must be J2000");
        if (r.PixelScaleArcsec.Value > 0 && Size(r, r.PixelScaleArcsec.Value) is { Error: { } err }) return CommandResult.Fail(err);

        await UnhookAllAsync("Idle", "");
        lock (_gate)
        {
            _request = r; _stacks.Clear(); _scale = r.PixelScaleArcsec.Value > 0 ? r.PixelScaleArcsec.Value : double.NaN;
            _rejected = 0; _pending = 0; _exposure = 0; _lastScale = double.NaN; _last = ""; _seen.Clear();
            _generation++;
            _phase = "Stacking"; _message = double.IsNaN(_scale) ? "waiting for the first frame to set the scale" : "waiting for frames";
            _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait });
            _run = new CancellationTokenSource();
            var queue = _queue; var ct = _run.Token;
            _worker = Task.Run(() => WorkAsync(queue, ct));
        }
        foreach (var id in r.ShooterIds.Select(s => s.Text).Where(s => s != "").Distinct())
        {
            Action<ShotEvent> hook = shot => OnShot(shot);
            await _node.HookEventAsync(ShooterIds.Shot(id), hook, "live stack frames");
            lock (_gate) _hooks[id] = hook;
        }
        await Publish();
        return CommandResult.Success();
    }

    private static (int W, int H, string? Error) Size(LiveStackRequest r, double scale)
    {
        double w = r.FovWidthDegrees.Value * 3600 / scale, h = r.FovHeightDegrees.Value * 3600 / scale;
        if (w * h / 1e6 > Math.Max(0.01, r.MaxMegapixels.Value))
            return (0, 0, $"{r.FovWidthDegrees.Value:0.##}°x{r.FovHeightDegrees.Value:0.##}° at {scale:0.##}\"/px is {w * h / 1e6:0.#} megapixels, over the {r.MaxMegapixels.Value:0.#} allowed: use a coarser scale");
        return ((int)Math.Max(1, Math.Round(w)), (int)Math.Max(1, Math.Round(h)), null);
    }

    private static LiveStacker Create(LiveStackRequest r, double scale)
    {
        var (w, h, _) = Size(r, scale);
        var wcs = TanWcs.Centered(r.Center.RaHours.Value * 15, r.Center.DecDegrees.Value, r.PositionAngleDegrees.Value, scale, w, h);
        return new LiveStacker(wcs, w, h)
        {
            Interpolation = Enum.Parse<Interpolation>(r.Interpolation.Text), MatchFlux = r.MatchFlux.Value, RejectSigma = Math.Max(0, r.RejectSigma.Value),
        };
    }

    private void OnShot(ShotEvent shot)
    {
        if (shot.FrameType.Text is not ("Light" or "")) return;
        if (shot.Quality.Text == "Rejected") { Reject($"{shot.Shooter.Text}: rejected by its scope ({shot.QualityNote.Text})"); return; }
        if (!shot.Format.Text.StartsWith(".fit", StringComparison.OrdinalIgnoreCase)) { Reject($"{shot.Shooter.Text}: not FITS ({shot.Format.Text})"); return; }
        Channel<Item> queue; int gen;
        lock (_gate)
        {
            if (_phase != "Stacking") return;
            // a scope relays its shooters' frames: the same frame may arrive under several ids
            if (!_seen.Add((shot.Shooter.Text, shot.Timestamp.Text, Fingerprint(shot.Data.Data)))) return;
            queue = _queue; gen = _generation; _pending++;
        }
        var item = new Item(shot.Data.Data, shot.Shooter.Text, shot.PointingRaHours.Value, shot.PointingDecDegrees.Value, shot.ExposureSeconds.Value, gen, shot.Filter.Text);
        if (!queue.Writer.TryWrite(item)) { lock (_gate) _pending--; Reject($"{shot.Shooter.Text}: queue full, frame skipped"); }
        else _ = Publish();
    }

    /// <summary>Cheap content key: the length and a hash of bytes sampled across the frame.</summary>
    private static long Fingerprint(byte[] d)
    {
        ulong h = 14695981039346656037UL ^ (ulong)d.Length;
        int step = Math.Max(1, d.Length / 4096);
        for (int i = 0; i < d.Length; i += step) h = (h ^ d[i]) * 1099511628211UL;
        return (long)h;
    }

    private async Task<CommandResult> AddFrameAsync(LiveStackFrame f)
    {
        Channel<Item> queue; int gen;
        lock (_gate)
        {
            if (_request is null) return CommandResult.Fail("no stack: Start one first");
            queue = _queue; gen = _generation; _pending++;
        }
        if (f.Image.Data.Length == 0) { lock (_gate) _pending--; return CommandResult.Fail("no image"); }
        await queue.Writer.WriteAsync(new Item(f.Image.Data, f.Source.Text == "" ? "added" : f.Source.Text, f.PointingRaHours.Value, f.PointingDecDegrees.Value, double.NaN, gen, f.Filter.Text));
        await Publish();
        return CommandResult.Success();
    }

    private void Reject(string why)
    {
        lock (_gate) { _rejected++; _message = why; }
        _ = Publish();
    }

    private async Task WorkAsync(Channel<Item> queue, CancellationToken ct)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(ct))
            {
                string result;
                try { result = await StackOneAsync(item, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { result = "!" + ex.Message; }
                lock (_gate)
                {
                    if (item.Generation != _generation) continue;
                    _pending = Math.Max(0, _pending - 1);
                    _last = item.Source;
                    if (result.StartsWith('!')) { _rejected++; _message = $"{item.Source}: {result[1..]}"; }
                    else _message = result;
                }
                await Publish();
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Registers and adds one frame; returns a message, starting with '!' when the frame was rejected.</summary>
    private async Task<string> StackOneAsync(Item item, CancellationToken ct)
    {
        LiveStackRequest r;
        lock (_gate) { if (_request is null || item.Generation != _generation) return "!stack was replaced"; r = _request; }
        var img = FitsImage.Parse(item.Fits);
        var (data, width, height, channels, bin, colour) = Prepare(img, r);

        var (rawWcs, how) = await RegisterAsync(r, img, item, ct);   // the raw frame is what gets solved
        if (rawWcs is null) return "!" + how;
        var wcs = bin > 1 ? LiveStacker.Binned(rawWcs, bin) : rawWcs;

        LiveStacker stack; FilterStack fs;
        var background = new float[channels];
        // which stack: one per filter (the frame's, or its FITS header's), unless filters are stacked together
        string filter = item.Filter != "" ? item.Filter : (img.Get("FILTER") ?? "");
        string key = r.SeparateFilters.Value ? filter.Trim() : "";
        lock (_gate)
        {
            if (item.Generation != _generation) return "!stack was replaced";
            if (double.IsNaN(_scale))
            {
                if (Size(r, wcs.PixelScaleArcsec) is { Error: { } err }) return "!" + err;
                _scale = wcs.PixelScaleArcsec;
            }
            if (!_stacks.TryGetValue(key, out fs!)) _stacks[key] = fs = new FilterStack { Stack = Create(r, _scale), Filter = key };
            stack = fs.Stack;
            int plane = width * height;
            var bg = Enumerable.Range(0, channels).Select(c => LiveStacker.Background(data.AsSpan(c * plane, plane).ToArray())).ToArray();
            // the stack holds sky-subtracted signal (so frames can be compared and scaled); the first frame's sky is added back on output
            if (r.NormalizeBackground.Value)
            {
                fs.Pedestal ??= channels == 3 ? bg : [bg[0], bg[0], bg[0]];
                for (int c = 0; c < channels; c++) background[c] = bg[c];
            }
            _lastScale = rawWcs.PixelScaleArcsec;
        }
        var added = stack.Add(data, width, height, channels, wcs, background);
        if (!added.Added) return "!" + added.Message;
        double seconds = !double.IsNaN(item.Seconds) && item.Seconds > 0 ? item.Seconds : img.GetDouble("EXPTIME", img.GetDouble("EXPOSURE", 0));
        lock (_gate) { _exposure += seconds; fs.Exposure += seconds; }
        string resample = added.BinFactor > 1 ? $"binned {added.BinFactor}x, " : "";
        resample += added.Subsamples > 1 ? $"area-averaged {added.Subsamples}x{added.Subsamples}" : stack.Wcs.PixelScaleArcsec < wcs.PixelScaleArcsec * 0.999 ? $"interpolated ({stack.Interpolation})" : "resampled";
        string extra = (key != "" ? $", {key}" : "") + (Math.Abs(added.FluxScale - 1) > 0.02 ? FormattableString.Invariant($", flux x{added.FluxScale:0.00}") : "")
                       + (added.RejectedPixels > 0 ? $", {added.RejectedPixels} outlier pixels left out" : "");
        return $"{item.Source}: stacked ({how}{colour}, {wcs.PixelScaleArcsec:0.##}\"/px → {stack.Wcs.PixelScaleArcsec:0.##}\"/px, {resample}{extra})";
    }

    private async Task<(TanWcs? Wcs, string How)> RegisterAsync(LiveStackRequest r, FitsImage img, Item item, CancellationToken ct)
    {
        string mode = r.Registration.Text;
        if (mode == "Auto" && TanWcs.FromHeader(img.Header) is { } own) return (own, "frame WCS");
        double ra = item.RaHours, dec = item.DecDegrees;
        if (double.IsNaN(ra) || double.IsNaN(dec)) (ra, dec) = HeaderPointing(img);
        if (mode is "Auto" or "Solve")
        {
            double scale; lock (_gate) scale = !double.IsNaN(_lastScale) ? _lastScale : r.FramePixelScaleArcsec.Value;   // hint: what solved last
            var answers = await _node.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve, new SolveRequest
            {
                Image = new ELink.Contracts.RawBytes { Data = item.Fits }, HintRaHours = ra, HintDecDegrees = dec, HintRadiusDegrees = 5,
                ScaleLowArcsecPerPixel = scale > 0 ? scale * 0.8 : 0, ScaleHighArcsecPerPixel = scale > 0 ? scale * 1.25 : 0,
                TimeoutSeconds = r.SolveTimeoutSeconds.Value,
            }, TimeSpan.FromSeconds(r.SolveTimeoutSeconds.Value + 30));
            ct.ThrowIfCancellationRequested();
            var s = answers is { Count: > 0 } ? answers[0] : null;
            if (s is { Solved.Value: true })
                return (FromSolve(s, img.Width, img.Height), "solved");
            if (mode == "Solve" || !(r.FramePixelScaleArcsec.Value > 0))
                return (null, s is null ? "no plate solver on the mesh" : "did not solve: " + s.Message.Text);
        }
        if (double.IsNaN(ra) || double.IsNaN(dec)) return (null, "the frame has no pointing to register it by");
        if (!(r.FramePixelScaleArcsec.Value > 0)) return (null, "pointing registration needs the frames' pixel scale");
        return (TanWcs.Centered(ra * 15, dec, r.FramePositionAngleDegrees.Value, r.FramePixelScaleArcsec.Value, img.Width, img.Height), "pointing");
    }

    /// <summary>Where capture software says the frame was taken: RA/DEC in degrees, or OBJCTRA/OBJCTDEC as "hh mm ss".</summary>
    private static (double RaHours, double DecDegrees) HeaderPointing(FitsImage img)
    {
        double ra = img.GetDouble("RA", double.NaN) / 15, dec = img.GetDouble("DEC", double.NaN);
        if (!double.IsNaN(ra) && !double.IsNaN(dec)) return (ra, dec);
        if (ELink.Core.Astro.Sexagesimal.TryParse(img.Get("OBJCTRA")?.Replace(' ', ':'), out ra) &&
            ELink.Core.Astro.Sexagesimal.TryParse(img.Get("OBJCTDEC")?.Replace(' ', ':'), out dec)) return (ra, dec);
        return (double.NaN, double.NaN);
    }

    /// <summary>The solver's TAN solution, or one built from its centre, rotation and scale when it has none.</summary>
    public static TanWcs FromSolve(SolveResult s, int width, int height)
    {
        if (s.HasWcs.Value)
            return new TanWcs(s.WcsCrVal1.Value, s.WcsCrVal2.Value, s.WcsCrPix1.Value - 1, s.WcsCrPix2.Value - 1, s.WcsCd11.Value, s.WcsCd12.Value, s.WcsCd21.Value, s.WcsCd22.Value);
        double pa = double.IsNaN(s.PositionAngle.Value) ? 0 : s.PositionAngle.Value;
        return TanWcs.Centered(s.RaHours.Value * 15, s.DecDegrees.Value, pa, s.PixelScale.Value, width, height);
    }

    /// <summary>The frame as it goes into the stack: raw Bayer frames debayered as asked (super pixel halves the grid),
    /// RGB kept, anything else as mono.</summary>
    private static (float[] Data, int Width, int Height, int Channels, int Bin, string Note) Prepare(FitsImage img, LiveStackRequest r)
    {
        var mode = Enum.Parse<DebayerMode>(r.Debayer.Text);
        if (img.Channels == 1)
        {
            string? pattern = Debayer.Normalize(r.BayerPattern.Text) ?? Debayer.PatternOf(img);
            if (pattern is null || mode == DebayerMode.None || img.Width < 2 || img.Height < 2)
                return (img.Data, img.Width, img.Height, 1, 1, pattern is null ? "" : $", {pattern} kept raw");
            var c = Debayer.Apply(img.Data, img.Width, img.Height, pattern, mode);
            return (c.Data, c.Width, c.Height, 3, c.Bin, mode == DebayerMode.SuperPixel ? $", {pattern} super pixel" : $", {pattern} debayered");
        }
        if (img.Channels == 3) return (img.Data, img.Width, img.Height, 3, 1, ", RGB");
        return (Luminance(img), img.Width, img.Height, 1, 1, "");
    }

    private static float[] Luminance(FitsImage img)
    {
        int n = img.Width * img.Height;
        var r = new float[n];
        for (int c = 0; c < img.Channels; c++)
            for (int i = 0; i < n; i++) r[i] += img.Data[c * n + i] / img.Channels;
        return r;
    }

    private async Task<CommandResult> ResetAsync(NOTESVoid _)
    {
        lock (_gate)
        {
            if (_request is null) return CommandResult.Fail("no stack");
            _stacks.Clear(); _scale = _request.PixelScaleArcsec.Value > 0 ? _request.PixelScaleArcsec.Value : double.NaN;
            _rejected = 0; _exposure = 0; _message = "emptied";
        }
        await Publish();
        return CommandResult.Success();
    }

    private LiveStackImage GetImage(LiveStackImageRequest q)
    {
        FilterStack? fs; string label;
        lock (_gate)
        {
            label = _request?.Label.Text ?? "";
            fs = q.Filter.Text != "" ? _stacks.GetValueOrDefault(q.Filter.Text.Trim()) : Primary;
        }
        if (fs is null || fs.Stack.Frames == 0) return new LiveStackImage { Message = q.Filter.Text != "" ? $"nothing stacked through {q.Filter.Text} yet" : "nothing stacked yet" };
        var stack = fs.Stack;
        var (data, w, h, wcs) = stack.Reduced(q.MaxWidth.Value > 0 ? q.MaxWidth.Value : int.MaxValue, q.MaxHeight.Value > 0 ? q.MaxHeight.Value : int.MaxValue);
        int channels = data.Length / (w * h), plane = w * h;
        var pedestal = fs.Pedestal ?? [0f, 0f, 0f];
        if (q.Neutralize.Value && channels == 3) Neutralize(data, plane);
        // the sky level back on (one for all channels when neutralised), and where nothing has landed yet: that sky
        float common = (pedestal[0] + pedestal[1] + pedestal[2]) / 3;
        for (int c = 0; c < channels; c++)
        {
            float sky = q.Neutralize.Value && channels == 3 ? common : pedestal[channels == 3 ? c : 0];
            for (int i = c * plane; i < (c + 1) * plane; i++) data[i] = float.IsNaN(data[i]) ? sky : data[i] + sky;
        }
        var cards = wcs.Cards().ToList();
        string Q(string s) => "'" + s.Replace("'", "''") + "'";
        cards.Add(("OBJECT", Q(label)));
        if (fs.Filter != "") cards.Add(("FILTER", Q(fs.Filter)));
        cards.Add(("NCOMBINE", stack.Frames.ToString(CultureInfo.InvariantCulture)));
        cards.Add(("EXPTIME", fs.Exposure.ToString("0.###", CultureInfo.InvariantCulture)));
        cards.Add(("CREATOR", Q("ELink live stack")));
        return new LiveStackImage
        {
            Ok = true, Width = w, Height = h, Channels = channels, PixelScaleArcsec = wcs.PixelScaleArcsec, Frames = stack.Frames, Filter = fs.Filter,
            Image = new ELink.Contracts.RawBytes { Data = FitsImage.WriteFloat32(w, h, data, cards, channels: channels) },
        };
    }

    /// <summary>Background and colour balance of a sky-subtracted RGB stack: each channel's remaining sky offset is
    /// removed, then red and blue are scaled so the stars (the brightest pixels) are as bright as in green on average.</summary>
    public static void Neutralize(float[] data, int plane)
    {
        var bright = new double[3];
        for (int c = 0; c < 3; c++)
        {
            var values = new List<float>(plane / 4 + 1);
            for (int i = c * plane; i < (c + 1) * plane; i += 4) if (!float.IsNaN(data[i])) values.Add(data[i]);
            if (values.Count == 0) return;
            values.Sort();
            float sky = values[values.Count / 2];
            for (int i = c * plane; i < (c + 1) * plane; i++) if (!float.IsNaN(data[i])) data[i] -= sky;
            // mean of the top 0.5 %: the stars
            int top = Math.Max(1, values.Count / 200);
            bright[c] = values.Skip(values.Count - top).Average(v => v - sky);
        }
        if (bright[1] <= 0) return;
        foreach (int c in new[] { 0, 2 })
        {
            if (bright[c] <= 0) continue;
            float k = (float)(bright[1] / bright[c]);
            for (int i = c * plane; i < (c + 1) * plane; i++) if (!float.IsNaN(data[i])) data[i] *= k;
        }
    }

    private async Task UnhookAllAsync(string phase, string message)
    {
        List<KeyValuePair<string, Action<ShotEvent>>> hooks;
        Task worker;
        lock (_gate)
        {
            hooks = _hooks.ToList(); _hooks.Clear();
            _queue.Writer.TryComplete(); _run.Cancel();
            worker = _worker;
            if (_request is not null) { _phase = phase; _message = message; }
            _pending = 0;
        }
        foreach (var (id, hook) in hooks)
            try { _node.UnhookEvent(ShooterIds.Shot(id), hook); } catch (ObjectDisposedException) { }
        try { await worker.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        await Publish();
    }

    public async ValueTask DisposeAsync()
    {
        try { await UnhookAllAsync("Stopped", ""); } catch (ObjectDisposedException) { }
        _commands.Dispose(); _publisher.Dispose();
    }
}
