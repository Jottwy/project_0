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

            Scene scene = EditorSceneManager.OpenScene(TargetPath, OpenSceneMode.Additive);
            try
            {
                Transform player = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root.name == RootName)
                    {
                        Object.DestroyImmediate(root);
                        continue;
                    }

                    var testPlayer = root.GetComponentInChildren<Wg3TestPlayer>(true);
                    if (testPlayer != null) player = testPlayer.transform;
                }

                if (player == null)
                {
                    Debug.LogError($"[MappingPlaytest] {TargetPath} no tiene Wg3TestPlayer: no hay a quién seguir.");
                    return;
                }

                var go = new GameObject(RootName);
                SceneManager.MoveGameObjectToScene(go, scene);
                var sampler = go.AddComponent<MapMemorySampler>();
                sampler.target = player;
                var view = go.AddComponent<MapMemoryDebugView>();
                view.sampler = sampler;

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
                EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
#endif
