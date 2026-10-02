using System.Threading.Channels;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Core;
using Event.CoreFunctionality;

namespace ELink.Automation;

/// <summary>Plate solving on the mesh: solve a FITS that is sent, or take a shot with any Shooter and solve that.</summary>
public sealed class PlateSolveService : IAsyncDisposable
{
    private readonly TypeSafeEVentNode _node;
    private readonly PlateSolver _solver;
    private readonly CommandSet _commands;
    private readonly SemaphoreSlim _one = new(1, 1);

    public PlateSolveService(TypeSafeEVentNode node, PlateSolver solver)
    {
        _node = node; _solver = solver;
        _commands = new CommandSet(node);
    }

    public Task StartAsync() => _commands.AddAsync<SolveRequest, SolveResult>(SolveIds.Solve, SolveAsync,
        "plate solve: a FITS image, or a fresh shot from a Shooter; returns the J2000 centre, rotation and scale");

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
            var o = await _solver.SolveAsync(image, r.HintRaHours.Value, r.HintDecDegrees.Value, r.HintRadiusDegrees.Value,
                r.ScaleLowArcsecPerPixel.Value, r.ScaleHighArcsecPerPixel.Value, TimeSpan.FromSeconds(Math.Clamp(r.TimeoutSeconds.Value, 5, 600)));
            result.Solved = o.Solved; result.RaHours = o.RaHours; result.DecDegrees = o.DecDegrees; result.PositionAngle = o.PositionAngle;
            result.PixelScale = o.PixelScale; result.FieldWidthDegrees = o.FieldWidthDegrees; result.FieldHeightDegrees = o.FieldHeightDegrees;
            result.Seconds = o.Seconds; result.Message = o.Message;
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
