using System;
using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    // ─────────────────────────────────────────────────────────────────
    // LayerConfig — ScriptableObject, one per layer (0..3).
    // ─────────────────────────────────────────────────────────────────

    [CreateAssetMenu(menuName = "Backrooms/LayerConfig", fileName = "LayerConfig")]
    public sealed class LayerConfig : ScriptableObject
    {
        [Range(0f, 1f)] public float wallDensity = 0.50f;
        [Range(1, 2)]   public int   corridorWidth = 1;
        [Range(0f, 1f)] public float openZoneChance = 0.20f;
        [Range(2, 6)]   public int   openZoneSize = 3;
        [Range(0f, 1f)] public float pitChance = 0.08f;
        [Range(0f, 1f)] public float pillarChance = 0.20f;
        [Range(0f, 1f)] public float doubleHeightChance = 0.05f;
        [Range(0f, 1f)] public float interLayerConnection = 0.08f;
    }

    // ─────────────────────────────────────────────────────────────────
    // StructureDefinition — parsed from default_structures.json
    // ─────────────────────────────────────────────────────────────────

    [Serializable]
    public sealed class StructureDefinition
    {
        public string   id;
        public float    probability;
        public int      layersTall = 1;
        public string[][] pattern;   // [row][col], row 0 = south
    }

    [Serializable]
    internal sealed class StructureLibraryJson
    {
        public StructureDefinition[] structures;
    }

    // ─────────────────────────────────────────────────────────────────
    // BSP node — used internally by WorldGenerator
    // ─────────────────────────────────────────────────────────────────

    internal sealed class BspZone
    {
        public int tx, tz, tw, th;  // tile coords (10×10 tile space)
        public bool isOpen;         // OpenZone flag

        public BspZone(int tx, int tz, int tw, int th)
        {
            this.tx = tx; this.tz = tz; this.tw = tw; this.th = th;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // WorldGenerator — deterministic chunk generation
    // ─────────────────────────────────────────────────────────────────

    public static class WorldGenerator
    {
        // Chunk is 20×20 cells; tile grid is 10×10 (2×2 cells per tile).
        private const int Cells = GridConstants.ChunkCells; // 20
        private const int Tiles = Cells / 2;                // 10

        private static List<StructureDefinition> _structures;

        public static void LoadStructures()
        {
            if (_structures != null) return;
            _structures = new List<StructureDefinition>();
            var ta = Resources.Load<TextAsset>("Structures/default_structures");
            if (ta == null)
            {
                Debug.LogWarning("[WorldGenerator] default_structures.json not found in Resources/Structures/");
                return;
            }
            var lib = JsonUtility.FromJson<StructureLibraryJson>(ta.text);
            if (lib?.structures != null)
                _structures.AddRange(lib.structures);
        }

        /// <summary>
        /// Generate a deterministic 20×20 GridCell array for one chunk+layer.
        /// forcedWalkable: set of cell indices that MUST become Corridor (from a
        /// Pit in the layer above).
        /// </summary>
        public static GridCell[] GenerateChunk(
            long seed, int chunkX, int chunkZ, int layer,
            LayerConfig cfg, HashSet<int> forcedWalkable = null)
        {
            LoadStructures();

            // Step 1 — deterministic seed
            var rng = new System.Random(DeriveRngSeed(seed, chunkX, chunkZ, layer));

            // Step 2 — fill with Wall
            var tiles = new GridCellType[Tiles * Tiles];
            for (int i = 0; i < tiles.Length; i++) tiles[i] = GridCellType.Wall;

            // Step 3 + 4 + 5 — BSP zoning + corridor carving + open zones
            var zones = new List<BspZone>();
            BspSplit(rng, cfg, 0, 0, Tiles, Tiles, zones);
            CarveZones(rng, cfg, tiles, zones);
            ConnectZones(rng, cfg, tiles, zones);

            // Step 6 — superstructures
            var pitTiles = new HashSet<int>();
            TryPlaceStructures(rng, cfg, tiles, layer, pitTiles);

            // Step 7 — pits from zone borders
            PlacePits(rng, cfg, tiles, zones, pitTiles);

            // Step 8 — pillars in large open zones
            PlacePillars(rng, cfg, tiles, zones);

            // Step 9 — guarantee border connectivity
            EnforceBorderCorridor(rng, tiles);

            // Step 10 — apply forced walkable from layer above
            if (forcedWalkable != null)
                foreach (int idx in forcedWalkable)
                    if (idx >= 0 && idx < tiles.Length)
                        tiles[idx] = GridCellType.Corridor;

            // Convert tile grid → cell grid (each tile → 2×2 cells)
            return TilesToCells(tiles, pitTiles);
        }

        // ── Returns a set of CELL indices that are Pit (for layer above to consume)
        public static HashSet<int> GetPitCellIndices(GridCell[] cells)
        {
            var set = new HashSet<int>();
            for (int i = 0; i < cells.Length; i++)
                if (cells[i].Kind == GridCellType.Pit)
                    set.Add(i);
            return set;
        }

        // ─── Seeding ──────────────────────────────────────────────────

        private static int DeriveRngSeed(long seed, int cx, int cz, int layer)
        {
            unchecked
            {
                long s = seed ^ ((long)cx * 73856093L) ^ ((long)cz * 19349663L)
                              ^ ((long)layer * 83492791L);
                return (int)(s ^ (s >> 32));
            }
        }

        // ─── BSP split ────────────────────────────────────────────────

        private static void BspSplit(System.Random rng, LayerConfig cfg,
            int tx, int tz, int tw, int th, List<BspZone> zones)
        {
            bool canSplitH = th >= cfg.openZoneSize * 2;
            bool canSplitV = tw >= cfg.openZoneSize * 2;

            if (!canSplitH && !canSplitV)
            {
                zones.Add(new BspZone(tx, tz, tw, th));
                return;
            }

            bool splitH = canSplitH && (!canSplitV || rng.NextDouble() < 0.5);
            if (splitH)
            {
                int min = Mathf.Max(2, th / 3);
                int max = Mathf.Min(th - 2, th * 2 / 3);
                if (min >= max) { zones.Add(new BspZone(tx, tz, tw, th)); return; }
                int cut = rng.Next(min, max + 1);
                BspSplit(rng, cfg, tx, tz, tw, cut, zones);
                BspSplit(rng, cfg, tx, tz + cut, tw, th - cut, zones);
            }
            else
            {
                int min = Mathf.Max(2, tw / 3);
                int max = Mathf.Min(tw - 2, tw * 2 / 3);
                if (min >= max) { zones.Add(new BspZone(tx, tz, tw, th)); return; }
                int cut = rng.Next(min, max + 1);
                BspSplit(rng, cfg, tx, tz, cut, th, zones);
                BspSplit(rng, cfg, tx + cut, tz, tw - cut, th, zones);
            }
        }

        // ─── Carve zones ──────────────────────────────────────────────

        private static void CarveZones(System.Random rng, LayerConfig cfg,
            GridCellType[] tiles, List<BspZone> zones)
        {
            foreach (var z in zones)
            {
                bool open = rng.NextDouble() < cfg.openZoneChance;
                z.isOpen = open;

                // Carve interior (leave 1-tile border as potential wall)
                int x0 = z.tx + 1, x1 = z.tx + z.tw - 1;
                int z0 = z.tz + 1, z1 = z.tz + z.th - 1;
                if (x0 >= x1 || z0 >= z1) continue;

                for (int zz = z0; zz < z1; zz++)
                    for (int xx = x0; xx < x1; xx++)
                        tiles[zz * Tiles + xx] = open
                            ? GridCellType.Open
                            : GridCellType.Corridor;
            }
        }

        // ─── Connect zones with corridors ─────────────────────────────

        private static void ConnectZones(System.Random rng, LayerConfig cfg,
            GridCellType[] tiles, List<BspZone> zones)
        {
            if (zones.Count < 2) return;

            // Simple MST-style: connect each zone to the nearest unconnected zone.
            var connected = new HashSet<int> { 0 };
            while (connected.Count < zones.Count)
            {
                int bestA = -1, bestB = -1;
                float bestDist = float.MaxValue;
                foreach (int a in connected)
                {
                    var za = zones[a];
                    for (int b = 0; b < zones.Count; b++)
                    {
                        if (connected.Contains(b)) continue;
                        var zb = zones[b];
                        float d = Vector2.Distance(
                            new Vector2(za.tx + za.tw / 2f, za.tz + za.th / 2f),
                            new Vector2(zb.tx + zb.tw / 2f, zb.tz + zb.th / 2f));
                        if (d < bestDist) { bestDist = d; bestA = a; bestB = b; }
                    }
                }
                if (bestA < 0) break;
                CarveCorridor(rng, cfg, tiles, zones[bestA], zones[bestB]);
                connected.Add(bestB);
            }
        }

        private static void CarveCorridor(System.Random rng, LayerConfig cfg,
            GridCellType[] tiles, BspZone a, BspZone b)
        {
            int ax = Mathf.Clamp(a.tx + a.tw / 2, 1, Tiles - 2);
            int az = Mathf.Clamp(a.tz + a.th / 2, 1, Tiles - 2);
            int bx = Mathf.Clamp(b.tx + b.tw / 2, 1, Tiles - 2);
            int bz = Mathf.Clamp(b.tz + b.th / 2, 1, Tiles - 2);
            int w = cfg.corridorWidth;

            // L-shaped corridor: horizontal then vertical
            int midX = rng.NextDouble() < 0.5 ? ax : bx;
            for (int x = Mathf.Min(ax, midX); x <= Mathf.Max(ax, midX); x++)
                for (int dw = 0; dw < w; dw++)
                    SetCorridor(tiles, x, Mathf.Clamp(az + dw, 0, Tiles - 1));
            for (int z = Mathf.Min(az, bz); z <= Mathf.Max(az, bz); z++)
                for (int dw = 0; dw < w; dw++)
                    SetCorridor(tiles, Mathf.Clamp(midX + dw, 0, Tiles - 1), z);
            for (int x = Mathf.Min(midX, bx); x <= Mathf.Max(midX, bx); x++)
                for (int dw = 0; dw < w; dw++)
                    SetCorridor(tiles, x, Mathf.Clamp(bz + dw, 0, Tiles - 1));
        }

        private static void SetCorridor(GridCellType[] tiles, int tx, int tz)
        {
            int idx = tz * Tiles + tx;
            if (idx >= 0 && idx < tiles.Length && tiles[idx] == GridCellType.Wall)
                tiles[idx] = GridCellType.Corridor;
        }

        // ─── Superstructures ──────────────────────────────────────────

        private static void TryPlaceStructures(System.Random rng, LayerConfig cfg,
            GridCellType[] tiles, int layer, HashSet<int> pitTilesOut)
        {
            if (_structures == null || _structures.Count == 0) return;

            foreach (var s in _structures)
            {
                if (rng.NextDouble() >= s.probability) continue;
                if (s.pattern == null || s.pattern.Length == 0) continue;

                int rows = s.pattern.Length;
                int cols = s.pattern[0]?.Length ?? 0;
                if (rows == 0 || cols == 0) continue;

                // Pick random anchor
                int maxTx = Tiles - cols - 1;
                int maxTz = Tiles - rows - 1;
                if (maxTx < 1 || maxTz < 1) continue;

                int anchorX = rng.Next(1, maxTx + 1);
                int anchorZ = rng.Next(1, maxTz + 1);

                for (int r = 0; r < rows; r++)
                {
                    var row = s.pattern[r];
                    int cols2 = row?.Length ?? 0;
                    for (int c = 0; c < Mathf.Min(cols, cols2); c++)
                    {
                        int ttx = anchorX + c;
                        int ttz = anchorZ + r;
                        int idx = ttz * Tiles + ttx;
                        if (idx < 0 || idx >= tiles.Length) continue;

                        switch (row[c])
                        {
                            case "W": tiles[idx] = GridCellType.Wall;     break;
                            case "C": tiles[idx] = GridCellType.Corridor; break;
                            case "O": tiles[idx] = GridCellType.Open;     break;
                            case "P": tiles[idx] = GridCellType.Pillar;   break;
                            case "T": tiles[idx] = GridCellType.Pit; pitTilesOut.Add(idx); break;
                        }
                    }
                }
            }
        }

        // ─── Pits ─────────────────────────────────────────────────────

        private static void PlacePits(System.Random rng, LayerConfig cfg,
            GridCellType[] tiles, List<BspZone> zones, HashSet<int> pitTiles)
        {
            foreach (var z in zones)
            {
                // Only on zone borders
                for (int x = z.tx; x < z.tx + z.tw; x++)
                {
                    TryPit(rng, cfg, tiles, pitTiles, x, z.tz);
                    TryPit(rng, cfg, tiles, pitTiles, x, z.tz + z.th - 1);
                }
                for (int zz = z.tz + 1; zz < z.tz + z.th - 1; zz++)
                {
                    TryPit(rng, cfg, tiles, pitTiles, z.tx, zz);
                    TryPit(rng, cfg, tiles, pitTiles, z.tx + z.tw - 1, zz);
                }
            }
        }

        private static void TryPit(System.Random rng, LayerConfig cfg,
            GridCellType[] tiles, HashSet<int> pitTiles, int tx, int tz)
        {
            if (tx < 0 || tz < 0 || tx >= Tiles || tz >= Tiles) return;
            int idx = tz * Tiles + tx;
            if (tiles[idx] == GridCellType.Wall) return;
            if (rng.NextDouble() < cfg.pitChance)
            {
                tiles[idx] = GridCellType.Pit;
                pitTiles.Add(idx);
            }
        }

        // ─── Pillars ──────────────────────────────────────────────────

        private static void PlacePillars(System.Random rng, LayerConfig cfg,
            GridCellType[] tiles, List<BspZone> zones)
        {
            foreach (var z in zones)
            {
                if (!z.isOpen || z.tw < 3 || z.th < 3) continue;
                // Asymmetric pillar positions inside the zone
                for (int zz = z.tz + 1; zz < z.tz + z.th - 1; zz++)
                {
                    for (int xx = z.tx + 1; xx < z.tx + z.tw - 1; xx++)
                    {
                        if ((xx + zz * 3) % 4 == 0 && rng.NextDouble() < cfg.pillarChance)
                        {
                            int idx = zz * Tiles + xx;
                            if (tiles[idx] == GridCellType.Open)
                                tiles[idx] = GridCellType.Pillar;
                        }
                    }
                }
            }
        }

        // ─── Border connectivity ───────────────────────────────────────

        private static void EnforceBorderCorridor(System.Random rng, GridCellType[] tiles)
        {
            // Each border side: guarantee at least one non-wall cell
            // (pick one random position per side if all are wall)
            EnsureSideCorridor(rng, tiles, 0, Tiles, false, 0);      // south z=0
            EnsureSideCorridor(rng, tiles, 0, Tiles, false, Tiles - 1); // north
            EnsureSideCorridor(rng, tiles, 0, Tiles, true, 0);       // west x=0
            EnsureSideCorridor(rng, tiles, 0, Tiles, true, Tiles - 1);  // east
        }

        private static void EnsureSideCorridor(System.Random rng, GridCellType[] tiles,
            int start, int end, bool alongX, int fixedAxis)
        {
            bool hasOpen = false;
            for (int i = start; i < end; i++)
            {
                int idx = alongX
                    ? fixedAxis * Tiles + i
                    : i * Tiles + fixedAxis;
                if (tiles[idx] != GridCellType.Wall) { hasOpen = true; break; }
            }
            if (!hasOpen)
            {
                int pos = rng.Next(start + 1, end - 1);
                int idx = alongX ? fixedAxis * Tiles + pos : pos * Tiles + fixedAxis;
                tiles[idx] = GridCellType.Corridor;
            }
        }

        // ─── Tile → Cell expansion ────────────────────────────────────

        private static GridCell[] TilesToCells(GridCellType[] tiles, HashSet<int> pitTileIndices)
        {
            var cells = new GridCell[Cells * Cells];
            for (int tz = 0; tz < Tiles; tz++)
            {
                for (int tx = 0; tx < Tiles; tx++)
                {
                    var kind = tiles[tz * Tiles + tx];
                    byte ceil = (kind == GridCellType.Wall) ? (byte)0 : (byte)2;
                    // Expand tile to 2×2 cells
                    for (int dz = 0; dz < 2; dz++)
                    {
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int cellX = tx * 2 + dx;
                            int cellZ = tz * 2 + dz;
                            cells[cellZ * Cells + cellX] = new GridCell(kind, ceil, 0);
                        }
                    }
                }
            }
            return cells;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // ChunkStreamer — manages a 3×3 ring of loaded chunks
    // ─────────────────────────────────────────────────────────────────

    public sealed class ChunkStreamer : MonoBehaviour
    {
        public long   seed      = 42;
        public int    layerCount = 4;
        public int    viewRadius = 1;   // 3×3 chunks
        public Transform playerTransform;

        [Header("Layer configs (0..3)")]
        public LayerConfig[] layerConfigs = new LayerConfig[4];

        private GridPrefabSet _prefabs;
        private readonly Dictionary<(int, int, int), GameObject> _loaded
            = new Dictionary<(int, int, int), GameObject>();
        private readonly Dictionary<(int, int, int), GridCell[]> _cache
            = new Dictionary<(int, int, int), GridCell[]>();

        private int _lastCX = int.MinValue;
        private int _lastCZ = int.MinValue;

        private const float Side = GridConstants.ChunkCells * GridConstants.CellSize; // 50 m

        private void Start()
        {
            _prefabs = GridPrefabSet.LoadFromResources();
            if (_prefabs.floor == null) return;
            UpdateChunks(force: true);
        }

        private void Update()
        {
            if (playerTransform == null) return;
            int cx = Mathf.FloorToInt(playerTransform.position.x / Side);
            int cz = Mathf.FloorToInt(playerTransform.position.z / Side);
            if (cx != _lastCX || cz != _lastCZ)
                UpdateChunks();
        }

        private void UpdateChunks(bool force = false)
        {
            if (playerTransform == null) return;
            int cx = Mathf.FloorToInt(playerTransform.position.x / Side);
            int cz = Mathf.FloorToInt(playerTransform.position.z / Side);

            if (!force && cx == _lastCX && cz == _lastCZ) return;
            _lastCX = cx; _lastCZ = cz;

            // Collect desired keys
            var desired = new HashSet<(int, int, int)>();
            for (int dz = -viewRadius; dz <= viewRadius; dz++)
                for (int dx = -viewRadius; dx <= viewRadius; dx++)
                    for (int layer = 0; layer < layerCount; layer++)
                        desired.Add((cx + dx, cz + dz, layer));

            // Destroy out-of-range
            var toRemove = new List<(int, int, int)>();
            foreach (var key in _loaded.Keys)
                if (!desired.Contains(key)) toRemove.Add(key);
            foreach (var key in toRemove)
            {
                Destroy(_loaded[key]);
                _loaded.Remove(key);
            }

            // Build missing chunks — all layers per column first (for pit chaining)
            for (int dz = -viewRadius; dz <= viewRadius; dz++)
            {
                for (int dx = -viewRadius; dx <= viewRadius; dx++)
                {
                    int ccx = cx + dx, ccz = cz + dz;
                    EnsureColumn(ccx, ccz);
                }
            }
        }

        private void EnsureColumn(int ccx, int ccz)
        {
            // Generate all layers top-down so forced-walkable propagates
            HashSet<int> forcedWalkable = null;
            var colCells = new GridCell[layerCount][];

            for (int layer = layerCount - 1; layer >= 0; layer--)
            {
                var key = (ccx, ccz, layer);
                if (!_cache.ContainsKey(key))
                {
                    var cfg = GetConfig(layer);
                    colCells[layer] = WorldGenerator.GenerateChunk(
                        seed, ccx, ccz, layer, cfg, forcedWalkable);
                    _cache[key] = colCells[layer];
                }
                else
                {
                    colCells[layer] = _cache[key];
                }
                forcedWalkable = WorldGenerator.GetPitCellIndices(colCells[layer]);
            }

            // Now build GameObjects for loaded chunks
            for (int layer = 0; layer < layerCount; layer++)
            {
                var key = (ccx, ccz, layer);
                if (_loaded.ContainsKey(key)) continue;

                var origin = new Vector3(
                    ccx * Side, layer * GridConstants.LayerHeight, ccz * Side);
                var layerAbove = layer + 1 < layerCount ? _cache.GetValueOrDefault((ccx, ccz, layer + 1)) : null;
                var go = GridChunkBuilder.Build(colCells[layer], _prefabs, origin,
                    $"Chunk_L{layer}_{ccx}_{ccz}", layerAbove);
                go.transform.SetParent(transform, true);
                _loaded[key] = go;
            }
        }

        private LayerConfig GetConfig(int layer)
        {
            if (layerConfigs != null && layer < layerConfigs.Length && layerConfigs[layer] != null)
                return layerConfigs[layer];
            // Fallback: inline defaults
            return DefaultConfig(layer);
        }

        private static LayerConfig DefaultConfig(int layer)
        {
            var cfg = ScriptableObject.CreateInstance<LayerConfig>();
            switch (layer)
            {
                case 0: cfg.wallDensity=0.45f; cfg.corridorWidth=2; cfg.openZoneChance=0.35f; cfg.openZoneSize=4; cfg.pitChance=0.05f; cfg.pillarChance=0.30f; cfg.doubleHeightChance=0.10f; cfg.interLayerConnection=0.08f; break;
                case 1: cfg.wallDensity=0.60f; cfg.corridorWidth=1; cfg.openZoneChance=0.15f; cfg.openZoneSize=3; cfg.pitChance=0.08f; cfg.pillarChance=0.15f; cfg.doubleHeightChance=0.05f; cfg.interLayerConnection=0.10f; break;
                case 2: cfg.wallDensity=0.70f; cfg.corridorWidth=1; cfg.openZoneChance=0.10f; cfg.openZoneSize=2; cfg.pitChance=0.12f; cfg.pillarChance=0.10f; cfg.doubleHeightChance=0.00f; cfg.interLayerConnection=0.12f; break;
                default: cfg.wallDensity=0.75f; cfg.corridorWidth=1; cfg.openZoneChance=0.05f; cfg.openZoneSize=2; cfg.pitChance=0.15f; cfg.pillarChance=0.05f; cfg.doubleHeightChance=0.00f; cfg.interLayerConnection=0.05f; break;
            }
            return cfg;
        }
    }
}
