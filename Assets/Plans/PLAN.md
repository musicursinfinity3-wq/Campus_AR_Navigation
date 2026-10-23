# Hardened Navigation and Authoring Refinement Plan

## Phase 1: Correctness & Hardening

### Step 1: Navigation Lifecycle & Debug Menu Safety
- [ ] 1.1 **NavigationSessionManager**: Wrap auto-alignment fallback in `StartNavigation` with `#if UNITY_EDITOR`.
- [ ] 1.2 **NavigationSessionManager**: Block start on device if `!QRAnchorRelocalizer.Instance.IsRelocalized && (GpsRelocalizer.Instance == null || !GpsRelocalizer.Instance.HasPerformedInitialSnap)`. Fire `OnNavigationFailed("Scan a marker or wait for GPS")`.
- [ ] 1.3 **NavigationSessionManager**: Null-guard `GpsRelocalizer.Instance` and `NavigationSessionManager.Instance` throughout.
- [ ] 1.4 **QRDebugMenu**: Wrap `OnGUI` and UI methods in `#if UNITY_EDITOR || DEVELOPMENT_BUILD`.
- [ ] 1.5 **QRDebugMenu**: Update `SimulateScan` to find neighbor nodes via `edges` in `MapDataManager.Instance.CurrentMap` instead of simple array index.
- [ ] 1.6 **AStarPathfinder**: Update `FindNearestNode` to exclude nodes of type `QRAnchor`.

### Step 2: GpsRelocalizer In-Motion & Alignment
- [ ] 2.1 **In-Motion Gates**: Implement displacement gate `displacement >= Mathf.Max(15f, 3f * currentAccuracy)`. Require accuracy `< inMotionMaxAccuracyMeters` at both start and end of movement window.
- [ ] 2.2 **In-Motion Stability**: Reject if max perpendicular distance from recorded camera positions to chord > 15% of chord length, or if VIO/GPS distance difference > 30%.
- [ ] 2.3 **Yaw Math**: Use absolute headings `targetMapRootYaw = vioAngle - gpsAngle` with `mapRoot.RotateAround(camera.position, Vector3.up, delta)` clamped by `maxYawRateDegreesPerSec`.
- [ ] 2.4 **Compass Reliability**: Add debug readout showing `compass.trueHeading` vs `arCamera.eulerAngles.y`. If unstable (two readings 1s apart differ by > 10°), retry/wait instead of skipping permanently.
- [ ] 2.5 **State Hooks**: Implement `ResetRelocalizationState()` (full reset), `ClearInMotionTarget()` (clears target/resets tracking only), and `MarkExternallyAligned()`.

### Step 3: QR Relocalization Hardening
- [ ] 3.1 **Best Candidate**: In `Update()`, process only the best candidate image per frame using hysteresis (keep current target unless another is 0.5m closer or current fails a gate: tracking state, distance, or viewing angle). Reset sample buffer when best candidate changes.
- [ ] 3.2 **Sampling Optimization**: Remove sample processing from `trackablesChanged` event handlers (relying entirely on per-frame `Update` sampling). Keep `removed` handler for cleanup.
- [ ] 3.3 **Cooldowns**: 12s per-anchor cooldown via `Dictionary`. `ResetRelocalization()` clears dictionary. Cooldown entry set on success AND on dismissal.
- [ ] 3.4 **Synchronization**: `FinalizeAlignment` calls `GpsRelocalizer.Instance.ClearInMotionTarget()`. Fire `OnRelocalizedToAnchor` only on first snap or when marker identity changes. Null-guard `GpsRelocalizer.Instance`.
- [ ] 3.5 **ARSession Reset**: Store `previousARSessionState`. Perform full reset (reloc, gps, stop navigation, clear cooldowns) specifically on `SessionTracking` -> `SessionInitializing`. Keep other safety resets.
- [ ] 3.6 **Lambda Safety**: Store `OnMapLoaded` handler reference for reliable unsubscription.
- [ ] 3.7 **Tracking Distance**: Set `maxTrackingDistance` default to 1.5m. Add on-screen scan guidance: "Hold the phone about an arm's length from the sign".
- **Dependencies**: Step 2.

