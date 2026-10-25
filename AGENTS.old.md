# Campus AR Navigation (Benguet State University)

Offline AR campus navigation, Android. Unity 6, URP, AR Foundation 6.5 + ARCore, C#, JsonUtility. No internet at runtime.

## Source of truth
- Read `PROGRESS.md` and `Assets/Plans/PLAN.md` before any task. Do not assume status beyond them.

## Design
- Campus map = graph of nodes/edges in campus_map.json (CampusNode, CampusEdge, CampusMapData), routed with A*.
- Node positions come from averaged GPS converted to local meters (x = east, z = north) from the map's GPS origin. Never from AR/VIO position (drifts outdoors).
- All nav content lives under the MapRoot transform. Never place waypoints in world space.
- MapRoot alignment: (1) GPS + compass snap with gated, low-rate yaw correction while walking; (2) wall-mounted image markers via ARTrackedImageManager for precision snaps.
- Marker heading = outward normal (transform.up) projected on the horizontal plane. Anchors stored in MapRoot-local space (anchorVersion 2).
- Anchors are side spurs off the walkway graph. Routes never pass through them.

## Rules
- Keep campus_map.json backward compatible (new fields need safe defaults).
- No online services or cloud APIs.
- Edit C# scripts only. Do NOT edit scenes, prefabs, or .meta files. Tell me which Inspector wiring or scene objects I must do myself.
- Implement only the requested items, then stop and give a `git diff` summary.

## Key scripts (Assets/Scripts)
MapDataManager, CampusMapData, CampusNavigation/{Sensors/AndroidSensorManager, Localization/GpsRelocalizer, Localization/QRAnchorRelocalizer, Admin/NodeRecorder, Admin/AdminUIController, Pathfinding/AStarPathfinder, Navigation/NavigationSessionManager, Navigation/UserNavigationUi, Visualizer/ARRouteVisualizer}

## Review workflow
When I say "review changes":
1. Read `Assets/Plans/PLAN.md` and `PROGRESS.md`.
2. Read `changes.diff` (or run `git diff`). Review only changed lines.
3. Compare against the PLAN.md items the changes claim to implement. For each item: Done / Partial / Missing / Wrong.
4. List problems first, each with file, method, and why: bugs, wrong math, missing items, violations of the Rules above. State uncertainty and flag anything that needs a device test.
5. Finish with ONE ready-to-paste prompt for the Unity AI assistant in a code block, containing:
   - numbered fixes, each naming file and method
   - constraints: C# scripts only, no scene/prefab/.meta edits, keep campus_map.json backward compatible, and the Inspector wiring I must do myself
   - "Implement only these items, then stop and run a `git diff` summary of changed files for my review"
6. If nothing needs fixing, say "No changes needed" and give the next PLAN.md step as the Unity prompt.
7. If you need a file or method that isn't in the diff, ask for exactly that one. Don't guess.
Do not edit any files during a review.