# Campus AR Navigation — Project Context & Architectural Blueprint

## 1. Project Goals & Context
- **Project Name:** Campus_AR_Navigation
- **Target Institution:** **Benguet State University (BSU)** (La Trinidad, Benguet, Philippines)
- **Campus Scale & Environment:** 
  - Large campus spanning approximately **2 kilometers** in coverage.
  - The walkable environment consists **predominantly of outdoor paths**, sidewalks, open grounds, and inter-building connections.
- **Target Audience:** **Freshmen and campus visitors** who are unfamiliar with the university layout and need intuitive, on-ground guidance.
- **Navigation Scope:** Exclusively targets **administrative offices, college offices, and key campus facilities / student service centers** across BSU.
- **Core Requirement:** **100% Offline Capability.** Must operate fully without requiring active cellular mobile data or campus Wi-Fi, allowing any visitor or student to open the app and navigate independently.
- **Target Platform:** Mobile (Android)
- **Engine Version:** Unity 6 (`6000.3.17f1`)
- **Render Pipeline:** Universal Render Pipeline (URP 17.3.0, `URP-Performant` profile)
- **Scripting Runtime / API Level:** .NET Standard 2.0
- **Input System:** Both New Input System (v1.19.0) and Legacy Input Manager enabled
- **Core Value Proposition:** An offline Augmented Reality mobile application where users select target offices or facilities from a directory and follow 3D AR arrows/chevrons overlaid on the real outdoor campus walkways. Includes an integrated on-device Admin authoring tool to map walkways, junctions, and office entrances without requiring third-party cloud mapping services.

---

## 2. Core Architecture & Technical Setup

### A. Packages & Subsystems
- **`com.unity.xr.arfoundation` (6.5.0):** AR session lifecycle, tracked images, plane detection, raycasting.
- **`com.unity.xr.arcore` (6.5.0):** Native Android ARCore runtime backend.
- **`com.unity.xr.interaction.toolkit` (3.5.1):** XR Origin setup, screen-space ray and gesture interactions.
- **`com.unity.xr-content.xr-sim-environments` (2.1.1):** In-Editor simulation of AR environments.
- **`TextMeshPro` / Unity UGUI:** UI elements, dropdowns, HUD overlays, text panels.
- **`com.unity.mobile.android-logcat` (1.4.7):** On-device debugging and diagnosis.

### B. Scene Architecture (`Assets/Scenes/SampleScene.unity`)
- **`[AR Session]`:** Handles AR tracking state and input management.
- **`[XR Origin (AR Rig)]`:** Contains `XROrigin`, `ARTrackedImageManager`, `ARPlaneManager`, `ARRaycastManager`, and `Main Camera` (`TrackedPoseDriver`, `ARCameraBackground`).
- **`[MapRoot]`:** The single parent reference transform for all navigation content (nodes, route lines, arrows, pins). Repositioning and rotating `MapRoot` automatically realigns all AR visual elements to the physical campus.
- **`[Managers]`:** Central GameObject holding key singleton components:
  - `MapDataManager`: Local JSON storage (`campus_map.json`) in `persistentDataPath` and `StreamingAssets`.
  - `AndroidSensorManager`: Accesses Android GPS and heading sensors with Editor simulation fallbacks.
  - `NodeRecorder`: Authoring tool for recording waypoints, junctions, and destinations.
  - `AStarPathfinder`: In-memory graph builder and pathfinding engine.
  - `ARRouteVisualizer`: Renders and animates AR breadcrumb arrows and destination pins.
  - `QRAnchorRelocalizer`: Snaps and orients `MapRoot` based on detected physical QR markers.
  - `NavigationSessionManager`: High-level session controller tracking user route progress.
- **`[UI]`:** Complete Canvas interface holding:
  - `StartPanel`: Welcome screen, scan prompt launcher, QR bypass option.
  - `ScanPromptPanel`: Reticle overlay and scanning line animation.
  - `SearchPanel`: Destination dropdown selection (offices/facilities) and pathfinding initiation.
  - `ActiveNavPanel`: Turn-by-turn banner showing destination name, remaining distance, and cancel button.
  - `AdminPanel`: Node recorder UI (record toggle, mark destination, clear map).
- **`[Debug Manager]`:** Holds `QRDebugMenu` for in-editor simulated marker scans.

---

## 3. Detailed Architectural Decisions & Discussion Recap

