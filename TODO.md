# ELink — EVent-based astronomy control stack

C# / .NET 8, Avalonia UI, EVent (dRPC mesh) as the only inter-module glue.
Principle: every piece of equipment and every logic block is an EVent endpoint set. No module
references another module directly; they share only the contract (type) assembly and event IDs.

## Architecture sketch

- `ELink.Contracts`   NOTES types + ID naming for equipment (Camera, Focuser, Wheel, Rotator, Dome,
                      Weather, GPS, LightPanel, DustCover, Pointer/Mover). Capability based, not "mount based".
- `ELink.Core`        Node bootstrap, discovery, device registry (from the network directory), helpers.
- `ELink.Indi`        INDI wire client. Pure protocol, no EVent, no UI.
- `ELink.IndiBridge`  INDI<->EVent translation: generic property mirror + typed device adapters.
- `ELink.Bridge`      Executable hosting the bridge(s): connects to indiserver(s), publishes to EVent.
- `ELink.Compose`     "Pointer" abstraction (anything that can point to a coordinate: mount, dome+mount,
                      alt-az, a smart scope) + "Shooter" (anything that can take frames) + SmartScope + groups.
- `ELink.Imaging`     FITS, WCS, live stacker, stretch, star detection (no EVent).
- `ELink.Automation`  Image requests, autofocus, sequencer, plate solve, centring, live stack, storage services.
- `ELink.Atlas` / `ELink.Stellarium`  Sky atlas service, Stellarium bridge.
- `ELink.UI` / `ELink.App`  Avalonia UI (MVVM), talks to the mesh only.
- `ELink.Station`     All-in-one executable (`elink`). See docs/development.md for diagrams.
- `tests/`            Unit tests (in-memory transport) + integration tests against INDI simulators.

ID scheme: `ELink.<Kind>.<DeviceName>.<Thing>` e.g. `ELink.Camera.CCD_Simulator.Exposure`,
`ELink.Telescope.Telescope_Simulator.Slew` (events = state changes, functions = commands/queries).

## MVVM over EVent (hard rule)

- View (Avalonia) <-> ViewModel <-> Model are linked **only** by EVent: ViewModels fire events / call `void`-style dRPC
  functions for commands and hook events for state. No project reference from `ELink.App` to any backend module
  (and none the other way); both reference only `ELink.Contracts` (types + ID constants). Each side only knows
  "that peer runs EVent".
- Backend modules, bridge, composition, sequencer and UI can run in one process (EVent local loopback = cheap local
  events) or split over TCP with zero code change. Use local events freely inside a process too.
- Backend publishes *state events* (retained-state query via a function so late-joining UIs can sync) and exposes
  *command functions*. ViewModels hold no logic about devices, they just mirror state and send intents.
- Test: UI app must run with the backend absent (shows "no devices") and backend must run headless.

## Design spec: autonomous imaging trains (hard rule)

- A smart scope (imaging train) is **autonomous**: guiding, dithering, centring after slews, autofocus triggers,
  meridian handling and its own safety reactions live *inside* the scope. Nobody above it drives these.
- The only top-level request is **"I want this image of this part of the sky"**: an area (centre, size, angle), a
  depth (exposure per spot, filters), an output pixel scale. There is no "direct target" vs "mosaic" special case:
  one target is just a small area.
- Scopes take producer/consumer work from that request and fill the frame; several scopes may contribute to one
  request and they are **not** kept in sync with each other. The live stack is the request's resulting image.
- Shared physical facts (site, time, weather, dome) are services scopes may consult; they never orchestrate scopes.

Roadmap order (agreed): site and time -> guiding/dithering in the scope -> meridian flip in the scope -> safety ->
polar alignment -> "fill this area" request replacing Observe/Mosaic special cases + scheduler -> calibration ->
focus triggers/offsets -> live-stack rejection/calibration -> the rest.

## TODO

### Phase 0 — Setup
- [x] Solution layout (UI references no backend; enforced by a test), .NET 8, EVent nupkg local feed
- [x] git, .gitignore, README, public GitHub repo, pushed after every milestone
- [x] Build/test script without resident build servers (`dev.sh`); tests start their own indiserver simulators
- [x] Docs: user guide and developer guide (`docs/`)

