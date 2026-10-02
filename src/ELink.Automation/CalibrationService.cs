using System.Globalization;
using System.Threading.Channels;
using ELink.Contracts.Equipment;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Core;
using ELink.Imaging;
using Event.CoreFunctionality;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

/// <summary>The calibration library: takes darks, biases and flats with any camera, combines them into masters (a mean
/// that leaves out each pixel's highest and lowest value: cosmic rays and hot flickers), keeps them as float FITS, and
/// finds the master that suits a light frame (same camera, size and binning; for darks also exposure, gain and
/// temperature, falling back to a bias; for flats the filter).</summary>
public sealed class CalibrationService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly string _dir;
    private readonly CommandSet _commands;
    private readonly StatePublisher<CalibrationState> _publisher;
    private readonly object _gate = new();
    private CalibrationState _state = new();
    private List<CalibrationMaster> _masters = new();
    private CancellationTokenSource? _run;

    public CalibrationService(TypeSafeEVentNode node, string dataDir)
    {
        _node = node; _dir = Path.Combine(dataDir, "calibration");
        _commands = new CommandSet(node);
        _publisher = new(node, CalibrationIds.State, CalibrationIds.GetState, () => { lock (_gate) return Clone(_state); });
        _masters = LoadIndex();
    }

    public async Task StartAsync()
    {
        await _commands.AddAsync<CaptureRequest, CommandResult>(CalibrationIds.Capture, StartCaptureAsync, "take calibration frames and make a master");
        await _commands.AddAsync<NOTESVoid, CommandResult>(CalibrationIds.Abort, _ => { lock (_gate) _run?.Cancel(); return Task.FromResult(CommandResult.Success()); }, "stop capturing");
        await _commands.AddAsync<NOTESVoid, CalibrationMasters>(CalibrationIds.List, _ => Task.FromResult(List()), "the masters in the library");
        await _commands.AddAsync<BinaryConvertibleString, CommandResult>(CalibrationIds.Delete, id => Task.FromResult(Delete(id.Text)), "remove a master");
        await _commands.AddAsync<MasterQuery, MasterMatch>(CalibrationIds.Find, q => Task.FromResult(Find(q)), "the master that suits a light frame");
        await _publisher.StartAsync();
    }

    // ---- the library ------------------------------------------------------------------------------------------

    private string IndexPath => Path.Combine(_dir, "library.bin");

    private List<CalibrationMaster> LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath)) return new();
            Span<byte> b = File.ReadAllBytes(IndexPath);
            var m = new CalibrationMasters(); m.FromBytes(ref b);
            return m.Masters.Where(x => File.Exists(Path.Combine(_dir, x.Id.Text + ".fits"))).ToList();
        }
        catch (Exception) { return new(); }
    }

    private void SaveIndex()
    {
        Directory.CreateDirectory(_dir);
        var m = new CalibrationMasters();
        lock (_gate) foreach (var x in _masters) m.Masters.Add(x);
        File.WriteAllBytes(IndexPath + ".part", m.ToBytes()); File.Move(IndexPath + ".part", IndexPath, true);
    }

    private CalibrationMasters List() { var m = new CalibrationMasters(); lock (_gate) foreach (var x in _masters) m.Masters.Add(x); return m; }

    private CommandResult Delete(string id)
    {
        lock (_gate) if (_masters.RemoveAll(m => m.Id.Text == id) == 0) return CommandResult.Fail($"no master {id}");
        try { File.Delete(Path.Combine(_dir, id + ".fits")); } catch { }
        SaveIndex();
        return CommandResult.Success();
    }

    private static bool SameSetting(double a, double b) => double.IsNaN(a) || double.IsNaN(b) || Math.Abs(a - b) < 1e-6;
    private static bool SameIso(string a, string b) => a == "" || b == "" || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The best master for a light frame, or why there is none.</summary>
    public MasterMatch Find(MasterQuery q)
    {
        List<CalibrationMaster> all; lock (_gate) all = _masters.ToList();
        var mine = all.Where(m => m.ShooterId.Text == q.ShooterId.Text && Math.Max(1, m.BinX.Value) == Math.Max(1, q.BinX.Value)
                                  && (q.Width.Value == 0 || (m.Width.Value == q.Width.Value && m.Height.Value == q.Height.Value))).ToList();
        CalibrationMaster? pick = null;
        if (q.Kind.Text == "Flat")
            pick = mine.Where(m => m.Kind.Text == "Flat" && string.Equals(m.Filter.Text, q.Filter.Text, StringComparison.OrdinalIgnoreCase))
                       .OrderByDescending(m => m.CreatedUtc.Text).FirstOrDefault();
        else
        {
            bool Temp(CalibrationMaster m) => double.IsNaN(m.TemperatureC.Value) || double.IsNaN(q.TemperatureC.Value) || Math.Abs(m.TemperatureC.Value - q.TemperatureC.Value) <= 3;
            double e = q.ExposureSeconds.Value;
            pick = mine.Where(m => m.Kind.Text == "Dark" && SameSetting(m.Gain.Value, q.Gain.Value) && SameIso(m.Iso.Text, q.Iso.Text) && Temp(m)
                                   && Math.Abs(m.ExposureSeconds.Value - e) <= Math.Max(0.5, 0.1 * e))
                       .OrderBy(m => Math.Abs(m.ExposureSeconds.Value - e)).ThenBy(m => double.IsNaN(m.TemperatureC.Value) || double.IsNaN(q.TemperatureC.Value) ? 0 : Math.Abs(m.TemperatureC.Value - q.TemperatureC.Value))
                       .ThenByDescending(m => m.CreatedUtc.Text).FirstOrDefault()
                   ?? mine.Where(m => m.Kind.Text == "Bias" && SameSetting(m.Gain.Value, q.Gain.Value) && SameIso(m.Iso.Text, q.Iso.Text))
                          .OrderByDescending(m => m.CreatedUtc.Text).FirstOrDefault();
        }
        if (pick is null)
            return new MasterMatch { Message = q.Kind.Text == "Flat" ? $"no flat of {q.ShooterId.Text} through '{q.Filter.Text}'" : $"no dark or bias of {q.ShooterId.Text} for {q.ExposureSeconds.Value:0.##} s" };
        return new MasterMatch { Found = true, Master = pick, Image = new ELink.Contracts.RawBytes { Data = File.ReadAllBytes(Path.Combine(_dir, pick.Id.Text + ".fits")) } };
    }

    // ---- capturing ---------------------------------------------------------------------------------------------

    private async Task<CommandResult> StartCaptureAsync(CaptureRequest r)
    {
        if (r.ShooterId.Text == "") return CommandResult.Fail("which camera? (its shooter)");
        if (r.Kind.Text is not ("Dark" or "Bias" or "Flat")) return CommandResult.Fail("Kind is Dark, Bias or Flat");
        if (r.Count.Value < 3) return CommandResult.Fail("at least 3 frames (the highest and lowest of each pixel are left out)");
        if (r.Kind.Text == "Dark" && !(r.ExposureSeconds.Value > 0)) return CommandResult.Fail("darks need the exposure of the lights they are for");
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_run is not null) return CommandResult.Fail("already capturing");
            cts = _run = new CancellationTokenSource();
            _state = new CalibrationState { Phase = "Capturing", Count = r.Count.Value, Message = $"{r.Kind.Text}s with {r.ShooterId.Text}" };
        }
        await Publish();
        _ = Task.Run(() => CaptureAsync(r, cts));
        return CommandResult.Success();
    }

    private async Task Set(Action<CalibrationState> change) { lock (_gate) change(_state); await Publish(); }
    private async Task Publish() { try { await _publisher.PublishAsync(); } catch (ObjectDisposedException) { } }

    private async Task CaptureAsync(CaptureRequest r, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        string shooter = r.ShooterId.Text, kind = r.Kind.Text;
        var frames = Channel.CreateUnbounded<ShotEvent>();
        Action<ShotEvent> onShot = s => frames.Writer.TryWrite(s);
        await _node.HookEventAsync(ShooterIds.Shot(shooter), onShot, "calibration frames");
        try
        {
            async Task<FitsImage> Take(double seconds)
            {
                while (frames.Reader.TryRead(out _)) { }
                var e = await Commands.CallAsync(_node, ShooterIds.Expose(shooter), new ShooterExposure
                {
                    Seconds = seconds, FrameType = kind, Filter = kind == "Flat" ? r.Filter.Text : "", Gain = r.Gain.Value, Iso = r.Iso.Text, BinX = r.BinX.Value, BinY = r.BinX.Value,
                });
                if (!e.Ok.Value) throw new InvalidOperationException(e.Error.Text);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(seconds + 120));
                var shot = await frames.Reader.ReadAsync(wait.Token);
                if (!shot.Format.Text.StartsWith(".fit", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"calibration needs FITS frames, got {shot.Format.Text}");
                return FitsImage.Parse(shot.Data.Data);
            }

            double exposure = kind == "Bias" ? 0.001 : r.ExposureSeconds.Value;
            float[]? bias = null;
            if (kind == "Flat")
            {
                // a flat must sit well inside the camera's range: find the exposure that puts it at the target level
                if (!(exposure > 0))
                {
                    await Set(s => s.Phase = "FindingExposure");
                    exposure = 1;
                    for (int i = 0; i < 6; i++)
                    {
                        var test = await Take(exposure);
                        double level = Median(test.Data) / test.Range;
                        await Set(s => { s.ExposureSeconds = exposure; s.Message = FormattableString.Invariant($"{exposure:0.###} s gives {level * 100:0}% of the range"); });
                        if (Math.Abs(level - r.FlatTargetFraction.Value) < 0.1 * r.FlatTargetFraction.Value) break;
                        exposure = Math.Clamp(exposure * r.FlatTargetFraction.Value / Math.Max(level, 0.002), 0.001, 120);
                    }
                }
                // flats are corrected for the bias before they are combined (when one is known)
                var b = Find(new MasterQuery { ShooterId = shooter, Kind = "Dark", ExposureSeconds = exposure, Gain = r.Gain.Value, Iso = r.Iso.Text, BinX = Math.Max(1, r.BinX.Value) });
                if (b.Found) bias = FitsImage.Parse(b.Image.Data).Data;
            }

            // combine as they come: sum, min and max per pixel; the mean without each pixel's extremes
            await Set(s => { s.Phase = "Capturing"; s.ExposureSeconds = exposure; s.Done = 0; });
            float[]? sum = null, min = null, max = null;
            int w = 0, h = 0; string bayer = "";
            var temps = new List<double>();
            double headerGain = double.NaN; string headerIso = ""; int headerBin = 1;
            for (int n = 0; n < r.Count.Value; n++)
            {
                var img = await Take(exposure);
                if (img.GetDouble("CCD-TEMP") is var t && !double.IsNaN(t)) temps.Add(t);
                if (sum is null)
                {
                    headerGain = img.GetDouble("GAIN"); headerIso = (img.Get("ISOSPEED") ?? "").Trim('\'', ' ');
                    headerBin = Math.Max(1, (int)img.GetDouble("XBINNING", 1));
                }
                if (sum is null) { w = img.Width; h = img.Height; bayer = img.Get("BAYERPAT") ?? ""; sum = new float[w * h]; min = new float[w * h]; max = new float[w * h]; Array.Fill(min, float.MaxValue); Array.Fill(max, float.MinValue); }
                if (img.Width != w || img.Height != h) throw new InvalidOperationException("the frames changed size");
                var d = img.Data;
                float scale = 1;
                if (kind == "Flat")
                {
                    // each flat to a median of 1 (the light may drift a little from frame to frame)
                    if (bias is not null && bias.Length >= w * h) { d = (float[])d.Clone(); for (int i = 0; i < w * h; i++) d[i] -= bias[i]; }
                    float med = (float)Median(d);
                    if (med <= 0) throw new InvalidOperationException("a flat came out dark: is the light on?");
                    scale = 1 / med;
                }
                for (int i = 0; i < w * h; i++)
                {
                    float v = d[i] * scale;
                    sum[i] += v;
                    if (v < min![i]) min[i] = v;
                    if (v > max![i]) max[i] = v;
                }
                int done = n + 1;
                await Set(s => s.Done = done);
            }
            await Set(s => s.Phase = "Combining");
            int count = r.Count.Value;
            var master = new float[w * h];
            for (int i = 0; i < master.Length; i++) master[i] = (sum![i] - min![i] - max![i]) / (count - 2);
            if (kind == "Flat")
            {
                float med = (float)Median(master);
                for (int i = 0; i < master.Length; i++) master[i] /= med;
            }

            double temp = temps.Count > 0 ? Math.Round(temps.Average(), 1) : double.NaN;
            double gain = !double.IsNaN(r.Gain.Value) ? r.Gain.Value : headerGain;
            string iso = r.Iso.Text != "" ? r.Iso.Text : headerIso;
            int bin = r.BinX.Value > 0 ? r.BinX.Value : headerBin;
            string id = $"{kind.ToLowerInvariant()}-{EquipmentId(shooter)}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            var info = new CalibrationMaster
            {
                Id = id, ShooterId = shooter, Kind = kind, ExposureSeconds = exposure, Gain = gain, Iso = iso, TemperatureC = temp,
                BinX = bin, Filter = kind == "Flat" ? r.Filter.Text : "", Frames = count, Width = w, Height = h, CreatedUtc = DateTime.UtcNow.ToString("o"),
            };
            var cards = new List<(string, string)>
            {
                ("IMAGETYP", $"'Master {kind}'"), ("EXPTIME", exposure.ToString("0.######", CultureInfo.InvariantCulture)), ("NCOMBINE", count.ToString()),
                ("ELSHOOTR", $"'{shooter}'"),
            };
            if (bayer != "") cards.Add(("BAYERPAT", $"'{bayer}'"));
            if (info.Filter.Text != "") cards.Add(("FILTER", $"'{info.Filter.Text}'"));
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, id + ".fits");
            await File.WriteAllBytesAsync(path + ".part", FitsImage.WriteFloat32(w, h, master, cards), ct);
            File.Move(path + ".part", path, true);
            lock (_gate) _masters.Add(info);
            SaveIndex();
            await Set(s => { s.Phase = "Done"; s.Message = $"master {kind.ToLowerInvariant()} of {count} frames: {id}"; });
        }
        catch (OperationCanceledException) { await Set(s => { s.Phase = "Aborted"; s.Message = "aborted"; }); }
        catch (Exception ex) { await Set(s => { s.Phase = "Error"; s.Message = ex.Message; }); }
        finally
        {
            try { _node.UnhookEvent(ShooterIds.Shot(shooter), onShot); } catch (ObjectDisposedException) { }
            lock (_gate) _run = null;
            cts.Dispose();
        }
    }

    private static string EquipmentId(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    private static double Median(float[] data)
    {
        int step = Math.Max(1, data.Length / 100000);
        var sample = new List<float>(data.Length / step + 1);
        for (int i = 0; i < data.Length; i += step) sample.Add(data[i]);
        sample.Sort();
        return sample.Count == 0 ? 0 : sample[sample.Count / 2];
    }

    private static CalibrationState Clone(CalibrationState s) => new() { Phase = s.Phase.Text, Message = s.Message.Text, Done = s.Done.Value, Count = s.Count.Value, ExposureSeconds = s.ExposureSeconds.Value };

    public ValueTask DisposeAsync()
    {
        lock (_gate) _run?.Cancel();
        _commands.Dispose(); _publisher.Dispose();
        return ValueTask.CompletedTask;
    }
}
