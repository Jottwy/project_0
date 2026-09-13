#if UNITY_EDITOR
using System.Collections.Generic;
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

        /// <summary>
        /// Escribe en el log con qué se está pintando de verdad el mundo de la escena de playtest:
        /// si el binder está enlazado, qué tiene <c>Wg3TestWorld.materials</c> en memoria y qué
        /// material y shader lleva cada ranura de cada renderer. Salida ordenada.
        /// </summary>
        [MenuItem("Backrooms/Mapeado/Diagnosticar pintura de playtest")]
        public static void Diagnose()
        {
            Scene scene = SceneManager.GetSceneByPath(TargetPath);
            bool opened = false;
            if (!scene.isLoaded)
            {
                scene = EditorSceneManager.OpenScene(TargetPath, OpenSceneMode.Additive);
                opened = true;
            }

            try
            {
                Wg3TestWorld world = null;
                MappingPlaytestMaterials paint = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (world == null) world = root.GetComponentInChildren<Wg3TestWorld>(true);
                    if (paint == null) paint = root.GetComponentInChildren<MappingPlaytestMaterials>(true);
                }

                var sb = new System.Text.StringBuilder("[MappingPlaytest] DIAG");
                sb.Append($" escena_abierta_antes={!opened} world={world != null} binder={paint != null}");
                sb.Append($" binder_enabled={(paint != null && paint.isActiveAndEnabled)}");
                sb.Append($" binder.world={(paint != null && paint.world != null)} binder.world_es_este={(paint != null && paint.world == world)}");
                if (paint != null)
                    sb.Append($" binder.floor={Describe(paint.floor)}");

                if (world != null)
                {
                    Wg3Materials m = world.materials;
                    sb.Append($"\n  world.materials: {(m == null ? "NULL" : $"floor={Describe(m.floor)} structure={Describe(m.structure)} ceiling={Describe(m.ceiling)} decoration={Describe(m.decoration)}")}");
                    sb.Append($"\n  mundo generado={world.World != null} piezas={(world.World != null ? world.World.placements.Count : 0)}");

                    var counts = new SortedDictionary<string, int>();
                    Renderer[] renderers = world.GetComponentsInChildren<Renderer>(true);
                    int slotMismatch = 0;
                    foreach (Renderer r in renderers)
                    {
                        var filter = r.GetComponent<MeshFilter>();
                        if (filter != null && filter.sharedMesh != null && filter.sharedMesh.subMeshCount != r.sharedMaterials.Length)
                            slotMismatch++;
                        foreach (Material mat in r.sharedMaterials)
                        {
                            string key = $"{r.GetType().Name}:{Describe(mat)}";
                            counts.TryGetValue(key, out int n);
                            counts[key] = n + 1;
                        }
                    }

                    sb.Append($"\n  renderers={renderers.Length} submallas_distintas_de_materiales={slotMismatch}");
                    foreach (KeyValuePair<string, int> entry in counts)
                        sb.Append($"\n    {entry.Value,5} × {entry.Key}");
                }

                Debug.Log(sb.ToString());
            }
            finally
            {
                if (opened) EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static string Describe(Material mat) =>
            mat == null ? "NULL" : $"{mat.name}<{(mat.shader != null ? mat.shader.name : "sin shader")}>";

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
                // P0.2: la libreta del prototipo. Con ella abierta se desactiva el jugador de prueba.
                var notebook = go.AddComponent<MapNotebookView>();
                notebook.sampler = sampler;
                notebook.playerControl = player.GetComponent<Wg3TestPlayer>();

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
