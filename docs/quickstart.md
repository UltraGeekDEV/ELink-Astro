# Quick start: a night with the Pi

For the home rig: the Raspberry Pi, the OnStep mount, a DSLR on the scope, and a phone or laptop. You only need a browser.

## 1. Plug in and power up

1. Plug the **OnStep** controller and the **camera** into the Pi's USB ports. Switch the camera on and set it to **M** (manual), with auto power-off disabled.
2. Power the Pi. Wait about **2 minutes**.

## 2. Connect to the Pi

1. On your phone or laptop, join the Wi-Fi network **ELink-Pi** (with your hotspot password).
2. Open **http://10.42.0.1:8080/** in the browser.

The label at the top says **connected** (in green) when the page is talking to the Pi. If it says *connecting…* or *no station at …*, see *If something is wrong* below.

At home with the network cable plugged in, **http://192.168.1.119:8080/** works too.

## 3. Check the devices (once a night)

Open the **Rig** tab.

- Under **Devices**, *LX200 OnStep* and *GPhoto CCD* should both say **connected**. If one doesn't, press its **Connect** button.
- Under **Mount**, press **Unpark** if it says parked, then **Track**.

## 4. Tell it your scope (only the first time)

In **Rig › Scope set up**:

| box | what to put |
|---|---|
| Scope name | anything, e.g. *Home 900* |
| Mount | LX200 OnStep |
| Camera | GPhoto CCD |
| Focal length mm | your scope's focal length (e.g. 900) |
| Pixel µm | from your camera's specs (e.g. Nikon D5300: 3.9; Canon 600D: 4.3) |
| Sensor width / height px | from your camera's specs (e.g. D5300: 6000 x 4000; 600D: 5184 x 3456) |

Press **Save scope**. A DSLR doesn't report its pixel size or sensor size, so leaving any of these three empty is what causes **"the field of ... is not known"**.

Also fill in **Rig › Site** once (or press **Use this device's location**). The mount and the planner need to know where you are.

## 5. Take a picture

Open the **New image** tab.

1. Type the target in the search box (e.g. *andromeda*, *M 45*, *orion*) and pick it.
2. Press **Fit the object** to size the area around it.
3. Set **Exposure** (30 s is a good start) and how deep you want it, or tick **until I stop it**.
4. Press **Start**.

The preview shows how many frames it takes and how long, and whether the target is up.

## 6. Watch it

The **Now** tab shows the coverage filling in, the last shot, the pace, and the live stack picture. **Pause**, **Resume** and **Stop** are there.

You can close the browser: the Pi carries on by itself. If the Pi loses power, it picks the picture up again when it comes back.

## 7. End of the night

Press **Stop** on the Now tab. Then go to **Rig › Mount** and press **Park**.

Your frames are on the Pi in `~/elink/home/images/`, in a folder per night.

## If something is wrong

| you see | do this |
|---|---|
| "the field of ... is not known" | Fill in focal length, pixel µm and sensor width/height in Scope set up (step 4), and check the camera says connected. |
| it doesn't say *connected* | Make sure you are on **ELink-Pi** and the address is `http://10.42.0.1:8080/`. If it still doesn't work, power the Pi off and on and wait 2 minutes. |
| OnStep won't connect | Unplug and replug its USB cable, then press Connect. If something else is on the Pi's USB serial too, OnStep may be on another port; see below. |
| "no stars" or frames rejected | Check focus, the lens cap, and clouds; try a longer exposure. |
| it says "waiting" | Usually the target is too low or it isn't dark yet. The Queue and Now tabs say why. |

**OnStep on another port.** The driver looks at `/dev/ttyUSB0` first. If OnStep is somewhere else, run this on the Pi, replacing the port with the right one:

```bash
indi_setprop -p 7624 "LX200 OnStep.DEVICE_PORT.PORT=/dev/ttyACM0"
```

Then press Connect again.

**The Pi's Wi-Fi.** It always starts the ELink-Pi hotspot. To have it join your home Wi-Fi instead, run `sudo elink-wifi client` on the Pi. To go back to the hotspot, run `sudo elink-wifi ap` or reboot.