### A. The Outdoor VIO vs. GPS Dilemma (Critical for BSU's 2km Scale)
- **Initial Implementation:** `NodeRecorder` recorded breadcrumbs every 5 meters in `MapRoot` local coordinates derived directly from ARCore's Visual-Inertial Odometry (VIO).
- **Why this Fails at BSU Scale:** Across a 2-kilometer outdoor campus, walking through open paths, grass areas, agricultural fields, and plazas gives ARCore few high-contrast visual trackables. Pure VIO accumulates substantial translational and rotational (yaw) drift over several hundred meters. If recorded purely in VIO space, the far end of campus would be warped by tens of meters in the saved file.
- **The Pitfall of Pure GPS:** Consumer smartphone GPS fluctuates frame-to-frame by 3–8 meters. Directly driving AR graphics with raw GPS makes AR chevrons jitter and slide across the ground. GPS also degrades near tall building facades or under covered walkways.

### B. The Agreed Final Solution: Two-Tier Hybrid Architecture
Both the user and assistant aligned on an optimal hybrid architecture combining the global stability of GPS with the local smoothness of ARCore VIO:

1. **Authoring / Mapping (Ground Truth = Filtered GPS):**
   - **ENU Projection:** Latitude/Longitude is the source of truth for global geometry across the 2km campus. Local metric coordinates (`localPosition`) are calculated deterministically via Flat-Earth / Equirectangular projection relative to a designated BSU campus origin `(originLatitude, originLongitude)`.
   - **Multi-Sample Averaging:** When recording an office entrance, facility, or junction, the admin pauses for 3–5 seconds. The app collects multiple GPS fixes, discards readings with `horizontalAccuracy > 8m`, and stores the median/average.
   - **Explicit Junction Marking:** Replaced the unreliable blind 3-meter automatic proximity stitching with an explicit admin button: *"Mark Junction / Link to Nearest Node"*.
   - **Ground Clamping:** Raw GPS altitude is discarded (due to high vertical noise ±15–30m). Paths are clamped to ground/eye-offset (`-1.4m`) or tagged with accessibility/stair flags.

2. **Runtime Positioning & Alignment for Freshmen & Visitors:**
   - **Frame-to-Frame Rendering:** ARCore VIO handles local camera movement and renders the next 1–3 waypoints (within 10–15m), ensuring zero visual jitter along the walkway.
   - **Initial Coarse Snap:** On app launch, a high-accuracy GPS fix plus device compass provides a one-time snap to orient `MapRoot` so users immediately have realistic distances and directional guidance toward target offices.
   - **Gentle In-Motion Drift Damping:** While walking at steady speeds (>1 m/s) with high GPS accuracy on straight outdoor stretches, `MapRoot`'s yaw offset is gently corrected via a rate-limited slew filter (1–2°/sec) so adjustments remain imperceptible.
   - **Precision QR Resets:** Physical QR code signs stationed at university entrance gates, bulletin boards, and key building portals act as instantaneous centimeter-level ground-truth resets.
   - **Office/Facility Transition:** When arriving at an office or stepping inside a building where GPS degrades, the GPS gate closes. The app transitions cleanly to pure ARCore VIO and indoor QR markers.

### C. 100% Offline Operation Verification
- **Satellite GNSS:** Phone GPS receiver talks directly to orbital satellites; no cellular data or internet needed.
- **Local Sensors:** Compass, accelerometer, and gyroscope operate entirely in hardware.
- **On-Device ARCore:** Computer vision processing runs entirely on the mobile device's chip.
- **Bundled Map Data:** `campus_map.json` is packaged in `StreamingAssets` and loaded into memory on start.
- **Local Pathfinding & Relocalization:** A* pathfinding and QR image recognition execute 100% offline.
- *Cold Start Consideration:* Offline GPS may take 30–60 seconds for an initial satellite almanac fix (cold start without cellular A-GPS). The presence of physical QR anchors at campus entrance points solves this by allowing instant manual alignment before GPS locks.

---

## 4. Current Implementation Status

