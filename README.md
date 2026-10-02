# ELink

An event-driven astronomy control stack (think Ekos / N.I.N.A.) built on **EVent**, a distributed event/dRPC mesh.
C# / .NET 8, Avalonia UI.

- **Everything is an EVent endpoint.** Equipment, smart scopes, the UI: no module references another; they share only
  the contract types and EVent IDs (`src/ELink.Contracts`). A test enforces that the UI never references a backend.
- **Not tied to a mount or one OTA.** A *Pointer* is anything that can be pointed at the sky, a *Shooter* is anything that
  takes frames. A **smart scope** is `pointers[] + shooters[]` ("point to, shoot at") and is itself a Pointer and a Shooter,
  so scopes compose into scopes: several OTAs on a mount, several mounts on a target, wide + narrow rigs, ...
- **MVVM over EVent.** View models mirror device state events and send commands as function calls. The UI runs in its own
  process, joined to the mesh, or inside the all-in-one station on the node's local loopback.

## Layout

| project | what |
|---|---|
| `ELink.Contracts` | NOTES types and ID helpers: equipment (Mount, Camera, Focuser, FilterWheel, Rotator, Dome, Weather, Gps), Pointer/Shooter/Scope, generic INDI mirror |
| `ELink.Core` | node helpers, `RemoteState` (follow a remote state), `StatePublisher`, `Commands`, astro math (precession, sexagesimal) |
| `ELink.Indi` | INDI XML protocol client with a live property model (no EVent inside) |
| `ELink.IndiBridge` | the INDI <-> EVent translation: generic mirror of every property + typed adapters per device kind |
| `ELink.Compose` | smart scopes, mount pointers, camera shooters, composition host with JSON persistence |
| `ELink.Atlas` | sky atlas service: KStars star catalogue, OpenNGC deep-sky objects, constellation figures, GSC faint stars, search |
| `ELink.Stellarium` | Stellarium bridge: ELink as a Stellarium telescope for any pointer, plus Remote Control (show target, read selection) |
| `ELink.Imaging` | FITS reader, screen auto-stretch, star detection and HFR |
| `ELink.Automation` | autofocus, sequencer (weather guard, pause/resume), mosaic scanner and frame storage services, driven only by EVent IDs |
| `ELink.UI` / `ELink.App` | Avalonia UI (library) and its stand-alone executable `elink-ui` |
| `ELink.Bridge` | headless INDI bridge executable |
| `ELink.Station` | all-in-one executable `elink`: bridge + composition host + UI on one node |

## Running

EVent is not on nuget.org: put `EVent.<ver>.nupkg` (and `EVent.Scripting`) into `nuget/` (a local feed, see `nuget.config`).

```bash
indiserver indi_simulator_telescope indi_simulator_ccd indi_simulator_wheel &      # or your real drivers
dotnet run --project src/ELink.Station -- --indi localhost:7624                    # everything in one process
# or as separate processes on one mesh:
dotnet run --project src/ELink.Bridge  -- --indi localhost --port 5698
dotnet run --project src/ELink.App     -- --host 127.0.0.1 --port 5698
```

**Mosaic (painting a virtual FOV):** give a virtual field of view, the frames the scope shoots (each with its own size, angle and offset, so heterogeneous OTAs work), a stepover and a target exposure per spot. The scope then slowly *paints* the area with exposure time. A coverage map accumulates the seconds each spot has received, summed over all frames' rotated footprints. The planner sweeps serpentine raster passes whose hop is set so one pass deposits a uniform slice of the target (a deep target means several light passes at small stepovers, with shifted lattices; a rotator can turn the scope between passes), then tops up what the passes left short. Every move is one single shot. A producer plans visits into a bounded queue, an executor turns the rotator and has the smart scope slew and shoot, and a recorder builds the real coverage map. `SetTarget` and `SetStepover` change a running scan. Stacking is out of scope: frames are saved with their pointing.

**Focusers:** every INDI focuser is driven through the standard INDI focuser interface, the same properties Ekos uses (absolute, relative and timed moves, abort, sync, reverse, backlash, max travel, speed, temperature), so a ZWO EAF on `indi_asi_focuser`, a Moonlite, or anything else INDI supports works without vendor code. What a focuser can do is detected from the properties its driver defines.

**Plate solving and centring:** astrometry.net's `solve-field` (system package, or unpacked in `~/.local/astrometry` without root; `ELINK_SOLVE_FIELD` overrides) solves any FITS or a fresh shot from any shooter. The centring service watches the mount's own target (`TARGET_EOD_COORD`), so a slew from a hand controller or other software counts as much as one from ELink: when it ends on a new target, the guide camera's frame is solved and the mount synced and re-slewed until within tolerance (or, for mounts whose syncs do not move the pointing, aimed off by the error). The guide scope and the primary need not be aligned: "Learn offset" solves both at one pointing; from then on the guide scope is aimed so the primary lands on the target, with the offset turned over after a meridian flip.

**Sky atlas:** an interactive chart (drag, wheel, click) built from the sky data KStars installs (`/usr/share/kstars`: about 43k stars to magnitude 8, 14k OpenNGC objects, constellation figures) plus faint stars from the GSC for small fields. Search (`M42`, `NGC 7000`, `Vega`, `andromeda`), see mounts where they point, send a pointer or scope to the selection, or make it the mosaic centre.

**Stellarium:** ELink is a Stellarium telescope. In Stellarium: Telescope Control → add → "External software or a remote computer", host `localhost`, port 10001 (`--stellarium-port`), equinox J2000. Stellarium then shows the bound pointer and its "slew to selection" (Ctrl+1) moves it. With Stellarium's Remote Control plugin enabled (port 8090, `--stellarium-remote`) the atlas can also show targets in Stellarium and take its selection. This was verified against a real (headless) Stellarium: `ELINK_LIVE_STELLARIUM=1 ./dev.sh test --filter StellariumLive`.

In the UI: open equipment from the left, compose pointers/shooters/scopes on the **Compose** tab, run **Observe**
(point, wait until settled, take N exposures) on a scope's tab, or browse and edit any raw INDI property on the **INDI** tab.

## Developing

`./dev.sh build|test` wraps `dotnet` without resident build servers (they pile up and eat gigabytes).
The integration tests start real `indiserver` simulators in a throwaway `HOME`; they are skipped when it is not installed.
The roadmap is in [TODO.md](TODO.md).
