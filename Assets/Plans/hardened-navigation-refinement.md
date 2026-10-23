# Phase 1: Correctness & Hardening

## 1. Navigation & Relocalization Constraints
### NavigationSessionManager
- **Description**: Restrict the `nodes[0]/nodes[1]` auto-alignment fallback to the Editor only. On physical devices, enforce a "Relocalized" state before allowing navigation.
- **Approach**:
  - Wrap the auto-alignment block in `StartNavigation` with `#if UNITY_EDITOR`.
  - Add a check: if not in Editor and `!QRAnchorRelocalizer.Instance.IsRelocalized && (GpsRelocalizer.Instance == null || !GpsRelocalizer.Instance.HasPerformedInitialSnap)`, call `OnNavigationFailed("Scan a marker or wait for GPS")` and return false.
  - Null-guard `GpsRelocalizer.Instance` throughout. Note: `GpsRelocalizer` must be attached to the `[Managers]` GameObject in the scene.

### QRDebugMenu
- **Description**: Secure the debug menu and improve simulation accuracy using the graph.
- **Approach**:
  - Wrap the entire `OnGUI` and related UI methods in `#if UNITY_EDITOR || DEVELOPMENT_BUILD`.
  - In `SimulateScan`, find the direction the path leaves the node by checking `MapDataManager.Instance.CurrentMap.edges` to find neighbors. Use the first neighbor found to compute `pathYaw`.
- **Assigned role**: developer
- **Dependencies**: None
- **Parallelizable**: Yes

## 2. GpsRelocalizer: In-Motion Yaw & Alignment
- **Description**: Implement absolute yaw correction and robust fresh-map initialization.
- **Approach**:
  - Update `targetYawOffset` logic: `targetMapRootYaw = vioAngle - gpsAngle`.
  - Use `mapRoot.RotateAround(arCameraTransform.position, Vector3.up, clampedDelta)` for rotation.
  - Implement gating in `CheckInMotionDisplacement`:
    - Record camera positions during the window.
    - Reject if perpendicular distance from chord > 15% of length.
    - Reject if VIO/GPS distance difference > 30%.
  - Add `public void ResetRelocalizationState()`: clears `hasTargetYaw` and `HasPerformedInitialSnap`.
  - Add `public void ClearInMotionTarget()`: clears `hasTargetYaw` and resets motion tracking state (but leaves `HasPerformedInitialSnap` intact).
  - Add `public void MarkExternallyAligned()`: sets `HasPerformedInitialSnap = true`.
  - **Compass**: Add debug readout showing `compass.trueHeading` vs `arCamera.eulerAngles.y`. Do not rely solely on `headingAccuracy` for validity.
- **Assigned role**: developer
- **Dependencies**: None
- **Parallelizable**: Yes

## 3. QRAnchorRelocalizer: Multi-Marker & Cooldowns
- **Description**: Robust pose sampling with cooldowns and lifecycle management.
- **Approach**:
  - Cooldown: `perAnchorCooldown = 12.0f`. Use `Dictionary<string, float> lastRelocalizedTimestamps` (set on success and dismissal; default `-999f`).
  - `Update()`: Process only the **best candidate image** per frame (closest image that passes all gates).
  - Remove sample processing from `trackablesChanged` event handlers (relying only on per-frame `Update` sampling).
  - Compute "is this a new/changed anchor" *before* overwriting state in `FinalizeAlignment`.
  - In `FinalizeAlignment`, call `GpsRelocalizer.Instance.ClearInMotionTarget()`.
  - **Lifecycle**: Store `previousARSessionState`. On reset (e.g., `SessionInitializing`):
    - `ResetRelocalization()`.
    - `GpsRelocalizer.Instance?.ResetRelocalizationState()`.
    - `NavigationSessionManager.Instance?.StopNavigation()`.
    - `ResetRelocalization()` must also clear the cooldown dictionary.
  - Fix lambda leak: define `HandleMapLoaded(CampusMapData _)` wrapper and use it for `MapDataManager.Instance.OnMapLoaded` subscription/unsubscription.