### Phase 1 — INDI <-> EVent translation layer (FIRST TARGET)
- [x] Read INDI dev guide / protocol spec
- [x] INDI XML protocol client in C# (framer + codec + live model, tested vs real indiserver)
      (defXXXVector, setXXXVector, newXXXVector, delProperty, getProperties, message, enableBLOB)
- [x] INDI property model (immutable snapshots)
- [x] BLOB handling (base64, .z decompress, enableBLOB)
- [x] Generic layer (static typed IndiProperty/Set/Snapshot/Blob contracts; any driver works) — originally planned as NON dynamic objects; static types are simpler and UI-friendly
      (`ELink.Indi.<server>.<device>.<property>` events + `.Set` functions) — works for any driver
- [x] Typed layer (Mount, Camera, Focuser, FilterWheel, Rotator, Dome, Weather, GPS done; LightPanel/DustCover pending): standard-property mappers (CONNECTION, EQUATORIAL_EOD_COORD, ON_COORD_SET, TELESCOPE_*,
      CCD_EXPOSURE, CCD_FRAME/BINNING/TEMPERATURE/COOLER, CCD1 BLOB, ABS_FOCUS_POSITION / REL_FOCUS,
      FILTER_SLOT, ABS_ROTATOR_ANGLE, DOME_*, WEATHER_*, GEOGRAPHIC_COORD, TIME_UTC, ...) to ELink.Contracts types
- [x] Capability detection: device interface bitmask (DRIVER_INFO) -> which EVent endpoints to publish
- [x] Reconnect/lifecycle: indiserver drops, device add/remove, property re-sync, backpressure
- [x] Bridge executable (`dotnet run --project src/ELink.Bridge -- --indi localhost=sim`): config for multiple indiservers, namespace per server
- [x] Integration tests against INDI simulators: telescope, ccd, focuser, wheel, rotator dome, weather, gps (guide pending)
- [ ] Reverse direction (stretch): expose EVent-described devices as an INDI server

### Phase 2 — Contracts and device model
- [~] Finalise capability contracts (first versions in place) (what a Camera/Focuser/... is in EVent terms), versioning rule (new ID per type change)
- [ ] Device registry built from the network directory (what exists, what capabilities, liveness)
- [ ] Native (non-INDI) device sample, to prove the model is not INDI shaped

### Phase 3 — Composition
- [x] Pointer capability (contract + MountPointer glue; dome-slaved/others pending): GoTo(coord), Sync, Abort, Park, Tracking, state events (mount, alt-az, dome-slaved, etc.)
- [x] Shooter capability (contract + CameraShooter glue with filter wheel): Expose, ROI/binning/gain, frame events (image + metadata)
- [x] SmartScope = point-to + shoot-at wrapper: any pointer + any N shooters/focusers/wheels/rotators; own EVent identity
- [x] SmartScope-of-SmartScopes (arrays, multi-OTA per mount, multi-mount per target, wide + narrow, etc.)
- [x] DSLRs through INDI's gphoto/canon/nikon drivers: ISO (CCD_ISO), FITS transfer forced, sensor size handed over by the train (CCD_INFO); tested with a fake DSLR driver under indiserver
- [ ] DSLR: native raw (CR2/NEF) reading via LibRaw, mirror lock, capture target
- [x] Imaging trains (optics, cameras with Imaging/Guiding roles, wheel, focuser, rotator; focal length pushed to cameras; per-camera scale and field); scopes with any number of trains per mount and inline guiding with any train or OAG camera
- [x] Train fields of view used directly by the image request
- [x] Composition at run time over EVent (Define/Remove/Snapshot) + JSON persistence
- [x] Per-shooter offsets (primary shooter is centred on target); pointing model hooks still open

