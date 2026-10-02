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
- [x] Imaging trains (optics, cameras with Imaging/Guiding roles, wheel, focuser, rotator; focal length pushed to cameras; per-camera scale and field); scopes with any number of trains per mount and inline guiding with any train or OAG camera
- [x] Train fields of view used directly by the image request
- [x] Composition at run time over EVent (Define/Remove/Snapshot) + JSON persistence
- [x] Per-shooter offsets (primary shooter is centred on target); pointing model hooks still open

### Phase 4 — Automation
- [x] Plate solve service (astrometry.net, unpacked in user space; any FITS or a shot from any Shooter)
- [x] Automatic centring after every slew (also hand controller / other software): solve the guide camera, sync + reslew (aim off when syncs do not help), learned guide-to-primary offset per pier side
- [x] Autofocus (HFR star measurement, hyperbola/parabola fit, coarse + fine sweeps) as a mesh service
- [x] Live stacking into a fixed field (e.g. the mosaic's virtual FOV) at any pixel scale: WCS/solve/pointing registration, exact homography resampling, bicubic upscaling, area-averaged downscaling, sky-level matching, float FITS + WCS out
- [x] Debayering of one-shot-colour frames (BAYERPAT + offsets or a given pattern): interpolated and super pixel; colour stacks and colour previews
- [ ] Live stack: outlier rejection (sigma clip), darks/flats, colour balance
- [x] Image request (replaces the mosaic and single-target special cases): an area to a depth, any number of scopes pulling work from one shared coverage plan with their own train fields, dither on every shot, failed scopes fail alone, live stack of all frames
- [ ] Image request: learn each train's camera angle from solves (planning assumes 0 without a rotator); per-scope filters; rotator passes again
- [x] Guiding inside the scope: multi-star guider, PHD2-style calibration over INDI pulse guiding, dithering and settling driven by the scope itself; "goto guiding" output (GuideCorrection: you are here, should be here) for ELink-native devices
- [ ] Guiding extras: PHD2 as an alternative guider behind the same contract, guide graph history, Dec backlash compensation, predictive PEC
- [x] Centring inside the scope: solve after its own slews, aim-off re-goto until within tolerance, stale-frame check, learned pointing correction nearby
- [x] Meridian flip inside the scope: hour angle from the site, pier side from the mount, waits for the flip point between exposures, verifies the turn-over, flips idle scopes too
- [x] Sequencer (plans of blocks, autofocus, pause/resume, weather interlock); [ ] scheduler/priorities, dome interlock
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

## Notes / decisions
- EVent package in `~/ELink/package` has no nuget/ folder; real feed is `~/Desktop/EVent/EVent/dist/EVent/nuget`.
- INDI simulators available: ccd, telescope, focus, wheel, rotator, dome, weather, gps, guide, sqm, io, lightpanel, dustcover, receiver.
- Typed events in EVent are acknowledged synchronously: a slow subscriber stalls the publisher, so high-rate
  data (BLOBs, position streams) needs care (separate IDs / raw events / throttling).
