# ELink — developer guide

How ELink is built, the rules it keeps, and how to add to it. For using it, see the [user guide](user-guide.md).

## The one rule

Modules never reference each other. They share **contract types** (`ELink.Contracts`) and **EVent IDs**, nothing else.
Every device, service and view model is a set of EVent endpoints on a mesh node. Two pieces of code that talk to each
other can sit in one process (EVent local loopback: a cheap in-process event bus) or on two machines, with no code change.

```mermaid
flowchart LR
    subgraph HW[Hardware]
        D1[INDI drivers<br/>indiserver]
    end
    subgraph Mesh[EVent mesh]
        direction LR
        B[IndiBridge<br/>Mount / Camera / Focuser ...]
        C[Compose<br/>Pointer · Shooter · SmartScope]
        A[Automation<br/>Image · Schedule · Solve · Centre · Focus · Live stack · Storage]
        S[Sky<br/>Atlas · Stellarium]
        U[UI<br/>Avalonia view models]
    end
    D1 -- INDI XML --> B
    B <-. events / dRPC .-> C
    C <-. events / dRPC .-> A
    A <-. events / dRPC .-> U
    C <-. events / dRPC .-> U
    S <-. events / dRPC .-> U
    B <-. events / dRPC .-> U
```

Dotted lines are mesh traffic, not references. `ArchitectureTests` fails the build if `ELink.UI` references any backend
project.

## Design spec: autonomous imaging trains

The coupling ELink exists to remove is the one in Ekos / N.I.N.A., where one coordination layer drives guiding,
dithering, centring, focusing and flips for every train.

```mermaid
flowchart TD
    R["Request: this image of this part of the sky<br/>(area, depth, filters, output scale)"] --> Q[[work: spots still short of depth]]
    Q -->|pull| A[Smart scope A]
    Q -->|pull| B[Smart scope B]
    subgraph A_inside[inside each scope]
        G[guiding · dithering] --- C[centring] --- F[focus triggers] --- M[meridian flip] --- S[own safety reactions]
    end
    A --- A_inside
    A -->|frames| L[Live stack = the requested image]
    B -->|frames| L
    Site[(site · time · weather · dome<br/>shared facts, consulted)] -.-> A & B
```

- A smart scope (imaging train) is **autonomous**: everything that keeps its own frames good lives inside it.
- The only top-level request is "this image of this part of the sky". A single target is a small area; there is
  no separate "direct" or "mosaic" mode. Scopes pull work producer/consumer style and are **not** kept in sync.
- Shared facts (site, time, weather, dome) are services scopes consult. They never orchestrate scopes.

`ImagingService` is that request; `Observe` stays as the scope's own one-target primitive the request uses.

## Projects

```mermaid
flowchart TD
    Contracts[ELink.Contracts<br/>NOTES types + IDs]
    Core[ELink.Core<br/>node, RemoteState, StatePublisher,<br/>Commands, astro math]
    Indi[ELink.Indi<br/>INDI protocol client]
    Imaging[ELink.Imaging<br/>FITS, stretch, stars]
    Bridge[ELink.IndiBridge]
    Compose[ELink.Compose]
    Auto[ELink.Automation]
    Atlas[ELink.Atlas]
    Stell[ELink.Stellarium]
    UI[ELink.UI]
    Core --> Contracts
    Bridge --> Core & Indi
    Compose --> Core
    Auto --> Core & Imaging
    Atlas --> Core
    Stell --> Core
    UI --> Core & Imaging
    Station([elink · Station]) --> Bridge & Compose & Auto & Atlas & Stell & UI
    App([elink-ui · App]) --> UI
    BridgeExe([ELink.Bridge exe]) --> Bridge
```

`ELink.Indi` knows nothing of EVent; `ELink.Imaging` knows nothing of either. Executables are the only place where
backends meet, and they meet only by being started on the same node.

## ID scheme

| pattern | example | kind |
|---|---|---|
| `ELink.<Kind>.<Id>.State` | `ELink.Mount.Telescope_Simulator.State` | event: state, fired on change |
| `ELink.<Kind>.<Id>.GetState` | `ELink.Camera.CCD_Simulator.GetState` | function: current state for late joiners |
| `ELink.<Kind>.<Id>.<Command>` | `ELink.Pointer.eq6.Goto` | function: command, returns `CommandResult` |
| `ELink.Shooter.<Id>.Shot` | `ELink.Shooter.main.Shot` | event: a frame (`ShotEvent`, FITS bytes + pointing) |
| `ELink.Automation.<Service>.*` | `ELink.Automation.Mosaic.Start` | service commands and state |
| `ELink.Devices.Announce` / `.List` | | device directory |
| `ELink.Indi.<server>.<device>.<property>` | | generic mirror of every INDI property |

