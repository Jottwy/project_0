using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// The 5 primitive prefabs (§6) plus the wall material, loaded from
    /// Resources/GridPrefabs as created by Backrooms/Create Grid Prefabs.
    /// </summary>
    public sealed class GridPrefabSet
    {
        public GameObject floor;
        public GameObject ceiling;
        public GameObject pillar;
        public GameObject voidEdge;
        public Material wallMaterial;
        public Material ceilingMaterial;

        public static GridPrefabSet LoadFromResources()
        {
            var set = new GridPrefabSet
            {
                floor = Resources.Load<GameObject>("GridPrefabs/Floor"),
                ceiling = Resources.Load<GameObject>("GridPrefabs/Ceiling"),
                pillar = Resources.Load<GameObject>("GridPrefabs/Pillar"),
                voidEdge = Resources.Load<GameObject>("GridPrefabs/VoidEdge"),
                wallMaterial = Resources.Load<Material>("GridMaterials/GridWall"),
                ceilingMaterial = Resources.Load<Material>("GridMaterials/GridCeiling"),
            };
            if (set.floor == null)
                Debug.LogError("[GridPrefabSet] GridPrefabs not found in Resources. " +
                               "Run Backrooms/Create Grid Prefabs first.");

            // Wall side faces are single-sided geometry, and the wall top caps
            // are single quads painted with the ceiling material. Both materials
            // must render both faces: walls so backs/tops never cull to
            // transparency from behind/under; the ceiling so the top-cap quads
            // (and any ceiling panel) close the roof seen from below in open
            // zones. Idempotent — works without re-running Create Grid Prefabs.
            // The double-sided wall side faces become coplanar with the
            // floor/ceiling panel edges; the offset shader makes them lose depth
            // ties so that seam stops z-fighting. Swap it in at load too, so the
            // fix is live on Play even before Create Grid Prefabs is re-run.
            var offsetShader = Shader.Find("Backrooms/GridWallOffset");
            if (set.wallMaterial != null && offsetShader != null
                && set.wallMaterial.shader != offsetShader)
                set.wallMaterial.shader = offsetShader;
            MakeDoubleSided(set.wallMaterial);
            MakeDoubleSided(set.ceilingMaterial);
            return set;
        }

        private static void MakeDoubleSided(Material mat)
        {
            if (mat != null && mat.HasProperty("_Cull"))
            {
                mat.SetFloat("_Cull", 0f); // CullMode.Off → both faces
                mat.doubleSidedGI = true;
            }
        }
    }

    /// <summary>
    /// Per-cell visual construction for one chunk of grid cells (§6 of
    /// BACKROOMS_GRID_SYSTEM.md). Walkable cells get Floor + Ceiling panels
    /// at the fixed 4 m room height; Wall cells collapse into ONE greedy
    /// mesh; Pillar/VoidEdge are prefab instances aligned to the 2.5 m grid.
    ///
    /// Render only — Rust owns collision; nothing here adds colliders.
    /// </summary>
    public static class GridChunkBuilder
    {
        private const float Cs = GridConstants.CellSize;
        private const float Ch = GridVisualConstants.CellHeight;
        private const int Size = GridConstants.ChunkCells;

        // Direction table for edge lips:
        // (dx, dz, yaw°) with prefabs authored on the +z edge.
        private static readonly (int dx, int dz, float yaw)[] Directions =
        {
            (0, 1, 0f), (1, 0, 90f), (0, -1, 180f), (-1, 0, 270f),
        };

        /// <summary>Build the whole chunk under one root placed at <paramref name="origin"/>.</summary>
        public static GameObject Build(GridCell[] cells, GridPrefabSet prefabs,
            Vector3 origin, string name)
        {
            var root = new GameObject(name);
            root.transform.position = origin;

            int[] wallHeights = WallGreedyMesher.ComputeWallHeights(cells, Size);
            byte[] wallInsets = WallGreedyMesher.ComputeWallInsets(cells, Size);
            BuildWalls(cells, prefabs, root.transform, wallHeights, wallInsets);

            for (int z = 0; z < Size; z++)
            {
                for (int x = 0; x < Size; x++)
                {
                    var cell = cells[z * Size + x];
                    switch (cell.Kind)
                    {
                        case GridCellType.Corridor:
                        case GridCellType.Open:
                        // Stair body removed for now (uniform 4 m rooms);
                        // the cell still reads as plain walkable floor.
                        case GridCellType.Stair:
                            PlaceFloor(prefabs, root.transform, x, z);
                            PlaceCeiling(prefabs, root.transform, x, z);
                            break;

                        case GridCellType.Anomaly:
                            PlaceFloor(prefabs, root.transform, x, z);
                            PlaceCeiling(prefabs, root.transform, x, z);
                            PlaceAnomalyMarker(root.transform, x, z);
                            break;

                        case GridCellType.Pillar:
                            PlaceFloor(prefabs, root.transform, x, z);
                            PlaceCeiling(prefabs, root.transform, x, z);
                            PlacePillar(prefabs, root.transform, x, z);
                            break;

                        case GridCellType.Pit:
                            // Hole down to layer N-1: ceiling but no floor, lip on
                            // every edge shared with a standing-walkable neighbour.
                            PlaceCeiling(prefabs, root.transform, x, z);
                            PlaceEdgeLips(cells, prefabs, root.transform, x, z);
                            break;

                        case GridCellType.Void:
                            // No floor at all; lip wherever a walkable cell borders it.
                            PlaceEdgeLips(cells, prefabs, root.transform, x, z);
                            break;

                        case GridCellType.Wall:
                            // The thin partition leaves a strip of the cell open
                            // toward the room; floor + ceiling close it. Fully
                            // enclosed wall cells need nothing (the greedy mesh
                            // is their only surface).
                            if (wallInsets[z * Size + x] != 0)
                            {
                                PlaceFloor(prefabs, root.transform, x, z);
                                PlaceCeiling(prefabs, root.transform, x, z);
                            }
                            break;
                    }
                }
            }

            return root;
        }

        private static void BuildWalls(GridCell[] cells, GridPrefabSet prefabs,
            Transform parent, int[] heights, byte[] insets)
        {
            var mesh = WallGreedyMesher.BuildChunkMesh(cells, Size, heights, insets);
            if (mesh.vertexCount == 0)
            {
                UnityEngine.Object.Destroy(mesh);
                return;
            }

            var go = new GameObject("Walls");
            go.transform.SetParent(parent, false);
            go.isStatic = true;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            // Submesh 0 = side faces (wall material, double-sided); submesh 1 =
            // top caps painted with the ceiling material so they read as part of
            // the ceiling and the coplanar seam with FloorCeiling panels vanishes.
            go.AddComponent<MeshRenderer>().sharedMaterials =
                new[] { prefabs.wallMaterial, prefabs.ceilingMaterial };
        }

        private static Vector3 CellCenter(int x, int z) =>
            new Vector3((x + 0.5f) * Cs, 0f, (z + 0.5f) * Cs);

        private static GameObject Instantiate(GameObject prefab, Transform parent,
            Vector3 localPos, float yaw)
        {
            var go = Object.Instantiate(prefab, parent);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            return go;
        }

        private static void PlaceFloor(GridPrefabSet prefabs, Transform parent, int x, int z)
        {
            Instantiate(prefabs.floor, parent, CellCenter(x, z), 0f);
        }

        /// <summary>Ceiling panel at the fixed 4 m room height (baked into the prefab).</summary>
        private static void PlaceCeiling(GridPrefabSet prefabs, Transform parent, int x, int z)
        {
            Instantiate(prefabs.ceiling, parent, CellCenter(x, z), 0f);
        }

        private static void PlacePillar(GridPrefabSet prefabs, Transform parent, int x, int z)
        {
            var go = Instantiate(prefabs.pillar, parent, CellCenter(x, z), 0f);
            var shaft = go.transform.Find("Shaft");
            if (shaft != null)
            {
                float h = 2f * Ch; // uniform 4 m room height
                var s = shaft.localScale;
                shaft.localScale = new Vector3(s.x, h, s.z);
                shaft.localPosition = new Vector3(0f, h / 2f, 0f);
            }
        }

        /// <summary>Lip on every edge of (x,z) shared with a floor-bearing neighbour.</summary>
        private static void PlaceEdgeLips(GridCell[] cells, GridPrefabSet prefabs,
            Transform parent, int x, int z)
        {
            foreach (var (dx, dz, yaw) in Directions)
            {
                int nx = x + dx;
                int nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= Size || nz >= Size)
                    continue;
                var n = cells[nz * Size + nx];
                // Only edges toward cells the player can stand on need a lip.
                if (n.IsWalkable && n.Kind != GridCellType.Pit && n.Kind != GridCellType.Void)
                    Instantiate(prefabs.voidEdge, parent, CellCenter(x, z), yaw);
            }
        }

        private static void PlaceAnomalyMarker(Transform parent, int x, int z)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "AnomalyMarker";
            var collider = marker.GetComponent<Collider>();
            if (collider != null)
                Object.Destroy(collider);
            marker.transform.SetParent(parent, false);
            marker.transform.localPosition = CellCenter(x, z) + new Vector3(0f, 1.2f, 0f);
            marker.transform.localScale = Vector3.one * 0.6f;
            marker.GetComponent<MeshRenderer>().sharedMaterial =
                MaterialHelper.MakeEmissive(new Color(0.9f, 0.35f, 0.15f), 2f);
        }
    }
}
