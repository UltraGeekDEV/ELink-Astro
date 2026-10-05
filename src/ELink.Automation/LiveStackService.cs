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
        /// <summary>Pseudo mono: the out-of-focus luminance stack holds the same frames again, so its frames are not counted twice.</summary>
        public bool Counted = true;
        /// <summary>The filter written in the image's header, when the stack's key is something else (a layer's name).</summary>
        public string? FitsFilter;
        /// <summary>The coarsest pixel scale of the frames in it (arcsec/px): how sharp the stack can be.</summary>
        public double InputScale;
    }

    /// <summary>The first layer a frame feeds: shot through its filter (a layer with none takes any) and within its scale range.</summary>
    public static ImagingLayer? LayerFor(IEnumerable<ImagingLayer> layers, string filter, double scale) =>
        layers.FirstOrDefault(l => (l.Filter.Text == "" || string.Equals(l.Filter.Text, filter, StringComparison.OrdinalIgnoreCase))
            && (l.MinScaleArcsec.Value <= 0 || scale >= l.MinScaleArcsec.Value * (1 - 1e-6))
            && (l.MaxScaleArcsec.Value <= 0 || scale <= l.MaxScaleArcsec.Value * (1 + 1e-6)));
    private const string PseudoLuminance = "pm:L";
    private static string PseudoKey(string channel) => "pm:" + channel;
    private static readonly string[] PseudoChannels = ["R", "G", "B"];
    private int FramesIn() => _stacks.Values.Where(x => x.Counted).Sum(x => x.Stack.Frames);
    private readonly Dictionary<string, FilterStack> _stacks = new();
    private double _scale = double.NaN;     // all stacks share one grid once the scale is known
    private FilterStack? Primary => _stacks.Values.OrderByDescending(s => s.Counted).ThenByDescending(s => s.Stack.Frames).FirstOrDefault();
    private string _phase = "Idle", _message = "", _last = "";
    private int _rejected, _pending, _generation;
    private double _exposure, _lastScale = double.NaN;

    private sealed record Item(byte[] Fits, string Source, double RaHours, double DecDegrees, double Seconds, int Generation, string Filter, string Pseudo = "");

    private readonly string? _dataDir;
    private string? _sessionDir;
    private int _framesSaved;
    private Timer? _saveTimer;
    private readonly SemaphoreSlim _saving = new(1, 1);
    /// <summary>How often a kept stack is saved while frames come in.</summary>
    public TimeSpan SaveEvery { get; set; } = TimeSpan.FromMinutes(5);

    /// <param name="dataDir">where kept stacks live (a folder per session key); null = stacks are never kept</param>
    public LiveStackService(TypeSafeEVentNode node, string? dataDir = null)
    {
        _dataDir = dataDir;
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
        await _commands.AddAsync<NOTESVoid, CommandResult>(LiveStackIds.Save, async _ => await SaveAsync(), "save a kept stack now");
        await _commands.AddAsync<LiveStackImageRequest, LiveStackImage>(LiveStackIds.GetImage, r => Task.FromResult(GetImage(r)), "the stacked image as FITS");
        await _publisher.StartAsync();
    }

    private LiveStackState BuildState()
    {
        lock (_gate)
        {
            var s = new LiveStackState
            {
                Phase = _phase, Label = _request?.Label.Text ?? "", Message = _message, FramesStacked = FramesIn(),
                FramesRejected = _rejected, FramesPending = _pending, Width = Primary?.Stack.Width ?? 0, Height = Primary?.Stack.Height ?? 0,
                PixelScaleArcsec = Primary?.Stack.Wcs.PixelScaleArcsec ?? 0, Channels = _stacks.Keys.Any(k => k.StartsWith("pm:")) ? 3 : Primary?.Stack.Channels ?? 0, CoveragePercent = Primary is { } p ? Math.Round(p.Stack.Coverage() * 100, 2) : 0,
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
        // a kept stack of this name: carry on with it (same field only), or start it afresh
        string? dir = r.SessionKey.Text.Trim() != "" && _dataDir is not null ? Path.Combine(_dataDir, "stacks", Safe(r.SessionKey.Text)) : null;
        Dictionary<string, FilterStack>? restored = null; double restoredScale = double.NaN; double restoredExposure = 0;
        if (dir is not null && Directory.Exists(dir))
        {
            if (r.Resume.Value)
            {
                try { (restored, restoredScale, restoredExposure) = LoadSession(dir, r); }
                catch (Exception ex) { return CommandResult.Fail($"the kept stack '{r.SessionKey.Text}' cannot be continued: {ex.Message} (start it afresh, or use another name)"); }
            }
            else Directory.Delete(dir, true);
        }
        lock (_gate)
        {
            _request = r; _stacks.Clear(); _scale = r.PixelScaleArcsec.Value > 0 ? r.PixelScaleArcsec.Value : double.NaN;
            _rejected = 0; _pending = 0; _exposure = 0; _lastScale = double.NaN; _last = ""; _seen.Clear();
            _sessionDir = dir; _framesSaved = 0;
            if (restored is not null)
            {
                foreach (var (k, v) in restored) _stacks[k] = v;
                _scale = restoredScale; _exposure = restoredExposure; _framesSaved = restored.Values.Sum(x => x.Stack.Frames);
            }
            _generation++;
            _phase = "Stacking";
            _message = restored is not null ? $"carrying on: {restored.Values.Sum(x => x.Stack.Frames)} frames kept from before"
                     : double.IsNaN(_scale) ? "waiting for the first frame to set the scale" : "waiting for frames";
            _queue = Channel.CreateBounded<Item>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.Wait });
            _run = new CancellationTokenSource();
            var queue = _queue; var ct = _run.Token;
            _worker = Task.Run(() => WorkAsync(queue, ct));
            if (dir is not null) _saveTimer = new Timer(_ => _ = SaveAsync(), null, SaveEvery, SaveEvery);
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
        var item = new Item(shot.Data.Data, shot.Shooter.Text, shot.PointingRaHours.Value, shot.PointingDecDegrees.Value, shot.ExposureSeconds.Value, gen, shot.Filter.Text, shot.PseudoChannel.Text);
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
        string calibrated = "";
        if (r.Calibrate.Value) (img, calibrated) = await CalibrateAsync(img, item, ct);
        var (data, width, height, channels, bin, colour) = Prepare(img, r);
        colour = calibrated + colour;

        var (rawWcs, how) = await RegisterAsync(r, img, item, ct);   // the raw frame is what gets solved
        if (rawWcs is null) return "!" + how;
        var wcs = bin > 1 ? LiveStacker.Binned(rawWcs, bin) : rawWcs;

        if (item.Pseudo != "") return await StackPseudoAsync(item, r, img, data, width, height, channels, wcs, how, colour);

        // which stacks: one per filter (the frame's, or its FITS header's), unless filters are stacked together; with layers, the stack
        // of every layer the frame feeds (its filter, its pixel scale within the layer's range)
        string filter = item.Filter != "" ? item.Filter : (img.Get("FILTER") ?? "");
        var targets = new List<(string Key, string Fits)>();
        if (r.Layers.Count > 0)
        {
            foreach (var layer in r.Layers.Where(l => LayerFor([l], filter.Trim(), rawWcs.PixelScaleArcsec) is not null)) targets.Add((layer.Label.Text, layer.Filter.Text));
            if (targets.Count == 0) return FormattableString.Invariant($"{item.Source}: skipped, {rawWcs.PixelScaleArcsec:0.##}\"/px through {(filter == "" ? "no filter" : filter)} feeds no layer");
        }
        else { string k = r.SeparateFilters.Value ? filter.Trim() : ""; targets.Add((k, k)); }

        double seconds = !double.IsNaN(item.Seconds) && item.Seconds > 0 ? item.Seconds : img.GetDouble("EXPTIME", img.GetDouble("EXPOSURE", 0));
        string message = "", failure = "";
        int stacked = 0;
        foreach (var (key, fitsFilter) in targets)
        {
            var (error, note) = AddToStack(r, item, key, fitsFilter, data, width, height, channels, wcs, rawWcs, seconds, how, colour, countExposure: stacked == 0);
            if (error is not null) { failure = error; continue; }
            stacked++; message = message == "" ? note : message + "; also " + key;
        }
        return stacked > 0 ? message : failure;
    }

    /// <summary>Tells whoever is watching where a frame has landed on the stack (its corners as fractions of the grid).</summary>
    private void AnnounceFrame(string source, string stackKey, LiveStacker stack, TanWcs wcs, int width, int height)
    {
        try
        {
            var corners = new[] { (0.0, 0.0), (width, 0.0), (width, height), (0.0, height) }.Select(c =>
            {
                var (ra, dec) = wcs.PixelToSky(c.Item1, c.Item2);
                var (sx, sy) = stack.Wcs.SkyToPixel(ra, dec);
                return (X: sx / stack.Width, Y: sy / stack.Height);
            }).ToArray();
            int frames; lock (_gate) frames = FramesIn();
            var ev = new StackFrameAdded
            {
                Source = source, Stack = stackKey, Frames = frames,
                X0 = corners[0].X, Y0 = corners[0].Y, X1 = corners[1].X, Y1 = corners[1].Y, X2 = corners[2].X, Y2 = corners[2].Y, X3 = corners[3].X, Y3 = corners[3].Y,
            };
            _ = Task.Run(async () => { try { await _node.FireEventAsync(LiveStackIds.FrameAdded, ev); } catch (Exception) { } });
        }
        catch (Exception) { }
    }

    /// <summary>Adds a registered frame to one stack: (error, message).</summary>
    private (string? Error, string Message) AddToStack(LiveStackRequest r, Item item, string key, string fitsFilter, float[] data, int width, int height, int channels,
        TanWcs wcs, TanWcs rawWcs, double seconds, string how, string colour, bool countExposure)
    {
        LiveStacker stack; FilterStack fs;
        var background = new float[channels];
        lock (_gate)
        {
            if (item.Generation != _generation) return ("!stack was replaced", "");
            if (double.IsNaN(_scale))
            {
                if (Size(r, wcs.PixelScaleArcsec) is { Error: { } err }) return ("!" + err, "");
                _scale = wcs.PixelScaleArcsec;
            }
            if (!_stacks.TryGetValue(key, out fs!)) _stacks[key] = fs = new FilterStack { Stack = Create(r, _scale), Filter = key, FitsFilter = fitsFilter };
            fs.InputScale = Math.Max(fs.InputScale, rawWcs.PixelScaleArcsec);
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
        if (!added.Added) return ("!" + added.Message, "");
        lock (_gate) { if (countExposure) _exposure += seconds; fs.Exposure += seconds; }
        AnnounceFrame(item.Source, key, stack, wcs, width, height);
        string resample = added.BinFactor > 1 ? $"binned {added.BinFactor}x, " : "";
        resample += added.Subsamples > 1 ? $"area-averaged {added.Subsamples}x{added.Subsamples}" : stack.Wcs.PixelScaleArcsec < wcs.PixelScaleArcsec * 0.999 ? $"interpolated ({stack.Interpolation})" : "resampled";
        string extra = (key != "" ? $", {key}" : "") + (Math.Abs(added.FluxScale - 1) > 0.02 ? FormattableString.Invariant($", flux x{added.FluxScale:0.00}") : "")
                       + (added.RejectedPixels > 0 ? $", {added.RejectedPixels} outlier pixels left out" : "");
        return (null, $"{item.Source}: stacked ({how}{colour}, {wcs.PixelScaleArcsec:0.##}\"/px → {stack.Wcs.PixelScaleArcsec:0.##}\"/px, {resample}{extra})");
    }

    /// <summary>Pseudo mono: one colour frame in which one channel was in focus. That channel goes to its own stack (the colours),
    /// the mean of the other two, out of focus, to the luminance stack; all on the same registration.</summary>
    private Task<string> StackPseudoAsync(Item item, LiveStackRequest r, FitsImage img, float[] data, int width, int height, int channels, TanWcs wcs, string how, string colour)
    {
        int c = Array.IndexOf(PseudoChannels, item.Pseudo);
        if (c < 0) return Task.FromResult($"!pseudo mono channel {item.Pseudo} is not R, G or B");
        if (channels != 3) return Task.FromResult("!pseudo mono needs the colour frame debayered (Debayer: Interpolated or SuperPixel)");
        int plane = width * height;
        var sharp = data.AsSpan(c * plane, plane).ToArray();
        var soft = new float[plane];
        for (int k = 0; k < 3; k++)
        {
            if (k == c) continue;
            for (int i = 0; i < plane; i++) soft[i] += data[k * plane + i] / 2;
        }
        float bgSharp = r.NormalizeBackground.Value ? LiveStacker.Background(sharp) : 0, bgSoft = r.NormalizeBackground.Value ? LiveStacker.Background(soft) : 0;
        FilterStack colourStack, lumaStack;
        lock (_gate)
        {
            if (item.Generation != _generation) return Task.FromResult("!stack was replaced");
            if (double.IsNaN(_scale))
            {
                if (Size(r, wcs.PixelScaleArcsec) is { Error: { } err }) return Task.FromResult("!" + err);
                _scale = wcs.PixelScaleArcsec;
            }
            FilterStack Get(string key, bool counted) => _stacks.TryGetValue(key, out var x) ? x : _stacks[key] = new FilterStack { Stack = Create(r, _scale), Filter = key, Counted = counted };
            colourStack = Get(PseudoKey(item.Pseudo), true); lumaStack = Get(PseudoLuminance, false);
            if (r.NormalizeBackground.Value)
            {
                colourStack.Pedestal ??= [bgSharp];
                lumaStack.Pedestal ??= [bgSoft];
            }
            _lastScale = wcs.PixelScaleArcsec;
        }
        var added = colourStack.Stack.Add(sharp, width, height, 1, wcs, [bgSharp]);
        if (!added.Added) return Task.FromResult("!" + added.Message);
        lumaStack.Stack.Add(soft, width, height, 1, wcs, [bgSoft]);
        double seconds = !double.IsNaN(item.Seconds) && item.Seconds > 0 ? item.Seconds : img.GetDouble("EXPTIME", img.GetDouble("EXPOSURE", 0));
        lock (_gate) { _exposure += seconds; colourStack.Exposure += seconds; lumaStack.Exposure += seconds; }
        AnnounceFrame(item.Source, PseudoKey(item.Pseudo), colourStack.Stack, wcs, width, height);
        return Task.FromResult($"{item.Source}: stacked, {item.Pseudo} in focus ({how}{colour}, {wcs.PixelScaleArcsec:0.##}\"/px → {colourStack.Stack.Wcs.PixelScaleArcsec:0.##}\"/px)");
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

    private sealed record CachedMaster(float[]? Data, string Id, DateTime At);
    private readonly Dictionary<string, CachedMaster> _masters = new();
    /// <summary>How long a library answer is trusted (so masters taken meanwhile are picked up).</summary>
    public TimeSpan MasterCacheTime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The raw frame less its dark (or bias), divided by its flat, from the calibration library (when it has any).</summary>
    private async Task<(FitsImage Image, string Note)> CalibrateAsync(FitsImage img, Item item, CancellationToken ct)
    {
        if (img.Channels != 1 || img.Get("IMAGETYP") is { } t && t.StartsWith("Master", StringComparison.OrdinalIgnoreCase)) return (img, "");
        string filter = item.Filter != "" ? item.Filter : (img.Get("FILTER") ?? "").Trim();
        double seconds = !double.IsNaN(item.Seconds) && item.Seconds > 0 ? item.Seconds : img.GetDouble("EXPTIME", img.GetDouble("EXPOSURE", 0));
        var q = new MasterQuery
        {
            ShooterId = item.Source, ExposureSeconds = seconds, Gain = img.GetDouble("GAIN"), Iso = (img.Get("ISOSPEED") ?? "").Trim(),
            TemperatureC = img.GetDouble("CCD-TEMP"), BinX = Math.Max(1, (int)img.GetDouble("XBINNING", 1)), Filter = filter, Width = img.Width, Height = img.Height,
        };
        q.Kind = "Dark"; var dark = await MasterAsync(q, ct);
        q.Kind = "Flat"; var flat = await MasterAsync(q, ct);
        if (dark.Data is null && flat.Data is null) return (img, "");
        var data = Calibrate.Apply(img.Data, dark.Data, flat.Data);
        string note = ", " + string.Join("+", new[] { dark.Data is null ? null : dark.Id.StartsWith("bias") ? "bias" : "dark", flat.Data is null ? null : "flat" }.Where(x => x is not null));
        return (FitsImage.FromPlanar(img.Width, img.Height, 1, data, img.Header, img.Range), note);
    }

    private async Task<CachedMaster> MasterAsync(MasterQuery q, CancellationToken ct)
    {
        string key = $"{q.Kind.Text}|{q.ShooterId.Text}|{q.ExposureSeconds.Value:R}|{q.Gain.Value:R}|{q.Iso.Text}|{Math.Round(q.TemperatureC.Value)}|{q.BinX.Value}|{q.Filter.Text}|{q.Width.Value}x{q.Height.Value}";
        lock (_gate) if (_masters.TryGetValue(key, out var c) && DateTime.UtcNow - c.At < MasterCacheTime) return c;
        CachedMaster found = new(null, "", DateTime.UtcNow);
        try
        {
            var answers = await _node.CallFunctionAsync<MasterQuery, MasterMatch>(CalibrationIds.Find, q, TimeSpan.FromSeconds(20));
            if (answers?.FirstOrDefault(a => a.Found.Value) is { } m)
            {
                var master = FitsImage.Parse(m.Image.Data);
                if (master.Width == q.Width.Value && master.Height == q.Height.Value) found = new(master.Data, m.Master.Id.Text, DateTime.UtcNow);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        lock (_gate) _masters[key] = found;
        return found;
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
            if (q.Filter.Text == "" && _stacks.Count > 0 && _stacks.Keys.All(k => k.StartsWith("pm:"))) return PseudoImage(q, label);
            bool combined = string.Equals(q.Filter.Text, "Combined", StringComparison.OrdinalIgnoreCase)
                            || q.Filter.Text == "" && _request is { Layers.Count: > 1 } && _stacks.Count > 1;
            if (combined) return CombinedImage(q, label);
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
        if ((fs.FitsFilter ?? fs.Filter) != "") cards.Add(("FILTER", Q(fs.FitsFilter ?? fs.Filter)));
        if (fs.FitsFilter is not null && fs.FitsFilter != fs.Filter && fs.Filter != "") cards.Add(("LAYER", Q(fs.Filter)));
        cards.Add(("NCOMBINE", stack.Frames.ToString(CultureInfo.InvariantCulture)));
        cards.Add(("EXPTIME", fs.Exposure.ToString("0.###", CultureInfo.InvariantCulture)));
        cards.Add(("CREATOR", Q("ELink live stack")));
        return new LiveStackImage
        {
            Ok = true, Width = w, Height = h, Channels = channels, PixelScaleArcsec = wcs.PixelScaleArcsec, Frames = stack.Frames, Filter = fs.Filter,
            Image = new ELink.Contracts.RawBytes { Data = FitsImage.WriteFloat32(w, h, data, cards, channels: channels) },
        };
    }

    private LiveStackImage MonoImage(FilterStack fs, LiveStackImageRequest q, string label, string name)
    {
        var (data, w, h, wcs) = fs.Stack.Reduced(q.MaxWidth.Value > 0 ? q.MaxWidth.Value : int.MaxValue, q.MaxHeight.Value > 0 ? q.MaxHeight.Value : int.MaxValue);
        return MonoImage(data, w, h, wcs, fs.Pedestal is { Length: > 0 } p ? p[0] : 0f, fs.Stack.Frames, fs.Exposure, label, name);
    }

    private static LiveStackImage MonoImage(float[] data, int w, int h, TanWcs wcs, float sky, int frames, double exposure, string label, string name)
    {
        for (int i = 0; i < data.Length; i++) data[i] = float.IsNaN(data[i]) ? sky : data[i] + sky;
        var cards = wcs.Cards().ToList();
        string Q(string t) => "'" + t.Replace("'", "''") + "'";
        cards.Add(("OBJECT", Q(label))); cards.Add(("FILTER", Q(name)));
        cards.Add(("NCOMBINE", frames.ToString(CultureInfo.InvariantCulture)));
        cards.Add(("EXPTIME", exposure.ToString("0.###", CultureInfo.InvariantCulture)));
        cards.Add(("CREATOR", Q("ELink live stack")));
        return new LiveStackImage
        {
            Ok = true, Width = w, Height = h, Channels = 1, PixelScaleArcsec = wcs.PixelScaleArcsec, Frames = frames, Filter = name,
            Image = new ELink.Contracts.RawBytes { Data = FitsImage.WriteFloat32(w, h, data, cards, channels: 1) },
        };
    }

    /// <summary>Layers laid over each other: the coarsest stack (the strongest signal) is the base, and each finer one adds what it
    /// resolves that the coarser one cannot, its detail (itself less a blur to the coarser one's resolution), scaled so that the
    /// two agree on how much light there is. Where a finer layer has no data (yet) the base shows as it is.</summary>
    private LiveStackImage CombinedImage(LiveStackImageRequest q, string label)
    {
        // (called with the gate held)
        var layers = _stacks.Values.Where(s => s.Stack.Frames > 0).OrderByDescending(s => s.InputScale > 0 ? s.InputScale : 1e9).ThenByDescending(s => s.Stack.Frames).ToList();
        if (layers.Count == 0) return new LiveStackImage { Message = "nothing stacked yet" };
        int mw = q.MaxWidth.Value > 0 ? q.MaxWidth.Value : int.MaxValue, mh = q.MaxHeight.Value > 0 ? q.MaxHeight.Value : int.MaxValue;
        var (data, w, h, wcs) = layers[0].Stack.Reduced(mw, mh);
        int channels = data.Length / (w * h), plane = w * h;
        var pedestal = layers[0].Pedestal ?? [0f, 0f, 0f];
        if (q.Neutralize.Value && channels == 3) Neutralize(data, plane);
        var used = new List<string> { layers[0].Filter };
        double previous = layers[0].InputScale;
        foreach (var layer in layers.Skip(1))
        {
            var (fine, fw, fh, _) = layer.Stack.Reduced(mw, mh);
            if (fw != w || fh != h) continue;
            int fc = fine.Length / plane;
            // the fine layer as one plane (mono: its channels' mean)
            var detail = new float[plane];
            for (int i = 0; i < plane; i++)
            {
                float sum = 0; int n = 0;
                for (int c = 0; c < fc; c++) { float v = fine[c * plane + i]; if (!float.IsNaN(v)) { sum += v; n++; } }
                detail[i] = n == fc ? sum / fc : float.NaN;
            }
            // blur it to the coarser layer's resolution: what is left is what only the finer layer sees
            double sigma = previous > 0 && wcs.PixelScaleArcsec > 0 ? previous / wcs.PixelScaleArcsec / 2.355 : 0;
            var soft = Blur(detail, w, h, sigma);
            // the same light in both: the base's total against the finer layer's, over where the latter has data
            double sumBase = 0, sumSoft = 0;
            for (int i = 0; i < plane; i++)
            {
                if (float.IsNaN(soft[i])) continue;
                float b = 0; int n = 0;
                for (int c = 0; c < channels; c++) { float v = data[c * plane + i]; if (!float.IsNaN(v)) { b += v; n++; } }
                if (n < channels) continue;
                sumBase += Math.Max(0, b / channels); sumSoft += Math.Max(0, soft[i]);
            }
            float k = sumSoft > 0 && sumBase > 0 ? (float)(sumBase / sumSoft) : 1f;
            for (int i = 0; i < plane; i++)
            {
                if (float.IsNaN(soft[i]) || float.IsNaN(detail[i])) continue;
                float add = k * (detail[i] - soft[i]);
                for (int c = 0; c < channels; c++) if (!float.IsNaN(data[c * plane + i])) data[c * plane + i] += add;
            }
            used.Add(layer.Filter);
            if (layer.InputScale > 0) previous = layer.InputScale;
        }
        float common = (pedestal[0] + pedestal[Math.Min(1, pedestal.Length - 1)] + pedestal[Math.Min(2, pedestal.Length - 1)]) / 3;
        for (int c = 0; c < channels; c++)
        {
            float sky = q.Neutralize.Value && channels == 3 ? common : pedestal[channels == 3 ? c : 0];
            for (int i = c * plane; i < (c + 1) * plane; i++) data[i] = float.IsNaN(data[i]) ? sky : data[i] + sky;
        }
        int frames = layers.Sum(s => s.Stack.Frames);
        var cards = wcs.Cards().ToList();
        string Q(string t) => "'" + t.Replace("'", "''") + "'";
        cards.Add(("OBJECT", Q(label)));
        if ((layers[0].FitsFilter ?? "") != "") cards.Add(("FILTER", Q(layers[0].FitsFilter!)));
        cards.Add(("LAYERS", Q(string.Join("+", used))));
        cards.Add(("NCOMBINE", frames.ToString(CultureInfo.InvariantCulture)));
        cards.Add(("EXPTIME", layers.Sum(s => s.Exposure).ToString("0.###", CultureInfo.InvariantCulture)));
        cards.Add(("CREATOR", Q("ELink live stack")));
        return new LiveStackImage
        {
            Ok = true, Width = w, Height = h, Channels = channels, PixelScaleArcsec = wcs.PixelScaleArcsec, Frames = frames, Filter = "Combined",
            Image = new ELink.Contracts.RawBytes { Data = FitsImage.WriteFloat32(w, h, data, cards, channels: channels) },
        };
    }

    /// <summary>Gaussian blur of one plane, where pixels without data (NaN) count for nothing (and stay NaN if they were).</summary>
    public static float[] Blur(float[] src, int w, int h, double sigma)
    {
        if (sigma < 0.4) return (float[])src.Clone();
        int r = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        var kernel = new float[2 * r + 1];
        for (int i = -r; i <= r; i++) kernel[i + r] = (float)Math.Exp(-i * i / (2 * sigma * sigma));
        var tmpV = new float[w * h]; var tmpW = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0, ws = 0;
                for (int i = Math.Max(0, x - r); i <= Math.Min(w - 1, x + r); i++)
                {
                    float v = src[y * w + i];
                    if (float.IsNaN(v)) continue;
                    float k = kernel[i - x + r]; s += v * k; ws += k;
                }
                tmpV[y * w + x] = s; tmpW[y * w + x] = ws;
            }
        var result = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (float.IsNaN(src[y * w + x])) { result[y * w + x] = float.NaN; continue; }
                float s = 0, ws = 0;
                for (int j = Math.Max(0, y - r); j <= Math.Min(h - 1, y + r); j++)
                {
                    float k = kernel[j - y + r]; s += tmpV[j * w + x] * k; ws += tmpW[j * w + x] * k;
                }
                result[y * w + x] = ws > 0 ? s / ws : float.NaN;
            }
        return result;
    }

    /// <summary>The pseudo mono colour image: red, green and blue from the frames in which that channel was in focus, optionally with
    /// the out-of-focus luminance mixed in (its weight 0 leaves that data out; 1 puts in all of it).</summary>
    private LiveStackImage PseudoImage(LiveStackImageRequest q, string label)
    {
        // (called with the gate held)
        var parts = PseudoChannels.Select(c => _stacks.GetValueOrDefault(PseudoKey(c))).ToArray();
        if (parts.Any(p => p is null || p.Stack.Frames == 0))
            return new LiveStackImage { Message = "pseudo mono needs frames with each of red, green and blue in focus (" + string.Join(", ", PseudoChannels.Select((c, i) => $"{c}: {parts[i]?.Stack.Frames ?? 0}")) + ")" };
        int mw = q.MaxWidth.Value > 0 ? q.MaxWidth.Value : int.MaxValue, mh = q.MaxHeight.Value > 0 ? q.MaxHeight.Value : int.MaxValue;
        var reduced = parts.Select(p => p!.Stack.Reduced(mw, mh)).ToArray();
        int w = reduced[0].Width, h = reduced[0].Height, plane = w * h;
        if (reduced.Any(x => x.Width != w || x.Height != h)) return new LiveStackImage { Message = "the pseudo mono stacks differ in size" };
        var data = new float[3 * plane];
        for (int c = 0; c < 3; c++) Array.Copy(reduced[c].Data, 0, data, c * plane, plane);
        var wcs = reduced[0].Wcs;
        var pedestal = parts.Select(p => p!.Pedestal is { Length: > 0 } ped ? ped[0] : 0f).ToArray();
        if (string.Equals(q.PseudoOutput.Text, "Luminance", StringComparison.OrdinalIgnoreCase))
        {
            // all the out-of-focus light, as one mono image
            if (_stacks.GetValueOrDefault(PseudoLuminance) is not { Stack.Frames: > 0 } l) return new LiveStackImage { Message = "no out-of-focus light stacked yet" };
            return MonoImage(l, q, label, "pseudo mono luminance");
        }
        if (string.Equals(q.PseudoOutput.Text, "Sharp", StringComparison.OrdinalIgnoreCase))
        {
            // the in-focus colours added up: sharp, with a third of the light of each frame
            var mono = new float[plane];
            for (int i = 0; i < plane; i++) mono[i] = (data[i] + data[plane + i] + data[2 * plane + i]) / 3;
            return MonoImage(mono, w, h, wcs, pedestal.Average(), parts.Sum(p => p!.Stack.Frames), parts.Sum(p => p!.Exposure), label, "pseudo mono sharp");
        }
        if (q.Neutralize.Value) Neutralize(data, plane);

        double weight = Math.Clamp(q.OutOfFocusWeight.Value, 0, 1);
        bool mixed = false;
        if (weight > 0 && _stacks.GetValueOrDefault(PseudoLuminance) is { Stack.Frames: > 0 } luma)
        {
            var (ld, lw, lh, _) = luma.Stack.Reduced(mw, mh);
            if (lw == w && lh == h)
            {
                float lsky = LiveStacker.Background(ld);
                var lc = new float[plane];
                double sumC = 0, sumO = 0;
                for (int i = 0; i < plane; i++)
                {
                    lc[i] = (data[i] + data[plane + i] + data[2 * plane + i]) / 3;
                    if (!float.IsNaN(lc[i]) && !float.IsNaN(ld[i])) { sumC += Math.Max(0, lc[i]); sumO += Math.Max(0, ld[i] - lsky); }
                }
                // the out-of-focus light is the same photons spread out: scale it to the sharp colours' total before mixing
                float k = sumO > 0 ? (float)(sumC / sumO) : 0;
                if (k > 0)
                {
                    for (int i = 0; i < plane; i++)
                    {
                        if (float.IsNaN(lc[i]) || float.IsNaN(ld[i])) continue;
                        float shift = (float)(weight * ((ld[i] - lsky) * k - lc[i]));
                        for (int c = 0; c < 3; c++) data[c * plane + i] += shift;
                    }
                    mixed = true;
                }
            }
        }
        float common = (pedestal[0] + pedestal[1] + pedestal[2]) / 3;
        for (int c = 0; c < 3; c++)
        {
            float sky = q.Neutralize.Value ? common : pedestal[c];
            for (int i = c * plane; i < (c + 1) * plane; i++) data[i] = float.IsNaN(data[i]) ? sky : data[i] + sky;
        }
        int frames = parts.Sum(p => p!.Stack.Frames);
        var cards = wcs.Cards().ToList();
        string Q(string t) => "'" + t.Replace("'", "''") + "'";
        cards.Add(("OBJECT", Q(label)));
        cards.Add(("FILTER", Q("pseudo mono")));
        cards.Add(("NCOMBINE", frames.ToString(CultureInfo.InvariantCulture)));
        cards.Add(("EXPTIME", parts.Sum(p => p!.Exposure).ToString("0.###", CultureInfo.InvariantCulture)));
        cards.Add(("OOFWEIGH", (mixed ? weight : 0).ToString("0.###", CultureInfo.InvariantCulture)));
        cards.Add(("CREATOR", Q("ELink live stack")));
        return new LiveStackImage
        {
            Ok = true, Width = w, Height = h, Channels = 3, PixelScaleArcsec = wcs.PixelScaleArcsec, Frames = frames, Filter = "pseudo mono",
            Image = new ELink.Contracts.RawBytes { Data = FitsImage.WriteFloat32(w, h, data, cards, channels: 3) },
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
        _saveTimer?.Dispose(); _saveTimer = null;
        await SaveAsync();
        foreach (var (id, hook) in hooks)
            try { _node.UnhookEvent(ShooterIds.Shot(id), hook); } catch (ObjectDisposedException) { }
        try { await worker.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        await Publish();
    }

    // ---- keeping a stack ---------------------------------------------------------------------------------------

    private static string Safe(string key) => new(key.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    private sealed record SavedFilter(string Filter, string File, float[]? Pedestal, double Exposure, double InputScale = 0);
    private sealed record SavedSession(double CenterRaHours, double CenterDecDegrees, double Width, double Height, double PositionAngle, double Scale, double Exposure, List<SavedFilter> Filters);

    private static (Dictionary<string, FilterStack>, double Scale, double Exposure) LoadSession(string dir, LiveStackRequest r)
    {
        var meta = System.Text.Json.JsonSerializer.Deserialize<SavedSession>(File.ReadAllText(Path.Combine(dir, "session.json"))) ?? throw new FormatException("empty");
        // the same field: centre, size and angle, and the scale if one is asked for
        if (Sky.SeparationDegrees(meta.CenterRaHours, meta.CenterDecDegrees, r.Center.RaHours.Value, r.Center.DecDegrees.Value) * 3600 > 1
            || Math.Abs(meta.Width - r.FovWidthDegrees.Value) > 1e-3 * Math.Max(1, meta.Width) || Math.Abs(meta.Height - r.FovHeightDegrees.Value) > 1e-3 * Math.Max(1, meta.Height) || Math.Abs(meta.PositionAngle - r.PositionAngleDegrees.Value) > 1e-6)
            throw new InvalidOperationException("it covers another field");
        if (r.PixelScaleArcsec.Value > 0 && Math.Abs(meta.Scale - r.PixelScaleArcsec.Value) > 1e-9) throw new InvalidOperationException(FormattableString.Invariant($"it was made at {meta.Scale:0.###}\"/px"));
        var stacks = new Dictionary<string, FilterStack>();
        foreach (var f in meta.Filters)
        {
            using var fs = File.OpenRead(Path.Combine(dir, f.File));
            var stack = LiveStacker.ReadFrom(fs);
            stack.Interpolation = Enum.Parse<Interpolation>(r.Interpolation.Text); stack.MatchFlux = r.MatchFlux.Value; stack.RejectSigma = Math.Max(0, r.RejectSigma.Value);
            stacks[f.Filter] = new FilterStack { Stack = stack, Filter = f.Filter, Pedestal = f.Pedestal, Exposure = f.Exposure, Counted = f.Filter != PseudoLuminance, FitsFilter = r.Layers.FirstOrDefault(l => l.Label.Text == f.Filter)?.Filter.Text, InputScale = f.InputScale };
        }
        return (stacks, meta.Scale, meta.Exposure);
    }

    /// <summary>Writes a kept stack (only when it has new frames). Files are written aside and moved in place.</summary>
    private async Task<CommandResult> SaveAsync()
    {
        string? dir; LiveStackRequest? r; List<FilterStack> stacks; double scale, exposure; int frames, saved;
        lock (_gate)
        {
            dir = _sessionDir; r = _request; stacks = _stacks.Values.ToList(); scale = _scale; exposure = _exposure;
            frames = stacks.Sum(x => x.Stack.Frames); saved = _framesSaved;
        }
        if (dir is null || r is null) return CommandResult.Fail("this stack is not kept (no session name)");
        if (frames == saved) return CommandResult.Success();
        await _saving.WaitAsync();
        try
        {
            Directory.CreateDirectory(dir);
            var files = new List<SavedFilter>();
            foreach (var x in stacks)
            {
                string file = "filter-" + (x.Filter == "" ? "all" : Safe(x.Filter)) + ".stack", path = Path.Combine(dir, file);
                await Task.Run(() => { using (var fs = File.Create(path + ".part")) x.Stack.WriteTo(fs); File.Move(path + ".part", path, true); });
                files.Add(new SavedFilter(x.Filter, file, x.Pedestal, x.Exposure, x.InputScale));
            }
            // the linear, unstretched stack is kept as plain FITS beside the stack files: it is what any later processing starts from
            try
            {
                var linear = GetImage(new LiveStackImageRequest { Neutralize = false });
                if (linear.Ok.Value) { string lp = Path.Combine(dir, "linear.fits"); await File.WriteAllBytesAsync(lp + ".part", linear.Image.Data); File.Move(lp + ".part", lp, true); }
            }
            catch (Exception) { }
            var meta = new SavedSession(r.Center.RaHours.Value, r.Center.DecDegrees.Value, r.FovWidthDegrees.Value, r.FovHeightDegrees.Value, r.PositionAngleDegrees.Value, scale, exposure, files);
            string metaPath = Path.Combine(dir, "session.json");
            await File.WriteAllTextAsync(metaPath + ".part", System.Text.Json.JsonSerializer.Serialize(meta));
            File.Move(metaPath + ".part", metaPath, true);
            lock (_gate) _framesSaved = frames;
            return CommandResult.Success();
        }
        catch (Exception ex) { lock (_gate) _message = "could not keep the stack: " + ex.Message; return CommandResult.Fail(ex.Message); }
        finally { _saving.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        try { await UnhookAllAsync("Stopped", ""); } catch (ObjectDisposedException) { }
        _commands.Dispose(); _publisher.Dispose();
    }
}
