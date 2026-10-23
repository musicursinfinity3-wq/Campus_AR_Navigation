# Project Overview
- **Game Title:** Campus AR Navigation (Benguet State University)
- **High-Level Concept:** An offline-first mobile AR wayfinding application that combines smartphone GNSS/GPS with ARCore visual-inertial odometry (VIO) to guide university visitors and freshmen across the ~2 km BSU campus. High-contrast wall-mounted image markers (QR codes) act as ground-truth anchor points that calibrate and snap the virtual navigation tree (`[MapRoot]`).
- **Players:** Single-user (visitors, students, and faculty navigating campus on handheld Android smartphones).
- **Inspiration / Reference Games:** Google Maps Live View AR, Airport / Indoor AR Wayfinding Kiosks.
- **Tone / Art Direction:** Clean, utilitarian, high-contrast accessible wayfinding with high-visibility HUD indicators and chevron ground guides.
- **Target Platform:** Android (ARCore-compatible mobile devices, min SDK 24+, offline operation).
- **Screen Orientation / Resolution:** Portrait 1080x1920 (Handheld smartphone).
- **Render Pipeline:** Universal Render Pipeline (URP - Performant).

---

# Game Mechanics

## Core Gameplay / Wayfinding Loop
1. **Calibration / Portal Entry:** The user arrives at a campus portal or building entrance and points the camera at a wall-mounted QR code sign.
2. **Quality Gated Detection:** The system detects the physical marker, confirms it is tracked with high confidence (`TrackingState.Tracking`), verifies it is within acceptable distance ($\le 4\text{ m}$) and front-facing viewing incidence ($\le 60^\circ$).
3. **Multi-Frame Accumulation & Smoothing:** Pose data is accumulated over 0.5–1.0 seconds (averaging position and circular-mean of horizontal normal heading). 
4. **Relocalization Snap:** `[MapRoot]` is rotated around the world vertical axis and translated so recorded metric coordinates align with the real physical world (instant on first scan, smoothly interpolated on subsequent re-scans).
5. **Route Navigation:** The user selects a target destination and follows 3D AR ground chevrons.
6. **Drift Damping Re-Scans:** Upon reaching intermediate building doors with QR markers, subsequent scans refine alignment. If an anomalous marker reading causes a jump $> 10\text{ m}$ or $> 30^\circ$, it is gated for confirmation to prevent map teleportation.

## Controls and Input Methods
- **6-DoF AR Pose Tracking:** AR Foundation / ARCore camera movement in physical 3D space.
- **Computer Vision Image Tracking:** Hardware detection of tracked reference images via `ARTrackedImageManager`.
- **Touch UI:** Standard touchscreen taps for destination selection, debug toggles, and re-scan override confirmations.

---

# UI

### 1. Existing Panels Retained
- **Start / Welcome Panel:** Introductory screen prompting user to begin.
- **Scan Prompt Panel:** Guides the user to point the phone at the wall-mounted QR sign.
- **User Navigation Panel:** Destination dropdown search, active path guidance HUD, and cancel button.
- **Admin Panel & QR Debug Menu:** For recording paths, nodes, and testing scans in-Editor.

### 2. New Hardened UI & Debug Elements
- **On-Screen Debug Telemetry Overlay (Toggleable in Inspector):**
  - Displays marker ID / payload, tracking state (`Tracking` / `Limited`), distance to camera, viewing incidence angle, computed horizontal heading, sample accumulation progress bar, and last applied translation / yaw correction.
- **Suspicious Re-scan Confirmation Banner (HUD Dialog):**
  - If a re-scan exceeds discrepancy limits ($> 10\text{ m}$ or $> 30^\circ$), a non-intrusive warning card appears: `"Re-scan difference is large (12.4m, 35°). Tap to Confirm or Scan Again"` with buttons to **Confirm Alignment** or **Dismiss / Rescan**.

---

# Key Asset & Context

### Existing Scripts & Conventions
- **`Assets/Scripts/CampusMapData.cs`:** Defines `CampusNode`, `CampusEdge`, and `CampusMapData`. Stores `qrFacingEuler` (where `.y` represents horizontal heading in degrees).
- **`Assets/Scripts/CampusNavigation/Localization/QRAnchorRelocalizer.cs`:** Responsible for listening to `ARTrackedImageManager.trackablesChanged` and computing the transform to align `[MapRoot]`.
- **`Assets/Scripts/CampusNavigation/Admin/NodeRecorder.cs`:** Responsible for recording new nodes and QR anchors during authoring sessions.
- **`Assets/Scripts/CampusNavigation/Localization/QRDebugMenu.cs`:** Editor on-screen debug tool for simulating scans.
- **`Assets/Settings/CampusImageLibrary.asset`:** The `XRReferenceImageLibrary` asset referenced by `ARTrackedImageManager`.

