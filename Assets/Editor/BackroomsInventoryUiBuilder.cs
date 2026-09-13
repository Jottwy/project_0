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
    /// deja la escena de pruebas (<see cref="BackroomsInventoryTestSceneBuilder"/>) apuntando a ella. Encuentra las piezas por NOMBRE dentro
    /// de la variante — es el mismo acoplamiento que tendría hacerlo a mano en el inspector, y aquí
    /// al menos queda escrito y avisa de lo que no encuentra.
    ///
    /// REBANADA 1b: además de la piel, <see cref="Relayout"/> mueve los tres grupos del vendor a las
    /// columnas del greybox aprobado. Queda fuera el feedback de selección con la luz del tubo
    /// (vive en <c>SelectableButtonFeedback</c> por referencia serializada).
    /// </summary>
    public static class BackroomsInventoryUiBuilder
    {
        public const string BasePath = "Assets/PolymindGames/STP/Prefabs/UI/STP_UI_Player.prefab";
        public const string VariantFolder = "Assets/Prefabs/UI";
        public const string VariantPath = VariantFolder + "/BR_UI_Player.prefab";
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

            // La escena viva (STP_Showcase) sigue con el inventario del vendor mientras dura la
            // migración; la variante se prueba en su escena propia.
            BackroomsInventoryTestSceneBuilder.Build();
            AssetDatabase.SaveAssets();
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

            Prompts(inv, theme, report);
            BuildStrap(root, theme, report);
            Relayout(inv, root, report);
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

        // ─── Reparto (rebanada 1b): las tres columnas del greybox a 1920×1080 ───────
        //
        // El vendor reparte en tres grupos de 640 px: LeftGroup (estaciones), MiddleGroup
        // (mochila + inspector) y RightGroup (personaje). Lo aprobado es personaje IZQUIERDA (440),
        // lo que llevas en el CENTRO (flexible) y alrededor a la DERECHA (400): estación arriba,
        // etiqueta del objeto abajo. Se mueven los grupos, no sus tripas: cada panel del vendor
        // sigue intacto por dentro (animaciones, layouts, referencias) y sólo cambia su rect.
        private const float Margin = 48f;
        private const float DockHeight = 132f;
        private const float LeftWidth = 440f;
        private const float RightWidth = 400f;
        private const float Gap = 32f;

        private static void Relayout(Transform inv, GameObject root, Report report)
        {
            float bottom = Margin + DockHeight + 16f;
            var left = inv.Find("RightGroup");
            var middle = inv.Find("MiddleGroup");
            var right = inv.Find("LeftGroup");
            if (left == null || middle == null || right == null) { report.Missing("grupos Left/Middle/Right"); return; }

            // Columnas: anclas al borde que les toca, alto estirado entre el margen y la barra inferior.
            Column((RectTransform)left, 0f, Margin, LeftWidth, bottom);
            Column((RectTransform)right, 1f, Margin, RightWidth, bottom);
            var mid = (RectTransform)middle;
            mid.anchorMin = new Vector2(0f, 0f);
            mid.anchorMax = new Vector2(1f, 1f);
            mid.pivot = new Vector2(0.5f, 0.5f);
            mid.offsetMin = new Vector2(Margin + LeftWidth + Gap, bottom);
            mid.offsetMax = new Vector2(-(Margin + RightWidth + Gap), -Margin);

            // Personaje: el preview ocupa la columna; los slots de ropa, pegados a su izquierda.
            var character = left.Find("Character");
            if (character != null)
            {
                Stretch((RectTransform)character);
                // El preview es una RenderTexture CUADRADA (575×575 del vendor, FOV fijo): estirarla a
                // la columna deforma al muñeco. Se le da la columna entera y un AspectRatioFitter la
                // mantiene 1:1 dentro (FitInParent), centrada.
                var preview = character.Find("CharacterPreview") as RectTransform;
                if (preview != null)
                {
                    preview.anchorMin = new Vector2(0f, 0f);
                    preview.anchorMax = new Vector2(1f, 1f);
                    preview.pivot = new Vector2(0.5f, 0.5f);
                    preview.offsetMin = new Vector2(96f, 0f);
                    preview.offsetMax = new Vector2(0f, -44f);
                    var fitter = preview.GetComponent<AspectRatioFitter>() ?? preview.gameObject.AddComponent<AspectRatioFitter>();
                    fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                    fitter.aspectRatio = 1f;
                }
                var containers = character.Find("Containers") as RectTransform;
                if (containers != null)
                {
                    containers.anchorMin = new Vector2(0f, 0.5f);
                    containers.anchorMax = new Vector2(0f, 0.5f);
                    containers.pivot = new Vector2(0f, 0.5f);
                    containers.anchoredPosition = new Vector2(0f, -22f);
                    containers.sizeDelta = new Vector2(88f, containers.sizeDelta.y);
                }
                var header = character.Find("Header") as RectTransform;
                if (header != null) TopLeft(header, 0f, 0f);
            }
            else report.Missing("RightGroup/Character");

            // Lo que llevas: la caja del inventario llena el centro; el inspector sale de ella y se
            // va a la columna derecha, debajo de la estación.
            var inventory = middle.Find("Inventory") as RectTransform;
            if (inventory != null)
            {
                Stretch(inventory);
                var header = inventory.Find("Header") as RectTransform;
                if (header != null) TopLeft(header, 0f, 0f);
                var weight = inventory.Find("Weight") as RectTransform;
                if (weight != null)
                {
                    weight.anchorMin = new Vector2(1f, 1f);
                    weight.anchorMax = new Vector2(1f, 1f);
                    weight.pivot = new Vector2(1f, 1f);
                    weight.anchoredPosition = new Vector2(0f, -2f);
                }
                var backpack = inventory.Find("Backpack") as RectTransform;
                if (backpack != null)
                {
                    backpack.anchorMin = new Vector2(0f, 0f);
                    backpack.anchorMax = new Vector2(1f, 1f);
                    backpack.offsetMin = new Vector2(0f, 56f);
                    backpack.offsetMax = new Vector2(0f, -52f);
                }
                // El inspector NO se reparenta: dentro de un prefab anidado, cambiar de padre no se
                // puede guardar como override de la variante (Unity lo descarta sin avisar). Se queda
                // bajo Inventory y se ancla FUERA de su rect, en la columna derecha, abajo.
                var inspector = inventory.Find("Inspector") as RectTransform;
                if (inspector != null)
                {
                    inspector.anchorMin = new Vector2(1f, 0f);
                    inspector.anchorMax = new Vector2(1f, 0f);
                    inspector.pivot = new Vector2(0f, 0f);
                    inspector.anchoredPosition = new Vector2(Gap, 0f);
                    inspector.sizeDelta = new Vector2(RightWidth, 300f);
                    report.Count("inspector a la derecha", 1);
                }
                else report.Missing("Inventory/Inspector");
            }
            else report.Missing("MiddleGroup/Inventory");

            // Alrededor: las estaciones del vendor arriba, dejando sitio al inspector abajo.
            var workstations = right.Find("Workstations") as RectTransform;
            if (workstations != null)
            {
                workstations.anchorMin = new Vector2(0f, 0f);
                workstations.anchorMax = new Vector2(1f, 1f);
                workstations.offsetMin = new Vector2(0f, 316f);
                workstations.offsetMax = new Vector2(0f, -44f);
            }

            // Funda: cuelga del MiddleGroup del vendor, cuyo borde inferior queda a `bottom` px de la
            // pantalla; para dejarla en la barra inferior se ancla a ese borde y se baja lo que sobra.
            var hotbar = root.GetComponentInChildren<HotbarUI>(true);
            if (hotbar != null)
            {
                var rt = (RectTransform)hotbar.transform;
                rt.anchorMin = new Vector2(0.5f, 0f);
                rt.anchorMax = new Vector2(0.5f, 0f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.anchoredPosition = new Vector2(0f, (Margin + 20f) - bottom);
                // Layout estirado a la raíz, SIEMPRE: un override viejo en la variante no se deshace
                // solo, y con Layout sin tamaño la funda se descentra y la cincha desaparece.
                if (hotbar.transform.Find("Layout") is RectTransform layout) Stretch(layout);
            }
            report.Count("columnas", 3);
        }

        private static void Column(RectTransform rt, float side, float margin, float width, float bottom)
        {
            rt.anchorMin = new Vector2(side, 0f);
            rt.anchorMax = new Vector2(side, 1f);
            rt.pivot = new Vector2(side, 0.5f);
            rt.anchoredPosition = new Vector2(side == 0f ? margin : -margin, 0f);
            rt.sizeDelta = new Vector2(width, 0f);
            rt.offsetMin = new Vector2(rt.offsetMin.x, bottom);
            rt.offsetMax = new Vector2(rt.offsetMax.x, -margin);
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void TopLeft(RectTransform rt, float x, float y)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
        }

        private static void Tape(Transform t, Image img, BackroomsUiTheme theme)
        {
            SetSprite(img, theme.DymoTape, Color.white);
            foreach (var tmp in t.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                Retype(tmp, theme, theme.MonoBold, theme.TapeInk);
                tmp.fontStyle |= FontStyles.UpperCase;
                tmp.characterSpacing = 6f;
                Fit(tmp, 9f);
            }
        }

        /// <summary>
        /// Nuestras fuentes son más anchas que la del vendor y sus cajas no crecen: un título que
        /// cabía ahora salta de línea y «LEFT CTRL» se monta sobre el icono. Una línea, y que el
        /// tamaño baje hasta caber (nunca por debajo de <paramref name="min"/>).
        /// </summary>
        private static void Fit(TextMeshProUGUI tmp, float min)
        {
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMax = Mathf.Max(tmp.fontSize, min);
            tmp.fontSizeMin = min;
        }

        /// <summary>Los avisos de tecla del vendor (FPS_UI_InputPrompt): glifo en cinta, texto que cabe.</summary>
        private static void Prompts(Transform inv, BackroomsUiTheme theme, Report report)
        {
            int n = 0;
            foreach (var t in inv.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != "KeyTxt" && t.name != "ControlTxt") continue;
                if (!t.TryGetComponent<TextMeshProUGUI>(out var tmp)) continue;
                bool key = t.name == "KeyTxt";
                Retype(tmp, theme, key ? theme.MonoBold : theme.Display, key ? theme.TapeInk : theme.Ink);
                Fit(tmp, 10f);
                if (key && t.parent != null && t.parent.name == "KeyBg")
                {
                    if (t.parent.TryGetComponent<Image>(out var bg)) SetSprite(bg, theme.DymoTape, Color.white);
                    // Con etiqueta de tecla, el icono de ratón del vendor se pisa con el texto: la cinta
                    // ya dice la tecla, el icono sobra.
                    var icon = t.parent.Find("KeyIcon");
                    if (icon != null && !string.IsNullOrWhiteSpace(tmp.text)) icon.gameObject.SetActive(false);
                }
                n++;
            }
            report.Count("avisos de tecla", n);
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