### Step 4: Sensor Precision & Admin UI Timers
- [ ] 4.1 **AndroidSensorManager**: Change `LastFixTimestamp` to `double`. Default `updateDistanceInMeters` to `0`.
- [ ] 4.2 **AndroidSensorManager**: In Editor simulation, advance timestamp every frame and add 0.05m jitter to position.
- [ ] 4.3 **AdminUIController**: Implement a coroutine-based message timer so `statsText` results (e.g., "Linked") persist for 3s before reverting to telemetry.

### Step 5: NodeRecorder: Authoring Logic Refinement
- [ ] 5.1 **Recording Guards**: Block start if `Status != Ready`. Block appending if `originLatitude == 0`. Block recording if `MapRoot` unaligned (except for fresh maps).
- [ ] 5.2 **Fresh Map Bootstrap**: Wait for good accuracy GPS fix. Set `originLatitude/Longitude`. Initialize `MapRoot` (yaw = `camera - compass`, position = `camera`). Call `MarkExternallyAligned()`. Ensure map is visible to `GpsRelocalizer`.
- [ ] 5.3 **API Update**: Change `StartRecording(string startNodeId = null)` and `StopAndSaveRecording(string endNodeId = null)`. Remove silent 3.5m node reuse.
- [ ] 5.4 **GPS Median Averaging**: Sample 20-30s window. Require `>= 10` distinct fixes (`LastFixTimestamp`). Store median. Abort with "Insufficient GPS data" if count not met.
- [ ] 5.5 **ID Generation**: Implement `NextNodeId()` based on `max(numeric suffix) + 1`.
- [ ] 5.6 **Spur Pattern**: `RecordQRAnchor` picks closest tracked image if payload `null`. Connect anchor spur to nearest non-anchor node by horizontal distance. A* must never route through an anchor node (exclude anchors from routing). `AdminUIController.RecordQRAnchorForCurrentSession` passes `null`.
- [ ] 5.7 **Junction Linking**: Skip the last 5 recorded nodes and any node within 3 graph hops of `lastRecordedNode`.
- [ ] 5.8 **SplitNearestEdge**: Implement logic to reuse endpoints within 3m or insert `Intersection` with lat/lon and new edge IDs. Preserve `isAccessible/isStairs`. Connect current recording and continue from junction.
- [ ] 5.9 **Helpers**: `MarkDestinationAtAnchor` creates a node at user's current averaged position, linked to the anchor. Remove `MarkQRAnchor`.
- **Dependencies**: Step 2, Step 4.

### Step 6: UI, Dialogs & Lifecycle
- [ ] 6.1 **Authoring Dialogs**: Implement code-driven, phone-scaled dialogs for Start-Node selection, Node Picker, End-Existing, and Join-Path. Dialogs are admin-only.
- [ ] 6.2 **UI Flow Protection**: No `ShowSearchPanel` mid-navigation. Pause auto-drop while any dialog is open.
- [ ] 6.3 **HUD & Identity**: `UserNavigationUI` shows scanned anchor's real label lookup from map. Return to Start panel on session reset.
- [ ] 6.4 **Status Feedback**: Subscribe to `OnNavigationEnded` to return to search/start panels. Show "Approximate Alignment" notice when only GPS snap is active.
- **Dependencies**: Step 3, Step 5.

### Step 7: Final Phase 1 Documentation
- [ ] 7.1 **Graph Constraints**: Document anchor-spur-plus-destination pattern and MarkDestinationAtAnchor helper. Add `savedTimestamp` to `CampusMapData` (set on every save).
- [ ] 7.2 **Progress Tracking**: Update `PROGRESS.md` and `DECISIONS.md` with Spur pattern, chord rejection, and the "Clear Map" restart note.

