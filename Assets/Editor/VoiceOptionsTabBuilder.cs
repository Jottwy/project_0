using System.Linq;
using BackroomsSurvival.UI;
using PolymindGames.UserInterface;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// ADR-046 — inyecta la pestaña "Voz" en el menú de opciones del vendor.
    ///
    /// DUPLICA `AudioPanel` EN VEZ DE CONSTRUIR UNO NUEVO, y es la decisión central de este
    /// script. Un panel de ese menú no es solo un RectTransform: lleva `AnimatedUIPanel` con su
    /// `_panelLayer` y `_canvasShowMode`, un `CanvasGroup`, un `Animator` con su override
    /// controller y un `VerticalLayoutGroup` calibrado. Reproducir todo eso de cero es inventarse
    /// media docena de valores que nadie documentó; duplicando, se heredan exactos.
    ///
    /// LO QUE ESTE SCRIPT NO PUEDE GARANTIZAR, dicho por delante: **no existe en el prefab ningún
    /// componente ni UnityEvent que ate un panel con su pestaña.** Se comprobó buscando quién
    /// referencia los componentes de `AudioPanel`: nadie. Así que el conmutado tiene que
    /// resolverse en runtime, con toda probabilidad por ORDEN entre los hijos de `Tabs` y los
    /// paneles. Por eso aquí se duplica el ÚLTIMO de cada uno y se deja al final: si la
    /// correspondencia es por índice, encaja sola. Si al probarlo la pestaña no cambia de panel,
    /// ese es el punto exacto a mirar y no hay que dudar del resto.
    ///
    /// IDEMPOTENTE: borra lo que hubiera puesto antes, así que se puede ejecutar tantas veces como
    /// haga falta mientras iteramos.
    /// </summary>
    public static class VoiceOptionsTabBuilder
    {
        private const string PrefabPath =
            "Assets/PolymindGames/FPSCore/Prefabs/UI/Menu/FPS_UI_Options.prefab";

        private const string PanelName = "VoicePanel";
        private const string TabName = "VoiceTab";

        [MenuItem("Backrooms/Build/Inyectar pestaña de Voz en el menú de opciones")]
        public static void Build()
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            if (root == null)
            {
                Debug.LogError($"[VoiceTab] No se pudo abrir {PrefabPath}");
                return;
            }

            try
            {
                var audioPanel = FindChild(root.transform, "AudioPanel");
                var tabs = FindChild(root.transform, "Tabs");
                if (audioPanel == null || tabs == null)
                {
                    Debug.LogError("[VoiceTab] No encuentro 'AudioPanel' o 'Tabs' en el prefab. " +
                                   "El vendor ha cambiado la estructura: hay que revisar este script.");
                    return;
                }

                // Idempotencia: fuera lo de la pasada anterior, antes de nada.
                DestroyIfExists(root.transform, PanelName);
                DestroyIfExists(tabs, TabName);

                var panel = ClonePanel(audioPanel);
                BuildRows(panel);
                var tab = CloneTab(tabs);

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                Debug.Log($"[VoiceTab] Listo. '{panel.name}' junto a los demás paneles y " +
                          $"'{tab.name}' al final de Tabs. Abre el prefab y comprueba que la " +
                          "pestaña conmuta; si no lo hace, el enlace pestaña→panel es por " +
                          "referencia y no por orden, y hay que atarlo a mano.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>Duplica AudioPanel y le cambia el componente de opciones.</summary>
        private static Transform ClonePanel(Transform audioPanel)
        {
            var clone = Object.Instantiate(audioPanel.gameObject, audioPanel.parent);
            clone.name = PanelName;
            clone.transform.SetSiblingIndex(audioPanel.GetSiblingIndex() + 1);

            // El componente de audio se va; el de voz entra. Se conservan AnimatedUIPanel,
            // CanvasGroup, Animator y el layout, que es justo lo que se venía a heredar.
            var audioUi = clone.GetComponent<AudioOptionsUI>();
            SelectableButton restore = null, apply = null;
            if (audioUi != null)
            {
                // Los dos botones comunes viven FUERA del panel; sin recuperarlos del original,
                // "Restaurar valores" y "Aplicar" quedarían sin enlazar en la pestaña nueva.
                restore = GetPrivate<SelectableButton>(audioUi, "_restoreDefaultsButton");
                apply = GetPrivate<SelectableButton>(audioUi, "_applyChangesButton");
                Object.DestroyImmediate(audioUi, true);
            }

            var voiceUi = clone.AddComponent<VoiceOptionsUI>();
            SetPrivate(voiceUi, "_restoreDefaultsButton", restore);
            SetPrivate(voiceUi, "_applyChangesButton", apply);

            // Las filas heredadas son sliders de volumen: no sirven y confundirían.
            foreach (Transform child in clone.transform.Cast<Transform>().ToList())
                Object.DestroyImmediate(child.gameObject);

            return clone.transform;
        }

        /// <summary>Construye las filas de la pestaña y las enlaza al componente.</summary>
        private static void BuildRows(Transform panel)
        {
            var ui = panel.GetComponent<VoiceOptionsUI>();

            SetPrivate(ui, "_levelFill", BuildMeter(panel, out var levelText));
            SetPrivate(ui, "_levelText", levelText);
            SetPrivate(ui, "_deviceDropdown", BuildDropdown(panel, "Micrófono"));
            SetPrivate(ui, "_channelDropdown", BuildDropdown(panel, "Canal"));
            SetPrivate(ui, "_micEnabledToggle", BuildToggle(panel, "Micrófono encendido"));
            SetPrivate(ui, "_openMicToggle", BuildToggle(panel, "Voz abierta (en vez de pulsar)"));
            SetPrivate(ui, "_noiseGateToggle", BuildToggle(panel, "Puerta de ruido"));
            SetPrivate(ui, "_autoGainToggle", BuildToggle(panel, "Nivel automático"));
            SetPrivate(ui, "_thresholdSlider", BuildSlider(panel, "Umbral de voz abierta", out var thrValue));
            SetPrivate(ui, "_thresholdValue", thrValue);
        }

        // ── Constructores de fila ────────────────────────────────────────────────
        //
        // Se construyen con uGUI plano y NO con los prefabs de fila del vendor
        // (FPS_UI_OptionToggle, FPS_UI_OptionDropdown): esos usan `SerializeReference` para sus
        // estados de selección, y escribir referencias gestionadas desde un script de editor es
        // frágil de una forma que solo se descubre en runtime. Fila plana y visible es preferible
        // a fila bonita que puede no deserializar.

        private static Transform NewRow(Transform parent, string label, float height)
        {
            var row = new GameObject(label, typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var rt = (RectTransform)row.transform;
            rt.sizeDelta = new Vector2(0f, height);

            var le = row.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;

            var text = new GameObject("Label", typeof(RectTransform)).AddComponent<Text>();
            text.transform.SetParent(row.transform, false);
            var trt = (RectTransform)text.transform;
            trt.anchorMin = new Vector2(0f, 0f);
            trt.anchorMax = new Vector2(0.45f, 1f);
            trt.offsetMin = Vector2.zero;
            trt.offsetMax = Vector2.zero;
            text.text = label;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = 20;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = new Color(0.92f, 0.90f, 0.84f);

            return row.transform;
        }

        private static Transform ControlSlot(Transform row)
        {
            var slot = new GameObject("Control", typeof(RectTransform));
            slot.transform.SetParent(row, false);
            var rt = (RectTransform)slot.transform;
            rt.anchorMin = new Vector2(0.47f, 0.15f);
            rt.anchorMax = new Vector2(1f, 0.85f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            return slot.transform;
        }

        private static Toggle BuildToggle(Transform panel, string label)
        {
            var slot = ControlSlot(NewRow(panel, label, 34f));
            var go = DefaultControls.CreateToggle(new DefaultControls.Resources());
            go.transform.SetParent(slot, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.anchoredPosition = new Vector2(12f, 0f);
            var childText = go.GetComponentInChildren<Text>();
            if (childText != null) Object.DestroyImmediate(childText.gameObject);
            return go.GetComponent<Toggle>();
        }

        private static Dropdown BuildDropdown(Transform panel, string label)
        {
            var slot = ControlSlot(NewRow(panel, label, 40f));
            var go = DefaultControls.CreateDropdown(new DefaultControls.Resources());
            go.transform.SetParent(slot, false);
            Stretch((RectTransform)go.transform);
            foreach (var t in go.GetComponentsInChildren<Text>(true))
                t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return go.GetComponent<Dropdown>();
        }

        private static Slider BuildSlider(Transform panel, string label, out Text value)
        {
            var row = NewRow(panel, label, 34f);
            var slot = ControlSlot(row);

            var go = DefaultControls.CreateSlider(new DefaultControls.Resources());
            go.transform.SetParent(slot, false);
            var srt = (RectTransform)go.transform;
            srt.anchorMin = new Vector2(0f, 0.5f);
            srt.anchorMax = new Vector2(0.78f, 0.5f);
            srt.offsetMin = new Vector2(0f, -8f);
            srt.offsetMax = new Vector2(0f, 8f);

            value = new GameObject("Value", typeof(RectTransform)).AddComponent<Text>();
            value.transform.SetParent(slot, false);
            var vrt = (RectTransform)value.transform;
            vrt.anchorMin = new Vector2(0.8f, 0f);
            vrt.anchorMax = Vector2.one;
            vrt.offsetMin = Vector2.zero;
            vrt.offsetMax = Vector2.zero;
            value.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            value.fontSize = 18;
            value.alignment = TextAnchor.MiddleRight;
            value.color = new Color(0.85f, 0.85f, 0.78f);
            value.text = "0.020";

            return go.GetComponent<Slider>();
        }

        /// <summary>Medidor de nivel: fondo + relleno anclado a la izquierda + texto de estado.
        /// Va el PRIMERO del panel porque es lo que contesta "¿por qué no me oyen?".</summary>
        private static Image BuildMeter(Transform panel, out Text status)
        {
            var row = NewRow(panel, "Nivel de entrada", 46f);

            var bg = new GameObject("MeterBg", typeof(RectTransform)).AddComponent<Image>();
            bg.transform.SetParent(ControlSlot(row), false);
            Stretch((RectTransform)bg.transform);
            bg.color = new Color(1f, 1f, 1f, 0.12f);

            var fill = new GameObject("MeterFill", typeof(RectTransform)).AddComponent<Image>();
            fill.transform.SetParent(bg.transform, false);
            var frt = (RectTransform)fill.transform;
            frt.anchorMin = Vector2.zero;
            frt.anchorMax = new Vector2(0f, 1f);
            frt.offsetMin = Vector2.zero;
            frt.offsetMax = Vector2.zero;
            fill.color = new Color(0.55f, 0.55f, 0.5f);

            status = new GameObject("Status", typeof(RectTransform)).AddComponent<Text>();
            status.transform.SetParent(row, false);
            var srt = (RectTransform)status.transform;
            srt.anchorMin = new Vector2(0f, 0f);
            srt.anchorMax = new Vector2(0.45f, 0.45f);
            srt.offsetMin = Vector2.zero;
            srt.offsetMax = Vector2.zero;
            status.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            status.fontSize = 15;
            status.alignment = TextAnchor.MiddleLeft;
            status.color = new Color(0.7f, 0.7f, 0.65f);
            status.text = "…";

            return fill;
        }

        private static Transform CloneTab(Transform tabs)
        {
            if (tabs.childCount == 0)
            {
                Debug.LogWarning("[VoiceTab] 'Tabs' no tiene hijos; no hay pestaña que duplicar.");
                return tabs;
            }

            var template = tabs.GetChild(tabs.childCount - 1);
            var clone = Object.Instantiate(template.gameObject, tabs);
            clone.name = TabName;
            clone.transform.SetAsLastSibling();

            // El texto puede ser Text o TextMeshPro según el prefab del vendor: se cubren los dos
            // por nombre de tipo, para no atar este script a TMP.
            foreach (var c in clone.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var t = c.GetType();
                if (t.Name == "Text" || t.Name == "TextMeshProUGUI")
                {
                    var prop = t.GetProperty("text");
                    prop?.SetValue(c, "VOZ");
                }
            }
            return clone.transform;
        }

        // ── Utilidades ───────────────────────────────────────────────────────────

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static Transform FindChild(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        private static void DestroyIfExists(Transform root, string name)
        {
            var existing = FindChild(root, name);
            if (existing != null) Object.DestroyImmediate(existing.gameObject);
        }

        /// <summary>SerializedObject y no reflexión: es la única vía que Unity considera un cambio
        /// de verdad sobre un prefab y que por tanto se guarda.</summary>
        private static void SetPrivate(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            if (prop == null)
            {
                Debug.LogWarning($"[VoiceTab] Campo '{field}' no existe en {target.GetType().Name}");
                return;
            }
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static T GetPrivate<T>(Object target, string field) where T : Object
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            return prop?.objectReferenceValue as T;
        }
    }
}
