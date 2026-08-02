using System.Linq;
using BackroomsSurvival.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-046 — mete las filas de voz DENTRO de la pestaña Audio del menú de opciones.
    ///
    /// LA PRIMERA VERSIÓN CREABA UNA QUINTA PESTAÑA Y NO SE VEÍA. El motivo estaba anotado por
    /// adelantado: en este prefab no hay ningún componente ni evento que ate un panel con su
    /// pestaña, así que un panel nuevo se creaba pero nadie lo mostraba nunca. Metiendo las filas
    /// en un panel que YA funciona, el problema desaparece en vez de resolverse — y además la voz
    /// es audio, así que su sitio natural es donde están los volúmenes.
    ///
    /// CLONA LOS PREFABS DE FILA DEL VENDOR (`FPS_UI_OptionToggle`, `FPS_UI_OptionDropdown`,
    /// `FPS_UI_OptionSlider`, `FPS_UI_OptionCategory`) en vez de construir controles con uGUI. Esas
    /// filas usan TextMeshPro, colores y espaciados propios; hechas a mano funcionarían pero se
    /// verían de otro juego, que es justo lo que se pidió evitar.
    ///
    /// IDEMPOTENTE, y además LIMPIA lo que dejó la versión anterior (`VoicePanel` y `VoiceTab`).
    /// </summary>
    public static class VoiceOptionsTabBuilder
    {
        private const string PrefabPath =
            "Assets/PolymindGames/FPSCore/Prefabs/UI/Menu/FPS_UI_Options.prefab";

        private const string WidgetsPath = "Assets/PolymindGames/FPSCore/Prefabs/UI/Menu/Widgets/";

        /// <summary>Prefijo de todo lo que crea este script, para poder retirarlo sin tocar nada más.</summary>
        private const string Mark = "Voice_";

        [MenuItem("Backrooms/Build/Inyectar ajustes de Voz en la pestaña Audio")]
        public static void Build()
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            if (root == null) { Debug.LogError($"[Voz] No se pudo abrir {PrefabPath}"); return; }

            try
            {
                var audioPanel = FindChild(root.transform, "AudioPanel");
                if (audioPanel == null)
                {
                    Debug.LogError("[Voz] No encuentro 'AudioPanel'. El vendor cambió la estructura.");
                    return;
                }

                CleanUp(root.transform, audioPanel);

                var category = Load("FPS_UI_OptionCategory");
                var toggle = Load("FPS_UI_OptionToggle");
                var dropdown = Load("FPS_UI_OptionDropdown");
                var slider = Load("FPS_UI_OptionSlider");
                if (category == null || toggle == null || dropdown == null || slider == null) return;

                AddRow(audioPanel, category, "VOZ DE PROXIMIDAD", out _);
                // Fila PROPIA para el estado en vivo. La version anterior reutilizaba la
                // etiqueta de otra fila, y salia un "microfono apagado" donde deberia poner
                // "Microfono": una fila no puede ser dos cosas a la vez.
                var statusRow = AddRow(audioPanel, category, "...", out _);

                AddRow(audioPanel, dropdown, "Micrófono", out var micGo);
                AddRow(audioPanel, dropdown, "Canal del micrófono", out var chanGo);
                AddRow(audioPanel, toggle, "Activar micrófono", out var micOnGo);
                AddRow(audioPanel, toggle, "Voz abierta (en vez de pulsar)", out var openGo);
                AddRow(audioPanel, toggle, "Puerta de ruido", out var gateGo);
                AddRow(audioPanel, toggle, "Nivel automático", out var agcGo);
                AddRow(audioPanel, slider, "Sensibilidad de voz abierta", out var thrGo);
                AddRow(audioPanel, dropdown, "Tecla para hablar (mantener)", out var pttGo);
                AddRow(audioPanel, dropdown, "Tecla para activar/desactivar", out var togGo);
                AddRow(audioPanel, toggle, "Probar mi micrófono (oírme a mí)", out var selfGo);

                var ui = audioPanel.GetComponent<VoiceOptionsUI>();
                if (ui == null) ui = audioPanel.gameObject.AddComponent<VoiceOptionsUI>();

                // Los dos botones comunes viven FUERA del panel; se copian del AudioOptionsUI que
                // ya está en este mismo objeto, o "Aplicar" y "Restaurar" no harían nada con la voz.
                CopyButtons(audioPanel, ui);

                Bind(ui, "_deviceDropdown", micGo.GetComponentInChildren<TMP_Dropdown>(true));
                Bind(ui, "_channelDropdown", chanGo.GetComponentInChildren<TMP_Dropdown>(true));
                Bind(ui, "_micEnabledToggle", micOnGo.GetComponentInChildren<Toggle>(true));
                Bind(ui, "_openMicToggle", openGo.GetComponentInChildren<Toggle>(true));
                Bind(ui, "_noiseGateToggle", gateGo.GetComponentInChildren<Toggle>(true));
                Bind(ui, "_autoGainToggle", agcGo.GetComponentInChildren<Toggle>(true));
                Bind(ui, "_thresholdSlider", thrGo.GetComponentInChildren<Slider>(true));
                Bind(ui, "_pttKeyDropdown", pttGo.GetComponentInChildren<TMP_Dropdown>(true));
                Bind(ui, "_toggleKeyDropdown", togGo.GetComponentInChildren<TMP_Dropdown>(true));
                Bind(ui, "_selfTestToggle", selfGo.GetComponentInChildren<Toggle>(true));

                Bind(ui, "_statusText", LabelOf(statusRow));

                MakeScrollable(audioPanel);

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.Refresh();
                Debug.Log("[Voz] Filas inyectadas en AudioPanel. Entra en Play → Opciones → Audio " +
                          "y baja: bajo los volúmenes debe salir 'VOZ DE PROXIMIDAD'.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        /// <summary>
        /// Envuelve TODAS las filas del panel —las de volumen del vendor incluidas— en un
        /// ScrollRect. Al añadir siete filas el contenido dejó de caber y la última acababa
        /// pisando los botones de abajo.
        ///
        /// El VerticalLayoutGroup se MUEVE del panel al contenido: si se quedara en el panel,
        /// intentaría colocar el viewport como si fuera una fila más y aplastaría el scroll. Y el
        /// contenido lleva ContentSizeFitter vertical para que su altura la dicte la suma de las
        /// filas, que es lo que el ScrollRect necesita para tener recorrido.
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
                moved.padding = layout.padding;
                moved.spacing = layout.spacing;
                moved.childAlignment = layout.childAlignment;
                moved.childForceExpandWidth = layout.childForceExpandWidth;
                moved.childForceExpandHeight = layout.childForceExpandHeight;
                moved.childControlWidth = layout.childControlWidth;
                moved.childControlHeight = layout.childControlHeight;
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

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>Retira lo de pasadas anteriores: las filas marcadas y, además, el panel y la
        /// pestaña que creaba la primera versión de este script y que nunca llegaron a verse.</summary>
        private static void CleanUp(Transform root, Transform audioPanel)
        {
            // DESENVOLVER PRIMERO. El viewport se llama "Voice_..." igual que las filas nuevas, y
            // borrarlo sin sacar antes su contenido se llevaria por delante los cinco sliders de
            // volumen DEL VENDOR, que desde la pasada anterior viven dentro.
            var content = audioPanel.Find(Mark + "Viewport/" + Mark + "Content");
            if (content != null)
            {
                foreach (var row in content.Cast<Transform>().ToList())
                    row.SetParent(audioPanel, false);

                var moved = content.GetComponent<VerticalLayoutGroup>();
                if (moved != null && audioPanel.GetComponent<VerticalLayoutGroup>() == null)
                {
                    var back = audioPanel.gameObject.AddComponent<VerticalLayoutGroup>();
                    back.padding = moved.padding;
                    back.spacing = moved.spacing;
                    back.childAlignment = moved.childAlignment;
                    back.childForceExpandWidth = moved.childForceExpandWidth;
                    back.childForceExpandHeight = moved.childForceExpandHeight;
                    back.childControlWidth = moved.childControlWidth;
                    back.childControlHeight = moved.childControlHeight;
                }
            }

            var oldScroll = audioPanel.GetComponent<ScrollRect>();
            if (oldScroll != null) Object.DestroyImmediate(oldScroll, true);

            foreach (var t in audioPanel.Cast<Transform>().ToList())
                if (t.name.StartsWith(Mark)) Object.DestroyImmediate(t.gameObject);

            foreach (var stale in new[] { "VoicePanel", "VoiceTab" })
            {
                var t = FindChild(root, stale);
                if (t != null)
                {
                    Object.DestroyImmediate(t.gameObject);
                    Debug.Log($"[Voz] Retirado '{stale}' de la version anterior.");
                }
            }
        }

        private static GameObject Load(string name)
        {
            var p = AssetDatabase.LoadAssetAtPath<GameObject>(WidgetsPath + name + ".prefab");
            if (p == null) Debug.LogError($"[Voz] No encuentro el widget {name}.prefab");
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

        private static void CopyButtons(Transform panel, VoiceOptionsUI ui)
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
            if (value == null) { Debug.LogWarning($"[Voz] Sin control para '{field}'"); return; }
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop == null) { Debug.LogWarning($"[Voz] Campo '{field}' inexistente"); return; }
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
