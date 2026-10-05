# ELink UI: fresh-eyes review

Reviewer: first-time user, no prior knowledge. Based on the screenshots in the session scratchpad and the AXAML/view-model source (Views/*.axaml). Nothing was run. Severity = how many users hit it x how badly.

## Overall impression

The structure (Sky / Scopes / Rig / Advanced) is sensible, but the vocabulary is the main problem. The same physical thing is called telescope, train, scope, shooter, pointer, mount, camera, "guide scope" and "primary" in different places, and the UI forces a two-step model (define "telescopes", then define "scopes" that contain them) before anything can happen. A first-time user hits that wall within two minutes. Second: raw internal state leaks into the UI ("OnTarget, on target", "Moving False", "Idle" everywhere). Third: several features exist twice at different levels (dither, centring, autofocus, guiding controls on the scope page vs. Rig set-up).

---

## Sky (empty-sky, sky-getting-started, rig-sky, sky-framed, 05..07d, sky-panel-folded; SkyView.axaml)

**[High] The first-run state looks done but is not, and the next step is not obvious.**
- Status strip says "Site not set" and "2 things to set up"; the chart's "BEFORE YOU IMAGE" box lists 4 items, 2 ticked. The Start button is greyed and a yellow notice says "Set up a scope first (Rig > Set up)". Three different places say what is missing, in three styles (strip chip, overlay card, yellow notice), and the card's "Not now" button is the only clearly clickable-looking thing, which dismisses the help.
- "Not now" is an odd label for "hide this checklist". Rename to "Hide" or "x".
- Fix: one place for readiness. Make the checklist rows obviously clickable (they are ghost buttons that look like text), and make the greyed Start button explain itself via the single notice only. The "2 things to set up" chip should be named "Setup: 2 left".
- Also "Site known" is shown as a requirement, but imaging works without it (Start enabled when the scope exists, with "Site not set" still shown). Say "optional" or "recommended: enables dark/horizon planning".

**[High] "Width 0 / Height 0" and "One frame of the scope" in the Image card.**
- The default image has width 0 and height 0 (meaning "one frame of the scope"). A new user sees two zero fields, a hint "One frame of the scope" and a button "Just one frame" that does nothing visible when already one frame. The tooltip that explains "0 = one frame" is only on Width, not on Height, and tooltips are not discoverable.
- Fix: show the actual size ("1.0 x 0.7 deg, one frame") in the fields, or replace zeros by an explicit "Size: [One frame | Custom]" toggle. Remove "Just one frame" or move it next to the size fields as "Reset to one frame".

**[High] The Image card is a form with ~12 visible controls plus a "More options" expander hiding 10 more.**
- Visible: name, centre RA/Dec (strings with no format hint), width, height, turn, each shot, scope checkboxes, depth, Start / Add to queue. Hidden in "More options": filter, ISO, dither, panel spacing, stop-after, output scale, weather device, a free-text "Layers" box with a CSV mini-language (`base, L, 4, 10, 30`), Live stack and Start afresh checkboxes, "Check the plan".
- The "Layers" text box (name, filter, finest "/px, coarsest "/px, minutes) is a hand-parsed mini-language in a progressive-disclosure expander; a normal user cannot guess it. The Sky toolbar also has a "Layers" button, which is something else (chart layers). Same word, two meanings.
- Fix: rename the chart button to "Show" or "View"; give image layers a proper editable list (row per layer) or hide it as expert; move "Weather device (pause when unsafe)" to Site/Rig, it is rig configuration not per-image.

**[High] Vocabulary on this one card: Image, Frame, Panel, Spot, Shot, Visit, Depth, Scope.**
- "Depth per spot (min)": the Help says "spot" = any part of the image; the form never says. "Stop after (shots)" while elsewhere "visits". "Panel spacing" (panel = a mosaic tile) shown even for a single frame. The word "shot" is used for both exposures and mosaic visits.
- Fix: pick Frame (the yellow rectangle), Panel (a tile), Exposure (one shot), Total exposure (min) = "Depth". Rename "Depth per spot (min)" to "Total exposure per part of the image (min)" or "Target exposure (min)".

**[Medium] Unlabeled combo box next to "Frame this / Go there" (TARGET card).**
- A bare ComboBox shows "eq" or "main" or empty, only a tooltip ("Which scope or mount"). A mount pointer ("eq") and a scope ("main") are mixed in the same list. Go there is meaningless without it.
- Fix: label it "Send:" and list scopes only (see Rig > mount pointers, below).

**[Medium] Two buttons whose difference is unclear: "Frame this" vs "Go there" vs "Start".**
- Frame this = plan; Go there = slew only; Start = image. The text "Framing M 42: drag the frame on the chart to adjust it" appears as an info notice and "Image: One frame of main - roughly 60 min with 1 scope" is another notice; two blue banners stack.

**[Medium] Chart toolbar: "-", "+", "Frame", "Mount", "Layers", "Find".**
- "Frame" = zoom to the frame; "Mount" = centre on the mount; neither is self-explanatory without hovering. Rename "Zoom to frame" / "Go to mount" or use icons with text.
- The chart search box is truncated ("Find: M42, NGC 7000, Vega, Jupi...").

**[Medium] "Hide panel" button floats over the status text/toasts and overlaps the bottom-right "N stars to mag X" line; toast "Image UIField is done: 6 shots" lands on top of it (07d, 07e1).** Hide panel button appears cut ("Hide panel |") in the 07d capture. Also when the panel is folded the label changes to "< Panel" (sky-panel-folded): inconsistent wording; use one label ("Image panel") with an arrow.

**[Medium] The flat-horizon overlay (07e, 07e1) covers the lower third of the chart and its text overlaps itself** ("Moon 30%", "M42 6 deg", "Telescope Simulator (Tracking) 33 deg" labels collide at the right; planets and the Sun labels have no explanation). The legend line is truncated with an ellipsis. It duplicates the "YOUR SKY" plot on Rig > Site (same control, same data).
- Fix: show it in only one place (Site) or give the chart overlay a close button and a smaller default.

**[Medium] Queue card in the Sky side panel.** A scheduler lives under the image form: Run / Stop, then each row hides priority, "Higher than (deg)", "Only when it is dark", Moon limits behind a per-row expander named "When it may run". "Higher than (deg)" reads wrong (higher than what?): rename "Minimum altitude". Queue state text under "QUEUE" is blank in the empty state, so the card heading has no state.
- Also "Add to queue" and "Start" both sit side by side with no explanation of whether Add to queue also starts anything.

**[Low] "Images kept from earlier nights" and "Stellarium" expanders at the bottom of the side panel.** The Stellarium panel has a paragraph telling you to configure Stellarium by hand and "the port above" (there is no port above). Move to Advanced or hide until a connection exists. "Forget" (danger) and the bare "refresh" arrow button are unclear.

**[Low] Context menu items: "Frame the image here", "Send the scope here", "What is here", "Centre the chart here".** Fine but undiscoverable; mention the right-click once in the hint text. Keyboard shortcuts (F, +, -, Ctrl+1..4) are undocumented in the UI.

---

## Scopes (empty-scopes, rig-scopes, rig-scopes-expanded, 06, 07, 07f, 07i; ScopesView.axaml, ScopePanelView.axaml)

**[High] Raw state strings.** The list card shows "OnTarget, on target" under every scope and the Mount field in the details shows "OnTarget, on target" too; the same state in CamelCase and sentence form concatenated. While the scope is exposing, Mount reads "Idle" or "OnTarget". After a run the card badge says "On target", the status strip "On target", and the title chip "Idle" elsewhere; there are at least 3 state vocabularies (Idle, Exposing 1 of 2, On target, Acquiring). "Idle" is never defined.
- Fix: one state enum with a one-line meaning; show "Mount: Tracking / Slewing / Parked" (what a mount can actually be), not the scope's own phase.

**[High] The scope title shows the same name twice.** Card header "main" with the grey id "main" underneath (rig-scopes-expanded shows "Main scope" over "main"). The id is only meaningful internally; drop it unless it differs.

**[High] The Guiding card shows an empty black chart and controls for testing.**
- A scope with no guiding configured still shows the card? (HasGuider is conditional, but the screenshot of the guided scope shows "not guiding - 0 stars - 0 frames - 0 dithers - Pulse" with phase chip "Idle" and, in another, "Acquiring" next to "not guiding": contradictory). The hint says "...these are for testing": so Start guiding / Stop / Recalibrate / Dither now are test buttons in primary UI. Hide behind an "Advanced" expander, because the design promise is that the scope guides itself.
- "Pulse" is jargon (guide output type), shown with no label. "+-4"" full height" graph with no axis labels.
- Fix: label the chip fields ("Guide output: pulse"), show the chart only while guiding, move the manual buttons under an expander.

**[High] The Scopes page is a mix of scope-level and device-level panels with no hierarchy.** Below the selected scope sit three unrelated expanders: "Focus helper (for focusing by hand)", "Autofocus", "Centring after hand-controller slews (mount-level service)". They are not scoped to the selected scope: Autofocus and Centring ask the user to pick "Shooter", "Focuser", "Mount", "Guide camera (solved)", "Primary camera" again, although the scope already knows these. This contradicts the stated "the scope does it by itself" and the Rig > Set up "What the scope does by itself" options (centre after slew, refocus triggers), so the same capability exists twice, with different knobs (tolerance arcmin in both, exposure s in both).
- Fix: Scope's own settings live in one place. Remove the Autofocus and Centring expanders from Scopes, or pre-select from the selected scope and demote to "Manual tools". Rename "Centring after hand-controller slews (mount-level service)" to something a human understands, e.g. "Re-centre after slews made outside ELink".

**[Medium] Autofocus, Centring: jargon and bare labels.** "Shooter" (never defined in UI), "Step size" (no unit), "Samples", "Tolerance '" (a lone apostrophe for arcminutes), "Max tries", "Sync the mount", "Centre the primary (use offset)", "Guide scope to primary offset", "Solve guide frame", "SOLVES" list of raw log lines. The hint paragraph in Centring is 4 clauses long. A new user cannot tell what "Learn offset" does or when to press it ("Point anywhere with stars" is the only instruction). Nothing says Run in Autofocus needs a connected, selected focuser; the error shows in red after the click.

**[Medium] Manual control expander ("point and take frames").** Four action buttons: "Go there and take frames", "Go there only", "Take a frame here", "Abort". Fine for testing but overlaps with the Sky Start/Go there and the camera drawer's Expose. Three ways to take a frame.

**[Medium] Empty state.** Left list says "A scope is a mount with one or more telescopes..." while the right panel repeats "Set up a scope" with another button and a paragraph. Duplicate message and duplicate button. "Focus helper (for focusing by hand)" is visible in the empty state with an expander whose arrow glyph renders as a stray corner mark (empty-scopes shows a tiny corner icon, not a chevron).

**[Low] Cooling card says "The train ramps the sensor..." (ScopePanelView.axaml:86).** "Train" appears nowhere else in the UI. Say telescope.

**[Low] Latest frame list shows file-like "cam-CCD_Simulator Light 1s Green" repeated lines, with underscores and device names.** Fine for logs, noisy for a main card.

**[Low] "What it did" expander is expanded by default and looks like a console log with raw timestamps.** Good feature; label "Activity log".

---

## Rig > Set up (empty-rig-setup, setup-*, rig-rig-setup; ComposerView.axaml)

**[High] Two-step telescope/scope model with confusing vocabulary.**
- Step 1 "Your telescopes" ("a telescope here is one optical path...A guide scope is a telescope too"), step 2 "Your scopes" ("a scope is a mount carrying one or more telescopes"). In plain astronomy a scope and a telescope are the same thing; here they are different. The checklist on Sky says "Scope set up: define a scope"; the status strip chips are scope names; the drawer title says "Telescope Simulator" for what is a mount device. Result: "Telescope Simulator" (a mount), "Main telescope" (an imaging train), "Main scope" (mount + trains), "guide scope" (a train).
- The saved summaries leak internals: "400 mm - CCD Simulator (imaging) - filter wheel - focuser", "Telescope Simulator - cam - guides itself": the string "cam" is the telescope's name, shown as the part list.
- Fix: rename in the UI: "telescope" (the optical train) -> **Optical train** or keep "Telescope", and "scope" -> **Mount setup / Rig** ... pick one noun for the mount+trains thing (suggest "Scope" and call the optical path "Optics" or "Camera train"). Stop using "Telescope Simulator" style device names as scope names. Auto-name a new scope from its mount. Offer a one-click "Create a scope from this telescope" ("quick setup") for the 90% case of one mount + one train.

**[High] "Add a scope" is disabled until a telescope exists, with only a hint "Set up a telescope first".** The button looks disabled-primary; the tooltip says "Add a telescope first". The order requirement (Equipment connected -> telescope -> scope) is only partly said. After saving the telescope, a toast "Telescope saved" is the only guidance; nothing points to step 2. Fix: after save, highlight "Add a scope" and show "Next: add a scope".

**[High] Telescope form is long and mixes concerns.** Name, focal length, aperture, per-camera role/gain/offset/cool-to/cooling rate, a pseudo-mono checkbox, an expander "This camera does not know its own sensor (some DSLRs)", filter wheel, focuser, rotator, focus-offsets text box, plus the red error block at the bottom (a red banner appears, cut off in the screenshot). Specific issues:
- Camera role dropdown: "Imaging / Not used / ... (guide)" with the hint "Leave the rest as 'Not used'": every connected camera in the whole rig is listed in every telescope form, including the guide camera of another telescope, and defaults vary.
- "Pseudo mono (colour camera)" is unexplained jargon; the explanation is in a tooltip. A first-time user wants "I have a colour camera / a mono camera".
- "Gain / Offset" show "as is" watermark (two text boxes), "Cool to" has "no cooling" watermark; "Cooling rate (C/min)" a numeric box with 3: five fields per camera.
- Focus offsets "L=0, R=30, Ha=120" is another hand-parsed free-text format.
- Fix: collapse the per-camera detail under "Camera settings" per camera; put pseudo mono and sensor overrides in an "Advanced" expander; give focus offsets a per-filter table fed from the filter wheel's slots.

**[High] Scope form is long and mixes concerns (set-up and behaviour).** Name, MOUNT checkboxes (checkbox for a single choice: the user may tick two mounts), TELESCOPES ON IT checkboxes, an expander "Where each sits relative to the pointing axis (several telescopes on one mount)" with two unlabeled numeric fields (tooltips only: east, north), GUIDING dropdown with an empty first item (screenshot shows a blank box, no "None" label), "Guiding settings" expander (Corrections go to / Guide port / Device id / Guide exposure / Dither every / Dither (guide px) / Settled below), then WHAT THE SCOPE DOES BY ITSELF (reject frames; centre after slew with tolerance; flip at meridian with minutes), "Autofocus: when to refocus" expander (start, filter change, every N min, temperature, stars grew %), "Combined scopes" expander.
- Blank Guiding combo: should read "No guiding".
- "Dither every (exposures)" and "Dither (guide px)" here duplicate "Dither (arcsec every shot)" in the Sky "More options". Which wins? Not stated.
- Mount as a checkbox list: use a drop-down (radio) unless nested scopes are intended.
- "Combined scopes (this scope is built from other scopes)" only appears with several scopes; a good idea but extremely confusing next to "several telescopes on one mount": two ways to put several optics together (several telescopes on one mount vs a combined scope). Explain, or hide in Advanced.
- "Flip at the meridian (German equatorial mounts; needs your site), this many minutes past it": a sentence as a checkbox label with a number in the middle; split into checkbox + labelled field.

**[Medium] "Advanced: mount pointers".** A third concept ("pointer") that the user is told they rarely need; but the pointers appear in the Sky target combo ("eq") and in Autofocus/Centring ("Mount"). Either hide pointers completely (auto-created, name = scope name) or never show them outside this expander. Also a mount-pointer "Define pointer" form (name + mount) that duplicates what saving a scope does.

**[Low] Heading numbers "1 - Your telescopes", "2 - Your scopes" look like a wizard but nothing locks or guides.** OK, but see above.

**[Low] Empty state: "Add a scope" disabled button looks identical to a dead button; hint line "Set up a telescope first." sits far below it.**

---

## Rig > Equipment (empty-rig-equipment, rig-rig-equipment, rig-equipment-connected; EquipmentView.axaml)

**[High] Contradictory empty/first-run state.** The page says "Nothing is plugged in yet. Start your INDI drivers from Rig > Drivers..." only when empty. In the simulated rig everything is already connected, so the user never learns that connecting is a step. The Sky checklist says "Equipment connected: mount and camera" while the mount drawer (drawer-mount) reads "Not connected: press Connect." for the same simulator (different run, but it shows the page can disagree with the checklist).

**[Medium] Identical or near-identical device names.** Two filter wheels: "CCD Simulator" and "Filter Simulator"; the first is the camera's built-in wheel with the same name as the camera, which looks like a duplicate row. Same for "Telescope Simulator" (a mount) which looks like a scope. Add the device's driver/INDI name and a coloured kind icon; sort by kind.

**[Medium] Status column is a mix of units/states:** "Idle - -0.8 C", "Closed", "Red", "Position 50000", "Tracking", "0 deg", "Safe". No header, so the column means different things per row. Add column headers (Device, Kind, State).

**[Medium] "Open" = open a device panel in a drawer.** The word "Open" is vague next to "Disconnect" (an action) -> "Details" or "Control". "Look again" -> "Rescan". "Connect all" is primary even when all are connected.

**[Low] No indication which devices are used by which scope/telescope, nor warnings for devices not assigned.**

---

## Rig > Site (empty-rig-site, rig-rig-site, 07e0, 07e2; SiteView.axaml)

**[High] "Site not set" yellow dot persists in the status bar while the site form is filled with defaults** (Name "Home", Elevation 0, Lowest altitude 15, the checkbox ticked). Latitude/longitude blank and only watermarked; nothing disables Apply or says "Enter latitude and longitude to continue". The "TONIGHT" card then says "site unknown / set the site's latitude and longitude" (two separate lines, one of which is orphaned at the bottom of an oversized card).
- Fix: highlight the missing fields (red outline), disable Apply until valid, and offer "Use the GPS" when a GPS device is connected (the GPS drawer shows a fix with coordinates yet the user still has to retype). Selecting a GPS in the dropdown should fill the fields.

**[Medium] Obstruction input is a hand-typed list "180:15, 200:35, 260:35, 280:15" (azimuth:altitude pairs).** The only help is a long label text and a watermark. A user cannot tell if it is a polygon or points, and what the 15 lowest altitude means vs. the obstructions. Provide a small editor on the horizon plot (click to add) or at least explain "north = 0, east = 90".

**[Medium] The checkbox "Give the mounts this location and the time" next to "Apply"** - what happens to the checkbox if you untick it after applying? When is it needed? A mount with no GPS needs it, so default on is fine, but the label reads like a one-off action. Rename to "Also send location and time to the mounts".

**[Medium] Page intro: "gives sidereal time, twilight, the Moon and planets..."** is a marketing paragraph. Replace with a single line: "Used for twilight, target visibility and meridian flips."

**[Low] TONIGHT card:** the time axis shows "now" at an arbitrary place; legend colours (blue band = ?, beige bars under = ?) are unlabeled. YOUR SKY plot duplicates the Sky overlay (see above). Several unlabeled monospace lines of body data follow.

**[Low] "Saved" green notice stays (rig-rig-site 07e2) with no timeout** while a toast says something else; there are two feedback mechanisms (inline notices and toasts) used inconsistently (Site uses inline, Set up uses a toast and inline, Profiles show "profile saved" in a red error style).

---

## Rig > Drivers (empty-rig-drivers, 07j; ProfilesView.axaml)

**[High] "Profiles" and "Equipment" and "Site" and "Set up" are four pages for one task: getting the rig ready.** Drivers is the true first step (start drivers, devices appear), yet it is the 4th item in the sub-menu. Reorder: Drivers (Connect), Equipment, Set up, Site, Calibration. Or add one "Rig" overview with the order.

**[High] A "Profile" is the central concept but the page begins with a blank dropdown and a greyed Start/Stop.** The explanation ("Start a profile and ELink runs the drivers itself (its own indiserver)...elink --profile NAME") is jargon-laden and refers to a command line. A beginner who has an INDI server already running elsewhere (the default flow in this screenshot: Connect to mesh at 127.0.0.1:29997) does not know if they need this at all. State: "Skip this page if your drivers are already running."

**[Medium] The two-pane picker.** "Installed drivers" list shows entries like "Shelyak Usis (Spectrographs, shelyak_usis)" - hundreds of unfiltered items, in no sensible order; "In this profile" shows only raw executable names ("indi_simulator_telescope"), not the readable names. The list highlights "iOptron SkyGuider Pro (Telescopes, indi_simulator_telescope)" for the search "simulator telescope" (wrong match shown first) and an "Add ->" button must be pressed; double-click is not mentioned. "Connect the devices" checkbox is ambiguous (connect them on start?).
- "indiserver port 7624" is an unexplained network detail; hide under advanced.

**[Medium] Colour semantics:** "profile 'UI rig' saved" shows in red (error colour). Delete profile is red but is the destructive one, good; but success text must not be red.

**[Low] "Name" is blank with no placeholder; Save profile is disabled until typed (good) but nothing says why.**

---

## Rig > Calibration (empty-rig-calibration, 07k; CalibrationView.axaml)

**[High] The form is a wall of unlabeled fields with an unrelated sentence below.**
- Fields: Camera, Kind, Exposure s, Frames, Filter (flats), Gain (empty = as set), ISO (DSLR), Flat level %. Which apply to which Kind is never said; a bias with exposure "0" works (screenshot shows 0 and a result "0.001 s"), flats need a filter and "Flat level %" (of what? full well? histogram?), darks need exposure and gain.
- The hint under the buttons reads "Cap the scope. Same exposure, gain and sensor temperature as the lights they are for." The first sentence ("Cap the scope.") is an instruction to physically cover the telescope, in the middle of a form, with no checkbox to say you did it. For flats the instruction would be different (panel/sky). The hint text changes with Kind, which is good, but "Cap the scope" here uses "scope" for the OTA, again.
- Fix: show only the fields that matter for the chosen Kind; add a dialog/step "Cover the telescope, then press Capture". Say what "Flat level %" means.

**[Medium] Intro text jargon: "combined into masters (each pixel's highest and lowest value left out)" and "The live stack takes off the dark that suits every frame (same camera, exposure, gain and temperature; else a bias)".** That is a description of the algorithm, not of what the user must do. Replace with: "Take dark, bias and flat frames once per camera. ELink applies them automatically."

**[Medium] MASTERS list shows one line of metadata "cam-CCD_Simulator - Bias - 0.001 s - gain 30 - 0.0 C - 1280x1024 - 3 frames - 2026-10-05" and a "Delete master" button that is greyed until a row is selected, with no row-selected affordance.** Show a delete icon per row, or name the selection state. The status line ("Done - master bias of 3 frames: bias-cam-CCD_Simulator-20261005082633") shows an internal file name.

**[Low] "Idle" status at the top of the page again (what it means: no capture running).**

---

## Advanced > Help (empty-adv-help, HelpViewModel glossary)

**[High] The glossary admits the vocabulary problem** ("Telescope", "Scope", "Image", "Frame", "Panel", "Depth per spot", "Turn", "Dither", ...) but it is buried in Advanced and not linked from anywhere (no "?" icon on the confusing labels). It is the one place defining "scope" vs "telescope"; it should be shown in-context (a "?" next to "1 - Your telescopes"/"2 - Your scopes").
- The Help page title says "What the words mean, and the keys" but the key list is below the fold. Add a quick-start section ("1. Rig > Drivers 2. Equipment 3. Set up 4. Sky") at the top. Help sits first in Advanced yet users need it first of all.

**[Low]** A glossary entry per Sky term is good, but contains jargon itself ("ASTAP or astrometry.net", "WCS", "HFR").

---

## Advanced > INDI properties (empty-adv-indi, 08; IndiBrowserView.axaml)

**[Medium] Developer tool as a main-menu item with raw output.** Cards are duplicated: the screenshot shows "Fast Exposure [CCD_FAST_TOGGLE]" twice in a row (looks like a bug); switch properties show both a state value ("Off") and a button labelled "On" that does the opposite? (Enabled shows "Off" with an "On" button; Disabled shows "On" with an "On" button): each row reads "Off [On]" which is ambiguous (state vs action). The Server dropdown shows "sim" (an internal id), and Device shows "Guide Simulator" while the earlier page lists the camera as "CCD Simulator". Fine for experts, so keep it under Advanced, but add a "Raw device properties for experts; changing them can break things" line and fix the toggle labelling.

---

## Advanced > Saving frames (empty-adv-saving; StorageView.axaml)

**[High] Frames are not saved unless you add each camera by hand here.** Default is "0 frames saved", the camera list is a blank dropdown, and the button "Save its frames" does nothing unless a camera is selected first. A user who images for an hour without visiting Advanced loses all data. This belongs with the scope settings ("Save frames to: [folder]", on by default) or at least a warning on Start in Sky ("No camera is saving frames").
- Wording: "SAVED SHOOTERS" / "Save its frames" / "shooters you pick": "shooter" is unexplained jargon (means camera). The Directory field shows a temp path twice (editable box plus an identical caption underneath). The "Use" primary button name is vague: "Set folder".
- RECENT card is empty with no placeholder text.

---

## Advanced > Manual live stack (empty-adv-stack, 07g; LiveStackView.axaml)

**[High] Overlaps the automatic live stack built into Sky.** "Use the frame from the Sky view" proves the redundancy. The page is a complete, separate stacking tool with its own field, output pixel scale, interpolation, memory limit, "Match sky levels", "Match brightness (other cameras, thin cloud)", "Reject outliers beyond sigma", "One stack per filter", "Calibrate", "Registration (Auto/Pointing)", "Frame scale hint", "Frame angle", Bayer options, Start/Stop/Empty/Refresh. Some are shown in the empty state with defaults that differ from the Sky page (Width 1.5 vs 1.95; pixel scale 3 vs 20). Very heavy for a feature the overview says the scope does automatically ("Build the picture while shooting" checkbox in Sky More options).
- The "FRAMES" source list shows "cam" and "main": a telescope and a scope in the same list (07g), not cameras. In the empty state the list is blank; "Idle"/"Stopped"/"Idle" status text sits in a different font on the right edge.
- Jargon: WCS, plate solve, sigma, interpolation (upscaling), Bayer, super pixel, "registration".
- Fix: demote to an expert page or merge into the Sky stack options. Rename "Manual live stack" to "Custom stack".

---

## Device drawers (drawer-*.png, Camera/Dome/FilterWheel/Focuser/Gps/mount/Rotator/Weather; *PanelView.axaml)

**[Medium] Drawer opens over the page and hides content with no dim.** The mount drawer covers the right Sky panel and truncates the chart toolbar ("Layer"); on Scopes it covers the scope's state fields ("Moun"). Opening Equipment > Open on a device while you are mid-form hides it. Provide a dim/scrim or push the content.

**[Medium] Drawers show raw values and clipped text.** Mount: STATE "Disconnec" is cut off, PIER / EPOCH value is clipped; Focuser MOVING shows "False"; Weather shows "Weather 0, Humidity 0, Wind 0 ... Ok" with no units for "Weather" (0?); GPS time prints "2026-10-05T08:24:34" raw. Camera: "BIN / GAIN 1x1 / 30" two values in one cell, "SENSOR 1280x1024, 5.2 um, 16 bit" wraps over three lines.

**[Medium] Mount drawer is the only device whose actions are disabled when disconnected, but its connect button is at the top-right, small and ghost-styled ("Connect" vs the green "Disconnect" elsewhere).** The yellow bar "Not connected: press Connect." is good; copy it for other devices.

**[Medium] Drawers allow direct control of devices that the scope controls (cooling in the Camera drawer vs "Cool/Warm up" on the scope page vs "Cool to" in the telescope form; Expose in the camera drawer vs Manual control; Move in the Focuser drawer vs Autofocus).** Fine for manual use, but warn: "This device belongs to scope X; the scope may change it".

**[Low] Close "x" only; Escape also closes (undocumented). Drawer title shows kind in tiny grey ("Mount", "Gps"). "Gps" is title-cased oddly vs "GPS Simulator" in the heading.**

---

## Status strip and global chrome (all screenshots; StatusBarView.axaml, MainWindow.axaml)

**[High] "Join the mesh at [host] [port] [Connect]" is the first thing on screen, and the status "connected to 127.0.0.1:29997" at the far right shows it is already connected.** A first-time user does not know what a mesh is or whether to press Connect. When already connected the bar should collapse to a small "Connected" chip; the Connect button remains bright blue (primary) even though nothing needs doing. Port number shows as a plain text box that differs between screenshots (29997/29995/29993): users will think they must change it.

**[Medium] Status strip content: "Site not set" (yellow dot), scope chips "main Idle", right chip "2 things to set up".** When the site is set it becomes "Daylight, dark from 19:57 - Moon 30%", and the right chip "Ready" (green). Mixed information (night state, scope states, setup) with the setup chip far right; nothing says that "Ready" means "all four checklist items are done". The scope chips are clickable (go to the scope), the night text clickable (go to the site), the setup chip opens a flyout, but none looks clickable. Add hover affordance or tooltips ("Opens Rig > Site").

**[Medium] Toast notifications overlap the page's bottom-right actions and stack (two toasts at once in 07i, covering Sky's Hide panel button and part of cards).** They are not dismissible by timeout in screenshots; messages ("Telescope 'cam' saved", "Scope 'guided' saved: it is under Scopes now") are informative but the second one is the real next-step hint: also show it inline.

**[Medium] Left rail sub-menu duplicates.** Rig has 5 sub-pages; Advanced has 4. "Equipment" and "Drivers" are both "which devices exist", "Site" and "Calibration" are rig data. Sub-menu names are one word each with no icons.

**[Low] Keyboard shortcuts Ctrl+1..4 and Esc exist, undiscoverable.**

**[Low] Contrast.** Hint text (grey on dark navy, ~11-12 px) in cards, e.g. the page intro paragraphs, the guiding "RA (blue)..." line and the section labels (TARGET, IMAGE) is dim and small; the greyed (disabled) primary buttons (Start, Save profile, Delete profile) are hard to tell from enabled secondary ones.

---

## Top 10 things to fix first

1. **Unify the vocabulary (telescope / scope / train / shooter / pointer / mount / primary / guide scope).** Pick one noun per concept, and apply across the checklist, forms, buttons, glossary, drawers.
2. **Replace the mandatory telescope-then-scope two-step with a one-screen "Add a scope" quick setup** (mount + one optical train, defaults filled), keeping the full model under "Advanced".
3. **Make first-run readiness a single, obvious path** (status chip + checklist + notice are three sources): number the steps, order pages accordingly (Drivers/Equipment -> Set up -> Site), make rows clearly clickable, and say which items are optional (Site).
4. **Frames are not saved by default; the Saving page is hidden in Advanced and needs each "shooter" added by hand.** Move to the scope/Sky flow, default on, and warn on Start.
5. **Remove duplicated controls at different levels:** autofocus/centring tools on Scopes vs "What the scope does by itself"; guiding test buttons; dither in two places (Sky "arcsec", Scope "guide px", "every N exposures"); manual live stack vs the automatic one; flat horizon plot on Sky and Site.
6. **Show human state, not internal enums** ("OnTarget, on target", "Moving False", "Idle" for a mount while exposing, "Acquiring" next to "not guiding", "Disconnec" clipped). Define the 6 scope states once.
7. **Sky Image card:** replace "0 / 0" width/height with an explicit size selector; rename "Depth per spot", remove "Layers" free-text mini-language from a beginner form, rename the chart's "Layers" button, label the unlabeled "eq/main" combo.
8. **Site page:** validate lat/long (disable Apply, mark missing fields), fill from a connected GPS, explain the horizon obstruction format or provide an editor; make "Site not set" go away in a visible way after success.
9. **Calibration form:** show only fields relevant to Kind, add an explicit "cover the telescope" step, explain Flat level %, remove algorithm talk from the intro.
10. **Fix layout defects:** drawer clipping and no scrim, toasts over the Hide panel button, overlapping labels in the flat-horizon overlay, duplicate "Fast Exposure" cards in INDI properties, low-contrast 11 px hint text, success messages in red (profiles), blank (not "None") dropdowns in the Guiding and Saved shooters selectors.

## Inconsistent terms

| Concept | Names used in the UI |
|---|---|
| The optical path (OTA + cameras + wheel + focuser) | telescope (Rig > Set up), train ("The train ramps the sensor", memory/spec), guide scope, shooter (Autofocus, Saving frames, Centring), "Telescopes on it" |
| The mount + optics + automation unit | scope, "Main scope", telescope ("Telescope Simulator" is a mount), rig (the example name "Backyard rig", Rig menu, "your rig"), "Your scopes", pointer |
| The mount | mount, pointer ("Advanced: mount pointers", the "eq" combo), telescope ("Telescope Simulator"), "Mount" field showing the scope's phase |
| A camera | camera, shooter, "primary camera" / "guide camera", Camera drawer, "CCD Simulator", "cam" |
| The guider | guider, "guide scope", "guide camera", OAG ("off-axis guider"), "guide port", guide output "Pulse" |
| Imaging goal | Image, Frame (yellow rectangle), field (Manual stack "FIELD (VIRTUAL FOV)"), target (TARGET card, "target minutes" in code), job |
| Part of the sky / tile | panel, spot ("Depth per spot"), visit ("Stop after (shots)"), shot, exposure, frame (also a camera frame!) |
| Total exposure goal | Depth per spot, "Stop after (shots)", target, "Each shot (s)" |
| Dither amount | "Dither (arcsec every shot)" (Sky), "Dither (guide px)" and "Dither every (exposures)" (scope), "Dither now" |
| Centring | "Centre on the target after each slew" (scope form), "Centre automatically after every slew" (Centring page), "Centre now", "Centre the chart here", "Centre the primary" |
| Queue | "Queue" (Sky), schedule (internal/Help), "Add to queue", "Run" |
| Live stack | "live stack" (Help), "Build the picture while shooting", "stacked image", "Manual live stack", "The stacked image" (layers) |
| Layers | chart layers button (Sky toolbar), image "Layers (optional)" (More options), layers in the plan, filter layers |
| Connect / drivers | "Join the mesh", Connect, Equipment "Connect all", Drivers "Start" (profile), "Connect the devices" |
| Idle / state words | Idle, Ready, On target, OnTarget, Exposing 1 of 2, Done, Not started, Stopped, Acquiring, Tracking |
| Saving | "Saving frames" (page), "frames saved", "Save its frames", "SAVED SHOOTERS" |
| Rig vs Set up vs Equipment | Rig (top menu), "Set up" (its first sub-page), "Equipment" (devices), "your rig" (profile) |

## Features / pages to merge or remove

- **Merge "Set up" telescope + scope into one flow** (see top 10 #2); remove "Advanced: mount pointers" from user view (auto-create pointers).
- **Merge Equipment + Drivers** into one "Devices" page (drivers on top, connected devices below), since both answer "what hardware is there".
- **Remove the Autofocus and Centring panels from Scopes** (or make them per-scope tools generated from the scope's own settings); keep only the "when to refocus / centre" settings in the scope form.
- **Merge Manual live stack into the Sky image's stack options** (or hide it as an expert page); remove the duplicated defaults.
- **Move Saving frames into the scope/Sky flow** with a default folder; keep Advanced only for per-camera overrides.
- **Move Calibration under the scope/telescope** it belongs to (cameras), or keep in Rig but link from Set up as a step.
- **Single flat-horizon plot** (Site only, or Sky only).
- **Drop the Stellarium expander** from the Sky panel (move to Advanced or a connection section).
- **Fold Help into a "?" on each page** plus a short quick-start; keep the glossary in Help.
- **Hide INDI properties** behind an "Expert mode" setting; it is a developer view.
- **Drop the "Join the mesh" bar** once connected (collapse to a status chip) and when the app is started in station mode.