IDs live as constants or helpers next to their types in `ELink.Contracts` (`EquipmentIds`, `PointerIds`, `ShooterIds`,
`ImagingIds`, ...). Never type an ID string anywhere else.

## The state + command pattern

Every endpoint (device, scope, service) has the same shape, built from three helpers in `ELink.Core`:

```mermaid
sequenceDiagram
    participant VM as Client (view model / service)
    participant N as Mesh
    participant P as Provider (device / service)
    Note over P: StatePublisher<T> dedupes and fires on change
    VM->>N: RemoteState<T>: hook X.State
    VM->>N: call X.GetState (seed)
    N->>P: GetState
    P-->>VM: current T
    VM->>N: Commands.CallAsync(X.Goto, args)
    N->>P: Goto
    P->>N: fire X.State (Slewing)
    P-->>VM: CommandResult Ok
    P->>N: fire X.State (Tracking)
    N-->>VM: state follows
```

- **`StatePublisher<T>`**: holds the last state, fires the event only when it changed, answers `GetState`.
- **`RemoteState<T>`**: hooks the event and seeds itself with `GetState`, so a late joiner is never blind.
- **`CommandSet` / `Commands.CallAsync`**: register command functions; calling one folds every provider's dRPC answer
  into one `CommandResult` (no provider = not ok).
- Publish the new state **before** returning "accepted", so a caller that waits on state never sees the old one.

EVent typed events are acknowledged synchronously: a slow subscriber stalls the publisher. Keep handlers short; hand
heavy work (solving, stacking, saving) to a queue.

## Composition

A *Pointer* is anything that can be pointed at the sky, a *Shooter* anything that takes frames. An **imaging
train** is one optical path: optics (focal length, aperture), one or more cameras (each `Imaging`, or `Guiding` for
an off-axis guider behind the same optics), filter wheel, focuser, rotator. A train is a Shooter (all its imaging
cameras at once); its guiding camera is the Shooter `<train>-guide`. It hands its focal length to its cameras
(INDI `SCOPE_INFO`, so frames carry `FOCALLEN`) and publishes each camera's pixel scale and field.

A **SmartScope** is `pointers[] + trains[]` (any number per mount, each with an offset) plus *what it guides with*:
another train (a guide scope), a train's `-guide` camera (an OAG), or nothing. It is registered as both a Pointer and
a Shooter under its own id, so scopes nest.

```mermaid
flowchart TD
    Rig[Scope 'rig'<br/>Pointer + Shooter<br/>guides itself]
    EQ[MountPointer 'eq'] --> M1[(Mount)]
    Main[Train 'main' 800 mm] --> C1[(Camera)] & W[(Wheel)] & F[(Focuser)]
    Main --> OAG[(OAG camera<br/>role Guiding)]
    Wide[Train 'wide' 250 mm] --> C2[(Camera)]
    GS[Train 'guidescope' 200 mm] --> C3[(Camera)]
    Rig --> EQ
    Rig -->|images with| Main & Wide
    Rig -.->|guides with one of| OAGS["main-guide (the OAG)"] & GS
```

The scope **centres itself** when asked (`CenterAfterSlew`): before its first exposure round after a slew it solves a
frame of its primary (or chosen) shooter and re-aims by the error (aim-off, no sync), detects frames taken before a
move showed, and learns a pointing correction applied to later slews within 15°.

The scope **refocuses its trains** itself: before an exposure round, each train with a focuser whose trigger fired
(start, time, temperature, filter, star growth from the HFR of its frames) is focused through
`AutofocusIds.RunAndWait` with its first imaging camera; runs on different focusers go on in parallel. Frames taken
while a round is prepared (centring, focus) are not relayed as the scope's frames.

The scope also does its own **meridian flip**: it follows its site's sidereal time for the target's hour angle and
its pointer's pier side (ASCOM/INDI: West = looking east, before the flip). Before each exposure round it waits for
the flip point if the round would cross it, then re-gotos the target and checks the pier side turned to East; a timer
does the same while it idles on target. Guiding is stopped first and recalibrates on the new side.

The scope owns a guider built from its guide settings (`<scope>-guider`; output Pulse to its mount's guide port
unless another port is named, or Correction to a target). A train cannot image and guide for the same scope.

`Observe` = go to, wait until every pointer settles, expose every shooter N times. Each `ShotEvent` carries the leaf
shooter id, the J2000 pointing and the `ObjectName` / `PlanId` of the request, so consumers never have to guess what a
frame belongs to. Shooters have offsets (arcmin east/north of the pointing axis); the primary shooter is the one centred.

## Services

