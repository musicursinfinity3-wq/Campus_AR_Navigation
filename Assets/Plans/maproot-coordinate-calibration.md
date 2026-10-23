# Project Overview
- **Game Title:** Campus AR Navigation
- **High-Level Concept:** An Augmented Reality mobile navigation app for college campuses that guides students to buildings and landmarks using QR code relocalization and 3D directional arrows.
- **Players:** Single-user (students, visitors, campus admins).
- **Inspiration / Reference Games:** Google Maps Live View, AR Wayfinding solutions.
- **Tone / Art Direction:** Clean, modern, functional academic navigation utility.
- **Target Platform:** Android (ARCore).
- **Screen Orientation / Resolution:** Portrait 1080x1920.
- **Render Pipeline:** Universal Render Pipeline (URP-Performant).

# Game Mechanics
## Core Gameplay Loop
1. **Calibration:** The user approaches a campus checkpoint (such as the campus entrance) and scans a physical QR code (or triggers simulated scan in debug mode) to lock the AR coordinate system (`MapRoot`).
2. **Destination Selection:** The user picks a target building or room from the dropdown list.
3. **Route Finding:** The app runs an A* pathfinder across the pre-recorded campus waypoint graph to find the shortest accessible route.
4. **Wayfinding:** 3D directional arrows are rendered in AR guiding the user along the path. As the user walks, remaining distance updates in real time until reaching the target destination.

## Controls and Input Methods
- Touch UI: On-screen buttons to start recording, mark destinations, drop anchors, and initiate navigation.
- Camera Motion Tracking: 6-DoF ARCore motion tracking for positioning and orientation.

# UI
- **Start Panel:** Welcome screen with "Scan QR Code" prompt.
- **Search Panel:** Destination dropdown and "Start Navigation" button.
- **Active Navigation HUD:** Top banner showing target destination name, remaining distance in meters, and a "Cancel" button.
- **Admin Panel:** Recording controls showing live node count, edge count, and distance to next node drop, plus manual landmark destination naming.

# Key Asset & Context
- `Assets/Scripts/CampusNavigation/Admin/NodeRecorder.cs`: Records campus nodes and breadcrumbs. Needs update to align and record in `MapRoot` local coordinate space.
- `Assets/Scripts/CampusNavigation/Navigation/NavigationSessionManager.cs`: Handles pathfinding and user position tracking relative to `MapRoot`.
- `Assets/Scenes/SampleScene.unity`: Contains `MapRoot` transform and admin controller bindings.

# Implementation Steps

### Step 1: Add MapRoot Reference and Coordinate Alignment to NodeRecorder.cs
- **Description:** 
  1. Add `[SerializeField] private Transform mapRoot;` to `NodeRecorder.cs` under the recording parameters header.
  2. In `Start()`, add auto-lookup fallback for `GameObject.Find("MapRoot")` if unassigned.
  3. In `StartRecording()`, snap `mapRoot.position` to `arCameraTransform.position` and `mapRoot.rotation` to `Quaternion.Euler(0f, arCameraTransform.eulerAngles.y, 0f)`.
  4. In `GetNormalizedPosition(Vector3 rawWorldPos)`, convert positions using `mapRoot.InverseTransformPoint(rawWorldPos)` so all recorded node positions match `MapRoot` local space identical to `NavigationSessionManager.cs`.
  5. Provide explicit line-by-line code instructions showing exactly where to insert these lines in `NodeRecorder.cs`.
- **Assigned role:** developer
- **Dependencies:** None
- **Parallelizable:** No

### Step 2: Validate Unity Scene Inspector Assignment & Verification
- **Description:** Verify that `NodeRecorder` on the admin controller GameObject has its `mapRoot` field properly populated in the Inspector, and test that starting recording sets the start node at local `(0, 0, 0)` with navigation matching 0m offset.
- **Assigned role:** developer
- **Dependencies:** Depends on Step 1
- **Parallelizable:** No

# Verification & Testing
1. **Compilation Check:** Confirm Unity console compiles with zero errors or warnings.
2. **Start Offset Test:** Start recording at a physical point; verify that the start node (`node_0001`) is recorded at `(0, 0, 0)`.
3. **Loopback Navigation Test:** Walk 20-30 meters, record a destination, stop recording, return to the start point, and tap "Go". Verify that remaining distance reads `< 2.5m` rather than `28m`, and navigation starts without the proximity error.
