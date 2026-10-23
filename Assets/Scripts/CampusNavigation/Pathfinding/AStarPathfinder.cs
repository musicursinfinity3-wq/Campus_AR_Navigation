using System.Collections.Generic;
using UnityEngine;
using CampusNavigation.Data;

namespace CampusNavigation.Pathfinding
{
    public class AStarPathfinder : MonoBehaviour
    {
        public static AStarPathfinder Instance { get; private set; }
        public event System.Action OnGraphInitialized;
        public bool IsGraphReady { get; private set; }

        private CampusMapData activeMap;
// Fast adjacency lookup: nodeId -> list of connected edges
        private Dictionary<string, List<CampusEdge>> adjacencyList = new Dictionary<string, List<CampusEdge>>();
        // Fast node lookup: nodeId -> CampusNode
        private Dictionary<string, CampusNode> nodeLookup = new Dictionary<string, CampusNode>();

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
            // Subscribe to map loading events if MapDataManager is present
            if (MapDataManager.Instance != null)
            {
                MapDataManager.Instance.OnMapLoaded += InitializeGraph;
                if (MapDataManager.Instance.CurrentMap != null)
                {
                    InitializeGraph(MapDataManager.Instance.CurrentMap);
                }
            }
        }

        private void OnDestroy()
        {
            if (MapDataManager.Instance != null)
            {
                MapDataManager.Instance.OnMapLoaded -= InitializeGraph;
            }
        }

        /// <summary>
        /// Builds the in-memory graph index from the loaded campus map.
        /// </summary>
        public void InitializeGraph(CampusMapData mapData)
        {
            if (mapData == null) return;

            activeMap = mapData;
            adjacencyList.Clear();
            nodeLookup.Clear();

            // Index all nodes
            foreach (var node in mapData.nodes)
            {
                if (!nodeLookup.ContainsKey(node.id))
                {
                    nodeLookup.Add(node.id, node);
                    adjacencyList.Add(node.id, new List<CampusEdge>());
                }
            }

            // Build bidirectional adjacency connections safely
            foreach (var edge in mapData.edges)
            {
                // Safety check: skip invalid edges where either node does not exist
                if (!nodeLookup.ContainsKey(edge.fromNodeId) || !nodeLookup.ContainsKey(edge.toNodeId))
                {
                    Debug.LogWarning($"[AStarPathfinder] Skipping edge '{edge.id}': Node '{edge.fromNodeId}' or '{edge.toNodeId}' missing from map.");
                    continue;
                }

                adjacencyList[edge.fromNodeId].Add(edge);

                // Add reverse link
                CampusEdge reverseEdge = new CampusEdge
                {
                    id = edge.id + "_rev",
                    fromNodeId = edge.toNodeId,
                    toNodeId = edge.fromNodeId,
                    distance = edge.distance,
                    isAccessible = edge.isAccessible,
                    isStairs = edge.isStairs
                };
                adjacencyList[edge.toNodeId].Add(reverseEdge);
            }

            Debug.Log($"[AStarPathfinder] Graph initialized with {nodeLookup.Count} nodes and {mapData.edges.Count} paths.");
            IsGraphReady = true;
            OnGraphInitialized?.Invoke();
        }