- [x] Full AR Foundation & XR Origin scene configured (`SampleScene.unity`).
- [x] Complete Graph Data model implemented (`CampusMapData.cs` with `CampusNode`, `CampusEdge`, `CampusMapData`).
- [x] Local JSON storage manager created (`MapDataManager.cs`).
- [x] A* Pathfinding engine active (`AStarPathfinder.cs`).
- [x] AR Route breadcrumb and pin visualizer built (`ARRouteVisualizer.cs`).
- [x] QR code relocalization system active (`QRAnchorRelocalizer.cs`).
- [x] User navigation flow and state UI implemented (`UserNavigationUI.cs`).
- [x] In-Editor QR debug menu operational (`QRDebugMenu.cs`).
- [x] Android sensor manager operational with BSU defaults and accuracy tracking (`AndroidSensorManager.cs`).
- [x] GPS-to-Local ENU conversions (`GPSToLocal`, `GPSToLocalMeters`) and Haversine distance implemented (`CampusMapData.cs`).
- [x] GPS-primary breadcrumb recording with horizontal accuracy gating implemented (`NodeRecorder.cs`).
- [x] Multi-sample GPS averaging routine for destinations/anchors implemented (`NodeRecorder.cs`).
- [x] Explicit junction linking feature and UI button implemented (`NodeRecorder.cs`, `AdminUIController.cs`).
- [x] Runtime GPS relocalization and in-motion yaw damping implemented and attached to Managers (`GpsRelocalizer.cs`).
- [x] Link Junction button added and wired in the `Admin Panel` scene hierarchy.
- [x] Modernized floating cancel navigation button (`Can Nav Button`) with glassmorphism style, soft drop shadow, frosted contrast backing, 18% white fill, 35% thin border, swappable centered icon, and cyan `#00E5FF` active/selected accent.

---

## 5. How to Use the System

### A. Testing in the Unity Editor
1. **Enter Play Mode:**
   - `AndroidSensorManager` automatically uses simulated BSU coordinates (`Lat: 16.4554, Lon: 120.5901, Alt: 1300m, Accuracy: 3.5m`).
2. **Authoring (Admin Mode):**
   - Click **"Toggle Admin Button"** to show the Admin Panel.
   - Click **"Start Record"** — the origin GPS coordinate is locked and initial node #1 is recorded.
   - Enter an office name (e.g., `Registrar's Office`) into the input field and click **"Mark Destination"**. The UI displays the 4-second multi-sample averaging progress bar, averages the samples, and creates a `Destination` node.
   - Click **"Link Junction"** to test connecting the current node to the nearest existing node within 12 meters.
   - Click **"Stop & Save"** — writes `campus_map.json` and syncs it to `Assets/StreamingAssets/campus_map.json`.
3. **User Navigation:**
   - Toggle the Admin Panel off.
   - Click **"Begin"** on `StartPanel` (or test with `QRDebugMenu` simulated scans).
   - In the `Search Panel` dropdown, select the recorded destination.
   - Click **"Start Navigation"** — `AStarPathfinder` computes the path and `ARRouteVisualizer` renders the AR arrows.

### B. On-Device Campus Mapping Workflow (Android at BSU)
1. **Set Campus Origin:** Go to the primary entrance (e.g., BSU Main Gate), launch the app, open Admin Panel, and tap **"Start Record"**.
2. **Walk Walkways:** Walk down outdoor paths. As long as GPS accuracy is $\le 10\text{ m}$, breadcrumb waypoints drop automatically every 8 meters using GPS distance.
3. **Record Target Offices & Facilities:** Stand still at each entrance door, type the office name, tap **"Mark Destination"**, and wait 4 seconds for GPS averaging.
4. **Connect Crossroads:** At walkway forks or intersections, tap **"Link Junction"** to link paths into a connected graph.
5. **Finalize & Deploy:** Tap **"Stop & Save"**. Transfer the resulting `campus_map.json` into `Assets/StreamingAssets/` and build the release APK. All freshmen and visitors can now navigate BSU completely offline.

---

## 6. Next Steps & Recommendations

1. **On-Campus Ground Survey:**
   - Conduct an initial survey of 2–3 key paths (e.g., Main Gate $\rightarrow$ Administration Building $\rightarrow$ Library) to collect real GPS coordinates and verify walking breadcrumb spacing.
2. **Physical QR Anchor Placement:**
   - Print and install high-contrast QR codes at key building entrances or kiosks to serve as precision resets when stepping indoors.
3. **Build & APK Deployment:**
   - Build Android APK via Unity Build Profiles/Settings and verify permissions (`ACCESS_FINE_LOCATION`, `CAMERA`) on target test devices.



