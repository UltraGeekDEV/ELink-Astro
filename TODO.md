# ELink — EVent-based astronomy control stack

C# / .NET 8, Avalonia UI, EVent (dRPC mesh) as the only inter-module glue.
Principle: every piece of equipment and every logic block is an EVent endpoint set. No module
references another module directly; they share only the contract (type) assembly and event IDs.

## Architecture sketch

- `ELink.Contracts`   NOTES types + ID naming for equipment (Camera, Focuser, Wheel, Rotator, Dome,
                      Weather, GPS, LightPanel, DustCover, Pointer/Mover). Capability based, not "mount based".
- `ELink.Core`        Node bootstrap, discovery, device registry (from the network directory), helpers.
- `ELink.Indi`        INDI wire client + INDI<->EVent bridge (translation layer). Pure protocol, no UI.
- `ELink.Bridge`      Executable hosting the bridge(s): connects to indiserver(s), publishes to EVent.
- `ELink.Compose`     "Pointer" abstraction (anything that can point to a coordinate: mount, dome+mount,
                      alt-az, a smart scope) + "Shooter" (anything that can take frames) + SmartScope + groups.
- `ELink.Sequencer`   Event driven sequence/target engine over smart scopes.
- `ELink.App`         Avalonia UI (MVVM), talks to the mesh only.
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

## TODO

### Phase 0 — Setup
- [ ] Solution layout (App references Contracts + EVent only; enforced by a test), .NET 8, central package management, EVent nupkg local feed
- [ ] git init, .gitignore, README, public GitHub repo, push after every milestone
- [ ] CI-free build/test scripts (`build.sh`, `test.sh`); indiserver sim launcher script

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
- [x] Composition at run time over EVent (Define/Remove/Snapshot) + JSON persistence
- [x] Per-shooter offsets (primary shooter is centred on target); pointing model hooks still open

### Phase 4 — Automation
- [ ] Plate solve service (astrometry.net / ASTAP via EVent function), center-on-target
- [x] Autofocus (HFR star measurement, hyperbola/parabola fit, coarse + fine sweeps) as a mesh service
- [ ] Meridian flip, dithering, guiding (PHD2/INDI guider) as modules
- [ ] Sequencer / targets / scheduler, safety (weather, dome) interlocks
- [ ] Frame storage (FITS writer with headers), session log

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
