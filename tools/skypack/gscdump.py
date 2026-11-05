import math, subprocess, os
env = dict(os.environ, GSCDAT="/home/ultrageek/elink/gsc/share/GSC", GSCBIN="/home/ultrageek/elink/gsc/share/GSC/bin")
seen = {}
for dec in range(-85, 90, 10):
    n = max(1, math.ceil(360 * math.cos(math.radians(max(0, abs(dec) - 5))) / 10))
    for k in range(n):
        ra = k * 360 / n
        out = subprocess.run(["nice", "-n", "15", "/home/ultrageek/elink/gsc/bin/gsc", "-c", f"{ra:.3f}", f"{dec:+.3f}", "-r", "480", "-m", "0", "12", "-n", "3000000", "-p", "2"],
                             capture_output=True, text=True, env=env).stdout
        for line in out.splitlines():
            p = line.split()
            if len(p) < 4 or not p[0].isdigit(): continue
            gid = p[0]
            if gid in seen: continue
            seen[gid] = (float(p[1]), float(p[2]), float(p[3].rstrip(";")))
    print("dec", dec, len(seen), flush=True)
with open("/tmp/gsc12.txt", "w") as f:
    for gid, (ra, de, m) in seen.items(): f.write(f"{ra:.5f} {de:+.5f} {m:.2f}\n")
print("ALLDONE", len(seen), flush=True)