### A. Packages & Subsystems
- **`com.unity.xr.arfoundation` (6.5.0):** AR session lifecycle, tracked images, plane detection, raycasting.
- **`com.unity.xr.arcore` (6.5.0):** Native Android ARCore runtime backend.
- **`com.unity.xr.interaction.toolkit` (3.5.1):** XR Origin setup, screen-space ray and gesture interactions.
- **`com.unity.xr-content.xr-sim-environments` (2.1.1):** In-Editor simulation of AR environments.
- **`TextMeshPro` / Unity UGUI:** UI elements, dropdowns, HUD overlays, text panels.
- **`com.unity.mobile.android-logcat` (1.4.7):** On-device debugging and diagnosis.

### B. Scene Architecture (`Assets/Scenes/SampleScene.unity`)
- **`[AR Session]`:** Handles AR tracking state and input management.
- **`[XR Origin (AR Rig)]`:** Contains `XROrigin`, `ARTrackedImageManager`, `ARPlaneManager`, `ARRaycastManager`, and `Main Camera` (`TrackedPoseDriver`, `ARCameraBackground`).
- **`[MapRoot]`:** The single parent reference transform for all navigation content (nodes, route lines, arrows, pins). Repositioning and rotating `MapRoot` automatically realigns all AR visual elements to the physical campus.
- **`[Managers]`:** Central GameObject holding key singleton components:
  - `MapDataManager`: Local JSON storage (`campus_map.json`) in `persistentDataPath` and `StreamingAssets`.
  - `AndroidSensorManager`: Accesses Android GPS and heading sensors with Editor simulation fallbacks.
  - `NodeRecorder`: Authoring tool for recording waypoints, junctions, and destinations.
  - `AStarPathfinder`: In-memory graph builder and pathfinding engine.
  - `ARRouteVisualizer`: Renders and animates AR breadcrumb arrows and destination pins.
  - `QRAnchorRelocalizer`: Snaps and orients `MapRoot` based on detected physical QR markers.
  - `NavigationSessionManager`: High-level session controller tracking user route progress.
- **`[UI]`:** Complete Canvas interface holding:
  - `StartPanel`: Welcome screen, scan prompt launcher, QR bypass option.
  - `ScanPromptPanel`: Reticle overlay and scanning line animation.
  - `SearchPanel`: Destination dropdown selection and pathfinding initiation.
  - `ActiveNavPanel`: Turn-by-turn banner showing destination name, remaining distance, and cancel button.
  - `AdminPanel`: Node recorder UI (record toggle, mark destination, clear map).
- **`[Debug Manager]`:** Holds `QRDebugMenu` for in-editor simulated marker scans.

---

## 3. Detailed Architectural Decisions & Discussion Recap

### A. The Outdoor VIO vs. GPS Dilemma
- **Initial Implementation:** `NodeRecorder` recorded breadcrumbs every 5 meters in `MapRoot` local coordinates derived directly from ARCore's Visual-Inertial Odometry (VIO).
- **Identified Failure Mode:** Over large outdoor expanses (e.g., grassy quads, plazas, open skies), ARCore suffers from low visual texture contrast, causing dead-reckoning drift (both translation and rotational yaw drift). Over 200–500 meters, this drift can accumulate to 5–25 meters, baking permanent geographic errors into the saved map file.
- **Proposed Pure GPS Alternative:** Rely entirely on GPS coordinates, deriving `localPosition` via East-North-Up (ENU) projection from a campus origin.
- **The Pitfall of Pure GPS in AR:** Consumer phone GPS naturally jitters by 3–8 meters frame-to-frame due to multi-path interference and satellite geometry. Directly feeding raw GPS into AR render positions would cause virtual arrows to jitter, shake, and jump erratically. Furthermore, GPS drops to zero indoors or under dense canopies.

### B. The Agreed Final Solution: Two-Tier Hybrid Architecture
Both the user and assistant aligned on an optimal hybrid architecture combining the global stability of GPS with the local smoothness of ARCore VIO:

