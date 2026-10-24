using UnityEngine;
using UnityEngine.UI;
using TMPro;
using CampusNavigation.Data;
using CampusNavigation.Navigation;
using CampusNavigation.Sensors;

namespace CampusNavigation.Admin
{
    public class AdminUIController : MonoBehaviour
    {
        [Header("Dependencies")]
        [SerializeField] private NodeRecorder recorder;
        [SerializeField] private Transform mapRoot;
        [SerializeField] private Transform arCameraTransform;

        [Header("UI Controls")]
[SerializeField] private Button clearMapButton;
        [SerializeField] private GameObject adminPanel;
        [SerializeField] private Button recordButton;
        [SerializeField] private TextMeshProUGUI recordButtonText;
        [SerializeField] private Button markDestinationButton;
        [SerializeField] private TMP_InputField destinationInputField;
        [SerializeField] private TextMeshProUGUI statsText;
        [SerializeField] private Button bypassDestinationButton;
        [SerializeField] private Button linkJunctionButton;
        [SerializeField] private Button joinPathButton;

        [Header("Managed UI Panels")]
[SerializeField] private GameObject startPanel;
        [SerializeField] private GameObject scanPromptPanel;
        [SerializeField] private GameObject userNavPanel;
        [SerializeField] private UserNavigationUI userNavigationUI;

        private GameObject previousActivePanel;
        private string pendingStatusMessage;
        private float statusMessageTimer;
        private Camera cachedMainCamera;

        // Authoring dialog state
private enum AdminDialogType { None, StartNodeSelection, EndNodeSelection, NodePicker, JoinPathConfirm }
        private AdminDialogType activeDialog = AdminDialogType.None;
        private CampusNode nearestNodeFound;
        private float nearestNodeDist;
        private Vector2 pickerScrollPos;

        private void Awake()
{
            AutoAssignPanels();
        }

        private void Start()
        {
            AutoAssignPanels();
            cachedMainCamera = Camera.main;

            if (recorder == null)
recorder = NodeRecorder.Instance != null ? NodeRecorder.Instance : FindFirstObjectByType<NodeRecorder>();

            if (clearMapButton != null)
                clearMapButton.onClick.AddListener(OnClearMapClicked);
            if (recordButton != null)
                recordButton.onClick.AddListener(OnRecordButtonClicked);
            if (markDestinationButton != null)
                markDestinationButton.onClick.AddListener(OnMarkDestinationClicked);
            if (bypassDestinationButton != null)
                bypassDestinationButton.onClick.AddListener(OnBypassToDestinationClicked);
            if (linkJunctionButton != null)
                linkJunctionButton.onClick.AddListener(OnLinkJunctionClicked);
            if (joinPathButton != null)
                joinPathButton.onClick.AddListener(OnJoinPathClicked);

            if (recorder != null)
            {
                recorder.OnRecordingError += (msg) => ShowTimedStatusMessage($"<color=red>Error: {msg}</color>", 4.0f);
            }

            UpdateUI();
        }

        private void AutoAssignPanels()
        {
            Transform root = transform.parent != null ? transform.parent : transform;
            if (startPanel == null)
            {
                var t = root.Find("StartPanel");
                if (t != null) startPanel = t.gameObject;
            }
            if (scanPromptPanel == null)
            {
                var t = root.Find("Scan Prompt Panel");
                if (t != null) scanPromptPanel = t.gameObject;
            }
            if (userNavPanel == null)
            {
                var t = root.Find("User Navigation Panel");
                if (t != null) userNavPanel = t.gameObject;
            }
            if (userNavigationUI == null)
            {
                userNavigationUI = FindFirstObjectByType<UserNavigationUI>();
            }
            if (adminPanel == null)
            {
                adminPanel = gameObject;
            }
        }

