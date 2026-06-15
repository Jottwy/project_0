#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using BackroomsSurvival.Gameplay.Chunks;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>Authoring window (v2) for <see cref="ChunkTemplate"/> assets:
    /// live preview, search, sectioned layout, validation and preview toggles.</summary>
    public class BackroomsChunkEditor : EditorWindow
    {
        private ChunkTemplate template;
        private Vector2 scrollPos;
        private GameObject previewObject;

        private bool liveUpdate       = true;
        private bool showWireframe    = false;
        private bool showColliders    = false;
        private bool showGrid         = true;
        private bool useSceneLighting = true;

        private string searchQuery = "";
        private readonly List<ChunkTemplate> foundTemplates = new List<ChunkTemplate>();

        private bool   previewDirty;
        private double lastChangeTime;
        private const double PreviewDelay = 0.3;

        [MenuItem("Backrooms/Chunk Editor")]
        public static void OpenWindow() => GetWindow<BackroomsChunkEditor>("Chunk Editor");

        private void OnEnable()  => EditorApplication.update += EditorUpdate;
        private void OnDisable() => EditorApplication.update -= EditorUpdate;

        // Debounced live preview. GUI.changed is only valid during OnGUI, so the
        // edit is flagged there and the rebuild happens here after a short delay.
        private void EditorUpdate()
        {
            if (!liveUpdate || template == null || !previewDirty) return;
            if (EditorApplication.timeSinceStartup - lastChangeTime < PreviewDelay) return;
            previewDirty = false;
            RegeneratePreview();
        }

        private void OnGUI()
        {
            HandleShortcuts();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Backrooms Chunk Editor v2", EditorStyles.boldLabel);
            if (GUILayout.Button("? Help", GUILayout.Width(60))) ShowHelp();
            EditorGUILayout.EndHorizontal();

            DrawTopToolbar();

            if (template == null)
            {
                EditorGUILayout.HelpBox("Search, or create a new template to begin.", MessageType.Info);
                return;
            }

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

            EditorGUI.BeginChangeCheck();
            DrawTemplateInfo();   EditorGUILayout.Space(10);
            DrawDimensions();     EditorGUILayout.Space(10);
            DrawLayers();         EditorGUILayout.Space(10);
            DrawWalls();          EditorGUILayout.Space(10);
            DrawVisualSettings();
            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(template);
                previewDirty   = true;
                lastChangeTime = EditorApplication.timeSinceStartup;
            }

            EditorGUILayout.Space(10);
            DrawPreviewOptions();    EditorGUILayout.Space(10);
            DrawTemplateManagement();

            EditorGUILayout.EndScrollView();
        }

        private void HandleShortcuts()
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.S && (e.control || e.command))
            {
                SaveTemplate();
                e.Use();
            }
        }

        // ── Toolbar ───────────────────────────────────────────────────────────
        private void DrawTopToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Search:", GUILayout.Width(50));
            searchQuery = EditorGUILayout.TextField(searchQuery, EditorStyles.toolbarTextField);
            if (GUILayout.Button("Find", EditorStyles.toolbarButton, GUILayout.Width(50))) SearchTemplates();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("+ New", EditorStyles.toolbarButton, GUILayout.Width(60))) NewTemplate();
            using (new EditorGUI.DisabledScope(template == null))
            {
                if (GUILayout.Button("Save", EditorStyles.toolbarButton, GUILayout.Width(60))) SaveTemplate();
                if (GUILayout.Button("Export JSON", EditorStyles.toolbarButton, GUILayout.Width(90))) ExportJson();
            }
            EditorGUILayout.EndHorizontal();
        }

        // ── Sections ──────────────────────────────────────────────────────────
        private void DrawTemplateInfo()
        {
            EditorGUILayout.LabelField("Template Info", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            template.name        = EditorGUILayout.TextField("Name", template.name);
            template.version     = EditorGUILayout.IntField("Version", template.version);
            template.weight      = EditorGUILayout.Slider("Weight", template.weight, 0f, 1f);
            EditorGUILayout.LabelField("Description");
            template.description = EditorGUILayout.TextArea(template.description, GUILayout.Height(50));

            if (template.tags == null) template.tags = new string[0];
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Tags");
            if (GUILayout.Button("Add Tag", GUILayout.Width(80)))
            {
                System.Array.Resize(ref template.tags, template.tags.Length + 1);
                template.tags[template.tags.Length - 1] = "newtag";
            }
            EditorGUILayout.EndHorizontal();

            int tagToRemove = -1;
            for (int i = 0; i < template.tags.Length; i++)
            {
                EditorGUILayout.BeginHorizontal();
                template.tags[i] = EditorGUILayout.TextField($"Tag {i}", template.tags[i]);
                if (GUILayout.Button("✕", GUILayout.Width(28))) tagToRemove = i;
                EditorGUILayout.EndHorizontal();
            }
            if (tagToRemove >= 0) template.tags = RemovedAt(template.tags, tagToRemove);

            EditorGUILayout.EndVertical();
        }

        private void DrawDimensions()
        {
            EditorGUILayout.LabelField($"Dimensions  ({template.width:F1}m × {template.length:F1}m × {template.depth:F1}m)", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            template.width  = LabeledSlider("Width",  template.width,  1f, 100f);
            template.length = LabeledSlider("Length", template.length, 1f, 100f);
            template.depth  = LabeledSlider("Depth",  template.depth,  1f, 100f);
            EditorGUILayout.EndVertical();
        }

        private void DrawLayers()
        {
            EditorGUILayout.LabelField("Layers", EditorStyles.boldLabel);
            if (template.layers == null) template.layers = new LayerConfig[0];

            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add Layer", GUILayout.Width(100)))
            {
                int n = template.layers.Length;
                System.Array.Resize(ref template.layers, n + 1);
                template.layers[n] = new LayerConfig
                {
                    type = LayerType.Floor,
                    height = 4f,
                    zPosition = n >= 1 ? template.layers[n - 1].zPosition - 4f : 0f,
                };
            }
            if (GUILayout.Button("- Remove Last", GUILayout.Width(100)) && template.layers.Length > 1)
                System.Array.Resize(ref template.layers, template.layers.Length - 1);
            EditorGUILayout.EndHorizontal();

            for (int i = 0; i < template.layers.Length; i++) DrawLayerSettings(i);
            EditorGUILayout.EndVertical();
        }

        private void DrawLayerSettings(int index)
        {
            var layer = template.layers[index];
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField($"Layer {index}  (z={layer.zPosition:F1}, h={layer.height:F2}m)", EditorStyles.boldLabel);

            layer.zPosition = EditorGUILayout.FloatField("Z Position", layer.zPosition);
            layer.type      = (LayerType)EditorGUILayout.EnumPopup("Type", layer.type);
            layer.height    = ValidatedSlider("Height", layer.height, 0.1f, 20f, layer.height >= 0.1f);

            if (layer.type == LayerType.Grate)
            {
                EditorGUILayout.LabelField("Grate Settings", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                layer.cellSize = EditorGUILayout.Slider("Cell Size", layer.cellSize, 0.01f, 0.5f);
                layer.holeSize = ValidatedSlider("Hole Size", layer.holeSize,
                    layer.cellSize * 0.5f, layer.cellSize * 0.99f, layer.holeSize < layer.cellSize);
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.FloatField("Bar Thickness (auto)", Mathf.Max(0f, layer.cellSize - layer.holeSize));
                EditorGUI.indentLevel--;
            }

            template.layers[index] = layer;
            EditorGUILayout.EndVertical();
        }

        private void DrawWalls()
        {
            EditorGUILayout.LabelField("Walls", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            // North/South run along X (width); East/West run along Z (length).
            EditorGUILayout.BeginHorizontal();
            WallColumn("North (z-neg)", ref template.north, template.width);
            WallColumn("South (z-pos)", ref template.south, template.width);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            WallColumn("East (x-pos)", ref template.east, template.length);
            WallColumn("West (x-neg)", ref template.west, template.length);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void WallColumn(string title, ref WallConfig wall, float wallLength)
        {
            EditorGUILayout.BeginVertical("box", GUILayout.ExpandWidth(true));
            DrawWallConfig(title, ref wall, wallLength);
            EditorGUILayout.EndVertical();
        }

        private void DrawWallConfig(string title, ref WallConfig wall, float wallLength)
        {
            if (wall.openings == null) wall.openings = new WallOpening[0];

            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            wall.enabled   = EditorGUILayout.Toggle("Enabled", wall.enabled);
            wall.thickness = EditorGUILayout.Slider("Thickness", wall.thickness, 0.1f, 1f);

            EditorGUILayout.LabelField("Openings");
            EditorGUI.indentLevel++;

            int removeIndex = -1;
            for (int i = 0; i < wall.openings.Length; i++)
            {
                var opening = wall.openings[i];
                bool valid = opening.width <= wallLength;
                EditorGUILayout.BeginVertical("box");

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"Opening {i}");
                if (GUILayout.Button("✕", GUILayout.Width(28))) removeIndex = i;
                EditorGUILayout.EndHorizontal();

                opening.position = EditorGUILayout.Slider("Pos", opening.position, 0f, wallLength);
                opening.width    = ValidatedSlider("Width", opening.width, 0.5f, 5f, valid);
                opening.height   = EditorGUILayout.Slider("Height", opening.height, 0.5f, 5f);
                wall.openings[i] = opening;

                EditorGUILayout.EndVertical();
            }
            if (removeIndex >= 0) wall.openings = RemovedAt(wall.openings, removeIndex);

            if (GUILayout.Button("+ Add Opening"))
            {
                System.Array.Resize(ref wall.openings, wall.openings.Length + 1);
                wall.openings[wall.openings.Length - 1] =
                    new WallOpening { position = wallLength * 0.5f, width = 1.5f, height = 2.2f };
            }
            EditorGUI.indentLevel--;
        }

        private void DrawVisualSettings()
        {
            EditorGUILayout.LabelField("Visual Settings", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            template.wallColor      = EditorGUILayout.ColorField("Wall Color", template.wallColor);
            template.floorColor     = EditorGUILayout.ColorField("Floor Color", template.floorColor);
            template.grateColor     = EditorGUILayout.ColorField("Grate Color", template.grateColor);
            template.customMaterial = (Material)EditorGUILayout.ObjectField("Custom Material", template.customMaterial, typeof(Material), false);
            EditorGUILayout.EndVertical();
        }

        private void DrawPreviewOptions()
        {
            EditorGUILayout.LabelField("Preview Options", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");

            liveUpdate    = EditorGUILayout.Toggle("Live Update", liveUpdate);
            EditorGUILayout.BeginHorizontal();
            showWireframe = EditorGUILayout.ToggleLeft("Wireframe", showWireframe, GUILayout.Width(120));
            showColliders = EditorGUILayout.ToggleLeft("Colliders", showColliders, GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            showGrid         = EditorGUILayout.ToggleLeft("Scene Grid", showGrid, GUILayout.Width(120));
            useSceneLighting = EditorGUILayout.ToggleLeft("Scene Lighting", useSceneLighting, GUILayout.Width(120));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Preview / Regenerate")) RegeneratePreview();
            if (GUILayout.Button("Fit View")) RecenterCamera();
            if (GUILayout.Button("Clear Preview")) ClearPreview();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DrawTemplateManagement()
        {
            EditorGUILayout.LabelField("Template Management", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Duplicate")) DuplicateTemplate();
            if (GUILayout.Button("Save As")) SaveAsTemplate();
            if (GUILayout.Button("Delete")) DeleteTemplate();
            if (GUILayout.Button("Open in Explorer")) OpenInExplorer();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static float LabeledSlider(string label, float value, float min, float max)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(90));
            float v = EditorGUILayout.Slider(value, min, max);
            EditorGUILayout.LabelField($"{v:F2}m", GUILayout.Width(55));
            EditorGUILayout.EndHorizontal();
            return v;
        }

        // Tints the field red when `valid` is false (visual validation).
        private static float ValidatedSlider(string label, float value, float min, float max, bool valid)
        {
            var prev = GUI.color;
            GUI.color = valid ? prev : Color.red;
            float v = EditorGUILayout.Slider(label, value, min, max);
            GUI.color = prev;
            return v;
        }

        private static T[] RemovedAt<T>(T[] array, int index)
        {
            var list = new List<T>(array);
            list.RemoveAt(index);
            return list.ToArray();
        }

        private static void ShowHelp() => EditorUtility.DisplayDialog("Chunk Editor",
            "Search/New a template, edit its sections, then Preview in scene or Export JSON.\n" +
            "Ctrl+S saves. Live Update rebuilds the preview ~0.3s after a change.", "OK");

        // ── Actions ───────────────────────────────────────────────────────────
        private void SearchTemplates()
        {
            foundTemplates.Clear();
            string q = (searchQuery ?? "").ToLowerInvariant();
            foreach (string guid in AssetDatabase.FindAssets($"t:{nameof(ChunkTemplate)}"))
            {
                var tmpl = AssetDatabase.LoadAssetAtPath<ChunkTemplate>(AssetDatabase.GUIDToAssetPath(guid));
                if (tmpl == null) continue;
                string n = (tmpl.name ?? "").ToLowerInvariant();
                bool tagHit = tmpl.tags != null && tmpl.tags.Any(t => !string.IsNullOrEmpty(t) && t.ToLowerInvariant().Contains(q));
                if (q.Length == 0 || n.Contains(q) || tagHit) foundTemplates.Add(tmpl);
            }

            if (foundTemplates.Count > 0)
            {
                template = foundTemplates[0];
                Debug.Log($"[BackroomsChunkEditor] Found {foundTemplates.Count} template(s); loaded '{template.name}'.");
            }
            else
            {
                EditorUtility.DisplayDialog("Search", "No matching ChunkTemplate found.", "OK");
            }
        }

        private void NewTemplate()
        {
            string path = EditorUtility.SaveFilePanelInProject("Save Chunk Template", "NewChunk", "asset", "");
            if (string.IsNullOrEmpty(path)) return;

            template = CreateInstance<ChunkTemplate>();
            template.InitializeDefaultLayers();
            template.InitializeDefaultWalls();
            AssetDatabase.CreateAsset(template, path);
            AssetDatabase.SaveAssets();
        }

        private void SaveTemplate()
        {
            if (template == null) return;
            EditorUtility.SetDirty(template);
            AssetDatabase.SaveAssets();
            Debug.Log("[BackroomsChunkEditor] Template saved.");
        }

        private void SaveAsTemplate()
        {
            if (template == null) return;
            string path = EditorUtility.SaveFilePanelInProject("Save As", template.name + "_copy", "asset", "");
            if (string.IsNullOrEmpty(path)) return;

            var copy = CreateInstance<ChunkTemplate>();
            EditorUtility.CopySerialized(template, copy);
            AssetDatabase.CreateAsset(copy, path);
            AssetDatabase.SaveAssets();
            template = copy;
        }

        private void DuplicateTemplate()
        {
            if (template == null) return;
            string path = AssetDatabase.GetAssetPath(template);
            if (string.IsNullOrEmpty(path)) { SaveAsTemplate(); return; }

            string newPath = AssetDatabase.GenerateUniqueAssetPath(path);
            if (AssetDatabase.CopyAsset(path, newPath))
            {
                AssetDatabase.SaveAssets();
                template = AssetDatabase.LoadAssetAtPath<ChunkTemplate>(newPath);
            }
        }

        private void DeleteTemplate()
        {
            if (template == null) return;
            if (!EditorUtility.DisplayDialog("Delete Template", $"Delete '{template.name}'? This cannot be undone.", "Delete", "Cancel"))
                return;
            string path = AssetDatabase.GetAssetPath(template);
            ClearPreview();
            if (!string.IsNullOrEmpty(path)) AssetDatabase.DeleteAsset(path);
            template = null;
        }

        private void OpenInExplorer()
        {
            if (template == null) return;
            string path = AssetDatabase.GetAssetPath(template);
            if (!string.IsNullOrEmpty(path)) EditorUtility.RevealInFinder(path);
        }

        private void ExportJson() => ChunkJsonExporter.ExportToJson(template);

        // ── Preview ───────────────────────────────────────────────────────────
        private void RegeneratePreview()
        {
            if (template == null) return;
            ClearPreview();
            previewObject = ChunkPreviewRenderer.RenderPreview(template, Vector3.zero,
                new ChunkPreviewRenderer.RenderOptions
                {
                    wireframe = showWireframe, showColliders = showColliders, showGrid = showGrid,
                });
            ApplySceneViewOptions();
        }

        private void ClearPreview()
        {
            if (previewObject != null) DestroyImmediate(previewObject);
            previewObject = null;
        }

        private void RecenterCamera()
        {
            var sv = SceneView.lastActiveSceneView;
            if (previewObject == null || sv == null) return;
            var rends = previewObject.GetComponentsInChildren<Renderer>();
            if (rends.Length == 0) return;

            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);
            sv.Frame(b, false);
        }

        private void ApplySceneViewOptions()
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null) return;
            sv.sceneLighting = useSceneLighting;
            sv.showGrid      = showGrid;
            sv.cameraMode    = SceneView.GetBuiltinCameraMode(showWireframe ? DrawCameraMode.Wireframe : DrawCameraMode.Textured);
            sv.Repaint();
        }
    }
}
#endif
