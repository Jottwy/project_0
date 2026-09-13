#if UNITY_EDITOR
using BackroomsSurvival.Gameplay.Mapping;
using BackroomsSurvival.WorldGen3;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// P0.1 de MAPPING-PROTOTYPE — crea <c>Assets/Scenes/MappingPlaytest.unity</c>: la escena
    /// aislada de WG3 (<c>WorldGen3Test.unity</c>, sin backend ni red) más un objeto
    /// <c>MapMemory</c> con el muestreador y el minimapa de depuración enganchados al jugador de prueba.
    /// "Backrooms ▸ Mapeado ▸ Crear escena de playtest".
    /// </summary>
    /// <remarks>
    /// **Copia, no edita, la escena de WG3**, y la abre ADITIVA: la escena que tenga abierta quien
    /// trabaja en el editor ni se cierra ni se toca. Relanzarlo rehace el objeto <c>MapMemory</c>
    /// sobre la copia existente.
    /// </remarks>
    public static class MappingPlaytestSceneCreator
    {
        private const string SourcePath = "Assets/Scenes/WorldGen3Test.unity";
        private const string TargetPath = "Assets/Scenes/MappingPlaytest.unity";
        private const string RootName = "MapMemory";
        private const string MaterialsFolder = "Assets/Materials/WorldGen3";

        private static Material LoadMaterial(string name) =>
            AssetDatabase.LoadAssetAtPath<Material>($"{MaterialsFolder}/{name}.mat");

        [MenuItem("Backrooms/Mapeado/Crear escena de playtest")]
        public static void Create()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[MappingPlaytest] Sal de Play antes de crear la escena.");
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TargetPath) == null &&
                !AssetDatabase.CopyAsset(SourcePath, TargetPath))
            {
                Debug.LogError($"[MappingPlaytest] No se pudo copiar {SourcePath} a {TargetPath}.");
                return;
            }

            // Si ya está abierta (quien la prueba la tiene delante), se edita en el sitio y NO se cierra:
            // cerrar la única escena cargada no está permitido.
            bool wasOpen = SceneManager.GetSceneByPath(TargetPath).isLoaded;
            Scene scene = EditorSceneManager.OpenScene(TargetPath, OpenSceneMode.Additive);
            try
            {
                Transform player = null;
                Wg3TestWorld world = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root.name == RootName)
                    {
                        Object.DestroyImmediate(root);
                        continue;
                    }

                    var testPlayer = root.GetComponentInChildren<Wg3TestPlayer>(true);
                    if (testPlayer != null) player = testPlayer.transform;
                    var testWorld = root.GetComponentInChildren<Wg3TestWorld>(true);
                    if (testWorld != null) world = testWorld;
                }

                if (player == null || world == null)
                {
                    Debug.LogError($"[MappingPlaytest] {TargetPath} necesita Wg3TestPlayer y Wg3TestWorld.");
                    return;
                }

                var go = new GameObject(RootName);
                SceneManager.MoveGameObjectToScene(go, scene);
                var sampler = go.AddComponent<MapMemorySampler>();
                sampler.target = player;
                var view = go.AddComponent<MapMemoryDebugView>();
                view.sampler = sampler;

                // Wg3Materials no es [Serializable]: asignarlo aquí a Wg3TestWorld se perdería al
                // guardar y el mundo saldría magenta. Las referencias van en MappingPlaytestMaterials,
                // con los mismos cuatro materiales que cablea el prefab GridTestWorld del juego real.
                var paint = go.AddComponent<MappingPlaytestMaterials>();
                paint.world = world;
                paint.floor = LoadMaterial("Wg3_Floor");
                paint.structure = LoadMaterial("Wg3_Structure");
                paint.ceiling = LoadMaterial("Wg3_Ceiling");
                paint.decoration = LoadMaterial("Wg3_Trim");
                if (paint.floor == null || paint.structure == null || paint.ceiling == null || paint.decoration == null)
                {
                    Debug.LogError($"[MappingPlaytest] Falta algún material en {MaterialsFolder}.");
                    return;
                }

                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene))
                {
                    Debug.LogError($"[MappingPlaytest] No se pudo guardar {TargetPath}.");
                    return;
                }

                Debug.Log($"[MappingPlaytest] Escena lista: {TargetPath} (sigue a '{player.name}').");
            }
            finally
            {
                if (!wasOpen) EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
#endif