| service | listens to | drives | notes |
|---|---|---|---|
| Autofocus | shooter shots | focuser | HFR per position, parabola fit on HFR², coarse + fine |
| Scheduler | site (dark, Moon, altitudes) | the image request | best possible image now, stops what becomes impossible, night after night |
| Storage | any shooter's shots | disk | night folders, FITS headers, JSON-lines log |
| Imaging | scopes' trains | any number of scopes | one image of an area, work shared between scopes (below) |
| PlateSolve | — | shooter (optional) | wraps `solve-field`, one solve at a time |
| Centering | mount state (incl. hand-controller slews) | mount, PlateSolve | sync + reslew, aim-off fallback, guide→primary offset |
| Guider (compose) | its guide camera's shots | GuidePort or GuideTarget | calibration, multi-star guiding, dithering, settle; owned by a scope |
| LiveStack | any shooter's shots | PlateSolve, Calibration (optional) | calibrates raw frames, registers them, resamples into a fixed sky grid |
| Calibration | a camera shooter's shots | that shooter | master darks/biases/flats (min/max-rejected mean, flat auto-exposure), `Find` the master for a light frame |
| Site | a GPS (optional) | mounts (location, time) | sidereal time, twilight, Sun/Moon/planets, observability with a horizon profile |
| Atlas | — | — | KStars stars, OpenNGC, GSC, constellations, search |
| Stellarium | a pointer | pointer | telescope protocol server + Remote Control client |

### The image request (fill an area)

```mermaid
flowchart LR
    R{{ImagingRequest<br/>area · depth · scopes}} --> M[(shared coverage map<br/>seconds per spot)]
    M --> PA[planner A<br/>A's frames] & PB[planner B<br/>B's frames]
    PA -->|next spot when A is free| A[Scope A · Observe 1 shot<br/>+ random dither]
    PB -->|next spot when B is free| B[Scope B · Observe 1 shot<br/>+ random dither]
    A & B -->|real footprint replaces the planned one| M
    A & B -->|frames| L[Live stack = the image]
```

`ImagingService` resolves each scope's frames from its imaging trains (published fields, shooter offsets, nested
scopes), and gives each scope a `CoveragePlanner` (raster passes + greedy top-up) over one shared map: a planner paints
the map when it hands out a spot, so the others skip it. After a shot the planned footprint is swapped for the real,
dithered one; a failed shot gives its spot back. An area that fits in a frame is shot centred every time. Scopes are
never synchronised; a broken scope fails alone and the others finish.

### Centring loop

```mermaid
stateDiagram-v2
    [*] --> Watching
    Watching --> Waiting: mount Slewing to a new target<br/>(not our own, > 2′ from last)
    Waiting --> Solving: slew ends (Tracking / Idle)
    Solving --> Centered: error ≤ tolerance
    Solving --> Correcting: error > tolerance
    Correcting --> Solving: sync + reslew<br/>(or aim off if syncs do not help)
    Solving --> Failed: max tries
    Centered --> Watching
    Failed --> Watching
```

The guide camera is aimed at `target − offset` (offset negated across a pier flip), so the primary lands on target.

### Live stacking

```mermaid
flowchart LR
    Shot[Shot events] -->|non-blocking| Q[[bounded queue]]
    Add{{Add: FITS pushed in}} --> Q
    Q --> Reg[register<br/>own WCS · Solve dRPC · pointing]
    Reg --> RS[LiveStacker.Add]
    RS --> Acc[(sum + weight<br/>float grids)]
    Acc --> Get{{GetImage: FITS + WCS,<br/>area-reduced to a max size}}
```

`LiveStacker` (in `ELink.Imaging`, no EVent) owns the output grid, a `TanWcs` built from the requested centre,
angle and scale. Each frame is mapped through the **exact homography** between its TAN WCS and the grid (two
gnomonic projections of one sphere are related by a plane homography), so there is no per-pixel trigonometry.

| ratio r = output scale / frame scale | how |
|---|---|
| r < 1 (output finer) | sample the frame with Catmull-Rom bicubic (or bilinear / nearest) |
| 1 ≤ r < 2 | average ⌈r⌉² bilinear samples spread over each output pixel |
| r ≥ 2 | box-bin the frame by ⌊r⌋ first (exact area average, WCS follows), then as above |

Raw one-shot-colour frames go through `Debayer` (`ELink.Imaging`) before resampling: *SuperPixel* turns each 2×2
cell into one RGB pixel and the frame's WCS follows with `LiveStacker.Binned(wcs, 2)`; *Interpolated* is a bilinear
demosaic on the same grid (the mean of same-colour pixels in the 3×3 neighbourhood, exact for linear data). The raw
frame is what gets plate solved. The stacker is planar with 1 or 3 channels, fixed by its first frame; mono frames
go into a colour stack as grey and colour frames into a mono stack as luminance.

Partial edge pixels add a fractional weight, the mean is sum/weight, and each frame's background median is matched
to the first frame's, per channel. Memory is 8 bytes per output pixel (16 in colour), capped by `MaxMegapixels`.

### Guiding (inside the scope)

