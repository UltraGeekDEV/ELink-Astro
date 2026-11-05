# Pi M 31 test rig

The tools used to test a headless ELink station on a Raspberry Pi 4 (2 GB) with the INDI simulators set up as the home
rig: 900 mm scope, 24 MP APS-C camera (6000 x 4000 RGGB, 3.9 µm), unguided mount, 30 s subs, M 31 at a 150 mm lens field
(8.97° x 5.95°). The Pi's address defaults to `192.168.137.48` (the Windows hotspot); set `PI=` to use another.
Everything a script writes goes to `out/` (ignored).

## On the Pi (`pi/`)

| file | what |
|---|---|
| `m31-indi.service` | indiserver on port 7625 with the telescope and CCD simulators, the GSC star catalogue (`GSCDAT`, `GSCBIN`) and a HOME of its own (`sim-home`, where the 24 MP sensor settings are saved) |
| `indi-ready.sh` | run after indiserver starts: connects both simulators and loads their saved settings (without it the CCD is a starless 1280 x 1024) |
| `m31-station.service` | the station from `~/elink/m31-test/pi-station`, mesh on 5699, web on 8090/8091, open to the network; MemoryHigh 1000M, MemoryMax 1150M |
| `m31-mon.service`, `mon.sh` | every 2 s: station memory (cgroup, anon, file), CPU seconds, INDI memory, available memory, SoC temperature, OOM kills, into `mon.log` |

Install: copy the units to `/etc/systemd/system/`, `mon.sh` and `indi-ready.sh` to `~/elink/m31-test/`, then
`sudo systemctl daemon-reload && sudo systemctl enable --now m31-indi m31-station m31-mon`. The paths are this Pi's
(user `ultrageek`); change them for another machine. The GC settings ship in the station's runtimeconfig.

## From the laptop

- `deploy.sh`: publish this checkout's station for linux-arm64, upload it, restart `m31-station` (images carry on by themselves).
- `soak.sh [SECONDS]`: every minute, say any warning or error in the session log, `[stack]` faults or exceptions in the journal,
  and units not running; every 10 minutes a summary (image state, memory, temperature, shots). Writes `out/soak.log`.
- `measure.sh NAME SECONDS`: shot cadence and memory peaks over a run (the GC comparisons in TODO.md were made with it).
- `night/`: a stand-in for the desktop window (`dotnet build -c Release`, then `dotnet bin/Release/net8.0/Night.dll HOST 5699 setup|preview|start [stack]|watch SECONDS|pause|resume|abort|state|saved`).
- `webtest/`: the web page driven from the laptop.
  - `ff.mjs URL STEPS.mjs OUTDIR [WxH]` runs a steps file in headless Firefox over WebDriver BiDi (profile under
    `~/snap/firefox/common/elink-ff-test`; the snap Firefox can only read profiles there) and closes it when done.
    Steps: `tour` (every page, nothing started), `control`, `cont` (continue M31 wide), `q` (queue page), `now` (Now page),
    `soakstart` (stop the queue, continue M31 wide).
  - `ffclose.mjs PORT...` closes test Firefoxes left running (the snap refuses signals from outside).
  - `web.mjs`, `ls.mjs`, `m45.mjs`, `sch.mjs`, `types.mjs`: the same calls from a Node leaf, no browser.
  - `freeze.mjs HOST`: a leaf that hooks what the page hooks; stop it with `kill -STOP` to play a phone asleep with the page open.
- `plansim/`: runs the coverage planner offline (edge fill and margin sweep, restart pick-up); `M45Replay.cs.txt` replays the
  first shots of an image. `stackbench/`: the live stacker on its own (memory per frame).

## What the soak of 4 November 2026 showed

96 minutes with the shipped build: one 30 s shot every 32 s, station memory 513 MB mean / 756 MB max, no OOM, 40-42 °C; the
image ran on to its 10 min depth (1248 shots, about 8 hours). Found and fixed on the way: a frozen web page stalled the station
(state events waited 30 s for it), the mount was not connected again after a power cut, the queue did not say why it stopped an
image. Ubuntu's unattended upgrades restart the station when they update .NET (the image carries on in about 30 s).
