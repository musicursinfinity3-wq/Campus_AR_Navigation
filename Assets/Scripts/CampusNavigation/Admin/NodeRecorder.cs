using System;
using System.Collections;
using System.Collections.Generic;
using CampusNavigation.Data;
using CampusNavigation.Localization;
using CampusNavigation.Sensors;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace CampusNavigation.Admin
{
    public class NodeRecorder : MonoBehaviour
    {
        public static NodeRecorder Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        [Header("Recording Parameters")]
        [Tooltip("Minimum distance in meters (GPS surface distance) between recorded breadcrumbs.")]
        [SerializeField]
        private float nodeDropInterval = 8.0f;
        public float NodeDropInterval => nodeDropInterval;

        [Tooltip("Maximum allowed GPS horizontal accuracy (in meters) to drop or record a node.")]
        [SerializeField]
        private float maxAllowedAccuracyMeters = 10.0f;

        [Header("Multi-Sample GPS Averaging")]
        [Tooltip("Number of seconds to sample and average GPS fixes when marking destinations/junctions.")]
        [SerializeField]
        private float sampleDurationSeconds = 4.0f;

        [Tooltip("Interval between successive GPS samples during averaging.")]
        [SerializeField]
        private float sampleIntervalSeconds = 0.5f;

        [Header("Graph Stitching Settings")]
        [Tooltip("Search radius in meters when manually linking to an existing nearby junction.")]
        [SerializeField]
        private float manualJunctionLinkRadius = 12.0f;

        [Tooltip("If true, automatically detects existing recorded nodes near the user and links them to create intersections.")]
        [SerializeField]
        private bool autoConnectIntersections = false;

        [Tooltip("Distance threshold (meters) to connect to existing nodes as a junction/intersection.")]
        [SerializeField]
        private float intersectionConnectRadius = 5.0f;

        [Header("References")]
        [Tooltip("Reference to the AR Camera. If null, Camera.main is used.")]
        [SerializeField]
        private Transform arCameraTransform;

        [Tooltip("The parent container transform for navigation nodes (MapRoot). Coordinates will be recorded in MapRoot local space.")]
        [SerializeField]
        private Transform mapRoot;

        [Header("Visualization (Optional)")]
        [Tooltip("Visual prefab dropped at each node position during recording.")]
        [SerializeField]
        private GameObject nodeDebugMarkerPrefab;

        // Recording state
        public bool IsRecording { get; private set; }
        public bool IsSampling { get; private set; }
        public bool IsDialogOpen { get; set; } // Flag to pause auto-drop while dialogs are open
        public float SamplingProgress { get; private set; } // 0.0 to 1.0
        public int RecordedNodeCount => currentRecordedMap?.nodes.Count ?? 0;
        public int RecordedEdgeCount => currentRecordedMap?.edges.Count ?? 0;
        public float DistanceToNextDrop { get; private set; }

        private CampusMapData currentRecordedMap;
        private CampusNode lastRecordedNode;
        private List<GameObject> spawnedDebugMarkers = new List<GameObject>();

        public event Action<CampusNode> OnNodeRecorded;
        public event Action<CampusEdge> OnEdgeRecorded;
        public event Action OnRecordingStateChanged;
        public event Action<float> OnSamplingProgressChanged;
        public event Action<string> OnRecordingError;

        private void Start()
        {
            if (arCameraTransform == null && Camera.main != null)
            {
                arCameraTransform = Camera.main.transform;
            }

            if (mapRoot == null)
            {
                GameObject rootGo = GameObject.Find("MapRoot");
                if (rootGo != null)
                    mapRoot = rootGo.transform;
            }
        }

        /// <summary>
        /// Starts a new recording session. 
        /// startNodeId: if provided, resumes recording from this existing node.
        /// </summary>
        public void StartRecording(string startNodeId = null, string mapName = "BSU_MainCampus_Map")
        {
            if (AndroidSensorManager.Instance == null || AndroidSensorManager.Instance.Status != SensorStatus.Ready)
            {
                OnRecordingError?.Invoke("GPS is not ready. Wait for sensor initialization.");
                return;
            }

            if (arCameraTransform == null && Camera.main != null)
            {
                arCameraTransform = Camera.main.transform;
            }

            if (mapRoot == null)
            {
                GameObject rootGo = GameObject.Find("MapRoot");
                if (rootGo != null)
                    mapRoot = rootGo.transform;
            }

            // Append to existing map if present, otherwise initialize fresh
            bool isAppending =
                MapDataManager.Instance != null
                && MapDataManager.Instance.CurrentMap != null
                && MapDataManager.Instance.CurrentMap.nodes.Count > 0;

            if (isAppending)
            {
                currentRecordedMap = MapDataManager.Instance.CurrentMap;
                if (currentRecordedMap.originLatitude == 0)
                {
                    OnRecordingError?.Invoke("Cannot append to a map with no GPS origin.");
                    return;
                }

                // Block if MapRoot is not aligned (QR or GPS snap)
                bool isAligned = (QRAnchorRelocalizer.Instance != null && QRAnchorRelocalizer.Instance.IsRelocalized) ||
                                 (GpsRelocalizer.Instance != null && GpsRelocalizer.Instance.HasPerformedInitialSnap);
                
                if (!isAligned)
                {
                    OnRecordingError?.Invoke("MapRoot is not aligned. Scan a QR or wait for GPS snap before appending.");
                    return;
                }

                Debug.Log($"[NodeRecorder] Appending to existing map with {currentRecordedMap.nodes.Count} nodes.");
            }
            else
            {
                // Fresh map setup: Exempt from alignment guard, we set the origin here.
                currentRecordedMap = new CampusMapData
                {
                    campusName = mapName,
                    mapVersion = "1.0.0",
                };

                // For a brand new map, we wait for the first good accuracy fix to bootstrap.
                StartCoroutine(BootstrapFreshMapRoutine());
                return;
            }

            lastRecordedNode = null;
            if (!string.IsNullOrEmpty(startNodeId))
            {
                lastRecordedNode = currentRecordedMap.nodes.Find(n => n.id == startNodeId);
                if (lastRecordedNode != null)
                {
                    Debug.Log($"[NodeRecorder] Resuming recording from existing node: {lastRecordedNode.label} ({lastRecordedNode.id})");
                }
            }

            ClearDebugMarkers();
            IsRecording = true;
            Debug.Log("[NodeRecorder] Recording session started.");
            OnRecordingStateChanged?.Invoke();

            // If no start node specified and appending, record initial waypoint at current location
            if (lastRecordedNode == null)
            {
                RecordNode(NodeType.Waypoint, isAppending ? "Waypoint" : "Start Point");
            }
        }

        private IEnumerator BootstrapFreshMapRoutine()
        {
            Debug.Log("[NodeRecorder] Waiting for high-accuracy GPS fix to bootstrap fresh map...");
            float timeout = 60f;
            float elapsed = 0f;
            
            while (elapsed < timeout)
            {
                if (AndroidSensorManager.Instance != null && AndroidSensorManager.Instance.Status == SensorStatus.Ready)
                {
                    if (AndroidSensorManager.Instance.HorizontalAccuracy <= maxAllowedAccuracyMeters)
                    {
                        // Check compass stability
                        float heading = AndroidSensorManager.Instance.CurrentHeading;
                        yield return new WaitForSeconds(1f);
                        elapsed += 1f;
                        
                        if (Mathf.Abs(Mathf.DeltaAngle(heading, AndroidSensorManager.Instance.CurrentHeading)) <= 10f)
                        {
                            // Success!
                            BootstrapFinish();
                            yield break;
                        }
                        Debug.LogWarning("[NodeRecorder] Compass unstable, continuing wait...");
                        continue;
                    }
                }
                
                yield return new WaitForSeconds(1f);
                elapsed += 1f;
            }

            OnRecordingError?.Invoke("GPS timeout — move outdoors");
        }

        private void BootstrapFinish()
        {
            if (AndroidSensorManager.Instance == null || currentRecordedMap == null) return;

            // 1. Set origin
            currentRecordedMap.originLatitude = AndroidSensorManager.Instance.CurrentLatitude;
            currentRecordedMap.originLongitude = AndroidSensorManager.Instance.CurrentLongitude;
            currentRecordedMap.originAltitude = AndroidSensorManager.Instance.CurrentAltitude;

            // 2. Initialize MapRoot alignment
            if (mapRoot != null && arCameraTransform != null)
            {
                float cameraYaw = arCameraTransform.eulerAngles.y;
                float compassHeading = AndroidSensorManager.Instance.CurrentHeading;
                float mapRootYaw = (cameraYaw - compassHeading);

                mapRoot.rotation = Quaternion.Euler(0f, mapRootYaw, 0f);
                mapRoot.position = arCameraTransform.position; // Positioned at camera
                
                MapDataManager.Instance?.SetCurrentMap(currentRecordedMap);
                GpsRelocalizer.Instance?.MarkExternallyAligned();
                Debug.Log("[NodeRecorder] Fresh map MapRoot initialized and aligned to GPS/Compass.");
            }

            ClearDebugMarkers();
            IsRecording = true;
            Debug.Log("[NodeRecorder] Fresh map recording session started.");
            OnRecordingStateChanged?.Invoke();

            RecordNode(NodeType.Waypoint, "Start Point");
        }

        private void Update()
        {
            if (!IsRecording || lastRecordedNode == null || IsSampling || IsDialogOpen)
                return;

            float currentAccuracy = 999f;
            bool hasLiveGps = AndroidSensorManager.Instance != null && AndroidSensorManager.Instance.IsGpsRunning;
            if (hasLiveGps)
            {
                currentAccuracy = AndroidSensorManager.Instance.HorizontalAccuracy;
                // Accuracy gate for live hardware GPS
                if (currentAccuracy > maxAllowedAccuracyMeters)
                    return;
            }

            // Calculate distance since last recorded node:
            // Prefer GPS distance if valid non-zero GPS coordinates exist on both points,
            // otherwise compute distance using camera position in MapRoot local space.
            double currLat = hasLiveGps ? AndroidSensorManager.Instance.CurrentLatitude : 0.0;
            double currLon = hasLiveGps ? AndroidSensorManager.Instance.CurrentLongitude : 0.0;

            if (Math.Abs(lastRecordedNode.latitude) > 0.0001 && Math.Abs(currLat) > 0.0001 &&
                Math.Abs(lastRecordedNode.longitude) > 0.0001 && Math.Abs(currLon) > 0.0001)
            {
                DistanceToNextDrop = CampusMapData.CalculateDistanceMeters(
                    lastRecordedNode.latitude,
                    lastRecordedNode.longitude,
                    currLat,
                    currLon
                );
            }
            else if (arCameraTransform != null)
            {
                Vector3 currentCamLocalPos = mapRoot != null
                    ? mapRoot.InverseTransformPoint(arCameraTransform.position)
                    : arCameraTransform.position;
                DistanceToNextDrop = Vector3.Distance(lastRecordedNode.localPosition, currentCamLocalPos);
            }
            else
            {
                DistanceToNextDrop = 0f;
            }

            if (DistanceToNextDrop >= nodeDropInterval)
            {
                Debug.Log(
                    $"<color=green>[NodeRecorder] ★ AUTO-DROPPED BREADCRUMB</color> - Distance={DistanceToNextDrop:F1}m (GPS Accuracy={currentAccuracy:F1}m)"
                );
                RecordNode(NodeType.Waypoint);
            }
        }

        /// <summary>
        /// Records a single node at current location.
        /// </summary>
        public CampusNode RecordNode(NodeType type, string label = null, string qrPayload = null)
        {
            if (!IsRecording)
            {
                Debug.LogWarning("[NodeRecorder] Cannot record node: Not in recording mode.");
                return null;
            }

            double lat = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentLatitude : 0.0;
            double lon = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentLongitude : 0.0;
            float alt = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentAltitude : 0f;
            float acc = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.HorizontalAccuracy : 999f;

            Vector3 derivedLocalPos = DeriveLocalPosition(lat, lon);

            CampusNode newNode = new CampusNode
            {
                id = NextNodeId(),
                label = string.IsNullOrEmpty(label) ? $"Node {currentRecordedMap.nodes.Count + 1}" : label,
                nodeType = type,
                latitude = lat,
                longitude = lon,
                altitude = alt,
                horizontalAccuracy = acc,
                sampleCount = 1,
                recordedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                localPosition = derivedLocalPos,
                qrPayload = qrPayload,
                qrFacingEuler = arCameraTransform != null ? arCameraTransform.eulerAngles : Vector3.zero,
            };

            AddNodeToMap(newNode);
            return newNode;
        }

        private string NextNodeId()
        {
            int maxId = 0;
            if (currentRecordedMap != null)
            {
                foreach (var node in currentRecordedMap.nodes)
                {
                    if (node.id.StartsWith("node_") && int.TryParse(node.id.Substring(5), out int id))
                    {
                        if (id > maxId) maxId = id;
                    }
                }
            }
            return $"node_{(maxId + 1):D4}";
        }

        /// <summary>
        /// Specifically records a QR Anchor node using an actively tracked image's pose.
        /// Heading and position are stored in MapRoot-local space.
        /// Connects as a spur to the nearest non-anchor node.
        /// </summary>
        public CampusNode RecordQRAnchor(string qrPayload, string label = null)
        {
            if (!IsRecording)
            {
                Debug.LogWarning("[NodeRecorder] Cannot record QR Anchor: Not recording.");
                return null;
            }

            if (mapRoot == null)
            {
                GameObject rootGo = GameObject.Find("MapRoot");
                if (rootGo != null) mapRoot = rootGo.transform;
            }

            ARTrackedImageManager imageManager = FindFirstObjectByType<ARTrackedImageManager>();
            ARTrackedImage targetImage = null;

            if (imageManager != null)
            {
                // If payload is null, pick the closest actively tracked image
                if (string.IsNullOrEmpty(qrPayload))
                {
                    float minDist = float.MaxValue;
                    foreach (var img in imageManager.trackables)
                    {
                        if (img.trackingState == UnityEngine.XR.ARSubsystems.TrackingState.Tracking)
                        {
                            float d = Vector3.Distance(arCameraTransform.position, img.transform.position);
                            if (d < minDist)
                            {
                                minDist = d;
                                targetImage = img;
                            }
                        }
                    }
                }
                else
                {
                    foreach (var img in imageManager.trackables)
                    {
                        if (img.trackingState == UnityEngine.XR.ARSubsystems.TrackingState.Tracking && img.referenceImage.name == qrPayload)
                        {
                            targetImage = img;
                            break;
                        }
                    }
                }
            }

            Vector3 localPos;
            float localHeading;

            if (targetImage != null)
            {
                localPos = mapRoot != null ? mapRoot.InverseTransformPoint(targetImage.transform.position) : targetImage.transform.position;
                localHeading = CampusMapData.ComputeMarkerHeading(targetImage.transform, mapRoot, out bool isFlat);
                if (isFlat)
                {
                    Debug.LogWarning($"[NodeRecorder] Marker '{targetImage.referenceImage.name}' appears flat (normal nearly vertical). Using fallback top-edge heading.");
                }
                if (string.IsNullOrEmpty(qrPayload))
                {
                    qrPayload = targetImage.referenceImage.name;
                }
            }
            else
            {
#if UNITY_EDITOR
                // Editor-only simulation path: user simulates holding a marker directly in front of the camera.
                if (arCameraTransform == null && Camera.main != null) arCameraTransform = Camera.main.transform;
                if (arCameraTransform != null)
                {
                    Vector3 simMarkerPos = arCameraTransform.position + arCameraTransform.forward * 1.5f;
                    localPos = mapRoot != null ? mapRoot.InverseTransformPoint(simMarkerPos) : simMarkerPos;
                    Vector3 outwardNormalInMapRoot = mapRoot != null
                        ? mapRoot.InverseTransformDirection(-arCameraTransform.forward)
                        : -arCameraTransform.forward;
                    Vector3 proj = Vector3.ProjectOnPlane(outwardNormalInMapRoot, Vector3.up).normalized;
                    localHeading = Vector3.SignedAngle(Vector3.forward, proj, Vector3.up);
                    if (string.IsNullOrEmpty(qrPayload)) qrPayload = "DEBUG_QR_PAYLOAD";
                    Debug.LogWarning($"<color=yellow>[NodeRecorder] Editor Simulated QR Anchor recorded:</color> Payload='{qrPayload}', Facing outward toward camera (heading={localHeading:F1}°)");
                }
                else
                {
                    Debug.LogError("[NodeRecorder] Cannot simulate QR Anchor: Camera transform is null.");
                    return null;
                }
#else
                Debug.LogError("[NodeRecorder] FAILED TO RECORD QR ANCHOR: No actively tracked image found in camera view.");
                return null;
#endif
            }

            CampusNode newNode = new CampusNode
            {
                id = NextNodeId(),
                label = string.IsNullOrEmpty(label) ? $"QR_{qrPayload}" : label,
                nodeType = NodeType.QRAnchor,
                localPosition = localPos,
                qrPayload = qrPayload,
                qrFacingEuler = new Vector3(0f, localHeading, 0f),
                anchorVersion = CampusMapData.CURRENT_ANCHOR_VERSION,
                latitude = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentLatitude : 0,
                longitude = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentLongitude : 0,
                altitude = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentAltitude : 0,
                horizontalAccuracy = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.HorizontalAccuracy : 999f,
                sampleCount = 1,
                recordedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            // Spur Pattern: connect to nearest non-anchor node
            CampusNode nearest = FindNearestNonAnchorNode(newNode.localPosition);
            
            currentRecordedMap.nodes.Add(newNode);
            
            if (nearest != null)
            {
                float dx = newNode.localPosition.x - nearest.localPosition.x;
                float dz = newNode.localPosition.z - nearest.localPosition.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                
                CampusEdge edge = new CampusEdge
                {
id = $"edge_{newNode.id}_{nearest.id}",
                    fromNodeId = newNode.id,
                    toNodeId = nearest.id,
                    distance = Mathf.Max(0.1f, dist),
                    isAccessible = true,
                    isStairs = false
                };
                currentRecordedMap.edges.Add(edge);
                OnEdgeRecorded?.Invoke(edge);
                Debug.Log($"[NodeRecorder] QR Anchor '{newNode.id}' connected as spur to '{nearest.id}' ({dist:F1}m)");
            }

            Debug.Log($"<color=cyan>[NodeRecorder] ✓ RECORDED QR ANCHOR (v{newNode.anchorVersion}):</color> ID={newNode.id} | Payload='{newNode.qrPayload}' | LocalPos={newNode.localPosition}");
            OnNodeRecorded?.Invoke(newNode);
            
            // Anchor-Spur Pattern: QRAnchor must NOT become lastRecordedNode
            return newNode;
        }

        private CampusNode FindNearestNonAnchorNode(Vector3 localPos)
        {
            CampusNode nearest = null;
            float minDist = float.MaxValue;
            foreach (var node in currentRecordedMap.nodes)
            {
                if (node.nodeType == NodeType.QRAnchor) continue;
                
                float dx = localPos.x - node.localPosition.x;
                float dz = localPos.z - node.localPosition.z;
                float dSqr = dx * dx + dz * dz;
                
                if (dSqr < minDist)
                {
                    minDist = dSqr;
                    nearest = node;
                }
            }
return nearest;
        }

        /// <summary>
        /// Marks a destination (Office, Facility) by median GPS samples over a multi-second window.
        /// </summary>
        public void MarkDestination(string destinationName)
        {
            if (!IsRecording) return;
            StartCoroutine(SampleAndRecordRoutine(NodeType.Destination, destinationName, null, null));
        }

        /// <summary>
        /// Helper: Creates a Destination node at user's current averaged position and links it to the provided anchor.
        /// </summary>
        public void MarkDestinationAtAnchor(string anchorId, string label)
        {
            if (!IsRecording) return;
            StartCoroutine(SampleAndRecordRoutine(NodeType.Destination, label, null, anchorId));
        }

        /// <summary>
        /// Manually links the currently active node to the nearest existing node.
        /// Skips nodes within 3 graph hops or the last 5 recorded nodes.
        /// </summary>
        public bool LinkToNearestJunction(out string connectedNodeId, out float distance)
        {
            connectedNodeId = null;
            distance = 0f;

            if (currentRecordedMap == null || lastRecordedNode == null || currentRecordedMap.nodes.Count < 2)
                return false;

            CampusNode nearest = null;
            float minDistance = float.MaxValue;

            // Skip last 5 nodes
            int skipCount = Math.Min(5, currentRecordedMap.nodes.Count);
            HashSet<string> excludedIds = new HashSet<string>();
            for (int i = 0; i < skipCount; i++)
            {
                excludedIds.Add(currentRecordedMap.nodes[currentRecordedMap.nodes.Count - 1 - i].id);
            }

            // Skip nodes within 3 graph hops
            HashSet<string> nearHops = GetNodesWithinHops(lastRecordedNode.id, 3);
            foreach (var id in nearHops) excludedIds.Add(id);

            foreach (var node in currentRecordedMap.nodes)
            {
                if (excludedIds.Contains(node.id)) continue;

                float dist = CampusMapData.CalculateNodeDistance(lastRecordedNode, node);

                if (dist < minDistance && dist <= manualJunctionLinkRadius)
                {
                    minDistance = dist;
                    nearest = node;
                }
            }

            if (nearest != null)
            {
                bool exists = currentRecordedMap.edges.Exists(e =>
                    (e.fromNodeId == lastRecordedNode.id && e.toNodeId == nearest.id) ||
                    (e.fromNodeId == nearest.id && e.toNodeId == lastRecordedNode.id)
                );

                if (!exists)
                {
                    float edgeDist = Mathf.Max(0.1f, minDistance);
                    CampusEdge edge = new CampusEdge
                    {
                        id = $"edge_{lastRecordedNode.id}_{nearest.id}",
                        fromNodeId = lastRecordedNode.id,
                        toNodeId = nearest.id,
                        distance = edgeDist,
                        isAccessible = true,
                        isStairs = false
                    };
                    currentRecordedMap.edges.Add(edge);
                    OnEdgeRecorded?.Invoke(edge);

                    connectedNodeId = nearest.id;
                    distance = edgeDist;
                    Debug.Log($"<color=yellow>[NodeRecorder] ✓ LINKED JUNCTION:</color> Connected {lastRecordedNode.id} to {nearest.id} ({edgeDist:F1}m)");
                    
                    // After manual link, continue from linked node
                    lastRecordedNode = nearest;
                    return true;
                }
            }

            return false;
        }

        private HashSet<string> GetNodesWithinHops(string startNodeId, int maxHops)
        {
            HashSet<string> visited = new HashSet<string> { startNodeId };
            List<string> currentLayer = new List<string> { startNodeId };

            for (int hop = 0; hop < maxHops; hop++)
            {
                List<string> nextLayer = new List<string>();
                foreach (var id in currentLayer)
                {
                    foreach (var edge in currentRecordedMap.edges)
                    {
                        string other = null;
                        if (edge.fromNodeId == id) other = edge.toNodeId;
                        else if (edge.toNodeId == id) other = edge.fromNodeId;

                        if (other != null && !visited.Contains(other))
                        {
                            visited.Add(other);
                            nextLayer.Add(other);
                        }
                    }
                }
                currentLayer = nextLayer;
                if (currentLayer.Count == 0) break;
            }
            return visited;
        }

        private IEnumerator SampleAndRecordRoutine(NodeType type, string label, string qrPayload, string linkToAnchorId)
        {
            IsSampling = true;
            SamplingProgress = 0f;
            OnSamplingProgressChanged?.Invoke(0f);

            List<double> latSamples = new List<double>();
            List<double> lonSamples = new List<double>();
            double lastTimestamp = -1;

            float sampleDuration = 25.0f; // 20-30s window
            float elapsed = 0f;
            while (elapsed < sampleDuration)
            {
                // Pause sampling if a dialog is open (if required by PLAN 6.2)
                if (IsDialogOpen)
                {
                    yield return new WaitForSeconds(0.5f);
                    continue;
                }

                if (AndroidSensorManager.Instance != null && AndroidSensorManager.Instance.IsGpsRunning)
                {
                    double ts = AndroidSensorManager.Instance.LastFixTimestamp;
                    if (ts != lastTimestamp) // Distinct fix check
                    {
                        latSamples.Add(AndroidSensorManager.Instance.CurrentLatitude);
                        lonSamples.Add(AndroidSensorManager.Instance.CurrentLongitude);
                        lastTimestamp = ts;
                    }
                }

                elapsed += 0.5f;
                SamplingProgress = Mathf.Clamp01(elapsed / sampleDuration);
                OnSamplingProgressChanged?.Invoke(SamplingProgress);
                yield return new WaitForSeconds(0.5f);
            }

            IsSampling = false;
            OnSamplingProgressChanged?.Invoke(1.0f);

            if (latSamples.Count < 10)
            {
                OnRecordingError?.Invoke("Insufficient GPS data. Need >= 10 distinct fixes.");
                yield break;
            }

            // Median Calculation
            latSamples.Sort();
            lonSamples.Sort();
            double finalLat = latSamples[latSamples.Count / 2];
            double finalLon = lonSamples[lonSamples.Count / 2];

            Vector3 derivedLocalPos = DeriveLocalPosition(finalLat, finalLon);

            CampusNode newNode = new CampusNode
            {
                id = NextNodeId(),
                label = string.IsNullOrEmpty(label) ? $"Node {currentRecordedMap.nodes.Count + 1}" : label,
                nodeType = type,
                latitude = finalLat,
                longitude = finalLon,
                altitude = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentAltitude : 0f,
                horizontalAccuracy = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.HorizontalAccuracy : 999f,
                sampleCount = latSamples.Count,
                recordedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                localPosition = derivedLocalPos,
                qrPayload = qrPayload,
                qrFacingEuler = arCameraTransform != null ? arCameraTransform.eulerAngles : Vector3.zero,
            };

            if (!string.IsNullOrEmpty(linkToAnchorId))
            {
                CampusNode anchor = currentRecordedMap.nodes.Find(n => n.id == linkToAnchorId);
                if (anchor == null)
                {
                    OnRecordingError?.Invoke("Anchor not found");
                    yield break;
                }

                currentRecordedMap.nodes.Add(newNode);
                
                float dx = newNode.localPosition.x - anchor.localPosition.x;
                float dz = newNode.localPosition.z - anchor.localPosition.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                
                CampusEdge edge = new CampusEdge {
                    id = $"edge_{newNode.id}_{linkToAnchorId}",
                    fromNodeId = newNode.id,
                    toNodeId = linkToAnchorId,
                    distance = Mathf.Max(0.1f, d),
                    isAccessible = true
                };
                currentRecordedMap.edges.Add(edge);
                OnEdgeRecorded?.Invoke(edge);
                OnNodeRecorded?.Invoke(newNode);
            }
            else
            {
                AddNodeToMap(newNode);
            }
        }

        private Vector3 DeriveLocalPosition(double lat, double lon)
        {
            if (currentRecordedMap != null && currentRecordedMap.originLatitude != 0)
            {
                return currentRecordedMap.GPSToLocal(lat, lon);
            }

            if (arCameraTransform != null && mapRoot != null)
            {
                return mapRoot.InverseTransformPoint(arCameraTransform.position);
            }

            return Vector3.zero;
        }

        private void AddNodeToMap(CampusNode newNode)
        {
            currentRecordedMap.nodes.Add(newNode);

            // Connect to previous breadcrumb node
            if (lastRecordedNode != null)
            {
                float dx = newNode.localPosition.x - lastRecordedNode.localPosition.x;
                float dz = newNode.localPosition.z - lastRecordedNode.localPosition.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);

                CampusEdge edge = new CampusEdge
                {
                    id = $"edge_{lastRecordedNode.id}_{newNode.id}",
                    fromNodeId = lastRecordedNode.id,
                    toNodeId = newNode.id,
                    distance = Mathf.Max(0.1f, dist),
                    isAccessible = true,
                    isStairs = false,
                };

                currentRecordedMap.edges.Add(edge);
                OnEdgeRecorded?.Invoke(edge);
            }

            lastRecordedNode = newNode;

            if (arCameraTransform != null)
                SpawnDebugMarker(arCameraTransform.position);

            Debug.Log(
                $"<color=cyan>[NodeRecorder] ✓ RECORDED NODE: </color> ID={newNode.id} | Type={newNode.nodeType} | Label='{newNode.label}'"
            );
            OnNodeRecorded?.Invoke(newNode);
        }

        /// <summary>
        /// Connects the last recorded node to any existing node by ID.
        /// </summary>
        public bool ConnectToExistingNode(string targetNodeId)
        {
            if (lastRecordedNode == null || currentRecordedMap == null)
                return false;

            CampusNode target = currentRecordedMap.nodes.Find(n => n.id == targetNodeId);
            if (target == null)
            {
                Debug.LogWarning($"[NodeRecorder] Target node {targetNodeId} not found in current map.");
                return false;
            }

            float dist = CampusMapData.CalculateNodeDistance(lastRecordedNode, target);
            if (dist < 0.1f) dist = 0.1f;

            CampusEdge branchEdge = new CampusEdge
            {
                id = $"edge_{lastRecordedNode.id}_{target.id}",
                fromNodeId = lastRecordedNode.id,
                toNodeId = target.id,
                distance = dist,
                isAccessible = true,
                isStairs = false,
            };

            currentRecordedMap.edges.Add(branchEdge);
            OnEdgeRecorded?.Invoke(branchEdge);
            Debug.Log($"[NodeRecorder] Connected {lastRecordedNode.id} to {target.id} (Distance: {dist:F2}m)");
            return true;
        }

        /// <summary>
        /// Finalizes and saves the recording.
        /// endNodeId: if provided, links the last recorded node to this existing node to finish the path.
        /// </summary>
        public bool StopAndSaveRecording(string endNodeId = null)
{
            if (!IsRecording)
                return false;

            if (!string.IsNullOrEmpty(endNodeId))
            {
                ConnectToExistingNode(endNodeId);
            }

            IsRecording = false;
            currentRecordedMap.savedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            OnRecordingStateChanged?.Invoke();

            if (MapDataManager.Instance == null)
            {
                Debug.LogError("[NodeRecorder] MapDataManager instance not found. Cannot save map.");
                return false;
            }

            bool success = MapDataManager.Instance.SaveMapToPersistentData(currentRecordedMap);
            if (success)
            {
                Debug.Log(
                    $"[NodeRecorder] Successfully finalized and saved map '{currentRecordedMap.campusName}' with {currentRecordedMap.nodes.Count} nodes."
                );
            }
            return success;
        }

        /// <summary>
        /// Splits an existing edge at the current location.
        /// </summary>
        public bool SplitNearestEdge()
        {
            if (!IsRecording || lastRecordedNode == null) return false;
            if (arCameraTransform == null || mapRoot == null) return false;

            Vector3 userLocalPos = mapRoot.InverseTransformPoint(arCameraTransform.position);
            
            CampusEdge bestEdge = null;
            float minPerpDist = float.MaxValue;
            Vector3 splitPoint = Vector3.zero;
            float bestT = 0f;

            foreach (var edge in currentRecordedMap.edges)
            {
                CampusNode a = currentRecordedMap.nodes.Find(n => n.id == edge.fromNodeId);
                CampusNode b = currentRecordedMap.nodes.Find(n => n.id == edge.toNodeId);
                if (a == null || b == null) continue;

                // Horizontal projection for chord logic
                Vector3 pA = new Vector3(a.localPosition.x, 0f, a.localPosition.z);
                Vector3 pB = new Vector3(b.localPosition.x, 0f, b.localPosition.z);
                Vector3 pUser = new Vector3(userLocalPos.x, 0f, userLocalPos.z);
                
                Vector3 line = pB - pA;
                float len = line.magnitude;
                if (len < 0.1f) continue;
                Vector3 dir = line / len;
                Vector3 v = pUser - pA;
                float t = Vector3.Dot(v, dir);
                t = Mathf.Clamp(t, 0, len);
                Vector3 proj = pA + dir * t;
                float d = Vector3.Distance(pUser, proj);

                if (d < minPerpDist && d < 5.0f) // Within 5m of edge
                {
                    minPerpDist = d;
                    bestEdge = edge;
                    bestT = t;
                    // Reconstruct 3D split point by lerping original localPositions
                    splitPoint = Vector3.Lerp(a.localPosition, b.localPosition, t / len);
                }
            }

            if (bestEdge != null)
            {
                CampusNode nodeA = currentRecordedMap.nodes.Find(n => n.id == bestEdge.fromNodeId);
                CampusNode nodeB = currentRecordedMap.nodes.Find(n => n.id == bestEdge.toNodeId);
                
                // Endpoint reuse per PLAN 5.8
                CampusNode targetNode = null;
                float segmentLen = Vector3.Distance(new Vector3(nodeA.localPosition.x, 0f, nodeA.localPosition.z), 
                                                   new Vector3(nodeB.localPosition.x, 0f, nodeB.localPosition.z));
                
                if (bestT < 3.0f) targetNode = nodeA;
                else if (segmentLen - bestT < 3.0f) targetNode = nodeB;

                if (targetNode != null)
                {
                    float dx = lastRecordedNode.localPosition.x - targetNode.localPosition.x;
                    float dz = lastRecordedNode.localPosition.z - targetNode.localPosition.z;
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                    
                    CampusEdge edge = new CampusEdge {
                        id = $"edge_{lastRecordedNode.id}_{targetNode.id}_{DateTime.Now.Ticks}",
                        fromNodeId = lastRecordedNode.id,
                        toNodeId = targetNode.id,
                        distance = Mathf.Max(0.1f, dist),
                        isAccessible = true
                    };
                    currentRecordedMap.edges.Add(edge);
                    lastRecordedNode = targetNode;
                    OnEdgeRecorded?.Invoke(edge);
                    Debug.Log($"[NodeRecorder] Reused endpoint {targetNode.id} instead of splitting.");
                    return true;
                }

                // Otherwise insert Intersection node
                CampusNode intersection = new CampusNode
                {
                    id = NextNodeId(),
                    label = "Intersection",
                    nodeType = NodeType.Intersection,
                    localPosition = splitPoint,
                    latitude = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentLatitude : 0,
                    longitude = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentLongitude : 0,
                    recordedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                currentRecordedMap.nodes.Add(intersection);

                // Create new edges with unique IDs
                string timestamp = DateTime.Now.Ticks.ToString();
                CampusEdge edgeAI = new CampusEdge {
                    id = $"edge_{nodeA.id}_{intersection.id}_{timestamp}",
                    fromNodeId = nodeA.id,
                    toNodeId = intersection.id,
                    distance = Vector3.Distance(new Vector3(nodeA.localPosition.x, 0, nodeA.localPosition.z), 
                                               new Vector3(splitPoint.x, 0, splitPoint.z)),
                    isAccessible = bestEdge.isAccessible,
                    isStairs = bestEdge.isStairs
                };
                CampusEdge edgeIB = new CampusEdge {
                    id = $"edge_{intersection.id}_{nodeB.id}_{timestamp}",
                    fromNodeId = intersection.id,
                    toNodeId = nodeB.id,
                    distance = Vector3.Distance(new Vector3(splitPoint.x, 0, splitPoint.z), 
                                               new Vector3(nodeB.localPosition.x, 0, nodeB.localPosition.z)),
                    isAccessible = bestEdge.isAccessible,
                    isStairs = bestEdge.isStairs
                };
                
                currentRecordedMap.edges.Remove(bestEdge);
                currentRecordedMap.edges.Add(edgeAI);
                currentRecordedMap.edges.Add(edgeIB);
                
                // Connect current recording to new junction
                float rdx = lastRecordedNode.localPosition.x - splitPoint.x;
                float rdz = lastRecordedNode.localPosition.z - splitPoint.z;
                CampusEdge recordingLink = new CampusEdge {
                    id = $"edge_{lastRecordedNode.id}_{intersection.id}_{timestamp}",
                    fromNodeId = lastRecordedNode.id,
                    toNodeId = intersection.id,
                    distance = Mathf.Max(0.1f, Mathf.Sqrt(rdx * rdx + rdz * rdz)),
                    isAccessible = true
                };
                currentRecordedMap.edges.Add(recordingLink);
                
                lastRecordedNode = intersection;
                OnNodeRecorded?.Invoke(intersection);
                OnEdgeRecorded?.Invoke(edgeAI);
                OnEdgeRecorded?.Invoke(edgeIB);
                OnEdgeRecorded?.Invoke(recordingLink);
                
                Debug.Log($"[NodeRecorder] Split edge {bestEdge.id} and joined path at new Intersection {intersection.id}");
                return true;
            }
            
            return false;
        }

        public void ClearRecordedMap()
        {
            if (currentRecordedMap != null)
            {
                currentRecordedMap.nodes.Clear();
                currentRecordedMap.edges.Clear();
            }
            lastRecordedNode = null;
            ClearDebugMarkers();

            if (MapDataManager.Instance != null && currentRecordedMap != null)
            {
                MapDataManager.Instance.SaveMapToPersistentData(currentRecordedMap);
            }

            Debug.Log("[NodeRecorder] Recorded map cleared.");
            OnRecordingStateChanged?.Invoke();
        }

        private void SpawnDebugMarker(Vector3 worldPos)
{
            if (nodeDebugMarkerPrefab == null)
                return;
            GameObject marker = Instantiate(nodeDebugMarkerPrefab, worldPos, Quaternion.identity);
            if (mapRoot != null) marker.transform.SetParent(mapRoot);
            spawnedDebugMarkers.Add(marker);
        }

        private void ClearDebugMarkers()
        {
            foreach (var marker in spawnedDebugMarkers)
            {
                if (marker != null)
                    Destroy(marker);
            }
            spawnedDebugMarkers.Clear();
        }
    }
}
