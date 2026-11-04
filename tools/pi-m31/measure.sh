#!/bin/bash
# measure.sh NAME SECONDS: watch the image (cadence), then the memory peaks and the station's heap lines
HERE=$(cd "$(dirname "$0")" && pwd); OUT=${OUT:-$HERE/out}; mkdir -p "$OUT"; PI=${PI:-192.168.137.48}
N=$1; T=$2; cd $HERE/night
timeout $((T + 60)) dotnet bin/Release/net8.0/Night.dll $PI 5699 watch $T > $OUT/mem-$N.watch 2>&1
{
  echo "== $N"; ssh ultrageek@$PI 'systemctl show m31-station -p Environment | grep -o "DOTNET_GC[^ ]*\|DOTNET_gc[^ ]*" | tr "\n" " "'; echo
  grep " shot " $OUT/mem-$N.watch | grep -o "(+ *[0-9.]*s)" | tr -d "(+s)" | awk '$1>0{n++; s+=$1; if($1>m)m=$1} END{printf "shots %d, mean gap %.1f s, longest %.1f s\n", n, s/n, m}'
  ssh ultrageek@$PI 'cd elink/m31-test; awk "{for(i=1;i<=NF;i++){split(\$i,a,\"=\"); if(a[1]==\"st\"&&a[2]>st)st=a[2]; if(a[1]==\"anon\"){if(a[2]>an)an=a[2]; s+=a[2]; n++}; if(a[1]==\"oom\"&&a[2]>o)o=a[2]}} END{printf \"cgroup max %d MB, anon max %d MB, anon mean %d MB, oom %d\n\",st,an,s/n,o}" mon.log; pid=$(systemctl show -p MainPID --value m31-station); grep VmHWM /proc/$pid/status; journalctl -u m31-station --no-pager -o cat --since "-'$((T/60+2))' min" | grep "^memory:" | tail -3'
} > $OUT/mem-$N.txt 2>&1
cat $OUT/mem-$N.txt
