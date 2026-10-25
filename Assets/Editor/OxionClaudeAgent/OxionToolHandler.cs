using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class OxionToolHandler
{
    // -------------------------------------------------------------------------
    // File / Project tools
    // -------------------------------------------------------------------------
    public static string ReadFile(string path)
    {
        string full = Path.Combine(Application.dataPath, "..", path).Replace("\\", "/");
        if (!File.Exists(full)) return $"ERROR: File not found: {path}";
        return File.ReadAllText(full);
    }

    public static string ListFiles(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) pattern = "Assets/**/*.cs";
        string dir = "Assets";
        string ext = ".cs";
        if (pattern.Contains("*."))
        {
            int idx = pattern.LastIndexOf("*.", StringComparison.Ordinal);
            ext = pattern.Substring(idx + 1);
        }
        var files = Directory.GetFiles(dir, "*" + ext, SearchOption.AllDirectories)
            .Select(f => f.Replace("\\", "/")).OrderBy(f => f).ToArray();
        if (files.Length > 200) files = files.Take(200).ToArray();
        return string.Join("\n", files) + $"\n({files.Length} files)";
    }

    public static string SearchProject(string query)
    {
        if (string.IsNullOrEmpty(query)) return "ERROR: query empty";
        var results = new List<string>();
        foreach (var f in Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(f);
            int idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            int line = text.Substring(0, idx).Count(c => c == '\n') + 1;
            string snippet = text.Substring(Math.Max(0, idx - 60), Math.Min(160, text.Length - Math.Max(0, idx - 60))).Replace("\n", " ").Replace("\r", "");
            results.Add($"{f.Replace("\\", "/")}:{line}: ...{snippet}...");
            if (results.Count >= 40) break;
        }
        if (results.Count == 0) return $"No matches for '{query}'";
        return string.Join("\n", results);
    }

    public static string WriteFile(string path, string content)
    {
        if (string.IsNullOrEmpty(path)) return "ERROR: path empty";
        string full = Path.Combine(Application.dataPath, "..", path).Replace("\\", "/");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content, new UTF8Encoding(false));
            AssetDatabase.Refresh();
            return $"Wrote {path} ({content.Length} chars)";
        }
        catch (Exception e) { return $"ERROR: {e.Message}"; }
    }

    public static string EditFile(string path, string oldString, string newString)
    {
        if (string.IsNullOrEmpty(path) || oldString == null) return "ERROR: path/oldString empty";
        string full = Path.Combine(Application.dataPath, "..", path).Replace("\\", "/");
        if (!File.Exists(full)) return $"ERROR: File not found: {path}";
        string text = File.ReadAllText(full);
        if (!text.Contains(oldString)) return "ERROR: oldString not found in file — read file first and copy exact text including whitespace";
        if (oldString == newString) return "ERROR: oldString == newString";
        File.WriteAllText(full, text.Replace(oldString, newString), new UTF8Encoding(false));
        AssetDatabase.Refresh();
        return $"Edited {path} (replaced {oldString.Length} chars)";
    }

    public static string DeleteFile(string path)
    {
        string full = Path.Combine(Application.dataPath, "..", path).Replace("\\", "/");
        if (File.Exists(full)) { File.Delete(full); File.Delete(full + ".meta"); AssetDatabase.Refresh(); return $"Deleted {path}"; }
        if (Directory.Exists(full)) { Directory.Delete(full, true); File.Delete(full.TrimEnd('/', '\\') + ".meta"); AssetDatabase.Refresh(); return $"Deleted folder {path}"; }
        return $"ERROR: Not found: {path}";
    }

    // -------------------------------------------------------------------------
    // Diagnostics
    // -------------------------------------------------------------------------
    public static string GetConsoleErrors()
    {
        var sb = new StringBuilder();
        // Unity doesn't expose console log directly; use the LogEntries internal API
        try
        {
            var logEntries = Type.GetType("UnityEditor.LogEntries, UnityEditor");
            if (logEntries != null)
            {
                var getCount = logEntries.GetMethod("GetCount");
                var getEntry = logEntries.GetMethod("GetEntryInternal");
                var clear = logEntries.GetMethod("Clear");
                int count = (int)getCount.Invoke(null, null);
                sb.AppendLine($"Console entries: {count}");
                // Use reflection struct LogEntry
                var entryType = Type.GetType("UnityEditor.LogEntry, UnityEditor");
                if (entryType != null && count > 0)
                {
                    object entry = Activator.CreateInstance(entryType);
                    var messageField = entryType.GetField("message");
                    var modeField = entryType.GetField("mode");
                    int show = Math.Min(30, count);
                    for (int i = Math.Max(0, count - show); i < count; i++)
                    {
                        getEntry.Invoke(null, new object[] { i, entry });
                        string msg = (string)messageField.GetValue(entry);
                        int mode = (int)modeField.GetValue(entry);
                        // mode 2 = error
                        string tag = mode == 2 ? "[ERROR]" : mode == 1 ? "[WARN]" : "[LOG]";
                        sb.AppendLine($"{tag} {msg.Split('\n')[0].Trim()}");
                    }
                }
            }
            else sb.AppendLine("LogEntries not available");
        }
        catch (Exception e) { sb.AppendLine($"ERROR reading console: {e.Message}"); }
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Scene helpers
    // -------------------------------------------------------------------------
    static GameObject FindGo(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var go = GameObject.Find(path);
        if (go != null) return go;
        // fallback: find by name anywhere
        var all = Resources.FindObjectsOfTypeAll<GameObject>();
        return all.FirstOrDefault(g => g.name == path && g.hideFlags == HideFlags.None);
    }

    static Type ResolveType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;
        var t = Type.GetType(typeName);
        if (t != null) return t;
        t = Type.GetType(typeName + ", Assembly-CSharp");
        if (t != null) return t;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            t = asm.GetType(typeName);
            if (t != null) return t;
        }
        // UnityEngine namespace fallback
        t = Type.GetType("UnityEngine." + typeName + ", UnityEngine.CoreModule");
        if (t != null) return t;
        return null;
    }

    public static string SceneGetHierarchy()
    {
        var scene = SceneManager.GetActiveScene();
        if (!scene.IsValid()) return "ERROR: No active scene";
        var sb = new StringBuilder();
        sb.AppendLine($"Active scene: {scene.path} ({scene.name})  objects:{scene.rootCount}");
        var roots = scene.GetRootGameObjects();
        int count = 0;
        void Dump(Transform tr, int depth)
        {
            if (count++ > 300) return;
            string indent = new string(' ', depth * 2);
            var comps = tr.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name);
            sb.AppendLine($"{indent}- {tr.name} [{string.Join(", ", comps)}]");
            foreach (Transform child in tr) Dump(child, depth + 1);
        }
        foreach (var r in roots.OrderBy(g => g.name)) Dump(r.transform, 0);
        if (count > 300) sb.AppendLine("... truncated");
        return sb.ToString();
    }

    public static string SceneFindObject(string name)
    {
        if (string.IsNullOrEmpty(name)) return "ERROR: name empty";
        var all = Resources.FindObjectsOfTypeAll<Transform>()
            .Where(t => t.gameObject.scene.IsValid() && t.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            .Take(20).ToList();
        if (all.Count == 0) return $"No GameObjects matching '{name}' in open scenes";
        var sb = new StringBuilder();
        foreach (var tr in all) sb.AppendLine(GetPath(tr));
        return sb.ToString();
    }

    static string GetPath(Transform tr)
    {
        var parts = new List<string>();
        while (tr != null) { parts.Add(tr.name); tr = tr.parent; }
        parts.Reverse();
        return string.Join("/", parts);
    }

    public static string SceneCreate(string name, string parentPath)
    {
        if (string.IsNullOrEmpty(name)) return "ERROR: name empty";
        var go = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        if (!string.IsNullOrEmpty(parentPath))
        {
            var parent = FindGo(parentPath);
            if (parent != null) go.transform.SetParent(parent.transform, false);
            else return $"WARNING: Parent '{parentPath}' not found — created '{name}' at root";
        }
        EditorUtility.SetDirty(go);
        return $"Created GameObject: {GetPath(go.transform)}";
    }

    public static string SceneDuplicate(string goPath)
    {
        var go = FindGo(goPath);
        if (go == null) return $"ERROR: GameObject not found: {goPath}";
        var dup = UnityEngine.Object.Instantiate(go, go.transform.parent);
        dup.name = go.name + " (Clone)";
        Undo.RegisterCreatedObjectUndo(dup, "Duplicate " + go.name);
        return $"Duplicated {goPath} -> {GetPath(dup.transform)}";
    }

    public static string SceneDelete(string goPath)
    {
        var go = FindGo(goPath);
        if (go == null) return $"ERROR: GameObject not found: {goPath}";
        Undo.DestroyObjectImmediate(go);
        return $"Deleted {goPath}";
    }

    public static string SceneAddComponent(string goPath, string componentType)
    {
        var go = FindGo(goPath);
        if (go == null) return $"ERROR: GameObject not found: {goPath}";
        var type = ResolveType(componentType);
        if (type == null) return $"ERROR: Type not found: {componentType}";
        if (go.GetComponent(type) != null) return $"NOTE: {goPath} already has {componentType}";
        Undo.AddComponent(go, type);
        EditorUtility.SetDirty(go);
        return $"Added {type.Name} to {goPath}";
    }

    public static string SceneSetTransform(string goPath, string pos, string rot, string scale)
    {
        var go = FindGo(goPath);
        if (go == null) return $"ERROR: GameObject not found: {goPath}";
        Undo.RecordObject(go.transform, "Set Transform " + go.name);
        if (!string.IsNullOrEmpty(pos) && TryVec3(pos, out var p)) go.transform.localPosition = p;
        if (!string.IsNullOrEmpty(rot) && TryVec3(rot, out var r)) go.transform.localEulerAngles = r;
        if (!string.IsNullOrEmpty(scale) && TryVec3(scale, out var s)) go.transform.localScale = s;
        EditorUtility.SetDirty(go);
        return $"Set transform {goPath} pos={go.transform.localPosition} rot={go.transform.localEulerAngles} scale={go.transform.localScale}";
    }

    static bool TryVec3(string s, out Vector3 v)
    {
        v = default;
        var parts = s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;
        if (float.TryParse(parts[0], out var x) && float.TryParse(parts[1], out var y) && float.TryParse(parts[2], out var z)) { v = new Vector3(x, y, z); return true; }
        return false;
    }

    public static string SceneSetProperty(string goPath, string componentType, string fieldName, string value)
    {
        var go = FindGo(goPath);
        if (go == null) return $"ERROR: GameObject not found: {goPath}";
        var type = ResolveType(componentType);
        if (type == null) return $"ERROR: Type not found: {componentType}";
        var comp = go.GetComponent(type);
        if (comp == null) return $"ERROR: Component {componentType} not on {goPath}";
        var so = new SerializedObject(comp);
        var prop = so.FindProperty(fieldName);
        if (prop == null) return $"ERROR: Field '{fieldName}' not found on {componentType} (check Inspector name)";
        Undo.RecordObject(comp, $"Set {fieldName}");
        bool ok = false;
        switch (prop.propertyType)
        {
            case SerializedPropertyType.Float: if (float.TryParse(value, out var f)) { prop.floatValue = f; ok = true; } break;
            case SerializedPropertyType.Integer: if (int.TryParse(value, out var iv)) { prop.intValue = iv; ok = true; } break;
            case SerializedPropertyType.Boolean: if (bool.TryParse(value, out var bv)) { prop.boolValue = bv; ok = true; } else if (value == "0") { prop.boolValue = false; ok = true; } else if (value == "1") { prop.boolValue = true; ok = true; } break;
            case SerializedPropertyType.String: prop.stringValue = value; ok = true; break;
            case SerializedPropertyType.Vector3: if (TryVec3(value, out var v3)) { prop.vector3Value = v3; ok = true; } break;
            case SerializedPropertyType.Color: if (ColorUtility.TryParseHtmlString(value, out var col)) { prop.colorValue = col; ok = true; } break;
            case SerializedPropertyType.Enum: if (int.TryParse(value, out var ei)) { prop.enumValueIndex = ei; ok = true; } break;
        }
        if (!ok) return $"ERROR: Cannot set '{fieldName}' ({prop.propertyType}) with value '{value}'";
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(comp);
        return $"Set {goPath}.{componentType}.{fieldName} = {value}";
    }

    public static string SceneWire(string sourcePath, string sourceComponent, string fieldName, string targetPath)
    {
        var src = FindGo(sourcePath);
        if (src == null) return $"ERROR: Source not found: {sourcePath}";
        var dst = FindGo(targetPath);
        if (dst == null) return $"ERROR: Target not found: {targetPath}";
        var srcType = ResolveType(sourceComponent);
        if (srcType == null) return $"ERROR: Source type not found: {sourceComponent}";
        var comp = src.GetComponent(srcType);
        if (comp == null) return $"ERROR: Component {sourceComponent} not on {sourcePath}";
        var so = new SerializedObject(comp);
        var prop = so.FindProperty(fieldName);
        if (prop == null) return $"ERROR: Field '{fieldName}' not found on {sourceComponent}";
        if (prop.propertyType != SerializedPropertyType.ObjectReference) return $"ERROR: Field '{fieldName}' is {prop.propertyType}, not ObjectReference — use scene_set_property for value types";
        Undo.RecordObject(comp, $"Wire {fieldName}");
        // Prefer component of requested type, else GameObject or first component
        UnityEngine.Object targetObj = dst;
        var dstComp = dst.GetComponent(srcType);
        if (dstComp == null)
        {
            // Try to infer from property type
            var fi = srcType.GetField(fieldName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (fi != null)
            {
                var ft = fi.FieldType;
                var found = dst.GetComponent(ft);
                if (found != null) targetObj = found;
            }
        }
        else targetObj = dstComp;
        prop.objectReferenceValue = targetObj;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(comp);
        return $"Wired {sourcePath}.{sourceComponent}.{fieldName} -> {targetPath} ({targetObj.GetType().Name})";
    }

    public static string SceneInstantiatePrefab(string prefabPath, string parentPath)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null) return $"ERROR: Prefab not found at {prefabPath}";
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        Undo.RegisterCreatedObjectUndo(instance, "Instantiate " + prefab.name);
        if (!string.IsNullOrEmpty(parentPath))
        {
            var parent = FindGo(parentPath);
            if (parent != null) instance.transform.SetParent(parent.transform, false);
        }
        return $"Instantiated {prefabPath} -> {GetPath(instance.transform)}";
    }

    public static string CreatePrefab(string goPath, string prefabPath)
    {
        var go = FindGo(goPath);
        if (go == null) return $"ERROR: GameObject not found: {goPath}";
        if (string.IsNullOrEmpty(prefabPath)) return "ERROR: prefabPath empty";
        string full = "Assets/" + prefabPath.TrimStart('/', '\\').Replace("\\", "/");
        if (!full.EndsWith(".prefab")) full += ".prefab";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Application.dataPath, "..", full)).Replace("\\", "/"));
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, full);
        if (prefab == null) return $"ERROR: SaveAsPrefabAsset failed: {full}";
        AssetDatabase.Refresh();
        return $"Created prefab {full} from {goPath}";
    }

    public static string CreateMaterial(string name, string shaderName, string colorHex)
    {
        if (string.IsNullOrEmpty(name)) return "ERROR: name empty";
        string path = $"Assets/Materials/{name}.mat";
        Directory.CreateDirectory(Path.Combine(Application.dataPath, "..", "Assets/Materials").Replace("\\", "/"));
        var mat = new Material(Shader.Find(string.IsNullOrEmpty(shaderName) ? "Universal Render Pipeline/Lit" : shaderName));
        if (!string.IsNullOrEmpty(colorHex) && ColorUtility.TryParseHtmlString(colorHex, out var col)) mat.SetColor("_BaseColor", col);
        AssetDatabase.CreateAsset(mat, path);
        AssetDatabase.Refresh();
        return $"Created material {path}";
    }

    public static string SetTag(string tagName)
    {
        if (string.IsNullOrEmpty(tagName)) return "ERROR: tagName empty";
        var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var tagsProp = tagManager.FindProperty("tags");
        for (int i = 0; i < tagsProp.arraySize; i++) if (tagsProp.GetArrayElementAtIndex(i).stringValue == tagName) return $"Tag '{tagName}' already exists";
        tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
        tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = tagName;
        tagManager.ApplyModifiedProperties();
        return $"Added tag '{tagName}'";
    }

    public static string ExecuteMenu(string menuPath)
    {
        if (string.IsNullOrEmpty(menuPath)) return "ERROR: menuPath empty";
        bool ok = EditorApplication.ExecuteMenuItem(menuPath);
        return ok ? $"Executed menu: {menuPath}" : $"ERROR: Menu not found or disabled: {menuPath}";
    }

    public static string SceneSave()
    {
        EditorSceneManager.SaveOpenScenes();
        return "Saved open scenes";
    }
}