        /// <summary>
        /// Finds the shortest path between startNodeId and targetNodeId using A*.
        /// Returns an ordered list of CampusNodes from start to destination.
        /// </summary>
        public List<CampusNode> FindPath(string startNodeId, string targetNodeId, bool requireAccessible = false)
        {
            if (!nodeLookup.ContainsKey(startNodeId) || !nodeLookup.ContainsKey(targetNodeId))
            {
                Debug.LogWarning($"[AStarPathfinder] Start ({startNodeId}) or Target ({targetNodeId}) node not found in graph.");
                return null;
            }

            CampusNode targetNode = nodeLookup[targetNodeId];

            // Open set: nodes to be evaluated
            List<string> openSet = new List<string> { startNodeId };
            HashSet<string> closedSet = new HashSet<string>();

            // Cost from start to current node along best known path
            Dictionary<string, float> gScore = new Dictionary<string, float>();
            gScore[startNodeId] = 0f;

            // Estimated total cost from start to goal through current node: f = g + h
            Dictionary<string, float> fScore = new Dictionary<string, float>();
            fScore[startNodeId] = Heuristic(nodeLookup[startNodeId].localPosition, targetNode.localPosition);

            // Breadcrumb map for path reconstruction
            Dictionary<string, string> cameFrom = new Dictionary<string, string>();

            while (openSet.Count > 0)
            {
                // Select node in openSet with lowest fScore
                string currentId = openSet[0];
                float lowestF = fScore.ContainsKey(currentId) ? fScore[currentId] : float.MaxValue;
                for (int i = 1; i < openSet.Count; i++)
                {
                    string id = openSet[i];
                    float f = fScore.ContainsKey(id) ? fScore[id] : float.MaxValue;
                    if (f < lowestF)
                    {
                        lowestF = f;
                        currentId = id;
                    }
                }

                // Reached target destination
                if (currentId == targetNodeId)
                {
                    return ReconstructPath(cameFrom, currentId);
                }

                openSet.Remove(currentId);
                closedSet.Add(currentId);

                // Evaluate all neighbors
                if (!adjacencyList.ContainsKey(currentId)) continue;

                foreach (var edge in adjacencyList[currentId])
                {
                    string neighborId = edge.toNodeId;
                    if (!nodeLookup.ContainsKey(neighborId)) continue;
                    if (closedSet.Contains(neighborId)) continue;
                    if (requireAccessible && !edge.isAccessible) continue;

                    float tentativeGScore = gScore[currentId] + edge.distance;

                    if (!gScore.ContainsKey(neighborId) || tentativeGScore < gScore[neighborId])
                    {
                        cameFrom[neighborId] = currentId;
                        gScore[neighborId] = tentativeGScore;

                        float h = Heuristic(nodeLookup[neighborId].localPosition, targetNode.localPosition);
                        fScore[neighborId] = tentativeGScore + h;

                        if (!openSet.Contains(neighborId))
                        {
                            openSet.Add(neighborId);
                        }
                    }
                }
            }

            Debug.LogWarning($"[AStarPathfinder] No path found between {startNodeId} and {targetNodeId}.");
            return null;
        }

        /// <summary>
        /// Helper to find the closest node in the map to a 3D position (e.g. current AR Camera position).
        /// </summary>
        public CampusNode FindNearestNode(Vector3 localPosition)
        {
            if (activeMap == null || activeMap.nodes.Count == 0) return null;

            CampusNode closest = null;
            float minDistanceSqr = float.MaxValue;

            foreach (var node in activeMap.nodes)
            {
                // Exclude QRAnchor nodes from nearest-node searches for navigation positioning
                if (node.nodeType == NodeType.QRAnchor)
                    continue;

                // Compare MapRoot-Local to MapRoot-Local
float distSqr = (node.localPosition - localPosition).sqrMagnitude;
                if (distSqr < minDistanceSqr)
                {
                    minDistanceSqr = distSqr;
                    closest = node;
                }
            }

            return closest;
        }

        /// <summary>
        /// Returns all nodes designated as destinations (for populating destination UI dropdowns).
        /// </summary>
        public List<CampusNode> GetAllDestinations()
        {
            List<CampusNode> destinations = new List<CampusNode>();
            if (activeMap == null) return destinations;

            foreach (var node in activeMap.nodes)
            {
                if (node.nodeType == NodeType.Destination)
                {
                    destinations.Add(node);
                }
            }
            return destinations;
        }

        private float Heuristic(Vector3 a, Vector3 b)
        {
            // Standard 3D Euclidean distance
            return Vector3.Distance(a, b);
        }

        private List<CampusNode> ReconstructPath(Dictionary<string, string> cameFrom, string currentId)
        {
            List<CampusNode> totalPath = new List<CampusNode>();
            if (nodeLookup.ContainsKey(currentId))
            {
                totalPath.Add(nodeLookup[currentId]);
            }

            while (cameFrom.ContainsKey(currentId))
            {
                currentId = cameFrom[currentId];
                if (nodeLookup.ContainsKey(currentId))
                {
                    totalPath.Insert(0, nodeLookup[currentId]);
                }
            }

            return totalPath;
        }
    }
}