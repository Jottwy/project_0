using System.IO;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Fase 3 test harness — NO IPC. Loads the binary chunks exported by the
    /// Rust viewer (backend/tools/grid_viewer --mode export) from
    /// StreamingAssets/grid_test and builds them with GridChunkBuilder, so the
    /// 3D render shows the exact grid validated in 2D during Fase 2.
    ///
    /// Replaced by the real IPC path in Fase 4; the byte parser carries over.
    /// </summary>
    public sealed class GridTestWorld : MonoBehaviour
    {
        [Header("Exported grid (must match the grid_viewer export run)")]
        public long seed = 42;
        public string folder = "grid_test";
        public int chunkRadius = 1; // 3×3 export

        [Header("View")]
        [Tooltip("-1 = all 4 layers stacked; 0..3 = only that layer")]
        public int onlyLayer = -1;
        public int layerCount = 4;

        private void Start()
        {
            var prefabs = GridPrefabSet.LoadFromResources();
            if (prefabs.floor == null)
                return;

            string dir = Path.Combine(Application.streamingAssetsPath, folder);
            int built = 0;
            int missing = 0;

            for (int cz = -chunkRadius; cz <= chunkRadius; cz++)
            {
                for (int cx = -chunkRadius; cx <= chunkRadius; cx++)
                {
                    // Load every layer of this chunk up front: layer N's build
                    // needs layer N+1's cells to open ceilings under Hollow
                    // tiles (vertical shaft connection), even when onlyLayer
                    // filters what gets built.
                    var layers = new GridCell[layerCount][];
                    for (int layer = 0; layer < layerCount; layer++)
                    {
                        string path = Path.Combine(dir,
                            $"seed{seed}_layer{layer}_chunk{cx}_{cz}.bytes");
                        if (File.Exists(path))
                            layers[layer] = GridChunkData.FromBytes(File.ReadAllBytes(path));
                    }

                    for (int layer = 0; layer < layerCount; layer++)
                    {
                        if (onlyLayer >= 0 && layer != onlyLayer)
                            continue;
                        if (layers[layer] == null)
                        {
                            missing++;
                            continue;
                        }

                        float side = GridConstants.ChunkCells * GridConstants.CellSize;
                        // Stack layers LayerHeight (4 m, ADR-007) apart, flush
                        // with the fixed 4 m room height: layer N's ceiling
                        // plane IS layer N+1's floor plane.
                        var origin = new Vector3(
                            cx * side,
                            layer * GridConstants.LayerHeight,
                            cz * side);
                        var layerAbove = layer + 1 < layerCount ? layers[layer + 1] : null;
                        var go = GridChunkBuilder.Build(layers[layer], prefabs, origin,
                            $"GridChunk_L{layer}_{cx}_{cz}", layerAbove);
                        go.transform.SetParent(transform, true);
                        built++;
                    }
                }
            }

            if (missing > 0)
                Debug.LogWarning($"[GridTestWorld] {missing} chunk files missing in {dir}. " +
                                 "From backend/tools/grid_viewer run: cargo run --release -- " +
                                 $"{seed} --mode export --out ../../../Assets/StreamingAssets/{folder}");
            Debug.Log($"[GridTestWorld] built {built} grid chunks from {dir}");
        }
    }
}
