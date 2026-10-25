using System.Collections.Generic;
using CampusNavigation.Data;
using UnityEngine;

namespace CampusNavigation.Visualizer
{
    public class ARRouteVisualizer : MonoBehaviour
    {
        public static ARRouteVisualizer Instance { get; private set; }

        [Header("Visualization Prefabs")]
        [Tooltip(
            "Prefab spawned along the path (e.g. arrow or chevron pointing forward). If null, a default primitive arrow is generated."
        )]
        [SerializeField]
        private GameObject arrowPrefab;

        [Tooltip("Prefab spawned at the final destination point.")]
        [SerializeField]
        private GameObject destinationPinPrefab;

        [Header("Path Settings")]
        [Tooltip("Distance in meters between instantiated arrows along a sidewalk segment.")]
        [SerializeField]
        private float arrowSpacing = 1.5f;

        [Tooltip("Maximum distance in meters from the camera that arrows will be visible.")]
        [SerializeField]
        private float maxVisibleDistance = 15.0f;

        [Tooltip("Distance in meters to consider a waypoint reached and remove it from view.")]
        [SerializeField]
        private float waypointReachRadius = 2.0f;

        [Tooltip(
            "Offset in meters above the node's y, since MapRoot.y is ground level."
        )]
        [SerializeField]
        private float groundYOffset = 0.02f;

        [Header("References")]
        [SerializeField]
        private Transform arCameraTransform;

        [SerializeField]
        private Transform mapRoot;

        // Active path state
        private List<CampusNode> currentRoute = new List<CampusNode>();
        private List<GameObject> activeRouteObjects = new List<GameObject>();
        private List<int> activeArrowSegmentIndex = new List<int>();
        private int currentSegment;
        private bool destinationReachedLogged;
        private int currentTargetIndex = 0;

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
            if (mapRoot == null)
            {
                GameObject rootGo = GameObject.Find("MapRoot");
                if (rootGo != null)
                    mapRoot = rootGo.transform;
            }

            if (arCameraTransform == null && Camera.main != null)
            {
                arCameraTransform = Camera.main.transform;
            }
        }

        private void Update()
        {
            if (currentRoute == null || currentRoute.Count == 0 || arCameraTransform == null)
                return;
            if (currentTargetIndex >= currentRoute.Count)
                return;

            // Calculate user position in MapRoot local space
            Vector3 userMapPos =
                mapRoot != null
                    ? mapRoot.InverseTransformPoint(arCameraTransform.position)
                    : arCameraTransform.position;

            Vector2 userPos = new Vector2(userMapPos.x, userMapPos.z);
            int bestSegment = currentSegment;
            float closestDistanceSquared = float.PositiveInfinity;
            int lastCandidate = Mathf.Min(currentSegment + 2, currentRoute.Count - 2);
            for (int i = currentSegment; i <= lastCandidate; i++)
            {
                Vector3 startPos = currentRoute[i].localPosition;
                Vector3 endPos = currentRoute[i + 1].localPosition;
                Vector2 start = new Vector2(startPos.x, startPos.z);
                Vector2 segment = new Vector2(endPos.x, endPos.z) - start;
                float t = segment.sqrMagnitude > 0f
                    ? Mathf.Clamp01(Vector2.Dot(userPos - start, segment) / segment.sqrMagnitude)
                    : 0f;
                Vector2 projection = start + t * segment;
                float distanceSquared = (userPos - projection).sqrMagnitude;
                if (distanceSquared < closestDistanceSquared)
                {
                    closestDistanceSquared = distanceSquared;
                    bestSegment = i;
                }
            }
            currentSegment = bestSegment;
            currentTargetIndex = currentSegment + 1;

            if (!destinationReachedLogged && currentSegment == currentRoute.Count - 2)
            {
                Vector3 startPos = currentRoute[currentSegment].localPosition;
                Vector3 endPos = currentRoute[currentSegment + 1].localPosition;
                Vector2 start = new Vector2(startPos.x, startPos.z);
                Vector2 end = new Vector2(endPos.x, endPos.z);
                Vector2 segment = end - start;
                bool passedEnd = segment.sqrMagnitude > 0f
                    ? Vector2.Dot(userPos - start, segment) >= segment.sqrMagnitude
                    : (userPos - end).sqrMagnitude <= 0.0001f;
                if (passedEnd)
                {
                    destinationReachedLogged = true;
                    Debug.Log("[ARRouteVisualizer] Destination reached!");
                }
            }
            if (arCameraTransform != null)
            {
                Vector3 camPos = arCameraTransform.position;
                Vector3 camForward = arCameraTransform.forward;

                for (int i = 0; i < activeRouteObjects.Count; i++)
                {
                    GameObject arrow = activeRouteObjects[i];
                    if (activeArrowSegmentIndex[i] < currentSegment)
                    {
                        if (arrow != null) Destroy(arrow);
                        activeRouteObjects[i] = null;
                        continue;
                    }
                    if (arrow != null)
                    {
                        float dist = Vector3.Distance(camPos, arrow.transform.position);

                        // Check distance limit
                        bool isWithinDistance = dist <= maxVisibleDistance;

                        // Check if behind the user
                        Vector3 toArrow = arrow.transform.position - camPos;
                        bool isBehind = Vector3.Dot(camForward, toArrow) < 0 && dist > 1.5f;

                        // Only show if close enough AND in front
                        arrow.SetActive(isWithinDistance && !isBehind);
                    }
                }
            }
        }

