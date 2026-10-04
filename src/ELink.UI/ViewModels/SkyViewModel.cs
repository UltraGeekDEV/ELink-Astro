using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ELink.Core;
using ELink.Core.Astro;
using ELink.UI.Controls;
using ELink.UI.Infrastructure;

namespace ELink.UI.ViewModels;

/// <summary>The home screen: the sky, with the image you are planning drawn on it as a frame you can drag, resize and turn.
/// Around the frame it draws each scope's own field, the panels of a mosaic, how far the image has got and the image itself
/// as it builds. The form beside it holds the same numbers; either can be used.</summary>
public sealed partial class SkyViewModel : ObservableObject
{
    private readonly MeshSession _mesh;
    private bool _rebuildQueued;

    public SkyViewModel(MeshSession mesh, AtlasViewModel atlas, ImageViewModel image, ScheduleViewModel schedule, LiveStackViewModel liveStack, StatusBarViewModel status)
    {
        _mesh = mesh; Atlas = atlas; Image = image; Schedule = schedule; LiveStack = liveStack; Status = status;
        atlas.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AtlasViewModel.Selection)) FrameSelectionCommand.NotifyCanExecuteChanged(); };
        status.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StatusBarViewModel.NeedsSetup)) OnPropertyChanged(nameof(ShowGettingStarted)); };
        image.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ImageViewModel.CenterRa) or nameof(ImageViewModel.CenterDec) or nameof(ImageViewModel.Width) or nameof(ImageViewModel.Height)
                or nameof(ImageViewModel.PositionAngle) or nameof(ImageViewModel.Label) or nameof(ImageViewModel.Map) or nameof(ImageViewModel.Stepover) or nameof(ImageViewModel.TargetMinutes)
                or nameof(ImageViewModel.Phase)) QueueRebuild();
        };
        image.PlanChanged += QueueRebuild;
        image.Stack.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(FrameDisplay.Image) or nameof(FrameDisplay.Wcs)) QueueRebuild(); };
        foreach (var c in image.Scopes) c.PropertyChanged += (_, _) => QueueRebuild();
        image.Scopes.CollectionChanged += (_, _) => QueueRebuild();
        Rebuild();
    }

    public AtlasViewModel Atlas { get; }
    public ImageViewModel Image { get; }
    public ScheduleViewModel Schedule { get; }
    public LiveStackViewModel LiveStack { get; }
    public StatusBarViewModel Status { get; }

    // the side panel can be folded away to give the chart the whole width
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PanelToggleText))] private bool _panelOpen = true;
    public string PanelToggleText => PanelOpen ? "Hide panel ▸" : "◂ Panel";
    [RelayCommand] private void TogglePanel() => PanelOpen = !PanelOpen;

    // a short list of what is still to do, over the chart, until it is done or put away
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowGettingStarted))] private bool _gettingStartedDismissed;
    public bool ShowGettingStarted => !GettingStartedDismissed && Status.NeedsSetup;
    [RelayCommand] private void DismissGettingStarted() => GettingStartedDismissed = true;

    // what the chart is given
    [ObservableProperty] private ChartFrame? _frame;
    [ObservableProperty] private IReadOnlyList<ChartPolygon> _polygons = Array.Empty<ChartPolygon>();
    [ObservableProperty] private IReadOnlyList<ChartImage> _images = Array.Empty<ChartImage>();
    [ObservableProperty] private string _planText = "";
    [ObservableProperty] private string _planClass = "info";
    // layers
    [ObservableProperty] private bool _showFields = true;
    [ObservableProperty] private bool _showCoverage = true;
    [ObservableProperty] private bool _showStack = true;
    partial void OnShowFieldsChanged(bool value) => QueueRebuild();
    partial void OnShowCoverageChanged(bool value) => QueueRebuild();
    partial void OnShowStackChanged(bool value) => QueueRebuild();
    // the sky position under the pointer when the context menu was opened
    [ObservableProperty] private double _contextRa;
    [ObservableProperty] private double _contextDec;

    public async Task StartAsync()
    {
        await Task.CompletedTask;
        ZoomToFrame();
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        UiThread.Post(() => { _rebuildQueued = false; Rebuild(); });
    }

    // ---- geometry ------------------------------------------------------------------------------------------------

    private static readonly Color[] FieldColors =
    {
        Color.FromRgb(120, 220, 255), Color.FromRgb(255, 140, 200), Color.FromRgb(160, 255, 140), Color.FromRgb(255, 170, 90), Color.FromRgb(200, 160, 255),
    };

    /// <summary>The area's own axes (x along its width, y along its height) to a sky position: the mapping the imaging service uses
    /// (<see cref="PlanProjection"/>), so what is drawn is what will be shot, also for large areas.</summary>
    private static (double Ra, double Dec) AreaToSky(double ra, double dec, double angle, double x, double y) => PlanProjection.ToSky(ra, dec, angle, x, y);

    private IEnumerable<Footprint> Fields(ImageViewModel.PlanScope scope) =>
        CoverageMap.Footprints(new Pose(0, 0, 0), scope.Frames, Image.PositionAngle);

    private static (double Ra, double Dec)[] Rect(double ra, double dec, double angle, double cx, double cy, double hw, double hh, double theta)
    {
        double c = Math.Cos(theta), s = Math.Sin(theta);
        return new[] { (-hw, hh), (hw, hh), (hw, -hh), (-hw, -hh) }
            .Select(p => AreaToSky(ra, dec, angle, cx + p.Item1 * c + p.Item2 * s, cy - p.Item1 * s + p.Item2 * c)).ToArray();
    }

    /// <summary>Redraws everything that depends on the form, the plan, the progress map and the stacked image.</summary>
    private void Rebuild()
    {
        if (!Image.TryCentre(out var ra, out var dec)) { Frame = null; Polygons = Array.Empty<ChartPolygon>(); Images = Array.Empty<ChartImage>(); PlanText = Image.PlanProblem; PlanClass = "error"; return; }
        double angle = Image.PositionAngle;
        var polys = new List<ChartPolygon>();
        var first = Image.PlanScopes.SelectMany(s => Fields(s).Take(1)).Cast<Footprint?>().FirstOrDefault();

        // the frame: the size asked for, or one shot of the first scope when it is a single frame
        double w = Image.Width, h = Image.Height;
        bool single = !(w > 0 && h > 0);
        if (single) { (w, h) = first is { } f0 ? (2 * f0.HalfWidth, 2 * f0.HalfHeight) : (1.0, 0.7); }
        Frame = new ChartFrame(ra, dec, w, h, angle, Image.Label, true);

        // each scope's own field at the centre, and the panels of a mosaic
        int panels = 1, cols = 1, rows = 1;
        int colour = 0;
        foreach (var scope in Image.PlanScopes)
        {
            var color = FieldColors[colour++ % FieldColors.Length];
            bool label = true;
            if (ShowFields)
                foreach (var f in Fields(scope))
                {
                    polys.Add(new ChartPolygon(Rect(ra, dec, angle, f.Cx, f.Cy, f.HalfWidth, f.HalfHeight, f.Theta), color, label ? scope.ScopeId : ""));
                    label = false;
                }
        }
        if (!single && first is { } fp)
        {
            double step = Image.Stepover > 0 ? Image.Stepover : Math.Max(0.02, 2 * Math.Min(fp.HalfWidth, fp.HalfHeight) * 0.85);
            // frame centres span the area less one frame; they are no further apart than the step, and evenly spread
            // (a field turned relative to the area covers more along one axis of the area and less along the other)
            double ex = Math.Abs(fp.HalfWidth * Math.Cos(fp.Theta)) + Math.Abs(fp.HalfHeight * Math.Sin(fp.Theta));
            double ey = Math.Abs(fp.HalfWidth * Math.Sin(fp.Theta)) + Math.Abs(fp.HalfHeight * Math.Cos(fp.Theta));
            double spanX = Math.Max(0, w - 2 * ex), spanY = Math.Max(0, h - 2 * ey);
            cols = spanX <= 1e-9 ? 1 : (int)Math.Ceiling(spanX / step - 1e-9) + 1;
            rows = spanY <= 1e-9 ? 1 : (int)Math.Ceiling(spanY / step - 1e-9) + 1;
            panels = cols * rows;
            if (panels > 1 && panels <= 400 && ShowFields)
                for (int i = 0; i < cols; i++)
                    for (int j = 0; j < rows; j++)
                    {
                        double x = cols == 1 ? 0 : -spanX / 2 + i * spanX / (cols - 1), y = rows == 1 ? 0 : -spanY / 2 + j * spanY / (rows - 1);
                        polys.Add(new ChartPolygon(Rect(ra, dec, angle, x, y, fp.HalfWidth, fp.HalfHeight, fp.Theta), Color.FromArgb(150, 255, 213, 79)));
                    }
        }
        Polygons = polys;

        // layers: the stacked image, and how far each part of the area has got
        var images = new List<ChartImage>();
        if (ShowStack && Image.Stack.Image is { } stack && Image.Stack.Wcs is { } wcs && Image.Stack.PixelsWide > 0)
        {
            double ph = Image.Stack.PixelsHigh; bool topDown = Image.Stack.TopDown;
            // picture pixel (x right, y down) to the stack's pixel: the first row of the picture is the top one, the last row of the data unless the file says otherwise
            (double, double) At(double px, double py) { var (r, d) = wcs.PixelToSky(px - 0.5, topDown ? py - 0.5 : ph - 0.5 - py); return (r / 15, d); }
            images.Add(new ChartImage(stack, At, 0.92));
        }
        double aw = Image.AreaWidthDegrees, ah = Image.AreaHeightDegrees;
        if (ShowCoverage && Image.Map is { } map && aw > 0 && ah > 0 && (Image.IsActive || Image.Phase is "Done" or "Aborted"))
        {
            double mw = Math.Max(1, map.Size.Width), mh = Math.Max(1, map.Size.Height);
            // picture pixel to the area's own axes (x along the width, y up the height)
            (double, double) At(double px, double py) => AreaToSky(ra, dec, angle, (px / mw - 0.5) * aw, (0.5 - py / mh) * ah);
            images.Add(new ChartImage(map, At, images.Count > 0 ? 0.30 : 0.55));
        }
        Images = images;

        // the plan in words
        if (Image.PlanProblem != "") { PlanText = Image.PlanProblem; PlanClass = "warn"; return; }
        var parts = new List<string>();
        parts.Add(single ? $"One frame of {Image.PlanScopes.FirstOrDefault()?.ScopeId ?? "the scope"}" : $"{Fmt(w)} × {Fmt(h)}");
        if (panels > 1) parts.Add($"{cols} × {rows} panels");
        int scopes = Image.PlanScopes.Count;
        if (Image.TargetMinutes > 0 && scopes > 0)
        {
            double minutes = panels * Image.TargetMinutes / scopes;
            string roughly = minutes >= 90 ? $"{minutes / 60:0.#} h" : minutes >= 1 ? $"{minutes:0} min" : "under a minute";
            parts.Add($"roughly {roughly} with {scopes} scope{(scopes == 1 ? "" : "s")}");
        }
        PlanText = string.Join("  ·  ", parts); PlanClass = "info";
    }

    private static string Fmt(double degrees) => degrees >= 1 ? $"{degrees:0.##}°" : $"{degrees * 60:0.#}′";

    // ---- the frame, moved on the chart ---------------------------------------------------------------------------

    /// <summary>The frame was dragged: the form follows.</summary>
    [RelayCommand]
    private void FrameEdited(FrameEdit e)
    {
        Image.CenterRa = Sexagesimal.Format(e.RaHours, 0);
        Image.CenterDec = Sexagesimal.Format(e.DecDegrees, 0);
        if (Frame is { } f)
        {
            // a single frame stays a single frame until it is resized
            bool resized = Math.Abs(e.WidthDegrees - f.WidthDegrees) > 1e-9 || Math.Abs(e.HeightDegrees - f.HeightDegrees) > 1e-9;
            // (the imaging service takes areas up to 60° on a side)
            if (resized || Image.Width > 0) { Image.Width = Math.Clamp(Math.Round(e.WidthDegrees, 3), 0.01, 60); Image.Height = Math.Clamp(Math.Round(e.HeightDegrees, 3), 0.01, 60); }
        }
        Image.PositionAngle = Math.Round(e.AngleDegrees, 1);
    }

    /// <summary>Back to one frame of the scope (no area).</summary>
    [RelayCommand] private void OneFrame() { Image.Width = 0; Image.Height = 0; }

    [RelayCommand]
    private void ZoomToFrame()
    {
        if (Frame is not { } f) return;
        Atlas.CenterRa = f.RaHours; Atlas.CenterDec = f.DecDegrees;
        Atlas.Fov = Math.Clamp(Math.Max(f.WidthDegrees, f.HeightDegrees) * 2.6, 0.5, 170);
    }

    // ---- the context menu ----------------------------------------------------------------------------------------

    [RelayCommand]
    private void FrameHere()
    {
        Image.CenterRa = Sexagesimal.Format(ContextRa, 0); Image.CenterDec = Sexagesimal.Format(ContextDec, 0);
        Image.Label = $"Field{ContextRa:0.0}{(ContextDec >= 0 ? "+" : "")}{ContextDec:0}".Replace(".", "_");
    }
    [RelayCommand] private void SelectHere() => Atlas.Select(new AtlasHitItem("Position", "Position", "", ContextRa, ContextDec, float.NaN, 0));
    [RelayCommand]
    private async Task GoHereAsync()
    {
        Atlas.Select(new AtlasHitItem("Position", "Position", "", ContextRa, ContextDec, float.NaN, 0));
        await Atlas.GotoCommand.ExecuteAsync(null);
    }
    [RelayCommand] private void CentreHere() { Atlas.CenterRa = ContextRa; Atlas.CenterDec = ContextDec; }

    private bool HasSelection() => Atlas.Selection is not null;

    /// <summary>The selected object becomes the frame (big ones an area, small ones one frame) and the chart zooms to it.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void FrameSelection()
    {
        Atlas.ImageThisCommand.Execute(null);
        QueueRebuild();
        UiThread.Post(ZoomToFrame);
    }
}
