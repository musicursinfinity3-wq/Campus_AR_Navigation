using System;
using System.Collections.Generic;
using CampusNavigation.Data;
using CampusNavigation.Localization;
using CampusNavigation.Pathfinding;
using CampusNavigation.Visualizer;
using UnityEngine;

namespace CampusNavigation.Navigation
{
    public class NavigationSessionManager : MonoBehaviour
    {
        public static NavigationSessionManager Instance { get; private set; }

        [Header("References")]
        [SerializeField]
        private Transform arCameraTransform;

        [SerializeField]
        private AStarPathfinder pathfinder;

        [SerializeField]
        private ARRouteVisualizer visualizer;

        [SerializeField]
        private Transform mapRoot;

        [Header("Settings")]
        [Tooltip(
            "Maximum allowed distance in meters between user and nearest node to begin navigation."
        )]
        [SerializeField]
        private float maxSnapRadius = 25.0f;

        public bool IsNavigating { get; private set; }
        public CampusNode CurrentDestination { get; private set; }
        public float RemainingDistance { get; private set; }

        public event Action<CampusNode> OnNavigationStarted;
        public event Action OnNavigationEnded;
        public event Action OnDestinationReached;
        public event Action<string> OnNavigationFailed;

        private List<CampusNode> activeRoute;
        private int routeSegmentIndex;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            if (arCameraTransform == null && Camera.main != null)
                arCameraTransform = Camera.main.transform;

            if (pathfinder == null)
                pathfinder = AStarPathfinder.Instance;

            if (visualizer == null)
                visualizer = ARRouteVisualizer.Instance;

            if (mapRoot == null)
            {
                GameObject rootGo = GameObject.Find("MapRoot");
                if (rootGo != null)
                    mapRoot = rootGo.transform;
            }
        }

        private void Update()
        {
            if (
                !IsNavigating
                || activeRoute == null
                || activeRoute.Count == 0
                || arCameraTransform == null
            )
                return;

            // Calculate distance in MapRoot local space
            Vector3 userMapPos =
                mapRoot != null
                    ? mapRoot.InverseTransformPoint(arCameraTransform.position)
                    : arCameraTransform.position;

            Vector3 destPos = CurrentDestination.localPosition;

            Vector2 userPos = new Vector2(userMapPos.x, userMapPos.z);
            float distanceToDestination = Vector2.Distance(userPos, new Vector2(destPos.x, destPos.z));

            if (activeRoute.Count < 2)
            {
                RemainingDistance = distanceToDestination;
            }
            else
            {
                int bestSegmentIndex = routeSegmentIndex;
                Vector2 bestProjection = userPos;
                float closestDistanceSquared = float.PositiveInfinity;
                int lastCandidate = Mathf.Min(routeSegmentIndex + 2, activeRoute.Count - 2);

                for (int i = routeSegmentIndex; i <= lastCandidate; i++)
                {
                    Vector3 startPos = activeRoute[i].localPosition;
                    Vector3 endPos = activeRoute[i + 1].localPosition;
                    Vector2 start = new Vector2(startPos.x, startPos.z);
                    Vector2 end = new Vector2(endPos.x, endPos.z);
                    Vector2 segment = end - start;
                    float t = segment.sqrMagnitude > 0f
                        ? Mathf.Clamp01(Vector2.Dot(userPos - start, segment) / segment.sqrMagnitude)
                        : 0f;
                    Vector2 projection = start + t * segment;
                    float distanceSquared = (userPos - projection).sqrMagnitude;

                    if (distanceSquared < closestDistanceSquared)
                    {
                        closestDistanceSquared = distanceSquared;
                        bestSegmentIndex = i;
                        bestProjection = projection;
                    }
                }

                routeSegmentIndex = bestSegmentIndex;
                Vector3 segmentEnd = activeRoute[routeSegmentIndex + 1].localPosition;
                RemainingDistance = Mathf.Sqrt(closestDistanceSquared)
                    + Vector2.Distance(bestProjection, new Vector2(segmentEnd.x, segmentEnd.z));

                for (int i = routeSegmentIndex + 1; i < activeRoute.Count - 1; i++)
                {
                    Vector3 startPos = activeRoute[i].localPosition;
                    Vector3 endPos = activeRoute[i + 1].localPosition;
                    RemainingDistance += Vector2.Distance(
                        new Vector2(startPos.x, startPos.z), new Vector2(endPos.x, endPos.z));
                }
            }

            // Arrival check: within 2.5m of final destination
            if (distanceToDestination <= 2.5f)
            {
                Debug.Log($"[NavigationSessionManager] Arrived at {CurrentDestination.label}!");
                OnDestinationReached?.Invoke();
                StopNavigation();
            }
        }