### Phase 4 — Automation
- [x] Plate solve service (astrometry.net, unpacked in user space; any FITS or a shot from any Shooter)
- [x] ASTAP as a second plate solver (tried first with a position hint, fallback either way); not yet run against a real ASTAP install
- [x] Automatic centring after every slew (also hand controller / other software): solve the guide camera, sync + reslew (aim off when syncs do not help), learned guide-to-primary offset per pier side
- [x] Autofocus (HFR star measurement, hyperbola/parabola fit, coarse + fine sweeps) as a mesh service
- [x] Live stacking into a fixed field (e.g. the mosaic's virtual FOV) at any pixel scale: WCS/solve/pointing registration, exact homography resampling, bicubic upscaling, area-averaged downscaling, sky-level matching, float FITS + WCS out
- [x] Debayering of one-shot-colour frames (BAYERPAT + offsets or a given pattern): interpolated and super pixel; colour stacks and colour previews
- [x] Live stack: running outlier rejection, flux matching between frames/cameras, one stack per filter, background and colour balance
- [x] Calibration library: darks, biases and flats per camera combined into masters (min/max-rejected mean), flats find their own exposure and have the bias taken off, matching by camera, size, binning, exposure, gain/ISO, temperature, filter; Calibration page
- [x] Live stack: darks and flats from the library applied to every raw frame before debayering
- [x] Image request (replaces the mosaic and single-target special cases): an area to a depth, any number of scopes pulling work from one shared coverage plan with their own train fields, dither on every shot, failed scopes fail alone, live stack of all frames
- [x] Images kept across nights: coverage and live stacks saved under the image's name and carried on; list/forget kept images
- [ ] Image request: learn each train's camera angle from solves (planning assumes 0 without a rotator); per-scope filters; rotator passes again
- [x] Guiding inside the scope: multi-star guider, PHD2-style calibration over INDI pulse guiding, dithering and settling driven by the scope itself; "goto guiding" output (GuideCorrection: you are here, should be here) for ELink-native devices
- [x] Frame grading inside the scope (stars, HFR, elongation, sky vs the camera's recent good frames): rejected frames do not count toward depth, are not stacked, are filed apart; clouds make the scope wait
- [x] Autofocus inside the scope: triggers (start, time, temperature, filter change, star growth) per train with a focuser; autofocus runs per focuser in parallel; RunAndWait
- [x] Focus: per-filter offsets (the train moves its focuser on a filter change; no refocus for that)
- [ ] Focus: temperature compensation between runs
- [x] Cameras in trains: gain/offset presets (INDI CCD_GAIN/CCD_OFFSET or CCD_CONTROLS), cooling ramps to a set point and warm-up, the scope waits until cold
- [ ] Guiding extras: PHD2 as an alternative guider behind the same contract, guide graph history, Dec backlash compensation, predictive PEC
- [x] Centring inside the scope: solve after its own slews, aim-off re-goto until within tolerance, stale-frame check, learned pointing correction nearby
- [x] Meridian flip inside the scope: hour angle from the site, pier side from the mount, waits for the flip point between exposures, verifies the turn-over, flips idle scopes too
- [x] Scheduler of image requests (priority, altitude/horizon, darkness, Moon distance and brightness, time windows; stops what becomes impossible; carries on night after night); replaces the old Observe-block sequencer
- [x] Camera angles learned from plate solves and used for planning
- [x] Equipment profiles: ELink runs the INDI drivers (own indiserver with a control pipe), connects devices, restarts a dying server; driver catalogue; --profile; Rig › Drivers
- [ ] Dome interlock
- [x] Frame storage (FITS headers stamped, night folders, JSON-lines session log)

### Phase 4b — Sky
- [x] Focusers through the full standard INDI focuser interface (Ekos parity; EAF-capable INDI drivers work as is)
- [x] Sky atlas service (KStars stars, OpenNGC, constellations, GSC faint stars, search) and interactive chart
- [x] Stellarium bridge: telescope server for any pointer (verified live), Remote Control show/selection
- [x] Site and time service: location (manual or GPS, clock check), horizon profile, sidereal time, twilight, Sun/Moon/planets, observability; pushes location/time to mounts
- [x] Atlas: horizon overlay, Sun/Moon/planets, planet search, visibility of the selection
- [ ] Atlas: comets/asteroids, satellites; survey imagery behind the chart

### Phase 5 — Avalonia UI
- [x] ViewModel base classes bound to EVent (state hook -> property, command -> function call), no backend refs
- [x] App shell, mesh connection/discovery panel, device browser (from directory + descriptions)
- [x] Generic property panel (works for any NON endpoint), then typed panels: mount, camera, focuser, ...
- [x] Smart scope composer (form based; drag/compose canvas later) (drag/compose), live image viewer (FITS stretch), sequence editor

### Phase 5b — UI redesign (docs/ux-review.md)
- [x] Four views (Sky, Scopes, Rig, Advanced) instead of twelve tabs; status strip (night, a chip per scope, what is left to set up); device panels in a drawer; toasts
- [x] Sky: one chart with the image frame (drag, resize, turn), each scope's field, mosaic panels, progress, the stacked picture, the queue, kept images, Stellarium
- [x] Large areas: one plan-to-sky mapping for the service, the chart and the stack; great-circle edges; rasters in tiles
- [x] Set up: add/edit/remove telescopes and scopes, pointers made for you, labelled camera settings, explained fields
- [x] Greyed-out actions that cannot work, messages that say what is wrong next to where it went wrong
- [x] Night timeline on the Site page; the horizon drawn as a picture (the flat horizon: altitude against azimuth, with the Sun, Moon, planets and the paths of the target, the image and the scopes; a strip on the Sky chart and a card on the Site page)
- [x] Per-scope colour (status strip, cards, chart fields) and a short log of what each scope did tonight
- [x] Glossary / help for the terms (scope, telescope, depth per spot, panel, focus offset, pseudo mono)
- [x] Plate solves of a camera's angle get the camera's pixel scale as a hint
- [x] Pseudo mono: a colour camera takes turns to focus R, G, B (focus offsets); the in-focus channel of each frame is stacked as colour, the other two as luminance (weight 0 = left out); storage writes `infocus/` and `oof/`
- [x] Pseudo mono: autofocus per channel (G, then R and B from green's focus; the offsets R and B are measured and kept with the telescope)
- [x] Pseudo mono: mono outputs: the out-of-focus luminance alone, or the in-focus colours added up (the picture selector)
- [x] Layers: the coverage map has N channels; each layer has a filter, a pixel-scale range and a depth; scopes plan for the layers their cameras feed; one live stack per layer
- [x] Layers: a combined output (the coarse base with each finer layer's detail laid over it); it is the default picture when there are layers
- [ ] Manual mode: most things should work without the automation (no mount control, no focuser, no filter wheel, no solver). Done: pseudo mono with a hand-focused camera (say which colour is in focus), the focus helper. To do: a walk through every feature for what it needs and how it behaves without it (stacking and layers from frames of a hand-guided mount, a manual filter wheel that asks for the filter, a scheduler that only advises)
- [x] Focus helper: statistics for hand focusing (HFR, roundness, per-colour for colour cameras, trend, best, history graph)
- [x] Set up: scopes are not offered as things to put on a mount (only a scope that already combines others shows them)

## Notes / decisions
- EVent lives in `EVent/` (docs in `EVent/wiki`, skill in `EVent/skill`, JS/C++/C# leaves); packages are the local feed `EVent/nuget`. `PublishEvent` is fire and forget (ordered, no acks): use it for streams.
- INDI simulators available: ccd, telescope, focus, wheel, rotator, dome, weather, gps, guide, sqm, io, lightpanel, dustcover, receiver.
- Typed events in EVent are acknowledged synchronously: a slow subscriber stalls the publisher, so high-rate
  data (BLOBs, position streams) needs care (separate IDs / raw events / throttling).

## From the fresh-eyes review (docs/ux-fresh-review.md)
- [x] One vocabulary (mount / telescope / scope / image / panel / shot / exposure goal), no pointer / train / shooter in the UI
- [x] Quick set up (one mount, one camera: telescope and scope in one go); "next: add a scope" hint
- [x] Rig pages in the order of a first set-up; Site and the plate solver marked optional; readiness rows with an arrow; "Set up: N left"
- [x] Frames saved by default when an image starts (switchable), with a warning when they are not
- [x] Calibration shows only the fields of the chosen kind and says to cover the telescope; Site explains its horizon format; profile messages not red when they succeed
- [x] Manual tools grouped on Scopes; guiding test buttons folded away; mount drawer wraps instead of clipping; Yes/No instead of True/False
- [x] Equipment + Drivers are one page (Devices); a scope has one mount (choosing another unticks the first); a scrim dims the page behind a device panel; choosing a GPS fills the Site; "Fill in the filter names" for the focus offsets; layers are rows; the setup chip has a tooltip
- [x] Focus offsets are a table (a row per filter, steps as numbers; "Fill in the filter wheel's filters"); the INDI page listed the first property of the first device twice (a race when the device was auto-selected): fixed

## Picture first
- [x] Picture page: the live stack processed (gradient removal, automatic stretch, neutral sky, colour touches), looks, before/after, histogram, zoom and pan
- [x] A frame lands on the picture as a white shape that fades; the picture cross-fades when it updates
- [x] Linear stack saved unstretched (with every kept image, and on Save with the picture as a 16-bit PNG)
- [x] A little life: pages cross-fade, toasts slide in, a scope that is exposing breathes
- [ ] Processing ideas: noise reduction, star colour boost / star reduction, deconvolution, local contrast, crop, export of 8-bit JPEG for sharing
- [ ] The frame-landing flash on the Sky chart too; a full-screen "watch" mode; an animated start of a new night

## From real frames (a DSLR, 120 s subs, M 27, 2026-07-24; ~/Pictures)
- [x] Frames lined up by their stars when there is no solver or the WCS is only the mount's position; the brightness matching compares stars' light, not the brightest pixels
- [x] The picture leaves empty parts black (not sky-coloured), takes no part of them into its estimates and is cut to the part that has data
- [ ] The stack grid follows the frames' orientation (so a tilted camera does not leave half of it empty); drop the weakly covered edge (only a few frames deep) from the picture
- [ ] Flats: subtract a bias or dark flat first (the corners where the flat is almost black blow up), a per-colour flat normalisation, flats as a library master for DSLR raw frames
- [ ] Remaining vignetting and dust after calibration: a vignetting-aware gradient model (radial) beside the polynomial
- [ ] RealDataTests (ELINK_REAL_LIGHTS, ELINK_REAL_FLATS, ELINK_REAL_COUNT, ELINK_REAL_OUT): run them on your own frames
- [x] Real data, nastier (M 42, 30 s subs, no guiding): the frame grader judges against the camera's own usual elongation, measures colour frames by their cells, and takes a stable change of star size as the new normal
- [x] Stars-alignment handles frames of another pixel scale (same camera angle); header scale from the frame's own SCALE card
- [x] Star alignment for any turn and scale (triangle matching), against any placed frame; each frame carries its telescope's pixel scale and camera angle (ShotEvent), so two telescopes work without a plate solver
- [x] Layers from mixed telescopes on real data (M 42: 300 mm and 900 mm frames in one folder, the camera turned 13.4°, scale ×0.338): wide and detail layers stacked, 68 stars matched across telescopes
- [ ] Reject soft frames harder when the cause is the focus (a note on the Scopes page, and an autofocus when the scope can)

## Tonight and framing
- [x] Tonight's best: ranked suggestions for the dark hours at your site and your smallest frame; one click frames it
- [x] Frame from a picture: solve it, show it on the chart, make the image exactly one frame of it
- [ ] Tonight's best: remember what you imaged (kept images) and what is already deep; filters by kind; "add the top 3 to the queue"; targets from your own list; planets and comets
- [ ] Frame from a picture: a live version from a scope's latest frame ("frame like what this telescope sees now"); a few pictures on the chart at once

## Session log and the web (EVent 0.2: PublishEvent, file endpoint, web endpoint, JS leaf)
- [x] Session log: LogEntry events (`SessionLog.Info/Warn/Error/Note`), a service that writes one file per night through an EVent file endpoint (append over the mesh allowed, replace not), follows scopes/autofocus/centring/stack frames, Advanced › Session log page with notes and a copy-for-bug-report button
- [ ] Services should write their own important lines (errors, device lost, flips, guiding lost) with `SessionLog`, not only phase changes
- [ ] Log: filter by source/level, open the folder, attach the log to a saved night's folder
- [x] Web interface: the station serves a page (src/ELink.Station/web) with the JS leaf over WebSocketServer + WebEndpoint/FileEndpoint: image progress with pause/resume/stop, scope cards, live picture with landing flashes, session log with notes; mesh only, same contracts as the desktop UI
- [ ] Web interface: login (EVent auth) before it may be opened beyond loopback; mobile polish on a real phone; start an image from the page
- [ ] Stream-style events (live previews, property streams) moved to `PublishEvent`
