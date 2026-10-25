# Campus AR Navigation (Benguet State University)

Offline AR campus navigation, Android. Unity 6, URP, AR Foundation 6.5 + ARCore, C#, JsonUtility. No internet at runtime.

## Source of truth
- `PROGRESS.md` and `Assets/Plans/PLAN.md`. Read both before any task.
- Do not assume status, behavior, or missing code beyond what they and the source files show.
- Do not edit `PLAN.md`, `PROGRESS.md`, or `DECISIONS.md` unless the task says so.

## Design
- Campus map = graph of nodes/edges in `campus_map.json` (CampusNode, CampusEdge, CampusMapData), routed with A*.
- Node positions come from averaged GPS converted to local meters (x = east, z = north) from the map's GPS origin. Never from AR/VIO position (drifts outdoors).
- All nav content lives under the MapRoot transform. Never place waypoints in world space.
- MapRoot alignment: (1) GPS + compass snap with gated, low-rate yaw correction while walking; (2) wall-mounted image markers via ARTrackedImageManager for precision snaps.
- Marker heading = outward normal (`transform.up`) projected on the horizontal plane. Anchors stored in MapRoot-local space (`anchorVersion` 2, `qrFacingEuler` = (0, heading, 0)).
- Anchors are side spurs off the walkway graph. Routes never pass through them.
- Node distances go through `CampusMapData.CalculateNodeDistance` (GPS if valid, else local 3D fallback).

## Rules
- Keep `campus_map.json` backward compatible (new fields need safe defaults).
- No online services or cloud APIs.
- Edit C# scripts only. Do NOT edit scenes, prefabs, `.asset` files, `campus_map.json`, or `.meta` files unless you are told to do so.
- If a task needs Inspector wiring or scene changes, do not attempt them. List exactly what I must do myself.
- Implement only the numbered items in the task. No refactors, renames, or extra "improvements".
- If an item needs a file or method you have not been given, say which one and stop. Do not guess.
- State uncertainty. Flag anything that needs a device test instead of claiming it works.

## Key scripts (Assets/Scripts)
MapDataManager, CampusMapData, CampusNavigation/{Sensors/AndroidSensorManager, Localization/GpsRelocalizer, Localization/QRAnchorRelocalizer, Admin/NodeRecorder, Admin/AdminUIController, Pathfinding/AStarPathfinder, Navigation/NavigationSessionManager, Navigation/UserNavigationUi, Visualizer/ARRouteVisualizer}

## When you finish a task
1. Stop. Do not start the next item or phase.
2. Run `git diff --stat` and give a summary of changed files, with the methods changed in each.
3. List any Inspector/scene steps I must do manually.
4. List anything uncertain or needing a device test.