1. **Authoring / Mapping (Ground Truth = Filtered GPS):**
   - **ENU Projection:** Latitude/Longitude is the source of truth for global geometry. Local coordinates (`localPosition`) are calculated deterministically via Flat-Earth / Equirectangular projection relative to `originLatitude`/`originLongitude`.
   - **Multi-Sample Averaging:** When recording a destination, junction, or anchor, the admin stops for 3–5 seconds. The app collects multiple GPS fixes, discards readings with `horizontalAccuracy > 8m`, and stores the median/average.
   - **Explicit Junction Marking:** Replaced the unreliable blind 3-meter automatic proximity stitching with an explicit admin button: *"Mark Junction / Link to Nearest Node"*.
   - **Ground Clamping:** Raw GPS altitude is discarded (due to high vertical noise ±15–30m). Paths are clamped to ground/eye-offset (`-1.4m`) or tagged with accessibility/stair flags.

2. **Runtime Positioning & Alignment:**
   - **Frame-to-Frame Rendering:** ARCore VIO handles local camera movement and renders the next 1–3 waypoints (within 10–15m), ensuring zero visual jitter.
   - **Initial Coarse Snap:** On app launch, a high-accuracy GPS fix plus device compass provides a one-time snap to orient `MapRoot` so users immediately have realistic distances and directional guidance.
   - **Gentle In-Motion Drift Damping:** While walking at steady speeds (>1 m/s) with high GPS accuracy on straight stretches, `MapRoot`'s yaw offset is gently corrected via a rate-limited slew filter (1–2°/sec) so adjustments remain imperceptible.
   - **Precision QR Resets:** Physical QR code signs at building entrances, gates, and major forks act as instantaneous centimeter-level ground-truth resets.
   - **Indoor / Canopy Fallback:** When GPS accuracy degrades (>10m) or cuts out indoors, the GPS gate closes. The app transitions seamlessly to pure ARCore VIO + indoor QR anchors.

### C. Offline Capability Verification
The entire system operates **100% offline**:
- GPS hardware (GNSS) communicates directly with satellites without internet.
- Sensor hardware (compass, IMU) and ARCore VIO process entirely on-device.
- Graph data (`campus_map.json`) is stored locally in `StreamingAssets` and `persistentDataPath`.
- Pathfinding (A*) and QR recognition run locally on the phone's CPU/GPU.
- *Cold Start Consideration:* Offline GPS may take 30–60 seconds for an initial satellite almanac fix (cold start without cellular A-GPS). The presence of physical QR anchors solves this by allowing instant manual alignment before GPS locks.

---

## 4. Current Implementation Status

- [x] Full AR Foundation & XR Origin scene configured (`SampleScene.unity`).
- [x] Complete Graph Data model implemented (`CampusMapData.cs` with `CampusNode`, `CampusEdge`, `CampusMapData`).
- [x] Local JSON storage manager created (`MapDataManager.cs`).
- [x] A* Pathfinding engine active (`AStarPathfinder.cs`).
- [x] AR Route breadcrumb and pin visualizer built (`ARRouteVisualizer.cs`).
- [x] QR code relocalization system active (`QRAnchorRelocalizer.cs`).
- [x] User navigation flow and state UI implemented (`UserNavigationUI.cs`).
- [x] In-Editor QR debug menu operational (`QRDebugMenu.cs`).
- [x] Android sensor manager skeleton created (`AndroidSensorManager.cs`).
- [x] Node recording authoring system operational (`NodeRecorder.cs`, `AdminUIController.cs`).

---

## 5. Next Steps & Action Items

1. **Update `CampusMapData.cs`:**
   - Add GPS-to-Local ENU conversion helper methods (`GPSToLocalMeters`).
   - Add metadata fields to `CampusNode`: `horizontalAccuracy`, `sampleCount`, and `recordedTimestamp`.
2. **Upgrade `NodeRecorder.cs` & `AdminUIController.cs`:**
   - Add a multi-sample GPS averaging coroutine for destination and anchor recording.
   - Replace automatic 3m proximity stitching with an explicit "Link to Nearest Node / Mark Junction" feature.
   - Add a live GPS accuracy status indicator (e.g., Green `<8m`, Yellow `8-15m`, Red `>15m`).
3. **Implement Runtime GPS Alignment (`GpsRelocalizer.cs`):**
   - Create a relocalizer component that performs an initial coarse `MapRoot` placement on startup.
   - Implement motion-gated, slew-rate-limited yaw correction for long outdoor walks.
4. **Conduct Real-World Loop Test:**
   - Walk a 200–300 meter closed loop on campus in recording mode to benchmark baseline tracking accuracy and calibrate distance thresholds.
