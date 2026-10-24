# Campus AR Navigation — Project Progress & Status

**Last Updated:** 2026-04-01  
**Project:** Campus AR Navigation (Benguet State University)  
**Target Platform:** Android (Unity 6 / URP / AR Foundation 6.5)

---

## 1. What's Working
- **Core AR & Sensor Foundation:**
  - AR Foundation & ARCore tracking active (`SampleScene.unity`).
  - `AndroidSensorManager` with high-precision `double` timestamps and Editor jittered simulation.
  - Equirectangular GPS-to-Local ENU metric projection (`GPSToLocalMeters`) and Haversine distance.
- **Graph & Map Data Management:**
  - Dynamic JSON load/save with `savedTimestamp` and automated StreamingAssets sync in Editor.
  - Bidirectional A* pathfinding with **Anchor-Spur exclusion** (users are never routed through QR anchors).
  - Hybrid distance calculation seamlessly falling back between GPS and local metric 3D coordinates.
- **Hardened Authoring Tool (`NodeRecorder.cs`):**
  - **Confirmation Workflow:** Replaced silent node reuse with explicit "Start from Node" / "Create New" / "Node Picker" dialogs.
  - **Median GPS Filtering:** 25s window for landmark recording with `>= 10` distinct fixes to eliminate multipath outliers.
  - **Edge Splitting ("Join Path"):** Real-time segment splitting to insert intersection nodes while preserving path flags.
  - **Spur Pattern:** QR anchors automatically connect to the nearest walkway node without disrupting the breadcrumb chain.
  - **Bootstrap Alignment:** Exempts brand-new maps from alignment guards; automatically initializes origin and MapRoot on first good fix.
- **Hardened Relocalization (`QRAnchorRelocalizer.cs` & `GpsRelocalizer.cs`):**
  - **Best-Candidate Hysteresis:** Per-frame sampling of all tracked images; keeps current target unless another is $0.5\text{m}$ closer.
  - **Strict Movement Gating:** GPS yaw damping requires straight 15m walk with stable chord stability ($<15\%$ deviation) and VIO/GPS agreement ($<30\%$ error).
  - **Lifecycle Returns:** UI automatically returns to Start/Search panels on session reset, background/resume, or navigation completion.
  - **Cooldowns & Sync:** 12s per-anchor cooldown; QR alignment clears stale GPS in-motion targets.
- **Visitor Experience & Wayfinding:**
  - Label lookups from map data for active HUD.
  - "Approximate Alignment" notice when only coarse GPS snap is active.
  - Scan guidance ("Arm's length") provided via public property for release builds.

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
