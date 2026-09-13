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

            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("CaptureCamera", typeof(Camera));
            var cam = camGo.GetComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            // Un pasillo amarillo de mentira detrás: sin él no se vería que el fondo oscurece algo.
            cam.backgroundColor = new Color(0.62f, 0.55f, 0.25f, 1f);
            cam.orthographic = true;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;

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
                RenderTo(cam, path);
                Debug.Log($"[InventarioShot] {path} ({Width}×{Height}).");
            }
            finally
            {
                Object.DestroyImmediate(instance);
                Object.DestroyImmediate(camGo);
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
                // Sin estación abierta no hay nada que enseñar, el tooltip nace del ratón y la rueda
                // de objetos (FPS_UI_ItemWheel, anidada en el inventario) sólo sale con su tecla.
                foreach (var t in kr.GetComponentsInChildren<Transform>(true))
                    if (t.name == "Workstations" || t.name == "ItemTooltip") t.gameObject.SetActive(false);
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

        private static void RenderTo(Camera cam, string path)
        {
            var rt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 };
            var previous = RenderTexture.active;
            var tex = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = previous;
                Object.DestroyImmediate(tex);
                rt.Release();
                Object.DestroyImmediate(rt);
            }
        }
    }
}
#endif
