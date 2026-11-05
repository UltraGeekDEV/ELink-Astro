#!/bin/bash
# soak watch: every minute look for problems (said at once), every 10 minutes a summary line; all of it also in soak.log
T=$(cd "$(dirname "$0")" && pwd); OUT=${OUT:-$T/out}; mkdir -p "$OUT"
PI=${PI:-192.168.137.48}; END=$(( $(date +%s) + ${1:-5400} )); L=$OUT/soak.log; n=0
LOG=elink/m31-test/images/logs/session-2026-11-03.log
seen=$(ssh -o BatchMode=yes ultrageek@$PI "grep -c '' $LOG"); since=$(date '+%Y-%m-%d %H:%M:%S')
echo "soak start $(date +%T)" | tee -a $L
while [ $(date +%s) -lt $END ]; do
  sleep 60; n=$((n+1)); now=$(date '+%Y-%m-%d %H:%M:%S')
  out=$(ssh -o BatchMode=yes -o ConnectTimeout=10 ultrageek@$PI "
    tail -n +$((seen+1)) $LOG | grep -E ' (warn|error) ' | cut -c1-220
    journalctl -u m31-station --since '$since' --until '$now' --no-pager -o cat | grep -E '\[stack\]|xception|Unhandled|Killed' | cut -c1-220
    systemctl is-active m31-station m31-indi | grep -v '^active' | sed 's/^/unit: /'
    echo LINES \$(grep -c '' $LOG)" 2>&1) || { echo "$(date +%T) Pi unreachable" | tee -a $L; continue; }
  seen=$(echo "$out" | awk '/^LINES/{print $2}'); since=$now
  echo "$out" | grep -v '^LINES' | grep . | sed "s/^/$(date +%T) PROBLEM /" | tee -a $L
  if [ $((n % 10)) -eq 0 ]; then
    st=$(timeout 40 dotnet $(ls $T/night/bin/*/*/Night.dll | head -1) $PI 5699 state 2>&1 | sed -n '2p;4p' | cut -c10- | tr '\n' ' ')
    m=$(ssh -o BatchMode=yes ultrageek@$PI "tail -300 elink/m31-test/mon.log | awk '{split(\$7,a,\"=\"); if(a[2]>mx)mx=a[2]; s+=a[2]; c++} END{printf \"anon10m mean %d max %d\", s/c, mx}'; tail -1 elink/m31-test/mon.log | cut -d' ' -f3,6,9-10; journalctl -u m31-station -o cat --no-pager | grep '^memory:' | tail -1 | cut -c9-90; grep -c 'home900: Exposing' $LOG")
    echo "$(date +%T) SUMMARY $st | $(echo "$m" | tr '\n' ' ')" | tee -a $L
  fi
done
echo "soak end $(date +%T)" | tee -a $L
