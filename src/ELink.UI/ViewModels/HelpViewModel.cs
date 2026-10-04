namespace ELink.UI.ViewModels;

/// <summary>What the words mean, and the keys.</summary>
public sealed class HelpViewModel
{
    public sealed record Entry(string Term, string Meaning);

    public IReadOnlyList<Entry> Terms { get; } = new Entry[]
    {
        new("Telescope", "One optical path: the telescope or lens, the cameras behind it (imaging, or an off-axis guider), and the filter wheel, focuser and rotator in between. A guide scope is a telescope too."),
        new("Scope", "A mount carrying one or more telescopes, with everything it does by itself: pointing, centring after a slew, focusing, guiding, dithering, flipping at the meridian, cooling, throwing away bad frames. You ask a scope for images; you do not drive it."),
        new("Image", "An area of sky, how deep it should get and which scopes may work on it. One frame of your scope is just a small area; a mosaic is a bigger one."),
        new("Frame", "The yellow rectangle on the sky chart: the image you are planning. Drag it to move it, a corner to resize it, the round handle to turn it."),
        new("Panel", "One of the pointings a mosaic is made of. The chart draws them inside the frame and says how many there are."),
        new("Depth per spot", "How long every part of the image should be exposed in total (all shots added up). 0 means keep going until you stop it."),
        new("Turn", "Where the top of the image points, in degrees east of north."),
        new("Dither", "A small random shift between shots, so that hot pixels and noise do not line up. Scopes that guide dither through the guider; others shift the pointing."),
        new("Rejected frame", "A frame the scope judged bad (clouds, trailing, soft focus, bright sky). It is filed apart, not stacked, and the spot is shot again."),
        new("Live stack", "The picture built from all the frames as they arrive, placed on the sky. It is drawn on the chart where the image is."),
        new("Queue", "Images waiting for the right conditions (dark, high enough, away from the Moon). It takes the best one that is possible now and carries on night after night."),
        new("Plate solving", "Working out where a frame is on the sky from its stars. Needed for centring and for stacking frames that carry no sky position. ASTAP or astrometry.net."),
        new("HFR", "Half-flux radius: how large the stars are. Smaller is sharper; autofocus looks for the smallest."),
        new("Master dark / bias / flat", "Averages of many calibration frames, kept per camera. The live stack takes the dark that suits each frame off and divides by the flat."),
        new("Pointer", "What the software points: a mount. You rarely meet it: saving a scope makes the pointer for its mount."),
    };

    public IReadOnlyList<Entry> Keys { get; } = new Entry[]
    {
        new("Ctrl+1 … Ctrl+4", "Sky, Scopes, Rig, Advanced"),
        new("Esc", "Close the device panel, or the list of search matches"),
        new("+ / -  or the wheel", "Zoom the chart (the wheel zooms towards the pointer)"),
        new("F", "Zoom the chart to the image frame"),
        new("Right-click the sky", "Frame the image here, send the scope here, what is here"),
    };
}
