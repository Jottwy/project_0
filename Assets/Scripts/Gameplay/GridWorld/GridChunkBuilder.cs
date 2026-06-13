using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// The 5 primitive prefabs (§6) plus the wall/ceiling materials, loaded from
    /// Resources/GridPrefabs as created by Backrooms/Create Grid Prefabs.
    /// </summary>
    public sealed class GridPrefabSet
    {
        public GameObject floor;
        public GameObject ceiling;
        public GameObject wall;
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
                wall = Resources.Load<GameObject>("GridPrefabs/Wall"),
                pillar = Resources.Load<GameObject>("GridPrefabs/Pillar"),
                voidEdge = Resources.Load<GameObject>("GridPrefabs/VoidEdge"),
                wallMaterial = Resources.Load<Material>("GridMaterials/GridWall"),
                ceilingMaterial = Resources.Load<Material>("GridMaterials/GridCeiling"),
            };
            if (set.floor == null)
                Debug.LogError("[GridPrefabSet] GridPrefabs not found in Resources. " +
                               "Run Backrooms/Create Grid Prefabs first.");

            // Wall side faces and ceiling panels must render both faces so backs
            // and undersides never cull to transparency. Idempotent — works
            // without re-running Create Grid Prefabs. The offset shader makes the
            // wall faces lose depth ties to coplanar floor/ceiling panel edges so
            // the seam stops z-fighting; swap it in at load too.
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

    /// <summary>Render classification of one 5 m tile (a 2×2 block of cells).</summary>
    public enum TileKind { Solid, Open, Border, Hollow }

    /// <summary>Result of <see cref="GridChunkBuilder.ClassifyTile"/>.</summary>
    public readonly struct TileClass
    {
        public readonly TileKind Kind;
        public readonly byte WallEdges;   // Border: which tile edges carry a wall
        public readonly bool HasPillar;
        public readonly bool HasAnomaly;

        public TileClass(TileKind kind, byte wallEdges, bool hasPillar, bool hasAnomaly)
        {
            Kind = kind;
            WallEdges = wallEdges;
            HasPillar = hasPillar;
            HasAnomaly = hasAnomaly;
        }
    }

    /// <summary>
    /// Tile-based visual construction for one chunk (ADR-001 tile system). The
    /// chunk's 20×20 Rust cells fold into 10×10 render tiles of 5 m (2×2 cells).
    /// Each tile gets Floor + Ceiling panels at the fixed 4 m room height and
    /// independent Wall prefab pieces (5×4×0.2) on its edges — no procedural
    /// mesh, no fusion: one piece, one wall, like LEGO.
    ///
    /// Render only — Rust owns collision; nothing here adds colliders.
    /// </summary>
    public static class GridChunkBuilder
    {
        private const float Ch = GridVisualConstants.CellHeight;
        private const float Ts = GridVisualConstants.TileSize;
        private const int Size = GridConstants.ChunkCells;
        // A render tile is a 2×2 block of cells → 10×10 tiles per chunk.
        private const int Tiles = Size / 2;

        // Tile-edge flags: which side of a tile a wall or lip sits on.
        public const byte EdgeSouth = 1; // -z
        public const byte EdgeNorth = 2; // +z
        public const byte EdgeWest  = 4; // -x
        public const byte EdgeEast  = 8; // +x

        // Wall pieces: edge flag → local offset (× TileSize) + yaw. The Wall
        // prefab runs along X (yaw 0 → N/S edges); yaw 90 turns it along Z.
        private static readonly (byte flag, float ox, float oz, float yaw)[] WallEdgeTable =
        {
            (EdgeSouth, 0f, -0.5f, 0f),
            (EdgeNorth, 0f, 0.5f, 0f),
            (EdgeWest, -0.5f, 0f, 90f),
            (EdgeEast, 0.5f, 0f, 90f),
        };

        // Per-edge neighbour lookup: flag → (dx, dz, void-lip yaw). The VoidEdge
        // lip is authored on the +z edge, so +z=0°, +x=90°, -z=180°, -x=270°.
        private static readonly (byte flag, int dx, int dz, float lipYaw)[] NeighbourTable =
        {
            (EdgeNorth, 0, 1, 0f),
            (EdgeEast, 1, 0, 90f),
            (EdgeSouth, 0, -1, 180f),
            (EdgeWest, -1, 0, 270f),
        };

        /// <summary>
        /// Classify the 2×2 cell block of tile (tileX, tileZ) into a render tile:
        ///  · 4 Wall cells    → Solid (no floor/ceiling; perimeter walls only).
        ///  · ≥1 Wall (mixed) → Border (floor + ceiling + a wall on every tile
        ///    side whose BOTH cells are Wall).
        ///  · 0 Wall, ≥2 Void
        ///    or ≥1 Pit       → Hollow (no floor/ceiling; void edges toward floor
        ///    tiles) — open vertical shaft (ADR-008).
        ///  · otherwise       → Open (floor + ceiling; +pillar/+anomaly if any
        ///    cell is one). Stair counts as Open; a single Void is floored.
        /// </summary>
        public static TileClass ClassifyTile(GridCell[] cells, int tileX, int tileZ)
        {
            int cx = tileX * 2;
            int cz = tileZ * 2;
            GridCellType sw = cells[cz * Size + cx].Kind;
            GridCellType se = cells[cz * Size + cx + 1].Kind;
            GridCellType nw = cells[(cz + 1) * Size + cx].Kind;
            GridCellType ne = cells[(cz + 1) * Size + cx + 1].Kind;

            int wallCount = 0;
            if (sw == GridCellType.Wall) wallCount++;
            if (se == GridCellType.Wall) wallCount++;
            if (nw == GridCellType.Wall) wallCount++;
            if (ne == GridCellType.Wall) wallCount++;

            int voidCount = 0;
            if (sw == GridCellType.Void) voidCount++;
            if (se == GridCellType.Void) voidCount++;
            if (nw == GridCellType.Void) voidCount++;
            if (ne == GridCellType.Void) voidCount++;

            int pitCount = 0;
            if (sw == GridCellType.Pit) pitCount++;
            if (se == GridCellType.Pit) pitCount++;
            if (nw == GridCellType.Pit) pitCount++;
            if (ne == GridCellType.Pit) pitCount++;

            bool hasPillar = sw == GridCellType.Pillar || se == GridCellType.Pillar
                          || nw == GridCellType.Pillar || ne == GridCellType.Pillar;
            bool hasAnomaly = sw == GridCellType.Anomaly || se == GridCellType.Anomaly
                          || nw == GridCellType.Anomaly || ne == GridCellType.Anomaly;

            if (wallCount == 4)
                return new TileClass(TileKind.Solid, 0, false, false);

            if (wallCount >= 1)
            {
                byte edges = 0;
                if (sw == GridCellType.Wall && se == GridCellType.Wall) edges |= EdgeSouth;
                if (nw == GridCellType.Wall && ne == GridCellType.Wall) edges |= EdgeNorth;
                if (sw == GridCellType.Wall && nw == GridCellType.Wall) edges |= EdgeWest;
                if (se == GridCellType.Wall && ne == GridCellType.Wall) edges |= EdgeEast;
                return new TileClass(TileKind.Border, edges, false, hasAnomaly);
            }

            if (voidCount >= 2 || pitCount >= 1)
                return new TileClass(TileKind.Hollow, 0, false, false);

            return new TileClass(TileKind.Open, 0, hasPillar, hasAnomaly);
        }

        /// <summary>True if any cell of tile (tileX, tileZ)'s 2×2 block is a Pit.</summary>
        private static bool TileHasPit(GridCell[] cells, int tileX, int tileZ)
        {
            int cx = tileX * 2;
            int cz = tileZ * 2;
            return cells[cz * Size + cx].Kind == GridCellType.Pit
                || cells[cz * Size + cx + 1].Kind == GridCellType.Pit
                || cells[(cz + 1) * Size + cx].Kind == GridCellType.Pit
                || cells[(cz + 1) * Size + cx + 1].Kind == GridCellType.Pit;
        }

        /// <summary>
        /// Build the whole chunk under one root placed at <paramref name="origin"/>.
        /// <paramref name="layerAbove"/> (optional): cells of the same chunk one
        /// layer up. A Pit up there is a real vertical transition whose mouth is
        /// this tile's ceiling plane — skip the ceiling panel so the shaft
        /// connects. Void-only Hollow tiles above do NOT open the ceiling: they
        /// are not transitions and the cell below them is not guaranteed walkable.
        /// </summary>
        public static GameObject Build(GridCell[] cells, GridPrefabSet prefabs,
            Vector3 origin, string name, GridCell[] layerAbove = null)
        {
            var root = new GameObject(name);
            root.transform.position = origin;

            // Classify every tile up front so placement can consult neighbours.
            var grid = new TileClass[Tiles * Tiles];
            for (int tz = 0; tz < Tiles; tz++)
                for (int tx = 0; tx < Tiles; tx++)
                    grid[tz * Tiles + tx] = ClassifyTile(cells, tx, tz);

            for (int tz = 0; tz < Tiles; tz++)
            {
                for (int tx = 0; tx < Tiles; tx++)
                {
                    var tile = grid[tz * Tiles + tx];
                    bool pitAbove = layerAbove != null && TileHasPit(layerAbove, tx, tz);
                    switch (tile.Kind)
                    {
                        case TileKind.Solid:
                            PlaceSolidWalls(prefabs, root.transform, grid, tx, tz);
                            break;

                        case TileKind.Open:
                            PlaceFloor(prefabs, root.transform, tx, tz);
                            if (!pitAbove) PlaceCeiling(prefabs, root.transform, tx, tz);
                            if (tile.HasPillar) PlacePillar(prefabs, root.transform, tx, tz);
                            if (tile.HasAnomaly) PlaceAnomalyMarker(root.transform, tx, tz);
                            break;

                        case TileKind.Border:
                            PlaceFloor(prefabs, root.transform, tx, tz);
                            if (!pitAbove) PlaceCeiling(prefabs, root.transform, tx, tz);
                            PlaceWalls(prefabs, root.transform, tile.WallEdges, tx, tz);
                            if (tile.HasAnomaly) PlaceAnomalyMarker(root.transform, tx, tz);
                            break;

                        case TileKind.Hollow:
                            PlaceVoidEdges(prefabs, root.transform, grid, tx, tz);
                            break;
                    }
                }
            }

            return root;
        }

        private static Vector3 TileCenter(int tx, int tz) =>
            new Vector3((tx + 0.5f) * Ts, 0f, (tz + 0.5f) * Ts);

        private static GameObject Instantiate(GameObject prefab, Transform parent,
            Vector3 localPos, float yaw)
        {
            var go = Object.Instantiate(prefab, parent);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            return go;
        }

        private static void AddColliderIfMissing(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
            {
                if (r.GetComponent<Collider>() != null) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var col    = r.gameObject.AddComponent<BoxCollider>();
                var mb     = mf.sharedMesh.bounds;
                col.center = mb.center;
                col.size   = mb.size;
            }
        }

        private static void PlaceFloor(GridPrefabSet prefabs, Transform parent, int tx, int tz)
            => AddColliderIfMissing(Instantiate(prefabs.floor, parent, TileCenter(tx, tz), 0f));

        /// <summary>Ceiling panel at the fixed 4 m room height (baked into the prefab).</summary>
        private static void PlaceCeiling(GridPrefabSet prefabs, Transform parent, int tx, int tz)
            => Instantiate(prefabs.ceiling, parent, TileCenter(tx, tz), 0f);

        /// <summary>Independent 5×4×0.2 wall pieces on the flagged tile edges.</summary>
        private static void PlaceWalls(GridPrefabSet prefabs, Transform parent,
            byte edges, int tx, int tz)
        {
            foreach (var (flag, ox, oz, yaw) in WallEdgeTable)
                if ((edges & flag) != 0)
                    AddColliderIfMissing(Instantiate(prefabs.wall, parent,
                        TileCenter(tx, tz) + new Vector3(ox * Ts, 0f, oz * Ts), yaw));
        }

        /// <summary>Solid tile: a wall on each side facing a non-solid in-chunk tile.</summary>
        private static void PlaceSolidWalls(GridPrefabSet prefabs, Transform parent,
            TileClass[] grid, int tx, int tz)
        {
            byte edges = 0;
            foreach (var (flag, dx, dz, _) in NeighbourTable)
            {
                int nx = tx + dx;
                int nz = tz + dz;
                // Chunk border: the neighbour tile lives in the next chunk
                // (unknown here). Emit the exterior wall so the seam isn't
                // left open against an empty/unwalled neighbour.
                if (nx < 0 || nz < 0 || nx >= Tiles || nz >= Tiles)
                {
                    PlaceWallOnEdge(prefabs, parent, tx, tz, dx, dz);
                    continue;
                }
                if (grid[nz * Tiles + nx].Kind != TileKind.Solid)
                    edges |= flag;
            }
            PlaceWalls(prefabs, parent, edges, tx, tz);
        }

        /// <summary>Emit one Wall piece on the tile edge facing (dx, dz), using
        /// the same position/rotation table as interior solid walls.</summary>
        private static void PlaceWallOnEdge(GridPrefabSet prefabs, Transform parent,
            int tx, int tz, int dx, int dz)
        {
            byte flag = 0;
            if      (dx ==  0 && dz ==  1) flag = EdgeNorth;
            else if (dx ==  1 && dz ==  0) flag = EdgeEast;
            else if (dx ==  0 && dz == -1) flag = EdgeSouth;
            else if (dx == -1 && dz ==  0) flag = EdgeWest;
            PlaceWalls(prefabs, parent, flag, tx, tz);
        }

        private static void PlacePillar(GridPrefabSet prefabs, Transform parent, int tx, int tz)
        {
            var go = Instantiate(prefabs.pillar, parent, TileCenter(tx, tz), 0f);
            var shaft = go.transform.Find("Shaft");
            if (shaft != null)
            {
                float h = 2f * Ch; // uniform 4 m room height
                var s = shaft.localScale;
                shaft.localScale = new Vector3(s.x, h, s.z);
                shaft.localPosition = new Vector3(0f, h / 2f, 0f);
            }
        }

        /// <summary>Hollow tile: a lip on each side facing a floor-bearing tile.</summary>
        private static void PlaceVoidEdges(GridPrefabSet prefabs, Transform parent,
            TileClass[] grid, int tx, int tz)
        {
            foreach (var (_, dx, dz, lipYaw) in NeighbourTable)
            {
                int nx = tx + dx;
                int nz = tz + dz;
                if (nx < 0 || nz < 0 || nx >= Tiles || nz >= Tiles)
                    continue;
                var k = grid[nz * Tiles + nx].Kind;
                if (k == TileKind.Open || k == TileKind.Border) // tiles that have a floor
                    Instantiate(prefabs.voidEdge, parent, TileCenter(tx, tz), lipYaw);
            }
        }

        private static void PlaceAnomalyMarker(Transform parent, int tx, int tz)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "AnomalyMarker";
            var collider = marker.GetComponent<Collider>();
            if (collider != null)
                Object.Destroy(collider);
            marker.transform.SetParent(parent, false);
            marker.transform.localPosition = TileCenter(tx, tz) + new Vector3(0f, 1.2f, 0f);
            marker.transform.localScale = Vector3.one * 0.6f;
            marker.GetComponent<MeshRenderer>().sharedMaterial =
                MaterialHelper.MakeEmissive(new Color(0.9f, 0.35f, 0.15f), 2f);
        }
    }
}
