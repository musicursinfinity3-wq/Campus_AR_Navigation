using System;
using System.Collections.Generic;
using CampusNavigation.Data;
using CampusNavigation.Sensors;
using UnityEngine;

namespace CampusNavigation.Localization
{
    /// <summary>
    /// Handles coarse global positioning and heading alignment for MapRoot using GPS and compass.
    /// Operates fully offline, performing an initial coarse snap on startup and gentle in-motion yaw damping.
    /// Works in harmony with QRAnchorRelocalizer (which provides precision resets at physical signs).
    /// </summary>
    public class GpsRelocalizer : MonoBehaviour
    {
        public static GpsRelocalizer Instance { get; private set; }

        [Header("Scene References")]
        [Tooltip("The parent container transform for all navigation content (MapRoot).")]
        [SerializeField] private Transform mapRoot;

        [Tooltip("The AR Camera transform.")]
        [SerializeField] private Transform arCameraTransform;

        [Header("Initial Alignment Thresholds")]
        [Tooltip("Maximum allowed GPS accuracy (in meters) to perform the initial startup snap.")]
        [SerializeField] private float initialSnapMaxAccuracyMeters = 10.0f;

        [Header("In-Motion Drift Damping")]
        [Tooltip("Allow gentle yaw correction while moving outdoors.")]
        [SerializeField] private bool enableInMotionYawDamping = true;

        [Tooltip("Minimum horizontal displacement (meters) needed between GPS samples to calculate motion heading.")]
        [SerializeField] private float minMotionDisplacementMeters = 8.0f;

        [Tooltip("Maximum yaw adjustment rate in degrees per second so corrections are visually imperceptible.")]
        [SerializeField] private float maxYawRateDegreesPerSec = 1.5f;

        [Tooltip("Maximum GPS accuracy (meters) required to accept an in-motion yaw update.")]
        [SerializeField] private float inMotionMaxAccuracyMeters = 6.0f;

        public bool HasPerformedInitialSnap { get; private set; }
        public event Action OnInitialGpsSnapCompleted;

        // Motion tracking state
        private double lastMotionLat;
        private double lastMotionLon;
        private Vector3 lastMotionCamPos;
        private float lastMotionAccuracy;
        private List<Vector3> windowCamPositions = new List<Vector3>();
        private float camSampleTimer;
        private const int MAX_WINDOW_CAM_POSITIONS = 100;
        
        private float targetYawOffset;
        private bool hasTargetYaw;

        // Compass stability tracking (readings compared 1.0 s apart)
        private float lastCompassHeading;
        private float compassStabilityTimer;
        private float compassSampleTimer;
        private bool hasCompassBaseline;
        private const float COMPASS_SAMPLE_INTERVAL_SECONDS = 1.0f;

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
                if (rootGo != null) mapRoot = rootGo.transform;
            }

            if (arCameraTransform == null && Camera.main != null)
            {
                arCameraTransform = Camera.main.transform;
            }

