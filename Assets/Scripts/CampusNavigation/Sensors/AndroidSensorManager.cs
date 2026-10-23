using System;
using System.Collections;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace CampusNavigation.Sensors
{
    public enum SensorStatus
    {
        NotStarted,
        RequestingPermissions,
        Initializing,
        Ready,
        Failed
    }

    public class AndroidSensorManager : MonoBehaviour
    {
        public static AndroidSensorManager Instance { get; private set; }

        [Header("Location Service Settings")]
        [Tooltip("Desired accuracy in meters. Smaller = more accurate GPS, slightly higher battery usage.")]
        [SerializeField] private float desiredAccuracyInMeters = 1.0f;

        [Tooltip("Minimum distance in meters before triggering a location update.")]
        [SerializeField] private float updateDistanceInMeters = 0.0f;

        [Tooltip("Maximum seconds to wait for GPS initialization before timing out.")]
[SerializeField] private int timeoutSeconds = 20;

        [Header("Status (Read-Only)")]
        [SerializeField] private SensorStatus status = SensorStatus.NotStarted;
        public SensorStatus Status => status;

        // Current Hardware Readings
        public double CurrentLatitude { get; private set; }
        public double CurrentLongitude { get; private set; }
        public float CurrentAltitude { get; private set; }
        public float HorizontalAccuracy { get; private set; } = 999f;
        public double LastFixTimestamp { get; private set; }
        public float CurrentHeading { get; private set; } // Degrees (0 to 360) from True North
        public float HeadingAccuracy { get; private set; }
        public bool IsGpsRunning => status == SensorStatus.Ready;

        public event Action OnSensorsReady;
        public event Action<string> OnSensorFailure;

        [Header("Editor Simulation Fallback")]
        [Tooltip("BSU Main Campus default latitude (La Trinidad, Benguet: ~16.4554)")]
        [SerializeField] private double editorSimulatedLatitude = 16.4554;
        [Tooltip("BSU Main Campus default longitude (La Trinidad, Benguet: ~120.5901)")]
        [SerializeField] private double editorSimulatedLongitude = 120.5901;
        [SerializeField] private float editorSimulatedAltitude = 1300.0f; // Approx elevation above sea level (m)
        [SerializeField] private float editorSimulatedAccuracy = 3.5f;

        private void Start()
        {
            StartCoroutine(InitializeSensorsRoutine());
        }

        private IEnumerator InitializeSensorsRoutine()
        {
#if UNITY_EDITOR
            // In Unity Editor, skip physical mobile hardware and use simulated coordinates
            // (Reference settings so compiler warnings CS0414 are suppressed)
            if (desiredAccuracyInMeters < 0 || updateDistanceInMeters < 0 || timeoutSeconds < 0) { }

            Debug.Log("[AndroidSensorManager] Running in Unity Editor: Using simulated GPS and Compass.");
            CurrentLatitude = editorSimulatedLatitude;
            CurrentLongitude = editorSimulatedLongitude;
            CurrentAltitude = editorSimulatedAltitude;
            HorizontalAccuracy = editorSimulatedAccuracy;
            CurrentHeading = 0f; // Facing North
            status = SensorStatus.Ready;
            OnSensorsReady?.Invoke();
            yield break;
#else
            status = SensorStatus.RequestingPermissions;

            // 1. Check and request Android runtime permissions
            if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
            {
                Debug.Log("[AndroidSensorManager] Requesting FINE_LOCATION permission...");
                Permission.RequestUserPermission(Permission.FineLocation);
                
                yield return new WaitForSeconds(0.5f);
                
                float promptWait = 0f;
                while (!Permission.HasUserAuthorizedPermission(Permission.FineLocation) && promptWait < 10f)
                {
                    promptWait += Time.deltaTime;
                    yield return null;
                }
            }

            if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
            {
                Fail("Location permission was denied by the user.");
                yield break;
            }

            // 2. Check if device has hardware location enabled (device GPS toggle on)
            if (!Input.location.isEnabledByUser)
            {
                Fail("Device location service is disabled. Please enable GPS in Android Settings.");
                yield break;
            }

            status = SensorStatus.Initializing;

            // 3. Start GPS service
            Input.location.Start(desiredAccuracyInMeters, updateDistanceInMeters);

            // 4. Enable native Compass/Magnetometer
            Input.compass.enabled = true;

            // 5. Wait for GPS service to finish initialization
            int waitCounter = timeoutSeconds;
            while (Input.location.status == LocationServiceStatus.Initializing && waitCounter > 0)
            {
                yield return new WaitForSeconds(1.0f);
                waitCounter--;
            }

            if (waitCounter <= 0)
            {
                Fail("GPS initialization timed out. Satellites could not be reached.");
                yield break;
            }

            if (Input.location.status == LocationServiceStatus.Failed)
            {
                Fail("Unable to determine device location via Input.location.");
                yield break;
            }

            // 6. Success on physical device
            status = SensorStatus.Ready;
            Debug.Log("[AndroidSensorManager] GPS and Compass initialized successfully.");
            OnSensorsReady?.Invoke();
#endif
        }

        private void Update()
        {
            if (status != SensorStatus.Ready) return;

#if UNITY_EDITOR
            // Simulate advancing timestamp and small position jitter every frame in Editor
            // This ensures multi-sample averaging and distinct fix logic can be tested.
            LastFixTimestamp += Time.deltaTime;
            
            // Apply tiny random jitter (~5cm) to simulated lat/lon
            double jitterLat = (UnityEngine.Random.value - 0.5) * 0.000001;
            double jitterLon = (UnityEngine.Random.value - 0.5) * 0.000001;
            CurrentLatitude = editorSimulatedLatitude + jitterLat;
            CurrentLongitude = editorSimulatedLongitude + jitterLon;
#else
            // Update GPS values when running
            if (Input.location.status == LocationServiceStatus.Running)
            {
                var loc = Input.location.lastData;
                CurrentLatitude = loc.latitude;
                CurrentLongitude = loc.longitude;
                CurrentAltitude = loc.altitude;
                HorizontalAccuracy = loc.horizontalAccuracy;
                LastFixTimestamp = loc.timestamp;
            }

            // Update Compass Heading (fallback to magnetic heading if true heading not available)
            if (Input.compass.enabled)
            {
                float heading = Input.compass.trueHeading;
                if (heading == 0f && Input.compass.magneticHeading != 0f)
                {
                    heading = Input.compass.magneticHeading;
                }

                CurrentHeading = heading;
                HeadingAccuracy = Input.compass.headingAccuracy;
            }
#endif
        }

        private void Fail(string reason)
        {
            status = SensorStatus.Failed;
            Debug.LogError($"[AndroidSensorManager] {reason}");
            OnSensorFailure?.Invoke(reason);
        }

        private void OnDisable()
        {
            if (Input.location.status == LocationServiceStatus.Running)
            {
                Input.location.Stop();
            }
        }
    }
}