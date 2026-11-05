#!/bin/sh
# after indiserver starts: connect the simulators and load their saved settings (the 6000x4000 RGGB sensor)
for i in $(seq 30); do indi_getprop -p 7625 -t 1 "CCD Simulator.CONNECTION.*" >/dev/null 2>&1 && break; sleep 1; done
for d in "Telescope Simulator" "CCD Simulator"; do
  indi_setprop -p 7625 "$d.CONNECTION.CONNECT=On"; sleep 2
  indi_setprop -p 7625 "$d.CONFIG_PROCESS.CONFIG_LOAD=On"; sleep 2
done
