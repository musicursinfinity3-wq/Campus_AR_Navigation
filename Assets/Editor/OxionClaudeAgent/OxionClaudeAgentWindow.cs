using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

public class OxionClaudeAgentWindow : EditorWindow
{
    const string Endpoint = "https://oxionai.com/v1/chat/completions";
    const string ModelsEndpoint = "https://oxionai.com/v1/models";
    const string PrefKey = "Oxion_ApiKey";
    const string PrefModel = "Oxion_ModelIndex";
    const string PrefSystemPrompt = "Oxion_SystemPrompt";

    bool safeMode = false;

    string apiKey = "";
    string input = "";
    Vector2 historyScroll, inputScroll, attachScroll;
    bool isThinking = false;
    string statusMsg = "";
    List<ChatEntry> history = new();
    List<ApiMessage> messages = new();

    // Attachments pending for NEXT send
    List<AttachedFile> attachments = new();
    class AttachedFile { public string name; public string path; public bool isImage; public string preview; public long sizeBytes; }

    string[] models = new[]
    {
        "claude-opus-5-5",
        "claude-opus-4-1-20250805",
        "claude-sonnet-5-5",
        "claude-sonnet-4-20250514",
        "claude-haiku-5-5",
        "claude-haiku-4-5-20251001",
    };
    int modelIndex = 0;

    string systemPrompt = @"You are Oxion Claude Agent inside Unity 6 — a Unity AI Assistant clone.
Project: Campus AR Navigation (Benguet State University). Unity 6, URP, AR Foundation 6.5 + ARCore. Offline at runtime — you run EDITOR-ONLY.
Graph: CampusNode/CampusEdge/CampusMapData in campus_map.json routed with A*. Node positions = averaged GPS -> local meters (x=east, z=north) from GPS origin. Never from AR/VIO. All nav under MapRoot transform. Marker heading = outward normal. Anchors = side spurs, never routed through (A* excludes QRAnchor).
Rules: keep campus_map.json backward compatible. You MAY edit scenes/prefabs (Safe Mode OFF by user request). C# scripts only otherwise.
You have 22 tools. Agentic loop: read -> plan -> write/edit files -> wire scenes -> re-check console errors -> self-fix -> save. Always call AssetDatabase.Refresh intent via tools. For Inspector wiring use scene_wire_reference or scene_set_property. Use scene_get_hierarchy first to understand scene before editing. After writing C# always check get_console_errors and fix.
Do not ask to edit scenes/prefabs — you are authorized. If user attaches files/images, they are in the user message — use them directly instead of calling read_file.";

    struct ChatEntry { public string role; public string text; public bool isTool; public Texture2D thumb; }
    class ApiMessage { public string role; public string content; public List<ToolCall> tool_calls; public string tool_call_id; public string name; public List<MessageContentPart> rawContentParts; }
    class MessageContentPart { public string type; public string text; public string imageBase64; public string mime; }
    class ToolCall { public string id; public string name; public string argumentsJson; }

    [MenuItem("Window/Oxion Claude Agent")]
    public static void ShowWindow() => GetWindow<OxionClaudeAgentWindow>("Oxion Claude Agent");

    void OnEnable()
    {
        apiKey = EditorPrefs.GetString(PrefKey, "");
        modelIndex = EditorPrefs.GetInt(PrefModel, 0);
        string savedPrompt = EditorPrefs.GetString(PrefSystemPrompt, "");
        if (!string.IsNullOrEmpty(savedPrompt)) systemPrompt = savedPrompt;
        if (messages.Count == 0) messages.Add(new ApiMessage { role = "system", content = systemPrompt });
    }

