#if UNITY_EDITOR
using System.Collections.Generic;
using BackroomsSurvival.UI;
using PolymindGames;
using PolymindGames.UserInterface;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Rebanada 1a del inventario (INVENTORY-ROADMAP.md, D12): la UI del jugador pasa a ser una
    /// VARIANTE del prefab del vendor, fuera de su árbol, con la piel «Bajo el fluorescente»
    /// aplicada por código. Menú Backrooms/UI/Build Inventory Variant.
    ///
    /// POR QUÉ VARIANTE Y NO UI PROPIA: lo caro del inventario es la fontanería — drag, tooltip,
    /// acciones, preview del personaje, contexto de input, cursor, Escape — y todo eso ya funciona
    /// en <c>STP_UI_Player</c>. Una variante lo hereda entero y sólo guarda como overrides lo que
    /// cambiamos: sprites, fuentes, colores y un fondo nuevo. Un reimport del vendor actualiza la
    /// base sin pisar la variante. Nada dentro de <c>Assets/PolymindGames</c> se toca.
    ///
    /// IDEMPOTENTE: relanzarlo reaplica el tema sobre la variante existente (la crea si falta) y
    /// vuelve a apuntar el <c>GameMode</c> de la escena viva. Encuentra las piezas por NOMBRE dentro
    /// de la variante — es el mismo acoplamiento que tendría hacerlo a mano en el inspector, y aquí
    /// al menos queda escrito y avisa de lo que no encuentra.
    ///
    /// LO QUE NO HACE (rebanada 1b): reordenar las tres columnas, la cincha de la funda como
    /// regla de carga, el papel del inspector a tamaño de columna. Aquí sólo cambia la piel de
    /// lo que ya está donde está.
    /// </summary>
    public static class BackroomsInventoryUiBuilder
    {
        public const string BasePath = "Assets/PolymindGames/STP/Prefabs/UI/STP_UI_Player.prefab";
        public const string VariantFolder = "Assets/Prefabs/UI";
        public const string VariantPath = VariantFolder + "/BR_UI_Player.prefab";
        public const string ScenePath = "Assets/PolymindGames/STP/Demo/Scenes/Showcase/STP_Showcase.unity";
        public const string BackdropName = "BR_Backdrop";
        public const string StrapName = "BR_Strap";

        // TMP que van en monoespaciada (cintas, cifras); el resto, en Barlow.
        private static readonly HashSet<string> MonoTextNames = new HashSet<string>
        {
            "Category", "WeightText", "Stack", "ItemStackText", "Text",
        };

        [MenuItem("Backrooms/UI/Build Inventory Variant")]
        public static void Build()
        {
            var theme = AssetDatabase.LoadAssetAtPath<BackroomsUiTheme>(BackroomsUiThemeBuilder.ThemePath);
            if (theme == null)
            {
                Debug.LogError("[InventoryUiBuilder] Falta el tema: lanza antes Backrooms/UI/Build Theme.");
                return;
            }

            EnsureVariant();

            var root = PrefabUtility.LoadPrefabContents(VariantPath);
            try
            {
                var report = new Report();
                ApplyTheme(root, theme, report);
                PrefabUtility.SaveAsPrefabAsset(root, VariantPath);
                Debug.Log($"[InventoryUiBuilder] Variante guardada en {VariantPath}. {report}");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            PointGameModeAtVariant();
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// La escena viva hereda el prefab de UI del <c>STP_GameMode.prefab</c> del vendor; aquí se
        /// sobreescribe SOLO ese campo en la instancia de la escena, igual que ya está sobreescrito
        /// el prefab del jugador. Un reimport del paquete pisa la escena: relanzar el menú lo repone.
        /// </summary>
        [MenuItem("Backrooms/UI/Point GameMode at BR_UI_Player")]
        public static void PointGameModeAtVariant()
        {
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
            var ui = variant != null ? variant.GetComponent<PlayerUI>() : null;
            if (ui == null)
            {
                Debug.LogError($"[InventoryUiBuilder] '{VariantPath}' no existe o no lleva PlayerUI en la raíz.");
                return;
            }

            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var gameModes = Object.FindObjectsByType<GameMode>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (gameModes.Length != 1)
            {
                Debug.LogError($"[InventoryUiBuilder] Esperaba UN GameMode en {ScenePath}, hay {gameModes.Length}.");
                return;
            }

            var so = new SerializedObject(gameModes[0]);
            var prop = so.FindProperty("_playerUIPrefab");
            if (prop == null)
            {
                Debug.LogError("[InventoryUiBuilder] GameMode ya no tiene '_playerUIPrefab': el vendor cambió el campo.");
                return;
            }

            if (prop.objectReferenceValue == ui)
            {
                Debug.Log("[InventoryUiBuilder] GameMode ya apunta a la variante.");
                return;
            }

            prop.objectReferenceValue = ui;
            so.ApplyModifiedProperties();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[InventoryUiBuilder] GameMode._playerUIPrefab -> {VariantPath} en {ScenePath}.");
        }

        private static void EnsureVariant()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath) != null) return;

            EnsureFolder(VariantFolder);
            var basePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BasePath);
            if (basePrefab == null) throw new System.IO.FileNotFoundException(BasePath);

            // SaveAsPrefabAsset sobre una INSTANCIA de prefab crea una variante, no una copia.
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(basePrefab);
            try
            {
                instance.name = "BR_UI_Player";
                PrefabUtility.SaveAsPrefabAsset(instance, VariantPath);
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }
            Debug.Log($"[InventoryUiBuilder] Variante creada: {VariantPath} (base {BasePath}).");
        }

        // ─── Tema ────────────────────────────────────────────────────────────

        private static void ApplyTheme(GameObject root, BackroomsUiTheme theme, Report report)
        {
            var inventoryUI = root.GetComponentInChildren<InventoryUI>(true);
            if (inventoryUI == null)
            {
                report.Missing("InventoryUI");
                return;
            }
            var inv = inventoryUI.transform;

            BuildBackdrop(inv, theme, report);

            // Fuentes y tinta: todo el inventario y la funda. El papel y las cintas se repintan después.
            foreach (var tmp in inv.GetComponentsInChildren<TextMeshProUGUI>(true))
                Retype(tmp, theme, MonoTextNames.Contains(tmp.name) ? theme.Mono : theme.Display, theme.Ink);

            // Huecos: cada slot es un rebaje; la barra de durabilidad brilla como el tubo.
            int slots = 0;
            foreach (var slot in inv.GetComponentsInChildren<ItemSlotUI>(true))
            {
                if (slot.TryGetComponent<Image>(out var bg)) SetSprite(bg, theme.SunkenSlot, Color.white);
                Paint(slot.transform, "DurabilityBG", theme.Tape);
                Paint(slot.transform, "DurabilityBar", theme.Fluorescent);
                slots++;
            }
            report.Count("slots", slots);

            // Cintas Dymo: cabeceras, tooltip, botón de ordenar y las acciones del objeto.
            int tapes = 0;
            foreach (var t in inv.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != "Header" && t.name != "SortBtn") continue;
                if (!t.TryGetComponent<Image>(out var img)) continue;
                Tape(t, img, theme);
                tapes++;
            }
            report.Count("cintas", tapes);

            var tooltip = inv.GetComponentInChildren<ItemTooltipUI>(true);
            if (tooltip != null && tooltip.TryGetComponent<Image>(out var tipImg)) Tape(tooltip.transform, tipImg, theme);
            else report.Missing("ItemTooltipUI");

            var actions = inv.GetComponentInChildren<ItemActionsUI>(true);
            if (actions != null)
                foreach (var img in actions.GetComponentsInChildren<Image>(true)) Tape(img.transform, img, theme);
            else report.Missing("ItemActionsUI");

            // La caja del inventario: suelo oscuro sin marco. El inspector: papel de almacén.
            var panel = Find(inv, "MiddleGroup/Inventory");
            if (panel != null && panel.TryGetComponent<Image>(out var panelImg)) SetSprite(panelImg, null, WithAlpha(theme.Ground, 0.92f));
            else report.Missing("MiddleGroup/Inventory (Image)");

            var inspector = Find(inv, "MiddleGroup/Inventory/Inspector");
            if (inspector != null)
            {
                if (inspector.TryGetComponent<Image>(out var paper)) SetSprite(paper, theme.WarehouseLabel, Color.white);
                // Tinta de papel para el texto del inspector, salvo lo que va sobre cinta (acciones, cabecera).
                foreach (var tmp in inspector.GetComponentsInChildren<TextMeshProUGUI>(true))
                    if (tmp.GetComponentInParent<ItemActionsUI>() == null && !IsOnTape(tmp.transform, inspector))
                        tmp.color = theme.PaperInk;
            }
            else report.Missing("MiddleGroup/Inventory/Inspector");

            // La carga: regla oscura con el relleno de tinta.
            Paint(inv, "MiddleGroup/Inventory/Weight/WeightBarBG", theme.Tape);
            Paint(inv, "MiddleGroup/Inventory/Weight/WeightBarBG/WeightBar", theme.Ink);

            BuildStrap(root, theme, report);
        }

        /// <summary>Fondo a pantalla completa, primer hijo del inventario para quedar debajo de todo.</summary>
        private static void BuildBackdrop(Transform inv, BackroomsUiTheme theme, Report report)
        {
            var backdrop = inv.Find(BackdropName);
            if (backdrop == null)
            {
                var go = new GameObject(BackdropName, typeof(RectTransform), typeof(CanvasGroup), typeof(Image),
                    typeof(BackroomsInventoryBackdrop));
                backdrop = go.transform;
                backdrop.SetParent(inv, false);
                backdrop.SetAsFirstSibling();
                var rt = (RectTransform)backdrop;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                report.Count("backdrop creado", 1);
            }
            var img = backdrop.GetComponent<Image>();
            img.sprite = null;
            img.color = WithAlpha(theme.Ground, 0.88f);
            img.raycastTarget = false;
            var group = backdrop.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;
        }

        /// <summary>La funda es una cincha de nylon: una Image de fondo en el propio Layout de la hotbar.</summary>
        private static void BuildStrap(GameObject root, BackroomsUiTheme theme, Report report)
        {
            var hotbar = root.GetComponentInChildren<HotbarUI>(true);
            if (hotbar == null) { report.Missing("HotbarUI"); return; }
            var layout = hotbar.transform.Find("Layout");
            if (layout == null) { report.Missing("HotbarUI/Layout"); return; }

            foreach (var tmp in hotbar.GetComponentsInChildren<TextMeshProUGUI>(true))
                Retype(tmp, theme, theme.Mono, theme.Ink);
            foreach (var slot in hotbar.GetComponentsInChildren<ItemSlotUI>(true))
                if (slot.TryGetComponent<Image>(out var bg)) SetSprite(bg, theme.SunkenSlot, Color.white);

            var strap = layout.Find(StrapName);
            if (strap == null)
            {
                var go = new GameObject(StrapName, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
                strap = go.transform;
                strap.SetParent(layout, false);
                strap.SetAsFirstSibling();
                go.GetComponent<LayoutElement>().ignoreLayout = true;
                var rt = (RectTransform)strap;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = new Vector2(-10f, -6f);
                rt.offsetMax = new Vector2(10f, 6f);
                report.Count("cincha creada", 1);
            }
            var img = strap.GetComponent<Image>();
            SetSprite(img, theme.NylonStrap, Color.white);
            img.type = Image.Type.Tiled;
            img.raycastTarget = false;
        }

        private static void Tape(Transform t, Image img, BackroomsUiTheme theme)
        {
            SetSprite(img, theme.DymoTape, Color.white);
            foreach (var tmp in t.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                Retype(tmp, theme, theme.MonoBold, theme.TapeInk);
                tmp.fontStyle |= FontStyles.UpperCase;
                tmp.characterSpacing = 6f;
            }
        }

        private static void Retype(TextMeshProUGUI tmp, BackroomsUiTheme theme, TMP_FontAsset font, Color color)
        {
            if (font != null) tmp.font = font;
            tmp.color = color;
        }

        private static void SetSprite(Image img, Sprite sprite, Color color)
        {
            img.sprite = sprite;
            img.color = color;
            if (sprite != null && sprite.border.sqrMagnitude > 0f) img.type = Image.Type.Sliced;
        }

        private static void Paint(Transform parent, string path, Color color)
        {
            var t = Find(parent, path);
            if (t != null && t.TryGetComponent<Image>(out var img)) { img.sprite = null; img.color = color; }
        }

        private static Transform Find(Transform parent, string path) => parent.Find(path);

        private static bool IsOnTape(Transform t, Transform stopAt)
        {
            for (var p = t; p != null && p != stopAt; p = p.parent)
                if (p.name == "Header" || p.name == "SortBtn") return true;
            return false;
        }

        private static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
        }

        private sealed class Report
        {
            private readonly List<string> _lines = new List<string>();
            public void Count(string what, int n) => _lines.Add($"{what}: {n}");
            public void Missing(string what)
            {
                _lines.Add($"NO ENCONTRADO {what}");
                Debug.LogWarning($"[InventoryUiBuilder] No encontrado en la variante: {what} (¿cambió el prefab del vendor?)");
            }
            public override string ToString() => string.Join(" · ", _lines);
        }
    }
}
#endif