### Critical Coordinate & Geometry Insight
In AR Foundation, an `ARTrackedImage` transform has the following local axes:
- `transform.right` ($+X$): Points along the image plane to the right.
- `transform.up` ($+Y$): Points **perpendicularly OUT of the image surface (the surface normal)**.
- `transform.forward` ($+Z$): Points along the image plane upward (top of the image).

When mounted vertically on a wall:
- The outward normal is `transform.up`.
- Projecting `transform.up` onto the horizontal plane (`Vector3.ProjectOnPlane(up, Vector3.up)`) yields the horizontal direction the sign faces in the physical world.
- The sign's horizontal heading is `Vector3.SignedAngle(Vector3.forward, projectedNormal, Vector3.up)`.
- If the sign is laid flat on a horizontal table/floor, `projectedNormal.sqrMagnitude < 0.01f`. In this fallback case, the image's top edge (`transform.forward`) projected onto the horizontal plane defines the heading, and a warning is logged.

---

# Implementation Steps

### Step 1: Centralize Wall-Normal Heading Math in `CampusMapData.cs`
- Add a shared static utility method `CampusMapData.ComputeMarkerHeading(Transform markerTransform, out bool isFlat)`:
  - Takes any marker transform (from `ARTrackedImage` at runtime or in `NodeRecorder`).
  - Extracts outward normal `markerTransform.up`.
  - Projects onto horizontal plane: `Vector3.ProjectOnPlane(normal, Vector3.up)`.
  - If `projected.sqrMagnitude < 0.01f`, sets `isFlat = true`, falls back to `Vector3.ProjectOnPlane(markerTransform.forward, Vector3.up)`, and normalizes.
  - Returns `Vector3.SignedAngle(Vector3.forward, projected, Vector3.up)` in degrees $[-180^\circ, 180^\circ]$.
- **Dependencies:** None.

### Step 2: Update `NodeRecorder.cs` for Identical Recording Math
- In `NodeRecorder.RecordQRAnchor(string qrPayload, string label)`:
  - When recording from an actively tracked `targetImage`, compute its heading using `CampusMapData.ComputeMarkerHeading(targetImage.transform, out bool isFlat)`.
  - Store `qrFacingEuler = new Vector3(0f, heading, 0f)`.
  - When recording fallback without an active tracked image (using camera pose), compute camera's horizontal forward heading: `Vector3.SignedAngle(Vector3.forward, Vector3.ProjectOnPlane(arCameraTransform.forward, Vector3.up).normalized, Vector3.up)`.
  - Store `qrFacingEuler = new Vector3(0f, camHeading, 0f)`.
- **Dependencies:** Step 1.

### Step 3: Harden `QRAnchorRelocalizer.cs`
Implement all 7 core requirements:
1. **Wall-Normal Orientation:**
   - Use `CampusMapData.ComputeMarkerHeading` for the physical tracked image.
   - Calculate $\Delta\text{yaw} = \text{Mathf.DeltaAngle}(recordedNode.qrFacingEuler.y, physicalHeading)$.
   - Generate rotation: `targetRot = Quaternion.Euler(0f, deltaYaw, 0f)`. Pure world-Y rotation, eliminating pitch and roll.
2. **Translation Alignment:**
   - Target position: `targetPos = physicalAnchorPos - (targetRot * recordedNode.localPosition)`.
3. **Quality Gating:**
   - Gated by:
     - `trackingState == TrackingState.Tracking` (reject `Limited` and `None`).
     - Distance gate: `Vector3.Distance(cam.position, trackedImage.position) <= maxTrackingDistance` (default `4.0f` meters).
     - Viewing angle gate: $\text{Vector3.Angle}(N, \text{camToMarkerDirection}) \le \text{maxViewingAngle}$ (default `60.0f` degrees).
     - Flat marker detection logged with warning.
4. **Multi-Frame Accumulation & Smoothing:**
   - Accumulator buffer collects samples over `sampleWindowSeconds` (default `0.75f` seconds, min sample count = 10).
   - Averages position: $\bar{P} = \frac{1}{N} \sum P_i$.
   - Circular-mean of heading: $\bar{\theta} = \text{atan2}\left(\frac{1}{N}\sum \sin \theta_i, \frac{1}{N}\sum \cos \theta_i\right)$.
   - On first alignment: snaps immediately to prevent laggy boot-up.
   - On subsequent re-scans: smoothly interpolates `MapRoot.position` and `MapRoot.rotation` over `smoothLerpDuration` (default `0.5f` seconds) via Coroutine.