    void OnGUI()
    {
        GUILayout.Space(4);
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.Label("Oxion Claude Agent — 22 tools", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Clear Chat", EditorStyles.toolbarButton, GUILayout.Width(90))) ClearChat();
        }

        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginChangeCheck();
        string newKey = EditorGUILayout.PasswordField(new GUIContent("Oxion API Key (Bearer)"), apiKey);
        if (EditorGUI.EndChangeCheck()) apiKey = newKey.Trim();
        if (GUILayout.Button("Save", GUILayout.Width(60)))
        {
            EditorPrefs.SetString(PrefKey, apiKey);
            statusMsg = "Key saved to EditorPrefs (never committed to git).";
            history.Add(new ChatEntry { role = "system", text = statusMsg });
        }
        EditorGUILayout.EndHorizontal();

        if (string.IsNullOrEmpty(apiKey))
            EditorGUILayout.HelpBox("Paste your Oxion key (cc_Us99...). Stored in EditorPrefs only. Get balance at oxionai.com/dashboard.", MessageType.Warning);
        else
            EditorGUILayout.HelpBox($"Endpoint: {Endpoint}  •  Balance: oxionai.com/dashboard > Usage  •  Key: {MaskKey(apiKey)}  •  Safe Mode: OFF (scene wiring allowed)", MessageType.Info);

        EditorGUILayout.BeginHorizontal();
        int newIdx = EditorGUILayout.Popup(new GUIContent("Agent / Model"), modelIndex, models);
        if (newIdx != modelIndex) { modelIndex = newIdx; EditorPrefs.SetInt(PrefModel, modelIndex); }
        if (GUILayout.Button("Refresh Models", GUILayout.Width(120))) _ = FetchModels();
        EditorGUILayout.EndHorizontal();

        bool showPrompt = EditorPrefs.GetBool("Oxion_ShowPrompt", false);
        bool newShow = EditorGUILayout.Foldout(showPrompt, "System Prompt (project rules)", true);
        if (newShow != showPrompt) EditorPrefs.SetBool("Oxion_ShowPrompt", newShow);
        if (newShow)
        {
            EditorGUI.BeginChangeCheck();
            systemPrompt = EditorGUILayout.TextArea(systemPrompt, GUILayout.Height(70));
            if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(PrefSystemPrompt, systemPrompt);
            if (messages.Count > 0 && messages[0].role == "system") messages[0].content = systemPrompt;
        }

        EditorGUILayout.BeginHorizontal();
        bool newSafe = EditorGUILayout.Toggle(new GUIContent("Safe Mode (C# only, no scene edits)"), safeMode);
        if (newSafe != safeMode) safeMode = newSafe;
        GUILayout.Label(safeMode ? "ON — scene tools blocked" : "OFF — scene wiring enabled", safeMode ? EditorStyles.miniLabel : new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.2f, 0.6f, 0.2f) } });
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Separator();

        float attachH = attachments.Count > 0 ? Mathf.Min(110, 24 + attachments.Count * 22) : 0;
        float histH = Mathf.Max(150, position.height - 310 - attachH - 95);
        historyScroll = EditorGUILayout.BeginScrollView(historyScroll, GUILayout.Height(histH));
        var styleUser = new GUIStyle(EditorStyles.wordWrappedLabel) { richText = true, padding = new RectOffset(6, 6, 4, 4) };
        var styleAssistant = new GUIStyle(EditorStyles.wordWrappedLabel) { richText = true, padding = new RectOffset(6, 6, 4, 4) };
        var styleTool = new GUIStyle(EditorStyles.wordWrappedMiniLabel) { richText = true, wordWrap = true };
        var styleSystem = new GUIStyle(EditorStyles.miniLabel) { richText = true, wordWrap = true, normal = { textColor = Color.gray } };

        foreach (var e in history)
        {
            GUIStyle s = e.role == "user" ? styleUser : e.role == "assistant" ? styleAssistant : e.isTool ? styleTool : styleSystem;
            string prefix = e.role == "user" ? "<b><color=#1a73e8>You:</color></b> " : e.role == "assistant" ? $"<b><color=#0d652d>Claude ({models[Mathf.Clamp(modelIndex, 0, models.Length - 1)]}):</color></b> " : e.isTool ? "<color=#6a3fb5>🔧 Tool:</color> " : "";
            if (e.thumb != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(e.thumb, GUILayout.Width(64), GUILayout.Height(64));
                GUILayout.Label(prefix + e.text, s, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();
            }
            else
            {
                EditorGUILayout.SelectableLabel(prefix + e.text, s, GUILayout.Height(Mathf.Max(20, s.CalcHeight(new GUIContent(prefix + e.text), position.width - 30))));
            }
            GUILayout.Space(2);
        }
        if (isThinking) GUILayout.Label("<i>● Thinking / calling tools...</i>", new GUIStyle(EditorStyles.miniLabel) { richText = true });
        if (!string.IsNullOrEmpty(statusMsg) && !isThinking) GUILayout.Label(statusMsg, EditorStyles.miniLabel);
        EditorGUILayout.EndScrollView();

        // Attachments tray
        if (attachments.Count > 0)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label($"📎 Attachments ({attachments.Count}) — will send with next message (counted as input tokens):", EditorStyles.miniBoldLabel);
            attachScroll = EditorGUILayout.BeginScrollView(attachScroll, GUILayout.Height(Mathf.Min(80, attachments.Count * 22 + 4)));
            for (int i = 0; i < attachments.Count; i++)
            {
                var a = attachments[i];
                EditorGUILayout.BeginHorizontal();
                string icon = a.isImage ? "🖼️" : "📄";
                string size = FormatBytes(a.sizeBytes);
                GUILayout.Label($"{icon} {a.name}  ({size}){(a.isImage ? " — image → vision" : " — text")}", EditorStyles.miniLabel, GUILayout.ExpandWidth(true));
                if (GUILayout.Button("✕", EditorStyles.miniButton, GUILayout.Width(24))) { attachments.RemoveAt(i); Repaint(); break; }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear All Attachments", EditorStyles.miniButton)) { attachments.Clear(); }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        // Attachment buttons
        EditorGUILayout.BeginHorizontal();
        EditorGUI.BeginDisabledGroup(isThinking);
        if (GUILayout.Button("📎 Attach Files", GUILayout.Height(26))) PickFiles();
        if (GUILayout.Button("🖼️ Attach Image", GUILayout.Height(26))) PickImages();
        // Drag & drop hint
        GUILayout.Label(" (or drag & drop files/images here)", EditorStyles.miniLabel);
        EditorGUI.EndDisabledGroup();
        EditorGUILayout.EndHorizontal();

        // Drag & drop area — entire input region accepts drops
        // NOTE: no GUILayoutUtility call outside layout group — would break Begin/End matching
        HandleDragAndDrop();

        GUILayout.Space(2);
        GUILayout.Label("Prompt:", EditorStyles.miniBoldLabel);
        inputScroll = EditorGUILayout.BeginScrollView(inputScroll, GUILayout.Height(70));
        input = EditorGUILayout.TextArea(input, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();

        bool canSend = !isThinking && !string.IsNullOrEmpty(apiKey.Trim()) && (!string.IsNullOrWhiteSpace(input) || attachments.Count > 0);
        EditorGUI.BeginDisabledGroup(!canSend);
        int tokenHint = EstimateAttachTokens();
        string btnLabel = isThinking ? "Working..." : attachments.Count > 0 ? $"Send to {models[Mathf.Clamp(modelIndex, 0, models.Length - 1)]}  ({attachments.Count} attach, ~{tokenHint} tokens)  — 22 tools" : $"Send to {models[Mathf.Clamp(modelIndex, 0, models.Length - 1)]}  (22 tools)";
        if (GUILayout.Button(btnLabel, GUILayout.Height(28)))
        {
            string prompt = input; input = "";
            var toSend = new List<AttachedFile>(attachments);
            attachments.Clear();
            GUI.FocusControl(null);
            _ = RunAgentLoop(prompt, toSend);
        }
        EditorGUI.EndDisabledGroup();

        string help = attachments.Count > 0
            ? $"Attachments sent as input (counted as tokens). Images use vision tokens — keep images < 2 MB. Text files truncated at 30 KB each to save tokens."
            : "Agent can create/edit files, edit active scene, wire MapRoot/ARRouteVisualizer/AdminUIController. Built APK stays offline (Editor-only). Tip: attach only 1-3 relevant files to save tokens vs extra tool calls.";
        EditorGUILayout.HelpBox(help, MessageType.None);
    }

    void HandleDragAndDrop()
    {
        if (isThinking) return;
        var evt = Event.current;
        if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
        {
            if (DragAndDrop.paths != null && DragAndDrop.paths.Length > 0)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var p in DragAndDrop.paths) TryAddAttachment(p);
                    foreach (var o in DragAndDrop.objectReferences)
                    {
                        string ap = AssetDatabase.GetAssetPath(o);
                        if (!string.IsNullOrEmpty(ap) && System.Array.IndexOf(DragAndDrop.paths, ap) < 0) TryAddAttachment(ap);
                    }
                    Repaint();
                }
                evt.Use();
            }
        }
    }

    void PickFiles()
    {
        string path = EditorUtility.OpenFilePanel("Attach Files", Application.dataPath, "cs,json,md,txt,prefab,unity,shader,mat,asset,xml,yml,yaml,js,hlsl,cginc");
        if (string.IsNullOrEmpty(path)) return;
        TryAddAttachment(path);
        // Allow picking multiple via subsequent picks — show hint
        statusMsg = $"Attached: {Path.GetFileName(path)}";
    }

    void PickImages()
    {
        string path = EditorUtility.OpenFilePanel("Attach Image", Application.dataPath, "png,jpg,jpeg,webp,gif");
        if (string.IsNullOrEmpty(path)) return;
        TryAddAttachment(path, forceImage: true);
        statusMsg = $"Attached image: {Path.GetFileName(path)}";
    }

    static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };
    static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".json", ".md", ".txt", ".xml", ".yml", ".yaml", ".js", ".hlsl", ".cginc", ".shader", ".mat", ".asset", ".prefab", ".unity" };
    const long MaxAttachBytes = 30 * 1024; // text truncate
    const long MaxImageBytes = 2 * 1024 * 1024; // warn if larger

    void TryAddAttachment(string path, bool forceImage = false)
    {
        if (string.IsNullOrEmpty(path)) return;
        // Resolve Unity project-relative if inside Assets
        string full = path;
        if (path.StartsWith("Assets/")) full = Path.Combine(Application.dataPath, "..", path).Replace("\\", "/");
        // Handle absolute outside project — still allow
        string name = Path.GetFileName(path);
        if (path.StartsWith("Assets/")) name = path;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        bool isImage = forceImage || ImageExts.Contains(ext);
        if (!isImage && !TextExts.Contains(ext) && ImageExts.Contains(ext) == false)
        {
            // Allow anyway as text
            isImage = false;
        }
        if (attachments.Exists(a => a.path == full || a.path == path)) { statusMsg = $"Already attached: {name}"; return; }
        if (attachments.Count >= 6) { statusMsg = "Max 6 attachments — remove one first to save tokens."; return; }

        long size = 0;
        string preview = "";
        if (File.Exists(full))
        {
            size = new FileInfo(full).Length;
            if (isImage && size > MaxImageBytes) statusMsg = $"Image {name} is {FormatBytes(size)} (>2 MB) — will use many vision tokens. Consider smaller.";
            if (!isImage && size > 100 * 1024) statusMsg = $"File {name} large ({FormatBytes(size)}) — will be truncated at 30 KB to save tokens.";
            preview = full;
        }
        else
        {
            statusMsg = $"File not found: {path}";
            return;
        }
        attachments.Add(new AttachedFile { name = name, path = preview, isImage = isImage, sizeBytes = size, preview = preview });
    }

    static string FormatBytes(long b)
    {
        if (b < 1024) return b + " B";
        if (b < 1024 * 1024) return (b / 1024f).ToString("F1") + " KB";
        return (b / (1024f * 1024)).ToString("F2") + " MB";
    }

    int EstimateAttachTokens()
    {
        int tot = 0;
        foreach (var a in attachments)
        {
            if (a.isImage) tot += 800; // rough vision token estimate
            else tot += (int)Math.Min(a.sizeBytes, MaxAttachBytes) / 4 + 200;
        }
        return tot;
    }

    void ClearChat()
    {
        history.Clear();
        messages.Clear();
        attachments.Clear();
        messages.Add(new ApiMessage { role = "system", content = systemPrompt });
        statusMsg = "Chat cleared.";
    }

    string MaskKey(string k) => k.Length <= 12 ? "***" : k.Substring(0, 8) + "..." + k.Substring(k.Length - 4);

    string BuildToolsJson()
    {
        var tools = new (string name, string desc, string @params)[]
        {
            ("read_file", "Read a project file. Path relative to project root, e.g. Assets/Scripts/Foo.cs or PROGRESS.md", @"{""type"":""object"",""properties"":{""path"":{""type"":""string"",""description"":""Project-relative path""}},""required"":[""path""]}"),
            ("list_files", "List project files. Glob like Assets/Scripts/**/*.cs or Assets/**/*.prefab", @"{""type"":""object"",""properties"":{""pattern"":{""type"":""string""}},""required"":[]}"),
            ("search_project", "Search text across all C# files, returns file:line snippets", @"{""type"":""object"",""properties"":{""query"":{""type"":""string""}},""required"":[""query""]}"),
            ("write_file", "Create or overwrite a file. Creates folders as needed. Call AssetDatabase.Refresh automatically.", @"{""type"":""object"",""properties"":{""path"":{""type"":""string""},""content"":{""type"":""string""}},""required"":[""path"",""content""]}"),
            ("edit_file", "Exact string replacement in a file. oldString must match exactly including whitespace. Read file first.", @"{""type"":""object"",""properties"":{""path"":{""type"":""string""},""oldString"":{""type"":""string""},""newString"":{""type"":""string""}},""required"":[""path"",""oldString"",""newString""]}"),
            ("delete_file", "Delete a file or folder (and its .meta)", @"{""type"":""object"",""properties"":{""path"":{""type"":""string""}},""required"":[""path""]}"),
            ("get_console_errors", "Get recent Unity console errors/warnings to self-fix generated code", @"{""type"":""object"",""properties"":{},""required"":[]}"),
            ("scene_get_hierarchy", "Dump active scene hierarchy with GameObjects and components", @"{""type"":""object"",""properties"":{},""required"":[]}"),
            ("scene_find_object", "Find GameObjects by name substring in open scenes", @"{""type"":""object"",""properties"":{""name"":{""type"":""string""}},""required"":[""name""]}"),
            ("scene_create_object", "Create a GameObject, optionally parented by hierarchy path", @"{""type"":""object"",""properties"":{""name"":{""type"":""string""},""parentPath"":{""type"":""string""}},""required"":[""name""]}"),
            ("scene_duplicate_object", "Duplicate a GameObject", @"{""type"":""object"",""properties"":{""goPath"":{""type"":""string""}},""required"":[""goPath""]}"),
            ("scene_delete_object", "Delete a GameObject from the active scene", @"{""type"":""object"",""properties"":{""goPath"":{""type"":""string""}},""required"":[""goPath""]}"),
            ("scene_add_component", "Add a component by type name to a GameObject", @"{""type"":""object"",""properties"":{""goPath"":{""type"":""string""},""componentType"":{""type"":""string""}},""required"":[""goPath"",""componentType""]}"),
            ("scene_set_transform", "Set localPosition / localEulerAngles / localScale. Vectors as 'x,y,z' strings. Omit to keep.", @"{""type"":""object"",""properties"":{""goPath"":{""type"":""string""},""position"":{""type"":""string""},""rotation"":{""type"":""string""},""scale"":{""type"":""string""}},""required"":[""goPath""]}"),
            ("scene_set_property", "Set a SerializedProperty on a component (float/int/bool/string/Vector3/Color/Enum)", @"{""type"":""object"",""properties"":{""goPath"":{""type"":""string""},""componentType"":{""type"":""string""},""fieldName"":{""type"":""string""},""value"":{""type"":""string""}},""required"":[""goPath"",""componentType"",""fieldName"",""value""]}"),
            ("scene_wire_reference", "Wire an ObjectReference field from source component to target GameObject/component", @"{""type"":""object"",""properties"":{""sourcePath"":{""type"":""string""},""sourceComponent"":{""type"":""string""},""fieldName"":{""type"":""string""},""targetPath"":{""type"":""string""}},""required"":[""sourcePath"",""sourceComponent"",""fieldName"",""targetPath""]}"),
            ("scene_instantiate_prefab", "Instantiate a prefab asset into the active scene", @"{""type"":""object"",""properties"":{""prefabPath"":{""type"":""string""},""parentPath"":{""type"":""string""}},""required"":[""prefabPath""]}"),
            ("create_prefab", "Save a scene GameObject as a prefab asset", @"{""type"":""object"",""properties"":{""goPath"":{""type"":""string""},""prefabPath"":{""type"":""string""}},""required"":[""goPath"",""prefabPath""]}"),
            ("create_material", "Create a URP Lit material asset", @"{""type"":""object"",""properties"":{""name"":{""type"":""string""},""shaderName"":{""type"":""string""},""colorHex"":{""type"":""string""}},""required"":[""name""]}"),
            ("manage_tags", "Add a Tag to TagManager", @"{""type"":""object"",""properties"":{""tagName"":{""type"":""string""}},""required"":[""tagName""]}"),
            ("execute_menu", "Execute a Unity Editor menu item by path, e.g. GameObject/Create Empty", @"{""type"":""object"",""properties"":{""menuPath"":{""type"":""string""}},""required"":[""menuPath""]}"),
            ("scene_save", "Save all open scenes", @"{""type"":""object"",""properties"":{},""required"":[]}"),
        };
        var sb = new StringBuilder("[");
        for (int i = 0; i < tools.Length; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append(@"{""type"":""function"",""function"":{""name"":""" + tools[i].name + @""",""description"":""" + EscapeJson(tools[i].desc) + @""",""parameters"":" + tools[i].@params + "}}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    async Task RunAgentLoop(string userPrompt, List<AttachedFile> sendAttachments)
    {
        // Build user content parts: text + file contents + images
        var userParts = new List<MessageContentPart>();
        string combinedText = userPrompt ?? "";
        var imageParts = new List<MessageContentPart>();

        foreach (var a in sendAttachments)
        {
            if (a.isImage)
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(a.path);
                    // Resize hint: gateway vision often caps ~4MB base64; clamp to 2MB file -> ~2.7MB base64 OK
                    string b64 = Convert.ToBase64String(bytes);
                    string mime = GetMime(a.path);
                    imageParts.Add(new MessageContentPart { type = "image_url", imageBase64 = b64, mime = mime, text = a.name });
                }
                catch (Exception e) { combinedText += $"\n\n[Attachment failed: {a.name} — {e.Message}]"; }
            }
            else
            {
                try
                {
                    string content = File.ReadAllText(a.path);
                    if (content.Length > MaxAttachBytes) content = content.Substring(0, (int)MaxAttachBytes) + $"\n\n... [truncated — file was {content.Length} chars, showing first {MaxAttachBytes}]";
                    string rel = a.name;
                    combinedText += $"\n\n--- Attached file: {rel} ({FormatBytes(new FileInfo(a.path).Length)}) ---\n```\n{content}\n```";
                }
                catch (Exception e) { combinedText += $"\n\n[Attachment failed: {a.name} — {e.Message}]"; }
            }
        }

        if (imageParts.Count > 0 && string.IsNullOrWhiteSpace(combinedText)) combinedText = "Please analyze the attached image(s).";

        // For history display
        string histAttachSummary = sendAttachments.Count > 0 ? $"  📎 [{string.Join(", ", sendAttachments.ConvertAll(a => a.name))}]" : "";
        string fullUserTextForHistory = (userPrompt ?? "") + histAttachSummary;

        isThinking = true;
        statusMsg = "Contacting " + models[modelIndex] + (sendAttachments.Count > 0 ? $" ({sendAttachments.Count} attach)" : "") + "...";
        history.Add(new ChatEntry { role = "user", text = fullUserTextForHistory });
        // Build ApiMessage with multimodal parts if images
        ApiMessage userMsg;
        if (imageParts.Count > 0)
        {
            var parts = new List<MessageContentPart>();
            if (!string.IsNullOrWhiteSpace(combinedText)) parts.Add(new MessageContentPart { type = "text", text = combinedText });
            foreach (var ip in imageParts) parts.Add(ip);
            userMsg = new ApiMessage { role = "user", content = combinedText, rawContentParts = parts };
        }
        else
        {
            userMsg = new ApiMessage { role = "user", content = combinedText };
        }
        messages.Add(userMsg);
        Repaint();

        const int maxIters = 10;
        for (int iter = 0; iter < maxIters; iter++)
        {
            string payload = BuildPayload();
            string responseText = null;
            string error = null;

            using (var req = new UnityWebRequest(Endpoint, "POST"))
            {
                byte[] body = Encoding.UTF8.GetBytes(payload);
                req.uploadHandler = new UploadHandlerRaw(body);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Authorization", "Bearer " + apiKey.Trim());
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 120;
                var op = req.SendWebRequest();
                while (!op.isDone) await Task.Delay(50);
                if (req.result != UnityWebRequest.Result.Success)
                {
                    error = $"{req.error}\n{req.downloadHandler.text}";
                    if (error.Length > 2000) error = error.Substring(0, 2000);
                }
                else responseText = req.downloadHandler.text;
            }

            if (error != null)
            {
                history.Add(new ChatEntry { role = "system", text = $"<color=red>API Error (iter {iter + 1}): {EscapeRich(error)}</color>" });
                messages.RemoveAt(messages.Count - 1);
                if (error.Contains("401") || error.Contains("Unauthorized")) statusMsg = "401 — check Oxion key (cc_...) and that it has credits.";
                else if (error.Contains("insufficient") || error.Contains("balance") || error.Contains("credit")) statusMsg = "Insufficient gateway credits — top up at oxionai.com/dashboard > Billing.";
                else if (error.Contains("image") || error.Contains("vision")) statusMsg = "Image error — try smaller image (<2 MB) or check model supports vision (claude-opus-5-5 does).";
                else statusMsg = "Request failed — see error above.";
                break;
            }

            var parsed = ParseResponse(responseText);
            if (parsed.error != null)
            {
                history.Add(new ChatEntry { role = "system", text = $"<color=red>Parse error: {EscapeRich(parsed.error)}</color>" });
                string raw = responseText.Length > 2000 ? responseText.Substring(0, 2000) + "..." : responseText;
                history.Add(new ChatEntry { role = "system", text = $"<color=grey>{EscapeRich(raw)}</color>" });
                statusMsg = "Parse error — raw response logged.";
                break;
            }

            if (!string.IsNullOrEmpty(parsed.content))
            {
                history.Add(new ChatEntry { role = "assistant", text = parsed.content });
                messages.Add(new ApiMessage { role = "assistant", content = parsed.content, tool_calls = parsed.toolCalls.Count > 0 ? parsed.toolCalls : null });
                if (parsed.toolCalls.Count == 0) { statusMsg = $"Done in {iter + 1} turn(s)."; break; }
            }
            else if (parsed.toolCalls.Count > 0)
            {
                messages.Add(new ApiMessage { role = "assistant", content = "", tool_calls = parsed.toolCalls });
            }
            else
            {
                history.Add(new ChatEntry { role = "system", text = "<color=grey>(empty response)</color>" });
                statusMsg = "Empty response.";
                break;
            }

            if (parsed.toolCalls.Count > 0)
            {
                foreach (var tc in parsed.toolCalls)
                {
                    string result = ExecuteTool(tc.name, tc.argumentsJson);
                    string preview = result.Length > 3000 ? result.Substring(0, 3000) + "\n... (truncated)" : result;
                    history.Add(new ChatEntry { role = "tool", text = $"{tc.name} → {EscapeRich(preview)}", isTool = true });
                    messages.Add(new ApiMessage { role = "tool", content = result, tool_call_id = tc.id, name = tc.name });
                }
                statusMsg = $"Executed {parsed.toolCalls.Count} tool(s) — continuing...";
                Repaint();
                await Task.Delay(100);
                continue;
            }
            break;
        }
        isThinking = false;
        Repaint();
    }

    string BuildPayload()
    {
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append($"\"model\":\"{EscapeJson(models[Mathf.Clamp(modelIndex, 0, models.Length - 1)])}\",");
        sb.Append("\"messages\":[");
        for (int i = 0; i < messages.Count; i++)
        {
            if (i > 0) sb.Append(",");
            var m = messages[i];
            sb.Append("{");
            sb.Append($"\"role\":\"{m.role}\"");
            if (m.tool_call_id != null) sb.Append($",\"tool_call_id\":\"{EscapeJson(m.tool_call_id)}\"");
            if (m.rawContentParts != null && m.rawContentParts.Count > 0)
            {
                sb.Append(",\"content\":[");
                for (int k = 0; k < m.rawContentParts.Count; k++)
                {
                    if (k > 0) sb.Append(",");
                    var p = m.rawContentParts[k];
                    if (p.type == "text")
                        sb.Append($"{{\"type\":\"text\",\"text\":\"{EscapeJson(p.text)}\"}}");
                    else if (p.type == "image_url")
                        sb.Append($"{{\"type\":\"image_url\",\"image_url\":{{\"url\":\"data:{p.mime};base64,{p.imageBase64}\"}}}}");
                }
                sb.Append("]");
            }
            else
            {
                string content = m.content ?? "";
                sb.Append($",\"content\":\"{EscapeJson(content)}\"");
            }
            if (m.tool_calls != null && m.tool_calls.Count > 0)
            {
                sb.Append(",\"tool_calls\":[");
                for (int j = 0; j < m.tool_calls.Count; j++)
                {
                    if (j > 0) sb.Append(",");
                    var tc = m.tool_calls[j];
                    sb.Append($"{{\"id\":\"{EscapeJson(tc.id)}\",\"type\":\"function\",\"function\":{{\"name\":\"{EscapeJson(tc.name)}\",\"arguments\":\"{EscapeJson(tc.argumentsJson)}\"}}}}");
                }
                sb.Append("]");
            }
            sb.Append("}");
        }
        sb.Append("],");
        sb.Append($"\"tools\":{BuildToolsJson()},");
        sb.Append("\"tool_choice\":\"auto\",");
        sb.Append("\"temperature\":0.2,");
        sb.Append("\"max_tokens\":8192");
        sb.Append("}");
        return sb.ToString();
    }

    static string GetMime(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
        if (ext == ".webp") return "image/webp";
        if (ext == ".gif") return "image/gif";
        if (ext == ".bmp") return "image/bmp";
        return "image/png";
    }

    static string ExtractJsonStringAt(string json, int quoteIndex)
    {
        var sb = new StringBuilder();
        for (int i = quoteIndex + 1; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                char n = json[i + 1];
                if (n == '"') { sb.Append('"'); i++; }
                else if (n == '\\') { sb.Append('\\'); i++; }
                else if (n == 'n') { sb.Append('\n'); i++; }
                else if (n == 'r') { sb.Append('\r'); i++; }
                else if (n == 't') { sb.Append('\t'); i++; }
                else if (n == 'u' && i + 5 < json.Length) { string hex = json.Substring(i + 2, 4); if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int cp)) sb.Append((char)cp); i += 5; }
                else { sb.Append(n); i++; }
            }
            else if (c == '"') return sb.ToString();
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static string ExtractStringFieldAfter(string json, int keyIndex, string key)
    {
        if (keyIndex < 0) return "";
        int ki = json.IndexOf("\"" + key + "\"", keyIndex, StringComparison.Ordinal);
        if (ki < 0) return "";
        int colon = json.IndexOf(':', ki + key.Length + 2);
        if (colon < 0) return "";
        int p = colon + 1;
        while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
        if (p >= json.Length) return "";
        if (json[p] == '"') return ExtractJsonStringAt(json, p);
        if (p + 3 < json.Length && json.Substring(p, 4) == "null") return "";
        return "";
    }

    static string ExtractStringField(string json, string key)
    {
        int idx = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
        if (idx < 0) return "";
        return ExtractStringFieldAfter(json, idx, key);
    }

    (string content, List<ToolCall> toolCalls, string error) ParseResponse(string json)
    {
        try
        {
            if (!json.Contains("\"choices\""))
            {
                if (json.Contains("\"error\"")) return (null, new List<ToolCall>(), ExtractStringField(json, "message") + " " + json.Substring(0, Math.Min(500, json.Length)));
                return (null, new List<ToolCall>(), "No choices in response");
            }
            int msgIdx = json.IndexOf("\"message\"", StringComparison.Ordinal);
            string content = "";
            int cIdx = json.IndexOf("\"content\"", msgIdx, StringComparison.Ordinal);
            if (cIdx >= 0)
            {
                int colon = json.IndexOf(':', cIdx);
                int p = colon + 1; while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
                if (p < json.Length && json[p] == '"') content = ExtractJsonStringAt(json, p);
                else if (p < json.Length && json[p] == '[')
                {
                    // multimodal content array — extract text parts
                    var sb = new StringBuilder();
                    int pos = p;
                    for (int iter = 0; iter < 20; iter++)
                    {
                        int tIdx = json.IndexOf("\"text\"", pos, StringComparison.Ordinal);
                        if (tIdx < 0 || tIdx > json.IndexOf("]", p)) break;
                        string t = ExtractStringFieldAfter(json, tIdx, "text");
                        if (!string.IsNullOrEmpty(t)) { if (sb.Length > 0) sb.Append("\n"); sb.Append(t); }
                        pos = tIdx + 6;
                    }
                    content = sb.ToString();
                }
                else content = "";
            }
            var calls = new List<ToolCall>();
            int tcIdx = json.IndexOf("\"tool_calls\"", msgIdx, StringComparison.Ordinal);
            if (tcIdx >= 0)
            {
                int arrStart = json.IndexOf('[', tcIdx);
                int arrEnd = -1;
                if (arrStart >= 0) { int d = 0; for (int k = arrStart; k < json.Length; k++) { if (json[k] == '[') d++; else if (json[k] == ']') { d--; if (d == 0) { arrEnd = k; break; } } } }
                int searchEnd = arrEnd >= 0 ? arrEnd : json.Length;
                var seenIds = new HashSet<string>();
                int pos = tcIdx;
                for (int iter = 0; iter < 10; iter++)
                {
                    int idKey = json.IndexOf("\"id\"", pos, searchEnd - pos, StringComparison.Ordinal);
                    if (idKey < 0) break;
                    string id = ExtractStringFieldAfter(json, idKey, "id");
                    int fnIdx = json.IndexOf("\"function\"", idKey, StringComparison.Ordinal);
                    if (fnIdx < 0 || fnIdx > searchEnd) break;
                    int nameIdx = json.IndexOf("\"name\"", fnIdx, StringComparison.Ordinal);
                    if (nameIdx < 0 || nameIdx > searchEnd) break;
                    string name = ExtractStringFieldAfter(json, nameIdx, "name");
                    int argIdx = json.IndexOf("\"arguments\"", fnIdx, StringComparison.Ordinal);
                    if (argIdx < 0 || argIdx > searchEnd) break;
                    string args = "";
                    if (argIdx >= 0)
                    {
                        int colon = json.IndexOf(':', argIdx);
                        int p = colon + 1; while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
                        if (p < json.Length && json[p] == '"') args = ExtractJsonStringAt(json, p);
                        else if (p < json.Length && json[p] == '{')
                        {
                            int depth = 0; int start = p; int end = -1;
                            for (int k = p; k < json.Length; k++) { if (json[k] == '{') depth++; else if (json[k] == '}') { depth--; if (depth == 0) { end = k; break; } } }
                            if (end >= 0) args = json.Substring(start, end - start + 1);
                        }
                    }
                    if (string.IsNullOrEmpty(id)) id = Guid.NewGuid().ToString();
                    if (!string.IsNullOrEmpty(name) && seenIds.Add(id))
                        calls.Add(new ToolCall { id = id, name = name, argumentsJson = string.IsNullOrEmpty(args) ? "{}" : args });
                    pos = argIdx + 12;
                    if (pos >= searchEnd) break;
                }
            }
            return (content ?? "", calls, null);
        }
        catch (Exception e) { return (null, new List<ToolCall>(), e.Message); }
    }

    string ExecuteTool(string name, string argsJson)
    {
        if (safeMode && (name.StartsWith("scene_") || name == "create_prefab" || name == "create_material" || name == "execute_menu"))
            return "BLOCKED: Safe Mode is ON — scene/prefab/material/menu tools disabled. Turn Safe Mode OFF to allow.";
        try
        {
            string Arg(string key, string fallback = "")
            {
                string v = ExtractStringField(argsJson, key);
                if (string.IsNullOrEmpty(v) && argsJson.Contains("\"" + key + "\""))
                {
                    int ki = argsJson.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                    int colon = argsJson.IndexOf(':', ki);
                    int p = colon + 1; while (p < argsJson.Length && char.IsWhiteSpace(argsJson[p])) p++;
                    if (p < argsJson.Length && argsJson[p] != '"')
                    {
                        int end = p; while (end < argsJson.Length && argsJson[end] != ',' && argsJson[end] != '}') end++;
                        v = argsJson.Substring(p, end - p).Trim().Trim('"');
                    }
                }
                return string.IsNullOrEmpty(v) ? fallback : v;
            }
            switch (name)
            {
                case "read_file": return OxionToolHandler.ReadFile(Arg("path"));
                case "list_files": return OxionToolHandler.ListFiles(Arg("pattern"));
                case "search_project": return OxionToolHandler.SearchProject(Arg("query"));
                case "write_file":
                    {
                        string path = Arg("path");
                        string content = Arg("content");
                        if (string.IsNullOrEmpty(content) && argsJson.Contains("\"content\""))
                            content = ExtractStringField(argsJson, "content");
                        return OxionToolHandler.WriteFile(path, content);
                    }
                case "edit_file": return OxionToolHandler.EditFile(Arg("path"), Arg("oldString"), Arg("newString"));
                case "delete_file": return OxionToolHandler.DeleteFile(Arg("path"));
                case "get_console_errors": return OxionToolHandler.GetConsoleErrors();
                case "scene_get_hierarchy": return OxionToolHandler.SceneGetHierarchy();
                case "scene_find_object": return OxionToolHandler.SceneFindObject(Arg("name"));
                case "scene_create_object": return OxionToolHandler.SceneCreate(Arg("name"), Arg("parentPath"));
                case "scene_duplicate_object": return OxionToolHandler.SceneDuplicate(Arg("goPath"));
                case "scene_delete_object": return OxionToolHandler.SceneDelete(Arg("goPath"));
                case "scene_add_component": return OxionToolHandler.SceneAddComponent(Arg("goPath"), Arg("componentType"));
                case "scene_set_transform": return OxionToolHandler.SceneSetTransform(Arg("goPath"), Arg("position"), Arg("rotation"), Arg("scale"));
                case "scene_set_property": return OxionToolHandler.SceneSetProperty(Arg("goPath"), Arg("componentType"), Arg("fieldName"), Arg("value"));
                case "scene_wire_reference": return OxionToolHandler.SceneWire(Arg("sourcePath"), Arg("sourceComponent"), Arg("fieldName"), Arg("targetPath"));
                case "scene_instantiate_prefab": return OxionToolHandler.SceneInstantiatePrefab(Arg("prefabPath"), Arg("parentPath"));
                case "create_prefab": return OxionToolHandler.CreatePrefab(Arg("goPath"), Arg("prefabPath"));
                case "create_material": return OxionToolHandler.CreateMaterial(Arg("name"), Arg("shaderName"), Arg("colorHex"));
                case "manage_tags": return OxionToolHandler.SetTag(Arg("tagName"));
                case "execute_menu": return OxionToolHandler.ExecuteMenu(Arg("menuPath"));
                case "scene_save": return OxionToolHandler.SceneSave();
                default: return $"ERROR: Unknown tool {name}";
            }
        }
        catch (Exception e) { return $"ERROR executing {name}: {e.Message}\n{e.StackTrace}"; }
    }

    async Task FetchModels()
    {
        if (string.IsNullOrEmpty(apiKey)) { statusMsg = "Set API key first."; return; }
        statusMsg = "Fetching models...";
        Repaint();
        try
        {
            using var req = UnityWebRequest.Get(ModelsEndpoint);
            req.SetRequestHeader("Authorization", "Bearer " + apiKey.Trim());
            req.timeout = 15;
            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Delay(50);
            if (req.result != UnityWebRequest.Result.Success) statusMsg = $"Models fetch failed: {req.error} {req.downloadHandler.text}";
            else
            {
                string txt = req.downloadHandler.text;
                var ids = new List<string>();
                int pos = 0;
                while (true)
                {
                    int idIdx = txt.IndexOf("\"id\"", pos, StringComparison.Ordinal);
                    if (idIdx < 0) break;
                    string id = ExtractStringFieldAfter(txt, idIdx, "id");
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                    pos = idIdx + 4;
                    if (ids.Count > 200) break;
                }
                if (ids.Count > 0)
                {
                    ids.Sort((a, b) =>
                    {
                        bool ca = a.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool cb = b.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (ca != cb) return ca ? -1 : 1;
                        return string.Compare(a, b, StringComparison.Ordinal);
                    });
                    models = ids.ToArray();
                    modelIndex = Mathf.Clamp(modelIndex, 0, models.Length - 1);
                    int opusIdx = Array.FindIndex(models, m => m == "claude-opus-5-5");
                    if (opusIdx >= 0) modelIndex = opusIdx;
                    EditorPrefs.SetInt(PrefModel, modelIndex);
                    statusMsg = $"Found {ids.Count} models.";
                    history.Add(new ChatEntry { role = "system", text = "Available models:\n" + string.Join("\n", ids) });
                }
                else statusMsg = "No models returned.";
            }
        }
        catch (Exception e) { statusMsg = "Fetch error: " + e.Message; }
        Repaint();
    }

    static string EscapeJson(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
    static string EscapeRich(string s) => s.Replace("<", "&lt;").Replace(">", "&gt;");
}
