#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using BackroomsSurvival.WorldGen3;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Crea <c>Assets/Scenes/WorldGen3Test.unity</c>: la escena aislada de la REGLA R9.
    ///
    /// Sin backend, sin IPC, sin red, sin streaming y sin sesión. Solo el compositor, la geometría
    /// y un jugador que anda. Si algo falla aquí, no hay media docena de sistemas que descartar
    /// antes de mirar la pieza.
    ///
    /// Los materiales se guardan como ASSETS y no se crean en memoria: un material de instancia
    /// sobrevive a la sesión pero no al guardado, así que reabrir la escena la dejaría entera en
    /// magenta y parecería un problema de shader cuando sería de ciclo de vida.
    /// </summary>
    public static class Wg3TestSceneCreator
    {
        private const string ScenePath = "Assets/Scenes/WorldGen3Test.unity";
        private const string MatFolder = "Assets/Materials/WorldGen3";

        [MenuItem("Backrooms/WorldGen3/Crear escena de prueba")]
        public static void CreateScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var materials = new Wg3Materials
            {
                // Paleta de las referencias: moqueta verde oscura, paredes hueso, techo un punto
                // más apagado que la pared y el rodapié un punto más claro. El contraste entre
                // pared y rodapié es lo que hace que la junta se lea como carpintería y no como
                // borde de polígono.
                floor = Mat("Wg3_Floor", new Color(0.21f, 0.25f, 0.19f), 0.06f),
                structure = Mat("Wg3_Structure", new Color(0.86f, 0.86f, 0.82f), 0.10f),
                ceiling = Mat("Wg3_Ceiling", new Color(0.80f, 0.80f, 0.77f), 0.04f),
                decoration = Mat("Wg3_Trim", new Color(0.95f, 0.95f, 0.93f), 0.22f)
            };
            if (materials.floor == null) return;

            UnityEngine.SceneManagement.Scene scene =
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Ambiental muy baja y plana: en las referencias no hay más luz que los plafones, y con
            // ambiental generosa los pasillos pierden la caída de luz que los hace largos.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.10f, 0.10f, 0.095f);
            RenderSettings.fog = false;
            RenderSettings.skybox = null;

            var worldGo = new GameObject("WorldGen3");
            var world = worldGo.AddComponent<Wg3TestWorld>();
            world.worldSeed = 42;
            world.materials = materials;
            world.Generate();

            var playerGo = new GameObject("Player");
            var controller = playerGo.AddComponent<CharacterController>();
            controller.height = 1.75f;
            controller.radius = 0.3f;
            controller.center = new Vector3(0f, 0.875f, 0f);
            controller.stepOffset = 0.32f;

            var player = playerGo.AddComponent<Wg3TestPlayer>();
            player.world = world;

            var eyeGo = new GameObject("Eye");
            eyeGo.transform.SetParent(playerGo.transform, false);
            eyeGo.transform.localPosition = new Vector3(0f, 0.72f, 0f);
            var cam = eyeGo.AddComponent<Camera>();
            cam.tag = "MainCamera";
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 300f;
            cam.fieldOfView = 70f;

            playerGo.transform.position = world.SpawnPoint;

            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);

            Debug.Log($"[WG3] escena creada en {ScenePath}. Entra a Play: WASD para andar, " +
                      $"R para otra semilla. El criterio de cierre de F0 es chocar con una columna " +
                      $"interior y NO chocar con el rodapié.");
        }

        /// <summary>Abre la escena de prueba sin volver a crearla. Separado de
        /// <see cref="CreateScene"/> a propósito: crear machaca el fichero, y perder de vista la
        /// escena no es motivo para regenerarla desde cero.</summary>
        [MenuItem("Backrooms/WorldGen3/Abrir escena de prueba")]
        public static void OpenScene()
        {
            if (!System.IO.File.Exists(ScenePath))
            {
                Debug.LogWarning($"[WG3] no existe {ScenePath}; usa «Crear escena de prueba».");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Debug.Log($"[WG3] escena abierta: {ScenePath}");
        }

        [MenuItem("Backrooms/WorldGen3/Regenerar mundo de la escena")]
        public static void RegenerateOpenScene()
        {
            var world = Object.FindFirstObjectByType<Wg3TestWorld>();
            if (world == null)
            {
                Debug.LogWarning("[WG3] no hay ningún Wg3TestWorld en la escena abierta.");
                return;
            }
            world.Reseed();
            EditorSceneManager.MarkSceneDirty(world.gameObject.scene);
        }

        // ── planta ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Vuelca una planta del mundo generado a PNG.
        ///
        /// Existe porque mirar la planta es la ÚNICA forma de ver lo que ningún test puede
        /// afirmar: si el mundo respira o se ahoga, si el campo de escala está haciendo algo, y si
        /// las piezas se leen como arquitectura o como cajas pegadas. Un test dice que no hay
        /// solapes; solo el ojo dice si la planta parece un edificio.
        ///
        /// SE RASTERIZA A MANO, sin cámara. El primer intento renderizaba con una ortográfica
        /// cenital y salía TODO MAGENTA: `Camera.Render()` a mano cae al camino de render heredado,
        /// y un shader de URP no tiene subshader ahí, así que sale el shader de error. Rasterizar
        /// no solo lo evita — también quita la dependencia del pipeline, de los materiales y de la
        /// iluminación, y permite colorear por CLASE DE ESCALA, que es justo el dato que se quiere
        /// mirar y que un render fiel no enseña.
        /// </summary>
        [MenuItem("Backrooms/WorldGen3/Volcar planta a PNG")]
        public static void DumpPlanToPng()
        {
            var world = Object.FindFirstObjectByType<Wg3TestWorld>();
            if (world == null)
            {
                Debug.LogWarning("[WG3] no hay ningún Wg3TestWorld en la escena abierta.");
                return;
            }

            // El mundo compuesto no se serializa, así que tras cada recarga de dominio vuelve a
            // null hasta que corre `Start`. Volcar justo después de recompilar caía siempre en esa
            // ventana; generar aquí la cierra sin depender del orden de arranque del editor.
            if (world.World == null || world.World.placements.Count == 0) world.Generate();
            Wg3World composed = world.World;
            if (composed == null || composed.placements.Count == 0)
            {
                Debug.LogWarning("[WG3] el mundo salió vacío; mira si el catálogo validó.");
                return;
            }

            const int Size = 1600;
            const float Pad = 5f;

            Bounds bounds = composed.FootprintBounds();
            float span = Mathf.Max(bounds.size.x, bounds.size.z) + Pad * 2f;
            float scale = Size / span;
            float minX = bounds.center.x - span * 0.5f;
            float minZ = bounds.center.z - span * 0.5f;

            var pixels = new Color32[Size * Size];
            var background = new Color32(14, 16, 14, 255);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = background;

            // Suelo por clase de escala: es lo que hace visible de un vistazo si el campo está
            // agrupando lo estrecho con lo estrecho o repartiendo al azar.
            var floorByScale = new[]
            {
                new Color32(56, 68, 54, 255),   // estrecha
                new Color32(72, 88, 68, 255),   // media
                new Color32(92, 110, 84, 255),  // grande
                new Color32(104, 102, 50, 255)  // rara
            };
            var boneColour = new Color32(226, 226, 214, 255);
            var stepColour = new Color32(180, 186, 168, 255);
            var forcedCapColour = new Color32(214, 88, 62, 255);
            var chosenCapColour = new Color32(150, 128, 200, 255);
            var socketColour = new Color32(195, 214, 52, 255);

            foreach (Wg3Placement p in composed.placements)
                FillRect(pixels, Size, minX, minZ, scale,
                    p.originX, p.originZ, p.SizeX, p.SizeZ, floorByScale[(int)p.piece.scale]);

            foreach (Wg3Placement p in composed.placements)
                foreach (Wg3Volume v in Wg3Geometry.BuildPlaced(p))
                {
                    if (v.kind == Wg3VolumeKind.Floor || v.kind == Wg3VolumeKind.Ceiling) continue;
                    if (v.kind == Wg3VolumeKind.Decoration) continue;
                    FillVolume(pixels, Size, minX, minZ, scale, v,
                        v.kind == Wg3VolumeKind.Step ? stepColour : boneColour);
                }

            foreach (Wg3Placement p in composed.placements)
                for (int s = 0; s < p.socketState.Length; s++)
                {
                    if (p.socketState[s] != Wg3World.SocketConnected) continue;
                    Vector2 q = p.WorldPoint(s);
                    FillDisc(pixels, Size, minX, minZ, scale, q.x, q.y, 0.55f, socketColour);
                }

            foreach (Wg3Cap c in composed.caps)
                FillDisc(pixels, Size, minX, minZ, scale, c.point.x, c.point.y, 0.75f,
                    c.forced ? forcedCapColour : chosenCapColour);

            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(pixels);
                tex.Apply(false);
                string path = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(), $"wg3_plan_{world.worldSeed}.png");
                System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
                Debug.Log($"[WG3] planta de la semilla {world.worldSeed} en {path} — " +
                          $"{composed.placements.Count} piezas, {composed.caps.Count} tapones.");
            }
            finally { Object.DestroyImmediate(tex); }
        }

        private static void FillRect(Color32[] px, int size, float minX, float minZ, float scale,
            float x, float z, float w, float d, Color32 colour)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt((x - minX) * scale), 0, size - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((x + w - minX) * scale), 0, size - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((z - minZ) * scale), 0, size - 1);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((z + d - minZ) * scale), 0, size - 1);
            for (int pz = z0; pz <= z1; pz++)
                for (int pxi = x0; pxi <= x1; pxi++)
                    px[pz * size + pxi] = colour;
        }

        /// <summary>Rasteriza una caja con giro probando cada píxel de su AABB contra la caja en
        /// su propio sistema. A 1600² y unos cientos de volúmenes sobra de rápido, y evita tener
        /// que escribir un rellenado de polígono para lo único que gira: columnas y escalones.</summary>
        private static void FillVolume(Color32[] px, int size, float minX, float minZ, float scale,
            in Wg3Volume v, Color32 colour)
        {
            float rad = v.yawDegrees * Mathf.Deg2Rad;
            float c = Mathf.Abs(Mathf.Cos(rad)), s = Mathf.Abs(Mathf.Sin(rad));
            float extX = (v.size.x * c + v.size.z * s) * 0.5f;
            float extZ = (v.size.x * s + v.size.z * c) * 0.5f;

            int x0 = Mathf.Clamp(Mathf.FloorToInt((v.center.x - extX - minX) * scale), 0, size - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((v.center.x + extX - minX) * scale), 0, size - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((v.center.z - extZ - minZ) * scale), 0, size - 1);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((v.center.z + extZ - minZ) * scale), 0, size - 1);

            float cos = Mathf.Cos(-rad), sin = Mathf.Sin(-rad);
            float halfX = v.size.x * 0.5f, halfZ = v.size.z * 0.5f;

            for (int pz = z0; pz <= z1; pz++)
                for (int pxi = x0; pxi <= x1; pxi++)
                {
                    float wx = minX + (pxi + 0.5f) / scale - v.center.x;
                    float wz = minZ + (pz + 0.5f) / scale - v.center.z;
                    float lx = wx * cos - wz * sin;
                    float lz = wx * sin + wz * cos;
                    if (Mathf.Abs(lx) <= halfX && Mathf.Abs(lz) <= halfZ)
                        px[pz * size + pxi] = colour;
                }
        }

        private static void FillDisc(Color32[] px, int size, float minX, float minZ, float scale,
            float x, float z, float radius, Color32 colour)
        {
            int r = Mathf.Max(1, Mathf.RoundToInt(radius * scale));
            int cx = Mathf.RoundToInt((x - minX) * scale);
            int cz = Mathf.RoundToInt((z - minZ) * scale);
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r * r) continue;
                    int pxi = cx + dx, pz = cz + dz;
                    if (pxi < 0 || pxi >= size || pz < 0 || pz >= size) continue;
                    px[pz * size + pxi] = colour;
                }
        }

        private static Material Mat(string name, Color color, float smoothness)
        {
            string path = $"{MatFolder}/{name}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return existing;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                // Desde ADR-065 el render ES URP; si este shader falta, el problema es el paquete,
                // no la escena, y crear un material Standard aquí lo taparía con un magenta que se
                // leería como error de conversión.
                Debug.LogError("[WG3] no se encuentra el shader URP/Lit. ¿Falta el paquete URP?");
                return null;
            }

            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
                AssetDatabase.CreateFolder("Assets", "Materials");
            if (!AssetDatabase.IsValidFolder(MatFolder))
                AssetDatabase.CreateFolder("Assets/Materials", "WorldGen3");

            var mat = new Material(shader);
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Metallic", 0f);
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }
    }
}
#endif
