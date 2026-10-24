# Campus AR Navigation — Architectural Decision Records (ADR)

This file logs key architectural decisions, rationale ("Why"), and constraints. It prevents future development sessions from reopening settled questions or reversing critical patterns.

---

### 2026-03-31: Wall-Mounted Marker Orientation via Projected Normal
- **Decision:** Marker horizontal heading must be derived from the outward surface normal (`transform.up` in ARFoundation ARTrackedImage) projected onto the horizontal plane (`Vector3.ProjectOnPlane(up, Vector3.up)`). Recorded anchors store this heading and position in `MapRoot` local space; runtime detection computes it in world space.
- **Why:** In AR Foundation, `transform.up` points perpendicular to and out of the image surface. When a marker is mounted vertically on a wall or kiosk, its local up-vector is horizontal in world space. Evaluating `eulerAngles.y` directly produces large errors or 180° flips depending on minor roll or pitch tilts. Projecting the outward normal onto the horizontal plane provides an unambiguous, robust compass heading for the wall.
- **Flat Marker Fallback:** If the marker is mounted horizontally (ceiling/floor) such that the projected normal has near-zero magnitude, the system falls back to projecting the image's top edge (`transform.forward`) onto the horizontal plane and logs a warning.

---

### 2026-03-31: Multi-Frame Accumulation and Quality Gating
- **Decision:** QR anchor detections are quality-gated by:
  1. `TrackingState.Tracking` only (no `Limited` or `None`).
  2. Maximum camera distance ($\le 4.0\text{m}$).
  3. Maximum viewing incidence angle ($\le 60^\circ$ between the marker's outward normal and the marker-to-camera vector).
  4. Multi-frame accumulation over $0.75\text{s}$ (minimum 8 frames) using position averaging and circular-mean/variance of heading.
- **Why:** Single-frame camera estimates at extreme viewing angles or distances exhibit significant depth and orientation noise. Requiring a brief stable viewing window eliminates false snaps and single-frame visual pops.

---

### 2026-03-31: Smoothed Re-scans and Horizontal Discrepancy Gate
- **Decision:** The first anchor scan of a session applies an instant snap to `[MapRoot]`. All subsequent re-scans apply a smooth 0.5s interpolation (`Vector3.Lerp` / `Quaternion.Slerp`). If a re-scan introduces a discrepancy $> 10\text{m}$ horizontally (measured by comparing where the map expects that anchor vs where it was physically seen) or $> 30^\circ$ yaw, the snap is blocked and requires user confirmation.
- **Why:** A walking user scanning an intermediate checkpoint should experience a gentle drift correction rather than an abrupt visual jolt. Furthermore, if a marker is misidentified or tracked with severe multipath reflection, the discrepancy gate prevents teleporting the entire campus map away from the user. Measuring distance at the anchor rather than the `MapRoot` origin ensures that markers hundreds of meters from the campus origin do not trigger false positive translation warnings on minor yaw corrections.

---

### 2026-03-31: Hybrid Node Distance Calculation
- **Decision:** Distance between nodes must be calculated via `CampusMapData.CalculateNodeDistance(a, b)` which checks if valid non-zero GPS coordinates exist. If coordinates are `0.0` (in-Editor simulation or indoor AR), it falls back to 3D Euclidean distance in `MapRoot` local space (`Vector3.Distance`).
- **Why:** When nodes had `lat: 0.0, lon: 0.0`, GPS distance returned `0.0m` regardless of physical separation. This caused phantom shortcut edges directly between distant nodes (e.g. Point C to Entrance with distance 0), completely breaking A* pathfinding routes.
- **Rule:** Never use pure `CalculateDistanceMeters(lat1, lon1, lat2, lon2)` without verifying that both nodes contain valid, non-zero coordinates.

---

### 2026-03-31: Session Resumption Without Redundant Start Points
- **Decision:** When resuming recording on an existing map within $3.5\text{m}$ of an existing node (e.g., Point B), `NodeRecorder` sets `lastRecordedNode = nearestNode` directly instead of instantiating a duplicate node labeled "Start Point".
- **Why:** Creating a new "Start Point" at the exact same coordinates as an existing destination caused duplicate zero-distance nodes, split graphs into disconnected segments, and caused A* to report "No path found".

---

### 2026-03-31: MapRoot as the Single Reference Transform
- **Decision:** All virtual navigation assets (nodes, lines, chevrons, destination pins) must be parented under `[MapRoot]`. Coordinates in `campus_map.json` are stored in `MapRoot` local space.
- **Why:** ARCore's internal coordinate origin starts wherever the camera boots up. By translating and rotating the single `[MapRoot]` transform, all campus AR assets instantly snap and align to the physical world without modifying individual waypoint transforms.

---

### 2026-03-30: Two-Tier Hybrid Positioning Architecture
- **Decision:** Latitude/Longitude with Equirectangular (ENU) projection is the global ground truth across the 2km campus. ARCore Visual-Inertial Odometry (VIO) handles local frame-to-frame smooth rendering of the nearest 10–15m of path.
- **Why:** Pure VIO accumulates significant rotational and translational drift over a 2km outdoor campus with few high-contrast trackables. Pure GPS jitters by 3–8m frame-to-frame, which would cause AR arrows to shake and jump. The hybrid approach gives global geometric stability with smooth local visual tracking.

---

### 2026-03-30: Multi-Sample Averaging for Destinations & Anchors
- **Decision:** When recording landmarks/offices, the admin stops for 4 seconds while the app samples GPS fixes, discards fixes with `accuracy > 10m`, and computes the average.
- **Why:** Single-frame GPS fixes can be heavily skewed by multipath reflections near building walls. Averaging over several seconds filters high-frequency noise and stabilizes entrance coordinates.

---

### 2026-03-30: QR Anchors as Ground-Truth Resets
- **Decision:** Physical QR code signs stationed at building portals and campus gates act as instant centimeter-level calibration anchors for `[MapRoot]`.
- **Why:** Provides an instant fix on app startup (bypassing the 30–60s offline GPS cold-start delay) and resets accumulated VIO drift when entering buildings where GPS signals degrade.

---

---

### 2026-04-01: Anchor-Spur Pattern for QR Calibration
- **Decision:** QR Anchor nodes must be recorded as "spur" nodes that connect to the nearest non-anchor (walkway) node. A* pathfinding must never route users through a QR Anchor node.
- **Why:** Calibration markers are often off the main walking path (e.g., mounted on a building pillar). Routing the user directly onto the physical sign causes navigation confusion and redundant turns. The spur pattern allows the map to snap to the anchor's ground truth while keeping the navigation route on the actual sidewalk.

---

### 2026-04-01: GPS Yaw Stability chord-based Rejection
- **Decision:** In-motion yaw corrections from GPS are rejected if the maximum perpendicular distance from any intermediate camera position to the start-end chord exceeds 15% of the chord length, or if VIO distance vs. GPS distance differs by > 30%.
- **Why:** Drift damping requires the user to walk in a roughly straight line to determine a reliable heading. If the user turns or weaves during the 15m movement window, the calculated bearing is invalid. The chord rejection ensures only stable walking windows contribute to yaw alignment.

---

---

### 2026-04-01: Map Deletion and Bundled Fallback
- **Decision:** The "Clear Map" function in the Admin UI deletes the user's persistent data file. On the next application restart, the system automatically falls back to the read-only bundled map stored in `StreamingAssets`.
- **Why:** To ensure the app is never in a broken state without a map. If an admin makes a mistake during survey, deleting the save allows them to revert to the factory-shipped campus map immediately.
