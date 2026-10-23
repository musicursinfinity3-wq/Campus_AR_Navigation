# Campus AR Navigation — Project Progress & Status

**Last Updated:** 2026-03-31  
**Project:** Campus AR Navigation (Benguet State University)  
**Target Platform:** Android (Unity 6 / URP / AR Foundation 6.5)

---

## 1. What's Working
- **Core AR & Sensor Foundation:**
  - AR Foundation & ARCore tracking active (`SampleScene.unity`).
  - `AndroidSensorManager` with live hardware GPS, heading/compass, and Editor simulation fallback.
  - Equirectangular GPS-to-Local ENU metric projection (`GPSToLocalMeters`) and Haversine distance.
- **Graph & Map Data Management:**
  - Dynamic JSON load/save (`campus_map.json`) across device `persistentDataPath` and build `StreamingAssets`.
  - Bidirectional A* pathfinding graph indexing (`AStarPathfinder.cs`).
  - Hybrid distance calculation (`CampusMapData.CalculateNodeDistance`) seamlessly falling back between GPS and local metric 3D coordinates.
  - Anchor versioning (`anchorVersion = 2`) with automated startup warning for legacy v0 anchors.
- **Node Recording & Authoring Tool:**
  - Breadcrumb recording in both hardware GPS and indoor/Editor AR tracking space (auto-drops every 3m).
  - Multi-sample GPS averaging routine (4s duration) for landmark destination doors.
  - Seamless session appending: auto-resumes from existing nearby nodes without dropping redundant "Start Point" duplicates.
  - Junction linking (`LinkToNearestJunction`) and auto-intersection connecting (`autoConnectIntersections`) with safe non-zero edge weights.
  - Strict QR anchor authoring: requires actively tracked image (`TrackingState.Tracking`); records position and outward normal heading in `MapRoot` local space; includes inverted camera-forward editor simulation.
- **Hardened Wall-Mounted QR Relocalization (`QRAnchorRelocalizer.cs`):**
  - **Wall-Normal Heading:** Heading computed by projecting marker outward surface normal (`transform.up`) onto horizontal plane with flat-marker fallback.
  - **Quality Gating:** Strict filtering by `TrackingState.Tracking`, maximum distance ($\le 4\text{ m}$), and viewing incidence angle ($\le 60^\circ$ between outward normal and marker-to-camera vector).
  - **Multi-Frame Circular Accumulator:** Buffers 0.75s of tracking (min 8 frames) averaging positions and computing circular mean & variance of headings; rejects unstable/high-spread samples.
  - **Smooth Slerp/Lerp Re-scans:** First scan snaps immediately; subsequent re-scans smoothly interpolate over 0.5s with coroutine preemption.
  - **Horizontal Discrepancy Gate:** Re-scans compare mapped anchor position to physical detection ($>10\text{m}$ horizontal or $>30^\circ$ yaw triggers confirmation dialog, preventing map teleportation).
  - **ARSession Lifecycle Tracking:** Resets relocalization and pending states on session restart or tracking loss.
  - **Responsive Telemetry Overlay & Validation:** Scalable GUI overlay showing real-time metrics (compiled out of release builds) and startup cross-validation between `CampusImageLibrary` and map anchors.
- **Visitor Experience & Wayfinding:**
  - Step-by-step user navigation flow (`UserNavigationUI.cs`).
  - Smooth 3D directional arrow visualizations (`ARRouteVisualizer.cs`).
  - Coarse startup auto-alignment of `MapRoot` and runtime drift damping (`GpsRelocalizer.cs`).

---

## 2. What's in Progress
- **Physical QR Marker Validation:**
  - Validating v2 wall-mounted QR anchors under outdoor sunlight with physical prints.

---

## 3. What's Next
1. **Physical QR Code Printing & Deployment:**
   - Print high-contrast $20\text{cm} \times 20\text{cm}$ matte QR signs and register them in `Assets/Settings/CampusImageLibrary.asset`.
   - Re-record existing campus anchors using the hardened v2 authoring flow.
2. **On-Device APK Build:**
   - Build Android APK and test on a physical device.
   - Verify camera and fine location permissions.
3. **Outdoor Walk & Sensor Calibration:**
   - Conduct a 50–100m walk test outdoors to test real hardware GPS jitter, satellite lock times, and breadcrumb spacing.

---

## 4. Known Bugs & Caveats
- **Legacy Anchors Migration:** Any existing QR anchors in `campus_map.json` recorded prior to version 2 (with `anchorVersion == 0`) used camera Euler angles and are flagged with a warning at startup. They must be re-recorded using the updated authoring tool to enable wall-normal alignment.
