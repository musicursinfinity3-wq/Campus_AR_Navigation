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
        public float SamplingProgress { get; private set; } // 0.0 to 1.0
        public int RecordedNodeCount => currentRecordedMap?.nodes.Count ?? 0;
        public int RecordedEdgeCount => currentRecordedMap?.edges.Count ?? 0;
        public float DistanceToNextDrop { get; private set; }

        private CampusMapData currentRecordedMap;
        private CampusNode lastRecordedNode;
        private int nodeCounter = 0;
        private List<GameObject> spawnedDebugMarkers = new List<GameObject>();

        public event Action<CampusNode> OnNodeRecorded;
        public event Action<CampusEdge> OnEdgeRecorded;
        public event Action OnRecordingStateChanged;
        public event Action<float> OnSamplingProgressChanged;

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
        /// Starts a new recording session. Sets the origin anchor and map data.
        /// </summary>
        public void StartRecording(string mapName = "BSU_MainCampus_Map")
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

            // Append to existing map if present, otherwise initialize fresh
            bool isAppending =
                MapDataManager.Instance != null
                && MapDataManager.Instance.CurrentMap != null
                && MapDataManager.Instance.CurrentMap.nodes.Count > 0;

            // Align MapRoot with current camera pose only for fresh maps if not already relocalized
            if (mapRoot != null && arCameraTransform != null && !isAppending)
            {
                if (QRAnchorRelocalizer.Instance == null || !QRAnchorRelocalizer.Instance.IsRelocalized)
                {
                    mapRoot.position = arCameraTransform.position;
                    mapRoot.rotation = Quaternion.Euler(0f, arCameraTransform.eulerAngles.y, 0f);
                    Debug.Log("[NodeRecorder] No active relocalization. Initializing MapRoot at camera pose.");
                }
            }

            if (isAppending)
            {
                currentRecordedMap = MapDataManager.Instance.CurrentMap;
                nodeCounter = currentRecordedMap.nodes.Count;
                Debug.Log($"[NodeRecorder] Appending to existing map with {nodeCounter} nodes.");
            }
            else
            {
                currentRecordedMap = new CampusMapData
                {
                    campusName = mapName,
                    mapVersion = "1.0.0",
                };
                nodeCounter = 0;

                // Sample initial GPS coordinates as the campus origin
                if (
                    AndroidSensorManager.Instance != null
                    && AndroidSensorManager.Instance.IsGpsRunning
                )
                {
                    currentRecordedMap.originLatitude = AndroidSensorManager.Instance.CurrentLatitude;
                    currentRecordedMap.originLongitude = AndroidSensorManager.Instance.CurrentLongitude;
                    currentRecordedMap.originAltitude = AndroidSensorManager.Instance.CurrentAltitude;
                    Debug.Log(
                        $"[NodeRecorder] Set campus GPS Origin: Lat={currentRecordedMap.originLatitude:F6}, Lon={currentRecordedMap.originLongitude:F6}"
                    );
                }
            }

            lastRecordedNode = null;
            ClearDebugMarkers();

            IsRecording = true;
            Debug.Log("[NodeRecorder] Recording session started.");
            OnRecordingStateChanged?.Invoke();

            // If appending to an existing map and starting close to an existing node,
            // reuse that existing node directly instead of creating a redundant 'Start Point'
            if (isAppending)
            {
                CampusNode nearestExisting = null;
                float minDist = float.MaxValue;
                Vector3 currentLocalPos = mapRoot != null && arCameraTransform != null
                    ? mapRoot.InverseTransformPoint(arCameraTransform.position)
                    : (arCameraTransform != null ? arCameraTransform.position : Vector3.zero);

                foreach (var node in currentRecordedMap.nodes)
                {
                    float d = Vector3.Distance(currentLocalPos, node.localPosition);
                    if (d < minDist)
                    {
                        minDist = d;
                        nearestExisting = node;
                    }
                }

                // If within 3.5m of an existing node (e.g. Point B), resume directly from it!
                if (nearestExisting != null && minDist <= 3.5f)
                {
                    lastRecordedNode = nearestExisting;
                    Debug.Log($"<color=green>[NodeRecorder] ✓ RESUMING FROM EXISTING NODE:</color> '{nearestExisting.label}' ({nearestExisting.id}) at {minDist:F2}m away. No extra Start Point needed.");
                }
                else
                {
                    // If starting at a completely new unlinked junction, record a waypoint
                    RecordNode(NodeType.Waypoint, "Waypoint");
                }
            }
            else
            {
                // First session on a brand new map: record initial start waypoint
                RecordNode(NodeType.Waypoint, "Start Point");
            }
        }

        private void Update()
        {
            if (!IsRecording || lastRecordedNode == null || IsSampling)
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

            nodeCounter++;

            double lat = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentLatitude : 0.0;
            double lon = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentLongitude : 0.0;
            float alt = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentAltitude : 0f;
            float acc = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.HorizontalAccuracy : 999f;

            Vector3 derivedLocalPos = DeriveLocalPosition(lat, lon);

            CampusNode newNode = new CampusNode
            {
                id = $"node_{nodeCounter:D4}",
                label = string.IsNullOrEmpty(label) ? $"Node {nodeCounter}" : label,
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

        /// <summary>
        /// Specifically records a QR Anchor node using an actively tracked image's pose.
        /// Heading and position are stored in MapRoot-local space.
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
                foreach (var img in imageManager.trackables)
                {
                    if (img.trackingState == UnityEngine.XR.ARSubsystems.TrackingState.Tracking)
                    {
                        if (string.IsNullOrEmpty(qrPayload) || img.referenceImage.name == qrPayload)
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
                // Invert camera forward because a marker's outward normal points toward the viewer!
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
                    Debug.LogWarning($"<color=yellow>[NodeRecorder] Editor Simulated QR Anchor recorded:</color> Payload='{qrPayload}', Facing outward toward camera (heading={localHeading:F1}°)");
                }
                else
                {
                    Debug.LogError("[NodeRecorder] Cannot simulate QR Anchor: Camera transform is null.");
                    return null;
                }
#else
                // On physical devices: STRICT REQUIREMENT. No camera-forward fallback allowed.
                Debug.LogError("[NodeRecorder] FAILED TO RECORD QR ANCHOR: No actively tracked image (TrackingState.Tracking) found in camera view. " +
                               "You must aim the camera directly at the physical QR marker with stable tracking before recording.");
                return null;
#endif
            }

            nodeCounter++;
            CampusNode newNode = new CampusNode
            {
                id = $"node_{nodeCounter:D4}",
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

            AddNodeToMap(newNode);
            Debug.Log($"<color=cyan>[NodeRecorder] ✓ RECORDED QR ANCHOR (v{newNode.anchorVersion}):</color> ID={newNode.id} | Payload='{newNode.qrPayload}' | LocalPos={newNode.localPosition} | LocalHeading={localHeading:F1}°");
            return newNode;
        }

        /// <summary>
        /// Marks a destination (Office, Facility) by averaging GPS samples over a multi-second window.
        /// </summary>
        public void MarkDestination(string destinationName)
        {
            if (!IsRecording) return;
            StartCoroutine(SampleAndRecordRoutine(NodeType.Destination, destinationName, null, null));
        }

        /// <summary>
        /// Marks a physical QR anchor point with multi-sample averaging.
        /// </summary>
        public void MarkQRAnchor(string qrCodeText, string label = null)
        {
            if (!IsRecording) return;
            StartCoroutine(SampleAndRecordRoutine(NodeType.QRAnchor, label ?? $"QR_{qrCodeText}", qrCodeText, null));
        }

        /// <summary>
        /// Manually links the currently active node to the nearest existing node within manualJunctionLinkRadius.
        /// </summary>
        public bool LinkToNearestJunction(out string connectedNodeId, out float distance)
        {
            connectedNodeId = null;
            distance = 0f;

            if (currentRecordedMap == null || lastRecordedNode == null || currentRecordedMap.nodes.Count < 2)
                return false;

            CampusNode nearest = null;
            float minDistance = float.MaxValue;

            foreach (var node in currentRecordedMap.nodes)
            {
                if (node.id == lastRecordedNode.id) continue;

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
                    return true;
                }
            }

            return false;
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

        private IEnumerator SampleAndRecordRoutine(NodeType type, string label, string qrPayload, Action<CampusNode> onComplete)
        {
            IsSampling = true;
            SamplingProgress = 0f;
            OnSamplingProgressChanged?.Invoke(0f);

            List<double> latSamples = new List<double>();
            List<double> lonSamples = new List<double>();
            List<float> accSamples = new List<float>();

            float elapsed = 0f;
            while (elapsed < sampleDurationSeconds)
            {
                if (AndroidSensorManager.Instance != null && AndroidSensorManager.Instance.IsGpsRunning)
                {
                    float currentAcc = AndroidSensorManager.Instance.HorizontalAccuracy;
                    if (currentAcc <= maxAllowedAccuracyMeters * 1.5f)
                    {
                        latSamples.Add(AndroidSensorManager.Instance.CurrentLatitude);
                        lonSamples.Add(AndroidSensorManager.Instance.CurrentLongitude);
                        accSamples.Add(currentAcc);
                    }
                }

                elapsed += sampleIntervalSeconds;
                SamplingProgress = Mathf.Clamp01(elapsed / sampleDurationSeconds);
                OnSamplingProgressChanged?.Invoke(SamplingProgress);
                yield return new WaitForSeconds(sampleIntervalSeconds);
            }

            IsSampling = false;
            OnSamplingProgressChanged?.Invoke(1.0f);

            double finalLat, finalLon;
            float finalAcc;

            if (latSamples.Count > 0)
            {
                double sumLat = 0;
                double sumLon = 0;
                float sumAcc = 0;
                for (int i = 0; i < latSamples.Count; i++)
                {
                    sumLat += latSamples[i];
                    sumLon += lonSamples[i];
                    sumAcc += accSamples[i];
                }
                finalLat = sumLat / latSamples.Count;
                finalLon = sumLon / latSamples.Count;
                finalAcc = sumAcc / latSamples.Count;
            }
            else
            {
                finalLat = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentLatitude : 0;
                finalLon = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentLongitude : 0;
                finalAcc = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.HorizontalAccuracy : 999f;
            }

            nodeCounter++;
            Vector3 derivedLocalPos = DeriveLocalPosition(finalLat, finalLon);

            CampusNode newNode = new CampusNode
            {
                id = $"node_{nodeCounter:D4}",
                label = string.IsNullOrEmpty(label) ? $"Node {nodeCounter}" : label,
                nodeType = type,
                latitude = finalLat,
                longitude = finalLon,
                altitude = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.CurrentAltitude : 0f,
                horizontalAccuracy = finalAcc,
                sampleCount = latSamples.Count,
                recordedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                localPosition = derivedLocalPos,
                qrPayload = qrPayload,
                qrFacingEuler = arCameraTransform != null ? arCameraTransform.eulerAngles : Vector3.zero,
            };

            AddNodeToMap(newNode);
            onComplete?.Invoke(newNode);
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
                float dist = CampusMapData.CalculateNodeDistance(lastRecordedNode, newNode);
                if (dist < 0.1f)
                {
                    dist = 0.1f;
                }

                CampusEdge edge = new CampusEdge
                {
                    id = $"edge_{lastRecordedNode.id}_{newNode.id}",
                    fromNodeId = lastRecordedNode.id,
                    toNodeId = newNode.id,
                    distance = dist,
                    isAccessible = true,
                    isStairs = false,
                };

                currentRecordedMap.edges.Add(edge);
                OnEdgeRecorded?.Invoke(edge);
            }

            // Optional auto-connect if enabled
            if (autoConnectIntersections)
            {
                foreach (var existing in currentRecordedMap.nodes)
                {
                    if (existing.id == newNode.id || (lastRecordedNode != null && existing.id == lastRecordedNode.id))
                        continue;

                    float dist = CampusMapData.CalculateNodeDistance(newNode, existing);

                    if (dist <= intersectionConnectRadius)
                    {
                        bool exists = currentRecordedMap.edges.Exists(e =>
                            (e.fromNodeId == newNode.id && e.toNodeId == existing.id) ||
                            (e.fromNodeId == existing.id && e.toNodeId == newNode.id)
                        );

                        if (!exists)
                        {
                            CampusEdge junctionEdge = new CampusEdge
                            {
                                id = $"edge_{newNode.id}_{existing.id}",
                                fromNodeId = newNode.id,
                                toNodeId = existing.id,
                                distance = Mathf.Max(0.1f, dist),
                                isAccessible = true,
                                isStairs = false,
                            };
                            currentRecordedMap.edges.Add(junctionEdge);
                            OnEdgeRecorded?.Invoke(junctionEdge);
                        }
                    }
                }
            }

            lastRecordedNode = newNode;

            if (arCameraTransform != null)
                SpawnDebugMarker(arCameraTransform.position);

            Debug.Log(
                $"<color=cyan>[NodeRecorder] ✓ RECORDED NODE #{nodeCounter}:</color> ID={newNode.id} | Type={newNode.nodeType} | Label='{newNode.label}' | Lat={newNode.latitude:F6}, Lon={newNode.longitude:F6} | Acc={newNode.horizontalAccuracy:F1}m"
            );
            OnNodeRecorded?.Invoke(newNode);
        }

        public bool StopAndSaveRecording()
        {
            if (!IsRecording)
                return false;

            IsRecording = false;
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

        public void ClearRecordedMap()
        {
            if (currentRecordedMap != null)
            {
                currentRecordedMap.nodes.Clear();
                currentRecordedMap.edges.Clear();
            }
            lastRecordedNode = null;
            nodeCounter = 0;
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