A `Guider` (composition layer, like pointers and shooters) loops a guide camera, follows up to a dozen stars
(median shift, so one lost star does not matter) and drives one of two outputs:

```mermaid
flowchart LR
    Cam[guide camera<br/>any Shooter] -->|frames| G[Guider<br/>multi-star tracker]
    G -->|Pulse output| P["GuidePort.Pulse<br/>(INDI TELESCOPE_TIMED_GUIDE_NS/WE)"]
    G -->|Correction output| T["GuideTarget.Correct<br/>'you are here, should be here'"]
    G -.->|every frame, either mode| E((Guider.Correction event))
    G -.-> S((Guider.Step event))
    Scope[Smart scope] -->|Start · Stop · Dither / settle| G
```

- **Pulse**: calibrated like PHD2 (West leg, back, Dec backlash take-up, North leg, back) into a `PulseCalibration`
  (pixels per ms for each axis; RA rate scaled by cos Dec). Recalibrates after a pier flip.
- **Correction** ("goto guiding"), for ELink-native devices that close the loop themselves: `GuideCorrection` carries
  `IsRa/IsDec` and `ShouldRa/ShouldDec` (J2000, absolute when the first guide frame was plate solved) and the move
  `MoveEastArcsec/MoveNorthArcsec`. A native mount implements `ELink.GuideTarget.<id>.Correct`; anything else may
  just listen to `ELink.Guider.<id>.Correction`.
- **Ownership**: the scope stops its guider before any slew (its own or a parent scope's `Goto`), starts it once its
  pointers settle, and before every exposure round asks it to settle or, every `DitherEvery` rounds, to dither.
  `Expose` on a guided scope answers "accepted" and exposes once settled, so a parent scope never knows.

## Coordinates

All mesh coordinates are **J2000** unless a field says otherwise. INDI mounts speak JNow (`EQUATORIAL_EOD_COORD`);
the mount adapter and `Precession` (IAU 1976) convert at the edge. `ELink.Core.Astro` has the shared math: `Sky`
(separation, offsets), `Gnomonic` (tangent plane), `SkyProjection` (stereographic, north up, east left),
`Sexagesimal`, `FieldOfView`, `Coverage`, `Horizon` (sidereal time, alt/az, rise/transit/set finder, horizon
profiles) and `SolarSystem` (Sun: Meeus 25; Moon: main terms of Meeus 47; planets: JPL approximate elements;
topocentric parallax), all checked against Meeus' worked examples and real 2024-25 events.

## Adding things

**A device kind** (say a light panel):
1. Contract in `ELink.Contracts/Equipment`: a NOTES state type and an ID helper class over `EquipmentIds`.
2. Adapter in `ELink.IndiBridge/Adapters` mapping the *standard* INDI properties (the ones Ekos uses), registered in
   `IndiEquipmentManager` by `DRIVER_INTERFACE` bit.
3. A panel view model in `ELink.UI` that only uses `RemoteState` + `Commands`.
4. An integration test against the INDI simulator.

**A service**: a class taking a `TypeSafeEVentNode`, a `StatePublisher` for its state, a `CommandSet` for its commands,
`StartAsync` / `DisposeAsync`. Host it in `ELink.Station/Program.cs`. Talk to equipment only through its IDs.

**A NOTES type**: subclass the binary-convertible base with a static `NOTESDescriptor`; register every field. Do not
name a field `Name` (it collides with the type-name override). `.Range` takes decimals (`0m, 100m`).
Binary payloads use `RawBytes`.

## Building and testing

```bash
./dev.sh build
./dev.sh test --nologo
./dev.sh test tests/ELink.Tests --filter "FullyQualifiedName~Mosaic"
```

`dev.sh` disables MSBuild node reuse and the shared compiler: resident build servers pile up to gigabytes on this
machine. Always build through it.

- Integration tests start real `indiserver` simulators with a throwaway `HOME` and their own socket (`-u`); tests that
  move a mount or depend on its pointing start their own server rather than share the `indiserver` collection fixture.
- Ports come from `ELink.Testing.TestPorts`.
- Plate-solve tests run when `solve-field` is found (`ELINK_SOLVE_FIELD`, `~/.local/astrometry/bin/solve-field`, PATH).
- Live Stellarium tests: `ELINK_LIVE_STELLARIUM=1`.
- UI tests run Avalonia headless and assert on rendered pixels.

### Gotchas

- `TypeSafeEVentNode`'s constructor blocks on its own async start-up; on a busy synchronization context (the UI thread)
  that deadlocks. Always create nodes with `ElinkNode.Create` / `CreateAsync`, which build them on a pool thread.
- The INDI CCD simulator renders where the mount *reports* it is, plus guide-pulse offsets; syncs do not change that.
- The CCD simulator is also a filter wheel: wait on exact device ids, not on "a wheel appeared".