        /// <summary>
        /// Starts navigation to a specific destination node by ID.
        /// </summary>
        public bool StartNavigation(string destinationNodeId)
        {
            if (pathfinder == null)
                pathfinder = AStarPathfinder.Instance;
            if (visualizer == null)
                visualizer = ARRouteVisualizer.Instance;
            if (arCameraTransform == null && Camera.main != null)
                arCameraTransform = Camera.main.transform;

            if (pathfinder == null || visualizer == null || arCameraTransform == null)
            {
                string error = "Missing navigation components.";
                Debug.LogError($"[NavigationSessionManager] {error}");
                OnNavigationFailed?.Invoke(error);
                return false;
            }

            // If MapRoot has not been aligned by a QR anchor yet, snap MapRoot
            // so the initial path segment aligns directly in front of the camera:
#if UNITY_EDITOR
            if (
                mapRoot != null
                && (
                    QRAnchorRelocalizer.Instance == null
                    || !QRAnchorRelocalizer.Instance.IsRelocalized
                )
            )
            {
                if (
                    MapDataManager.Instance != null
                    && MapDataManager.Instance.CurrentMap != null
                    && MapDataManager.Instance.CurrentMap.nodes.Count > 0
                )
                {
                    var nodes = MapDataManager.Instance.CurrentMap.nodes;
                    CampusNode startNode = nodes[0];

                    // Calculate the angle the path actually heads in local space
                    float recordedPathYaw = startNode.qrFacingEuler.y;
                    if (nodes.Count > 1)
                    {
                        Vector3 walkDir = (
                            nodes[1].localPosition - startNode.localPosition
                        ).normalized;
                        if (walkDir.sqrMagnitude > 0.001f)
                        {
                            recordedPathYaw = Mathf.Atan2(walkDir.x, walkDir.z) * Mathf.Rad2Deg;
                        }
                    }

                    // Rotate MapRoot so recordedPathYaw matches the current camera's Y-facing angle
                    float deltaYaw = arCameraTransform.eulerAngles.y - recordedPathYaw;
                    mapRoot.rotation = Quaternion.Euler(0f, deltaYaw, 0f);
                    mapRoot.position =
                        arCameraTransform.position - (mapRoot.rotation * startNode.localPosition);

                    Debug.Log(
                        $"[NavigationSessionManager] Auto-aligned MapRoot: deltaYaw={deltaYaw:F1}°, CamYaw={arCameraTransform.eulerAngles.y:F1}°, PathYaw={recordedPathYaw:F1}°"
                    );
                }
            }
#else
            // On physical devices, enforce a "Relocalized" state before allowing navigation.
            bool isRelocalized = QRAnchorRelocalizer.Instance != null && QRAnchorRelocalizer.Instance.IsRelocalized;
            bool hasGpsSnap = GpsRelocalizer.Instance != null && GpsRelocalizer.Instance.HasPerformedInitialSnap;

            if (!isRelocalized && !hasGpsSnap)
            {
                string error = "Scan a marker or wait for GPS to align the map before starting navigation.";
                Debug.LogWarning($"[NavigationSessionManager] {error}");
                OnNavigationFailed?.Invoke("Scan a marker or wait for GPS");
                return false;
            }
#endif

            // Convert World Camera position to MapRoot Local position
            Vector3 userLocalPos =
                mapRoot != null
                    ? mapRoot.InverseTransformPoint(arCameraTransform.position)
                    : arCameraTransform.position;

            // 2. Find the closest node using the converted local position
            // Now pass the LOCAL position to the pathfinder
            CampusNode nearest = pathfinder.FindNearestNode(userLocalPos);
            if (nearest == null)
            {
                string error = "No campus paths found. Please record or load a campus map first.";
                Debug.LogWarning($"[NavigationSessionManager] {error}");
                OnNavigationFailed?.Invoke(error);
                return false;
            }

            // 3. Distance check in unified local space
            float distToStart = Vector2.Distance(
                new Vector2(userLocalPos.x, userLocalPos.z),
                new Vector2(nearest.localPosition.x, nearest.localPosition.z));
            if (distToStart > maxSnapRadius)
            {
                string error =
                    $"You are {distToStart:F0}m away from the nearest campus path. Please walk towards the campus entrance or scan a directory QR code.";
                Debug.LogWarning($"[NavigationSessionManager] {error}");
                OnNavigationFailed?.Invoke(error);
                return false;
            }

            // 2. Compute shortest route via A*
            activeRoute = pathfinder.FindPath(nearest.id, destinationNodeId);
            routeSegmentIndex = 0;
            if (activeRoute == null || activeRoute.Count == 0)
            {
                string error = $"No connected path found to destination.";
                Debug.LogWarning($"[NavigationSessionManager] {error}");
                OnNavigationFailed?.Invoke(error);
                return false;
            }

            CurrentDestination = activeRoute[activeRoute.Count - 1];
            IsNavigating = true;

            // 3. Display 3D arrows in AR
            visualizer.DisplayRoute(activeRoute);

            Debug.Log(
                $"[NavigationSessionManager] Navigation started to '{CurrentDestination.label}'. Waypoints: {activeRoute.Count}"
            );
            OnNavigationStarted?.Invoke(CurrentDestination);
            return true;
        }

        /// <summary>
        /// Cancels current navigation and clears AR arrows.
        /// </summary>
        public void StopNavigation()
        {
            routeSegmentIndex = 0;
            if (!IsNavigating)
                return;

            IsNavigating = false;
            CurrentDestination = null;
            activeRoute = null;

            if (visualizer != null)
            {
                visualizer.ClearActiveRoute();
            }

            Debug.Log("[NavigationSessionManager] Navigation stopped.");
            OnNavigationEnded?.Invoke();
        }
    }
}
