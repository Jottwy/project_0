#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using BackroomsSurvival.Gameplay.GridWorld;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Creates Assets/Scenes/GridRenderTest.unity: camera + warm light + a
    /// GridTestWorld that builds the exported Fase 2 grid on Play. No IPC,
    /// no network, no player — visual validation of the Fase 3 renderer only.
    /// </summary>
    public static class GridTestSceneCreator
    {
        private const string ScenePath = "Assets/Scenes/GridRenderTest.unity";

        [MenuItem("Backrooms/Create Grid Render Test Scene")]
        public static void CreateScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Sickly warm light + yellowish fog (§8b is future work; this is
            // just enough to read the geometry).
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.84f);
            light.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(55f, -35f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.40f, 0.32f);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            var cam = camGo.AddComponent<Camera>();
            cam.farClipPlane = 500f;
            // Above the 3×3 chunk block (150×150 m), looking into it.
            camGo.transform.position = new Vector3(0f, 70f, -110f);
            camGo.transform.rotation = Quaternion.Euler(35f, 0f, 0f);

            var worldGo = new GameObject("GridTestWorld");
            worldGo.AddComponent<GridTestWorld>();

            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[GridTestSceneCreator] Scene saved to {ScenePath}");
        }
    }
}
#endif
