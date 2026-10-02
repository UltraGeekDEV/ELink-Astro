using System.Threading.Channels;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Core;
using Event.CoreFunctionality;

namespace ELink.Automation;

/// <summary>Plate solving on the mesh: solve a FITS that is sent, or take a shot with any Shooter and solve that. With
/// more than one solver installed (ASTAP, astrometry.net), it tries ASTAP first when it has a hint of where to look
/// (that is what ASTAP is quick at) and astrometry.net first for blind solves, and the other when the first fails.</summary>
public sealed class PlateSolveService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly IReadOnlyList<IPlateSolver> _solvers;
    private readonly CommandSet _commands;
    private readonly SemaphoreSlim _one = new(1, 1);

    public PlateSolveService(TypeSafeEVentNode node, params IPlateSolver[] solvers)
    {
        if (solvers.Length == 0) throw new ArgumentException("no plate solver");
        _node = node; _solvers = solvers;
        _commands = new CommandSet(node);
    }

    public async Task StartAsync()
    {
        await _commands.AddAsync<SolveRequest, SolveResult>(SolveIds.Solve, SolveAsync,
            "plate solve: a FITS image, or a fresh shot from a Shooter; returns the J2000 centre, rotation and scale");
        await _commands.AddAsync<Event.Connections.Models.BaseBinaryConvertibles.NOTESVoid, EVent.Connections.Models.BaseBinaryConvertibles.BinaryConvertibleString>(SolveIds.Solvers,
            _ => Task.FromResult((EVent.Connections.Models.BaseBinaryConvertibles.BinaryConvertibleString)string.Join(",", _solvers.Select(x => x.Name))), "the installed plate solvers");
    }

    /// <summary>The solvers to try, in order.</summary>
    private List<IPlateSolver> Order(SolveRequest r)
    {
        if (r.Solver.Text.Trim() != "")
            return _solvers.Where(x => string.Equals(x.Name, r.Solver.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        bool hinted = !double.IsNaN(r.HintRaHours.Value) && !double.IsNaN(r.HintDecDegrees.Value);
        return _solvers.OrderBy(x => (x.Name == "ASTAP") == hinted ? 0 : 1).ToList();
    }

    public async Task<SolveResult> SolveAsync(SolveRequest r)
    {
        var result = new SolveResult { ShooterId = r.ShooterId.Text };
        byte[] image;
        if (r.ShooterId.Text != "")
        {
            try { image = await ShootAsync(r.ShooterId.Text, r.ExposureSeconds.Value); }
            catch (Exception ex) { result.Message = ex.Message; await Announce(result); return result; }
        }
        else image = r.Image.Data;
        if (image.Length == 0) { result.Message = "no image"; return result; }

        await _one.WaitAsync();   // one solve at a time: it is CPU-bound
        try
        {
            var order = Order(r);
            if (order.Count == 0) throw new InvalidOperationException($"no solver {r.Solver.Text} (installed: {string.Join(", ", _solvers.Select(x => x.Name))})");
            SolveOutcome o = null!;
            var failures = new List<string>();
            double lo = r.ScaleLowArcsecPerPixel.Value, hi = r.ScaleHighArcsecPerPixel.Value;
            bool scaled = lo > 0 && hi > lo;
            // a wrong scale hint (a pixel size typed in for a camera that knows better) must not stop solving: without it next
            foreach (bool withScale in scaled ? [true, false] : new[] { false })
            {
                foreach (var solver in order)
                {
                    o = await solver.SolveAsync(image, r.HintRaHours.Value, r.HintDecDegrees.Value, r.HintRadiusDegrees.Value,
                        withScale ? lo : 0, withScale ? hi : 0, TimeSpan.FromSeconds(Math.Clamp(r.TimeoutSeconds.Value, 5, 600)));
                    result.Solver = solver.Name;
                    if (o.Solved) break;
                    failures.Add($"{solver.Name}{(scaled && !withScale ? " (no scale hint)" : "")}: {o.Message}");
                }
                if (o.Solved)
                {
                    if (!withScale)
                        Console.Error.WriteLine(FormattableString.Invariant($"[solve] solved at {o.PixelScale:0.###}\"/px only without the scale hint {lo:0.###}-{hi:0.###}\"/px: check the train's focal length and pixel size"));
                    break;
                }
            }
            if (!o.Solved && failures.Count > 1) o = o with { Message = string.Join("; ", failures) };
            result.Solved = o.Solved; result.RaHours = o.RaHours; result.DecDegrees = o.DecDegrees; result.PositionAngle = o.PositionAngle;
            result.PixelScale = o.PixelScale; result.FieldWidthDegrees = o.FieldWidthDegrees; result.FieldHeightDegrees = o.FieldHeightDegrees;
            result.Seconds = o.Seconds; result.Message = o.Message;
            if (o.Wcs is { } w)
            {
                result.HasWcs = true; result.WcsCrVal1 = w.CrVal1; result.WcsCrVal2 = w.CrVal2;
                result.WcsCrPix1 = w.CrPix1 + 1; result.WcsCrPix2 = w.CrPix2 + 1;
                result.WcsCd11 = w.Cd11; result.WcsCd12 = w.Cd12; result.WcsCd21 = w.Cd21; result.WcsCd22 = w.Cd22;
            }
        }
        catch (Exception ex) { result.Message = "solver failed: " + ex.Message; }
        finally { _one.Release(); }
        await Announce(result);
        return result;
    }

    private async Task Announce(SolveResult r) { try { await _node.FireEventAsync(SolveIds.Solved, r); } catch (ObjectDisposedException) { } }

    /// <summary>One exposure from a Shooter: hook its frames, expose, take the first frame that arrives.</summary>
    private async Task<byte[]> ShootAsync(string shooter, double seconds)
    {
        var frames = Channel.CreateUnbounded<ShotEvent>();
        Action<ShotEvent> hook = s => frames.Writer.TryWrite(s);
        await _node.HookEventAsync(ShooterIds.Shot(shooter), hook, "plate solve frames");
        try
        {
            var r = await Commands.CallAsync(_node, ShooterIds.Expose(shooter), new ShooterExposure { Seconds = seconds, FrameType = "Light" });
            if (!r.Ok.Value) throw new InvalidOperationException(r.Error.Text);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 90));
            var shot = await frames.Reader.ReadAsync(cts.Token);
            if (!shot.Format.Text.StartsWith(".fit", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"plate solving needs FITS, the shooter delivered {shot.Format.Text}");
            return shot.Data.Data;
        }
        catch (OperationCanceledException) { throw new TimeoutException($"no frame from {shooter}"); }
        finally { try { _node.UnhookEvent(ShooterIds.Shot(shooter), hook); } catch (ObjectDisposedException) { } }
    }

    public ValueTask DisposeAsync() { _commands.Dispose(); return ValueTask.CompletedTask; }
}
