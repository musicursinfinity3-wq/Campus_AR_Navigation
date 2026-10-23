using System;
using System.Collections.Generic;
using UnityEngine;

namespace CampusNavigation.Data
{
    /// <summary>
    /// Type of node in the campus network.
    /// </summary>
    public enum NodeType
    {
        Waypoint,       // Standard breadcrumb node along a sidewalk
        Intersection,   // Fork or junction where paths split/join
        Destination,    // Notable landmark (e.g., "Library", "Cafeteria", "Room 101")
        QRAnchor        // Physical calibration point with an attached QR code
    }

    [Serializable]
    public class CampusNode
    {
        public string id;               // Unique ID: e.g. "node_001" or "qr_hall_a"
        public string label;            // Human-readable name (e.g. "Science Hall Entrance")
        public NodeType nodeType;       // Classification of the node
        
        // Local metric coordinates in meters (X = East, Y = Altitude, Z = North)
        public Vector3 localPosition;
        
        // Native hardware GPS coordinates sampled at this location
        public double latitude;
        public double longitude;
        public float altitude;

        // GPS Accuracy and Sampling Metadata
        public float horizontalAccuracy;  // GPS accuracy in meters at recording time
        public int sampleCount;            // Number of GPS samples averaged
        public long recordedTimestamp;     // Unix timestamp (ms) when node was recorded

        // QR Anchor data (populated only if nodeType == NodeType.QRAnchor)
        public string qrPayload;        // Decoded string / Reference image name
        public Vector3 qrFacingEuler;   // Expected orientation/yaw (y component stores horizontal heading in degrees)
        public int anchorVersion = 0;   // 0 = legacy (camera Euler yaw), 2 = wall-normal projected in MapRoot space
    }

    [Serializable]
    public class CampusEdge
    {
        public string id;               // e.g. "edge_001_002"
        public string fromNodeId;
        public string toNodeId;
        public float distance;          // Distance in meters (edge weight for A*)
        public bool isAccessible;       // Wheelchair / ramp accessible
        public bool isStairs;           // Mark if path involves stairs
    }

    [Serializable]
    public class CampusMapData
    {
        public string mapVersion = "1.0.0";
        public string campusName = "Main Campus";
        
        // Origin GPS point that corresponds to local (0, 0, 0)
        public double originLatitude;
        public double originLongitude;
        public float originAltitude;

        public List<CampusNode> nodes = new List<CampusNode>();
        public List<CampusEdge> edges = new List<CampusEdge>();

        /// <summary>
        /// Converts GPS (lat, lon) to local metric coordinates (X = East, Z = North in meters)
        /// relative to this map's origin GPS coordinate.
        /// </summary>
        public Vector3 GPSToLocal(double lat, double lon)
        {
            return GPSToLocalMeters(lat, lon, originLatitude, originLongitude);
        }

        /// <summary>
        /// Converts GPS (lat, lon) to metric coordinates relative to an arbitrary origin using equirectangular projection.
        /// Accurate to centimeters over campus scales (up to ~10 km).
        /// </summary>
        public static Vector3 GPSToLocalMeters(double lat, double lon, double originLat, double originLon)
        {
            const double MetersPerDegreeLat = 111132.954;
            double latRad = originLat * Math.PI / 180.0;
            double metersPerDegreeLon = 111412.84 * Math.Cos(latRad);

            float east = (float)((lon - originLon) * metersPerDegreeLon);
            float north = (float)((lat - originLat) * MetersPerDegreeLat);

            return new Vector3(east, 0f, north);
        }

