#if UNITY_EDITOR
using System.Collections.Generic;
using BackroomsSurvival.UI;
using BackroomsSurvival.Wearables;
using PolymindGames;
using PolymindGames.UserInterface;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using BackroomsSurvival.Gameplay.Body;
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
            BuildTransitions(inv, theme, report);
        }

        /// <summary>
        /// Pulido 3 y 4: abrir y cerrar con fundido y columnas que se deslizan; rebote al recibir un objeto, destello y
        /// cinta roja al rechazarlo, y la carga en rojo cerca del máximo.
        /// </summary>
        private static void BuildTransitions(Transform inv, BackroomsUiTheme theme, Report report)
        {
            var transition = inv.GetComponent<BackroomsInventoryTransition>();
            if (transition == null) transition = inv.gameObject.AddComponent<BackroomsInventoryTransition>();
            // RightGroup es la columna Personaje (izquierda de la pantalla); LeftGroup, Alrededor (derecha).
            var columns = new[] { "RightGroup", "MiddleGroup/Inventory", "LeftGroup" };
            var slides = new[] { new Vector2(-28f, 0f), new Vector2(0f, -18f), new Vector2(28f, 0f) };
            var ts = new SerializedObject(transition);
            var columnsProp = ts.FindProperty("_columns");
            var slidesProp = ts.FindProperty("_slides");
            columnsProp.arraySize = columns.Length;
            slidesProp.arraySize = slides.Length;
            for (int i = 0; i < columns.Length; i++)
            {
                var column = inv.Find(columns[i]) as RectTransform;
                if (column == null) report.Missing(columns[i]);
                columnsProp.GetArrayElementAtIndex(i).objectReferenceValue = column;
                slidesProp.GetArrayElementAtIndex(i).vector2Value = slides[i];
            }
            ts.ApplyModifiedPropertiesWithoutUndo();

            var feedback = inv.GetComponent<BackroomsSlotFeedback>();
            if (feedback == null) feedback = inv.gameObject.AddComponent<BackroomsSlotFeedback>();
            var tag = EnsureRect(inv, "BR_RejectTag", typeof(Image), typeof(CanvasGroup));
            IgnoreLayout(tag.gameObject);
            tag.anchorMin = new Vector2(0.5f, 0.5f);
            tag.anchorMax = new Vector2(0.5f, 0.5f);
            tag.pivot = new Vector2(0.5f, 0f);
            tag.sizeDelta = new Vector2(260f, 30f);
            tag.SetAsLastSibling();
            var tagText = tag.Find("Label") is Transform tagLabel
                ? tagLabel.GetComponent<TextMeshProUGUI>()
                : Label(tag, "Label", string.Empty, theme.MonoBold, 14f, theme.TapeInk, TextAlignmentOptions.Center);
            Stretch((RectTransform)tagText.transform);
            var tagImage = tag.GetComponent<Image>();
            Tape(tag, tagImage, theme);
            if (theme.DymoTapeRed != null) tagImage.sprite = theme.DymoTapeRed;
            tagImage.raycastTarget = false;
            tagText.raycastTarget = false;
            var tagGroup = tag.GetComponent<CanvasGroup>();
            tagGroup.alpha = 0f;
            tagGroup.blocksRaycasts = false;
            tagGroup.interactable = false;

            var fs = new SerializedObject(feedback);
            fs.FindProperty("_rejectTag").objectReferenceValue = tag;
            fs.FindProperty("_rejectGroup").objectReferenceValue = tagGroup;
            fs.FindProperty("_rejectText").objectReferenceValue = tagText;
            fs.FindProperty("_warnColor").colorValue = Color.Lerp(theme.TapeRed, Color.white, 0.3f);
            var weightUi = inv.GetComponentInChildren<InventoryWeightDisplayUI>(true);
            if (weightUi != null)
            {
                var ws = new SerializedObject(weightUi);
                fs.FindProperty("_loadText").objectReferenceValue = ws.FindProperty("_weightText").objectReferenceValue;
                if (ws.FindProperty("_weightBar").objectReferenceValue is Object bar)
                    fs.FindProperty("_loadFill").objectReferenceValue = new SerializedObject(bar).FindProperty("_fillImage").objectReferenceValue;
                // La barra del vendor no expone su relleno en este prefab: el relleno es la Image que ya pinta Paint().
                if (fs.FindProperty("_loadFill").objectReferenceValue == null
                    && inv.Find("MiddleGroup/Inventory/Weight/WeightBarBG/WeightBar") is Transform fill)
                    fs.FindProperty("_loadFill").objectReferenceValue = fill.GetComponent<Image>();
            }
            else report.Missing("InventoryWeightDisplayUI");
            fs.ApplyModifiedPropertiesWithoutUndo();
            report.Count("pulido abrir/cerrar y objetos", 1);
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

                        // Pulido: el render se funde con la caja por los bordes en vez de acabar en seco.
                        var fade = EnsureRect(backdrop, "BR_EdgeFade", typeof(RawImage), typeof(BackroomsEdgeFade));
                        Stretch(fade);
                        var fadeImage = fade.GetComponent<RawImage>();
                        fadeImage.raycastTarget = false;
                        fadeImage.enabled = false;
                        var fadeSo = new SerializedObject(fade.GetComponent<BackroomsEdgeFade>());
                        fadeSo.FindProperty("_color").colorValue = theme.Ground;
                        fadeSo.ApplyModifiedPropertiesWithoutUndo();

                        // Cinta con la zona enfocada (BackroomsPreviewZoom la rellena y la funde).
                        var zoneTag = EnsureRect(backdrop, "BR_ZoneTag", typeof(Image), typeof(CanvasGroup));
                        Place(zoneTag, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 20f), new Vector2(160f, 30f));
                        var zoneText = zoneTag.Find("Label") is Transform zoneLabel
                            ? zoneLabel.GetComponent<TextMeshProUGUI>()
                            : Label(zoneTag, "Label", string.Empty, theme.MonoBold, 16f, theme.TapeInk, TextAlignmentOptions.Center);
                        Stretch((RectTransform)zoneText.transform);
                        Tape(zoneTag, zoneTag.GetComponent<Image>(), theme);
                        zoneTag.GetComponent<Image>().raycastTarget = false;
                        zoneText.raycastTarget = false;
                        var tagGroup = zoneTag.GetComponent<CanvasGroup>();
                        tagGroup.alpha = 0f;
                        tagGroup.blocksRaycasts = false;
                        tagGroup.interactable = false;
                    }
                }

                // D14: las manos van en la barra; la caja «Manos» aparte se retira.
                if (character.Find("BR_Hands") is Transform oldHands) Object.DestroyImmediate(oldHands.gameObject);

                // Segunda columna de slots, a la derecha del muñeco (greybox). Pieza 6: de arriba abajo como el cuerpo.
                if (character.Find("Containers/HeadContainer") is Transform headSlot && headSlot.TryGetComponent<ItemContainerUI>(out var headTemplate))
                {
                    var rightSlots = EnsureRect(character, "BR_ContainersRight", typeof(VerticalLayoutGroup));
                    Place(rightSlots, Vector2.one, Vector2.one, new Vector2(-8f, -(top + 40f)), new Vector2(88f, 5f * 80f + 4f * 28f));
                    var column = rightSlots.GetComponent<VerticalLayoutGroup>();
                    column.spacing = 28f;
                    column.childAlignment = TextAnchor.UpperCenter;
                    column.childControlWidth = false;
                    column.childControlHeight = false;
                    column.childForceExpandWidth = false;
                    column.childForceExpandHeight = false;
                    InspectionOnly(rightSlots.gameObject);
                    // Un guante por mano: el hueco del par se retira y su entrada muerta sale de la lista del vendor.
                    if (rightSlots.Find("BR_GlovesContainer") is Transform oldGloves)
                    {
                        Object.DestroyImmediate(oldGloves.gameObject);
                        var inventorySo = new SerializedObject(inventoryUI);
                        var uis = inventorySo.FindProperty("_nonPersistentContainers");
                        for (int i = uis.arraySize - 1; i >= 0; i--)
                            if (uis.GetArrayElementAtIndex(i).objectReferenceValue == null) uis.DeleteArrayElementAtIndex(i);
                        inventorySo.ApplyModifiedPropertiesWithoutUndo();
                    }
                    for (int k = 0; k < RightSlots.Length; k++)
                    {
                        var (slotName, containerName, label) = RightSlots[k];
                        var slotUi = EnsureEquipmentSlot(rightSlots, slotName, containerName, label, headTemplate, theme);
                        slotUi.transform.SetSiblingIndex(k);
                        RegisterContainerUI(inventoryUI, slotUi);
                    }
                    report.Count("huecos de la segunda columna", RightSlots.Length);
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
                    // ADR-149 enm. 1: bolsillos de lo puesto encima y en las piernas, con los huecos rotos tachados.
                    float thirdTop = secondTop + secondH + 12f;
                    float pocketH = SectionHeight(4);
                    var (outerSection, outerGrid) = PocketSection(inventory, inventoryUI, backpack, theme, "BR_OuterPocketsSection", "BR_OuterPockets",
                        BackroomsBackpackPrototypeCreator.OuterPocketsContainer, BackroomsBackpackPrototypeCreator.OuterContainer, "BOLSILLOS · ENCIMA",
                        thirdTop, pocketH, 4);
                    var (legsSection, legsGrid) = PocketSection(inventory, inventoryUI, backpack, theme, "BR_LegsPocketsSection", "BR_LegsPockets",
                        BackroomsBackpackPrototypeCreator.LegsPocketsContainer, "Legs", "BOLSILLOS · PIERNAS", thirdTop + pocketH + 12f, pocketH, 6);
                    BuildSections(inventory, firstTop, new[] { "bag", "pockets", "outer", "legs" }, new[] { bag, pockets, outerSection, legsSection },
                        new[] { storage, backpack, outerGrid, legsGrid }, theme);
                    report.Count("bolsillos por prenda", 2);
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
            const float aroundTabW = 118f, craftTabW = 96f, tailorTabW = 118f, tabGap = 4f;
            var aroundBtn = EnsureToggleButton(aroundHeader, "AroundBtn", "ALREDEDOR", -(RightWidth - 8f - aroundTabW), theme, aroundTabW);
            var craftBtn = EnsureToggleButton(aroundHeader, "CraftBtn", "CRAFTEO",
                -(RightWidth - 8f - aroundTabW - tabGap - craftTabW), theme, craftTabW);
            // ADR-149 (Joel, 2026-09-14): la sastrería tiene pestaña propia.
            var tailorBtn = EnsureToggleButton(aroundHeader, "TailorBtn", "SASTRERÍA",
                -(RightWidth - 8f - aroundTabW - tabGap - craftTabW - tabGap - tailorTabW), theme, tailorTabW);
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
                so.FindProperty("_tailorButton").objectReferenceValue = tailorBtn;
                so.FindProperty("_tailoringPanel").objectReferenceValue = BuildTailoringPanel((RectTransform)right, top, theme, report);
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
                // Joel (2026-09-14): en juego solo los huecos, que aparecen al usar el cinturón y se desvanecen a los 2 s.
                var beltLayout = hotbar.transform.Find("Layout");
                var frame = hotbar.transform.Find("SelectionFrame");
                belt.FindProperty("_strap").objectReferenceValue = beltLayout.Find(StrapName)?.GetComponent<Image>();
                // `??` no sirve con componentes de Unity: GetComponent devuelve un nulo falso en el editor.
                var slotsGroup = beltLayout.GetComponent<CanvasGroup>();
                if (slotsGroup == null) slotsGroup = beltLayout.gameObject.AddComponent<CanvasGroup>();
                belt.FindProperty("_slotsGroup").objectReferenceValue = slotsGroup;
                if (frame != null)
                {
                    var frameGroup = frame.GetComponent<CanvasGroup>();
                    if (frameGroup == null) frameGroup = frame.gameObject.AddComponent<CanvasGroup>();
                    belt.FindProperty("_frameGroup").objectReferenceValue = frameGroup;
                }
                belt.ApplyModifiedPropertiesWithoutUndo();
                var vendorHotbar = new SerializedObject(hotbar);
                vendorHotbar.FindProperty("_holsterVisibleDuration").floatValue = 0f; // siempre activo: el fundido es nuestro
                vendorHotbar.ApplyModifiedPropertiesWithoutUndo();
            }
            else report.Missing("HotbarUI");
            report.Count("columnas", 3);
        }

        private const string TailoringPanelName = "BR_TailoringPanel";
        private const float TailoringRowHeight = 36f;

        /// <summary>
        /// ADR-149, pestaña SASTRERÍA (<see cref="BackroomsTailoringPanel"/>): el mismo hueco que las estaciones, una lista con
        /// scroll y una fila plantilla apagada (nombre, estado, COSER, CINTA) que el panel clona; abajo materiales y aviso.
        /// </summary>
        private static GameObject BuildTailoringPanel(RectTransform right, float top, BackroomsUiTheme theme, Report report)
        {
            var panel = EnsureRect(right, TailoringPanelName);
            Inset(panel, 0f, InspectorHeight + WindowGap, 0f, top);
            IgnoreLayout(panel.gameObject);
            InspectionOnly(panel.gameObject);

            var title = Label(panel, "Title", "ROPA PUESTA · ZONA A ZONA", theme.MonoBold, 12f, theme.InkDim, TextAlignmentOptions.MidlineLeft);
            var titleRt = (RectTransform)title.transform;
            titleRt.anchorMin = new Vector2(0f, 1f);
            titleRt.anchorMax = Vector2.one;
            titleRt.pivot = new Vector2(0.5f, 1f);
            titleRt.offsetMin = new Vector2(BoxPad, -34f);
            titleRt.offsetMax = new Vector2(-BoxPad, -8f);

            var scroll = EnsureRect(panel, "Scroll", typeof(Image), typeof(ScrollRect));
            Inset(scroll, BoxPad, 64f, BoxPad, 40f);
            var scrollImage = scroll.GetComponent<Image>();
            scrollImage.sprite = null;
            scrollImage.color = Color.clear;
            scrollImage.raycastTarget = true;
            var viewport = EnsureRect(scroll, "Viewport", typeof(RectMask2D));
            Stretch(viewport);
            var content = EnsureRect(viewport, "Content", typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = Vector2.zero;
            content.offsetMax = Vector2.zero;
            var list = content.GetComponent<VerticalLayoutGroup>();
            list.spacing = 4f;
            list.childControlWidth = true;
            list.childControlHeight = true;
            list.childForceExpandWidth = true;
            list.childForceExpandHeight = false;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var scrollRect = scroll.GetComponent<ScrollRect>();
            scrollRect.viewport = viewport;
            scrollRect.content = content;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 24f;

            var empty = Label(panel, "Empty", "No llevas ropa que se pueda coser", theme.Mono, 15f, theme.InkDim, TextAlignmentOptions.Center);
            Inset((RectTransform)empty.transform, BoxPad, 64f, BoxPad, 40f);

            // La fila plantilla vive fuera de la lista y apagada: el panel la clona dentro de Content.
            var row = EnsureRect(panel, "RowTemplate", typeof(Image), typeof(LayoutElement));
            Place(row, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(RightWidth - 2f * BoxPad, TailoringRowHeight));
            var rowLayout = row.GetComponent<LayoutElement>();
            rowLayout.ignoreLayout = false;
            rowLayout.preferredHeight = TailoringRowHeight;
            rowLayout.minHeight = TailoringRowHeight;
            var rowImage = row.GetComponent<Image>();
            SetSprite(rowImage, theme.SunkenSlot, theme.Tape);
            rowImage.raycastTarget = true;
            const float buttonW = 72f, buttonsW = 2f * buttonW + 4f + 12f;
            var rowName = Label(row, "Name", "Prenda", theme.MonoBold, 13f, theme.Ink, TextAlignmentOptions.MidlineLeft);
            var nameRt = (RectTransform)rowName.transform;
            nameRt.anchorMin = new Vector2(0f, 1f);
            nameRt.anchorMax = Vector2.one;
            nameRt.pivot = new Vector2(0.5f, 1f);
            nameRt.offsetMin = new Vector2(10f, -19f);
            nameRt.offsetMax = new Vector2(-buttonsW, -2f);
            rowName.overflowMode = TextOverflowModes.Ellipsis;
            var rowState = Label(row, "State", "estado", theme.Mono, 11f, theme.InkDim, TextAlignmentOptions.MidlineLeft);
            var stateRt = (RectTransform)rowState.transform;
            stateRt.anchorMin = Vector2.zero;
            stateRt.anchorMax = new Vector2(1f, 0f);
            stateRt.pivot = new Vector2(0.5f, 0f);
            stateRt.offsetMin = new Vector2(10f, 2f);
            stateRt.offsetMax = new Vector2(-buttonsW, 18f);
            rowState.overflowMode = TextOverflowModes.Ellipsis;
            EnsureToggleButton(row, "SewBtn", "COSER", -(6f + buttonW + 4f), theme, buttonW);
            EnsureToggleButton(row, "TapeBtn", "CINTA", -6f, theme, buttonW);
            row.gameObject.SetActive(false);

            var materials = Label(panel, "Materials", "Aguja 0 · Hilo 0 · Tela 0 · Cinta 0", theme.Mono, 12f, theme.Ink, TextAlignmentOptions.MidlineLeft);
            var materialsRt = (RectTransform)materials.transform;
            materialsRt.anchorMin = Vector2.zero;
            materialsRt.anchorMax = new Vector2(1f, 0f);
            materialsRt.pivot = new Vector2(0.5f, 0f);
            materialsRt.offsetMin = new Vector2(BoxPad, 34f);
            materialsRt.offsetMax = new Vector2(-BoxPad, 58f);
            var notice = Label(panel, "Notice", string.Empty, theme.Mono, 12f, theme.Fluorescent, TextAlignmentOptions.MidlineLeft);
            var noticeRt = (RectTransform)notice.transform;
            noticeRt.anchorMin = Vector2.zero;
            noticeRt.anchorMax = new Vector2(1f, 0f);
            noticeRt.pivot = new Vector2(0.5f, 0f);
            noticeRt.offsetMin = new Vector2(BoxPad, 8f);
            noticeRt.offsetMax = new Vector2(-BoxPad, 32f);
            notice.overflowMode = TextOverflowModes.Ellipsis;

            var tailoring = panel.GetComponent<BackroomsTailoringPanel>();
            if (tailoring == null) tailoring = panel.gameObject.AddComponent<BackroomsTailoringPanel>();
            var so = new SerializedObject(tailoring);
            so.FindProperty("_content").objectReferenceValue = content;
            so.FindProperty("_rowTemplate").objectReferenceValue = row;
            so.FindProperty("_empty").objectReferenceValue = empty;
            so.FindProperty("_materials").objectReferenceValue = materials;
            so.FindProperty("_notice").objectReferenceValue = notice;
            so.FindProperty("_garmentInk").colorValue = theme.Ink;
            so.FindProperty("_zoneInk").colorValue = theme.InkDim;
            so.ApplyModifiedPropertiesWithoutUndo();

            panel.gameObject.SetActive(false);
            report.Count("panel de sastrería", 1);
            return panel.gameObject;
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
        private static (RectTransform section, RectTransform grid) PocketSection(RectTransform inventory, InventoryUI inventoryUI,
            RectTransform template, BackroomsUiTheme theme, string sectionName, string gridName, string container, string owner, string label,
            float top, float height, int sibling)
        {
            var section = Section(inventory, sectionName, theme, top, height, label + " · SIN PRENDA", "ponte una prenda con bolsillos", sibling);
            var grid = EnsureRect(inventory, gridName, typeof(GridLayoutGroup), typeof(ItemContainerUI), typeof(BackroomsWornSlotsUI));
            grid.SetSiblingIndex(sibling + 1);
            PlaceGrid(grid, top, height);
            var ui = grid.GetComponent<ItemContainerUI>();
            BindContainerUI(ui, container, template.GetComponent<ItemContainerUI>());
            RegisterContainerUI(inventoryUI, ui);
            EnsureSlots(ui, BackroomsBackpackPrototypeCreator.PocketContainerSlots, theme);
            var so = new SerializedObject(grid.GetComponent<BackroomsWornSlotsUI>());
            so.FindProperty("_ownerContainer").stringValue = owner;
            so.FindProperty("_label").stringValue = label;
            so.FindProperty("_emptyTitle").stringValue = "SIN PRENDA";
            so.FindProperty("_emptyCount").stringValue = "ponte una prenda con bolsillos";
            so.FindProperty("_title").objectReferenceValue = section.Find("Title").GetComponent<TextMeshProUGUI>();
            so.FindProperty("_count").objectReferenceValue = section.Find("Count").GetComponent<TextMeshProUGUI>();
            so.ApplyModifiedPropertiesWithoutUndo();
            return (section, grid);
        }

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

        // Segunda columna del personaje: objeto, contenedor y cinta.
        private static readonly (string Name, string Container, string Label)[] RightSlots =
        {
            ("BR_FaceContainer", BackroomsBackpackPrototypeCreator.FaceContainer, "Face"),
            ("BR_OuterContainer", BackroomsBackpackPrototypeCreator.OuterContainer, "Outer"),
            ("BR_GloveLContainer", BackroomsBackpackPrototypeCreator.GloveLContainer, "Glove L"),
            ("BR_GloveRContainer", BackroomsBackpackPrototypeCreator.GloveRContainer, "Glove R"),
            ("BR_WaistContainer", BackroomsBackpackPrototypeCreator.WaistContainer, "Waist"),
        };

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

        // ─── Conmutador Ropa | Heridas ────
        //
        // ADR-149 R0: la vista Heridas son las 15 zonas del cuerpo, de frente (su izquierda a la derecha de la pantalla).
        // Coordenadas en fracción del panel; cada casilla pinta su estado y trata con un clic.
        private const string WoundsPanelName = "BR_WoundsPanel";
        private const float WoundZoneW = 0.29f;
        private const float WoundZoneH = 0.1f;
        /// <summary>Los mismos colores que pinta <c>BackroomsWoundZoneUI</c>.</summary>
        private static (string, Color)[] WoundLegend(BackroomsUiTheme theme) => new[]
        {
            ("rasguño", new Color(0.69f, 0.53f, 0.12f)),
            ("corte", theme.TapeRed),
            ("fractura", new Color(0.37f, 0.23f, 0.55f)),
            ("tratado", new Color(0.29f, 0.35f, 0.23f)),
        };

        private static readonly (BodyZone Zone, float X, float Y)[] WoundZones =
        {
            (BodyZone.Head, 0.355f, 0.06f),
            (BodyZone.UpperArmR, 0.03f, 0.18f), (BodyZone.Chest, 0.355f, 0.18f), (BodyZone.UpperArmL, 0.68f, 0.18f),
            (BodyZone.ForearmR, 0.03f, 0.30f), (BodyZone.Abdomen, 0.355f, 0.30f), (BodyZone.ForearmL, 0.68f, 0.30f),
            (BodyZone.HandR, 0.03f, 0.42f), (BodyZone.HandL, 0.68f, 0.42f),
            (BodyZone.ThighR, 0.19f, 0.54f), (BodyZone.ThighL, 0.52f, 0.54f),
            (BodyZone.ShinR, 0.19f, 0.66f), (BodyZone.ShinL, 0.52f, 0.66f),
            (BodyZone.FootR, 0.19f, 0.78f), (BodyZone.FootL, 0.52f, 0.78f),
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
            // ADR-149 R0: el panel de ejemplo (sin zonas reales) se sustituye entero.
            if (wounds != null && wounds.GetComponentInChildren<BackroomsWoundZoneUI>(true) == null)
            {
                Object.DestroyImmediate(wounds.gameObject);
                wounds = null;
            }
            if (wounds == null)
            {
                var go = new GameObject(WoundsPanelName, typeof(RectTransform), typeof(Image));
                wounds = (RectTransform)go.transform;
                wounds.SetParent(character, false);
                go.GetComponent<Image>().raycastTarget = false;
            }

            var caption = wounds.Find("Caption") as RectTransform;
            if (caption == null)
            {
                caption = (RectTransform)new GameObject("Caption", typeof(RectTransform), typeof(TextMeshProUGUI)).transform;
                caption.SetParent(wounds, false);
            }
            caption.anchorMin = new Vector2(0f, 0f);
            caption.anchorMax = new Vector2(1f, 0f);
            caption.pivot = new Vector2(0.5f, 0f);
            caption.sizeDelta = new Vector2(0f, 40f);
            caption.anchoredPosition = new Vector2(0f, 4f);
            var captionText = caption.GetComponent<TextMeshProUGUI>();
            captionText.text = "Clic: venda o férula · clic derecho: coser o cinta";
            captionText.alignment = TextAlignmentOptions.Center;
            captionText.fontSize = 13f;
            captionText.raycastTarget = false;

            foreach (var z in WoundZones)
            {
                string zoneName = "Zone_" + z.Zone;
                var zrt = wounds.Find(zoneName) as RectTransform;
                if (zrt == null)
                {
                    zrt = (RectTransform)new GameObject(zoneName, typeof(RectTransform), typeof(Image), typeof(BackroomsWoundZoneUI)).transform;
                    zrt.SetParent(wounds, false);
                    var lbl = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
                    lbl.transform.SetParent(zrt, false);
                    Stretch((RectTransform)lbl.transform);
                }
                zrt.anchorMin = new Vector2(z.X, 1f - z.Y - WoundZoneH);
                zrt.anchorMax = new Vector2(z.X + WoundZoneW, 1f - z.Y);
                zrt.offsetMin = Vector2.zero;
                zrt.offsetMax = Vector2.zero;
                var image = zrt.GetComponent<Image>();
                image.raycastTarget = true;
                var tmp = zrt.Find("Label").GetComponent<TextMeshProUGUI>();
                tmp.text = BodyZones.Label(z.Zone);
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.fontSize = 11f;
                tmp.enableWordWrapping = true;
                tmp.raycastTarget = false;

                var zs = new SerializedObject(zrt.GetComponent<BackroomsWoundZoneUI>());
                zs.FindProperty("_zone").intValue = (int)z.Zone;
                zs.FindProperty("_fill").objectReferenceValue = image;
                zs.FindProperty("_label").objectReferenceValue = tmp;
                zs.FindProperty("_notice").objectReferenceValue = captionText;
                zs.FindProperty("_healthy").colorValue = theme.Tape;
                zs.FindProperty("_cut").colorValue = theme.TapeRed;
                zs.ApplyModifiedPropertiesWithoutUndo();
            }
            report.Count("zonas de heridas", WoundZones.Length);

            // ADR-149 R2c (Joel: «como Project Zomboid»): el color de cada zona dice qué tiene.
            var legend = EnsureRect(wounds, "Legend", typeof(HorizontalLayoutGroup));
            legend.anchorMin = new Vector2(0f, 0f);
            legend.anchorMax = new Vector2(1f, 0f);
            legend.pivot = new Vector2(0.5f, 0f);
            legend.sizeDelta = new Vector2(0f, 22f);
            legend.anchoredPosition = new Vector2(0f, 46f);
            var legendLayout = legend.GetComponent<HorizontalLayoutGroup>();
            legendLayout.spacing = 6f;
            legendLayout.childAlignment = TextAnchor.MiddleCenter;
            legendLayout.childControlWidth = false;
            legendLayout.childControlHeight = false;
            legendLayout.childForceExpandWidth = false;
            legendLayout.childForceExpandHeight = false;
            foreach (var (text, color) in WoundLegend(theme))
            {
                var chip = EnsureRect(legend, "Chip_" + text, typeof(Image));
                chip.sizeDelta = new Vector2(78f, 20f);
                var chipImage = chip.GetComponent<Image>();
                chipImage.color = color;
                chipImage.raycastTarget = false;
                Stretch((RectTransform)Label(chip, "Text", text, theme.MonoBold, 11f, theme.Ink, TextAlignmentOptions.Center).transform);
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
                if (img.gameObject != wounds.gameObject && img.transform.parent.name != "Legend") SetSprite(img, theme.SunkenSlot, theme.Tape);

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
            ("BR_ContainersRight/BR_FaceContainer", "Face"),
            ("BR_ContainersRight/BR_OuterContainer", "Outer"),
            ("BR_ContainersRight/BR_GloveLContainer", "Hands"),
            ("BR_ContainersRight/BR_GloveRContainer", "Hands"),
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
            if (character.Find("BR_PreviewBackdrop/BR_ZoneTag") is Transform zoneTag)
            {
                so.FindProperty("_zoneTag").objectReferenceValue = zoneTag.GetComponent<CanvasGroup>();
                so.FindProperty("_zoneText").objectReferenceValue = zoneTag.GetComponentInChildren<TextMeshProUGUI>(true);
            }
            else report.Missing("BR_PreviewBackdrop/BR_ZoneTag");
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
