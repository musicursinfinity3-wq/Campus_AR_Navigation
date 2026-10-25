using System.Collections.Generic;
using CampusNavigation.Admin;
using CampusNavigation.Data;
using CampusNavigation.Localization;
using CampusNavigation.Pathfinding;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CampusNavigation.Navigation
{
    public class UserNavigationUI : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField]
        private NavigationSessionManager sessionManager;

        [SerializeField]
        private AStarPathfinder pathfinder;

        [SerializeField]
        private QRAnchorRelocalizer relocalizer;

        [SerializeField]
        private Transform mapRoot;

        [Header("Workflow Panels")]
        [Tooltip("The welcome/intro screen (your unpacked StartPanel)")]
        [SerializeField]
        private GameObject startPanel;

        [Tooltip("The screen instructing user to point camera at the QR sign")]
        [SerializeField]
        private GameObject scanPromptPanel;

        [Tooltip("The destination dropdown + Go button panel")]
        [SerializeField]
        private GameObject searchPanel;

        [Tooltip("The walking navigation banner showing distance + Cancel")]
        [SerializeField]
        private GameObject activeNavPanel;

        [Header("Scan Prompt Controls")]
        [SerializeField]
        private Button cancelScanButton;

        [Header("Start Panel Controls")]
        [SerializeField]
        private Button beginButton;

        [SerializeField]
        private Button bypassButton;

        [Header("Search Controls")]
        [SerializeField]
        private TMP_Dropdown destinationDropdown;

        [SerializeField]
        private Button startNavButton;

        [SerializeField]
        private TextMeshProUGUI statusFeedbackText;

        [SerializeField]
        private TextMeshProUGUI anchorStatusText; // Shows e.g. "Anchored: Main Entrance ✓"

        [Header("Active Navigation Controls")]
        [SerializeField]
        private TextMeshProUGUI destinationNameText;

        [SerializeField]
        private TextMeshProUGUI distanceText;

        [SerializeField]
        private Button cancelNavButton;

        [Header("Location Info & Office Hours")]
        [SerializeField]
        private CampusLocationDatabase locationDatabase;

        [SerializeField]
        private GameObject officeHoursPanel;

        [SerializeField]
        private TextMeshProUGUI officeHoursText;

        private List<CampusNode> availableDestinations = new List<CampusNode>();
        private TextMeshProUGUI cachedScanHintText;
        private AdminUIController adminController;

        private void Awake()
        {
            if (scanPromptPanel != null)
            {
                cachedScanHintText = scanPromptPanel.GetComponentInChildren<TextMeshProUGUI>();
            }
        }

        private void Start()
{
            if (sessionManager == null)
                sessionManager = NavigationSessionManager.Instance;
            if (pathfinder == null)
                pathfinder = AStarPathfinder.Instance;
            if (relocalizer == null)
                relocalizer = QRAnchorRelocalizer.Instance;

            // Button listeners
            if (beginButton != null)
                beginButton.onClick.AddListener(OnBeginClicked);

            if (bypassButton != null)
                bypassButton.onClick.AddListener(ShowSearchPanel);

            if (startNavButton != null)
                startNavButton.onClick.AddListener(OnStartNavClicked);

            if (cancelNavButton != null)
                cancelNavButton.onClick.AddListener(OnCancelNavClicked);

            if (cancelScanButton != null)
                cancelScanButton.onClick.AddListener(ShowStartPanel);

            // Relocalization listener (when QR code is scanned)
            if (relocalizer != null)
            {
                relocalizer.OnRelocalizedToAnchor += OnQRAnchorScanned;
                relocalizer.OnSuspiciousRelocalizationDetected += OnSuspiciousRelocalization;
                relocalizer.OnPendingRelocalizationResolved += OnSuspiciousRelocalizationResolved;
            }

            // Navigation events
            if (sessionManager != null)
            {
                sessionManager.OnNavigationFailed += ShowFeedbackMessage;
                sessionManager.OnDestinationReached += OnReachedDestination;
                sessionManager.OnNavigationEnded += HandleNavigationEnded;
            }

            if (relocalizer != null)
            {
                relocalizer.OnScanHintChanged += UpdateScanHintUI;
                relocalizer.OnSessionReset += HandleSessionReset;

                // Sync the scan prompt text once with whatever hint is already active
                UpdateScanHintUI(relocalizer.ScanHint);
            }

            if (pathfinder != null)
{
                pathfinder.OnGraphInitialized += HandleGraphReady;
                if (
                    pathfinder.IsGraphReady
                    && MapDataManager.Instance != null
                    && MapDataManager.Instance.CurrentMap != null
                )
                {
                    HandleGraphReady();
                }
            }

            // Start on Phase 1: Welcome screen
            ShowStartPanel();
        }

        private void OnDestroy()
        {
            if (pathfinder != null)
                pathfinder.OnGraphInitialized -= HandleGraphReady;

            if (relocalizer != null)
            {
                relocalizer.OnRelocalizedToAnchor -= OnQRAnchorScanned;
                relocalizer.OnSuspiciousRelocalizationDetected -= OnSuspiciousRelocalization;
                relocalizer.OnPendingRelocalizationResolved -= OnSuspiciousRelocalizationResolved;
                relocalizer.OnScanHintChanged -= UpdateScanHintUI;
                relocalizer.OnSessionReset -= HandleSessionReset;
            }

            if (sessionManager != null)
            {
                sessionManager.OnNavigationFailed -= ShowFeedbackMessage;
                sessionManager.OnDestinationReached -= OnReachedDestination;
                sessionManager.OnNavigationEnded -= HandleNavigationEnded;
            }
        }

        /// <summary>
        /// Full AR session reset (re-init / app resume): return visitors to the welcome screen,
        /// unless an admin is currently working in the admin panel.
        /// </summary>
        private void HandleSessionReset()
        {
            if (adminController == null)
                adminController = FindFirstObjectByType<AdminUIController>();

            if (adminController != null && adminController.IsAdminPanelActive)
                return;

            ShowStartPanel();
        }

        private void OnSuspiciousRelocalization(QRAnchorRelocalizer.PendingRelocalization pending)
        {
            ShowFeedbackMessage($"Large discrepancy detected for '{pending.anchorNode.label}' ({pending.horizontalDistanceDiscrepancy:F1}m, {pending.yawDiscrepancyDegrees:F1}°). Rescan or confirm via overlay.");
        }

        private void OnSuspiciousRelocalizationResolved()
        {
            if (statusFeedbackText != null && statusFeedbackText.text.Contains("Large discrepancy"))
            {
                statusFeedbackText.text = "";
            }
        }

        private void HandleNavigationEnded()
        {
            // When navigation is canceled or finished, return to search if aligned, otherwise start
            if ((relocalizer != null && relocalizer.IsRelocalized) || 
                (GpsRelocalizer.Instance != null && GpsRelocalizer.Instance.HasPerformedInitialSnap))
            {
                // Keep any status message (e.g. "You have arrived") set just before navigation ended
                ShowSearchPanelInternal(false);
            }
            else
            {
                ShowStartPanel();
            }
        }

        private void UpdateScanHintUI(string hint)
        {
            if (cachedScanHintText != null)
            {
                cachedScanHintText.text = hint;
            }
        }

        private void Update()
        {
            if (sessionManager != null && sessionManager.IsNavigating)
            {
                if (distanceText != null)
                {
                    distanceText.text = $"{sessionManager.RemainingDistance:F1} m";
                }
            }

            // Show "Approximate Alignment" notice ONLY when not precision relocalized
            if (anchorStatusText != null)
            {
                bool isPrecision = relocalizer != null && relocalizer.IsRelocalized;
                bool isCoarse = GpsRelocalizer.Instance != null && GpsRelocalizer.Instance.HasPerformedInitialSnap;
                
                if (!isPrecision)
                {
                    if (isCoarse)
                    {
                        anchorStatusText.text = "<color=yellow>! Approximate GPS Alignment</color>";
                    }
                    else
                    {
                        anchorStatusText.text = "Waiting for alignment...";
                    }
                }
            }
}

        // ================= FLOW CONTROLLERS =================
        /// <summary>
        /// Used in Editor to simulate having scanned the nearest QR anchor/node at the current camera position.
        /// This ensures the map aligns to the user's current intent instead of just snapping the origin.
        /// </summary>
        public void SimulateScanAtCurrentLocation()
        {
            Transform cam = Camera.main != null ? Camera.main.transform : null;
            if (
                cam == null
                || MapDataManager.Instance == null
                || MapDataManager.Instance.CurrentMap == null
            )
            {
                Debug.LogWarning(
                    "[UserNavigationUI] Cannot simulate: Map not loaded or Camera missing."
                );
                return;
            }

            // Find MapRoot if not assigned
            if (mapRoot == null)
            {
                GameObject go = GameObject.Find("MapRoot");
                if (go != null)
                    mapRoot = go.transform;
            }

            if (mapRoot == null)
            {
                Debug.LogError(
                    "[UserNavigationUI] MapRoot not found. Please assign it in the Inspector."
                );
                return;
            }

            // 1. Find the nearest node in the map to use as our "Simulated QR Anchor"
            // We use the camera's current local position relative to the map's current placement
            CampusNode anchorNode = null;
            if (pathfinder != null)
            {
                Vector3 localCamPos = mapRoot.InverseTransformPoint(cam.position);
                anchorNode = pathfinder.FindNearestNode(localCamPos);
            }

            // Fallback to the first node if none found
            if (anchorNode == null && MapDataManager.Instance.CurrentMap.nodes.Count > 0)
            {
                anchorNode = MapDataManager.Instance.CurrentMap.nodes[0];
            }

            if (anchorNode != null)
            {
                // 2. Relocalize MapRoot so that anchorNode.localPosition coincides with cam.position
                // Rotation (Yaw only)
                float recordedYaw = anchorNode.qrFacingEuler.y;
                float currentYaw = cam.eulerAngles.y;
                float deltaYaw = currentYaw - recordedYaw;

                mapRoot.rotation = Quaternion.Euler(0f, deltaYaw, 0f);

                // Translation
                Vector3 rotatedLocalPos = mapRoot.rotation * anchorNode.localPosition;
                mapRoot.position = cam.position - rotatedLocalPos;

                Debug.Log(
                    $"[UserNavigationUI] Simulated Scan: Aligned map to node '{anchorNode.label}' ({anchorNode.id}) at camera position."
                );
            }

            ShowSearchPanel();
        }

        /// <summary>
        /// Phase 1: Student taps "Begin" on the welcome screen.
        /// </summary>
        private void OnBeginClicked()
        {
            // If already relocalized in this session, go straight to destination select
            if (relocalizer != null && relocalizer.IsRelocalized)
            {
                ShowSearchPanel();
            }
            else
            {
                // Prompt student to scan entrance QR code
                ShowScanPromptPanel();
            }
        }

        /// <summary>
        /// Phase 2: Called automatically when the physical QR code is detected by ARCore.
        /// </summary>
        private void OnQRAnchorScanned(string anchorNodeId)
        {
            CampusNode anchorNode = MapDataManager.Instance?.CurrentMap?.nodes.Find(n => n.id == anchorNodeId);
            string label = anchorNode != null ? anchorNode.label : "QR Anchor";

            Debug.Log($"[UserNavigationUI] QR Anchor verified: {label}.");

#if UNITY_ANDROID && !UNITY_EDITOR
            Handheld.Vibrate();
#endif

            if (anchorStatusText != null)
            {
                anchorStatusText.text = $"✓ Location Verified: {label}";
            }

            // Do not interrupt active navigation with the search panel
            if (sessionManager != null && sessionManager.IsNavigating)
            {
                ShowFeedbackMessage($"Resynchronized to {label}");
                return;
            }

            // Move to Phase 3: Show destination selection
            ShowSearchPanel();
        }

        /// <summary>
        /// Phase 3: Student selects a building and taps "Go".
        /// </summary>
        private void OnStartNavClicked()
        {
            if (
                availableDestinations.Count == 0
                || destinationDropdown == null
                || sessionManager == null
            )
                return;

            int selectedIndex = destinationDropdown.value;
            if (selectedIndex < 0 || selectedIndex >= availableDestinations.Count)
                return;

            CampusNode chosenDest = availableDestinations[selectedIndex];
            bool success = sessionManager.StartNavigation(chosenDest.id);

            if (success)
            {
                ShowActiveNavPanel(
                    string.IsNullOrEmpty(chosenDest.label) ? chosenDest.id : chosenDest.label
                );
            }
        }

        /// <summary>
        /// Phase 4: Student cancels active navigation.
        /// </summary>
        private void OnCancelNavClicked()
        {
            if (sessionManager != null)
            {
                sessionManager.StopNavigation();
            }
            ShowSearchPanelInternal(true);
        }

        private void OnReachedDestination()
        {
            // Don't clear status here, then set the arrival message after the panel switch.
            // (HandleNavigationEnded also calls ShowSearchPanelInternal(false) right after this event.)
            ShowSearchPanelInternal(false);
            ShowFeedbackMessage("You have arrived at your destination!");
        }

        // ================= UI STATE SWITCHERS =================

        public void ShowStartPanel()
        {
            SetPanelState(startPanel: true, scanPrompt: false, search: false, activeNav: false);
        }

        public void ShowScanPromptPanel()
        {
            SetPanelState(startPanel: false, scanPrompt: true, search: false, activeNav: false);
        }

        // Parameterless overload kept (not a default argument) so it still binds to UnityAction
        // listeners such as bypassButton.onClick and to external callers.
        public void ShowSearchPanel()
        {
            if (sessionManager != null && sessionManager.IsNavigating) return;
            ShowSearchPanelInternal(true);
        }

        public void ShowSearchPanel(bool clearStatus)
        {
            ShowSearchPanelInternal(clearStatus);
        }

        private void ShowSearchPanelInternal(bool clearStatus)
        {
            SetPanelState(startPanel: false, scanPrompt: false, search: true, activeNav: false);
            if (clearStatus && statusFeedbackText != null)
                statusFeedbackText.text = "";
            if (MapDataManager.Instance != null && MapDataManager.Instance.CurrentMap != null)
            {
                PopulateDestinations(MapDataManager.Instance.CurrentMap);
            }
        }

        public void ShowActiveNavPanel(string destName)
        {
            SetPanelState(startPanel: false, scanPrompt: false, search: false, activeNav: true);
            if (destinationNameText != null)
                destinationNameText.text = $"GOING TO: {destName}";

            UpdateOfficeHours(destName);
        }

        private void UpdateOfficeHours(string destName)
        {
            if (officeHoursPanel == null && officeHoursText == null) return;

            CampusNavigation.Data.LocationInfo info = locationDatabase != null ? locationDatabase.GetLocationInfo(destName) : null;

            if (info != null)
{
                if (officeHoursText != null)
                {
                    string details = $"<b>OFFICE HOURS:</b> {info.openDays} ({info.officeHours})";
                    if (!string.IsNullOrEmpty(info.additionalInfo))
                    {
                        details += $" | {info.additionalInfo}";
                    }
                    officeHoursText.text = details;
                }
                if (officeHoursPanel != null)
                {
                    officeHoursPanel.SetActive(true);
                }
            }
            else
            {
                // Fallback if no specific database entry exists
                if (officeHoursText != null)
                {
                    officeHoursText.text = "<b>OFFICE HOURS:</b> Mon - Fri (8:00 AM - 5:00 PM)";
                }
                if (officeHoursPanel != null)
                {
                    officeHoursPanel.SetActive(true);
                }
            }
        }

        private void SetPanelState(bool startPanel, bool scanPrompt, bool search, bool activeNav)
        {
            if (this.startPanel != null)
                this.startPanel.SetActive(startPanel);
            if (this.scanPromptPanel != null)
                this.scanPromptPanel.SetActive(scanPrompt);
            if (this.searchPanel != null)
            {
                Transform parent = this.searchPanel.transform.parent;
                if (parent != null)
                {
                    parent.gameObject.SetActive(search || activeNav);
                }
                this.searchPanel.SetActive(search);
            }
            if (this.activeNavPanel != null)
                this.activeNavPanel.SetActive(activeNav);
        }

        public void PopulateDestinations(CampusMapData mapData)
        {
            if (destinationDropdown == null || pathfinder == null)
                return;

            availableDestinations = pathfinder.GetAllDestinations();
            destinationDropdown.ClearOptions();

            List<string> options = new List<string>();
            if (availableDestinations.Count == 0)
            {
                options.Add("No destinations recorded yet");
                destinationDropdown.interactable = false;
                if (startNavButton != null)
                    startNavButton.interactable = false;
            }
            else
            {
                foreach (var dest in availableDestinations)
                {
                    options.Add(string.IsNullOrEmpty(dest.label) ? dest.id : dest.label);
                }
                destinationDropdown.interactable = true;
                if (startNavButton != null)
                    startNavButton.interactable = true;
            }

            destinationDropdown.AddOptions(options);
        }

        private void ShowFeedbackMessage(string message)
        {
            if (statusFeedbackText != null)
                statusFeedbackText.text = message;
        }

        private void HandleGraphReady()
        {
            if (MapDataManager.Instance != null)
            {
                PopulateDestinations(MapDataManager.Instance.CurrentMap);
            }
        }
    }
}
