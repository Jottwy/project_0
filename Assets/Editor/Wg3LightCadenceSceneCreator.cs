#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using BackroomsSurvival.WorldGen3;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Crea <c>Assets/Scenes/WorldGen3LightCadence.unity</c>: los tres mundos del arnés del ritmo
    /// de luces, uno por semilla.
    ///
    /// Mismo aislamiento que la escena de prueba (REGLA R9): sin backend, sin red, sin sesión. Y
    /// mismos materiales de asset que aquélla, por el mismo motivo — un material de instancia no
    /// sobrevive al guardado y reabrir la escena la dejaría en magenta, que se lee como problema de
    /// shader cuando sería de ciclo de vida.
    /// </summary>
    public static class Wg3LightCadenceSceneCreator
    {
        private const string ScenePath = "Assets/Scenes/WorldGen3LightCadence.unity";
        private const string MatFolder = "Assets/Materials/WorldGen3";

        [MenuItem("Backrooms/WorldGen3/Crear escena de cadencia de luces")]
        public static void CreateScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var materials = new Wg3Materials
            {
                floor = Mat("Wg3_Floor"),
                structure = Mat("Wg3_Structure"),
                ceiling = Mat("Wg3_Ceiling"),
                decoration = Mat("Wg3_Trim")
            };
            if (materials.floor == null)
            {
                Debug.LogWarning("[WG3] faltan los materiales de WorldGen3; lanza antes " +
                                 "«Crear escena de prueba», que es quien los crea como asset.");
                return;
            }

            UnityEngine.SceneManagement.Scene scene =
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Ambiental muy baja: con ambiental generosa un plafón fundido y uno encendido se
            // parecen, y el ritmo —que es lo que esta escena existe para mirar— desaparece.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.10f, 0.10f, 0.095f);
            RenderSettings.fog = false;
            RenderSettings.skybox = null;

            var rigGo = new GameObject("Wg3LightCadence");
            var rig = rigGo.AddComponent<Wg3LightCadenceRig>();
            rig.materials = materials;
            rig.seeds = new[] { 42, 1337, 90210 };
            rig.Build();

            var playerGo = new GameObject("Player");
            var controller = playerGo.AddComponent<CharacterController>();
            controller.height = 1.75f;
            controller.radius = 0.3f;
            controller.center = new Vector3(0f, 0.875f, 0f);
            controller.stepOffset = 0.32f;

            var player = playerGo.AddComponent<Wg3TestPlayer>();

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

            playerGo.transform.position = rig.SpawnPoint;

            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);

            Debug.Log($"[WG3] escena de cadencia creada en {ScenePath}: semillas " +
                      $"{string.Join(", ", rig.seeds)}, separadas {rig.spacingMeters} m en X. " +
                      "Los tres recuentos por semilla salen en la consola al construir. OJO: R y T " +
                      "regeneran UN solo mundo y lo devuelven al origen (el ensamblador coloca en " +
                      "coordenadas absolutas); para rehacer los tres, «Construir» en el menú " +
                      "contextual del arnés.");
        }

        [MenuItem("Backrooms/WorldGen3/Abrir escena de cadencia de luces")]
        public static void OpenScene()
        {
            if (!System.IO.File.Exists(ScenePath))
            {
                Debug.LogWarning($"[WG3] no existe {ScenePath}; usa «Crear escena de cadencia de luces».");
                return;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        /// <summary>Los materiales YA creados por <see cref="Wg3TestSceneCreator"/>. No se crean
        /// aquí a propósito: dos sitios creando el mismo asset con valores distintos es cómo se
        /// acaba con dos escenas que no se parecen y nadie sabe por qué.</summary>
        private static Material Mat(string name) =>
            AssetDatabase.LoadAssetAtPath<Material>($"{MatFolder}/{name}.mat");
    }
}
#endif
