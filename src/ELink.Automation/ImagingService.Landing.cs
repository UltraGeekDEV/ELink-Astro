using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Core;
using ELink.Core.Astro;
using Event.Connections.Models.BaseBinaryConvertibles;
using EVent.Connections.Models.BaseBinaryConvertibles;

namespace ELink.Automation;

public sealed partial class ImagingService
{
    /// <summary>Where one scope's shots really land. Each good frame is plate solved in the background while the next one is exposing
    /// (one at a time; a frame that comes while a solve is still running is not solved, there will be another), at a low priority and
    /// binned, so capture never waits for it. A solved frame moves its paint on the map from where it was meant to go to where it
    /// went, so anything it missed is filled in later; and a steady pointing error (the median of the last few) is aimed off.
    /// There is no re-centring: short hops are trusted to be roughly right, and the map takes care of the rest.</summary>
    private sealed class Landing(ImagingService service, ImagingRequest request, Plan plan, string scope, ScopeFrames frames)
    {
        private Task _running = Task.CompletedTask;
        private bool? _haveSolver;
        private readonly List<(double X, double Y)> _offsets = new();
        private (double X, double Y) _aim;
        private int _solved, _failed, _skipped;
        private DateTime _lastNote = DateTime.MinValue;

        /// <summary>How far to aim off (area degrees): where the mount lands relative to where it is sent.</summary>
        public (double X, double Y) AimOff { get { lock (_offsets) return _aim; } }

        public void Solve(List<ShotEvent> shots, Pose at, Pose sent, CoveragePlanner planner, double exposure)
        {
            if (_haveSolver == false) return;
            // only a frame of a camera at the scope's centre says where the scope itself went
            if (frames.Frames.Length == 0 || frames.Frames[0].OffsetEast != 0 || frames.Frames[0].OffsetNorth != 0) return;
            var shot = shots.FirstOrDefault(f => f.Quality.Text != "Rejected" && f.Format.Text.StartsWith(".fit", StringComparison.OrdinalIgnoreCase));
            if (shot is null) return;
            if (!_running.IsCompleted) { _skipped++; return; }
            _running = Task.Run(() => RunAsync(shot, at, sent, planner, exposure));
        }

        public async Task DrainAsync() { try { await _running.WaitAsync(TimeSpan.FromSeconds(120)); } catch (Exception) { } }

        private async Task RunAsync(ShotEvent shot, Pose at, Pose sent, CoveragePlanner planner, double exposure)
        {
            var node = service._node;
            try
            {
                if (_haveSolver is null)
                {
                    var names = await node.CallFunctionAsync<NOTESVoid, BinaryConvertibleString>(SolveIds.Solvers, NOTESVoid.Void, TimeSpan.FromSeconds(10));
                    _haveSolver = names is { Count: > 0 } && names.Any(n => n.Text != "");
                    if (_haveSolver == false) { SessionLog.Info(node, scope, "no plate solver on the mesh: frames are mapped where they were sent"); return; }
                }
                double cra = request.Center.RaHours.Value, cdec = request.Center.DecDegrees.Value, pa = request.PositionAngleDegrees.Value;
                var (ra, dec) = PlanProjection.ToSky(cra, cdec, pa, sent.X, sent.Y);
                double scale = shot.PixelScaleArcsec.Value;
                var answers = await node.CallFunctionAsync<SolveRequest, SolveResult>(SolveIds.Solve, new SolveRequest
                {
                    Image = shot.Data, HintRaHours = ra, HintDecDegrees = dec, HintRadiusDegrees = 3,
                    ScaleLowArcsecPerPixel = scale > 0 ? scale * 0.85 : 0, ScaleHighArcsecPerPixel = scale > 0 ? scale * 1.15 : 0, TimeoutSeconds = 90,
                }, TimeSpan.FromSeconds(150));
                var s = answers?.FirstOrDefault();
                if (s is not { Solved.Value: true } || !PlanProjection.TryFromSky(cra, cdec, pa, s.RaHours.Value, s.DecDegrees.Value, out double x, out double y))
                {
                    _failed++;
                    if (_failed == 1 || _failed % 10 == 0) SessionLog.Info(node, scope, $"a frame did not solve ({s?.Message.Text ?? "no answer"}): it stays where it was sent ({_failed} so far)");
                    return;
                }
                _solved++;
                var landed = at with { X = x, Y = y };
                double ox = x - sent.X, oy = y - sent.Y, off = Math.Sqrt(ox * ox + oy * oy);
                double frame = 2 * Math.Min(frames.Frames[0].HalfWidth, frames.Frames[0].HalfHeight);
                if (off > 2 * frame)
                {
                    SessionLog.Warn(node, scope, FormattableString.Invariant($"a frame solved {off * 60:0}' from where it was sent: not believed (a wrong solve, or the mount is lost)"));
                    return;
                }
                lock (service._gate)
                {
                    // the paint moves from where the shot was meant to land to where it did
                    var was = planner.PaintFootprints(at); var real = planner.PaintFootprints(landed);
                    plan.Map.Paint(was, -exposure); plan.Map.Paint(real, exposure);
                    plan.Actual.Paint(was, -exposure); plan.Actual.Paint(real, exposure);
                }
                (double X, double Y) aim;
                lock (_offsets)
                {
                    _offsets.Add((ox, oy)); if (_offsets.Count > 5) _offsets.RemoveAt(0);
                    static double Median(IEnumerable<double> v) { var a = v.Order().ToArray(); return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }
                    _aim = _offsets.Count >= 2 ? (Median(_offsets.Select(o => o.X)), Median(_offsets.Select(o => o.Y))) : _aim;
                    aim = _aim;
                }
                if (DateTime.UtcNow - _lastNote > TimeSpan.FromMinutes(15) || _solved == 1)
                {
                    _lastNote = DateTime.UtcNow;
                    SessionLog.Info(node, scope, FormattableString.Invariant(
                        $"frames land {off * 60:0.0}' from where they are sent (aiming off {Math.Sqrt(aim.X * aim.X + aim.Y * aim.Y) * 60:0.0}'); {_solved} solved, {_failed} unsolved, {_skipped} not tried (a solve was still running)"));
                }
            }
            catch (Exception ex) { _failed++; Console.Error.WriteLine($"[imaging] landing solve: {ex.Message}"); }
        }
    }
}