5. **Re-scan Discrepancy Gating:**
   - If `hasEverRelocalized == true`:
     - Calculate $\Delta\text{pos} = \|\text{currentMapRoot.pos} - \text{targetPos}\|$ and $\Delta\text{yaw} = |\text{DeltaAngle}(\text{currentYaw}, \text{targetYaw})|$.
     - If $\Delta\text{pos} > \text{maxAllowedPosDiscrepancy}$ (default $10.0\text{m}$) or $\Delta\text{yaw} > \text{maxAllowedYawDiscrepancy}$ (default $30.0^\circ$):
       - Do NOT automatically snap.
       - Log warning: `[QRAnchorRelocalizer] Suspicious re-scan detected...`
       - Fire `OnSuspiciousRelocalizationDetected` event with a pending confirmation callback.
       - Provide `ConfirmPendingRelocalization()` and `CancelPendingRelocalization()`.
6. **Debug Telemetry Overlay:**
   - Add serialized toggle `showDebugOverlay = true` and `verboseLogging = true`.
   - Implement `OnGUI()` overlay displaying real-time marker diagnostics (Marker Name, Distance, Viewing Angle, Tracking State, Accumulated Samples, Last Yaw/Pos Correction, Status).
7. **Image Library Validation:**
   - In `Start()` and when map loads:
     - Check each `NodeType.QRAnchor` in `CurrentMap.nodes`: verify `node.qrPayload` exists in `imageManager.referenceLibrary`. If missing, log warning.
     - Check each image in `imageManager.referenceLibrary`: check if any map node uses it. If not, log notice.
- **Dependencies:** Step 1, Step 2.

### Step 4: Add Confirmation UI Hook in `UserNavigationUI.cs` & `QRDebugMenu.cs`
- In `UserNavigationUI.cs`: Subscribe to `OnSuspiciousRelocalizationDetected`. Display a warning banner with "Confirm" and "Rescan" buttons if triggered.
- In `QRDebugMenu.cs`: Update simulated scans to test both normal alignment and threshold-exceeding alignment.
- **Dependencies:** Step 3.

### Step 5: Update Documentation & Architectural Records
- Update `PROGRESS.md`: Log newly implemented wall-mounted QR tracking, smoothing, gating, and remaining outdoor test items.
- Update `DECISIONS.md`: Log Architectural Decision Record explaining why the image normal projected onto the horizontal plane is used for wall-mounted markers, why multi-frame circular averaging is required, and why outlier re-scans are gated.
- **Dependencies:** Step 1-4.

---

# Verification & Testing

1. **Math & Axis Verification:**
   - Execute an Editor test script to verify `CampusMapData.ComputeMarkerHeading` across 4 cardinal wall orientations:
     - Wall facing North: normal = $(0, 0, 1)$, heading = $0^\circ$.
     - Wall facing East: normal = $(1, 0, 0)$, heading = $90^\circ$.
     - Wall facing South: normal = $(0, 0, -1)$, heading = $180^\circ$.
     - Wall facing West: normal = $(-1, 0, 0)$, heading = $-90^\circ$.
     - Ceiling / floor (flat): normal = $(0, 1, 0)$ triggers fallback and warning.
2. **Quality Gating Validation:**
   - Test marker at $2\text{m}$ (passes).
   - Test marker at $6\text{m}$ (rejected by distance gate).
   - Test marker at $75^\circ$ oblique angle (rejected by viewing angle gate).
   - Test marker in `TrackingState.Limited` (rejected).
3. **Multi-Frame Accumulation & Smoothing Validation:**
   - Verify sample accumulation counts over $0.75\text{s}$.
   - Verify first scan snaps immediately (0s delay).
   - Verify subsequent re-scan interpolates smoothly over $0.5\text{s}$ without snapping glitches.
4. **Outlier Discrepancy Gating Validation:**
   - Relocalize to Marker A.
   - Simulate detection of a marker placed $25\text{m}$ away ($> 10\text{m}$ threshold) or rotated $45^\circ$ ($> 30^\circ$ threshold).
   - Confirm map does NOT teleport; verify warning log and pending confirmation state.
   - Trigger `ConfirmPendingRelocalization()` and verify it applies.
5. **Reference Image Library Validation:**
   - Test with a map node whose payload is not in `CampusImageLibrary` to confirm validation warning is logged.
