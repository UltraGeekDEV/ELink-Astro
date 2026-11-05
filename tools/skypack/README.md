# skypack: ELink's own sky data

Builds `src/ELink.App/sky/elink-sky.pack`. The desktop app (and the station, when it has no `--sky-data`) loads the atlas
from it, so no planetarium needs to be installed. What's in it:

| What | From | Licence |
|---|---|---|
| 42,864 stars to about magnitude 8, with names and B-V colours | Hipparcos and Tycho-2, as packaged by KStars | GPL |
| 2.46 million stars from magnitude 8 to 12 | Guide Star Catalog 1.2 (STScI) | public domain |
| 13,970 deep-sky objects | OpenNGC (Mattia Verga) | CC-BY-SA 4.0 |
| Constellation figures | KStars | GPL |

The Milky Way's glow isn't stored in the pack: the atlas computes it from how densely the faint stars crowd the sky.

## Rebuilding

1. Dump the GSC to magnitude 12 on a machine with the INDI `gsc` tool and its data. `gscdump.py` takes about 2 minutes on a
   Pi 4 and writes `/tmp/gsc12.txt`, one "ra dec mag" a line.
2. Build the pack from a KStars data directory and the dump:

```bash
dotnet run -c Release --project tools/skypack -- "C:/Program Files/KStars Desktop Planetarium/bin/data/kstars" tools/skypack/out/gsc12.txt src/ELink.App/sky/elink-sky.pack
```

`skypack check PACK` prints what a small field round M42 gets.
