# Setup Wizard & Floorplan Editor (fork features)

This fork adds two major UI features on top of upstream ESPresense Companion: a **calibration
setup wizard** (Calibration page → "Setup" tab) and a **floorplan editor** (main map → View/Edit
toolbar). Both write directly to `config.yaml` through the section-rewrite API; changes apply
within ~1 second via the normal config poll, no restart needed.

## Safety net

Before the first editor write per app run, a timestamped backup of `config.yaml` is created next
to it (`config.yaml.bak-floorplan-<timestamp>`). The newest 5 backups are kept, older ones are
pruned automatically. Note: rewriting a section re-serializes it from the object model — comments
and hand-formatting **inside** the rewritten sections are lost (the backups preserve them).

## Setup tab (Calibration page)

| Card | What it does |
|------|--------------|
| **Node Health** | Offline nodes (2-minute grace so a rebooting node doesn't alert), "online but stuck" detection via a last-telemetry-received timestamp, mixed-firmware detection. |
| **Configuration Checks** | Floor bounds entered min/max-swapped (the classic ceiling-height-instead-of-absolute-Z mistake), degenerate/too-small room polygons (<1 m²), overlapping rooms beyond a shared edge, nodes outside their floor bounds, and an RSSI placement sanity check: a node whose *median* smoothed distance error across same-floor neighbors exceeds 50% probably has wrong coordinates entered. |
| **Calibration** | "Calibrate now" wakes the optimizer immediately (skips the full warmup after a restart too) — replaces manually lowering `interval_secs` and restarting. |
| **Problem Pair Suggestions** | Same-floor node pairs whose distance error stays persistently high (≥2 h observation, error above 40% for ≥75% of recent samples). Confirming a pair appends it to `optimization.excluded_pairs`. Moving a node resets its pairs' statistics. |
| **Walk Test** | Place a tracked device at a *known* position and record for 30–900 s. The recorded point acts as an extra reference transmitter with known coordinates in every calibration fit (the beacon's reference RSSI is self-calibrated at record time). Raw per-tick readings are stored for the locator replay. Points persist in `.storage/walktest-points.json`; points whose receiving node moved afterwards are ignored automatically. Placement suggestions point to the currently worst-calibrated pairs. Coordinates can be typed in, taken from a suggestion, or picked on the map (see below). |
| **Optimizer Tuning** | Fits each optimizer/penalty/absorption-bounds candidate on collected measures and scores it on held-out node pairs (3-fold cross-validation, folds split by whole pair to avoid leakage). Walk-test measures count as extra data. Apply writes the winning configuration. |
| **Locator Tuning** | Replays the walk-test points' raw per-tick readings (real live noise at a known true position) through nadaraya_watson bandwidth/kernel candidates, using the live locator's own math. Scored on mean 2D error **and** jitter (how much the estimate wanders while the beacon sits still). Caveats: stationary noise only; the scenario/Kalman smoothing above the locators is not replayed. |
| **Settings** | Edits every remaining config section: optimization (interval, snapshot window, limits, penalty, optimizer mode), locators (toggles, NW bandwidth/kernel, weighting sigmas), timeout/away_timeout/device_retention, filtering (Kalman), history, map, gps, mqtt. Deliberately not exposed: `weights.correlation/rmse` (they define the scoring target) and locator `floors` arrays. |

### Picking a walk-test spot on the map

The main map has a walk icon button directly below the **View**/**Edit** toggle (view mode only).
Activating it turns the cursor into a crosshair; clicking the map jumps straight to the Setup tab
with the walk-test form prefilled: X/Y from the clicked point, Z set to the clicked floor's base
height (its lower z bound — adjust it if the device sits on furniture). Clicking the button again
or switching to Edit mode cancels the picker. The deep link is a plain URL
(`/calibration?tab=setup&walk_x=..&walk_y=..&walk_z=..`), so it is bookmarkable too.

## Floorplan editor (main map)

Toolbar: **View** (read-only) / **Edit**. Inside Edit, node editing is a sub-tool ("Edit nodes"
button) next to the room tools; switching sub-tools keeps unsaved edits.

- **Nodes**: drag markers to reposition, numeric X/Y/Z panel, click-to-place new nodes (the id
  field suggests nodes already announcing over MQTT but not yet placed), delete. The node's
  `room` field is recomputed from the new position on save.
- **Rooms**: click to select, drag corner handles, click an edge midpoint dot to insert a corner,
  double-click a corner to remove it (min 3), rename via the panel, draw new rooms click-by-click
  (the closing edge back to the first point is previewed — Finish closes automatically), delete.
- **Floors**: add (x/y prefilled from the current floor, z stacked on the highest floor; jumps to
  the new tab), rename, delete (blocked while nodes still reference the floor), bounds via
  "Draw bounds" (click two opposite corners on the map) or numeric fields.
- **Tracing image**: load a scanned floor plan as a session-only layer in *map coordinates* (pans
  and zooms with the map). Recommended workflow: **Measure scale** (click both ends of a
  known-length feature, enter the real distance — comma or dot decimals both work) → **Set
  origin** (click the point that should become map 0,0; the image is then locked against
  moving/rotating until the origin is re-set) → **Draw bounds** → draw rooms over it. Rotation
  (90° steps + fine degrees) is available before the origin is set.
- **Zoom**: +/−/reset buttons bottom-right (wheel zoom needs Shift held).

## Devices page additions

- The delete button also removes the device's exact-id entry from the config `devices:` list
  (wildcard patterns are never auto-removed by a single-device delete).
- "Config: Tracked & Excluded Devices" panel manages the `devices:`/`exclude_devices:` lists
  directly (name/id entries, wildcard ids like `phone:*`).

## API endpoints (all fork-added)

```
GET  /api/wizard/validation                     health/geometry/placement issues
GET  /api/wizard/health                          node health gate
POST /api/wizard/calibrate-now                   wake the optimizer immediately
GET  /api/wizard/excluded-pairs/suggestions      persistent bad pairs
POST /api/wizard/excluded-pairs                  append confirmed pairs
GET/POST /api/wizard/walktest/{status,start,stop,cancel,suggest}
DELETE   /api/wizard/walktest/points/{id}
GET/POST /api/wizard/autotune/{status,start,apply}
GET/POST /api/wizard/locatortune/{status,run,apply}
GET/POST /api/wizard/settings                    all editable config sections
GET/POST /api/config/devices                     tracked/excluded device lists
POST   /api/floorplan/node        upsert node position (room recomputed)
DELETE /api/floorplan/node/{id}
POST   /api/floorplan/room        upsert room polygon/name
DELETE /api/floorplan/room/{floorId}/{roomId}
POST   /api/floorplan/floor       add/rename floor
DELETE /api/floorplan/floor/{id}  blocked while nodes reference it
POST   /api/floorplan/floor-bounds
```

## Running the backend tests

`dotnet test` couples frontend (pnpm/playwright) tests via an MSBuild target that currently fails
on a pnpm build-script policy. Run the backend suite directly instead:

```
dotnet build tests/ESPresense.Companion.Tests -c Release
DOTNET_ROLL_FORWARD=LatestMajor dotnet vstest \
  tests/ESPresense.Companion.Tests/bin/Release/net8.0/ESPresense.Companion.Tests.dll
```
