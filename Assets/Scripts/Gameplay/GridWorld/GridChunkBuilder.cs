using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// The 6 primitive prefabs (§6) plus the wall material, loaded from
    /// Resources/GridPrefabs as created by Backrooms/Create Grid Prefabs.
    /// </summary>
    public sealed class GridPrefabSet
    {
        public GameObject floorCeiling;
        public GameObject pillar;
        public GameObject stair;
        public GameObject voidEdge;
        public GameObject ceilingStep;
        public Material wallMaterial;

        public static GridPrefabSet LoadFromResources()
        {
            var set = new GridPrefabSet
            {
                floorCeiling = Resources.Load<GameObject>("GridPrefabs/FloorCeiling"),
                pillar = Resources.Load<GameObject>("GridPrefabs/Pillar"),
                stair = Resources.Load<GameObject>("GridPrefabs/Stair"),
                voidEdge = Resources.Load<GameObject>("GridPrefabs/VoidEdge"),
                ceilingStep = Resources.Load<GameObject>("GridPrefabs/CeilingStep"),
                wallMaterial = Resources.Load<Material>("GridMaterials/GridWall"),
            };
            if (set.floorCeiling == null)
                Debug.LogError("[GridPrefabSet] GridPrefabs not found in Resources. " +
                               "Run Backrooms/Create Grid Prefabs first.");

            // The greedy wall mesh is single-sided geometry (one quad per face);
            // make its material render both faces so wall backs and wall tops
            // never cull to transparency when the camera is behind/under them.
            // Cube prefabs are closed boxes and don't need this. Idempotent, so
            // it also works without re-running Create Grid Prefabs.
            if (set.wallMaterial != null && set.wallMaterial.HasProperty("_Cull"))
            {
                set.wallMaterial.SetFloat("_Cull", 0f); // CullMode.Off → both faces
                set.wallMaterial.doubleSidedGI = true;
            }
            return set;
        }
    }

    /// <summary>
    /// Per-cell visual construction for one chunk of grid cells (§6 of
    /// BACKROOMS_GRID_SYSTEM.md). Walkable cells get FloorCeiling tiles;
    /// Wall cells collapse into ONE greedy mesh; Pillar/Stair/VoidEdge/
    /// CeilingStep are prefab instances aligned to the 2.5 m grid.
    ///
    /// Render only — Rust owns collision; nothing here adds colliders.
    /// </summary>
    public static class GridChunkBuilder
    {
        private const float Cs = GridConstants.CellSize;
        private const int Size = GridConstants.ChunkCells;

        // Direction table shared by lips and ceiling steps:
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

            BuildWalls(cells, prefabs, root.transform);

            for (int z = 0; z < Size; z++)
            {
                for (int x = 0; x < Size; x++)
                {
                    var cell = cells[z * Size + x];
                    switch (cell.Kind)
                    {
                        case GridCellType.Corridor:
                        case GridCellType.Open:
                            PlaceFloorCeiling(prefabs, root.transform, x, z, cell.ceilingHeight);
                            break;

                        case GridCellType.Anomaly:
                            PlaceFloorCeiling(prefabs, root.transform, x, z, cell.ceilingHeight);
                            PlaceAnomalyMarker(root.transform, x, z);
                            break;

                        case GridCellType.Pillar:
                            // The column stores ceiling_height 0 (solid cell); roof it
                            // at the surrounding room height so the open-zone ceiling
                            // stays continuous over every pillar.
                            byte pillarCeil = NeighbourCeiling(cells, x, z);
                            PlaceFloorCeiling(prefabs, root.transform, x, z, pillarCeil);
                            PlacePillar(prefabs, root.transform, x, z, pillarCeil);
                            break;

                        case GridCellType.Stair:
                            // Stair body replaces the floor; roof the cell at room
                            // height so the ceiling closes over it (the proper
                            // hole-to-layer-above is post-Fase-3 art work).
                            PlaceCeilingOnly(prefabs, root.transform, x, z,
                                NeighbourCeiling(cells, x, z));
                            Instantiate(prefabs.stair, root.transform, CellCenter(x, z), 0f);
                            break;

                        case GridCellType.Pit:
                            // Hole down to layer N-1: ceiling but no floor, lip on
                            // every edge shared with a standing-walkable neighbour.
                            PlaceCeilingOnly(prefabs, root.transform, x, z, cell.ceilingHeight);
                            PlaceEdgeLips(cells, prefabs, root.transform, x, z);
                            break;

                        case GridCellType.Void:
                            // No floor at all; lip wherever a walkable cell borders it.
                            PlaceEdgeLips(cells, prefabs, root.transform, x, z);
                            break;

                        case GridCellType.Wall:
                            break; // greedy mesh handles every wall cell
                    }

                    if (cell.IsWalkable)
                        PlaceCeilingSteps(cells, prefabs, root.transform, x, z, cell);
                }
            }

            return root;
        }

        private static void BuildWalls(GridCell[] cells, GridPrefabSet prefabs, Transform parent)
        {
            int[] heights = WallGreedyMesher.ComputeWallHeights(cells, Size);
            List<WallRect> rects = WallGreedyMesher.GreedyRects(heights, Size);
            if (rects.Count == 0)
                return;

            var go = new GameObject("Walls");
            go.transform.SetParent(parent, false);
            go.isStatic = true;
            go.AddComponent<MeshFilter>().sharedMesh = WallGreedyMesher.BuildMesh(rects, Cs);
            go.AddComponent<MeshRenderer>().sharedMaterial = prefabs.wallMaterial;
        }

        private static Vector3 CellCenter(int x, int z) =>
            new Vector3((x + 0.5f) * Cs, 0f, (z + 0.5f) * Cs);

        /// <summary>
        /// Ceiling height (units) of the room around an unroofed cell: the tallest
        /// walkable orthogonal neighbour, so a column or stair is roofed by the same
        /// ceiling as the space it stands in. Falls back to 2 (5 m) if isolated.
        /// Mirrors WallGreedyMesher.ComputeWallHeights so walls and these inner
        /// cells agree on the ceiling they meet.
        /// </summary>
        private static byte NeighbourCeiling(GridCell[] cells, int x, int z)
        {
            int h = 0;
            foreach (var (dx, dz, _) in Directions)
            {
                int nx = x + dx;
                int nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= Size || nz >= Size)
                    continue;
                var n = cells[nz * Size + nx];
                if (n.IsWalkable && n.ceilingHeight > h)
                    h = n.ceilingHeight;
            }
            return (byte)Mathf.Max(h, 2);
        }

        private static GameObject Instantiate(GameObject prefab, Transform parent,
            Vector3 localPos, float yaw)
        {
            var go = Object.Instantiate(prefab, parent);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            return go;
        }

        private static void PlaceFloorCeiling(GridPrefabSet prefabs, Transform parent,
            int x, int z, byte ceilingUnits)
        {
            var go = Instantiate(prefabs.floorCeiling, parent, CellCenter(x, z), 0f);
            var ceiling = go.transform.Find("Ceiling");
            if (ceiling != null && ceilingUnits > 0)
            {
                var p = ceiling.localPosition;
                ceiling.localPosition = new Vector3(p.x, ceilingUnits * Cs + 0.04f, p.z);
            }
        }

        private static void PlaceCeilingOnly(GridPrefabSet prefabs, Transform parent,
            int x, int z, byte ceilingUnits)
        {
            var go = Instantiate(prefabs.floorCeiling, parent, CellCenter(x, z), 0f);
            var floor = go.transform.Find("Floor");
            if (floor != null)
                Object.Destroy(floor.gameObject);
            var ceiling = go.transform.Find("Ceiling");
            if (ceiling != null && ceilingUnits > 0)
            {
                var p = ceiling.localPosition;
                ceiling.localPosition = new Vector3(p.x, ceilingUnits * Cs + 0.04f, p.z);
            }
        }

        private static void PlacePillar(GridPrefabSet prefabs, Transform parent,
            int x, int z, byte ceilingUnits)
        {
            var go = Instantiate(prefabs.pillar, parent, CellCenter(x, z), 0f);
            var shaft = go.transform.Find("Shaft");
            if (shaft != null)
            {
                float h = Mathf.Max(ceilingUnits, 2) * Cs;
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

        /// <summary>
        /// Fascia between two walkable neighbours with different ceilings (§1).
        /// Only +x/+z are checked so each shared edge is handled exactly once.
        /// </summary>
        private static void PlaceCeilingSteps(GridCell[] cells, GridPrefabSet prefabs,
            Transform parent, int x, int z, GridCell cell)
        {
            for (int d = 0; d < 2; d++)
            {
                var (dx, dz, yaw) = Directions[d]; // (0,1,0°) and (1,0,90°)
                int nx = x + dx;
                int nz = z + dz;
                if (nx >= Size || nz >= Size)
                    continue;
                var n = cells[nz * Size + nx];
                if (!n.IsWalkable || n.ceilingHeight == cell.ceilingHeight)
                    continue;

                int high = Mathf.Max(cell.ceilingHeight, n.ceilingHeight);
                int gap = Mathf.Abs(cell.ceilingHeight - n.ceilingHeight);

                var go = Instantiate(prefabs.ceilingStep, parent,
                    CellCenter(x, z) + new Vector3(0f, high * Cs, 0f), yaw);
                // Fascia is authored 1 cell tall hanging from local y=0:
                // scaling Y by gap/1 makes it close exactly the height gap.
                go.transform.localScale = new Vector3(1f, gap, 1f);
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