## 4. AndroidSensorManager & UI Refinements
### AndroidSensorManager
- **Description**: High precision timestamps and Editor simulation.
- **Approach**:
  - `LastFixTimestamp` as `double`. `updateDistanceInMeters` defaults to `0`.
  - Editor simulation: changing timestamp and position jitter (~0.05m) every frame.

### AdminUIController & UserNavigationUI
- **Description**: Stats timers, navigation returns, and approximate alignment feedback.
- **Approach**:
  - **AdminUIController**: 3-second result message timer for `statsText`.
  - **UserNavigationUI**: 
    - No `ShowSearchPanel` while navigating.
    - Show anchor label from map in HUD.
    - Subscribe to `OnNavigationEnded` -> return to Start/Search panel.
    - Return to Start panel after `ARSession` reset.
    - Show "Approximate Alignment" notice when only GPS has snapped.

## 5. NodeRecorder: Workflow & Correctness
- **Approach**:
  - **Node ID**: Implement `NextNodeId()`: `max(numeric suffix) + 1`. (Phase 1).
  - **API**: `StartRecording(string startNodeId = null)` and `StopAndSaveRecording(string endNodeId = null)`. Remove silent 3.5m reuse.
  - **Dialogs**: Runtime-instantiated, admin-only, phone-scaled. (Nearest Node, Node Picker, End Existing, Join Path).
  - **Auto-drop**: Pause while any dialog is open.
  - **Join Semantic**: Split edge, connect `lastRecordedNode` to new junction, then continue recording from junction. Set lat/lon on new junction node.
  - **Spur Pattern**: `RecordQRAnchor` picks closest image if payload null. Anchor connects via its own path; does NOT change `lastRecordedNode`.
  - **MarkDestinationAtAnchor**: Destination created at user's current averaged position, linked to anchor.
  - **Fresh Map**: Wait for good accuracy fix. Set origin. Call `GpsRelocalizer.MarkExternallyAligned()`.

## 6. Graph Constraints & Documentation
- **CampusMapData**: Enforce `NodeType` exclusivity. Add `savedTimestamp` (Phase 1).
- **Architectural Records**: Update `DECISIONS.md` with Spur pattern, chord rejection, and Clear Map note (bundled map returns on restart).

# Phase 2: Visuals & Persistence

## 7. MapDataManager
- **Description**: Timestamp-based loading and silent autosave.
- **Approach**:
  - Prefer bundled map only if its `savedTimestamp` is newer.
  - **Autosave**: Silent flag to bypass `OnMapLoaded` event.

## 8. Ground Height & Search
- **Approach**:
  - `markerMountHeight` (1.45m), `phoneHoldHeight` (1.4m), `groundYOffset` (0.02).
  - Anchor nodes store `y = markerMountHeight`.
  - `FindNearestNode`: Horizontal distance, prefer non-anchors.

# Implementation Steps (Phase 1)
1. **Refine NavigationSessionManager startup checks and QRDebugMenu safety.**
2. **Update GpsRelocalizer in-motion yaw damping and fresh-map alignment.**
3. **Harden QRAnchorRelocalizer with best-candidate logic, cooldowns, and reset hooks.**
   - **Dependencies**: Step 2.
4. **Update AndroidSensorManager and implement AdminUIController result timers.**
5. **Implement NodeRecorder enhancements: NextNodeId, edge splitting, Median averaging, and recording blocks.**
   - **Dependencies**: Step 4.
6. **Implement dynamic authoring dialogs and UserNavigationUI navigation lifecycle.**
   - **Dependencies**: Step 3, Step 5.
7. **Update PROGRESS.md and DECISIONS.md.**

# Verification & Testing (Phase 1)
- **Cooldowns**: Verify 12s lock. Verify `ResetRelocalization` clears cooldown for immediate rescan.
- **Multi-Marker**: Visible two markers; confirm only the best candidate is sampled.
- **Join/Split**: Verify edge splitting connects current recording and preserves path flow.
- **Graph Hops**: Junction link skips nodes within 3 graph hops.
- **Routing**: Confirm anchor spurs do not create route detours.
- **GPS**: Straight 20m walk verify yaw alignment. Node ID uniqueness.
- **Compass**: Check debug readout against known direction on phone.