## Phase 2: Visuals & Persistence

- [ ] 8.1 **Map Loading**: Compare `savedTimestamp`; prefer bundled map if newer.
- [ ] 8.2 **Autosave**: Every 5 nodes and in `OnApplicationPause`. Must be silent (no `OnMapLoaded` event).
- [ ] 8.3 **Ground Alignment**: Anchor nodes store `y = markerMountHeight`. GPS snap rule: `MapRoot.y = camera.y - phoneHoldHeight`. `groundYOffset = 0.02`.
- [ ] 8.4 **Path Calculation**: `RemainingDistance` calculated along the full route path.
- [ ] 8.5 **Visual Refinement**: `flatten arrows (y=0)`. Move `currentTargetIndex` by closest segment *ahead* of user. Permanent culling of passed arrows via `segmentIndex`.
- [ ] 8.6 **Search Refinement**: `FindNearestNode` uses horizontal distance.
- [ ] 8.7 **Map Management**: `ResetToBundledMap()` and update `DeleteSavedMap` warnings.

---

## Mapping Table (User Requirements to Steps)

| Requirement | Step(s) |
| :--- | :--- |
| GpsRelocalizer Gates (15m, 3x acc, Chord 15%, VIO/GPS 30%, start/end acc) | 2.1, 2.2 |
| Absolute Yaw & Max Rate Clamp | 2.3 |
| Compass retries & instability | 2.4 |
| QR Hysteresis / Best Candidate per frame (closest, 0.5m diff) | 3.1 |
| Remove sample processed from event handlers | 3.2 |
| 12s Cooldown (Success/Dismiss) & Reset cleanup | 3.3 |
| Gps Synchronization & Overwrite check | 3.4 |
| ARSession Reset Logic (Tracking -> Initializing) | 3.5 |
| GPS Median (10 distinct fixes, 20-30s, Abort message) | 5.4 |
| Node ID Numeric Suffix | 5.5 |
| StartRecording/StopAndSave API & Start/End Flow | 5.3, 5.5, 6.1 |
| Pause auto-drop for dialogs | 6.2 |
| SplitNearestEdge (new IDs, 3m reuse, flags, Join semantics) | 5.8 |
| LinkToNearestJunction (skip 5 nodes, 3 hops) | 5.7 |
| MarkDestinationAtAnchor / Remove MarkQRAnchor | 5.6, 5.9 |
| UserNavigationUI (No search panel while nav, real label, lifecycle) | 6.2, 6.3, 6.4 |
| Phase 2 (Timestamp, Autosave, Height rules, Route Progress, Culling) | 8.1 - 8.7 |

---

## Verification & Testing (Phase 1)
- [ ] **QR Cooldown**: Verify 12s lock. Verify `ResetRelocalization` clears cooldown for immediate rescan.
- [ ] **Multi-Marker**: Visible two markers; confirm hysteresis and best-candidate (0.5m logic).
- [ ] **Authoring Flow**: Verify Start-Node dialog and End-Existing logic. Verify auto-drop pauses.
- [ ] **Join/Split**: Verify edge splitting connects current recording and preserves path flow.
- [ ] **Graph Hops**: Junction link skips nodes within 3 graph hops.
- [ ] **Routing**: Confirm anchor spurs do not create route detours.
- [ ] **GPS Yaw**: Straight 20m walk verify absolute yaw alignment.
- [ ] **Compass**: Check debug readout against known direction on phone.
- [ ] **Nav Block**: Verify block message on device.
- [ ] **GPS Median**: Verify median position logic with distinct timestamps.
- [ ] **Background/Resume**: Background/resume stops navigation and requires re-alignment.
- [ ] **New Map Alignment**: New map recorded on a phone lines up with a real walk.
- [ ] **Stale Yaw**: No stale GPS yaw after a QR scan.
- [ ] **Node Uniqueness**: Node ID uniqueness.
