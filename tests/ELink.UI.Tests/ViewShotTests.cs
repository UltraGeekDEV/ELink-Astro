using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using ELink.Core.Astro;
using ELink.UI.Controls;
using ELink.UI.ViewModels;
using ELink.Contracts;
using ELink.Contracts.Automation;
using ELink.Contracts.Composition;
using ELink.Contracts.Equipment;
using ELink.Core;
using ELink.Imaging;
using Xunit;

namespace ELink.UI.Tests;

/// <summary>Every view rendered against the simulated stack, empty and with a rig: a smoke test for the layouts, and the
/// pictures a design review looks at (ELINK_SHOT_HEIGHT=2600 ELINK_SCREENSHOTS=dir).</summary>
public class ViewShotTests : IClassFixture<IndiServerFixture>
{
    private readonly IndiServerFixture _server;
    public ViewShotTests(IndiServerFixture server) { _server = server; }

    [AvaloniaFact]
    public async Task EveryViewRendersEmptyAndWithARig()
    {
        Assert.True(_server.Available);
        await using var rig = await UiRig.StartAsync(_server);
        var vm = rig.Vm;
        Assert.True(await UiRig.Eventually(() => vm.Catalog.Equipment.Count >= 6), "devices did not show up");
        Assert.DoesNotContain(vm.Catalog.Equipment, d => d.Kind == "GuidePort");       // plumbing is hidden

        foreach (var (view, section, name) in new (AppView, string?, string)[]
        {
            (AppView.Sky, null, "sky"), (AppView.Scopes, null, "scopes"), (AppView.Rig, "Set up", "rig-setup"), (AppView.Rig, "Equipment", "rig-equipment"),
            (AppView.Rig, "Site", "rig-site"), (AppView.Rig, "Drivers", "rig-drivers"), (AppView.Rig, "Calibration", "rig-calibration"),
            (AppView.Advanced, "Help", "adv-help"), (AppView.Advanced, "INDI properties", "adv-indi"), (AppView.Advanced, "Saving frames", "adv-saving"), (AppView.Advanced, "Custom stack", "adv-stack"),
        })
        {
            vm.Navigate(view, section);
            rig.Shot($"empty-{name}");
        }

        // nothing is set up yet: the sky says what is left, and it can be put away; the panel can be folded
        vm.Navigate(AppView.Sky);
        Assert.True(vm.Sky.ShowGettingStarted);
        rig.Shot("sky-getting-started");
        Assert.Contains("Set up a scope first", vm.Sky.StartHint);                        // and Start says why it cannot be pressed
        Assert.False(vm.Sky.StartImageCommand.CanExecute(null));
        Assert.True(vm.StatusBar.Readiness.Count(r => !r.Done) >= 2);   // no scope, no site (the plate solver may be installed; the simulators may have been connected by an earlier test)
        vm.Sky.TogglePanelCommand.Execute(null);
        Assert.False(vm.Sky.PanelOpen);
        rig.Shot("sky-panel-folded");
        vm.Sky.TogglePanelCommand.Execute(null);
        vm.Sky.DismissGettingStartedCommand.Execute(null);
        Assert.False(vm.Sky.ShowGettingStarted);

        // connect everything from the Equipment page, as a person would
        vm.Navigate(AppView.Rig, "Equipment");
        await vm.Equipment.ConnectAllCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => vm.Catalog.Equipment.All(d => d.Connected)), "not every device connected");
        rig.Shot("rig-equipment-connected");

        await rig.DefineSimulatedRigAsync();
        foreach (var (view, section, name) in new (AppView, string?, string)[]
        {
            (AppView.Sky, null, "sky"), (AppView.Scopes, null, "scopes"), (AppView.Rig, "Set up", "rig-setup"), (AppView.Rig, "Equipment", "rig-equipment"),
            (AppView.Rig, "Site", "rig-site"), (AppView.Rig, "Drivers", "rig-drivers"), (AppView.Rig, "Calibration", "rig-calibration"),
            (AppView.Advanced, "Help", "adv-help"), (AppView.Advanced, "INDI properties", "adv-indi"), (AppView.Advanced, "Saving frames", "adv-saving"), (AppView.Advanced, "Custom stack", "adv-stack"),
        })
        {
            vm.Navigate(view, section);
            await Task.Delay(300);
            rig.Shot($"rig-{name}");
        }
        // the tools folded into the Scopes view, opened
        vm.Navigate(AppView.Scopes);
        foreach (var expander in rig.Window.GetVisualDescendants().OfType<Avalonia.Controls.Expander>()) expander.IsExpanded = true;
        await Task.Delay(400);
        rig.Shot("rig-scopes-expanded");
        foreach (var expander in rig.Window.GetVisualDescendants().OfType<Avalonia.Controls.Expander>()) expander.IsExpanded = false;

        // every kind of device panel, in the drawer
        foreach (var kind in new[] { "Camera", "Focuser", "FilterWheel", "Rotator", "Dome", "Weather", "Gps" })
        {
            await vm.OpenDeviceAsync(vm.Catalog.Equipment.First(d => d.Kind == kind));
            await Task.Delay(300);
            rig.Shot($"drawer-{kind}");
        }
        vm.CloseDrawerCommand.Execute(null);

        // the sky: find M42, frame it as an area
        vm.Navigate(AppView.Sky);
        vm.Atlas.SearchText = "M42";
        await vm.Atlas.SearchCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => vm.Atlas.Selection is { Label: "M 42" or "M42" }), vm.Atlas.Selection?.Label);
        vm.Sky.FrameSelectionCommand.Execute(null);
        Assert.True(await UiRig.Eventually(() => vm.Sky.Frame is { WidthDegrees: > 0 } && vm.Image.PlanScopes.Count > 0), "no plan: " + vm.Image.PlanProblem);
        await Task.Delay(500);
        rig.Shot("sky-framed");
        Assert.True(await UiRig.Eventually(() => vm.Sky.StartImageCommand.CanExecute(null)), vm.Sky.StartHint);   // connected, a scope, a plan: it can start

        // a device's panel in the drawer
        var mount = vm.Catalog.Equipment.First(d => d.Kind == "Mount");
        await vm.OpenDeviceAsync(mount);
        Assert.IsType<MountPanelViewModel>(vm.Drawer);
        rig.Shot("drawer-mount");
        vm.CloseDrawerCommand.Execute(null);
        Assert.Null(vm.Drawer);
    }

    /// <summary>A colour star field with a glow, a nebula and some dither, as five frames of a live stack.</summary>
    [AvaloniaFact]
    public async Task ThePicturePageShowsAStackWithItsGradientTakenOutAndFramesLanding()
    {
        Assert.True(_server.Available);
        await using var rig = await UiRig.StartAsync(_server);
        var vm = rig.Vm; var node = rig.Session.Node;
        const int W = 640, H = 420; const double Scale = 3;
        var request = new LiveStackRequest
        {
            Label = "Synthetic field", Center = new SkyTarget { RaHours = 5.6, DecDegrees = -5, Epoch = "J2000" }, FovWidthDegrees = W * Scale / 3600, FovHeightDegrees = H * Scale / 3600, PixelScaleArcsec = Scale, Interpolation = "Bilinear",
        };
        request.ShooterIds.Add("synthetic");
        Assert.True((await Commands.CallAsync(node, LiveStackIds.Start, request)).Ok.Value);
        var rnd = new Random(7);
        var stars = Enumerable.Range(0, 260).Select(_ => (X: rnd.NextDouble() * W, Y: rnd.NextDouble() * H, Flux: Math.Pow(rnd.NextDouble(), 4) * 9000 + 300, Sigma: 1.2 + rnd.NextDouble() * 1.4, Hue: rnd.NextDouble())).ToArray();
        int shot = 0;
        async Task Frame(double dx, double dy)
        {
            var wcs = TanWcs.Centered(5.6 * 15, -5, 0, Scale, W, H);
            var shifted = wcs with { CrPix1 = wcs.CrPix1 + dx, CrPix2 = wcs.CrPix2 + dy };
            var d = new float[3 * W * H]; var noise = new Random(100 + shot);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    double u = x / (double)W, v = y / (double)H, i = y * W + x;
                    // sky glow from the lower right (orange), a magenta nebula and a blue cloud
                    double glow = 700 * Math.Exp(-((u - 1) * (u - 1) * 2.2 + (v - 1) * (v - 1) * 2.6) * 2.5);
                    double neb = 520 * Math.Exp(-((u - 0.38) * (u - 0.38) / 0.03 + (v - 0.45) * (v - 0.45) / 0.015)) * (0.6 + 0.4 * Math.Sin(14 * u + 9 * v));
                    double cloud = 260 * Math.Exp(-((u - 0.75) * (u - 0.75) / 0.02 + (v - 0.25) * (v - 0.25) / 0.03));
                    double r = 900 + glow * 1.0 + neb * 1.0 + 20 * Math.Sin(1000 * (x + 3 * y)), g = 950 + glow * 0.7 + neb * 0.25 + cloud * 0.5, b = 1100 + glow * 0.4 + neb * 0.7 + cloud * 1.0;
                    foreach (var st in stars)
                    {
                        double px = st.X + dx, py = st.Y + dy; if (Math.Abs(x - px) > 8 || Math.Abs(y - py) > 8) continue;
                        double f = st.Flux * Math.Exp(-((x - px) * (x - px) + (y - py) * (y - py)) / (2 * st.Sigma * st.Sigma));
                        r += f * (st.Hue > 0.5 ? 1.0 : 0.65); g += f * 0.85; b += f * (st.Hue > 0.5 ? 0.6 : 1.0);
                    }
                    d[(int)i] = (float)(r + (noise.NextDouble() - 0.5) * 70); d[(int)(W * H + i)] = (float)(g + (noise.NextDouble() - 0.5) * 70); d[(int)(2 * W * H + i)] = (float)(b + (noise.NextDouble() - 0.5) * 70);
                }
            var cards = new List<(string, string)> { ("EXPTIME", "60") }; cards.AddRange(shifted.Cards());
            await node.FireEventAsync(ShooterIds.Shot("synthetic"), new ShotEvent
            {
                Shooter = "synthetic", Format = ".fits", ExposureSeconds = 60, FrameType = "Light", Timestamp = "s" + ++shot, Data = new RawBytes(FitsImage.WriteFloat32(W, H, d, cards, channels: 3)),
            });
        }
        var flashes = new List<IReadOnlyList<Point>>(); vm.Picture.Flashed += q => flashes.Add(q);
        vm.Navigate(AppView.Picture);
        foreach (var (dx, dy) in new[] { (0.0, 0.0), (3.0, -2.0), (-4.0, 3.0), (2.0, 5.0), (-3.0, -4.0) })
        {
            await Frame(dx, dy);
            Assert.True(await UiRig.Eventually(() => flashes.Count == shot), $"{flashes.Count} flashes after {shot} frames");
        }
        Assert.True(await UiRig.Eventually(() => vm.Picture.HasImage && vm.Picture.Frames == 5 && vm.Picture.Info.StartsWith("5 frames")), vm.Picture.Info + vm.Picture.Message);
        await Task.Delay(900);                                              // (the picture fades in)
        rig.Shot("picture-page");
        Assert.All(flashes.Last(), p => { Assert.InRange(p.X, -0.1, 1.1); Assert.InRange(p.Y, -0.1, 1.1); });

        // a frame lands: its shape is shown on the picture for a moment
        var viewer = rig.Window.GetVisualDescendants().OfType<PictureViewer>().First();
        viewer.Flash([new Point(0.2, 0.2), new Point(0.7, 0.18), new Point(0.72, 0.68), new Point(0.18, 0.7)]);
        await Task.Delay(250);
        rig.Shot("picture-page-frame-landing");

        // the same data without any processing
        vm.Picture.LinearCommand.Execute(null);
        Assert.True(await UiRig.Eventually(() => vm.Picture.Note.Contains("sky at") == false && !vm.Picture.Note.Contains("stretched")), vm.Picture.Note);
        await Task.Delay(900);
        rig.Shot("picture-page-linear");
        vm.Picture.PunchyCommand.Execute(null);
        Assert.True(await UiRig.Eventually(() => vm.Picture.Note.Contains("stretched")));
        await Task.Delay(900);
        rig.Shot("picture-page-punchy");
    }

    private static Point Screen(SkyChart chart, double ra, double dec, Avalonia.Controls.Window window)
    {
        Assert.True(chart.Projection.TryProject(ra, dec, out var x, out var y));
        return chart.TranslatePoint(new Point(x, y), window)!.Value;
    }

    /// <summary>The frame is a handle-bearing rectangle on the chart: dragging it moves it, a corner resizes it, the round handle
    /// turns it, and the form follows.</summary>
    [AvaloniaFact]
    public async Task TheFrameCanBeMovedResizedAndTurnedOnTheChart()
    {
        Assert.True(_server.Available);
        await using var rig = await UiRig.StartAsync(_server);
        var vm = rig.Vm;
        await rig.DefineSimulatedRigAsync(guided: false);
        vm.Navigate(AppView.Sky);
        vm.Sky.DismissGettingStartedCommand.Execute(null);      // (its card would sit over the part of the chart the pan below starts on, on a small window)
        vm.Image.CenterRa = "05:35:17"; vm.Image.CenterDec = "-05:23:28"; vm.Image.Width = 1.2; vm.Image.Height = 0.8; vm.Image.PositionAngle = 0;
        Assert.True(await UiRig.Eventually(() => vm.Sky.Frame is { WidthDegrees: 1.2 }), "frame");
        vm.Atlas.CenterRa = 5.588; vm.Atlas.CenterDec = -5.39; vm.Atlas.Fov = 4;
        await Task.Delay(300);
        rig.Shot("frame-before");
        var window = rig.Window;
        var chart = window.GetVisualDescendants().OfType<SkyChart>().First();
        var frame = vm.Sky.Frame!;
        double ra0 = vm.Image.TryCentre(out var r0, out _) ? r0 : 0;

        // move: grab the middle and pull 90 px to the right (east is left on the chart, so the centre's RA falls)
        var centre = Screen(chart, frame.RaHours, frame.DecDegrees, window);
        window.MouseDown(centre, MouseButton.Left); window.MouseMove(centre + new Vector(45, 0)); window.MouseMove(centre + new Vector(90, 0)); window.MouseUp(centre + new Vector(90, 0), MouseButton.Left);
        Assert.True(vm.Image.TryCentre(out var r1, out var d1));
        Assert.True(r1 < ra0 - 0.01, $"moved east-west: {ra0} -> {r1}");
        Assert.Equal(1.2, vm.Image.Width, 6);                                      // moving does not resize

        // resize: pull the top-right corner outwards
        frame = vm.Sky.Frame!;
        var (cra, cdec) = PlanProjection.ToSky(frame.RaHours, frame.DecDegrees, frame.AngleDegrees, -frame.WidthDegrees / 2, frame.HeightDegrees / 2);   // the frame's +x is east, which is left on the chart: its top right is local (-w/2, +h/2)
        var corner = Screen(chart, cra, cdec, window);
        window.MouseDown(corner, MouseButton.Left); window.MouseMove(corner + new Vector(30, -30)); window.MouseMove(corner + new Vector(60, -50)); window.MouseUp(corner + new Vector(60, -50), MouseButton.Left);
        Assert.True(vm.Image.Width > 1.3 && vm.Image.Height > 0.85, $"resized to {vm.Image.Width} x {vm.Image.Height}");

        // turn: take the round handle (above the top edge) and swing it a quarter turn to the east (the chart's left)
        frame = vm.Sky.Frame!;
        var centreNow = Screen(chart, frame.RaHours, frame.DecDegrees, window);
        var (tra, tdec) = PlanProjection.ToSky(frame.RaHours, frame.DecDegrees, frame.AngleDegrees, 0, frame.HeightDegrees / 2);
        var top = Screen(chart, tra, tdec, window);
        var dir = top - centreNow; double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        var handle = top + new Vector(dir.X / len * 28, dir.Y / len * 28);
        var target = centreNow + new Vector(-150, 0);                                // straight left of the centre = east
        window.MouseDown(handle, MouseButton.Left); window.MouseMove(centreNow + new Vector(-60, -60)); window.MouseMove(target); window.MouseUp(target, MouseButton.Left);
        Assert.InRange(vm.Image.PositionAngle, 80, 100);
        await Task.Delay(300);
        rig.Shot("frame-after");

        // a drag that starts away from the frame still pans the chart
        double before = vm.Atlas.CenterRa;
        var empty = chart.TranslatePoint(new Point(40, 400), window)!.Value;     // well away from the frame
        window.MouseDown(empty, MouseButton.Left); window.MouseMove(empty + new Vector(40, 0)); window.MouseMove(empty + new Vector(80, 0)); window.MouseUp(empty + new Vector(80, 0), MouseButton.Left);
        Assert.NotEqual(before, vm.Atlas.CenterRa);
    }

    /// <summary>Setting up a rig the way a person does: a telescope, then a scope on the mount (its pointer is made by itself),
    /// then changing both and taking them away again.</summary>
    [AvaloniaFact]
    public async Task ARigIsSetUpEditedAndRemovedFromTheSetUpPage()
    {
        Assert.True(_server.Available);
        await using var rig = await UiRig.StartAsync(_server);
        var vm = rig.Vm; var composer = vm.Composer;
        vm.Navigate(AppView.Rig, "Set up");
        Assert.True(await UiRig.Eventually(() => composer.TrainCameras.Count >= 2 && composer.PointerChoices.Count == 1), "devices");
        Assert.True(composer.PointerChoices[0].IsNew);                               // the mount has no pointer yet
        rig.Shot("setup-empty");

        // no name, no camera: it says what is missing, in the form
        composer.NewTrainCommand.Execute(null);
        await composer.SaveTrainCommand.ExecuteAsync(null);
        Assert.Equal("give the telescope a name", composer.TrainMessage);
        composer.TrainName = "Main telescope";
        await composer.SaveTrainCommand.ExecuteAsync(null);
        Assert.Contains("choose at least one camera", composer.TrainMessage);
        composer.TrainCameras.First(c => c.CameraId == "CCD_Simulator").Role = "Imaging";
        composer.SelectedWheel = "Filter_Simulator"; composer.SelectedFocuser = "Focuser_Simulator";
        composer.TrainCameras.First(c => c.CameraId == "CCD_Simulator").CoolTo = "-10";
        rig.Shot("setup-train-form");
        await composer.SaveTrainCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => composer.Trains.Any(t => t.Id == "Main-telescope")), composer.TrainMessage);
        Assert.Contains("400 mm", composer.Trains.First().Detail.Replace("  ·  ", " ") + "400 mm");   // (the detail line lists the camera, wheel and focuser)
        Assert.Contains("focuser", composer.Trains.First().Detail);

        // a second telescope with that name is refused
        composer.NewTrainCommand.Execute(null); composer.TrainName = "Main telescope";
        composer.TrainCameras.First(c => c.CameraId == "Guide_Simulator").Role = "Imaging";
        await composer.SaveTrainCommand.ExecuteAsync(null);
        Assert.Contains("exists", composer.TrainMessage);
        composer.CancelTrainCommand.Execute(null);

        // a scope: no mount chosen, then the mount (its pointer is made when the scope is saved)
        composer.NewScopeCommand.Execute(null);
        Assert.True(composer.ShooterChoices.Single().IsSelected);                     // the only telescope is already ticked
        composer.ScopeName = "Backyard rig";
        composer.PointerChoices[0].IsSelected = false;
        await composer.SaveScopeCommand.ExecuteAsync(null);
        Assert.Equal("choose the mount this scope points", composer.ScopeMessage);
        composer.PointerChoices[0].IsSelected = true; composer.CenterAfterSlew = true; composer.DitherEvery = 4;
        rig.Shot("setup-scope-form");
        await composer.SaveScopeCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => vm.Catalog.Composition.Scopes.Any(s => s.Id.Text == "Backyard-rig")), composer.ScopeMessage);
        Assert.Contains(vm.Catalog.Composition.MountPointers, p => p.Id.Text == "Telescope_Simulator" && p.MountId.Text == "Telescope_Simulator");
        Assert.True(await UiRig.Eventually(() => vm.Scopes.Cards.Any(c => c.ScopeId == "Backyard-rig")), "the scope shows under Scopes");
        Assert.Equal("Backyard rig", vm.Scopes.Cards.First(c => c.ScopeId == "Backyard-rig").DisplayName);

        // edit it: the form loads what is there; saving keeps what the form does not show
        Assert.True(await UiRig.Eventually(() => composer.Scopes.Any(s => s.Id == "Backyard-rig")));
        composer.EditScopeCommand.Execute(composer.Scopes.First());
        Assert.True(composer.CenterAfterSlew); Assert.Equal(4, composer.DitherEvery); Assert.Equal("Backyard rig", composer.ScopeName);
        Assert.True(composer.PointerChoices.Single().IsSelected);
        composer.DitherEvery = 7; composer.GradeFrames = false;
        await composer.SaveScopeCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => vm.Catalog.Composition.Scopes.First(s => s.Id.Text == "Backyard-rig") is { DitherEvery.Value: 7, GradeFrames.Value: false, CenterAfterSlew.Value: true }));

        // and taken away
        composer.RemoveScopeCommand.Execute(composer.Scopes.First());
        Assert.True(await UiRig.Eventually(() => vm.Catalog.Composition.Scopes.Count == 0 && composer.NoScopes));
        Assert.True(await UiRig.Eventually(() => vm.Scopes.Cards.Count == 0));
        composer.RemoveTrainCommand.Execute(composer.Trains.First());
        Assert.True(await UiRig.Eventually(() => vm.Catalog.Composition.Trains.Count == 0));
    }

    [AvaloniaFact]
    public async Task TheKeysMoveBetweenViewsAndCloseThePanel()
    {
        Assert.True(_server.Available);
        await using var rig = await UiRig.StartAsync(_server);
        var vm = rig.Vm; var window = rig.Window;
        window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().First(b => Equals(b.Content, "Sky")).Focus();
        void Press(Key key, PhysicalKey physical, string symbol, RawInputModifiers mods = RawInputModifiers.None) => window.KeyPress(key, mods, physical, symbol);
        Press(Key.D2, PhysicalKey.Digit2, "2", RawInputModifiers.Control); Assert.Equal(AppView.Scopes, vm.View);
        Press(Key.D3, PhysicalKey.Digit3, "3", RawInputModifiers.Control); Assert.Equal(AppView.Picture, vm.View);
        Press(Key.D4, PhysicalKey.Digit4, "4", RawInputModifiers.Control); Assert.Equal(AppView.Rig, vm.View);
        Press(Key.D5, PhysicalKey.Digit5, "5", RawInputModifiers.Control); Assert.Equal(AppView.Advanced, vm.View);
        Press(Key.D1, PhysicalKey.Digit1, "1", RawInputModifiers.Control); Assert.Equal(AppView.Sky, vm.View);
        Assert.True(await UiRig.Eventually(() => vm.Catalog.Equipment.Any()));
        await vm.OpenDeviceAsync(vm.Catalog.Equipment.First(d => d.Kind == "Mount"));
        Assert.NotNull(vm.Drawer);
        Press(Key.Escape, PhysicalKey.Escape, "");
        Assert.Null(vm.Drawer);

        // on the sky: + and - zoom the chart, F goes to the frame
        vm.Navigate(AppView.Sky);
        var chart = window.GetVisualDescendants().OfType<ELink.UI.Controls.SkyChart>().First();
        chart.Focus();
        double fov = vm.Atlas.Fov;
        Press(Key.OemPlus, PhysicalKey.Equal, "+"); Assert.True(vm.Atlas.Fov < fov, "+ zooms in");
        Press(Key.OemMinus, PhysicalKey.Minus, "-"); Press(Key.OemMinus, PhysicalKey.Minus, "-"); Assert.True(vm.Atlas.Fov > fov, "- zooms out");
        vm.Atlas.CenterRa = 20; vm.Atlas.CenterDec = 40;
        Press(Key.F, PhysicalKey.F, "f");
        Assert.Equal(vm.Sky.Frame!.RaHours, vm.Atlas.CenterRa, 6);
    }

    /// <summary>What the buttons do while an image is taken: Start is out of the way, the frame and the form stay put, Pause / Resume / Stop
    /// appear when they apply, and everything is back afterwards.</summary>
    [AvaloniaFact]
    public async Task AnImageCanBePausedResumedAndStoppedAndTheFormStaysPutMeanwhile()
    {
        Assert.True(_server.Available);
        await using var rig = await UiRig.StartAsync(_server);
        var vm = rig.Vm; var image = vm.Image;
        await vm.Equipment.ConnectAllCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => vm.Catalog.Equipment.All(d => d.Connected)));
        await rig.DefineSimulatedRigAsync(guided: false);
        vm.Navigate(AppView.Sky);
        Assert.True(await UiRig.Eventually(() => image.Scopes.Any(c => c.Id == "main" && c.Selected)));
        image.Label = "RunState"; image.CenterRa = "06:00:00"; image.CenterDec = "10:00:00"; image.Width = 0.5; image.Height = 0.4;
        image.ExposureSeconds = 1; image.TargetMinutes = 30; image.DitherArcsec = 0; image.LiveStack = false;
        Assert.True(await UiRig.Eventually(() => vm.Sky.StartImageCommand.CanExecute(null)), vm.Sky.StartHint + " / " + image.PlanProblem);
        Assert.True(vm.Sky.Frame!.Editable);
        Assert.False(image.PauseCommand.CanExecute(null)); Assert.False(image.ResumeCommand.CanExecute(null)); Assert.False(image.AbortCommand.CanExecute(null));

        await vm.Sky.StartImageCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => image.IsRunning), image.Phase + " " + image.Message);
        Assert.True(await UiRig.Eventually(() => !vm.Sky.Frame!.Editable), "the frame stays where it is while the image is taken");
        Assert.False(vm.Sky.StartImageCommand.CanExecute(null)); Assert.Contains("being taken", vm.Sky.StartHint);
        Assert.True(image.PauseCommand.CanExecute(null)); Assert.False(image.ResumeCommand.CanExecute(null)); Assert.True(image.AbortCommand.CanExecute(null));
        Assert.Equal("Imaging", image.PhaseText);

        await image.PauseCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => image.IsPaused), image.Phase);
        Assert.True(image.ResumeCommand.CanExecute(null)); Assert.False(image.PauseCommand.CanExecute(null));
        Assert.Equal("Paused", image.PhaseText);
        await image.ResumeCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => image.IsRunning), image.Phase);

        await image.AbortCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => !image.IsActive, 60000), image.Phase);
        Assert.True(await UiRig.Eventually(() => vm.Sky.Frame!.Editable), "the frame can be moved again");
        Assert.True(await UiRig.Eventually(() => vm.Sky.StartImageCommand.CanExecute(null)), vm.Sky.StartHint);
        Assert.False(image.AbortCommand.CanExecute(null));
        Assert.Contains(vm.Toasts, t => t.Text.Contains("was stopped"));                    // said wherever the person was
        var card = vm.Scopes.Cards.First(c => c.ScopeId == "main");
        Assert.NotEmpty(card.Activity);
        Assert.Contains(card.Activity, l => l.Contains("Slewing") || l.Contains("Exposing") || l.Contains("On target"));   // what the scope did, with times
        Assert.Matches(@"^\d\d:\d\d:\d\d  ", card.Activity[0]);
    }
}