            if (AndroidSensorManager.Instance != null)
            {
                AndroidSensorManager.Instance.OnSensorsReady += HandleSensorsReady;
            }
        }

        private void OnDestroy()
        {
            if (AndroidSensorManager.Instance != null)
            {
                AndroidSensorManager.Instance.OnSensorsReady -= HandleSensorsReady;
            }
        }

        private void HandleSensorsReady()
        {
            if (!HasPerformedInitialSnap)
            {
                TryPerformInitialSnap();
            }
        }

        private void Update()
        {
            if (!HasPerformedInitialSnap)
            {
                TryPerformInitialSnap();
                return;
            }

            // In-motion damping disabled: do not measure, store or apply any yaw target
            if (!enableInMotionYawDamping)
            {
                hasTargetYaw = false;
                windowCamPositions.Clear();
                return;
            }

            // In-flight gentle yaw damping while walking
            if (enableInMotionYawDamping && hasTargetYaw && mapRoot != null && arCameraTransform != null)
            {
                float currentYaw = mapRoot.eulerAngles.y;
                float step = Mathf.MoveTowardsAngle(currentYaw, targetYawOffset, maxYawRateDegreesPerSec * Time.deltaTime);
                
                // Use RotateAround to ensure MapRoot rotates around the user's current camera position
                float clampedDelta = Mathf.DeltaAngle(currentYaw, step);
                mapRoot.RotateAround(arCameraTransform.position, Vector3.up, clampedDelta);

                if (Mathf.Abs(Mathf.DeltaAngle(step, targetYawOffset)) < 0.1f)
                {
                    hasTargetYaw = false;
                }
            }

            // Record camera position at ~4 Hz (0.25s) with a size cap
            if (arCameraTransform != null)
            {
                camSampleTimer += Time.deltaTime;
                if (camSampleTimer >= 0.25f)
                {
                    camSampleTimer = 0f;
                    if (windowCamPositions.Count >= MAX_WINDOW_CAM_POSITIONS)
                    {
                        windowCamPositions.RemoveAt(0);
                    }
                    windowCamPositions.Add(arCameraTransform.position);
                }
            }

            // Check if user has walked far enough outdoors to evaluate bearing
            CheckInMotionDisplacement();
        }

        /// <summary>
        /// Attempts a one-time coarse placement of MapRoot at startup based on initial GPS and compass.
        /// </summary>
        public bool TryPerformInitialSnap()
        {
            // Do not override if user already scanned a precision QR code
            if (QRAnchorRelocalizer.Instance != null && QRAnchorRelocalizer.Instance.IsRelocalized)
            {
                HasPerformedInitialSnap = true;
                return true;
            }

            if (AndroidSensorManager.Instance == null || !AndroidSensorManager.Instance.IsGpsRunning)
                return false;

            if (AndroidSensorManager.Instance.HorizontalAccuracy > initialSnapMaxAccuracyMeters)
                return false;

            // Compass Stability Check
            float currentHeading = AndroidSensorManager.Instance.CurrentHeading;
            float headingAcc = AndroidSensorManager.Instance.HeadingAccuracy;
            
            if (headingAcc < 0)
            {
                // Compass reports invalid accuracy: discard any stability history
                hasCompassBaseline = false;
                compassStabilityTimer = 0f;
                compassSampleTimer = 0f;
                return false;
            }

            // First valid reading only establishes the baseline
            if (!hasCompassBaseline)
            {
                hasCompassBaseline = true;
                lastCompassHeading = currentHeading;
                compassSampleTimer = 0f;
                compassStabilityTimer = 0f;
                return false;
            }

            compassSampleTimer += Time.deltaTime;
            compassStabilityTimer += Time.deltaTime;

            // Compare readings taken 1.0 s apart; refresh the baseline at each comparison so slow drift
            // is measured per second instead of against a stale first reading.
            if (compassSampleTimer >= COMPASS_SAMPLE_INTERVAL_SECONDS)
            {
                float headingChange = Mathf.Abs(Mathf.DeltaAngle(currentHeading, lastCompassHeading));
                lastCompassHeading = currentHeading;
                compassSampleTimer = 0f;

                if (headingChange > 10f)
                {
                    compassStabilityTimer = 0f;
                    return false; // Compass jittering, wait for it to settle
                }
            }

            if (compassStabilityTimer < COMPASS_SAMPLE_INTERVAL_SECONDS)
            {
                return false; // Require at least 1 second of stable compass readings
            }

            if (MapDataManager.Instance == null || MapDataManager.Instance.CurrentMap == null)
                return false;

            CampusMapData map = MapDataManager.Instance.CurrentMap;
            if (map.originLatitude == 0) return false;

            if (mapRoot == null || arCameraTransform == null) return false;

            // 1. Calculate user's local metric position relative to campus origin
            double currentLat = AndroidSensorManager.Instance.CurrentLatitude;
            double currentLon = AndroidSensorManager.Instance.CurrentLongitude;
            Vector3 userLocalPos = map.GPSToLocal(currentLat, currentLon);

            // 2. Set MapRoot orientation based on phone compass (orients North)
            float cameraYaw = arCameraTransform.eulerAngles.y;
            float mapRootYaw = (cameraYaw - currentHeading);

            mapRoot.rotation = Quaternion.Euler(0f, mapRootYaw, 0f);

            // 3. Shift MapRoot so that the user's current camera position corresponds to userLocalPos
            Vector3 rotatedLocalPos = mapRoot.rotation * userLocalPos;
            mapRoot.position = arCameraTransform.position - rotatedLocalPos;

            HasPerformedInitialSnap = true;
            lastMotionLat = currentLat;
            lastMotionLon = currentLon;
            lastMotionCamPos = arCameraTransform.position;
            lastMotionAccuracy = AndroidSensorManager.Instance.HorizontalAccuracy;
            windowCamPositions.Clear();

            Debug.Log($"<color=cyan>[GpsRelocalizer] ✓ INITIAL COARSE SNAP:</color> Accuracy={AndroidSensorManager.Instance.HorizontalAccuracy:F1}m | Heading={currentHeading:F1}° | LocalPos={userLocalPos}");
            OnInitialGpsSnapCompleted?.Invoke();
            return true;
        }

        private void CheckInMotionDisplacement()
        {
            if (AndroidSensorManager.Instance == null || !AndroidSensorManager.Instance.IsGpsRunning)
            {
                windowCamPositions.Clear();
                return;
            }

            float currentAccuracy = AndroidSensorManager.Instance.HorizontalAccuracy;
            if (currentAccuracy >= inMotionMaxAccuracyMeters)
            {
                // Poor fix: restart the whole measurement window (GPS + camera baseline), not just the camera samples
                ResetMotionTracking();
                return;
            }

            // First good fix after a poor one: re-baseline now instead of waiting a full displacement window
            if (lastMotionAccuracy >= inMotionMaxAccuracyMeters)
            {
                ResetMotionTracking();
                return;
            }

            // Skip if recently precision-relocalized via QR
            // TODO: this skip disables in-motion yaw damping entirely after any QR scan (IsRelocalized stays true
            // until a session reset), so VIO yaw drift after the QR snap is never corrected by GPS. Revisit after
            // outdoor walk tests (e.g. only skip for a limited time/distance after the scan).
            if (QRAnchorRelocalizer.Instance != null && QRAnchorRelocalizer.Instance.IsRelocalized)
            {
                windowCamPositions.Clear();
                return;
            }

            double currLat = AndroidSensorManager.Instance.CurrentLatitude;
            double currLon = AndroidSensorManager.Instance.CurrentLongitude;

            float gpsDist = CampusMapData.CalculateDistanceMeters(lastMotionLat, lastMotionLon, currLat, currLon);
            float minRequired = Mathf.Max(minMotionDisplacementMeters, Mathf.Max(15f, 3f * currentAccuracy));

            if (gpsDist >= minRequired)
{
                // Accuracy check at both ends
                if (lastMotionAccuracy >= inMotionMaxAccuracyMeters)
                {
                    ResetMotionTracking();
                    return;
                }

                // Calculate GPS displacement vector in ENU meters
                CampusMapData map = MapDataManager.Instance != null ? MapDataManager.Instance.CurrentMap : null;
                if (map != null && map.originLatitude != 0 && arCameraTransform != null)
                {
                    Vector3 prevPos = map.GPSToLocal(lastMotionLat, lastMotionLon);
                    Vector3 currPos = map.GPSToLocal(currLat, currLon);
                    Vector3 gpsDir = (currPos - prevPos).normalized;

                    // Calculate VIO camera displacement in world space
                    Vector3 vioVector = (arCameraTransform.position - lastMotionCamPos);
                    float vioDist = vioVector.magnitude;
                    Vector3 vioDir = vioVector;
                    vioDir.y = 0f;
                    vioDir.Normalize();

                    // VIO vs GPS distance verification (30% limit)
                    if (Mathf.Abs(vioDist - gpsDist) / gpsDist > 0.3f)
                    {
                        ResetMotionTracking();
                        return;
                    }

                    // Chord stability check: rejection if user didn't walk roughly straight (15% limit)
                    // Flatten y coordinates for planar chord analysis
                    Vector3 chordStart = new Vector3(lastMotionCamPos.x, 0f, lastMotionCamPos.z);
                    Vector3 chordEnd = new Vector3(arCameraTransform.position.x, 0f, arCameraTransform.position.z);
                    float chordLength = Vector3.Distance(chordStart, chordEnd);
                    if (chordLength < 0.1f)
                    {
                        ResetMotionTracking();
                        return;
                    }
                    if (windowCamPositions.Count > 2)
                    {
                        float maxLateralDev = 0f;
                        Vector3 chordDir = (chordEnd - chordStart) / chordLength;
                        
                        foreach (var pos in windowCamPositions)
                        {
                            Vector3 planarPos = new Vector3(pos.x, 0f, pos.z);
                            Vector3 toPos = planarPos - chordStart;
                            Vector3 lateral = toPos - Vector3.Project(toPos, chordDir);
                            maxLateralDev = Mathf.Max(maxLateralDev, lateral.magnitude);
                        }

                        if (maxLateralDev > 0.15f * chordLength)
                        {
                            ResetMotionTracking();
                            return;
                        }
                    }

                    if (gpsDir.sqrMagnitude > 0.1f && vioDir.sqrMagnitude > 0.1f)
                    {
                        float gpsAngle = Mathf.Atan2(gpsDir.x, gpsDir.z) * Mathf.Rad2Deg;
                        float vioAngle = Mathf.Atan2(vioDir.x, vioDir.z) * Mathf.Rad2Deg;
                        
                        // Absolute MapRoot Yaw Calculation: measuredYaw = vioAngle - gpsAngle
                        float measuredYaw = Mathf.DeltaAngle(gpsAngle, vioAngle);
                        
                        // Blend yaw correction (apply 50% fraction of measured delta to avoid overcorrecting single windows)
                        float currentMapRootYaw = mapRoot != null ? mapRoot.eulerAngles.y : 0f;
                        float deltaToTarget = Mathf.DeltaAngle(currentMapRootYaw, measuredYaw);
                        targetYawOffset = currentMapRootYaw + (deltaToTarget * 0.5f);
                        hasTargetYaw = true;
                    }
                }

                ResetMotionTracking();
            }
        }

        public void ResetRelocalizationState()
        {
            HasPerformedInitialSnap = false;
            hasTargetYaw = false;
            hasCompassBaseline = false;
            compassStabilityTimer = 0f;
            compassSampleTimer = 0f;
            ResetMotionTracking();
            Debug.Log("[GpsRelocalizer] GPS relocalization state fully reset.");
        }

        public void ClearInMotionTarget()
        {
            hasTargetYaw = false;
            ResetMotionTracking();
        }

        public void MarkExternallyAligned()
        {
            HasPerformedInitialSnap = true;
            ResetMotionTracking();
        }

        public void ResetMotionTracking()
        {
            if (AndroidSensorManager.Instance != null && AndroidSensorManager.Instance.IsGpsRunning)
            {
                lastMotionLat = AndroidSensorManager.Instance.CurrentLatitude;
                lastMotionLon = AndroidSensorManager.Instance.CurrentLongitude;
                lastMotionAccuracy = AndroidSensorManager.Instance.HorizontalAccuracy;
            }
            if (arCameraTransform != null)
            {
                lastMotionCamPos = arCameraTransform.position;
            }
            windowCamPositions.Clear();
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (AndroidSensorManager.Instance == null || arCameraTransform == null) return;
            
            float scale = Mathf.Max(1f, Screen.width / 1080f);
            Matrix4x4 origMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

            // Positioned at top right, offset vertically below QR debug menu or placed neatly in right quadrant
            float scaledWidth = Screen.width / scale;
            float scaledHeight = Screen.height / scale;
            GUILayout.BeginArea(new Rect(scaledWidth - 340, scaledHeight - 200, 320, 180), GUI.skin.box);
            GUILayout.Label("<b>GPS RELOCALIZER TELEMETRY</b>");
            GUILayout.Label($"Heading: {AndroidSensorManager.Instance.CurrentHeading:F1}° (Acc: {AndroidSensorManager.Instance.HeadingAccuracy:F1}°)");
            GUILayout.Label($"Camera Yaw: {arCameraTransform.eulerAngles.y:F1}°");
            GUILayout.Label($"GPS Accuracy: {AndroidSensorManager.Instance.HorizontalAccuracy:F1} m");
            GUILayout.Label($"Snap active: {HasPerformedInitialSnap}");
            GUILayout.Label($"In-motion target: {(hasTargetYaw ? $"{targetYawOffset:F1}°" : "None")}");
            GUILayout.EndArea();

            GUI.matrix = origMatrix;
        }
#endif
}
}
