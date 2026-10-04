# ELink UI review

Notes from going through every tab at full page length (rendered headless against the INDI simulators, short
exposures) and reading the views behind them. Screens: `ELINK_SHOT_HEIGHT=2600 ELINK_SCREENSHOTS=/some/dir ./dev.sh test tests/ELink.UI.Tests`.

The backend is sound; the UI is a stack of forms. Three things make it hard to use: **12 fixed tabs plus one tab per
opened device**, **the same ideas asked for in several places** (the sky, the field of view, the target), and **no
sense of state** (nothing is ever disabled, nothing says what a scope is doing right now).

## 1. Proposed structure: 12+ tabs down to 4 views

| view | what lives here | replaces |
|---|---|---|
| **Sky** (the home screen) | the atlas with the framing tool on it, the plan for the selected image (compact form), the queue of images, the stack growing as a layer on the chart | Sky atlas, Image, Live stack, Schedule |
| **Scopes** | one card per scope: status chip, what it is doing now, last frame, guide RMS, temperatures, Focus now / Take flats / Centre now / Park; open a scope for its details; devices open in a side drawer instead of a tab | the scope tabs, Autofocus, Centring (the scope already centres itself), the device tabs |
| **Rig** | a short wizard for mount → trains → scope, with Site, Profiles (drivers) and Calibration as sections in a left-hand list | Compose, Site, Profiles, Calibration, Storage settings |
| **Advanced** | raw INDI property browser, storage, service logs | INDI |

A thin **status bar** is always visible: night state (day / twilight / dark, hours of darkness left, Moon), one coloured
chip per scope (idle / slewing / centring / focusing / guiding / exposing 12 of 30 / waiting for sky / error), and the
latest error with a link to where it came from.

## 2. The framing tool lives on the sky chart

Today the chart already draws one rectangle (`Polygons`) fed from the Image tab's fields, and the chart is read-only
apart from picking. Make the chart the place where you say *what* to image.

- **Drag the frame to move it, drag a corner to resize, drag the handle to rotate.** The form fields (centre, width,
  height, angle) stay as the exact numbers but are driven by the frame, not the other way round.
- **Show each scope's train as its own field-of-view rectangle** at the frame centre, from `TrainState` (field size,
  learned angle), so you see at once whether one panel covers it or how many are needed.
- **Mosaic preview**: the planner's panel grid with overlap drawn on the frame, and the label "3×2 panels, about 4 h 20 min".
- **Layers you can switch on the chart**: horizon and dark/twilight line, Moon with a keep-out circle, the coverage map
  (shading by depth), and the **live stack drawn at its sky position**: the picture of your mosaic fills in where the
  sky is. That alone removes the Live stack tab.
- **Click the sky for actions**: right-click or long-press gives Go to here, Frame here, Add to queue, Centre here.
- Selection panel keeps: the object's name and size, altitude now, when it is above *your* horizon and for how long.
- The queue (what is now the Schedule tab) is a collapsible strip under the chart with the entries as one-line rows:
  name, status ("waits: Moon 12° away", "running 12/30"), priority arrows. Constraints (altitude, Moon, darkness) open
  per row on demand.

## 3. Usability problems found

Ordered roughly by how much they get in the way.

### Hard to understand what is going on
1. **Nothing is ever disabled.** There is no `CanExecute` or `IsEnabled` anywhere in the UI. Observe on a
   disconnected mount, Start with no scope ticked, Define scope with nothing chosen: all look available, and the
   explanation, if any, arrives as text elsewhere. Buttons should be disabled with a reason on hover.
2. **No overview of what each scope is doing.** A scope panel header says *Exposing 0 / 2 rounds* while the box beside
   it says *Shooting: Idle*; those are different sources shown side by side with no explanation. Debug-style text
   leaks through (`OnTarget · on target: True`).
3. **No connection state anywhere.** The left-hand equipment list shows names and tiny kind labels but not whether a
   device is connected, busy or in error. You have to open it to find out.
4. **No global error or activity area.** Errors appear as one line at the bottom of whichever page you are on and
   vanish when you leave it.
