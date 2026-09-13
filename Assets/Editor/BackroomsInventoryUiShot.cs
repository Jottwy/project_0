#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Captura la variante de UI del inventario a un PNG SIN Play: instancia <c>BR_UI_Player</c> en
    /// una escena vacía, pasa sus canvas a Screen Space Camera, fuerza visibles los paneles, ata
    /// contenedores de mentira con objetos nuestros para que los huecos no salgan vacíos y renderiza
    /// con una cámara a 1920×1080. Menú Backrooms/UI/Capturar inventario. Headless: SIN
    /// <c>-nographics</c>, que sin GPU no hay imagen.
    ///
    /// Lo que NO captura, y hay que mirar en juego: la animación de los paneles, el tooltip y la
    /// selección (nacen de input), y el preview 3D del personaje (RenderTexture que sólo pinta
    /// con un jugador vivo). Vale para ver la piel — sprites, fuentes, tinta — no la fontanería.
    /// </summary>
    public static class BackroomsInventoryUiShot
    {
        private const string OutDir = "Builds/Captures";
        private const string OutFile = "inventario_tab.png";
        private const string DumpFile = "inventario_tab.txt";
        private const int Width = 1920;
        private const int Height = 1080;

        // Lo que se mete en la mochila de la captura: nombres de definición bajo Resources.
        private static readonly string[] SampleItems =
        {
            "Assets/Resources/Definitions/Item/BR_Bandage.asset",
            "Assets/Resources/Definitions/Item/BR_Almond Water.asset",
            "Assets/Resources/Definitions/Item/BR_Crank Flashlight.asset",
            "Assets/Resources/Definitions/Item/BR_Screwdriver.asset",
            "Assets/Resources/Definitions/Item/BR_Spray Can.asset",
            "Assets/Resources/Definitions/Item/BR_Metal Beam.asset",
        };

        [MenuItem("Backrooms/UI/Capturar inventario")]
        public static void Capture()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BackroomsInventoryUiBuilder.VariantPath);
            if (prefab == null)
            {
                Debug.LogError("[InventarioShot] Falta la variante: lanza antes Backrooms/UI/Build Inventory Variant.");
                return;
            }

            if (EditorApplication.isPlaying)
            {
                Debug.LogError("[InventarioShot] En Play no: sal de Play y vuelve a lanzarlo.");
                return;
            }
            // Con el editor abierto, escena ADITIVA que se cierra al acabar: la escena del usuario ni
            // se guarda ni se sustituye. Headless no hay nada abierto que proteger.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                Application.isBatchMode ? NewSceneMode.Single : NewSceneMode.Additive);

            var camGo = new GameObject("CaptureCamera", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            // Un pasillo amarillo de mentira detrás: sin él no se vería que el fondo oscurece algo.
            cam.backgroundColor = new Color(0.62f, 0.55f, 0.25f, 1f);
            cam.orthographic = true;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            // Solo la capa UI: con la escena del usuario cargada al lado, su geometría no se cuela.
            cam.cullingMask = LayerMask.GetMask("UI");
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            // La RenderTexture ANTES del layout: el CanvasScaler mide la cámara, y sin ella mide la Game view.
            cam.targetTexture = rt;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            try
            {
                foreach (var canvas in instance.GetComponentsInChildren<Canvas>(true))
                {
                    if (canvas.renderMode == RenderMode.WorldSpace) continue;
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = cam;
                    canvas.planeDistance = 10f;
                }

                ForceVisible(instance);
                PopulateContainers(instance);
                RenderCharacterPreview(instance);

                // TMP dinámico: pide los glifos antes del render o salen cuadrados.
                foreach (var tmp in instance.GetComponentsInChildren<TextMeshProUGUI>(true))
                    tmp.ForceMeshUpdate(true, true);
                Canvas.ForceUpdateCanvases();

                Directory.CreateDirectory(OutDir);
                string path = Path.Combine(OutDir, OutFile);
                RenderTo(cam, rt, path);
                DumpRects(instance, cam, Path.Combine(OutDir, DumpFile));
                Debug.Log($"[InventarioShot] {path} ({Width}×{Height}).");
            }
            finally
            {
                cam.targetTexture = null;
                rt.Release();
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(instance);
                Object.DestroyImmediate(camGo);
                if (!Application.isBatchMode) EditorSceneManager.CloseScene(scene, true);
            }
        }

        /// <summary>
        /// Los paneles del vendor nacen apagados (alpha 0) y los abre la inspección en Play; aquí no
        /// hay Play, así que se encienden todos a mano, incluido nuestro fondo.
        /// </summary>
        private static void ForceVisible(GameObject root)
        {
            // Sólo el inventario y la funda: el resto de prefabs anidados bajo los canvas (pausa,
            // opciones, muerte, loot, mensajes...) se apagan, o la captura es una sopa de paneles.
            var inventory = root.GetComponentInChildren<InventoryUI>(true);
            var hotbar = root.GetComponentInChildren<HotbarUI>(true);
            // Primero los nodos de primer nivel (la rueda de objetos cuelga de uno que no es Canvas),
            // después los hijos de cada canvas: sólo sobreviven las ramas con inventario o funda.
            foreach (Transform top in root.transform)
                top.gameObject.SetActive(top.GetComponentInChildren<InventoryUI>(true) != null
                                         || top.GetComponentInChildren<HotbarUI>(true) != null);
            foreach (var canvas in root.GetComponentsInChildren<Canvas>(true))
            {
                foreach (Transform child in canvas.transform)
                {
                    bool keep = (inventory != null && child == inventory.transform)
                                || (hotbar != null && child == hotbar.transform);
                    child.gameObject.SetActive(keep);
                }
            }

            var keepRoots = new List<Transform>();
            if (inventory != null) keepRoots.Add(inventory.transform);
            if (hotbar != null) keepRoots.Add(hotbar.transform);
            foreach (var kr in keepRoots)
            {
                foreach (var t in kr.GetComponentsInChildren<Transform>(true))
                    if (t.name != "KeyIcon") t.gameObject.SetActive(true); // el builder apaga los KeyIcon a propósito
                // La vista Heridas nace apagada (la enciende su botón); encendida taparía el muñeco.
                foreach (var t in kr.GetComponentsInChildren<Transform>(true))
                    if (t.name == "BR_WoundsPanel") t.gameObject.SetActive(false);
                foreach (var btn in kr.GetComponentsInChildren<Button>(true))
                    if (btn.name == "RopaBtn" || btn.name == "CraftBtn") btn.interactable = false;
                // Sin estación abierta no hay nada que enseñar, el tooltip nace del ratón y la rueda
                // de objetos (FPS_UI_ItemWheel, anidada en el inventario) sólo sale con su tecla.
                foreach (var t in kr.GetComponentsInChildren<Transform>(true))
                    if (t.name == "ItemTooltip") t.gameObject.SetActive(false);
                // De las estaciones solo la de crafteo a mano, la que se ve sin estación abierta.
                foreach (var t in kr.GetComponentsInChildren<Transform>(true))
                    if (t.parent != null && t.parent.name == "Workstations" && t.name != "CraftingStation") t.gameObject.SetActive(false);
                foreach (var wheel in kr.GetComponentsInChildren<ItemWheelUI>(true))
                    wheel.gameObject.SetActive(false);
                foreach (var g in kr.GetComponentsInChildren<CanvasGroup>(true))
                {
                    g.alpha = 1f;
                    g.interactable = true;
                    g.blocksRaycasts = true;
                }
            }
        }

        /// <summary>
        /// Cada <see cref="ItemContainerUI"/> recibe un contenedor propio del tamaño de sus slots;
        /// la mochila, además, unos cuantos objetos nuestros. Un fallo aquí no tumba la captura:
        /// se registra y se sigue, los huecos saldrán vacíos.
        /// </summary>
        private static void PopulateContainers(GameObject root)
        {
            var defs = new List<ItemDefinition>();
            foreach (var p in SampleItems)
            {
                var d = AssetDatabase.LoadAssetAtPath<ItemDefinition>(p);
                if (d != null) defs.Add(d); else Debug.LogWarning($"[InventarioShot] sin definición: {p}");
            }

            foreach (var ui in root.GetComponentsInChildren<ItemContainerUI>(true))
            {
                try
                {
                    int size = Mathf.Max(1, ui.GetComponentsInChildren<ItemSlotUIBase>(true).Length);
                    var container = new ItemContainer.Builder().WithName(ui.ContainerName).WithSize(size).Build();
                    ui.AttachToContainer(container);

                    if (ui.ContainerName == "Backpack" || ui.ContainerName == "Holster")
                    {
                        int added = 0;
                        foreach (var d in defs)
                        {
                            // De uno en uno: con StackSize 1 el vendor recorta un AddItemsById(n>1) a 1.
                            var (n, why) = container.AddItem(new ItemStack(new Item(d), 1));
                            if (n == 0) Debug.LogWarning($"[InventarioShot] {ui.ContainerName} rechaza {d.name}: {why}");
                            else added++;
                            if (ui.ContainerName == "Holster" && added >= 2) break;
                        }
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[InventarioShot] {ui.ContainerName}: {e.GetType().Name} {e.Message}");
                }
            }
        }

        /// <summary>
        /// El muñeco de la izquierda: el vendor lo pinta con una cámara propia sobre una RenderTexture
        /// compartida, y sólo enciende <c>CharacterVisuals</c> al inspeccionar con un jugador vivo.
        /// Aquí se enciende a mano y se renderiza una vez antes de capturar la UI.
        /// </summary>
        private static void RenderCharacterPreview(GameObject root)
        {
            var preview = root.GetComponentInChildren<CharacterPreviewUI>(true);
            if (preview == null) { Debug.LogWarning("[InventarioShot] sin CharacterPreviewUI: el muñeco saldrá vacío"); return; }
            for (var t = preview.transform; t != null; t = t.parent) t.gameObject.SetActive(true);
            foreach (var t in preview.GetComponentsInChildren<Transform>(true)) t.gameObject.SetActive(true);
            // Sin Play el Animator no corre y el muñeco queda en T-pose: se evalúa un frame a mano.
            foreach (var anim in preview.GetComponentsInChildren<Animator>(true))
            {
                anim.enabled = true;
                anim.Update(0f);
                anim.Update(0.25f);
            }
            var cam = preview.GetComponentInChildren<Camera>(true);
            if (cam == null || cam.targetTexture == null) { Debug.LogWarning("[InventarioShot] el preview no tiene cámara con RenderTexture"); return; }
            cam.Render();
        }

        /// <summary>
        /// Rectángulos en píxeles de pantalla (origen arriba-izquierda, como el greybox) de todo lo
        /// activo bajo el inventario y la funda, con lo que pinta cada nodo. Los hijos de un slot se
        /// omiten: son 50 copias de lo mismo.
        /// </summary>
        private static void DumpRects(GameObject root, Camera cam, string path)
        {
            var sb = new System.Text.StringBuilder();
            var inv = root.GetComponentInChildren<InventoryUI>(true);
            var hot = root.GetComponentInChildren<HotbarUI>(true);
            if (inv != null) Dump(inv.transform as RectTransform, cam, sb, 0);
            if (hot != null) Dump(hot.transform as RectTransform, cam, sb, 0);
            File.WriteAllText(path, sb.ToString());
        }

        private static readonly Vector3[] Corners = new Vector3[4];

        private static void Dump(RectTransform rt, Camera cam, System.Text.StringBuilder sb, int depth)
        {
            if (rt == null || !rt.gameObject.activeInHierarchy) return;
            rt.GetWorldCorners(Corners);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, Corners[0]);
            Vector2 b = RectTransformUtility.WorldToScreenPoint(cam, Corners[2]);
            var what = new System.Text.StringBuilder();
            if (rt.TryGetComponent<Image>(out var img) && img.enabled)
                what.Append($" img={(img.sprite ? img.sprite.name : "-")}#{ColorUtility.ToHtmlStringRGBA(img.color)}");
            if (rt.TryGetComponent<TextMeshProUGUI>(out var tmp) && tmp.enabled)
            {
                string t = tmp.text.Replace("\n", " ");
                what.Append($" txt=\"{(t.Length > 30 ? t.Substring(0, 30) : t)}\"@{tmp.fontSize:0}");
            }
            if (rt.TryGetComponent<LayoutGroup>(out var lg))
            {
                what.Append($" layout={lg.GetType().Name} pad={lg.padding.left},{lg.padding.right},{lg.padding.top},{lg.padding.bottom} align={lg.childAlignment}");
                if (lg is GridLayoutGroup g) what.Append($" cell={g.cellSize.x:0}x{g.cellSize.y:0} sp={g.spacing.x:0},{g.spacing.y:0} {g.constraint}:{g.constraintCount}");
                if (lg is HorizontalOrVerticalLayoutGroup h) what.Append($" sp={h.spacing:0} ctrl={h.childControlWidth},{h.childControlHeight} exp={h.childForceExpandWidth},{h.childForceExpandHeight}");
            }
            if (rt.TryGetComponent<RawImage>(out var raw) && raw.enabled) what.Append($" raw={(raw.texture ? raw.texture.name : "-")} uv={raw.uvRect}");
            if (rt.TryGetComponent<ContentSizeFitter>(out var csf)) what.Append($" fitter={csf.horizontalFit},{csf.verticalFit}");
            if (rt.TryGetComponent<AspectRatioFitter>(out var arf) && arf.enabled) what.Append($" aspect={arf.aspectMode}");
            what.Append($" a={rt.anchorMin.x:0.##},{rt.anchorMin.y:0.##}-{rt.anchorMax.x:0.##},{rt.anchorMax.y:0.##}");
            if (rt.TryGetComponent<LayoutElement>(out var le) && le.ignoreLayout) what.Append(" ignoreLayout");
            if (rt.TryGetComponent<CanvasGroup>(out var cg) && cg.alpha < 0.99f) what.Append($" alpha={cg.alpha:0.##}");
            sb.Append(' ', depth * 2).Append(rt.name)
              .Append($" [{Mathf.RoundToInt(a.x)},{Mathf.RoundToInt(Height - b.y)} {Mathf.RoundToInt(b.x - a.x)}x{Mathf.RoundToInt(b.y - a.y)}]")
              .Append(what).Append('\n');
            if (rt.GetComponent<ItemSlotUIBase>() != null) return;
            foreach (Transform c in rt) Dump(c as RectTransform, cam, sb, depth + 1);
        }

        private static void RenderTo(Camera cam, RenderTexture rt, string path)
        {
            var previous = RenderTexture.active;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            try
            {
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(tex);
            }
        }
    }
}
#endif
