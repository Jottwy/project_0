using System.Linq;
using BackroomsSurvival.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Mete las filas visuales que faltaban DENTRO de la pestaña Graphics del menú de opciones.
    ///
    /// CALCO DE <see cref="VoiceOptionsTabBuilder"/>, y por el mismo motivo: en este prefab no hay
    /// ningún componente ni evento que ate un panel con su pestaña, así que una pestaña nueva se
    /// crearía y no la mostraría nadie. Las filas van al panel que YA funciona.
    ///
    /// CLONA LOS PREFABS DE FILA DEL VENDOR en vez de construir controles con uGUI: hechos a mano
    /// funcionarían pero se verían de otro juego.
    ///
    /// IDEMPOTENTE: retira lo de pasadas anteriores (todo lo que empieza por <c>Gfx_</c>) antes de
    /// volver a montarlo, desenvolviendo primero el scroll para no llevarse por delante las filas
    /// del vendor que viven dentro desde la pasada anterior.
    /// </summary>
    public static class GraphicsOptionsRowsBuilder
    {
        private const string PrefabPath =
            "Assets/PolymindGames/FPSCore/Prefabs/UI/Menu/FPS_UI_Options.prefab";

        private const string WidgetsPath = "Assets/PolymindGames/FPSCore/Prefabs/UI/Menu/Widgets/";

        /// <summary>Prefijo de todo lo que crea este script, para poder retirarlo sin tocar nada más.</summary>
        private const string Mark = "Gfx_";

        [MenuItem("Backrooms/Build/Inyectar ajustes gráficos en la pestaña Graphics")]
        public static void Build()
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            if (root == null) { Debug.LogError($"[Gráficos] No se pudo abrir {PrefabPath}"); return; }

            try
            {
                var panel = FindChild(root.transform, "GraphicsPanel");
                if (panel == null)
                {
                    Debug.LogError("[Gráficos] No encuentro 'GraphicsPanel'. El vendor cambió la estructura.");
                    return;
                }

                CleanUp(panel);

                var category = Load("FPS_UI_OptionCategory");
                var toggle = Load("FPS_UI_OptionToggle");
                var dropdown = Load("FPS_UI_OptionDropdown");
                var slider = Load("FPS_UI_OptionSlider");
                if (category == null || toggle == null || dropdown == null || slider == null) return;

                // Etiquetas en INGLÉS por decisión de Joel.
                AddRow(panel, category, "QUALITY PRESET", out _);
                AddRow(panel, dropdown, "Quality Preset", out var presetGo);

                AddRow(panel, category, "RENDERING", out _);
                AddRow(panel, slider, "Render Scale", out var renderScaleGo);
                AddRow(panel, dropdown, "Upscaling", out var upscalingGo);
                AddRow(panel, toggle, "HDR", out var hdrGo);
                AddRow(panel, dropdown, "Anti-Aliasing", out var aaGo);
                AddRow(panel, dropdown, "MSAA", out var msaaGo);

                AddRow(panel, category, "SHADOWS", out _);
                AddRow(panel, toggle, "Shadows", out var shadowsGo);
                AddRow(panel, dropdown, "Shadow Quality", out var shadowQualityGo);
                AddRow(panel, slider, "Shadow Distance", out var shadowDistanceGo);
                AddRow(panel, dropdown, "Shadow Cascades", out var cascadesGo);
                AddRow(panel, toggle, "Additional Light Shadows", out var addLightShadowsGo);

                AddRow(panel, category, "POST-PROCESSING", out _);
                AddRow(panel, toggle, "Bloom", out var bloomGo);
                AddRow(panel, toggle, "Motion Blur", out var motionBlurGo);
                AddRow(panel, toggle, "Depth of Field", out var dofGo);
                AddRow(panel, toggle, "Chromatic Aberration", out var chromaticGo);

                AddRow(panel, category, "TEXTURES & DETAIL", out _);
                AddRow(panel, dropdown, "Texture Quality", out var textureQualityGo);
                AddRow(panel, dropdown, "Anisotropic Filtering", out var anisotropicGo);
                AddRow(panel, slider, "LOD Bias", out var lodBiasGo);

                var ui = panel.GetComponent<BackroomsGraphicsOptionsUI>();
                if (ui == null) ui = panel.gameObject.AddComponent<BackroomsGraphicsOptionsUI>();

                // Los dos botones comunes viven FUERA del panel; se copian del GraphicsOptionsUI
                // que ya está en este mismo objeto, o "Apply" y "Restore" no harían nada con esto.
                CopyButtons(panel, ui);

                Bind(ui, "_presetDropdown", DropdownIn(presetGo));

                Bind(ui, "_renderScaleSlider", SliderIn(renderScaleGo));
                Bind(ui, "_upscalingDropdown", DropdownIn(upscalingGo));
                Bind(ui, "_hdrToggle", ToggleIn(hdrGo));
                Bind(ui, "_antiAliasingDropdown", DropdownIn(aaGo));
                Bind(ui, "_msaaDropdown", DropdownIn(msaaGo));

                Bind(ui, "_shadowsToggle", ToggleIn(shadowsGo));
                Bind(ui, "_shadowQualityDropdown", DropdownIn(shadowQualityGo));
                Bind(ui, "_shadowDistanceSlider", SliderIn(shadowDistanceGo));
                Bind(ui, "_shadowCascadesDropdown", DropdownIn(cascadesGo));
                Bind(ui, "_additionalLightShadowsToggle", ToggleIn(addLightShadowsGo));

                Bind(ui, "_bloomToggle", ToggleIn(bloomGo));
                Bind(ui, "_motionBlurToggle", ToggleIn(motionBlurGo));
                Bind(ui, "_depthOfFieldToggle", ToggleIn(dofGo));
                Bind(ui, "_chromaticAberrationToggle", ToggleIn(chromaticGo));

                Bind(ui, "_textureQualityDropdown", DropdownIn(textureQualityGo));
                Bind(ui, "_anisotropicDropdown", DropdownIn(anisotropicGo));
                Bind(ui, "_lodBiasSlider", SliderIn(lodBiasGo));

                MakeScrollable(panel);

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.Refresh();
                Debug.Log("[Gráficos] 18 ajustes inyectados en GraphicsPanel. Entra en Play → " +
                          "Options → Graphics y baja: bajo el campo de visión debe salir " +
                          "'QUALITY PRESET'. AÚN NO APLICAN NADA, es el menú.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static TMP_Dropdown DropdownIn(GameObject go) => go.GetComponentInChildren<TMP_Dropdown>(true);
        private static Toggle ToggleIn(GameObject go) => go.GetComponentInChildren<Toggle>(true);
        private static Slider SliderIn(GameObject go) => go.GetComponentInChildren<Slider>(true);

        /// <summary>
        /// Envuelve TODAS las filas del panel —las del vendor incluidas— en un ScrollRect. Con
        /// veintitrés filas nuevas el contenido no cabe ni de lejos.
        ///
        /// El VerticalLayoutGroup se MUEVE del panel al contenido: si se quedara en el panel,
        /// colocaría el viewport como si fuera una fila más y aplastaría el scroll.
        /// </summary>
        private static void MakeScrollable(Transform panel)
        {
            if (panel.Find(Mark + "Viewport") != null) return; // ya envuelto

            var viewport = new GameObject(Mark + "Viewport", typeof(RectTransform)).transform;
            viewport.SetParent(panel, false);
            Stretch((RectTransform)viewport);
            viewport.gameObject.AddComponent<RectMask2D>();

            var content = new GameObject(Mark + "Content", typeof(RectTransform)).transform;
            content.SetParent(viewport, false);
            var crt = (RectTransform)content;
            crt.anchorMin = new Vector2(0f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.offsetMin = Vector2.zero;
            crt.offsetMax = Vector2.zero;

            foreach (var row in panel.Cast<Transform>().ToList())
            {
                if (row == viewport) continue;
                row.SetParent(content, false);
            }

            var layout = panel.GetComponent<VerticalLayoutGroup>();
            if (layout != null)
            {
                var moved = content.gameObject.AddComponent<VerticalLayoutGroup>();
                CopyLayout(layout, moved);
                Object.DestroyImmediate(layout, true);
            }

            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = panel.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 28f;
            scroll.viewport = (RectTransform)viewport;
            scroll.content = crt;
        }

        private static void CopyLayout(VerticalLayoutGroup from, VerticalLayoutGroup to)
        {
            to.padding = from.padding;
            to.spacing = from.spacing;
            to.childAlignment = from.childAlignment;
            to.childForceExpandWidth = from.childForceExpandWidth;
            to.childForceExpandHeight = from.childForceExpandHeight;
            to.childControlWidth = from.childControlWidth;
            to.childControlHeight = from.childControlHeight;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Retira lo de pasadas anteriores. DESENVUELVE PRIMERO: el viewport se llama "Gfx_..."
        /// igual que las filas nuevas, y borrarlo sin sacar antes su contenido se llevaría por
        /// delante las filas DEL VENDOR, que desde la pasada anterior viven dentro.
        /// </summary>
        private static void CleanUp(Transform panel)
        {
            var content = panel.Find(Mark + "Viewport/" + Mark + "Content");
            if (content != null)
            {
                foreach (var row in content.Cast<Transform>().ToList())
                    row.SetParent(panel, false);

                var moved = content.GetComponent<VerticalLayoutGroup>();
                if (moved != null && panel.GetComponent<VerticalLayoutGroup>() == null)
                    CopyLayout(moved, panel.gameObject.AddComponent<VerticalLayoutGroup>());
            }

            var oldScroll = panel.GetComponent<ScrollRect>();
            if (oldScroll != null) Object.DestroyImmediate(oldScroll, true);

            foreach (var t in panel.Cast<Transform>().ToList())
                if (t.name.StartsWith(Mark)) Object.DestroyImmediate(t.gameObject);
        }

        private static GameObject Load(string name)
        {
            var p = AssetDatabase.LoadAssetAtPath<GameObject>(WidgetsPath + name + ".prefab");
            if (p == null) Debug.LogError($"[Gráficos] No encuentro el widget {name}.prefab");
            return p;
        }

        private static Transform AddRow(Transform panel, GameObject prefab, string label, out GameObject go)
        {
            go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, panel);
            go.name = Mark + label;
            var t = go.transform;
            t.SetAsLastSibling();

            var text = LabelOf(t);
            if (text != null) text.text = label;
            return t;
        }

        /// <summary>La etiqueta de una fila es su PRIMER TextMeshPro. Buscarlo por nombre sería
        /// atarse a la jerarquía interna de un prefab de vendor que puede cambiar.</summary>
        private static TextMeshProUGUI LabelOf(Transform row) =>
            row.GetComponentInChildren<TextMeshProUGUI>(true);

        private static void CopyButtons(Transform panel, BackroomsGraphicsOptionsUI ui)
        {
            foreach (var other in panel.GetComponents<MonoBehaviour>())
            {
                if (other == null || other == ui) continue;
                var so = new SerializedObject(other);
                var restore = so.FindProperty("_restoreDefaultsButton");
                var apply = so.FindProperty("_applyChangesButton");
                if (restore == null && apply == null) continue;

                var target = new SerializedObject(ui);
                if (restore != null)
                    target.FindProperty("_restoreDefaultsButton").objectReferenceValue = restore.objectReferenceValue;
                if (apply != null)
                    target.FindProperty("_applyChangesButton").objectReferenceValue = apply.objectReferenceValue;
                target.ApplyModifiedPropertiesWithoutUndo();
                return;
            }
        }

        /// <summary>SerializedObject y no reflexión: es la única vía que Unity registra como cambio
        /// real sobre un prefab, y por tanto la única que se guarda.</summary>
        private static void Bind(Object target, string field, Object value)
        {
            if (value == null) { Debug.LogWarning($"[Gráficos] Sin control para '{field}'"); return; }
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop == null) { Debug.LogWarning($"[Gráficos] Campo '{field}' inexistente"); return; }
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static Transform FindChild(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }
    }
}