5. **No first-run guidance.** With nothing defined there is no hint of the order: connect devices, define the rig,
   set the site, pick a target, go. A readiness checklist at the top of Sky ("Rig ✓  Site ✗  Plate solver ✓  Mount
   connected ✗") would fix most of it.

### Layout and visuals
6. **The atlas overflows the window at 1280 px**: the right-hand panel and the toolbar run off the edge ("889 stars to
   mag 7.6, 62 c…", the selection text is cut off). The minimum window width is 900, which is worse.
7. **The tab bar wraps to three rows** and mixes the 12 fixed tabs with closable device tabs. Where you are, and
   what moved, is hard to tell.
8. **The equipment list names every INDI interface separately**: *CCD Simulator* appears three times (Camera,
   FilterWheel, GuidePort), *Telescope Simulator* three times. The GuidePort rows are plumbing.
9. **Type hierarchy is upside down.** Tab titles are about 20 px, page titles are 20 px, but labels are 11 px at
   55 % opacity (low contrast on the dark background) and section headers are 12 px at 55 %. Everything sits in
   identically weighted cards.
10. **Spinner boxes for every number** (arrows eat the width; no units in the box). Units are inconsistent:
    `°`, `"/px`, `s`, `min`, `px`.
11. **The connection bar (host, port, Connect) is always shown**, also in the all-in-one station where it is
    pointless; the connected status is tiny text top right.
12. **Primary actions are not consistent**: Image has eight buttons in one row of equal weight (Preview, Start, Pause,
    Resume, Abort, Apply depth now, Add to schedule, Start afresh).

### Compose
13. **No way to edit anything.** The "Defined" list only offers Remove; to change a scope or a train you retype all of
    it. Selecting an entry should load it into the form.
14. **Raw ids instead of names**: `CCD_Simulator`, `Telescope_Simulator` in every dropdown and row.
15. **The train camera row has seven unlabelled input boxes** (pixel size, width, height, gain, offset, cooling °C,
    cooling rate); only some have placeholders and none have headers, the explanation is a paragraph above.
16. **"Pointer from a mount" is a step the user must know about.** A scope should be created from a mount, and the
    pointer made for it behind the scenes. Other scopes also appear as selectable pointers and trains by default
    (`main (scope)`, `guided (scope)`), which confuses before nesting is wanted.
17. **Three long cards on one scroll page.** A wizard (mount → telescope(s) and cameras → guiding → extras), with the
    focus, flip and grading options under "Advanced", would match how people build a rig.

### Image, stack, schedule
18. **The Image tab asks for 17 values before the first action.** Most have good defaults and belong under *More*:
    stepover, max shots, weather guard, dither pixels, output scale, minutes per spot.
19. **The coverage map has no orientation or scale**: a blue and yellow rectangle, a one-line caption, no north/east,
    no scope positions. On the sky chart it would have all of this for free.
20. **The result image has no stretch, zoom, pan or histogram.** The live stack is a dark noisy grey square.
21. **Live stack repeats the Image field** (hence a "Use the Image tab's field" button) and shows registration options
    (frame scale and angle "for Pointing") that matter only for one registration mode.
22. **Schedule entries show five numeric fields each, need "Apply changes", and cross-reference other tabs** ("Waiting
    — the site is not known (Site tab)"). An unlabelled dropdown plus a red Remove at the end of the row is unclear.
23. **Messages use one error style for everything**: informational lines ("UIField is on the schedule") and
    confirmations ("saved" on Site) appear in the red error colour. (Compose now separates green from red.)

### Terms and help
24. **Jargon without help**: Pointer, Shooter, Train, Spot, Depth, Stepover, Coverage. Only 11 tooltips exist in the
    whole UI for several hundred inputs.
25. **Site is typed as `47:29:52`** with only a label as format hint; the horizon is a free-text list
    (`180:15, 200:35`) with no picture. The night (dusk, dawn, Moon) is a text block, where a single timeline bar would
    show it at a glance, and it belongs in the status bar.
26. **Centring tab is a leftover**: scopes already centre themselves; this older mount-level service shows
    "Guide camera: cam / Primary camera: cam" and a solve log with empty shooter names.

## 4. Suggested order of work

1. **Foundations**: status bar with scope chips and the readiness checklist; enable/disable with reasons; one
   message component (info / success / error) used by every page; device list that shows connection state and hides
   GuidePort entries. Fix the atlas overflow.
2. **Sky view**: merge Sky atlas + Image + Live stack + Schedule; draggable and rotatable frame with train FOVs; layers
   (coverage, stack, Moon, horizon); the queue strip.
3. **Scopes view**: scope cards, drawer for device panels; fold Autofocus and Centring in as scope actions.
4. **Rig wizard**: edit existing, friendly names, labelled camera rows, Advanced section, Site timeline.
5. **Visual pass**: type scale and contrast, spacing, one accent colour for the primary action, units in fields,
   tooltips on every non-obvious field, a short glossary page.

The contract-only design makes this safe: view models mirror state and send commands, so none of it touches the backend.
