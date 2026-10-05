using System.Globalization;
using System.Text;
using System.Text.Json;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Imaging;
using Event.CoreFunctionality;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

/// <summary>Saves the frames of the shooters it watches: one folder per night, descriptive file names, FITS headers
/// stamped with what is known (object and plan from the observation that took the frame, pointing, exposure, filter), and a JSON-lines
/// session log next to the frames. A shooter is only an EVent ID to it.</summary>
public sealed class StorageService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly CommandSet _commands;
    private readonly StatePublisher<StorageState> _publisher;
    private readonly object _gate = new();
    private readonly Dictionary<string, Action<ShotEvent>> _watching = new();
    private readonly Dictionary<string, int> _counters = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string _directory;
    private int _saved;
    private string _lastFile = "", _message = "";

    /// <summary>The night changes at local noon, so a whole night lands in one folder.</summary>
    public Func<DateTime> LocalNow { get; set; } = () => DateTime.Now;

    public StorageService(TypeSafeEVentNode node, string directory)
    {
        _node = node; _directory = directory;
        _commands = new CommandSet(node);
        _publisher = new(node, StorageIds.State, StorageIds.GetState, BuildState);
    }

    public int FramesSaved { get { lock (_gate) return _saved; } }

    public async Task StartAsync()
    {
        await _commands.AddAsync<BinaryConvertibleString, CommandResult>(StorageIds.SetDirectory, SetDirectoryAsync, "where frames are saved");
        await _commands.AddAsync<StorageWatch, CommandResult>(StorageIds.Watch, WatchAsync, "start or stop saving the frames of a shooter");
        await _publisher.StartAsync();
    }

    private StorageState BuildState()
    {
        lock (_gate)
        {
            var s = new StorageState { Directory = _directory, FramesSaved = _saved, LastFile = _lastFile, Message = _message };
            foreach (var w in _watching.Keys.Order()) s.Watching.Add(w);
            return s;
        }
    }

    private async Task<CommandResult> SetDirectoryAsync(BinaryConvertibleString dir)
    {
        string path = dir.Text.Trim();
        if (path == "") return CommandResult.Fail("a directory is required");
        try { Directory.CreateDirectory(path); }
        catch (Exception ex) { return CommandResult.Fail($"cannot use {path}: {ex.Message}"); }
        lock (_gate) { _directory = path; _counters.Clear(); }
        await _publisher.PublishAsync();
        return CommandResult.Success();
    }

    private async Task<CommandResult> WatchAsync(StorageWatch w)
    {
        string id = w.ShooterId.Text;
        if (id == "") return CommandResult.Fail("ShooterId is required");
        if (w.Enabled.Value)
        {
            lock (_gate) if (_watching.ContainsKey(id)) return CommandResult.Success();
            Action<ShotEvent> handler = shot => SaveShot(id, shot);
            await _node.HookEventAsync(ShooterIds.Shot(id), handler, "frame storage");
            lock (_gate) _watching[id] = handler;
        }
        else
        {
            Action<ShotEvent>? handler;
            lock (_gate) _watching.Remove(id, out handler);
            if (handler is not null) _node.UnhookEvent(ShooterIds.Shot(id), handler);
        }
        await _publisher.PublishAsync();
        return CommandResult.Success();
    }

    private void SaveShot(string watchId, ShotEvent shot)
    {
        _writeLock.Wait();
        try
        {
            string dir; lock (_gate) dir = _directory;
            var now = LocalNow();
            string night = now.AddHours(-12).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string nightDir = Path.Combine(dir, night);
            bool rejected = shot.Quality.Text == "Rejected";
            if (rejected) nightDir = Path.Combine(nightDir, "rejected");   // kept, but out of the way of stacking software
            Directory.CreateDirectory(nightDir);

            string obj = shot.ObjectName.Text, planId = shot.PlanId.Text;   // stamped by the scope that ran the observation

            string frame = string.IsNullOrWhiteSpace(shot.FrameType.Text) ? "Light" : shot.FrameType.Text;
            string filter = shot.Filter.Text;
            string ext = shot.Format.Text.StartsWith('.') ? shot.Format.Text : "." + shot.Format.Text;
            if (ext == ".") ext = ".dat";
            string stem = Sanitize(string.Join("_", new[] { obj, frame, filter }.Where(p => p != "")) +
                                   "_" + shot.ExposureSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture) + "s");
            string key = Path.Combine(nightDir, stem);
            if (!_counters.TryGetValue(key, out int n)) n = Directory.GetFiles(nightDir, stem + "_*").Length;
            string file;
            do { n++; file = Path.Combine(nightDir, $"{stem}_{n:0000}{ext}"); } while (File.Exists(file));
            _counters[key] = n;

            byte[] bytes = shot.Data.Data;
            if (shot.PseudoChannel.Text != "" && ext is ".fits" or ".fit" && SplitPseudo(shot, nightDir, stem, ext, obj, planId) is { } pair)
            {
                SavePair(watchId, shot, dir, nightDir, pair, obj, planId, frame);
                return;
            }
            if (ext is ".fits" or ".fit")
            {
                var cards = new Dictionary<string, string>
                {
                    ["SWCREATE"] = FitsHeader.StringCard("SWCREATE", "ELink", "capture software"),
                    ["ELINKSRC"] = FitsHeader.StringCard("ELINKSRC", shot.Shooter.Text, "shooter that took the frame"),
                    ["EXPTIME"] = FitsHeader.NumberCard("EXPTIME", shot.ExposureSeconds.Value, "exposure time in seconds"),
                };
                if (obj != "") cards["OBJECT"] = FitsHeader.StringCard("OBJECT", obj);
                if (planId != "") cards["ELINKPLN"] = FitsHeader.StringCard("ELINKPLN", planId, "sequence plan");
                if (filter != "") cards["FILTER"] = FitsHeader.StringCard("FILTER", filter);
                if (!double.IsNaN(shot.PointingRaHours.Value)) cards["RA"] = FitsHeader.NumberCard("RA", shot.PointingRaHours.Value * 15, "pointing, degrees, J2000");
                if (!double.IsNaN(shot.PointingDecDegrees.Value)) cards["DEC"] = FitsHeader.NumberCard("DEC", shot.PointingDecDegrees.Value, "pointing, degrees, J2000");
                if (shot.Quality.Text != "") cards["ELQUAL"] = FitsHeader.StringCard("ELQUAL", shot.Quality.Text, shot.QualityNote.Text == "" ? "frame grade" : shot.QualityNote.Text);
                if (shot.Stars.Value >= 0) cards["ELSTARS"] = FitsHeader.NumberCard("ELSTARS", shot.Stars.Value, "stars found");
                if (!double.IsNaN(shot.Hfr.Value)) cards["ELHFR"] = FitsHeader.NumberCard("ELHFR", shot.Hfr.Value, "median half-flux radius, px");
                try { bytes = FitsHeader.Set(bytes, cards); }
                catch (FormatException) { /* not a parsable FITS: store as received */ }
            }
            string tmp = file + ".part";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, file);

            var log = new
            {
                time = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), file = Path.GetRelativePath(dir, file), shooter = shot.Shooter.Text,
                watched = watchId, frame, filter, exposure = shot.ExposureSeconds.Value, @object = obj, plan = planId,
                raHours = Finite(shot.PointingRaHours.Value), decDegrees = Finite(shot.PointingDecDegrees.Value), bytes = bytes.Length,
                quality = shot.Quality.Text, qualityNote = shot.QualityNote.Text, stars = shot.Stars.Value, hfr = Finite(shot.Hfr.Value),
            };
            File.AppendAllText(Path.Combine(nightDir, "session.jsonl"), JsonSerializer.Serialize(log) + "\n", Encoding.UTF8);

            lock (_gate) { _saved++; _lastFile = file; _message = ""; }
            _ = Announce(file, watchId, obj, frame, filter, shot.ExposureSeconds.Value, bytes.Length);
        }
        catch (Exception ex)
        {
            lock (_gate) _message = ex.Message;
            Console.Error.WriteLine($"[storage] cannot save a frame: {ex.Message}");
            _ = _publisher.PublishAsync();
        }
        finally { _writeLock.Release(); }
    }

    /// <summary>Pseudo mono frames are saved as two mono frames: the channel that was in focus (folder <c>infocus</c>) and the mean of the
    /// other two (folder <c>oof</c>), each super-pixel debayered, so no channel is ever interpolated from another.</summary>
    private static (byte[] InFocus, byte[] OutOfFocus)? SplitPseudo(ShotEvent shot, string nightDir, string stem, string ext, string obj, string planId)
    {
        try
        {
            var img = FitsImage.Parse(shot.Data.Data);
            string? pattern = Debayer.PatternOf(img);
            if (img.Channels != 1 || pattern is null) return null;
            var c = Debayer.SuperPixel(img.Data, img.Width, img.Height, pattern);
            int channel = "RGB".IndexOf(shot.PseudoChannel.Text, StringComparison.Ordinal);
            if (channel < 0) return null;
            int plane = c.Width * c.Height;
            var sharp = c.Data.AsSpan(channel * plane, plane).ToArray();
            var soft = new float[plane];
            for (int k = 0; k < 3; k++)
                if (k != channel) for (int i = 0; i < plane; i++) soft[i] += c.Data[k * plane + i] / 2;
            IEnumerable<(string, string)> Cards(string role) => new[]
            {
                ("SWCREATE", "'ELink'"), ("EXPTIME", shot.ExposureSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture)),
                ("PSEUDOCH", "'" + shot.PseudoChannel.Text + "'"), ("PSEUDORL", "'" + role + "'"),
            }.Concat(obj != "" ? [("OBJECT", "'" + obj.Replace("'", "''") + "'")] : []).Concat(planId != "" ? [("ELINKPLN", "'" + planId.Replace("'", "''") + "'")] : []);
            return (FitsImage.WriteFloat32(c.Width, c.Height, sharp, Cards("in focus")), FitsImage.WriteFloat32(c.Width, c.Height, soft, Cards("out of focus")));
        }
        catch (FormatException) { return null; }
    }

    private void SavePair(string watchId, ShotEvent shot, string root, string nightDir, (byte[] InFocus, byte[] OutOfFocus) pair, string obj, string planId, string frame)
    {
        string channel = shot.PseudoChannel.Text;
        string stem = Sanitize(string.Join("_", new[] { obj, frame, channel }.Where(p => p != "")) + "_" + shot.ExposureSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture) + "s");
        string? first = null;
        foreach (var (folder, bytes) in new[] { ("infocus", pair.InFocus), ("oof", pair.OutOfFocus) })
        {
            string dir = Path.Combine(nightDir, folder);
            Directory.CreateDirectory(dir);
            string key = Path.Combine(dir, stem);
            if (!_counters.TryGetValue(key, out int n)) n = Directory.GetFiles(dir, stem + "_*").Length;
            string file;
            do { n++; file = Path.Combine(dir, $"{stem}_{n:0000}.fits"); } while (File.Exists(file));
            _counters[key] = n;
            File.WriteAllBytes(file + ".part", bytes);
            File.Move(file + ".part", file);
            first ??= file;
            var log = new
            {
                time = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), file = Path.GetRelativePath(root, file), shooter = shot.Shooter.Text, watched = watchId,
                frame, filter = channel, pseudo = folder, exposure = shot.ExposureSeconds.Value, @object = obj, plan = planId, bytes = bytes.Length,
                raHours = Finite(shot.PointingRaHours.Value), decDegrees = Finite(shot.PointingDecDegrees.Value),
            };
            File.AppendAllText(Path.Combine(nightDir, "session.jsonl"), JsonSerializer.Serialize(log) + "\n", Encoding.UTF8);
        }
        lock (_gate) { _saved++; _lastFile = first!; _message = ""; }
        _ = Announce(first!, watchId, obj, frame, channel, shot.ExposureSeconds.Value, pair.InFocus.Length + pair.OutOfFocus.Length);
    }

    private async Task Announce(string file, string shooter, string obj, string frame, string filter, double seconds, int bytes)
    {
        try
        {
            await _publisher.PublishAsync();
            await _node.FireEventAsync(StorageIds.Saved, new FrameSaved
            { Path = file, ShooterId = shooter, ObjectName = obj, FrameType = frame, Filter = filter, ExposureSeconds = seconds, Bytes = bytes });
        }
        catch (ObjectDisposedException) { }
    }

    private static double? Finite(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : v;

    private static string Sanitize(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.ToString().Trim('_', '.');
    }

    public async ValueTask DisposeAsync()
    {
        List<(string id, Action<ShotEvent> h)> hooks;
        lock (_gate) { hooks = _watching.Select(w => (w.Key, w.Value)).ToList(); _watching.Clear(); }
        foreach (var (id, h) in hooks) { try { _node.UnhookEvent(ShooterIds.Shot(id), h); } catch (ObjectDisposedException) { } }
        _commands.Dispose(); _publisher.Dispose();
        await Task.CompletedTask;
    }
}
