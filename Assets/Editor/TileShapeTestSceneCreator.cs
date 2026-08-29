#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using BackroomsSurvival.Gameplay.GridWorld;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Crea Assets/Scenes/TileShapeTest.unity: un 3×3 de losas de suelo de 5 m
    /// (15 × 15 m en total) en blanco plano y SIN NINGUNA LUZ, para juzgar formas
    /// de sala y modelos de pared sin que la iluminación ni las texturas opinen.
    ///
    /// Escena aislada a propósito: ni backend, ni IPC, ni streaming, ni jugador.
    /// No comparte nada con GridRenderTest — esa sí levanta el renderer real.
    ///
    /// POR QUÉ UNLIT: "blanco" y "sin luz" son incompatibles con un shader Lit.
    /// Sin luces y con la ambiental a negro, un URP/Lit blanco se ve NEGRO. El
    /// material de esta escena usa Universal Render Pipeline/Unlit, que emite su
    /// color base tal cual y no mira ninguna luz — por eso la escena puede quedar
    /// literalmente sin un solo Light y aun así verse.
    ///
    /// POR QUÉ HAY JUNTA: en blanco plano y sin sombras, 9 losas pegadas se leen
    /// como UN cuadrado de 15 m. La junta de <see cref="Gap"/> deja ver la
    /// retícula. Los CENTROS siguen en la retícula real de 5 m (paso
    /// <see cref="GridVisualConstants.TileSize"/>): solo encoge la losa, no la
    /// separa, así que las medidas que midas aquí son las del juego.
    /// </summary>
    public static class TileShapeTestSceneCreator
    {
        private const string ScenePath = "Assets/Scenes/TileShapeTest.unity";
        private const string MatFolder = "Assets/Materials/Debug";
        private const string MatPath = MatFolder + "/UnlitWhite.mat";

        /// Losas por lado. 3 → el 3×3 de 15 × 15 m pedido.
        private const int TilesPerSide = 3;

        /// Paso entre centros de losa: la retícula real de render (5 m).
        private const float Step = GridVisualConstants.TileSize;

        /// Junta visible entre losas. Solo encoge la losa; el paso no cambia.
        private const float Gap = 0.06f;

        /// Grosor de la losa. Cosmético — da canto que leer desde un ángulo bajo.
        private const float Slab = 0.2f;

        [MenuItem("Backrooms/Create Tile Shape Test Scene")]
        public static void CreateScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var mat = CreateOrLoadUnlitWhite();
            if (mat == null)
                return;

            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Sin luces, y la ambiental a negro para que nada añada relleno: con
            // Unlit da igual para las losas, pero cualquier prefab LIT que sueltes
            // aquí después debe salir negro, no medio iluminado por accidente. Esa
            // es justo la señal de "este modelo no es unlit".
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.black;
            RenderSettings.fog = false;
            // Sin skybox: si no, la cúpula por defecto ilumina y además tiñe el fondo.
            RenderSettings.skybox = null;

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.farClipPlane = 200f;
            // Encima del bloque de 15 × 15 m centrado en el origen, mirándolo en picado.
            camGo.transform.position = new Vector3(0f, 16f, -16f);
            camGo.transform.rotation = Quaternion.Euler(40f, 0f, 0f);

            var root = new GameObject("Floor 3x3");

            // El bloque se centra en el origen: con 3 losas, los centros caen en
            // −5, 0, +5. Así puedes soltar un prefab en (0,0,0) y estar en el
            // centro de la losa del medio, no en una esquina.
            float half = (TilesPerSide - 1) * 0.5f;
            for (int tx = 0; tx < TilesPerSide; tx++)
            {
                for (int tz = 0; tz < TilesPerSide; tz++)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    go.name = $"Floor_{tx}_{tz}";
                    go.transform.SetParent(root.transform, false);
                    go.transform.localPosition = new Vector3(
                        (tx - half) * Step, -Slab * 0.5f, (tz - half) * Step);
                    go.transform.localScale = new Vector3(Step - Gap, Slab, Step - Gap);
                    go.GetComponent<MeshRenderer>().sharedMaterial = mat;
                }
            }

            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[TileShapeTestSceneCreator] {TilesPerSide}x{TilesPerSide} losas de " +
                      $"{Step} m ({TilesPerSide * Step} m de lado), sin luces. Escena: {ScenePath}");
        }

        /// <summary>
        /// Material Unlit blanco, creado una sola vez y reutilizado. Se guarda como
        /// asset (no material de instancia) para que puedas duplicarlo y probar
        /// tonos/texturas sin volver a generar la escena.
        /// </summary>
        private static Material CreateOrLoadUnlitWhite()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (existing != null)
                return existing;

            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
            {
                // Sin este shader la escena entera sale negra, que es exactamente lo
                // que la escena quiere evitar. Fallar aquí y decir por qué es mejor
                // que crear un material rosa y dejar al autor adivinando.
                Debug.LogError("[TileShapeTestSceneCreator] No se encuentra el shader " +
                               "'Universal Render Pipeline/Unlit'. ¿Falta el paquete URP?");
                return null;
            }

            var mat = new Material(shader) { name = "UnlitWhite" };
            mat.SetColor("_BaseColor", Color.white);

            if (!AssetDatabase.IsValidFolder("Assets/Materials"))
                AssetDatabase.CreateFolder("Assets", "Materials");
            if (!AssetDatabase.IsValidFolder(MatFolder))
                AssetDatabase.CreateFolder("Assets/Materials", "Debug");
            AssetDatabase.CreateAsset(mat, MatPath);
            AssetDatabase.SaveAssets();
            return mat;
        }
    }
}
#endif
