using System;
using System.IO;
using CampusNavigation.Data;
using UnityEngine;
using UnityEngine.Networking;

namespace CampusNavigation.Data
{
    public class MapDataManager : MonoBehaviour
    {
        public static MapDataManager Instance { get; private set; }

        public const string MAP_FILE_NAME = "campus_map.json";

        public CampusMapData CurrentMap { get; private set; }

        public event Action<CampusMapData> OnMapLoaded;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Start()
        {
            LoadMap();
        }

        /// <summary>
        /// Saves a newly recorded map to the device's persistent storage.
        /// </summary>
        public bool SaveMapToPersistentData(CampusMapData mapData)
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, MAP_FILE_NAME);
                mapData.savedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string json = JsonUtility.ToJson(mapData, true);
                File.WriteAllText(path, json);
#if UNITY_EDITOR
                // In the Editor, also automatically save directly into StreamingAssets
                // so your build APK always has the latest recorded map!
                string streamingDir = Application.streamingAssetsPath;
                if (!Directory.Exists(streamingDir))
                {
                    Directory.CreateDirectory(streamingDir);
                }
                string streamingPath = Path.Combine(streamingDir, MAP_FILE_NAME);
                File.WriteAllText(streamingPath, json);
                if (!Application.isPlaying)
                {
                    UnityEditor.AssetDatabase.Refresh();
                }
                Debug.Log(
                    $"[MapDataManager] Editor: Auto-synced map to StreamingAssets: {streamingPath}"
                );
#endif
                CurrentMap = mapData;
                OnMapLoaded?.Invoke(CurrentMap);
                Debug.Log($"[MapDataManager] Map saved successfully to: {path}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MapDataManager] Failed to save map: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Saves recording progress without notifying listeners or changing the bundled map.
        /// </summary>
        public bool SaveMapSilently(CampusMapData mapData)
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, MAP_FILE_NAME);
                mapData.savedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string json = JsonUtility.ToJson(mapData, true);
                File.WriteAllText(path, json);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MapDataManager] Failed to silently save map: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deletes the saved map file from the device and clears the in-memory map.
        /// </summary>
        public bool DeleteSavedMap()
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, MAP_FILE_NAME);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Debug.Log($"[MapDataManager] Deleted map file at: {path}");
                }

                // Immediately revert to bundled map in memory
                LoadMap();
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MapDataManager] Failed to delete map: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deletes the persistent map and reloads the bundled one from StreamingAssets.
        /// </summary>
        public void ResetToBundledMap()
        {
            DeleteSavedMap();
        }

        /// <summary>
        /// Loads both sources, preferring the bundled map only when strictly newer.
        /// Missing savedTimestamp fields default to zero for backward compatibility.
        /// </summary>
        public void LoadMap(Action<CampusMapData> onComplete = null)
        {
            string persistentPath = Path.Combine(Application.persistentDataPath, MAP_FILE_NAME);
            CampusMapData persistentMap = null;

            // 1. Check persistent data path first (e.g. updated map or recorded on-device)
            if (File.Exists(persistentPath))
            {
                try
                {
                    string json = File.ReadAllText(persistentPath);
                    persistentMap = JsonUtility.FromJson<CampusMapData>(json);
                    if (persistentMap == null)
                        Debug.LogWarning("[MapDataManager] Persistent map parsed as null, falling back to StreamingAssets.");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[MapDataManager] Persistent map load failed, falling back to StreamingAssets: {ex.Message}"
                    );
                }
            }

            // Always read the bundled map before choosing (asynchronous on Android APK).
            StartCoroutine(LoadFromStreamingAssetsCoroutine(onComplete, persistentMap));
        }

        public void SetCurrentMap(CampusMapData mapData)
        {
            CurrentMap = mapData;
            OnMapLoaded?.Invoke(CurrentMap);
        }

        private System.Collections.IEnumerator LoadFromStreamingAssetsCoroutine(
            Action<CampusMapData> onComplete,
            CampusMapData persistentMap
        )
        {
            string streamingPath = Path.Combine(Application.streamingAssetsPath, MAP_FILE_NAME);
            string json = "";

            if (streamingPath.Contains("://") || streamingPath.Contains(":///"))
            {
                // Android requires UnityWebRequest to read from compressed APK
                using (UnityWebRequest uwr = UnityWebRequest.Get(streamingPath))
                {
                    yield return uwr.SendWebRequest();
                    if (uwr.result == UnityWebRequest.Result.Success)
                    {
                        json = uwr.downloadHandler.text;
                    }
                    else
                    {
                        Debug.LogError($"[MapDataManager] StreamingAssets read error: {uwr.error}");
                    }
                }
            }
            else
            {
                // Standalone / iOS / Editor
                if (File.Exists(streamingPath))
                {
                    try
                    {
                        json = File.ReadAllText(streamingPath);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[MapDataManager] StreamingAssets read failed: {ex.Message}");
                    }
                }
            }

            CampusMapData bundledMap = null;
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    bundledMap = JsonUtility.FromJson<CampusMapData>(json);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MapDataManager] StreamingAssets map parse failed: {ex.Message}");
                }
            }

            long persistentTimestamp = persistentMap?.savedTimestamp ?? 0;
            long bundledTimestamp = bundledMap?.savedTimestamp ?? 0;
            string winningSource;
            if (bundledMap != null &&
                (persistentMap == null || bundledTimestamp > persistentTimestamp))
            {
                CurrentMap = bundledMap;
                winningSource = "StreamingAssets";
            }
            else if (persistentMap != null)
            {
                CurrentMap = persistentMap;
                winningSource = "persistentDataPath";
            }
            else
            {
                Debug.LogWarning(
                    "[MapDataManager] No usable map found in either source. Initializing blank map."
                );
                CurrentMap = new CampusMapData();
                winningSource = "blank map";
            }

            Debug.Log($"[MapDataManager] Map source selected: {winningSource}. " +
                $"persistent savedTimestamp={persistentTimestamp}, bundled savedTimestamp={bundledTimestamp}.");
            onComplete?.Invoke(CurrentMap);
            OnMapLoaded?.Invoke(CurrentMap);
        }
    }
}
