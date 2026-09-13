#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BackroomsSurvival.Gameplay.HandInteraction;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools.HandInteraction
{
    /// <summary>
    /// Tools ▸ Interaction Authoring. Una cara para la misma API que usa Claude: cada botón construye un
    /// <see cref="HandApiRequest"/> y enseña la respuesta. Nada de lógica propia aquí, a propósito: lo que
    /// funcione en la ventana funciona igual por línea de comandos.
    /// </summary>
    public sealed class HandInteractionWindow : EditorWindow
    {
        private GameObject _prefab;
        private HandInteractionProfile _profile;
        private string _kind = "";
        private float _nudgeMm = 10f, _nudgeDeg = 10f;
        private string _output = "";
        private Vector2 _scroll, _outScroll;
        private readonly List<Texture2D> _thumbs = new();

        [MenuItem("Tools/Interaction Authoring", false, 2000)]
        public static void Open() => GetWindow<HandInteractionWindow>("Interaction Authoring");

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Objeto", EditorStyles.boldLabel);
            _prefab = (GameObject)EditorGUILayout.ObjectField("Wieldable", _prefab, typeof(GameObject), false);
            _profile = (HandInteractionProfile)EditorGUILayout.ObjectField("Perfil", _profile, typeof(HandInteractionProfile), false);
            if (_profile == null && _prefab != null)
                _profile = HandInteractionAnalyzer.AllProfiles().FirstOrDefault(p => p.wieldablePrefab == _prefab);
            if (_profile != null && _prefab == null) _prefab = _profile.wieldablePrefab;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = _prefab != null;
                if (GUILayout.Button("1. Inspect")) Run(new HandApiRequest { command = "inspect", prefab = PrefabPath });
                _kind = EditorGUILayout.TextField(_kind, GUILayout.Width(70));
                if (GUILayout.Button("2. Prepare"))
                {
                    var r = Run(new HandApiRequest { command = "prepare", prefab = PrefabPath, kind = _kind, overwrite = _profile != null && EditorUtility.DisplayDialog("Prepare", "¿Rehacer los objetivos del perfil existente?", "Rehacer", "Conservar") });
                    if (r.profile != null) _profile = AssetDatabase.LoadAssetAtPath<HandInteractionProfile>(r.profile.path);
                }
                GUI.enabled = true;
            }
            EditorGUILayout.LabelField("Kind vacío = el que sugiera Inspect (OneHand / TwoHand).", EditorStyles.miniLabel);

            if (_profile != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Perfil", EditorStyles.boldLabel);
                var so = new SerializedObject(_profile);
                foreach (var name in new[] { "kind", "modelNodeName", "gripMeshNodeName", "rightHand", "leftHand",
                             "secondaryFullWeightDistance", "secondaryZeroWeightDistance", "maxShoulderShiftMeters", "outputFolder" })
                    EditorGUILayout.PropertyField(so.FindProperty(name), true);
                so.ApplyModifiedProperties();

                EditorGUILayout.Space();
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("3. Preview + capturas")) { Run(new HandApiRequest { command = "preview", profile = ProfilePath, capture = true }); LoadThumbs("preview"); }
                    if (GUILayout.Button("4. Bake + validate")) Run(new HandApiRequest { command = "bake", profile = ProfilePath });
                    if (GUILayout.Button("Validate")) Run(new HandApiRequest { command = "validate", profile = ProfilePath });
                    if (GUILayout.Button("Capturas")) { Run(new HandApiRequest { command = "capture", profile = ProfilePath }); LoadThumbs("baked"); }
                }

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Corrección (vista del jugador; hornea al aplicar)", EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    _nudgeMm = EditorGUILayout.FloatField("mm", _nudgeMm);
                    _nudgeDeg = EditorGUILayout.FloatField("grados", _nudgeDeg);
                }
                foreach (var hand in new[] { "L", "R" })
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(hand, GUILayout.Width(14));
                        foreach (var d in new[] { "forward", "back", "up", "down", "left", "right", "in", "out" })
                            if (GUILayout.Button(d, EditorStyles.miniButton)) Nudge(hand, d);
                        foreach (var d in new[] { "clockwise", "counterclockwise" })
                            if (GUILayout.Button(d == "clockwise" ? "↻" : "↺", EditorStyles.miniButton, GUILayout.Width(24))) Nudge(hand, d);
                    }
            }

            EditorGUILayout.Space();
            foreach (var t in _thumbs.Where(t => t != null))
                GUILayout.Label(t, GUILayout.Width(position.width - 30), GUILayout.Height((position.width - 30) * 9f / 16f));
            EditorGUILayout.LabelField("Respuesta", EditorStyles.boldLabel);
            _outScroll = EditorGUILayout.BeginScrollView(_outScroll, GUILayout.MinHeight(220));
            EditorGUILayout.TextArea(_output, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndScrollView();
        }

        private string PrefabPath => _prefab != null ? AssetDatabase.GetAssetPath(_prefab) : "";
        private string ProfilePath => _profile != null ? AssetDatabase.GetAssetPath(_profile) : "";

        private void Nudge(string hand, string direction)
            => Run(new HandApiRequest
            {
                command = "nudge", profile = ProfilePath, hand = hand, direction = direction,
                millimeters = _nudgeMm, degrees = _nudgeDeg, bake = true,
            });

        private HandApiResponse Run(HandApiRequest req)
        {
            HandApiResponse res;
            try
            {
                EditorUtility.DisplayProgressBar("Interaction Authoring", req.command, 0.5f);
                res = HandInteractionApi.Execute(req);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            _output = JsonUtility.ToJson(res, true);
            Repaint();
            return res;
        }

        private void LoadThumbs(string prefix)
        {
            foreach (var t in _thumbs) if (t != null) DestroyImmediate(t);
            _thumbs.Clear();
            if (_profile == null) return;
            string dir = $"{HandInteractionApi.DefaultCaptureRoot}/{_profile.name}";
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir, prefix + "_*.png").OrderBy(f => f))
            {
                var tex = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
                tex.LoadImage(File.ReadAllBytes(file));
                _thumbs.Add(tex);
            }
        }

        private void OnDisable()
        {
            foreach (var t in _thumbs) if (t != null) DestroyImmediate(t);
        }
    }
}
#endif
