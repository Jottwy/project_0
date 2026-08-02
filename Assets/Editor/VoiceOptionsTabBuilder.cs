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

                AddRow(audioPanel, dropdown, "Micrófono", out var micGo);
                var chanRow = AddRow(audioPanel, dropdown, "Canal del micrófono", out var chanGo);
                AddRow(audioPanel, toggle, "Activar micrófono", out var micOnGo);
                AddRow(audioPanel, toggle, "Voz abierta (en vez de pulsar)", out var openGo);
                AddRow(audioPanel, toggle, "Puerta de ruido", out var gateGo);
                AddRow(audioPanel, toggle, "Nivel automático", out var agcGo);
                AddRow(audioPanel, slider, "Sensibilidad de voz abierta", out var thrGo);

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

                // El estado en vivo va en la etiqueta de la fila del micrófono: es donde el ojo ya
                // está mirando cuando algo no suena.
                Bind(ui, "_statusText", LabelOf(chanRow));

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.Refresh();
                Debug.Log("[Voz] Filas inyectadas en AudioPanel. Entra en Play → Opciones → Audio " +
                          "y baja: bajo los volúmenes debe salir 'VOZ DE PROXIMIDAD'.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        /// <summary>Retira lo de pasadas anteriores: las filas marcadas y, además, el panel y la
        /// pestaña que creaba la primera versión de este script y que nunca llegaron a verse.</summary>
        private static void CleanUp(Transform root, Transform audioPanel)
        {
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
