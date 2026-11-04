#!/bin/sh
# every 2 s: time, station memory now/peak (MiB), station CPU seconds, indi memory, available memory, SoC temp
while true; do
  g=/sys/fs/cgroup/system.slice/m31-station.service; i=/sys/fs/cgroup/system.slice/m31-indi.service
  cur=$(($(cat $g/memory.current 2>/dev/null || echo 0)/1048576)); pk=$(($(cat $g/memory.peak 2>/dev/null || echo 0)/1048576))
  cpu=$(awk "/usage_usec/{print \$2/1e6}" $g/cpu.stat 2>/dev/null); im=$(($(cat $i/memory.current 2>/dev/null || echo 0)/1048576))
  av=$(awk "/MemAvailable/{print int(\$2/1024)}" /proc/meminfo); t=$(($(cat /sys/class/thermal/thermal_zone0/temp)/1000))
  anon=$(awk "/^anon /{print int(\$2/1048576)}" $g/memory.stat 2>/dev/null); fil=$(awk "/^file /{print int(\$2/1048576)}" $g/memory.stat 2>/dev/null); oom=$(awk "/oom_kill /{print \$2}" $g/memory.events 2>/dev/null)
  echo "$(date +%H:%M:%S) st=${cur} peak=${pk} cpu=${cpu} indi=${im} avail=${av} anon=${anon} file=${fil} temp=${t} oom=${oom}"
  sleep 2
done
