using System;
using System.Collections;
using System.Collections.Generic;
using CampusNavigation.Data;
using CampusNavigation.Navigation;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CampusNavigation.Localization
{
    public class QRAnchorRelocalizer : MonoBehaviour
    {
        public static QRAnchorRelocalizer Instance { get; private set; }

        [Header("Scene References")]
        [Tooltip("The parent container transform for all navigation arrows and nodes.")]
        [SerializeField]
        private Transform mapRoot;

        [Tooltip("ARTrackedImageManager on XR Origin. If null, will search automatically.")]
        [SerializeField]
        private ARTrackedImageManager imageManager;

        [Tooltip("ARCamera transform. If null, Camera.main will be used.")]
        [SerializeField]
        private Transform arCameraTransform;

        [Header("Quality Gating")]
        [Tooltip("Minimum tracking confidence required to evaluate marker.")]
        [SerializeField]
        private TrackingState requiredTrackingState = TrackingState.Tracking;

        [Tooltip("Maximum allowed distance between camera and marker in meters.")]
        [SerializeField]
        private float maxTrackingDistance = 1.5f;

        [Tooltip(
            "Maximum viewing angle in degrees between marker outward normal and marker-to-camera vector."
        )]
        [SerializeField]
        private float maxViewingAngleDegrees = 60.0f;

        [Header("Relocalization Logic")]
        [Tooltip("Cooldown period in seconds before the same anchor can be re-scanned.")]
        [SerializeField]
        private float perAnchorCooldown = 12.0f;

        [Header("Multi-Frame Accumulation")]
[Tooltip("Time in seconds to accumulate stable frames before computing final pose.")]
        [SerializeField]
        private float sampleWindowSeconds = 0.75f;

        [Tooltip("Minimum sample count required within sample window.")]
        [SerializeField]
        private int minSampleCount = 8;

        [Tooltip(
            "Maximum allowed circular standard deviation / spread in degrees for heading samples."
        )]
        [SerializeField]
        private float maxAllowedHeadingSpreadDegrees = 15.0f;

        [Header("Smoothing & Transition")]
        [Tooltip("Duration of smooth slerp/lerp transition on subsequent re-scans (seconds).")]
        [SerializeField]
        private float smoothLerpDuration = 0.5f;

        [Header("Re-Scan Discrepancy Gate")]
        [Tooltip(
            "Maximum allowable horizontal translation discrepancy between map's anchor pose and detected physical anchor."
        )]
        [SerializeField]
        private float maxAllowedPosDiscrepancy = 10.0f;

        [Tooltip("Maximum allowable yaw discrepancy in degrees.")]
        [SerializeField]
        private float maxAllowedYawDiscrepancy = 30.0f;

        [Header("Debug & Telemetry")]
        [SerializeField]
        private bool showDebugOverlay = true;

        [SerializeField]
        private bool verboseLogging = true;

        [Range(1.0f, 3.0f)]
        [SerializeField]
        private float debugOverlayScale = 1.8f;

        public bool IsRelocalized { get; private set; }
        public string ActiveAnchorId { get; private set; }
        public bool HasPendingCorrection { get; private set; }
        public PendingRelocalization PendingCorrection { get; private set; }

        public const string DEFAULT_SCAN_HINT = "Hold the phone about an arm's length from the sign.";
        public string ScanHint { get; private set; } = DEFAULT_SCAN_HINT;
        public event Action<string> OnScanHintChanged;

        public event Action<string> OnRelocalizedToAnchor;
        public event Action<PendingRelocalization> OnSuspiciousRelocalizationDetected;
        public event Action OnPendingRelocalizationResolved;

        // Multi-frame Accumulator State
        private class SampleBuffer
        {
            public string markerName;
            public CampusNode anchorNode;
            public List<Vector3> positions = new List<Vector3>();
            public List<float> headings = new List<float>();
            public float firstSampleTime;
            public float lastSampleTime;
        }

        private SampleBuffer currentBuffer = null;
        private Coroutine activeLerpCoroutine = null;
        private ARTrackedImage currentObservedImage = null;
        
        private Dictionary<string, float> lastRelocalizedTimestamps = new Dictionary<string, float>();
        private ARSessionState previousARSessionState = ARSessionState.None;

        // Telemetry state for debug UI
private string debugMarkerName = "None";
        private float debugDistance = 0f;
        private float debugViewingAngle = 0f;
        private string debugTrackingState = "None";
        private float debugHeading = 0f;
        private Vector3 debugLastPosCorrection = Vector3.zero;
        private float debugLastYawCorrection = 0f;
        private float debugAccumProgress = 0f;
        private string debugStatusMessage = "Waiting for marker";

        public struct PendingRelocalization
        {
            public CampusNode anchorNode;
            public Vector3 targetPosition;
            public Quaternion targetRotation;
            public float horizontalDistanceDiscrepancy;
            public float yawDiscrepancyDegrees;
        }

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
            if (imageManager == null)
                imageManager = FindFirstObjectByType<ARTrackedImageManager>();

            if (mapRoot == null)
            {
                GameObject rootGo = GameObject.Find("MapRoot");
                if (rootGo != null)
                    mapRoot = rootGo.transform;
            }

            if (arCameraTransform == null && Camera.main != null)
                arCameraTransform = Camera.main.transform;

            if (imageManager != null)
            {
                imageManager.trackablesChanged.AddListener(OnTrackablesChanged);
            }
            else
            {
                Debug.LogWarning(
                    "[QRAnchorRelocalizer] No ARTrackedImageManager found. Add one to XR Origin."
                );
            }

            // Subscribe to ARSession state changes to reset relocalization on session reset / loss
            ARSession.stateChanged += OnARSessionStateChanged;

            // Startup Image Library Validation
            ValidateLibraryAndMap();

            if (MapDataManager.Instance != null)
            {
                MapDataManager.Instance.OnMapLoaded += HandleMapLoaded;
            }
        }

        private void HandleMapLoaded(CampusMapData _)
        {
            ValidateLibraryAndMap();
        }

        private void OnDestroy()
        {
            if (imageManager != null)
            {
                imageManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
            }
            ARSession.stateChanged -= OnARSessionStateChanged;
            
            if (MapDataManager.Instance != null)
            {
                MapDataManager.Instance.OnMapLoaded -= HandleMapLoaded;
            }
        }

        private void OnARSessionStateChanged(ARSessionStateChangedEventArgs args)
        {
            if (verboseLogging)
            {
                Debug.Log($"[QRAnchorRelocalizer] ARSession state: {args.state} (previous: {previousARSessionState})");
            }

            // Reset logic specifically for SessionTracking -> SessionInitializing (e.g. app background/resume reset)
            if (previousARSessionState == ARSessionState.SessionTracking && args.state == ARSessionState.SessionInitializing)
            {
                PerformFullSessionReset("ARSession state transition SessionTracking -> SessionInitializing");
            }

            if (
                args.state == ARSessionState.None
                || args.state == ARSessionState.Unsupported
                || args.state == ARSessionState.NeedsInstall
                || args.state == ARSessionState.CheckingAvailability
            )
            {
                ResetRelocalization();
            }

            previousARSessionState = args.state;
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            // On resume (pauseStatus == false), provide a fallback reset if session re-initialized
            if (!pauseStatus)
            {
                PerformFullSessionReset("Application resumed from background");
            }
        }

        private void PerformFullSessionReset(string reason)
        {
            if (verboseLogging)
            {
                Debug.Log($"[QRAnchorRelocalizer] Performing full session reset: {reason}");
            }
            ResetRelocalization();
            GpsRelocalizer.Instance?.ResetRelocalizationState();
            NavigationSessionManager.Instance?.StopNavigation();
            lastRelocalizedTimestamps.Clear();
        }

        private void SetScanHint(string hint)
        {
            if (ScanHint != hint)
            {
                ScanHint = hint;
                OnScanHintChanged?.Invoke(ScanHint);
            }
        }

        private void Update()
        {
            // Scan guidance message when no marker is currently being tracked
            if (currentObservedImage == null)
            {
                SetScanHint(DEFAULT_SCAN_HINT);
                debugStatusMessage = DEFAULT_SCAN_HINT;
            }

            // Skip candidate sampling while HasPendingCorrection is true (a blocked re-scan must not re-fire every 0.75 s)
            if (HasPendingCorrection)
            {
                return;
            }

            // Sample all tracked images and find the best candidate per frame
            ProcessBestImageCandidate();

            if (currentObservedImage != null && currentBuffer != null)
            {
                // Timeout check: if samples haven't arrived within 0.4s, reset accumulator
                if (Time.time - currentBuffer.lastSampleTime > 0.4f)
                {
                    ResetAccumulator("Tracking sample timeout");
                    return;
                }

                float elapsed = Time.time - currentBuffer.firstSampleTime;
                debugAccumProgress = Mathf.Clamp01(elapsed / sampleWindowSeconds);

                if (elapsed >= sampleWindowSeconds)
                {
                    EvaluateAccumulatedBuffer();
                }
            }
        }

        private void ProcessBestImageCandidate()
        {
            if (imageManager == null || arCameraTransform == null) return;

            ARTrackedImage bestImage = null;
            float minDistance = float.MaxValue;

            foreach (var img in imageManager.trackables)
            {
                // Gate check for every tracking image
                if (img.trackingState != requiredTrackingState) continue;

                float dist = Vector3.Distance(arCameraTransform.position, img.transform.position);
                if (dist > maxTrackingDistance || dist < 0.1f) continue;

                // Viewing angle check
                Vector3 outwardNormal = img.transform.up;
                Vector3 markerToCam = (arCameraTransform.position - img.transform.position).normalized;
                if (Vector3.Angle(outwardNormal, markerToCam) > maxViewingAngleDegrees) continue;

                // Cooldown check (key is marker payload or trackable name)
                string cooldownKey = img.referenceImage.name;
                if (lastRelocalizedTimestamps.TryGetValue(cooldownKey, out float lastTime))
                {
                    if (Time.time < lastTime + perAnchorCooldown) continue;
                }

                // Best candidate selection with hysteresis (keep current unless another is 0.5m closer)
                float selectionDist = dist;
                if (currentObservedImage != null && img.trackableId == currentObservedImage.trackableId)
                {
                    selectionDist -= 0.5f; // Hysteresis bonus for current target
                }

                if (selectionDist < minDistance)
                {
                    minDistance = selectionDist;
                    bestImage = img;
                }
            }

            // Handle target changes
            if (bestImage != null)
            {
                if (currentObservedImage == null || currentObservedImage.trackableId != bestImage.trackableId)
                {
                    // Switched to a new best candidate or started tracking
                    ResetAccumulator($"Switched to best candidate: {bestImage.referenceImage.name}");
                    ProcessTrackedImageSample(bestImage);
                }
                else
                {
                    // Continue sampling existing best candidate
                    ProcessTrackedImageSample(bestImage);
                }
            }
            else if (currentObservedImage != null)
            {
                // Lost all valid candidates
                ResetAccumulator("No valid candidates in view");
            }
        }

        private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> eventArgs)
        {
            // Per-frame Update processing replaces added/updated triggers to avoid double-sampling.
            
            foreach (var img in eventArgs.removed)
            {
                if (currentObservedImage != null && currentObservedImage.trackableId == img.Key)
                {
                    ResetAccumulator("Tracked image removed from session");
                }
            }
        }

        /// <summary>
        /// Public entrypoint used by simulation / testing tools to align MapRoot directly.
        /// </summary>
        public void RelocalizeMapRoot(Transform physicalAnchor, CampusNode recordedNode)
        {
            if (physicalAnchor == null || recordedNode == null || mapRoot == null)
                return;

            float physicalHeading = CampusMapData.ComputeMarkerHeading(physicalAnchor, null, out _);
            ApplyVerifiedRelocalization(physicalAnchor.position, physicalHeading, recordedNode);
        }

        public void SetMapRootForTesting(Transform root)
        {
            mapRoot = root;
        }

        private void ProcessTrackedImageSample(ARTrackedImage trackedImage)
        {
            if (arCameraTransform == null && Camera.main != null)
                arCameraTransform = Camera.main.transform;

            string markerName = trackedImage.referenceImage.name;
            debugMarkerName = markerName;
            debugTrackingState = trackedImage.trackingState.ToString();

            // 1. Tracking State Gate
            if (trackedImage.trackingState != requiredTrackingState)
            {
                if (currentObservedImage == trackedImage)
                    ResetAccumulator($"Tracking state degraded to {trackedImage.trackingState}");
                return;
            }

            // 2. Distance Gate
            float distance =
                arCameraTransform != null
                    ? Vector3.Distance(arCameraTransform.position, trackedImage.transform.position)
                    : 0f;
            debugDistance = distance;

            if (distance > maxTrackingDistance || distance < 0.1f)
            {
                if (currentObservedImage == trackedImage)
                    ResetAccumulator(
                        $"Marker distance ({distance:F2}m) exceeded limit ({maxTrackingDistance}m)"
                    );
                return;
            }

            // 3. Viewing Angle Gate: marker-to-camera direction vs outward normal
            Vector3 outwardNormal = trackedImage.transform.up; // In ARTrackedImage, up points OUT of marker
            Vector3 markerToCam =
                arCameraTransform != null
                    ? (arCameraTransform.position - trackedImage.transform.position).normalized
                    : Vector3.forward;

            float angleToCam = Vector3.Angle(outwardNormal, markerToCam);
            debugViewingAngle = angleToCam;

            if (angleToCam > maxViewingAngleDegrees)
            {
                if (currentObservedImage == trackedImage)
                    ResetAccumulator(
                        $"Viewing incidence angle ({angleToCam:F1}°) exceeded limit ({maxViewingAngleDegrees}°)"
                    );
                return;
            }

            // 4. Node Lookup & Anchor Version Check
            CampusNode anchorNode = FindAnchorNode(markerName);
            if (anchorNode == null)
            {
                debugStatusMessage = $"Marker '{markerName}' has no matching QRAnchor node in map.";
                return;
            }

            if (anchorNode.anchorVersion < CampusMapData.CURRENT_ANCHOR_VERSION)
            {
                debugStatusMessage =
                    $"Anchor '{anchorNode.label}' uses legacy v{anchorNode.anchorVersion} convention. Re-record required!";
                return;
            }

            // 5. Accumulate sample
            float worldHeading = CampusMapData.ComputeMarkerHeading(
                trackedImage.transform,
                null,
                out bool isFlat
            );
            debugHeading = worldHeading;

            if (isFlat)
            {
                Debug.LogWarning(
                    $"[QRAnchorRelocalizer] Tracked image '{markerName}' normal is near vertical (flat marker)."
                );
            }

            if (currentBuffer == null || currentBuffer.markerName != markerName)
            {
                currentBuffer = new SampleBuffer
                {
                    markerName = markerName,
                    anchorNode = anchorNode,
                    firstSampleTime = Time.time,
                    lastSampleTime = Time.time,
                };
                currentObservedImage = trackedImage;
            }

            currentBuffer.positions.Add(trackedImage.transform.position);
            currentBuffer.headings.Add(worldHeading);
            currentBuffer.lastSampleTime = Time.time;
        }

        private void EvaluateAccumulatedBuffer()
        {
            if (currentBuffer == null)
                return;

            if (currentBuffer.positions.Count < minSampleCount)
            {
                ResetAccumulator(
                    $"Insufficient samples ({currentBuffer.positions.Count}/{minSampleCount})"
                );
                return;
            }

            // Compute average position
            Vector3 avgPos = Vector3.zero;
            foreach (var p in currentBuffer.positions)
                avgPos += p;
            avgPos /= currentBuffer.positions.Count;

            // Compute circular mean and circular variance of headings
            float sumSin = 0f;
            float sumCos = 0f;
            foreach (var h in currentBuffer.headings)
            {
                float rad = h * Mathf.Deg2Rad;
                sumSin += Mathf.Sin(rad);
                sumCos += Mathf.Cos(rad);
            }

            int n = currentBuffer.headings.Count;
            float r = Mathf.Sqrt(sumSin * sumSin + sumCos * sumCos) / n;
            float meanHeading = Mathf.Atan2(sumSin, sumCos) * Mathf.Rad2Deg;

            // Circular spread approximation: sqrt(2 * (1 - R)) in radians converted to degrees
            float circularSpreadDegrees = Mathf.Sqrt(Mathf.Max(0f, 2f * (1f - r))) * Mathf.Rad2Deg;

            if (circularSpreadDegrees > maxAllowedHeadingSpreadDegrees)
            {
                ResetAccumulator(
                    $"Heading variance too high ({circularSpreadDegrees:F1}° > {maxAllowedHeadingSpreadDegrees}°)"
                );
                return;
            }

            // Pose candidate is verified!
            CampusNode node = currentBuffer.anchorNode;
            ApplyVerifiedRelocalization(avgPos, meanHeading, node);

            // Buffer successfully processed
            currentBuffer = null;
            currentObservedImage = null;
            debugAccumProgress = 0f;
        }

        private void ApplyVerifiedRelocalization(
            Vector3 physicalMarkerWorldPos,
            float physicalMarkerWorldHeading,
            CampusNode recordedNode
        )
        {
            if (mapRoot == null)
            {
                Debug.LogError(
                    "[QRAnchorRelocalizer] MapRoot is null. Cannot apply relocalization."
                );
                return;
            }

            // Heading math: deltaYaw is the rotation around Vector3.up required so recordedNode's local heading matches physical world heading
            // physicalHeading = mapRootYaw + recordedLocalHeading => targetMapRootYaw = physicalHeading - recordedLocalHeading
            float recordedLocalHeading = recordedNode.qrFacingEuler.y;
            float targetMapRootYaw = Mathf.DeltaAngle(
                recordedLocalHeading,
                physicalMarkerWorldHeading
            );
            Quaternion targetRotation = Quaternion.Euler(0f, targetMapRootYaw, 0f);

            // Translation math:
            // physicalMarkerWorldPos = targetMapRootPosition + (targetRotation * recordedNode.localPosition)
            // => targetMapRootPosition = physicalMarkerWorldPos - (targetRotation * recordedNode.localPosition)
            Vector3 targetPosition =
                physicalMarkerWorldPos - (targetRotation * recordedNode.localPosition);

            // Discrepancy Gate (Only on subsequent scans when already relocalized)
            if (IsRelocalized)
            {
                // Discrepancy metric: compare where MapRoot currently places this anchor vs where it was physically detected
                Vector3 currentMappedAnchorPos = mapRoot.TransformPoint(recordedNode.localPosition);
                Vector3 horizontalDelta = Vector3.ProjectOnPlane(
                    currentMappedAnchorPos - physicalMarkerWorldPos,
                    Vector3.up
                );
                float horizontalDistDiscrepancy = horizontalDelta.magnitude;

                float currentMapRootYaw = mapRoot.eulerAngles.y;
                float yawDiscrepancy = Mathf.Abs(
                    Mathf.DeltaAngle(currentMapRootYaw, targetMapRootYaw)
                );

                if (
                    horizontalDistDiscrepancy > maxAllowedPosDiscrepancy
                    || yawDiscrepancy > maxAllowedYawDiscrepancy
                )
                {
                    Debug.LogWarning(
                        $"<color=yellow>[QRAnchorRelocalizer] SUSPICIOUS RE-SCAN BLOCKED:</color> Anchor '{recordedNode.label}'\n"
                            + $"Horizontal error: {horizontalDistDiscrepancy:F2}m (Limit: {maxAllowedPosDiscrepancy}m)\n"
                            + $"Yaw error: {yawDiscrepancy:F1}° (Limit: {maxAllowedYawDiscrepancy}°)\n"
                            + $"Map teleportation prevented. Waiting for confirmation or re-scan."
                    );

                    HasPendingCorrection = true;
                    PendingCorrection = new PendingRelocalization
                    {
                        anchorNode = recordedNode,
                        targetPosition = targetPosition,
                        targetRotation = targetRotation,
                        horizontalDistanceDiscrepancy = horizontalDistDiscrepancy,
                        yawDiscrepancyDegrees = yawDiscrepancy,
                    };

                    OnSuspiciousRelocalizationDetected?.Invoke(PendingCorrection);
                    debugStatusMessage =
                        $"Re-scan blocked: err={horizontalDistDiscrepancy:F1}m, {yawDiscrepancy:F1}°. Confirmation required.";
                    return;
                }
            }

            // Apply correction
            ExecuteAlignment(
                targetPosition,
                targetRotation,
                recordedNode,
                isSmoothTransition: IsRelocalized
            );
        }

        private void ExecuteAlignment(
            Vector3 targetPos,
            Quaternion targetRot,
            CampusNode recordedNode,
            bool isSmoothTransition
        )
        {
            // Cancel any ongoing smooth interpolation before starting a new one
            if (activeLerpCoroutine != null)
            {
                StopCoroutine(activeLerpCoroutine);
                activeLerpCoroutine = null;
            }

            debugLastPosCorrection = targetPos - mapRoot.position;
            debugLastYawCorrection = Mathf.DeltaAngle(
                mapRoot.eulerAngles.y,
                targetRot.eulerAngles.y
            );

            if (!isSmoothTransition || smoothLerpDuration <= 0.05f)
            {
                // Instant snap (first scan of session or smooth disabled)
                mapRoot.position = targetPos;
                mapRoot.rotation = targetRot;
                FinalizeAlignment(recordedNode);
            }
            else
            {
                // Smooth interpolation for re-scans to prevent jarring visual jump
                activeLerpCoroutine = StartCoroutine(
                    SmoothAlignmentCoroutine(targetPos, targetRot, recordedNode)
                );
            }
        }

        private IEnumerator SmoothAlignmentCoroutine(
            Vector3 targetPos,
            Quaternion targetRot,
            CampusNode recordedNode
        )
        {
            Vector3 startPos = mapRoot.position;
            Quaternion startRot = mapRoot.rotation;
            float elapsed = 0f;

            while (elapsed < smoothLerpDuration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.SmoothStep(0f, 1f, elapsed / smoothLerpDuration);
                mapRoot.position = Vector3.Lerp(startPos, targetPos, t);
                mapRoot.rotation = Quaternion.Slerp(startRot, targetRot, t);
                yield return null;
            }

            mapRoot.position = targetPos;
            mapRoot.rotation = targetRot;
            activeLerpCoroutine = null;
            FinalizeAlignment(recordedNode);
        }

        private void FinalizeAlignment(CampusNode recordedNode)
        {
            bool isNewAnchor = !IsRelocalized || ActiveAnchorId != recordedNode.id;
            
            IsRelocalized = true;
            ActiveAnchorId = recordedNode.id;
            HasPendingCorrection = false;
            
            // Set cooldown for this anchor (fallback to node.id if qrPayload is null/empty e.g. debug simulated scans)
            string cooldownKey = string.IsNullOrEmpty(recordedNode.qrPayload) ? recordedNode.id : recordedNode.qrPayload;
            lastRelocalizedTimestamps[cooldownKey] = Time.time;

            // Synchronize with GpsRelocalizer
            GpsRelocalizer.Instance?.ClearInMotionTarget();

            string hint = $"Aligned to '{recordedNode.label}'";
            SetScanHint(hint);
            debugStatusMessage = hint;
            Debug.Log(
                $"<color=green>[QRAnchorRelocalizer] SUCCESS:</color> Map aligned to '{recordedNode.label}' ({recordedNode.id}). MapRoot Pos={mapRoot.position}, Yaw={mapRoot.eulerAngles.y:F1}°"
            );
            
            if (isNewAnchor)
            {
                OnRelocalizedToAnchor?.Invoke(recordedNode.id);
            }
            
            OnPendingRelocalizationResolved?.Invoke();
        }

        /// <summary>
        /// Manually confirm and execute a suspicious re-scan that was gated.
        /// </summary>
        public void ConfirmPendingRelocalization()
        {
            if (!HasPendingCorrection)
                return;

            Debug.Log(
                $"[QRAnchorRelocalizer] User confirmed pending re-scan correction for '{PendingCorrection.anchorNode.label}'."
            );
            ExecuteAlignment(
                PendingCorrection.targetPosition,
                PendingCorrection.targetRotation,
                PendingCorrection.anchorNode,
                isSmoothTransition: true
            );
        }

        /// <summary>
        /// Dismiss a suspicious re-scan and retain current map alignment.
        /// </summary>
        public void DismissPendingRelocalization()
        {
            if (HasPendingCorrection && PendingCorrection.anchorNode != null)
            {
                // Set cooldown even on dismissal to prevent immediate re-triggering of the same suspicious scan
                string cooldownKey = string.IsNullOrEmpty(PendingCorrection.anchorNode.qrPayload)
                    ? PendingCorrection.anchorNode.id
                    : PendingCorrection.anchorNode.qrPayload;
                lastRelocalizedTimestamps[cooldownKey] = Time.time;
            }

            HasPendingCorrection = false;
            string hint = "Pending correction dismissed by user.";
            SetScanHint(hint);
            debugStatusMessage = hint;
            Debug.Log("[QRAnchorRelocalizer] Pending re-scan dismissed.");
            OnPendingRelocalizationResolved?.Invoke();
        }

        public void ResetRelocalization()
        {
            if (activeLerpCoroutine != null)
            {
                StopCoroutine(activeLerpCoroutine);
                activeLerpCoroutine = null;
            }
            IsRelocalized = false;
            ActiveAnchorId = null;
            HasPendingCorrection = false;
            lastRelocalizedTimestamps.Clear();
            ResetAccumulator("Full reset");
            Debug.Log("[QRAnchorRelocalizer] Relocalization state fully reset.");
        }

        private void ResetAccumulator(string reason)
        {
            if (currentBuffer != null && verboseLogging)
            {
                Debug.Log($"[QRAnchorRelocalizer] Reset accumulator: {reason}");
            }
            currentBuffer = null;
            currentObservedImage = null;
            debugAccumProgress = 0f;
        }

        private CampusNode FindAnchorNode(string qrPayload)
        {
            if (MapDataManager.Instance == null || MapDataManager.Instance.CurrentMap == null)
                return null;

            var nodes = MapDataManager.Instance.CurrentMap.nodes;
            return nodes.Find(n => n.nodeType == NodeType.QRAnchor && n.qrPayload == qrPayload);
        }

        /// <summary>
        /// Verifies bidirectional pairing between CampusImageLibrary and map QRAnchor nodes.
        /// </summary>
        public void ValidateLibraryAndMap()
        {
            if (imageManager == null)
                return;

            XRReferenceImageLibrary library =
                imageManager.referenceLibrary as XRReferenceImageLibrary;
            if (library == null)
            {
                Debug.LogWarning(
                    "[QRAnchorRelocalizer] ARTrackedImageManager has no XRReferenceImageLibrary assigned."
                );
                return;
            }

            var map = MapDataManager.Instance != null ? MapDataManager.Instance.CurrentMap : null;
            if (map == null || map.nodes == null)
                return;

            // 1. Check each QRAnchor node exists in image library
            foreach (var node in map.nodes)
            {
                if (node.nodeType == NodeType.QRAnchor)
                {
                    bool foundInLibrary = false;
                    for (int i = 0; i < library.count; i++)
                    {
                        if (library[i].name == node.qrPayload)
                        {
                            foundInLibrary = true;
                            break;
                        }
                    }

                    if (!foundInLibrary)
                    {
                        Debug.LogWarning(
                            $"<color=orange>[QRAnchorRelocalizer] VALIDATION MISMATCH:</color> Map QRAnchor '{node.label}' (payload '{node.qrPayload}') has NO matching entry in CampusImageLibrary!"
                        );
                    }
                }
            }

            // 2. Check each library entry is mapped to a node
            for (int i = 0; i < library.count; i++)
            {
                string libImgName = library[i].name;
                bool foundInMap = map.nodes.Exists(n =>
                    n.nodeType == NodeType.QRAnchor && n.qrPayload == libImgName
                );
                if (!foundInMap && verboseLogging)
                {
                    Debug.Log(
                        $"[QRAnchorRelocalizer] Notice: Library image '{libImgName}' is currently unused in the active map."
                    );
                }
            }

            // Check legacy anchors
            CampusMapData.ValidateMapAnchors(map, out _);
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!showDebugOverlay)
                return;

            // Responsive scaled GUI for high-DPI smartphone displays
            float scale = Mathf.Max(1f, Screen.width / 1080f) * debugOverlayScale;
            Matrix4x4 origMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(
                Vector3.zero,
                Quaternion.identity,
                new Vector3(scale, scale, 1f)
            );

            GUILayout.BeginArea(new Rect(20, 20, 480, 260), GUI.skin.box);
            GUILayout.Label("<b>QR ANCHOR RELOCALIZER TELEMETRY</b>");
            GUILayout.Label($"Marker: {debugMarkerName} ({debugTrackingState})");
            GUILayout.Label(
                $"Distance: {debugDistance:F2} m / {maxTrackingDistance:F1} m | Angle: {debugViewingAngle:F1}° / {maxViewingAngleDegrees:F1}°"
            );
            GUILayout.Label(
                $"Heading: {debugHeading:F1}° | Accum: {(debugAccumProgress * 100f):F0}%"
            );
            GUILayout.Label($"Status: {debugStatusMessage}");
            GUILayout.Label(
                $"Map Relocalized: {(IsRelocalized ? $"YES ({ActiveAnchorId})" : "NO")}"
            );
            GUILayout.Label(
                $"Last Correction: ΔPos={debugLastPosCorrection.magnitude:F2}m, ΔYaw={debugLastYawCorrection:F1}°"
            );

            if (HasPendingCorrection)
            {
                GUILayout.Space(5);
                GUI.color = Color.yellow;
                GUILayout.Label($"<b>SUSPICIOUS RE-SCAN DETECTED!</b>");
                GUILayout.Label(
                    $"Dist Error: {PendingCorrection.horizontalDistanceDiscrepancy:F2}m | Yaw Error: {PendingCorrection.yawDiscrepancyDegrees:F1}°"
                );
                GUI.color = Color.white;
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Confirm Overwrite", GUILayout.Height(35)))
                {
                    ConfirmPendingRelocalization();
                }
                if (GUILayout.Button("Dismiss", GUILayout.Height(35)))
                {
                    DismissPendingRelocalization();
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.EndArea();
            GUI.matrix = origMatrix;
        }
#endif
    }
}
