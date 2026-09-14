#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using BackroomsSurvival.Gameplay.Mapping;
using PolymindGames.UserInterface;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// P0.5 de MAPPING-PROTOTYPE — retrata el libro de supervivencia con la pestaña «Notas» SIN entrar en Play y sin abrir
    /// ni tocar escenas: instancia el wieldable del vendor en una escena de previsualización aislada, monta la pestaña,
    /// dibuja dos hojas con un recuerdo sintético y guarda PNG en <c>Temp/captures/</c>.
    /// </summary>
    /// <remarks>
    /// Pensado para convivir con otra sesión en el mismo editor: no cambia la escena activa ni el modo Play, y lo que
    /// crea muere con la escena de previsualización. En modo edición no hay <c>Awake</c> de los MonoBehaviour del vendor,
    /// así que el libro sale en su pose de bind (sirve para juzgar la página, no el encuadre en juego).
    /// </remarks>
    public static class MappingBookCaptureTool
    {
        private const string WieldablePath = "Assets/PolymindGames/STP/Prefabs/Wieldables/STP_Wieldable_SurvivalBook.prefab";
        private const string OutDir = "Temp/captures";
        private const int Width = 1280;
        private const int Height = 900;

        [MenuItem("Backrooms/Mapeado/Capturar libro con Notas")]
        public static void Capture()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(WieldablePath);
            if (prefab == null)
            {
                Debug.LogError($"MAPBOOKSHOT no encuentro {WieldablePath}");
                return;
            }

            Directory.CreateDirectory(OutDir);
            Scene scene = EditorSceneManager.NewPreviewScene();
            var texture = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            try
            {
                var book = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                foreach (var skinned in book.GetComponentsInChildren<SkinnedMeshRenderer>(true)) skinned.updateWhenOffscreen = true;

                SurvivalBookUI ui = book.GetComponentInChildren<SurvivalBookUI>(true);
                if (ui == null)
                {
                    Debug.LogError("MAPBOOKSHOT el wieldable no tiene SurvivalBookUI");
                    return;
                }

                MapMemory memory = SyntheticMemory();
                var notebook = new MapNotebook(new MapPen("Boli azul", 0xF22A47A8u, 3f, 0.002f, 1f));
                MapNotebookBookTab tab = MapNotebookBookTab.Attach(ui, null, notebook, 0xFFF1EFE5u);
                if (tab == null) return;

                // Sin Play nadie abre el libro: se activa la cadena hasta el panel para que se pinte.
                Transform notes = FindDeep(book.transform, "NotesContent");
                for (Transform t = notes != null ? notes.parent : null; t != null; t = t.parent) t.gameObject.SetActive(true);

                DrawSheet(notebook, memory, new MapHere(true, 20, 20, 0));
                DrawSheet(notebook, memory, new MapHere(true, 120, 30, 0));
                notebook.Locate(memory, new MapHere(true, 120, 30, 0), 40.0, 0f);

                var cameraGo = new GameObject("MappingCaptureCamera");
                SceneManager.MoveGameObjectToScene(cameraGo, scene);
                var camera = cameraGo.AddComponent<Camera>();
                camera.scene = scene;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.16f, 0.14f, 0.12f, 1f);
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 20f;
                camera.fieldOfView = 40f;
                camera.targetTexture = texture;
                camera.cullingMask = ~0;

                var lightGo = new GameObject("MappingCaptureLight");
                SceneManager.MoveGameObjectToScene(lightGo, scene);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.2f;
                lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

                var canvases = new List<Canvas>(book.GetComponentsInChildren<Canvas>(true));
                foreach (Canvas canvas in canvases) canvas.worldCamera = camera;

                // Encuadre: las dos páginas (menú y contenido) de frente, desde el lado que se ve de la cara del Canvas.
                Bounds pages = PageBounds(canvases, out Vector3 facing);
                float distance = pages.extents.magnitude / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.15f;
                cameraGo.transform.position = pages.center - facing * distance;
                cameraGo.transform.rotation = Quaternion.LookRotation(facing, Vector3.up);

                var written = new List<string>();
                notebook.Select(0);
                tab.RefreshForCapture(memory);
                tab.RefreshForCapture(memory);
                written.Add(Shot(camera, texture, "mapbook_01_notas_hoja1"));

                notebook.Select(1);
                tab.RefreshForCapture(memory);
                tab.PoseFlipForCapture(0.4f);
                written.Add(Shot(camera, texture, "mapbook_02_pasar_adelante_40"));
                tab.PoseFlipForCapture(0.75f);
                written.Add(Shot(camera, texture, "mapbook_03_pasar_adelante_75"));

                notebook.Select(0);
                tab.RefreshForCapture(memory);
                tab.PoseFlipForCapture(0.4f);
                written.Add(Shot(camera, texture, "mapbook_04_pasar_atras_40"));
                tab.PoseFlipForCapture(1f);
                tab.RefreshForCapture(memory);
                written.Add(Shot(camera, texture, "mapbook_05_notas_final"));

                // Plano cerrado de la hoja.
                Bounds sheet = SheetBounds(tab);
                if (sheet.size.sqrMagnitude > 0f)
                {
                    float close = sheet.extents.magnitude / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.1f;
                    cameraGo.transform.position = sheet.center - facing * close;
                    written.Add(Shot(camera, texture, "mapbook_06_hoja_de_cerca"));
                }

                Debug.Log($"MAPBOOKSHOT ok pages={pages.size} facing={facing} files={string.Join(",", written)}");
            }
            catch (Exception exception)
            {
                Debug.LogError("MAPBOOKSHOT " + exception);
            }
            finally
            {
                RenderTexture.active = null;
                texture.Release();
                Object.DestroyImmediate(texture);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static void DrawSheet(MapNotebook notebook, MapMemory memory, MapHere here)
        {
            notebook.TakeSheet(memory, here);
            notebook.StartDrawing(memory, 40.0, 20.0);
            for (int i = 0; i < 1000 && notebook.Drawing; i++) notebook.Step(memory, 0.1f);
        }

        /// <summary>Dos salas con pasillo en el chunk (0,0) y una sala con columna en (1,0); vistas hace poco.</summary>
        private static MapMemory SyntheticMemory()
        {
            var memory = new MapMemory(0.5f, 50f, 3.32f, 60f, 0.5f, 20000);
            var walls = new HashSet<long>();
            var floors = new List<(int x, int z)>();
            void Room(int x0, int z0, int x1, int z1)
            {
                for (int x = x0; x < x1; x++)
                    for (int z = z0; z < z1; z++)
                        floors.Add((x, z));
            }

            Room(8, 8, 42, 42);
            Room(42, 20, 72, 26);
            Room(72, 6, 96, 46);
            Room(96, 22, 104, 28);
            Room(104, 10, 150, 50);
            var floorSet = new HashSet<long>();
            foreach (var f in floors) floorSet.Add(Key(f.x, f.z));
            foreach (var (x, z) in new[] { (125, 28), (126, 28), (125, 29), (126, 29), (25, 25), (26, 25) })
                floorSet.Remove(Key(x, z));

            var xs = new List<int>();
            var zs = new List<int>();
            var kinds = new List<MapCellKind>();
            foreach (var f in floors)
            {
                if (!floorSet.Contains(Key(f.x, f.z))) continue;
                xs.Add(f.x); zs.Add(f.z); kinds.Add(MapCellKind.Floor);
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int wx = f.x + dx, wz = f.z + dz;
                    if (floorSet.Contains(Key(wx, wz)) || !walls.Add(Key(wx, wz))) continue;
                    xs.Add(wx); zs.Add(wz); kinds.Add(MapCellKind.Wall);
                }
            }

            memory.AddSample(39.0, 0, xs.ToArray(), zs.ToArray(), kinds.ToArray(), xs.Count);
            // Una muestra con la celda del jugador a cada lado del borde X = 100: la flecha este/oeste.
            memory.AddSample(39.5, 0, 99, 25, new int[0], new int[0], new MapCellKind[0], 0);
            memory.AddSample(40.0, 0, 101, 25, new int[0], new int[0], new MapCellKind[0], 0);
            return memory;
        }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        private static Bounds PageBounds(List<Canvas> canvases, out Vector3 facing)
        {
            var corners = new Vector3[4];
            var bounds = new Bounds();
            bool any = false;
            facing = Vector3.forward;
            foreach (Canvas canvas in canvases)
            {
                if (!canvas.isRootCanvas) continue;
                var rect = (RectTransform)canvas.transform;
                rect.GetWorldCorners(corners);
                foreach (Vector3 corner in corners)
                {
                    if (!any) { bounds = new Bounds(corner, Vector3.zero); any = true; }
                    else bounds.Encapsulate(corner);
                }

                // La cara visible de un Canvas mira a −forward: la cámara se pone en ese lado, mirando a +forward.
                facing = rect.forward;
            }

            return bounds;
        }

        private static Bounds SheetBounds(MapNotebookBookTab tab)
        {
            Transform sheet = FindDeep(tab.transform.root, "SheetArea");
            if (sheet == null) return new Bounds();
            var corners = new Vector3[4];
            ((RectTransform)sheet).GetWorldCorners(corners);
            var bounds = new Bounds(corners[0], Vector3.zero);
            for (int i = 1; i < 4; i++) bounds.Encapsulate(corners[i]);
            return bounds;
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (child.name == name) return child;
            return null;
        }

        private static string Shot(Camera camera, RenderTexture texture, string name)
        {
            Canvas.ForceUpdateCanvases();
            camera.Render();
            RenderTexture.active = texture;
            var image = new Texture2D(Width, Height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            image.Apply();
            string path = Path.Combine(OutDir, name + ".png");
            File.WriteAllBytes(path, image.EncodeToPNG());
            Object.DestroyImmediate(image);
            return path;
        }
    }
}
#endif