        /// <summary>
        /// Clears any existing path and builds 3D arrows along the newly provided route.
        /// </summary>
        public void DisplayRoute(List<CampusNode> route)
        {
            ClearActiveRoute();

            if (route == null || route.Count < 2)
            {
                Debug.LogWarning("[ARRouteVisualizer] Route must contain at least 2 nodes.");
                return;
            }

            currentRoute = new List<CampusNode>(route);
            currentTargetIndex = 1;

            // Spawn arrows segment by segment in MapRoot local space
            for (int i = 0; i < currentRoute.Count - 1; i++)
            {
                Vector3 pA = currentRoute[i].localPosition + Vector3.up * groundYOffset;
                Vector3 pB = currentRoute[i + 1].localPosition + Vector3.up * groundYOffset;
                pA.y = groundYOffset;
                pB.y = groundYOffset;

                Vector3 direction = (pB - pA).normalized;
                float segmentLength = Vector3.Distance(pA, pB);
                Quaternion arrowRotation =
                    direction != Vector3.zero
                        ? Quaternion.LookRotation(direction, Vector3.up)
                        : Quaternion.identity;

                int arrowCount = Mathf.Max(1, Mathf.FloorToInt(segmentLength / arrowSpacing));
                for (int j = 1; j <= arrowCount; j++)
                {
                    float t = (float)j / (arrowCount + 1);
                    Vector3 arrowPos = Vector3.Lerp(pA, pB, t);

                    GameObject arrowObj = SpawnArrow(arrowPos, arrowRotation);
                    activeRouteObjects.Add(arrowObj);
                    activeArrowSegmentIndex.Add(i);
                }
            }

            // Spawn final destination marker in MapRoot local space
            Vector3 destinationPos =
                currentRoute[currentRoute.Count - 1].localPosition + Vector3.up * groundYOffset;
            destinationPos.y = groundYOffset;
            GameObject pin = SpawnDestinationPin(destinationPos);
            if (pin != null)
            {
                activeRouteObjects.Add(pin);
                activeArrowSegmentIndex.Add(currentRoute.Count - 2);
            }

            Debug.Log(
                $"[ARRouteVisualizer] Route displayed with {activeRouteObjects.Count} visual elements."
            );
        }

        /// <summary>
        /// Clears all visual arrows and destination markers currently spawned.
        /// </summary>
        public void ClearActiveRoute()
        {
            foreach (var obj in activeRouteObjects)
            {
                if (obj != null)
                    Destroy(obj);
            }
            activeRouteObjects.Clear();
            activeArrowSegmentIndex.Clear();
            currentRoute.Clear();
            currentSegment = 0;
            destinationReachedLogged = false;
            currentTargetIndex = 0;
        }

        private GameObject SpawnArrow(Vector3 localPos, Quaternion localRot)
        {
            Transform parentTransform = mapRoot != null ? mapRoot : transform;

            if (arrowPrefab != null)
            {
                GameObject arrow = Instantiate(arrowPrefab, parentTransform);
                arrow.transform.localPosition = localPos;
                arrow.transform.localRotation = localRot;
                return arrow;
            }

            // Fallback primitive chevron arrow if no prefab assigned
            GameObject arrowFallback = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            arrowFallback.name = "AR_Arrow";
            arrowFallback.transform.SetParent(parentTransform, false);
            arrowFallback.transform.localPosition = localPos;
            arrowFallback.transform.localRotation = localRot * Quaternion.Euler(90f, 0f, 0f);
            arrowFallback.transform.localScale = new Vector3(0.12f, 0.35f, 0.12f);

            Collider col = arrowFallback.GetComponent<Collider>();
            if (col != null)
                Destroy(col);

            return arrowFallback;
        }

        private GameObject SpawnDestinationPin(Vector3 localPos)
        {
            Transform parentTransform = mapRoot != null ? mapRoot : transform;

            if (destinationPinPrefab != null)
            {
                GameObject pin = Instantiate(destinationPinPrefab, parentTransform);
                pin.transform.localPosition = localPos;
                return pin;
            }

            // Fallback primitive pin
            GameObject pinFallback = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            pinFallback.name = "AR_DestinationPin";
            pinFallback.transform.SetParent(parentTransform, false);
            pinFallback.transform.localPosition = localPos + Vector3.up * 0.8f;
            pinFallback.transform.localScale = new Vector3(0.4f, 0.4f, 0.4f);

            Collider col = pinFallback.GetComponent<Collider>();
            if (col != null)
                Destroy(col);

            return pinFallback;
        }
    }
}
