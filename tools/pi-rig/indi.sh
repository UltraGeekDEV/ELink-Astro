#!/bin/sh
# The real rig: the Nikon D3500 (indi_gphoto_ccd, its device name pinned so the station always finds it as "Nikon D3500")
# and OnStep (indi_lx200_OnStep) on port 7627. Devices that are not connected are connected again every 15 s, so the camera
# and the mount may be switched on before or after the Pi. OnStep: ONSTEP_PORT=/dev/ttyUSB0 (or ttyACM0) for USB serial,
# or ONSTEP_HOST=192.168.0.1:9999 for its Wi-Fi; neither = the driver searches the serial ports itself.
P="-p 7627"; F=/tmp/elink-rig.fifo
rm -f $F; mkfifo $F
indiserver -p 7627 -u /tmp/indiserver-rig -f $F &
S=$!
trap "kill $S 2>/dev/null" TERM INT
sleep 2
echo "start indi_gphoto_ccd -n \"Nikon D3500\"" > $F
echo "start indi_lx200_OnStep" > $F
sleep 5
if [ -n "$ONSTEP_HOST" ]; then
  indi_setprop $P "LX200 OnStep.CONNECTION_MODE.CONNECTION_TCP=On"
  indi_setprop $P "LX200 OnStep.DEVICE_ADDRESS.ADDRESS;PORT=${ONSTEP_HOST%:*};${ONSTEP_HOST#*:}"
elif [ -n "$ONSTEP_PORT" ]; then
  indi_setprop $P "LX200 OnStep.DEVICE_PORT.PORT=$ONSTEP_PORT"
else
  indi_setprop $P "LX200 OnStep.DEVICE_AUTO_SEARCH.INDI_ENABLED=On" 2>/dev/null
fi
while kill -0 $S 2>/dev/null; do
  for d in "Nikon D3500" "LX200 OnStep"; do
    indi_getprop $P -t 3 "$d.CONNECTION.CONNECT" 2>/dev/null | grep -q "=On" || indi_setprop $P "$d.CONNECTION.CONNECT=On" 2>/dev/null
  done
  sleep 15
done
wait $S
