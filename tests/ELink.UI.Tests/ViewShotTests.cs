using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using ELink.Core.Astro;
using ELink.UI.Controls;
using ELink.UI.ViewModels;
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
            (AppView.Advanced, "INDI properties", "adv-indi"), (AppView.Advanced, "Saving frames", "adv-saving"), (AppView.Advanced, "Manual live stack", "adv-stack"),
        })
        {
            vm.Navigate(view, section);
            rig.Shot($"empty-{name}");
        }

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
            (AppView.Advanced, "INDI properties", "adv-indi"), (AppView.Advanced, "Saving frames", "adv-saving"), (AppView.Advanced, "Manual live stack", "adv-stack"),
        })
        {
            vm.Navigate(view, section);
            await Task.Delay(300);
            rig.Shot($"rig-{name}");
        }
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

        // a device's panel in the drawer
        var mount = vm.Catalog.Equipment.First(d => d.Kind == "Mount");
        await vm.OpenDeviceAsync(mount);
        Assert.IsType<MountPanelViewModel>(vm.Drawer);
        rig.Shot("drawer-mount");
        vm.CloseDrawerCommand.Execute(null);
        Assert.Null(vm.Drawer);
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
        var (e, n) = SkyChart.LocalToSky(frame.AngleDegrees, -frame.WidthDegrees / 2, frame.HeightDegrees / 2);   // the frame's +x is east, which is left on the chart: its top right is local (-w/2, +h/2)
        var (cra, cdec) = Gnomonic.ToSky(frame.RaHours, frame.DecDegrees, e, n);
        var corner = Screen(chart, cra, cdec, window);
        window.MouseDown(corner, MouseButton.Left); window.MouseMove(corner + new Vector(30, -30)); window.MouseMove(corner + new Vector(60, -50)); window.MouseUp(corner + new Vector(60, -50), MouseButton.Left);
        Assert.True(vm.Image.Width > 1.3 && vm.Image.Height > 0.85, $"resized to {vm.Image.Width} x {vm.Image.Height}");

        // turn: take the round handle (above the top edge) and swing it a quarter turn to the east (the chart's left)
        frame = vm.Sky.Frame!;
        var centreNow = Screen(chart, frame.RaHours, frame.DecDegrees, window);
        var (te, tn) = SkyChart.LocalToSky(frame.AngleDegrees, 0, frame.HeightDegrees / 2);
        var (tra, tdec) = Gnomonic.ToSky(frame.RaHours, frame.DecDegrees, te, tn);
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
}
