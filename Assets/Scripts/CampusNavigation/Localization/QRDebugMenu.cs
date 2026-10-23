using CampusNavigation.Data;
using CampusNavigation.Localization;
using CampusNavigation.Pathfinding;
using CampusNavigation.Admin;
using UnityEngine;

public class QRDebugMenu : MonoBehaviour
{
    [SerializeField]
    private bool showDebugMenu = true;

    #if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void OnGUI()
        {
            if (!showDebugMenu)
                return;

            // Custom styles for large touch-friendly buttons and readable text
            GUIStyle buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 22,
                alignment = TextAnchor.MiddleCenter,
            };

            GUIStyle headerStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };

            GUIStyle statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Italic,
                alignment = TextAnchor.MiddleCenter
            };

            GUIStyle labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 20 };

            // Main Debug Area
            GUILayout.BeginArea(new Rect(20, 120, 480, 800));
        
            GUILayout.Label("AR NAVIGATION DEBUG", headerStyle, GUILayout.Height(50));
        
            // Relocalization Status
            bool isReloc = QRAnchorRelocalizer.Instance != null && QRAnchorRelocalizer.Instance.IsRelocalized;
            string status = isReloc ? $"<color=green>✓ RELOCALIZED ({QRAnchorRelocalizer.Instance.ActiveAnchorId})</color>" : "<color=red>✗ NOT RELOCALIZED</color>";
            GUILayout.Label(status, statusStyle, GUILayout.Height(40));

            if (GUILayout.Button("Reset Relocalization", buttonStyle, GUILayout.Height(50)))
            {
                QRAnchorRelocalizer.Instance?.ResetRelocalization();
            }
        
            GUILayout.Space(15);

            // Recording Actions (If in Editor)
            var recorder = CampusNavigation.Admin.NodeRecorder.Instance;
            if (recorder != null && recorder.IsRecording)
            {
                GUILayout.Label("<b>Recording Controls</b>", labelStyle);
                if (GUILayout.Button("Simulate Record QR Anchor", buttonStyle, GUILayout.Height(60)))
                {
                    recorder.RecordQRAnchor("DEBUG_QR_PAYLOAD", "Debug Anchor");
                }
                GUILayout.Space(15);
            }

            GUILayout.Label("<b>Simulate QR Scan at Node:</b>", labelStyle);

            if (MapDataManager.Instance?.CurrentMap?.nodes == null)
            {
                GUILayout.Label("No map loaded.", labelStyle);
                GUILayout.EndArea();
                return;
            }

            // List nodes to snap to
            foreach (var node in MapDataManager.Instance.CurrentMap.nodes)
            {
                if (node.nodeType == NodeType.Destination || node.nodeType == NodeType.QRAnchor)
                {
                    if (GUILayout.Button($"Scan: {node.label}", buttonStyle, GUILayout.Height(60)))
                    {
                        SimulateScan(node);
                    }
                    GUILayout.Space(8);
                }
            }

            GUILayout.EndArea();
        }

        private void SimulateScan(CampusNode node)
        {
            if (QRAnchorRelocalizer.Instance != null && Camera.main != null && MapDataManager.Instance?.CurrentMap != null)
            {
                Transform cam = Camera.main.transform;
                var map = MapDataManager.Instance.CurrentMap;

                // Find the direction the path leaves this node towards the first neighbor
                float pathYaw = node.qrFacingEuler.y;
            
                // Find neighbors from the edge list
                CampusEdge neighborEdge = map.edges.Find(e => e.fromNodeId == node.id || e.toNodeId == node.id);
                if (neighborEdge != null)
                {
                    string otherId = neighborEdge.fromNodeId == node.id ? neighborEdge.toNodeId : neighborEdge.fromNodeId;
                    CampusNode otherNode = map.nodes.Find(n => n.id == otherId);
                    if (otherNode != null)
                    {
                        Vector3 toNext = (otherNode.localPosition - node.localPosition).normalized;
                        if (toNext.sqrMagnitude > 0.001f)
                        {
                            pathYaw = Mathf.Atan2(toNext.x, toNext.z) * Mathf.Rad2Deg;
                        }
                    }
                }

                // Create a temporary orientation facing the path heading
                CampusNode alignedNode = new CampusNode
                {
                    id = node.id,
                    label = node.label,
                    localPosition = node.localPosition,
                    qrFacingEuler = new Vector3(0f, pathYaw, 0f)
                };

                Debug.Log($"[Debug] Simulating scan at: {node.label} (Aligning forward towards first neighbor)");
                QRAnchorRelocalizer.Instance.RelocalizeMapRoot(cam, alignedNode);
            }
        }
    #endif
}
