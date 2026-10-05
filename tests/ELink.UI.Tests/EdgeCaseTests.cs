using Avalonia.Headless.XUnit;
using ELink.Contracts.Automation;
using ELink.Core;
using ELink.UI.ViewModels;
using Event.Connections.Models.BaseBinaryConvertibles;
using Xunit;

namespace ELink.UI.Tests;

/// <summary>What the UI does with bad numbers, odd shapes and awkward places: it says what is wrong, where, and keeps Start out of reach.</summary>
public class EdgeCaseTests : IClassFixture<IndiServerFixture>
{
    private readonly IndiServerFixture _server;
    public EdgeCaseTests(IndiServerFixture server) { _server = server; }

    [Theory]
    [InlineData(" Main  telescope ", "Main-telescope")]
    [InlineData("a/b\\c", "a-b-c")]
    [InlineData("!!!", null)]
    [InlineData("", null)]
    [InlineData("Ünï côde", "Ünï-côde")]
    [InlineData("--x--", "x")]
    public void NamesBecomeIdsOrNothing(string name, string? id) => Assert.Equal(id, ComposerViewModel.IdFromName(name));

    private static async Task<UiRig> RigAsync(IndiServerFixture server, bool withSolver = true)
    {
        var rig = await UiRig.StartAsync(server, withSolver);
        await rig.Vm.Equipment.ConnectAllCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => rig.Vm.Catalog.Equipment.All(d => d.Connected)));
        await rig.DefineSimulatedRigAsync(guided: false);
        rig.Vm.Navigate(AppView.Sky);
        Assert.True(await UiRig.Eventually(() => rig.Vm.Image.Scopes.Any(c => c.Id == "main" && c.Selected)));
        var image = rig.Vm.Image;
        image.Label = "Edge"; image.CenterRa = "06:00:00"; image.CenterDec = "10:00:00"; image.Width = 0.6; image.Height = 0.5; image.ExposureSeconds = 1; image.TargetMinutes = 1;
        Assert.True(await UiRig.Eventually(() => rig.Vm.Sky.StartImageCommand.CanExecute(null) && rig.Vm.Image.PlanScopes.Count == 1), rig.Vm.Sky.StartHint + " / " + image.PlanProblem);
        return rig;
    }

    [AvaloniaFact]
    public async Task BadNumbersAreSaidInThePlanAndStartStaysOutOfReach()
    {
        Assert.True(_server.Available);
        await using var rig = await RigAsync(_server);
        var vm = rig.Vm; var image = vm.Image;

        async Task Problem(Action bad, string contains, bool frameGone = false)
        {
            bad();
            Assert.True(await UiRig.Eventually(() => image.PlanProblem.Contains(contains, StringComparison.OrdinalIgnoreCase)), $"expected '{contains}', got '{image.PlanProblem}'");
            Assert.True(await UiRig.Eventually(() => !vm.Sky.StartImageCommand.CanExecute(null)), "Start must be out of reach");
            Assert.True(await UiRig.Eventually(() => vm.Sky.PlanText.Contains(contains, StringComparison.OrdinalIgnoreCase)), $"the chart says: {vm.Sky.PlanText}");   // (the chart follows the plan a moment later)
            if (frameGone) Assert.Null(vm.Sky.Frame);
        }
        async Task Fine(Action good)
        {
            good();
            Assert.True(await UiRig.Eventually(() => image.PlanProblem == "" && vm.Sky.StartImageCommand.CanExecute(null)), image.PlanProblem);
            Assert.NotNull(vm.Sky.Frame);
        }

        await Problem(() => image.CenterRa = "25:00:00", "RA", frameGone: true);
        await Fine(() => image.CenterRa = "06:00:00");
        await Problem(() => image.CenterDec = "95:00:00", "Dec", frameGone: true);
        await Fine(() => image.CenterDec = "10:00:00");
        await Problem(() => image.CenterRa = "not a number", "RA", frameGone: true);
        await Fine(() => image.CenterRa = "06:00:00");
        await Problem(() => image.Width = 61, "60");
        await Fine(() => image.Width = 0.6);
        await Problem(() => image.ExposureSeconds = 0, "exposure");
        await Fine(() => image.ExposureSeconds = 1);
        await Problem(() => { image.CenterDec = "89:55:00"; image.Width = 2; image.Height = 2; }, "pole");
        await Fine(() => { image.CenterDec = "10:00:00"; image.Width = 0.6; image.Height = 0.5; });

        // no scope ticked: Start says so (not an error in the plan)
        foreach (var c in image.Scopes) c.Selected = false;
        Assert.True(await UiRig.Eventually(() => vm.Sky.StartHint.Contains("Tick a scope")));
        Assert.False(vm.Sky.StartImageCommand.CanExecute(null));
        foreach (var c in image.Scopes) c.Selected = true;
        Assert.True(await UiRig.Eventually(() => vm.Sky.StartImageCommand.CanExecute(null)));
    }

    [AvaloniaFact]
    public async Task ATelescopeOrScopeInUseCannotBeRemovedFromUnderItsScope()
    {
        Assert.True(_server.Available);
        await using var rig = await RigAsync(_server);
        var vm = rig.Vm; var composer = vm.Composer;
        Assert.True(await UiRig.Eventually(() => composer.Trains.Any(t => t.Id == "cam") && composer.Pointers.Any(p => p.Id == "eq")));
        composer.RemoveTrainCommand.Execute(composer.Trains.First(t => t.Id == "cam"));
        Assert.True(await UiRig.Eventually(() => vm.Toasts.Any(t => t.Text.Contains("is part of scope 'main'"))), string.Join(" | ", vm.Toasts.Select(t => t.Text)));
        composer.RemovePointerCommand.Execute(composer.Pointers.First(p => p.Id == "eq"));
        Assert.True(await UiRig.Eventually(() => composer.PointerMessage.Contains("is part of scope 'main'")), composer.PointerMessage);
        Assert.Equal("error", composer.PointerMessageKind);
        Assert.Contains(composer.Trains, t => t.Id == "cam");                         // still there
        // after the scope goes, the telescope can
        composer.RemoveScopeCommand.Execute(composer.Scopes.First());
        Assert.True(await UiRig.Eventually(() => composer.NoScopes));
        composer.RemoveTrainCommand.Execute(composer.Trains.First(t => t.Id == "cam"));
        Assert.True(await UiRig.Eventually(() => !composer.Trains.Any(t => t.Id == "cam")));
    }

    [AvaloniaFact]
    public async Task TheSitePageSaysWhatIsWrongWithWhatWasTyped()
    {
        Assert.True(_server.Available);
        await using var rig = await UiRig.StartAsync(_server);
        var site = rig.Vm.Site;
        site.Latitude = "forty seven"; site.Longitude = "19.04";
        await site.ApplyCommand.ExecuteAsync(null);
        Assert.Contains("latitude", site.Message); Assert.Equal("error", site.MessageKind);
        site.Latitude = "47.5"; site.Longitude = "east";
        await site.ApplyCommand.ExecuteAsync(null);
        Assert.Contains("longitude", site.Message); Assert.Equal("error", site.MessageKind);
        site.Longitude = "19.04"; site.HorizonText = "180:15, nonsense";
        await site.ApplyCommand.ExecuteAsync(null);
        Assert.NotEqual("saved", site.Message); Assert.Equal("error", site.MessageKind);
        site.HorizonText = "180:15, 200:35";
        await site.ApplyCommand.ExecuteAsync(null);
        Assert.Equal("saved", site.Message); Assert.Equal("ok", site.MessageKind);
        Assert.True(await UiRig.Eventually(() => site.Known));
        Assert.True(await UiRig.Eventually(() => site.Night is not null && site.Night.Dark.Count > 0), "the night bar follows");
        Assert.True(await UiRig.Eventually(() => rig.Vm.StatusBar.NightText != "" && rig.Vm.StatusBar.NightText != "Site not set"), rig.Vm.StatusBar.NightText);
    }

    [AvaloniaFact]
    public async Task DoubleClickingTheSkyFramesWhatIsThere()
    {
        Assert.True(_server.Available);
        await using var rig = await RigAsync(_server);
        var vm = rig.Vm;
        Assert.False(vm.StatusBar.NotConnected);
        vm.Atlas.CenterRa = 5.588; vm.Atlas.CenterDec = -5.39; vm.Atlas.Fov = 3;
        await vm.Atlas.RefreshAsync();
        Assert.True(await UiRig.Eventually(() => vm.Atlas.Dsos.Any(d => d.Id == "M 42")), "the atlas answered");
        vm.Sky.FrameAtCommand.Execute((5.5885, -5.39, 100.0));
        Assert.True(await UiRig.Eventually(() => vm.Image.Label == "M42" && vm.Sky.Frame is { WidthDegrees: > 1 }), $"{vm.Image.Label} {vm.Sky.Frame}");
        Assert.True(await UiRig.Eventually(() => Math.Abs(vm.Atlas.CenterRa - 5.588) < 0.05));
    }

    [AvaloniaFact]
    public async Task OneAxisAtZeroIsOneFramesWorthAndTheFrameShowsIt()
    {
        Assert.True(_server.Available);
        await using var rig = await RigAsync(_server);
        var vm = rig.Vm; var image = vm.Image;
        image.Width = 0; image.Height = 3;                                   // a strip: one frame wide, three degrees tall
        Assert.True(await UiRig.Eventually(() => vm.Sky.Frame is { HeightDegrees: 3 }), "strip");
        var field = image.PlanScopes.Single().Frames[0];
        Assert.True(Math.Abs(2 * field.HalfWidth * 0.9 - vm.Sky.Frame!.WidthDegrees) < 1e-6, $"frame {vm.Sky.Frame}; image {image.Width} x {image.Height}; field half {field.HalfWidth} x {field.HalfHeight}");   // what the imaging service takes for no width
        Assert.True(vm.Sky.Frame.WidthDegrees < vm.Sky.Frame.HeightDegrees);
        Assert.DoesNotContain("One frame", vm.Sky.PlanText);                              // a strip is an area, of panels in a column
        Assert.Contains("panels", vm.Sky.PlanText);
        image.Width = 0; image.Height = 0;
        Assert.True(await UiRig.Eventually(() => vm.Sky.PlanText.StartsWith("One frame") && Math.Abs(vm.Sky.Frame!.HeightDegrees - 2 * field.HalfHeight * 0.9) < 1e-6), $"{vm.Sky.PlanText} / {vm.Sky.Frame}");
    }

    [AvaloniaFact]
    public async Task QueuedImagesCanBeEditedAndRemovedAndTheQueueFollows()
    {
        Assert.True(_server.Available);
        await using var rig = await RigAsync(_server);
        var vm = rig.Vm; var image = vm.Image; var node = rig.Session.Node;
        async Task<Schedule> Backend() => Assert.Single((await node.CallFunctionAsync<NOTESVoid, Schedule>(SchedulerIds.GetSchedule, NOTESVoid.Void, TimeSpan.FromSeconds(5)))!);

        Assert.False(vm.Schedule.RunCommand.CanExecute(null));                // nothing queued: nothing to run
        await image.AddToScheduleCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => vm.Schedule.Rows.Count == 1), image.Message);
        Assert.Equal("ok", image.MessageKind);                                // "in the queue" is good news, not an error
        Assert.True(vm.Schedule.RunCommand.CanExecute(null));
        // the same name again replaces it
        image.TargetMinutes = 5;
        await image.AddToScheduleCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => vm.Schedule.Rows.Count == 1 && vm.Schedule.Rows[0].Entry.Request.TargetSeconds.Value == 300));

        // a condition edited: it reaches the scheduler by itself a moment later
        var row = vm.Schedule.Rows[0];
        row.Priority = 7; row.RequireDark = false; row.MinMoonSeparation = 40;
        Assert.True(await AsyncExt.EventuallyAsync(async () =>
        {
            var e = (await Backend()).Entries.FirstOrDefault();
            return e is { Priority.Value: 7, RequireDark.Value: false } && Math.Abs(e.MinMoonSeparationDegrees.Value - 40) < 1e-9;
        }), "the edit should be applied without a button");
        // switched off: it stays in the queue
        // (the rows are rebuilt when the scheduler's state comes back, so the row to edit is looked up again until the edit has taken)
        Assert.True(await AsyncExt.EventuallyAsync(async () => { vm.Schedule.Rows[0].Enabled = false; await Task.Delay(150); return !(await Backend()).Entries[0].Enabled.Value; }));
        Assert.Equal(1, (await Backend()).Entries.Count);

        // taken out
        await vm.Schedule.RemoveRowCommand.ExecuteAsync(vm.Schedule.Rows[0]);
        Assert.True(await UiRig.Eventually(() => vm.Schedule.Rows.Count == 0));
        Assert.Empty((await Backend()).Entries);
        Assert.False(vm.Schedule.RunCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task LayersAreCheckedAgainstTheCamerasAsTheyAreTyped()
    {
        Assert.True(_server.Available);
        await using var rig = await RigAsync(_server);
        var vm = rig.Vm; var image = vm.Image;
        image.LayerRows.Add(new LayerRow { Name = "coarse", FinestScale = 500, CoarsestScale = 900, Minutes = 1 });
        Assert.True(await UiRig.Eventually(() => image.PlanProblem.Contains("coarse")), image.PlanProblem);
        Assert.True(await UiRig.Eventually(() => !vm.Sky.StartImageCommand.CanExecute(null)));
        image.LayerRows[0].Name = "";
        Assert.True(await UiRig.Eventually(() => image.PlanProblem.Contains("needs a name")), image.PlanProblem);
        image.LayerRows[0].Name = "everything"; image.LayerRows[0].FinestScale = 0; image.LayerRows[0].CoarsestScale = 0;
        Assert.True(await UiRig.Eventually(() => image.PlanProblem == "" && vm.Sky.StartImageCommand.CanExecute(null)), image.PlanProblem);
        image.RemoveLayerCommand.Execute(image.LayerRows[0]);
        Assert.True(await UiRig.Eventually(() => image.PlanProblem == ""));
    }

    [AvaloniaFact]
    public async Task TheFocusHelperMeasuresFramesUntilItIsStopped()
    {
        Assert.True(_server.Available);
        await using var rig = await RigAsync(_server);
        var helper = rig.Vm.FocusAssist;
        Assert.True(await UiRig.Eventually(() => helper.Shooters.Count > 0 && helper.SelectedShooter is not null));
        helper.ExposureSeconds = 0.5;
        await helper.BeginCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => helper.IsRunning && helper.DetailText.Contains("frames")), helper.Message + helper.Phase);
        Assert.False(helper.BeginCommand.CanExecute(null));
        await helper.StopCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => !helper.IsRunning));
    }

    [AvaloniaFact]
    public async Task QuickSetupMakesTheTelescopeAndTheScopeInOneGo()
    {
        Assert.True(_server.Available);
        await using var rig = await UiRig.StartAsync(_server);
        var vm = rig.Vm;
        await vm.Equipment.ConnectAllCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => vm.Catalog.Equipment.All(d => d.Connected)));
        var setup = vm.Composer;
        Assert.True(await UiRig.Eventually(() => setup.CanQuickSetup), "quick set up should be offered when a mount and a camera are there and nothing else is");
        setup.QuickName = "Garden"; setup.QuickFocalLength = 600;
        await setup.QuickSetupCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => vm.Catalog.Composition.Scopes.Any(s => s.Id.Text == "Garden")), setup.TrainMessage + setup.ScopeMessage);
        var train = Assert.Single(vm.Catalog.Composition.Trains);
        Assert.Equal(600, train.FocalLengthMm.Value);
        Assert.Single(train.Cameras);
        Assert.False(setup.CanQuickSetup);                               // done: not offered again
        Assert.Equal("", setup.ScopeNextHint);
    }

    [AvaloniaFact]
    public async Task TheSessionLogShowsWhatTheScopesDoAndTakesYourNotes()
    {
        await using var rig = await RigAsync(_server, withSolver: false);
        var log = rig.Vm.SessionLog;
        Assert.True(await UiRig.Eventually(() => log.Lines.Any(l => l.Contains("session log open"))), "the log did not open");
        Assert.True(await UiRig.Eventually(() => log.Lines.Any(l => l.Contains("main:") && l.Contains("Idle"))), string.Join("\n", log.Lines));

        log.Note = "clouds drifting in from the west";
        Assert.True(log.AddNoteCommand.CanExecute(null));
        log.AddNoteCommand.Execute(null);
        Assert.Equal("", log.Note);
        Assert.True(await UiRig.Eventually(() => log.Lines.Any(l => l.Contains("note  you: clouds drifting in from the west"))), string.Join("\n", log.Lines));

        string copied = "";
        log.CopyToClipboard = t => { copied = t; return Task.CompletedTask; };
        await log.CopyCommand.ExecuteAsync(null);
        Assert.Contains("clouds drifting", copied);
        Assert.Contains(".NET", copied);
        Assert.Contains(log.File, copied);

        rig.Vm.Navigate(AppView.Advanced, "Session log");
        rig.Shot("session-log");
    }

    [AvaloniaFact]
    public async Task TonightsBestNeedsASiteThenSuggestsAndFramesWithOneClick()
    {
        Assert.True(_server.Available);
        await using var rig = await RigAsync(_server);
        var vm = rig.Vm; var tonight = vm.Tonight;
        await tonight.RefreshCommand.ExecuteAsync(null);
        Assert.True(tonight.NeedsSite, tonight.Message);
        Assert.Empty(tonight.Items);

        vm.Site.Latitude = "47:29:52"; vm.Site.Longitude = "19:02:25"; vm.Site.MinAltitude = 15;
        await vm.Site.ApplyCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => vm.Site.Known));
        await tonight.RefreshCommand.ExecuteAsync(null);
        Assert.True(await UiRig.Eventually(() => tonight.Items.Count >= 5), tonight.Message);
        Assert.False(tonight.NeedsSite);
        Assert.Contains("dark", tonight.Window);
        Assert.True(tonight.Items.Zip(tonight.Items.Skip(1)).All(p => p.First.Score >= p.Second.Score), "best first");
        var pick = tonight.Items[1];
        Assert.Contains("good hours", pick.Why);
        vm.Navigate(AppView.Sky); await Task.Delay(500); rig.Shot("sky-tonight");

        // one click makes it the image
        tonight.FrameItCommand.Execute(pick);
        Assert.True(await UiRig.Eventually(() => vm.Atlas.Selection?.Label == pick.Label), vm.Atlas.Selection?.Label);
        Assert.True(await UiRig.Eventually(() => vm.Sky.Frame is { } f && Math.Abs(f.RaHours - pick.RaHours) < 0.05 && Math.Abs(f.DecDegrees - pick.DecDegrees) < 0.5), "the frame goes to the suggestion");
        Assert.Equal(pick.Label.Replace(" ", ""), vm.Image.Label.Replace(" ", ""), ignoreCase: true);
    }

    [AvaloniaFact]
    public async Task AFramingPictureIsSolvedShownOnTheChartAndMakesTheImageExactlyOneFrameOfIt()
    {
        Assert.True(_server.Available);
        await using var rig = await RigAsync(_server, withSolver: false);      // (a real solver on the mesh would be asked too)
        var vm = rig.Vm; var framing = vm.Sky.Framing;
        // no plate solver here: a stand-in that "solves" it as a field at 5h36m, -5°, turned 30°, 4"/px
        SolveRequest? seen = null;
        var truth = ELink.Imaging.TanWcs.Centered(5.6 * 15, -5, 30, 4, 600, 400);
        using var fakes = new ELink.Core.CommandSet(rig.HostNode);
        await fakes.AddAsync<SolveRequest, SolveResult>(SolveIds.Solve, r =>
        {
            seen = r;
            return Task.FromResult(new SolveResult
            {
                Solved = true, RaHours = 5.6, DecDegrees = -5, PositionAngle = 30, PixelScale = 4, FieldWidthDegrees = 600 * 4 / 3600.0, FieldHeightDegrees = 400 * 4 / 3600.0,
                HasWcs = true, WcsCrVal1 = truth.CrVal1, WcsCrVal2 = truth.CrVal2, WcsCrPix1 = truth.CrPix1 + 1, WcsCrPix2 = truth.CrPix2 + 1, WcsCd11 = truth.Cd11, WcsCd12 = truth.Cd12, WcsCd21 = truth.Cd21, WcsCd22 = truth.Cd22,
            });
        }, "fake solver");
        await Task.Delay(300);                                                      // (the UI node learns that someone answers)
        seen = null;

        // a picture: some stars on black
        var rnd = new Random(2); var pixels = new float[600 * 400 * 3];
        for (int s = 0; s < 80; s++) { int cx = rnd.Next(10, 590), cy = rnd.Next(10, 390); for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) for (int c = 0; c < 3; c++) pixels[c * 240000 + (cy + dy) * 600 + cx + dx] = 1f - 0.15f * (dx * dx + dy * dy); }
        string png = Path.Combine(Path.GetTempPath(), "framing-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(png, ELink.Imaging.Processing.PngWriter.Encode(pixels, 600, 400, 3));
        await framing.FrameFromAsync(png);
        Assert.True(framing.MessageKind == "ok", framing.Message);
        Assert.Contains("exactly one frame", framing.Message);
        Assert.NotNull(seen); Assert.True(double.IsNaN(seen!.HintRaHours.Value));            // nothing is known about a plain picture: a blind solve
        var sent = ELink.Imaging.FitsImage.Parse(seen.Image.Data);
        Assert.Equal((600, 400, 1), (sent.Width, sent.Height, sent.Channels));
        // the image to take is exactly one frame of the picture
        var image = vm.Image;
        Assert.Equal(600 * 4 / 3600.0, image.Width, 3); Assert.Equal(400 * 4 / 3600.0, image.Height, 3); Assert.Equal(30, image.PositionAngle, 1);
        Assert.True(ELink.Core.Astro.Sexagesimal.TryParse(image.CenterRa, out var ra) && Math.Abs(ra - 5.6) < 0.001, image.CenterRa);
        Assert.True(await UiRig.Eventually(() => vm.Sky.Frame is { } f && Math.Abs(f.WidthDegrees - 600 * 4 / 3600.0) < 0.001 && Math.Abs(f.AngleDegrees - 30) < 0.1), "the frame on the chart is the picture's");
        // and the picture is on the chart, where it belongs
        Assert.True(framing.HasPicture); Assert.Contains("framing-", framing.FileName);
        Assert.True(await UiRig.Eventually(() => vm.Sky.Images.Count >= 1));
        var (cra, cdec) = framing.PixelToSky!(300, 200);
        Assert.Equal(5.6, cra, 3); Assert.Equal(-5, cdec, 2);
        var (tra, tdec) = framing.PixelToSky(0, 0);
        double sep = ELink.Core.Astro.Sky.SeparationDegrees(cra, cdec, tra, tdec);
        Assert.InRange(sep, 0.5 * Math.Sqrt(600 * 600 + 400 * 400) * 4 / 3600 * 0.98, 0.5 * Math.Sqrt(600 * 600 + 400 * 400) * 4 / 3600 * 1.02);   // a corner is half a diagonal away
        framing.ShowOnChart = false;
        Assert.True(await UiRig.Eventually(() => vm.Sky.Images.Count == 0));
        framing.ShowOnChart = true;
        framing.ClearCommand.Execute(null);
        Assert.False(framing.HasPicture);
        Assert.True(await UiRig.Eventually(() => vm.Sky.Images.Count == 0));
        // what it cannot use is said
        await framing.FrameFromAsync(Path.Combine(Path.GetTempPath(), "nothing-here.png"));
        Assert.Equal("error", framing.MessageKind);
        string txt = Path.Combine(Path.GetTempPath(), "x" + Guid.NewGuid().ToString("N") + ".txt"); File.WriteAllText(txt, "no");
        await framing.FrameFromAsync(txt);
        Assert.Contains("FITS", framing.Message);
    }

    /// <summary>With ELINK_REAL_PICTURE set to one of your own frames (a FITS or a picture of the sky): the real plate solver frames the image like it.</summary>
    [AvaloniaFact]
    public async Task ARealPictureIsSolvedByTheRealSolverAndFramesTheImage()
    {
        if (Environment.GetEnvironmentVariable("ELINK_REAL_PICTURE") is not { } path || !File.Exists(path)) return;
        Assert.True(_server.Available);
        await using var rig = await RigAsync(_server);
        if (ELink.Automation.PlateSolver.Locate() is null) return;
        var vm = rig.Vm; var framing = vm.Sky.Framing;
        await framing.FrameFromAsync(path);
        var note = $"[framing] {framing.MessageKind}: {framing.Message}\n";
        note += $"[framing] image: centre {vm.Image.CenterRa} {vm.Image.CenterDec}, {vm.Image.Width:0.###}° × {vm.Image.Height:0.###}°, turned {vm.Image.PositionAngle}°\n";
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "elink-framing.txt"), note);
        Assert.True(framing.MessageKind == "ok", framing.Message);
        vm.Navigate(AppView.Sky); await Task.Delay(1500); rig.Shot("sky-framed-from-picture");
    }
}

internal static class AsyncExt
{
    public static async Task<bool> EventuallyAsync(Func<Task<bool>> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (await cond()) return true; await Task.Delay(50); }
        return await cond();
    }

}
