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
| `ELink.Imaging` | FITS reader, screen auto-stretch, star detection and HFR |
| `ELink.Automation` | autofocus and sequencer services (weather guard, pause/resume), driven only by EVent IDs |
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

In the UI: open equipment from the left, compose pointers/shooters/scopes on the **Compose** tab, run **Observe**
(point, wait until settled, take N exposures) on a scope's tab, or browse and edit any raw INDI property on the **INDI** tab.

## Developing

`./dev.sh build|test` wraps `dotnet` without resident build servers (they pile up and eat gigabytes).
The integration tests start real `indiserver` simulators in a throwaway `HOME`; they are skipped when it is not installed.
The roadmap is in [TODO.md](TODO.md).