        private void Update()
        {
            // If a temporary status message is active, update timer and preserve it
            if (statusMessageTimer > 0)
            {
                statusMessageTimer -= Time.deltaTime;
                if (statsText != null)
                {
                    statsText.text = pendingStatusMessage;
                }
                return;
            }

            if (recorder != null && recorder.IsRecording && statsText != null)
            {
                float gpsAcc = AndroidSensorManager.Instance != null ? AndroidSensorManager.Instance.HorizontalAccuracy : 999f;
                string accColor = gpsAcc <= 8.0f ? "<color=green>" : (gpsAcc <= 15.0f ? "<color=yellow>" : "<color=red>");

                if (recorder.IsSampling)
                {
                    statsText.text = $"<color=cyan><b>Averaging GPS Fixes...</b> [{(recorder.SamplingProgress * 100f):F0}%]</color>\n" +
                                     $"Please stand still at destination/office entrance!";
                }
                else
                {
                    statsText.text = $"Nodes: {recorder.RecordedNodeCount} | Edges: {recorder.RecordedEdgeCount}\n" +
                                     $"GPS Accuracy: {accColor}{gpsAcc:F1}m</color> | Next Breadcrumb: {recorder.DistanceToNextDrop:F1}m / {recorder.NodeDropInterval:F1}m";
                }
            }
        }

        private void OnRecordButtonClicked()
        {
            if (recorder == null) return;

            if (!recorder.IsRecording)
            {
                // Find nearest node to suggest starting point
                FindNearestNodeForDialog();
                if (nearestNodeFound != null && nearestNodeDist < 10.0f)
                {
                    OpenDialog(AdminDialogType.StartNodeSelection);
                }
                else
                {
                    recorder.StartRecording();
                    if (recordButtonText != null) recordButtonText.text = "Stop & Save";
                }
            }
            else
            {
                // Find nearest node to suggest end link
                FindNearestNodeForDialog();
                if (nearestNodeFound != null && nearestNodeDist < 8.0f)
                {
                    OpenDialog(AdminDialogType.EndNodeSelection);
                }
                else
                {
                    recorder.StopAndSaveRecording();
                    if (recordButtonText != null) recordButtonText.text = "Start Record";
                }
            }
            UpdateUI();
        }

        private void OnJoinPathClicked()
        {
            if (recorder == null || !recorder.IsRecording) return;
            OpenDialog(AdminDialogType.JoinPathConfirm);
        }

        private void FindNearestNodeForDialog()
        {
            if (MapDataManager.Instance?.CurrentMap?.nodes == null) return;
            
            Transform cam = arCameraTransform != null ? arCameraTransform : (cachedMainCamera != null ? cachedMainCamera.transform : null);
            if (cam == null || mapRoot == null) return;

            Vector3 userLocalPos = mapRoot.InverseTransformPoint(cam.position);

            nearestNodeFound = null;
            nearestNodeDist = float.MaxValue;

            foreach (var node in MapDataManager.Instance.CurrentMap.nodes)
            {
                float d = Vector3.Distance(userLocalPos, node.localPosition);
                if (d < nearestNodeDist)
                {
                    nearestNodeDist = d;
                    nearestNodeFound = node;
                }
            }
        }

        private void OpenDialog(AdminDialogType type)
        {
            activeDialog = type;
            if (recorder != null) recorder.IsDialogOpen = (type != AdminDialogType.None);
        }

