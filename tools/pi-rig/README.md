# The real rig on the Pi: 114/900 newtonian, Nikon D3500, OnStep

Runs the station against the real drivers instead of the simulators (`tools/pi-m31`). The two setups use the same ports, so the
units conflict: starting one stops the other.

| File | On the Pi |
|---|---|
| `indi.sh` | `~/elink/rig/indi.sh`: indiserver on 7627 with `indi_gphoto_ccd` (device name pinned to "Nikon D3500") and `indi_lx200_OnStep`; connects whatever is not connected every 15 s |
| `compose.json` | `~/elink/rig/state/compose.json`: the scope, the train (900 mm, 114 mm, 6000×4000 at 3.89 µm: 0.89"/px, 1.49° × 0.99°), OnStep as its mount, meridian flip 6 min past the meridian |
| `elink-rig-indi.service`, `elink-rig.service` | `/etc/systemd/system/` (not enabled) |

The site (`site.json`) is copied from the test station's.

## Once

```bash
sudo usermod -aG dialout,plugdev ultrageek
```

`dialout` opens OnStep's USB serial port, and `plugdev` opens the camera (libgphoto2's udev rule). Without them, both drivers
fail to connect. For OnStep over USB, set its port in `elink-rig-indi.service` (`Environment=ONSTEP_PORT=/dev/ttyUSB0`, or
`ttyACM0`). For OnStep's Wi-Fi, set `ONSTEP_HOST=ip:port`. Then run `sudo systemctl daemon-reload`.

## Switching

```bash
sudo systemctl start elink-rig          # real rig (stops the simulator station)
sudo systemctl start m31-indi m31-station   # back to the simulators
```

`sudo systemctl enable elink-rig elink-rig-indi` makes the real rig start at boot.

## Before the first night

- **The camera:**
  - Set the mode dial to **M**.
  - Put the lens/scope adapter on, with exposure delay and long-exposure noise reduction off.
  - Keep the memory card in, or not, as the driver expects.
- **Exposure length:** GPhotoCCD times any length itself (bulb), so subs are not limited to the camera's shutter speeds. Longer
  subs cut the per-sub overhead (about 6 s: 17% of a 30 s sub, 5% of a 120 s one). Unguided, the limit is trailing:
  at 0.89"/px, check star shapes before going past 60 s.
- **Take one test shot** from the Rig page at the length you plan to use. Check that a FITS arrives, that its `EXPTIME` is what
  you asked for, and that the sky level matches the length (a sub that comes back at 30 s exposure means the driver fell back
  to the camera's own shutter speed).
- **OnStep:**
  - Set its own meridian limit ("Minutes Past Meridian", west) above the station's 6 minutes, so the station flips first.
  - Save a park position, or the end-of-night park falls back to stopping tracking.
  - The station unparks OnStep before its first slew, and only after OnStep has the time and site. It then turns tracking on.
