using System;
using System.Collections.Generic;
using UnityEngine;

namespace CampusNavigation.Data
{
    [Serializable]
    public class LocationInfo
    {
        public string locationName;
        public string openDays = "Monday - Friday";
        public string officeHours = "8:00 AM - 5:00 PM";
        [TextArea(2, 4)]
        public string additionalInfo = "";
    }

    [CreateAssetMenu(fileName = "CampusLocationDatabase", menuName = "Campus Navigation/Location Database")]
    public class CampusLocationDatabase : ScriptableObject
    {
        [SerializeField]
        public List<LocationInfo> locations = new List<LocationInfo>();

        public LocationInfo GetLocationInfo(string targetName)
        {
            if (string.IsNullOrEmpty(targetName) || locations == null) return null;

            string cleanTarget = targetName.Trim();
            
            // 1. Exact match (case-insensitive)
            foreach (var loc in locations)
            {
                if (string.Equals(loc.locationName?.Trim(), cleanTarget, StringComparison.OrdinalIgnoreCase))
                {
                    return loc;
                }
            }

            // 2. Partial containment match (e.g., target "Library 1st floor" matches database "Library")
            foreach (var loc in locations)
            {
                if (!string.IsNullOrEmpty(loc.locationName) && 
                    (cleanTarget.IndexOf(loc.locationName.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 ||
                     loc.locationName.Trim().IndexOf(cleanTarget, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return loc;
                }
            }

            return null;
        }
    }
}