        /// <summary>
        /// Calculates great-circle surface distance in meters between two GPS coordinates using Haversine formula.
        /// </summary>
        public static float CalculateDistanceMeters(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371000.0; // Earth's radius in meters
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;

            double a = Math.Sin(dLat / 2.0) * Math.Sin(dLat / 2.0) +
                       Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                       Math.Sin(dLon / 2.0) * Math.Sin(dLon / 2.0);

            double c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));
            return (float)(R * c);
        }

        /// <summary>
        /// Current QR anchor convention version for wall-mounted image markers.
        /// Version 2 stores heading and position in MapRoot-local space using projected wall normals.
        /// </summary>
        public const int CURRENT_ANCHOR_VERSION = 2;

        /// <summary>
        /// Computes the horizontal facing heading in degrees [-180, 180] from a marker's outward normal.
        /// If mapRoot is null, calculates in World Space (used at runtime on detected markers).
        /// If mapRoot is provided, calculates in MapRoot Local Space (used at recording time).
        /// </summary>
        public static float ComputeMarkerHeading(Transform markerTransform, Transform mapRoot, out bool isFlat)
        {
            isFlat = false;
            if (markerTransform == null)
                return 0f;

            // In ARFoundation ARTrackedImage, transform.up points perpendicular OUT of the image surface.
            Vector3 outwardNormal = markerTransform.up;
            Vector3 topEdge = markerTransform.forward;

            Vector3 referenceNormal;
            Vector3 referenceTop;
            Vector3 upAxis;
            Vector3 forwardRef;

            if (mapRoot != null)
            {
                // In MapRoot local space
                referenceNormal = mapRoot.InverseTransformDirection(outwardNormal);
                referenceTop = mapRoot.InverseTransformDirection(topEdge);
                upAxis = Vector3.up;
                forwardRef = Vector3.forward;
            }
            else
            {
                // In World Space
                referenceNormal = outwardNormal;
                referenceTop = topEdge;
                upAxis = Vector3.up;
                forwardRef = Vector3.forward;
            }

            Vector3 projected = Vector3.ProjectOnPlane(referenceNormal, upAxis);
            if (projected.sqrMagnitude < 0.01f)
            {
                // Marker lies flat (normal is nearly vertical)
                isFlat = true;
                Vector3 projectedTop = Vector3.ProjectOnPlane(referenceTop, upAxis);
                if (projectedTop.sqrMagnitude < 0.001f)
                {
                    return 0f;
                }
                projected = projectedTop.normalized;
            }
            else
            {
                projected.Normalize();
            }

            return Vector3.SignedAngle(forwardRef, projected, upAxis);
        }

        /// <summary>
        /// Validates QR anchors in the map. Logs warnings for any legacy (v0) anchors.
        /// </summary>
        public static bool ValidateMapAnchors(CampusMapData mapData, out int legacyCount)
        {
            legacyCount = 0;
            if (mapData == null || mapData.nodes == null)
                return true;

            foreach (var node in mapData.nodes)
            {
                if (node.nodeType == NodeType.QRAnchor && node.anchorVersion < CURRENT_ANCHOR_VERSION)
                {
                    legacyCount++;
                    Debug.LogWarning(
                        $"<color=yellow>[CampusMapData] LEGACY QR ANCHOR DETECTED:</color> '{node.label}' ({node.id}, payload: '{node.qrPayload}') was recorded with legacy convention (v{node.anchorVersion}). " +
                        $"It cannot be used for wall-normal relocalization until re-recorded with v{CURRENT_ANCHOR_VERSION}."
                    );
                }
            }

            return legacyCount == 0;
        }

        /// <summary>
        /// Calculates true distance in meters between two nodes. Prefers GPS if valid non-zero
        /// coordinates are present on both nodes, otherwise falls back to 3D localPosition Euclidean distance.
        /// </summary>
        public static float CalculateNodeDistance(CampusNode a, CampusNode b)
        {
            if (a == null || b == null) return float.MaxValue;

            if (Math.Abs(a.latitude) > 0.0001 && Math.Abs(b.latitude) > 0.0001 &&
                Math.Abs(a.longitude) > 0.0001 && Math.Abs(b.longitude) > 0.0001)
            {
                return CalculateDistanceMeters(a.latitude, a.longitude, b.latitude, b.longitude);
            }

            return Vector3.Distance(a.localPosition, b.localPosition);
        }
    }
}