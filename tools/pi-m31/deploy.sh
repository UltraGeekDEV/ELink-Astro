#!/bin/bash
# build this checkout's station for the Pi, upload it, restart the m31 test station (a boot service; state and frames kept)
set -e
T=$(cd "$(dirname "$0")" && pwd); R=$(cd "$T/../.." && pwd); OUT=${OUT:-$T/out}; mkdir -p "$OUT"
PI=${PI:-192.168.137.48}
cd "$R"
rm -rf $OUT/pi-station; ./dev.sh publish src/ELink.Station -c Release -r linux-arm64 --self-contained false -o $OUT/pi-station 2>&1 | grep -E " error |rror\(s\)" || true
tar -C "$OUT" -czf $OUT/pi-station.tgz pi-station
scp -q $OUT/pi-station.tgz ultrageek@$PI:elink/m31-test/
ssh -o BatchMode=yes ultrageek@$PI 'D=/home/ultrageek/elink/m31-test; sudo systemctl stop m31-station; cd $D && rm -rf pi-station && tar -xzf pi-station.tgz && sudo systemctl start m31-station; sleep 15; systemctl is-active m31-station; tail -1 $D/mon.log'
