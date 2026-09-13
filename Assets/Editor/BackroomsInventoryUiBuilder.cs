#if UNITY_EDITOR
using System.Collections.Generic;
using BackroomsSurvival.UI;
using BackroomsSurvival.Wearables;
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
                SkinSlot(slot.transform, theme);
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
            Relayout(inv, root, theme, report);
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

        // ─── Reparto: la rejilla del greybox 1d0e788e a 1920×1080 ─────────────────
        //
        // El vendor reparte en tres grupos: LeftGroup (estaciones), MiddleGroup (mochila + inspector)
        // y RightGroup (personaje). Lo aprobado: personaje IZQUIERDA (440), lo que llevas CENTRO
        // (flexible), alrededor DERECHA (400). Cada columna es una cabecera de 36 px a todo el ancho
        // y, 8 px debajo, su caja de contenido hasta y=876; debajo, 24 px, la barra de 132 px con
        // Manos · Cinturón (CENTRADO en pantalla: es la misma pieza que se ve en juego) · Atajos. La carga
        // total va en una línea en la cabecera del centro (Joel, 2026-09-13). Nada se reparenta (en un prefab anidado no se guarda): lo
        // que tiene que salir de su grupo se ancla FUERA de su rect.
        private const float Margin = 48f;
        private const float DockHeight = 132f;
        private const float DockGap = 24f;
        private const float LeftWidth = 440f;
        private const float RightWidth = 400f;
        private const float Gap = 32f;
        private const float HeaderHeight = 36f;
        private const float HeaderGap = 8f;
        private const float InspectorHeight = 284f;
        private const float WindowGap = 16f;
        private const float Cell = 72f;
        private const float CellGap = 8f;
        private const float BoxPad = 16f;
        private const float HandsWidth = 176f;
        private const float HolsterWidth = 520f;
        private const float HintsWidth = 308f;

        private static void Relayout(Transform inv, GameObject root, BackroomsUiTheme theme, Report report)
        {
            float bottom = Margin + DockHeight + DockGap;
            float top = HeaderHeight + HeaderGap;
            var left = inv.Find("RightGroup");
            var middle = inv.Find("MiddleGroup");
            var right = inv.Find("LeftGroup");
            if (left == null || middle == null || right == null) { report.Missing("grupos Left/Middle/Right"); return; }
            var inventoryUI = inv.GetComponent<InventoryUI>();

            Column((RectTransform)left, 0f, Margin, LeftWidth, bottom);
            Column((RectTransform)right, 1f, Margin, RightWidth, bottom);
            var mid = (RectTransform)middle;
            mid.anchorMin = new Vector2(0f, 0f);
            mid.anchorMax = new Vector2(1f, 1f);
            mid.pivot = new Vector2(0.5f, 0.5f);
            mid.offsetMin = new Vector2(Margin + LeftWidth + Gap, bottom);
            mid.offsetMax = new Vector2(-(Margin + RightWidth + Gap), -Margin);

            // ── Personaje ──
            var character = left.Find("Character");
            if (character != null)
            {
                Stretch((RectTransform)character);
                Box(character, "BR_Box", theme, top, 0f);
                var header = character.Find("Header") as RectTransform;
                if (header != null) { HeaderBar(header); Retitle(header, "PERSONAJE"); }

                // Slots del cuerpo: columna pegada a la izquierda de la caja, primer hueco en y=132.
                if (character.Find("Containers") is RectTransform containers)
                {
                    const float spacing = 28f;
                    Place(containers, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(8f, -(top + 40f)),
                        new Vector2(88f, 5f * 80f + 4f * spacing));
                    if (containers.TryGetComponent<VerticalLayoutGroup>(out var v)) v.spacing = spacing;

                    // Prototipo de mochilas (ADR-147 enm. 1): el hueco «Backpack» del vendor no tenía contenedor detrás.
                    if (containers.Find("BackpackContainer") is Transform backSlot
                        && containers.Find("HeadContainer") is Transform head && head.TryGetComponent<ItemContainerUI>(out var headUi))
                    {
                        var backUi = backSlot.GetComponent<ItemContainerUI>();
                        if (backUi == null) backUi = backSlot.gameObject.AddComponent<ItemContainerUI>();
                        BindContainerUI(backUi, BackroomsBackpackPrototypeCreator.BackContainer, headUi);
                        RegisterContainerUI(inventoryUI, backUi);
                        foreach (Transform child in backSlot)
                            if (child.name.StartsWith("STP_UI_ItemSlot") && child.GetComponent<ItemSlotUIBase>() == null)
                                child.gameObject.SetActive(false);
                        foreach (var created in EnsureSlots(backUi, 1, theme))
                            Place((RectTransform)created.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(Cell, Cell));
                        report.Count("hueco de espalda", 1);
                    }
                    else report.Missing("Containers/BackpackContainer o HeadContainer");
                }

                // El render es CUADRADO: se ve ENTERO detrás de los slots, llenando la caja (BR_PreviewBackdrop, pedido de
                // Joel: la franja cortaba la ropa al hacer zoom). El RawImage del vendor, que tiene el arrastre y la rueda,
                // queda en la franja central, transparente y encima.
                var preview = character.Find("CharacterPreview") as RectTransform;
                if (preview != null)
                {
                    if (preview.GetComponent<AspectRatioFitter>() is AspectRatioFitter fitter) Object.DestroyImmediate(fitter);
                    Inset(preview, 92f, 8f, 92f, top + 8f);
                    if (preview.TryGetComponent<RawImage>(out var raw))
                    {
                        var backdrop = EnsureRect(character, "BR_PreviewBackdrop", typeof(RawImage));
                        IgnoreLayout(backdrop.gameObject);
                        InspectionOnly(backdrop.gameObject);
                        Inset(backdrop, 8f, 8f, 8f, top + 8f);
                        if (character.Find("BR_Box") is Transform previewBox) backdrop.SetSiblingIndex(previewBox.GetSiblingIndex() + 1);
                        var backdropImage = backdrop.GetComponent<RawImage>();
                        backdropImage.texture = raw.texture;
                        backdropImage.raycastTarget = false;
                        float w = (LeftWidth - 16f) / (1080f - bottom - Margin - top - 16f);
                        backdropImage.uvRect = new Rect((1f - w) * 0.5f, 0f, w, 1f);
                        raw.color = new Color(1f, 1f, 1f, 0f);
                    }
                }

                // D14: las manos van en la barra; la caja «Manos» aparte se retira.
                if (character.Find("BR_Hands") is Transform oldHands) Object.DestroyImmediate(oldHands.gameObject);

                // Segunda columna de slots, a la derecha del muñeco (greybox). De momento, la cintura.
                if (character.Find("Containers/HeadContainer") is Transform headSlot && headSlot.TryGetComponent<ItemContainerUI>(out var headTemplate))
                {
                    var rightSlots = EnsureRect(character, "BR_ContainersRight", typeof(VerticalLayoutGroup));
                    Place(rightSlots, Vector2.one, Vector2.one, new Vector2(-8f, -(top + 40f)), new Vector2(88f, 4f * 80f + 3f * 28f));
                    var column = rightSlots.GetComponent<VerticalLayoutGroup>();
                    column.spacing = 28f;
                    column.childAlignment = TextAnchor.UpperCenter;
                    column.childControlWidth = false;
                    column.childControlHeight = false;
                    column.childForceExpandWidth = false;
                    column.childForceExpandHeight = false;
                    InspectionOnly(rightSlots.gameObject);
                    var waistUi = EnsureEquipmentSlot(rightSlots, "BR_WaistContainer", BackroomsBackpackPrototypeCreator.WaistContainer, "Waist", headTemplate, theme);
                    RegisterContainerUI(inventoryUI, waistUi);
                    report.Count("hueco de cintura", 1);
                }
                else report.Missing("Containers/HeadContainer (plantilla de slot)");

                BuildBodyViewToggle(character, header, preview, theme, report);
                BuildPreviewZoom(character, preview, root, theme, report);
            }
            else report.Missing("RightGroup/Character");

            // ── Lo que llevas ──
            var inventory = middle.Find("Inventory") as RectTransform;
            if (inventory != null)
            {
                Stretch(inventory);
                if (inventory.TryGetComponent<Image>(out var invImg)) invImg.color = Color.clear;
                Box(inventory, "BR_Box", theme, top, 0f);
                var header = inventory.Find("Header") as RectTransform;
                if (header != null) { HeaderBar(header); Retitle(header, "LO QUE LLEVAS"); }

                if (inventory.Find("Backpack") is RectTransform backpack)
                {
                    float firstTop = top + 12f;
                    float firstH = SectionHeight(BackroomsBackpackPrototypeCreator.BackStorageSlots);
                    float secondTop = firstTop + firstH + 12f;
                    float secondH = SectionHeight(BackroomsBackpackPrototypeCreator.BaseSlots);

                    var bag = Section(inventory, "BR_BagSection", theme, firstTop, firstH, "ESPALDA · SIN MOCHILA", "ponte una mochila", 1);
                    var storage = EnsureRect(inventory, "BR_BackStorage", typeof(GridLayoutGroup), typeof(ItemContainerUI), typeof(BackroomsWornSlotsUI));
                    storage.SetSiblingIndex(2);
                    PlaceGrid(storage, firstTop, firstH);
                    var storageUi = storage.GetComponent<ItemContainerUI>();
                    BindContainerUI(storageUi, BackroomsBackpackPrototypeCreator.BackStorageContainer, backpack.GetComponent<ItemContainerUI>());
                    RegisterContainerUI(inventoryUI, storageUi);
                    EnsureSlots(storageUi, BackroomsBackpackPrototypeCreator.BackStorageSlots, theme);
                    var slotsUi = new SerializedObject(storage.GetComponent<BackroomsWornSlotsUI>());
                    slotsUi.FindProperty("_title").objectReferenceValue = bag.Find("Title").GetComponent<TextMeshProUGUI>();
                    slotsUi.FindProperty("_count").objectReferenceValue = bag.Find("Count").GetComponent<TextMeshProUGUI>();
                    slotsUi.ApplyModifiedPropertiesWithoutUndo();

                    var pockets = Section(inventory, "BR_PocketsSection", theme, secondTop, secondH, "BASE", BackroomsBackpackPrototypeCreator.BaseSlots + " huecos", 3);
                    PlaceGrid(backpack, secondTop, secondH);
                    BuildSections(inventory, firstTop, new[] { "bag", "pockets" }, new[] { bag, pockets }, new[] { storage, backpack }, theme);
                    report.Count("almacén de la espalda", 1);
                }
                else report.Missing("Inventory/Backpack");

                // Alrededor, abajo: el panel del objeto (inspector) sale del centro por la derecha.
                if (inventory.Find("Inspector") is RectTransform inspector)
                {
                    Place(inspector, new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(Gap, 0f), new Vector2(RightWidth, InspectorHeight));
                    report.Count("inspector a la derecha", 1);
                }
                else report.Missing("Inventory/Inspector");

                // Carga: una línea a la derecha de la cabecera — «CARGA ▬▬▬ 2,5 / 40 KG». Es el total; cada
                // prenda llevará la suya en su fila.
                if (inventory.Find("BR_LoadBox") is RectTransform oldLoad) Object.DestroyImmediate(oldLoad.gameObject);
                if (inventory.Find("Weight") is RectTransform weight)
                {
                    const float loadW = 460f, labelW = 72f, textW = 128f, pad = 12f;
                    Place(weight, Vector2.one, Vector2.one, new Vector2(-pad, -4f), new Vector2(loadW, HeaderHeight - 8f));
                    if (weight.TryGetComponent<Image>(out var wImg)) wImg.color = Color.clear;
                    var loadLabel = EnsureRect(weight, "BR_LoadLabel", typeof(Image));
                    Place(loadLabel, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(labelW, 24f));
                    Stretch((RectTransform)Label(loadLabel, "Text", "CARGA", theme.MonoBold, 12f, theme.TapeInk, TextAlignmentOptions.Center).transform);
                    Tape(loadLabel, loadLabel.GetComponent<Image>(), theme);
                    loadLabel.SetAsFirstSibling();
                    if (weight.Find("WeightBarBG") is RectTransform bar)
                    {
                        bar.anchorMin = new Vector2(0f, 0.5f);
                        bar.anchorMax = new Vector2(1f, 0.5f);
                        bar.pivot = new Vector2(0.5f, 0.5f);
                        bar.sizeDelta = new Vector2(-(labelW + pad + textW + pad), 14f);
                        bar.anchoredPosition = new Vector2((labelW - textW) * 0.5f, 0f);
                    }
                    if (weight.Find("WeightText") is RectTransform wText)
                    {
                        Place(wText, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), Vector2.zero, new Vector2(textW, HeaderHeight - 8f));
                        if (wText.TryGetComponent<TextMeshProUGUI>(out var wTmp))
                        {
                            wTmp.alignment = TextAlignmentOptions.MidlineRight;
                            wTmp.fontSize = 15f;
                            wTmp.enableAutoSizing = false;
                        }
                    }
                }
                else report.Missing("Inventory/Weight");

                // Atajos: los avisos de tecla del vendor, apilados en su caja de la barra inferior.
                if (inventory.Find("Prompts") is RectTransform prompts)
                {
                    Place(prompts, new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(Gap + RightWidth - HintsWidth, -DockGap),
                        new Vector2(HintsWidth, DockHeight));
                    Box(prompts, "BR_Box", theme, 0f, 0f);
                    DockTitle(prompts, "ATAJOS", theme);
                    if (prompts.Find("AutoMovePrompt") is RectTransform auto)
                        Place(auto, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(8f, -40f), auto.rect.size);
                    if (prompts.Find("SplitStackPrompt") is RectTransform split)
                        Place(split, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(8f, -84f), split.rect.size);
                    if (prompts.Find("SortBtn") is RectTransform sort)
                        Place(sort, Vector2.one, Vector2.one, new Vector2(-BoxPad, -48f), new Vector2(100f, 32f));
                }
                else report.Missing("Inventory/Prompts");
            }
            else report.Missing("MiddleGroup/Inventory");

            // ── Alrededor | Crafteo ──
            // La cabecera no lleva título: son dos pestañas, como Ropa | Heridas en el personaje.
            var aroundHeader = EnsureRect(right, "BR_Header", typeof(Image));
            HeaderBar(aroundHeader);
            InspectionOnly(aroundHeader.gameObject);
            if (aroundHeader.Find("Name") is RectTransform oldName) Object.DestroyImmediate(oldName.gameObject);
            Tape(aroundHeader, aroundHeader.GetComponent<Image>(), theme);
            const float aroundTabW = 124f, craftTabW = 108f, tabGap = 4f;
            var aroundBtn = EnsureToggleButton(aroundHeader, "AroundBtn", "ALREDEDOR", -(RightWidth - 8f - aroundTabW), theme, aroundTabW);
            var craftBtn = EnsureToggleButton(aroundHeader, "CraftBtn", "CRAFTEO",
                -(RightWidth - 8f - aroundTabW - tabGap - craftTabW), theme, craftTabW);
            Box(right, "BR_Box", theme, top, InspectorHeight + WindowGap);
            if (right.Find("Workstations") is RectTransform workstations)
            {
                Inset(workstations, 0f, InspectorHeight + WindowGap, 0f, top);
                foreach (Transform station in workstations)
                {
                    if (station.name == "BR_AroundEmpty") continue;
                    Inset((RectTransform)station, 0f, 0f, 0f, 44f);
                    if (station.TryGetComponent<Image>(out var sImg)) sImg.color = Color.clear;
                    StationRow(station, "CategoryName", 52f, TextAlignmentOptions.MidlineLeft);
                    StationRow(station, "StationName", 52f, TextAlignmentOptions.MidlineRight);
                    // La columna de categorías empieza bajo esa fila, no encima.
                    if (station.Find("Categories") is RectTransform cats) cats.offsetMax = new Vector2(cats.offsetMax.x, -44f);
                }

                var empty = Label(workstations, "BR_AroundEmpty", "Nada al alcance", theme.Mono, 15f, theme.InkDim,
                    TextAlignmentOptions.Center);
                Inset((RectTransform)empty.transform, BoxPad, BoxPad, BoxPad, BoxPad);
                empty.gameObject.SetActive(false);

                var aroundToggle = aroundHeader.GetComponent<BackroomsAroundViewToggle>();
                if (aroundToggle == null) aroundToggle = aroundHeader.gameObject.AddComponent<BackroomsAroundViewToggle>();
                var so = new SerializedObject(aroundToggle);
                so.FindProperty("_aroundButton").objectReferenceValue = aroundBtn;
                so.FindProperty("_craftButton").objectReferenceValue = craftBtn;
                so.FindProperty("_workstations").objectReferenceValue = workstations;
                so.FindProperty("_emptyLabel").objectReferenceValue = empty.gameObject;
                so.ApplyModifiedPropertiesWithoutUndo();
                report.Count("conmutador alrededor/crafteo", 1);
            }
            else report.Missing("LeftGroup/Workstations");

            // ── Funda ──
            var hotbar = root.GetComponentInChildren<HotbarUI>(true);
            if (hotbar != null)
            {
                var rt = (RectTransform)hotbar.transform;
                // Centro de la pantalla: el centro del grupo cae 20 px a la derecha (columnas de 440 y 400).
                Place(rt, new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2((RightWidth - LeftWidth) * 0.5f, -DockGap),
                    new Vector2(HolsterWidth, DockHeight));
                if (hotbar.transform.Find("Layout") is RectTransform layout)
                {
                    Stretch(layout);
                    if (layout.TryGetComponent<HorizontalLayoutGroup>(out var h))
                    {
                        h.padding = new RectOffset((int)BoxPad, (int)BoxPad, 44, (int)BoxPad);
                        h.spacing = CellGap;
                        h.childAlignment = TextAnchor.UpperLeft;
                    }
                    foreach (Transform c in layout)
                        if (c.GetComponent<ItemSlotUIBase>() != null) ((RectTransform)c).sizeDelta = new Vector2(Cell, Cell);
                    if (layout.Find(StrapName) is RectTransform strap) { strap.offsetMin = Vector2.zero; strap.offsetMax = Vector2.zero; }
                }
                DockTitle(rt, "MANOS · CINTURÓN", theme);
                // D14 enm. 1: 8 huecos creados aquí con piel (2 manos + hasta 6 del cinturón); se ven los que da la cintura.
                if (hotbar.GetComponentInChildren<ItemContainerUI>(true) is ItemContainerUI holsterUi)
                {
                    foreach (var created in EnsureSlots(holsterUi, BackroomsBackpackPrototypeCreator.HolsterSlots, theme))
                        ((RectTransform)created.transform).sizeDelta = new Vector2(Cell, Cell);
                    var byBelt = holsterUi.GetComponent<BackroomsWornSlotsUI>();
                    if (byBelt == null) byBelt = holsterUi.gameObject.AddComponent<BackroomsWornSlotsUI>();
                    var beltSlots = new SerializedObject(byBelt);
                    beltSlots.FindProperty("_ownerContainer").stringValue = BackroomsBackpackPrototypeCreator.WaistContainer;
                    beltSlots.FindProperty("_baseSlots").intValue = BackroomsBackpackPrototypeCreator.HandSlots;
                    beltSlots.ApplyModifiedPropertiesWithoutUndo();
                    report.Count("barra de manos y cinturón", 1);
                }
                else report.Missing("HotbarUI/ItemContainerUI");
                // En juego, el cinturón se compacta sin rótulo; con TAB vuelve a su caja (BackroomsBeltHud).
                var beltHud = hotbar.GetComponent<BackroomsBeltHud>();
                if (beltHud == null) beltHud = hotbar.gameObject.AddComponent<BackroomsBeltHud>();
                var beltTitle = rt.Find("BR_Title");
                if (beltTitle.GetComponent<CanvasGroup>() == null) beltTitle.gameObject.AddComponent<CanvasGroup>();
                var belt = new SerializedObject(beltHud);
                belt.FindProperty("_box").objectReferenceValue = rt;
                belt.FindProperty("_layout").objectReferenceValue = hotbar.transform.Find("Layout").GetComponent<HorizontalLayoutGroup>();
                belt.FindProperty("_title").objectReferenceValue = beltTitle.GetComponent<CanvasGroup>();
                belt.FindProperty("_selectionFrame").objectReferenceValue = hotbar.transform.Find("SelectionFrame");
                belt.FindProperty("_inventoryCell").floatValue = Cell;
                belt.FindProperty("_inventoryTop").floatValue = 44f;
                belt.FindProperty("_pad").floatValue = BoxPad;
                belt.FindProperty("_gap").floatValue = CellGap;
                belt.ApplyModifiedPropertiesWithoutUndo();
            }
            else report.Missing("HotbarUI");
            report.Count("columnas", 3);
        }

        private static void StationRow(Transform station, string name, float leftPx, TextAlignmentOptions align)
        {
            if (!(station.Find(name) is RectTransform rt)) return;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(leftPx, -34f);
            rt.offsetMax = new Vector2(-BoxPad, -4f);
            if (rt.TryGetComponent<TextMeshProUGUI>(out var tmp)) tmp.alignment = align;
        }

        private static void HeaderBar(RectTransform rt)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(0f, -HeaderHeight);
            rt.offsetMax = Vector2.zero;
        }

        private static void Retitle(RectTransform header, string text)
        {
            if (!(header.Find("Name") is RectTransform name) || !name.TryGetComponent<TextMeshProUGUI>(out var tmp)) return;
            tmp.text = text;
            name.pivot = new Vector2(0f, 0.5f);
            name.anchoredPosition = new Vector2(44f, 0f);
            name.sizeDelta = new Vector2(360f, 0f);
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
        }

        private static RectTransform EnsureRect(Transform parent, string name, params System.Type[] components)
        {
            if (parent.Find(name) is RectTransform existing) return existing;
            var types = new List<System.Type> { typeof(RectTransform) };
            types.AddRange(components);
            var go = new GameObject(name, types.ToArray());
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        private static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.sizeDelta = size;
            rt.anchoredPosition = pos;
        }

        private static void Inset(RectTransform rt, float leftPx, float bottomPx, float rightPx, float topPx)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(leftPx, bottomPx);
            rt.offsetMax = new Vector2(-rightPx, -topPx);
        }

        /// <summary>Caja del greybox: relleno un punto sobre el suelo y borde de 2 px, detrás de todo.</summary>
        private static RectTransform Box(Transform parent, string name, BackroomsUiTheme theme, float topInset, float bottomInset)
        {
            var rt = EnsureRect(parent, name, typeof(Image), typeof(Outline));
            Inset(rt, 0f, bottomInset, 0f, topInset);
            Panel(rt, theme, 0.06f, 0.22f);
            IgnoreLayout(rt.gameObject);
            InspectionOnly(rt.gameObject);
            rt.SetAsFirstSibling();
            return rt;
        }

        private static void Panel(RectTransform rt, BackroomsUiTheme theme, float fill, float border)
        {
            var img = rt.GetComponent<Image>();
            img.sprite = null;
            img.color = WithAlpha(Color.Lerp(theme.Ground, theme.Ink, fill), 0.96f);
            img.raycastTarget = false;
            var outline = rt.GetComponent<Outline>();
            outline.effectColor = Color.Lerp(theme.Ground, theme.Ink, border);
            outline.effectDistance = new Vector2(2f, -2f);
            outline.useGraphicAlpha = false;
        }

        private static TextMeshProUGUI Label(Transform parent, string name, string text, TMP_FontAsset font, float size, Color color,
            TextAlignmentOptions align)
        {
            var rt = EnsureRect(parent, name, typeof(TextMeshProUGUI));
            var tmp = rt.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            if (font != null) tmp.font = font;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.enableWordWrapping = false;
            tmp.raycastTarget = false;
            return tmp;
        }

        /// <summary>Caja de la barra inferior colgada del borde de abajo de <paramref name="parent"/>.</summary>
        private static RectTransform DockBox(Transform parent, string name, BackroomsUiTheme theme, float anchorX, float x, float width, string title)
        {
            var rt = EnsureRect(parent, name, typeof(Image), typeof(Outline));
            Place(rt, new Vector2(anchorX, 0f), new Vector2(0f, 1f), new Vector2(x, -DockGap), new Vector2(width, DockHeight));
            Panel(rt, theme, 0.06f, 0.22f);
            IgnoreLayout(rt.gameObject);
            InspectionOnly(rt.gameObject);
            DockTitle(rt, title, theme);
            return rt;
        }

        private static void DockTitle(RectTransform box, string title, BackroomsUiTheme theme)
        {
            var tmp = Label(box, "BR_Title", title, theme.DisplayBold != null ? theme.DisplayBold : theme.Display, 17f, theme.Ink,
                TextAlignmentOptions.TopLeft);
            var rt = (RectTransform)tmp.transform;
            Place(rt, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(BoxPad, -10f), new Vector2(box.sizeDelta.x - 2f * BoxPad, 24f));
            IgnoreLayout(rt.gameObject);
            rt.SetAsLastSibling();
        }

        /// <summary>Sección del centro (cabecera y caja; plegar llega después).</summary>
        private static RectTransform Section(RectTransform inventory, string name, BackroomsUiTheme theme, float y, float height,
            string title, string count, int siblingIndex)
        {
            var rt = EnsureRect(inventory, name, typeof(Image), typeof(Outline));
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(BoxPad, -(y + height));
            rt.offsetMax = new Vector2(-BoxPad, -y);
            Panel(rt, theme, 0.10f, 0.28f);
            InspectionOnly(rt.gameObject);
            rt.SetSiblingIndex(siblingIndex);
            SectionLabel(rt, "Title", title, theme.Display, 17f, theme.Ink, TextAlignmentOptions.MidlineLeft);
            SectionLabel(rt, "Count", count, theme.Mono, 13f, theme.InkDim, TextAlignmentOptions.MidlineRight);
            var titleRt = (RectTransform)rt.Find("Title");
            titleRt.offsetMin = new Vector2(BoxPad + 20f, titleRt.offsetMin.y);
            SectionLabel(rt, "BR_Fold", "-", theme.MonoBold, 17f, theme.Fluorescent, TextAlignmentOptions.MidlineLeft);
            Place((RectTransform)rt.Find("BR_Fold"), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(BoxPad, 0f), new Vector2(16f, SectionTitleHeight));
            return rt;
        }

        private const float SectionTitleHeight = 34f;

        /// <summary>
        /// D13: el componente que coloca, pliega y reordena las secciones en juego, y una franja de cabecera clicable y
        /// arrastrable por sección. Las posiciones de arriba quedan como estado inicial (lo que ve la captura sin Play).
        /// </summary>
        private static void BuildSections(RectTransform inventory, float topInset, string[] ids, RectTransform[] sections,
            RectTransform[] grids, BackroomsUiTheme theme)
        {
            var component = inventory.GetComponent<BackroomsInventorySections>();
            if (component == null) component = inventory.gameObject.AddComponent<BackroomsInventorySections>();
            var so = new SerializedObject(component);
            SetArray(so.FindProperty("_ids"), ids.Length, (p, i) => p.stringValue = ids[i]);
            SetArray(so.FindProperty("_sections"), ids.Length, (p, i) => p.objectReferenceValue = sections[i]);
            SetArray(so.FindProperty("_grids"), ids.Length, (p, i) => p.objectReferenceValue = grids[i]);
            SetArray(so.FindProperty("_foldMarks"), ids.Length,
                (p, i) => p.objectReferenceValue = sections[i].Find("BR_Fold").GetComponent<TextMeshProUGUI>());
            so.FindProperty("_topInset").floatValue = topInset;
            so.FindProperty("_gap").floatValue = 12f;
            so.FindProperty("_sidePad").floatValue = BoxPad;
            so.FindProperty("_titleHeight").floatValue = SectionTitleHeight;
            so.ApplyModifiedPropertiesWithoutUndo();

            for (int i = 0; i < ids.Length; i++)
            {
                if (grids[i].GetComponent<CanvasGroup>() == null) grids[i].gameObject.AddComponent<CanvasGroup>();
                var hit = EnsureRect(sections[i], "BR_HeaderHit", typeof(Image), typeof(BackroomsSectionHeader));
                hit.anchorMin = new Vector2(0f, 1f);
                hit.anchorMax = new Vector2(1f, 1f);
                hit.pivot = new Vector2(0.5f, 1f);
                hit.offsetMin = new Vector2(0f, -SectionTitleHeight);
                hit.offsetMax = Vector2.zero;
                var img = hit.GetComponent<Image>();
                img.sprite = null;
                img.color = Color.clear;
                img.raycastTarget = true;
                hit.SetAsLastSibling();
                var header = new SerializedObject(hit.GetComponent<BackroomsSectionHeader>());
                header.FindProperty("_owner").objectReferenceValue = component;
                header.FindProperty("_id").stringValue = ids[i];
                header.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void SetArray(SerializedProperty array, int size, System.Action<SerializedProperty, int> set)
        {
            array.arraySize = size;
            for (int i = 0; i < size; i++) set(array.GetArrayElementAtIndex(i), i);
        }

        private static float SectionHeight(int slots)
        {
            float inner = 920f - 4f * BoxPad;
            int cols = Mathf.Max(1, Mathf.FloorToInt((inner + CellGap) / (Cell + CellGap)));
            int rows = Mathf.Max(1, Mathf.CeilToInt(slots / (float)cols));
            return SectionTitleHeight + 8f + rows * Cell + (rows - 1) * CellGap + BoxPad;
        }

        private static void PlaceGrid(RectTransform grid, float sectionTop, float sectionHeight)
        {
            grid.anchorMin = new Vector2(0f, 1f);
            grid.anchorMax = new Vector2(1f, 1f);
            grid.pivot = new Vector2(0.5f, 1f);
            grid.offsetMin = new Vector2(BoxPad, -(sectionTop + sectionHeight));
            grid.offsetMax = new Vector2(-BoxPad, -(sectionTop + SectionTitleHeight));
            if (grid.TryGetComponent<GridLayoutGroup>(out var layout))
            {
                layout.cellSize = new Vector2(Cell, Cell);
                layout.spacing = new Vector2(CellGap, CellGap);
                layout.padding = new RectOffset((int)BoxPad, (int)BoxPad, 8, (int)BoxPad);
                layout.childAlignment = TextAnchor.UpperLeft;
            }
        }

        private static void SkinSlot(Transform slot, BackroomsUiTheme theme)
        {
            if (slot.TryGetComponent<Image>(out var bg)) SetSprite(bg, theme.SunkenSlot, Color.white);
            Paint(slot, "DurabilityBG", theme.Tape);
            Paint(slot, "DurabilityBar", theme.Fluorescent);
            foreach (var tmp in slot.GetComponentsInChildren<TextMeshProUGUI>(true))
                Retype(tmp, theme, MonoTextNames.Contains(tmp.name) ? theme.Mono : theme.Display, theme.Ink);
        }

        /// <summary>
        /// Crea con la plantilla del panel los huecos que falten como hijos directos (lo que busca
        /// <c>ItemContainerUI.GenerateSlots</c>) y les pone la piel. Devuelve solo los creados ahora.
        /// </summary>
        private static List<ItemSlotUIBase> EnsureSlots(ItemContainerUI ui, int count, BackroomsUiTheme theme)
        {
            var created = new List<ItemSlotUIBase>();
            int existing = 0;
            foreach (Transform child in ui.transform)
                if (child.GetComponent<ItemSlotUIBase>() != null) existing++;
            var template = new SerializedObject(ui).FindProperty("_slotTemplate").objectReferenceValue as ItemSlotUIBase;
            if (template == null) return created;
            for (int i = existing; i < count; i++)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(template.gameObject, ui.transform);
                go.name = "BR_Slot" + i;
                var slot = go.GetComponent<ItemSlotUIBase>();
                SkinSlot(go.transform, theme);
                created.Add(slot);
            }
            return created;
        }

        /// <summary>Slot de equipo propio con la forma de los del vendor: cinta encima y un hueco de 72 px con su contenedor.</summary>
        private static ItemContainerUI EnsureEquipmentSlot(RectTransform column, string name, string containerName, string label,
            ItemContainerUI template, BackroomsUiTheme theme)
        {
            var slotRoot = EnsureRect(column, name, typeof(ItemContainerUI));
            slotRoot.sizeDelta = new Vector2(80f, 80f);
            var ui = slotRoot.GetComponent<ItemContainerUI>();
            BindContainerUI(ui, containerName, template);
            foreach (var created in EnsureSlots(ui, 1, theme))
                Place((RectTransform)created.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(Cell, Cell));
            var tape = EnsureRect(slotRoot, "Header", typeof(Image));
            Place(tape, new Vector2(0.5f, 1f), new Vector2(0.5f, 0f), new Vector2(0f, -5f), new Vector2(80f, 24f));
            var text = Label(tape, "Category", label, theme.MonoBold, 15f, theme.TapeInk, TextAlignmentOptions.Center);
            Stretch((RectTransform)text.transform);
            Tape(tape, tape.GetComponent<Image>(), theme);
            tape.SetAsLastSibling();
            return ui;
        }

        /// <summary>Nombre del contenedor y plantilla de hueco copiada de un panel del vendor que ya funciona.</summary>
        private static void BindContainerUI(ItemContainerUI ui, string containerName, ItemContainerUI templateFrom)
        {
            var so = new SerializedObject(ui);
            so.FindProperty("_containerName").stringValue = containerName;
            if (templateFrom != null)
                so.FindProperty("_slotTemplate").objectReferenceValue =
                    new SerializedObject(templateFrom).FindProperty("_slotTemplate").objectReferenceValue;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Los paneles de equipo del vendor se atan al abrir el inventario: van en _nonPersistentContainers.</summary>
        private static void RegisterContainerUI(InventoryUI inventoryUI, ItemContainerUI ui)
        {
            if (inventoryUI == null || ui == null) return;
            var so = new SerializedObject(inventoryUI);
            var list = so.FindProperty("_nonPersistentContainers");
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == ui) return;
            list.arraySize++;
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = ui;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SectionLabel(RectTransform section, string name, string text, TMP_FontAsset font, float size, Color color,
            TextAlignmentOptions align)
        {
            var t = (RectTransform)Label(section, name, text, font, size, color, align).transform;
            t.anchorMin = new Vector2(0f, 1f);
            t.anchorMax = new Vector2(1f, 1f);
            t.pivot = new Vector2(0.5f, 1f);
            t.offsetMin = new Vector2(BoxPad, -34f);
            t.offsetMax = new Vector2(-BoxPad, 0f);
        }

        // ─── Conmutador Ropa | Heridas (placeholder estructural, greybox 1d0e788e) ────
        //
        // Solo layout: qué se ve en el hueco del preview. Las zonas son de ejemplo (sin ADR de
        // cuerpo por zonas todavía, roadmap «Sistemas anotados, sin empezar»); coordenadas en
        // fracción del panel, calcadas del greybox para que la primera vista en juego case con la
        // maqueta aprobada.
        private const string WoundsPanelName = "BR_WoundsPanel";
        private static readonly (string Name, float X, float Y, float W, float H)[] PlaceholderZones =
        {
            ("Cabeza", 0.186f, 0.110f, 0.273f, 0.102f),
            ("Cuello", 0.186f, 0.194f, 0.273f, 0.102f),
            ("Hombro izq.", 0.018f, 0.194f, 0.273f, 0.102f),
            ("Hombro der.", 0.627f, 0.194f, 0.273f, 0.102f),
            ("Brazo izq.", 0.018f, 0.306f, 0.273f, 0.102f),
            ("Pecho", 0.186f, 0.306f, 0.273f, 0.102f),
            ("Brazo der.", 0.627f, 0.306f, 0.273f, 0.102f),
            ("Antebrazo izq.", 0.018f, 0.418f, 0.273f, 0.102f),
            ("Abdomen", 0.186f, 0.418f, 0.273f, 0.102f),
            ("Antebrazo der.", 0.627f, 0.418f, 0.273f, 0.102f),
            ("Mano izq.", 0.018f, 0.531f, 0.273f, 0.102f),
            ("Cadera", 0.186f, 0.531f, 0.273f, 0.102f),
            ("Mano der.", 0.627f, 0.531f, 0.273f, 0.102f),
            ("Muslo izq.", 0.018f, 0.643f, 0.273f, 0.102f),
            ("Muslo der.", 0.627f, 0.643f, 0.273f, 0.102f),
            ("Pierna izq.", 0.018f, 0.755f, 0.273f, 0.102f),
            ("Pierna der.", 0.627f, 0.755f, 0.273f, 0.102f),
            ("Pie izq.", 0.018f, 0.867f, 0.273f, 0.102f),
            ("Pie der.", 0.627f, 0.867f, 0.273f, 0.102f),
        };

        private static void BuildBodyViewToggle(Transform character, RectTransform header, RectTransform previewRT,
            BackroomsUiTheme theme, Report report)
        {
            if (header == null || previewRT == null) { report.Missing("Character/Header o CharacterPreview"); return; }

            // Los botones cuelgan de la COLUMNA, no de la cabecera: la cabecera del vendor lleva layout
            // automático y los metía encima del título. Versiones viejas bajo Header se retiran.
            foreach (var old in new[] { "RopaBtn", "HeridasBtn" })
                if (header.Find(old) is Transform stale) Object.DestroyImmediate(stale.gameObject);
            var column = (RectTransform)character;
            var ropaBtn = EnsureToggleButton(column, "RopaBtn", "ROPA", -92f, theme);
            var heridasBtn = EnsureToggleButton(column, "HeridasBtn", "HERIDAS", -8f, theme);
            InspectionOnly(ropaBtn.gameObject);
            InspectionOnly(heridasBtn.gameObject);

            var wounds = character.Find(WoundsPanelName) as RectTransform;
            if (wounds == null)
            {
                var go = new GameObject(WoundsPanelName, typeof(RectTransform), typeof(Image));
                wounds = (RectTransform)go.transform;
                wounds.SetParent(character, false);
                go.GetComponent<Image>().raycastTarget = false;
                foreach (var z in PlaceholderZones)
                {
                    var zg = new GameObject(z.Name, typeof(RectTransform), typeof(Image));
                    var zrt = (RectTransform)zg.transform;
                    zrt.SetParent(wounds, false);
                    zrt.anchorMin = new Vector2(z.X, 1f - z.Y - z.H);
                    zrt.anchorMax = new Vector2(z.X + z.W, 1f - z.Y);
                    zrt.offsetMin = Vector2.zero;
                    zrt.offsetMax = Vector2.zero;
                    zg.GetComponent<Image>().raycastTarget = false;

                    var lbl = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
                    var lrt = (RectTransform)lbl.transform;
                    lrt.SetParent(zrt, false);
                    Stretch(lrt);
                    var tmp = lbl.GetComponent<TextMeshProUGUI>();
                    tmp.text = z.Name;
                    tmp.alignment = TextAlignmentOptions.Center;
                    tmp.fontSize = 12f;
                    tmp.enableWordWrapping = true;
                    tmp.raycastTarget = false;
                }

                var caption = new GameObject("Caption", typeof(RectTransform), typeof(TextMeshProUGUI));
                var crt = (RectTransform)caption.transform;
                crt.SetParent(wounds, false);
                crt.anchorMin = new Vector2(0f, 0f);
                crt.anchorMax = new Vector2(1f, 0f);
                crt.pivot = new Vector2(0.5f, 0f);
                crt.sizeDelta = new Vector2(0f, 40f);
                crt.anchoredPosition = new Vector2(0f, -44f);
                var ctmp = caption.GetComponent<TextMeshProUGUI>();
                ctmp.text = "PLACEHOLDER — sin ADR de cuerpo por zonas";
                ctmp.alignment = TextAlignmentOptions.Center;
                ctmp.fontSize = 13f;
                ctmp.raycastTarget = false;

                report.Count("zonas de heridas (placeholder)", PlaceholderZones.Length);
            }

            // La columna del vendor también reparte a sus hijos: sin esto el panel queda con ancho ~0
            // y cada etiqueta sale letra a letra en vertical.
            IgnoreLayout(wounds.gameObject);
            InspectionOnly(wounds.gameObject);
            // Mismo hueco que el preview del vendor (fuera de la vista Ropa, dentro de Heridas).
            wounds.anchorMin = previewRT.anchorMin;
            wounds.anchorMax = previewRT.anchorMax;
            wounds.pivot = previewRT.pivot;
            wounds.offsetMin = previewRT.offsetMin;
            wounds.offsetMax = previewRT.offsetMax;
            wounds.GetComponent<Image>().color = WithAlpha(theme.Ground, 0.85f);
            foreach (var tmp in wounds.GetComponentsInChildren<TextMeshProUGUI>(true))
                Retype(tmp, theme, theme.Mono, theme.Ink);
            foreach (var img in wounds.GetComponentsInChildren<Image>(true))
                if (img.gameObject != wounds.gameObject) SetSprite(img, theme.SunkenSlot, theme.Tape);

            var toggle = character.GetComponent<BackroomsBodyViewToggle>();
            if (toggle == null) toggle = character.gameObject.AddComponent<BackroomsBodyViewToggle>();
            var so = new SerializedObject(toggle);
            so.FindProperty("_ropaButton").objectReferenceValue = ropaBtn;
            so.FindProperty("_heridasButton").objectReferenceValue = heridasBtn;
            so.FindProperty("_previewRoot").objectReferenceValue = previewRT.gameObject;
            so.FindProperty("_previewBackdrop").objectReferenceValue = character.Find("BR_PreviewBackdrop")?.gameObject;
            so.FindProperty("_woundsPanel").objectReferenceValue = wounds.gameObject;
            so.ApplyModifiedPropertiesWithoutUndo();

            wounds.gameObject.SetActive(false);
            report.Count("conmutador ropa/heridas", 1);
        }

        // Zoom por zona (roadmap, idea de Joel): clic en la cinta de un slot acerca la cámara del preview a esa parte.
        private static readonly (string Path, string Zone)[] ZoneHeaders =
        {
            ("Containers/HeadContainer", "Head"),
            ("Containers/TorsoContainer", "Torso"),
            ("Containers/BackpackContainer", "Back"),
            ("Containers/LegsContainer", "Legs"),
            ("Containers/FeetContainer", "Feet"),
            ("BR_ContainersRight/BR_WaistContainer", "Waist"),
        };

        private static void BuildPreviewZoom(Transform character, RectTransform previewRT, GameObject root,
            BackroomsUiTheme theme, Report report)
        {
            var previewUi = root.GetComponentInChildren<CharacterPreviewUI>(true);
            if (previewRT == null || previewUi == null) { report.Missing("CharacterPreview o CharacterPreviewUI"); return; }

            var vendor = new SerializedObject(previewUi);
            var zoom = previewRT.GetComponent<BackroomsPreviewZoom>();
            if (zoom == null) zoom = previewRT.gameObject.AddComponent<BackroomsPreviewZoom>();
            var so = new SerializedObject(zoom);
            so.FindProperty("_camera").objectReferenceValue = vendor.FindProperty("_camera").objectReferenceValue;
            so.FindProperty("_characterVisuals").objectReferenceValue = vendor.FindProperty("_characterVisuals").objectReferenceValue;
            so.FindProperty("_rotationHandler").objectReferenceValue = root.GetComponentInChildren<CharacterPreviewRotationHandlerUI>(true);
            so.ApplyModifiedPropertiesWithoutUndo();

            int wired = 0;
            foreach (var (path, zone) in ZoneHeaders)
            {
                if (!(character.Find(path + "/Header") is Transform tape) || !tape.TryGetComponent<Image>(out var image))
                {
                    report.Missing(path + "/Header");
                    continue;
                }
                image.raycastTarget = true;
                var zoneHeader = tape.GetComponent<BackroomsZoneHeader>();
                if (zoneHeader == null) zoneHeader = tape.gameObject.AddComponent<BackroomsZoneHeader>();
                var hs = new SerializedObject(zoneHeader);
                hs.FindProperty("_zone").stringValue = zone;
                hs.FindProperty("_zoom").objectReferenceValue = zoom;
                hs.FindProperty("_idleSprite").objectReferenceValue = theme.DymoTape;
                hs.FindProperty("_activeSprite").objectReferenceValue = theme.DymoTapeRed;
                hs.ApplyModifiedPropertiesWithoutUndo();
                wired++;
            }
            report.Count("cintas con zoom", wired);
        }

        private static Button EnsureToggleButton(RectTransform header, string name, string label, float xFromRight,
            BackroomsUiTheme theme, float width = 76f)
        {
            var existing = header.Find(name) as RectTransform;
            GameObject go;
            if (existing == null)
            {
                go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
                var rt = (RectTransform)go.transform;
                rt.SetParent(header, false);
                rt.anchorMin = new Vector2(1f, 1f);
                rt.anchorMax = new Vector2(1f, 1f);
                rt.pivot = new Vector2(1f, 1f);
                rt.sizeDelta = new Vector2(76f, 28f);
                rt.anchoredPosition = new Vector2(xFromRight, -4f);

                var txtGo = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
                var txtRt = (RectTransform)txtGo.transform;
                txtRt.SetParent(rt, false);
                Stretch(txtRt);
                var tmp = txtGo.GetComponent<TextMeshProUGUI>();
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.fontSize = 12f;
                tmp.raycastTarget = false;
            }
            else go = existing.gameObject;
            IgnoreLayout(go);
            // Posición y tamaño SIEMPRE, no solo al crear: si no, un cambio de medida nunca llega a la variante.
            var brt = (RectTransform)go.transform;
            brt.anchorMin = Vector2.one;
            brt.anchorMax = Vector2.one;
            brt.pivot = Vector2.one;
            brt.sizeDelta = new Vector2(width, 28f);
            brt.anchoredPosition = new Vector2(xFromRight, -4f);

            var img = go.GetComponent<Image>();
            img.sprite = null;
            img.color = Color.white;
            var outline = go.GetComponent<Outline>();
            if (outline == null) outline = go.AddComponent<Outline>();
            outline.effectColor = Color.Lerp(theme.Ground, theme.Ink, 0.35f);
            outline.effectDistance = new Vector2(1f, -1f);
            var button = go.GetComponent<Button>();
            var colors = button.colors;
            colors.normalColor = Color.Lerp(theme.Ground, theme.Ink, 0.10f);
            colors.highlightedColor = Color.Lerp(theme.Ground, theme.Ink, 0.22f);
            colors.pressedColor = Color.Lerp(theme.Ground, theme.Fluorescent, 0.45f);
            colors.selectedColor = colors.normalColor;
            colors.disabledColor = Color.Lerp(theme.Ground, theme.Fluorescent, 0.35f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.05f;
            button.colors = colors;
            var label2 = go.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label2 != null)
            {
                label2.text = label;
                Retype(label2, theme, theme.MonoBold, theme.Ink);
                label2.fontStyle |= FontStyles.UpperCase;
            }
            return button;
        }

        /// <summary>
        /// Lo nuestro que cuelga fuera de los paneles del vendor no se oculta al cerrar TAB: se apaga y
        /// enciende con la inspección. Nace apagado (la captura lo fuerza visible).
        /// </summary>
        private static void InspectionOnly(GameObject go)
        {
            if (go.GetComponent<CanvasGroup>() == null) go.AddComponent<CanvasGroup>();
            if (go.GetComponent<BackroomsInspectionOnly>() == null) go.AddComponent<BackroomsInspectionOnly>();
            var group = go.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = false;
            group.interactable = false;
        }

        private static void IgnoreLayout(GameObject go)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.ignoreLayout = true;
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