        private void OnGUI()
        {
            if (activeDialog == AdminDialogType.None) return;

            float scale = Mathf.Max(1f, Screen.width / 1080f);
            Matrix4x4 origMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

            float sw = Screen.width / scale;
            float sh = Screen.height / scale;

            Rect dialogRect = new Rect(sw * 0.1f, sh * 0.2f, sw * 0.8f, sh * 0.5f);
            GUI.Box(dialogRect, "");
            GUILayout.BeginArea(new Rect(dialogRect.x + 20, dialogRect.y + 20, dialogRect.width - 40, dialogRect.height - 40));

            GUIStyle titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            GUIStyle textStyle = new GUIStyle(GUI.skin.label) { fontSize = 24, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            GUIStyle btnStyle = new GUIStyle(GUI.skin.button) { fontSize = 26, fixedHeight = 80 };

            switch (activeDialog)
            {
                case AdminDialogType.StartNodeSelection:
                    GUILayout.Label("Start Recording", titleStyle);
                    GUILayout.Space(20);
                    GUILayout.Label($"Nearest node '{nearestNodeFound.label}' found at {nearestNodeDist:F1}m. Start from this node?", textStyle);
                    GUILayout.Space(40);
                    if (GUILayout.Button("Yes, Start from here", btnStyle))
                    {
                        recorder.StartRecording(nearestNodeFound.id);
                        if (recordButtonText != null) recordButtonText.text = "Stop & Save";
                        OpenDialog(AdminDialogType.None);
                    }
                    if (GUILayout.Button("No, Create New Point", btnStyle))
                    {
                        recorder.StartRecording();
                        if (recordButtonText != null) recordButtonText.text = "Stop & Save";
                        OpenDialog(AdminDialogType.None);
                    }
                    if (GUILayout.Button("Choose Other Node...", btnStyle))
                    {
                        OpenDialog(AdminDialogType.NodePicker);
                    }
                    if (GUILayout.Button("Cancel", btnStyle)) OpenDialog(AdminDialogType.None);
                    break;

                case AdminDialogType.EndNodeSelection:
                    GUILayout.Label("Finish Recording", titleStyle);
                    GUILayout.Space(20);
                    GUILayout.Label($"Nearest node '{nearestNodeFound.label}' found at {nearestNodeDist:F1}m. Link and end here?", textStyle);
                    GUILayout.Space(40);
                    if (GUILayout.Button("Yes, End at this node", btnStyle))
                    {
                        recorder.StopAndSaveRecording(nearestNodeFound.id);
                        if (recordButtonText != null) recordButtonText.text = "Start Record";
                        OpenDialog(AdminDialogType.None);
                    }
                    if (GUILayout.Button("No, End at current", btnStyle))
                    {
                        recorder.StopAndSaveRecording();
                        if (recordButtonText != null) recordButtonText.text = "Start Record";
                        OpenDialog(AdminDialogType.None);
                    }
                    if (GUILayout.Button("Cancel", btnStyle)) OpenDialog(AdminDialogType.None);
                    break;

                case AdminDialogType.NodePicker:
                    GUILayout.Label("Select Start Node", titleStyle);
                    pickerScrollPos = GUILayout.BeginScrollView(pickerScrollPos);
                    foreach (var node in MapDataManager.Instance.CurrentMap.nodes)
                    {
                        if (GUILayout.Button($"{node.label} ({node.id})", btnStyle))
                        {
                            recorder.StartRecording(node.id);
                            if (recordButtonText != null) recordButtonText.text = "Stop & Save";
                            OpenDialog(AdminDialogType.None);
                        }
                    }
                    GUILayout.EndScrollView();
                    if (GUILayout.Button("Back", btnStyle)) OpenDialog(AdminDialogType.StartNodeSelection);
                    break;

                case AdminDialogType.JoinPathConfirm:
                    GUILayout.Label("Join Existing Path", titleStyle);
                    GUILayout.Space(20);
                    GUILayout.Label("Split the nearest edge and connect current recording here?", textStyle);
                    GUILayout.Space(40);
                    if (GUILayout.Button("Yes, Join Path", btnStyle))
                    {
                        if (recorder.SplitNearestEdge())
                        {
                            ShowTimedStatusMessage("<color=green>Path Joined Successfully!</color>", 3.0f);
                        }
                        else
                        {
                            ShowTimedStatusMessage("<color=yellow>No edge found nearby to join.</color>", 3.0f);
                        }
                        OpenDialog(AdminDialogType.None);
                    }
                    if (GUILayout.Button("Cancel", btnStyle)) OpenDialog(AdminDialogType.None);
                    break;
            }

            GUILayout.EndArea();
            GUI.matrix = origMatrix;
        }

        private void OnMarkDestinationClicked()
        {
            if (recorder == null || !recorder.IsRecording || recorder.IsSampling || recorder.IsDialogOpen) return;

            string destName = destinationInputField != null && !string.IsNullOrEmpty(destinationInputField.text)
                ? destinationInputField.text
                : "Office/Facility";

            recorder.MarkDestination(destName);

            if (destinationInputField != null)
                destinationInputField.text = "";
        }

        public void OnLinkJunctionClicked()
        {
            if (recorder == null || !recorder.IsRecording) return;

            if (recorder.LinkToNearestJunction(out string connectedId, out float dist))
            {
                ShowTimedStatusMessage($"<color=green>Linked to {connectedId} ({dist:F1}m)!</color>", 3.0f);
            }
            else
            {
                ShowTimedStatusMessage("<color=yellow>No nearby node found within link radius.</color>", 3.0f);
            }
        }

        public void RecordQRAnchorForCurrentSession()
        {
            if (recorder == null || !recorder.IsRecording) return;
            recorder.RecordQRAnchor(null, "QR Anchor");
        }

        private void ShowTimedStatusMessage(string message, float duration)
        {
            pendingStatusMessage = message;
            statusMessageTimer = duration;
            if (statsText != null)
            {
                statsText.text = message;
            }
        }

        private void UpdateUI()
        {
            bool recording = recorder != null && recorder.IsRecording;
            if (markDestinationButton != null)
                markDestinationButton.interactable = recording;
            if (destinationInputField != null)
                destinationInputField.interactable = recording;
            if (linkJunctionButton != null)
                linkJunctionButton.interactable = recording;
        }

        public void ToggleAdminPanel()
        {
            AutoAssignPanels();

            if (adminPanel == null) return;

            bool willOpen = !adminPanel.activeSelf;

            if (willOpen)
            {
                // Remember which panel was open before opening admin
                if (startPanel != null && startPanel.activeSelf) previousActivePanel = startPanel;
                else if (scanPromptPanel != null && scanPromptPanel.activeSelf) previousActivePanel = scanPromptPanel;
                else if (userNavPanel != null && userNavPanel.activeSelf) previousActivePanel = userNavPanel;

                // Hide ALL other panels so ONLY Admin Panel is visible
                if (startPanel != null) startPanel.SetActive(false);
                if (scanPromptPanel != null) scanPromptPanel.SetActive(false);
                if (userNavPanel != null) userNavPanel.SetActive(false);

                // Show only Admin Panel on top
                adminPanel.SetActive(true);
                adminPanel.transform.SetAsLastSibling();

                // Keep Toggle Admin Button on top of Admin Panel
                Transform toggleBtn = adminPanel.transform.parent != null ? adminPanel.transform.parent.Find("Toggle Admin Button") : null;
                if (toggleBtn != null) toggleBtn.SetAsLastSibling();
            }
            else
            {
                adminPanel.SetActive(false);

                // Restore previously active panel, or default to startPanel
                if (previousActivePanel != null)
                {
                    previousActivePanel.SetActive(true);
                }
                else if (startPanel != null)
                {
                    startPanel.SetActive(true);
                }
            }
        }

        public void OnBypassToDestinationClicked()
        {
            AutoAssignPanels();

            // Close Admin Panel
            if (adminPanel != null)
                adminPanel.SetActive(false);

            // Hide other panels
            if (startPanel != null) startPanel.SetActive(false);
            if (scanPromptPanel != null) scanPromptPanel.SetActive(false);

            // Activate Destination (Search) Panel
            if (userNavigationUI != null)
            {
                userNavigationUI.ShowSearchPanel();
            }
            else if (userNavPanel != null)
            {
                userNavPanel.SetActive(true);
                var search = userNavPanel.transform.Find("Search Panel");
                if (search != null) search.gameObject.SetActive(true);
                var activeNav = userNavPanel.transform.Find("Active Nav Panel");
                if (activeNav != null) activeNav.gameObject.SetActive(false);
            }
        }

        private void OnClearMapClicked()
        {
            if (MapDataManager.Instance != null)
            {
                MapDataManager.Instance.DeleteSavedMap();
                ShowTimedStatusMessage("Map Cleared! (Bundled map returns on restart)", 3.0f);
            }
        }
}
}