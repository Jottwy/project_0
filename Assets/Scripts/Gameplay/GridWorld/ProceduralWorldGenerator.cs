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
        [Range(0f, 1f)] public float wallDensity         = 0.50f;
        [Range(1, 2)]   public int   corridorWidth       = 1;
        [Range(0f, 1f)] public float openZoneChance      = 0.20f;
        [Range(2, 6)]   public int   openZoneSize        = 3;
        [Range(0f, 1f)] public float pitChance           = 0.08f;
        [Range(0f, 1f)] public float pillarChance        = 0.20f;
        [Range(0f, 1f)] public float doubleHeightChance  = 0.05f;
        [Range(0f, 1f)] public float interLayerConnection = 0.08f;
        // Aperture ratio range: how open the gaps in dividing walls are.
        [Range(0f, 1f)] public float minApertureRatio    = 0.30f;
        [Range(0f, 1f)] public float maxApertureRatio    = 0.70f;
    }

    // ─────────────────────────────────────────────────────────────────
    // StructureDefinition — parsed from default_structures.json
    // ─────────────────────────────────────────────────────────────────

    [Serializable]
    public sealed class StructureDefinition
    {
        public string     id;
        public float      probability;
        public int        layersTall = 1;
        public string[][] pattern;   // [row][col], row 0 = south
    }

    [Serializable]
    internal sealed class StructureLibraryJson
    {
        public StructureDefinition[] structures;
    }

    // ─────────────────────────────────────────────────────────────────
    // WorldGenerator — deterministic chunk generation
    // Principle: everything starts as open space; wall lines divide it.
    // ─────────────────────────────────────────────────────────────────

    public static class WorldGenerator
    {
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

        public static GridCell[] GenerateChunk(
            long seed, int chunkX, int chunkZ, int layer,
            LayerConfig cfg, HashSet<int> forcedWalkable = null)
        {
            LoadStructures();
            var rng = new System.Random(DeriveRngSeed(seed, chunkX, chunkZ, layer));

            // Step 1 — fill all Corridor (everything is open space by default)
            var tiles = new GridCellType[Tiles * Tiles];
            for (int i = 0; i < tiles.Length; i++) tiles[i] = GridCellType.Corridor;

            // Step 2 — place wall lines with apertures
            PlaceWallLines(rng, cfg, tiles);

            // Step 3 — superstructures
            var pitTiles = new HashSet<int>();
            TryPlaceStructures(rng, cfg, tiles, layer, pitTiles);

            // Step 4 — pits near walls
            PlacePits(rng, cfg, tiles, pitTiles);

            // Step 5 — pillars in open areas
            PlacePillars(rng, cfg, tiles);

            // Step 6 — fixed border connections (highest priority, last)
            GenerateBorderConnections(tiles);

            // Step 7 — forced walkable from layer above
            if (forcedWalkable != null)
                foreach (int idx in forcedWalkable)
                    if ((uint)idx < (uint)tiles.Length)
                        tiles[idx] = GridCellType.Corridor;

            return TilesToCells(tiles, pitTiles);
        }

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
                // layer intentionally ignored: every layer of a chunk shares the
                // same pattern so floors/ceilings/walls line up across the stack.
                long s = seed ^ ((long)cx * 73856093L) ^ ((long)cz * 19349663L);
                return (int)(s ^ (s >> 32));
            }
        }

        // ─── Wall line placement ──────────────────────────────────────

        private static void PlaceWallLines(System.Random rng, LayerConfig cfg, GridCellType[] tiles)
        {
            int numH = Mathf.RoundToInt(cfg.wallDensity * 8);
            int numV = Mathf.RoundToInt(cfg.wallDensity * 8);

            float minAp = cfg.minApertureRatio;
            float maxAp = Mathf.Max(minAp, cfg.maxApertureRatio);

            var hPos = PickWallPositions(rng, numH);
            var vPos = PickWallPositions(rng, numV);

            foreach (int pos in hPos)
            {
                float ap = (float)(minAp + rng.NextDouble() * (maxAp - minAp));
                PlaceWallLine(tiles, pos, horizontal: true, rng, ap);
            }
            foreach (int pos in vPos)
            {
                float ap = (float)(minAp + rng.NextDouble() * (maxAp - minAp));
                PlaceWallLine(tiles, pos, horizontal: false, rng, ap);
            }
        }

        // Valid interior range [2, Tiles-3], min separation 2 tiles from each other.
        private static List<int> PickWallPositions(System.Random rng, int numWalls)
        {
            var candidates = new List<int>(Tiles - 4);
            for (int i = 2; i <= Tiles - 3; i++) candidates.Add(i);
            Shuffle(candidates, rng);

            var chosen = new List<int>(numWalls);
            foreach (int c in candidates)
            {
                if (chosen.Count >= numWalls) break;
                bool tooClose = false;
                foreach (int p in chosen)
                    if (Mathf.Abs(c - p) < 2) { tooClose = true; break; }
                if (!tooClose) chosen.Add(c);
            }
            return chosen;
        }

        private static void PlaceWallLine(GridCellType[] tiles, int pos, bool horizontal,
            System.Random rng, float apertureRatio)
        {
            // Fill the whole line with Wall.
            for (int i = 0; i < Tiles; i++)
                tiles[LineIdx(horizontal, pos, i)] = GridCellType.Wall;

            // Carve apertures: minimum 2 open tiles, in pairs of 2 for passability.
            int openCount = Mathf.Clamp(Mathf.RoundToInt(Tiles * apertureRatio), 2, Tiles - 2);
            int pairs = openCount / 2;

            // Candidate pair-start positions: [0, Tiles-2]
            var candidates = new List<int>(Tiles - 1);
            for (int i = 0; i <= Tiles - 2; i++) candidates.Add(i);
            Shuffle(candidates, rng);

            var open = new bool[Tiles];
            int placed = 0;
            foreach (int start in candidates)
            {
                if (placed >= pairs) break;
                if (open[start] || open[start + 1]) continue;
                open[start] = open[start + 1] = true;
                placed++;
            }

            for (int i = 0; i < Tiles; i++)
                if (open[i])
                    tiles[LineIdx(horizontal, pos, i)] = GridCellType.Corridor;
        }

        // tile index for a point along a horizontal (tz=pos) or vertical (tx=pos) line.
        private static int LineIdx(bool horizontal, int pos, int i) =>
            horizontal ? pos * Tiles + i : i * Tiles + pos;

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
                        int idx = (anchorZ + r) * Tiles + (anchorX + c);
                        if ((uint)idx >= (uint)tiles.Length) continue;
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
            GridCellType[] tiles, HashSet<int> pitTiles)
        {
            for (int tz = 0; tz < Tiles; tz++)
            {
                for (int tx = 0; tx < Tiles; tx++)
                {
                    int idx = tz * Tiles + tx;
                    if (tiles[idx] == GridCellType.Wall) continue;
                    if (!IsAdjacentToWall(tiles, tx, tz)) continue;
                    if (rng.NextDouble() < cfg.pitChance)
                    {
                        tiles[idx] = GridCellType.Pit;
                        pitTiles.Add(idx);
                    }
                }
            }
        }

        private static bool IsAdjacentToWall(GridCellType[] tiles, int tx, int tz)
        {
            if (tx > 0        && tiles[tz * Tiles + (tx - 1)] == GridCellType.Wall) return true;
            if (tx < Tiles-1  && tiles[tz * Tiles + (tx + 1)] == GridCellType.Wall) return true;
            if (tz > 0        && tiles[(tz - 1) * Tiles + tx] == GridCellType.Wall) return true;
            if (tz < Tiles-1  && tiles[(tz + 1) * Tiles + tx] == GridCellType.Wall) return true;
            return false;
        }

        // ─── Pillars ──────────────────────────────────────────────────

        private static void PlacePillars(System.Random rng, LayerConfig cfg, GridCellType[] tiles)
        {
            for (int tz = 1; tz < Tiles - 1; tz++)
            {
                for (int tx = 1; tx < Tiles - 1; tx++)
                {
                    if (tiles[tz * Tiles + tx] == GridCellType.Wall) continue;
                    if ((tx + tz * 3) % 4 != 0) continue;
                    if (IsAdjacentToWall(tiles, tx, tz)) continue;
                    if (rng.NextDouble() < cfg.pillarChance)
                        tiles[tz * Tiles + tx] = GridCellType.Pillar;
                }
            }
        }

        // ─── Border connections ───────────────────────────────────────

        private static readonly int[] BorderSlots = { 1, 5, 9 };

        private static void GenerateBorderConnections(GridCellType[] tiles)
        {
            foreach (int pos in BorderSlots)
            {
                int p2 = Mathf.Min(pos + 1, Tiles - 1);

                ForceCorridor(tiles, pos, 0);  ForceCorridor(tiles, p2, 0);
                ForceCorridor(tiles, pos, 1);  ForceCorridor(tiles, p2, 1);

                ForceCorridor(tiles, pos, Tiles - 1); ForceCorridor(tiles, p2, Tiles - 1);
                ForceCorridor(tiles, pos, Tiles - 2); ForceCorridor(tiles, p2, Tiles - 2);

                ForceCorridor(tiles, 0, pos);  ForceCorridor(tiles, 0, p2);
                ForceCorridor(tiles, 1, pos);  ForceCorridor(tiles, 1, p2);

                ForceCorridor(tiles, Tiles - 1, pos);  ForceCorridor(tiles, Tiles - 1, p2);
                ForceCorridor(tiles, Tiles - 2, pos);  ForceCorridor(tiles, Tiles - 2, p2);
            }
        }

        private static void ForceCorridor(GridCellType[] tiles, int tx, int tz)
        {
            int idx = tz * Tiles + tx;
            if ((uint)idx < (uint)tiles.Length)
                tiles[idx] = GridCellType.Corridor;
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
                    for (int dz = 0; dz < 2; dz++)
                        for (int dx = 0; dx < 2; dx++)
                            cells[(tz * 2 + dz) * Cells + (tx * 2 + dx)] =
                                new GridCell(kind, ceil, 0);
                }
            }
            return cells;
        }

        // ─── Utility ──────────────────────────────────────────────────

        private static void Shuffle<T>(List<T> list, System.Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                var t = list[i]; list[i] = list[j]; list[j] = t;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // ChunkStreamer — manages a 3×3 ring of loaded chunks
    // ─────────────────────────────────────────────────────────────────

    public sealed class ChunkStreamer : MonoBehaviour
    {
        public long      seed       = 42;
        public int       layerCount = 4;
        public int       viewRadius = 1;
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

        private const float Side = GridConstants.ChunkCells * GridConstants.CellSize;

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

            var desired = new HashSet<(int, int, int)>();
            for (int dz = -viewRadius; dz <= viewRadius; dz++)
                for (int dx = -viewRadius; dx <= viewRadius; dx++)
                    for (int layer = 0; layer < layerCount; layer++)
                        desired.Add((cx + dx, cz + dz, layer));

            var toRemove = new List<(int, int, int)>();
            foreach (var key in _loaded.Keys)
                if (!desired.Contains(key)) toRemove.Add(key);
            foreach (var key in toRemove)
            {
                Destroy(_loaded[key]);
                _loaded.Remove(key);
            }

            for (int dz = -viewRadius; dz <= viewRadius; dz++)
                for (int dx = -viewRadius; dx <= viewRadius; dx++)
                    EnsureColumn(cx + dx, cz + dz);
        }

        private void EnsureColumn(int ccx, int ccz)
        {
            HashSet<int> forcedWalkable = null;
            var colCells = new GridCell[layerCount][];

            for (int layer = layerCount - 1; layer >= 0; layer--)
            {
                var key = (ccx, ccz, layer);
                if (!_cache.ContainsKey(key))
                {
                    // Always layer-0 config so every layer shares one pattern.
                    colCells[layer] = WorldGenerator.GenerateChunk(
                        seed, ccx, ccz, layer, GetConfig(0), forcedWalkable);
                    _cache[key] = colCells[layer];
                }
                else
                    colCells[layer] = _cache[key];

                forcedWalkable = WorldGenerator.GetPitCellIndices(colCells[layer]);
            }

            for (int layer = 0; layer < layerCount; layer++)
            {
                var key = (ccx, ccz, layer);
                if (_loaded.ContainsKey(key)) continue;

                var origin = new Vector3(ccx * Side, layer * GridConstants.LayerHeight, ccz * Side);
                var above  = layer + 1 < layerCount
                    ? _cache.GetValueOrDefault((ccx, ccz, layer + 1)) : null;
                var go = GridChunkBuilder.Build(colCells[layer], _prefabs, origin,
                    $"Chunk_L{layer}_{ccx}_{ccz}", above);
                go.transform.SetParent(transform, true);
                _loaded[key] = go;
            }
        }

        private LayerConfig GetConfig(int layer)
        {
            if (layerConfigs != null && layer < layerConfigs.Length && layerConfigs[layer] != null)
                return layerConfigs[layer];
            return DefaultConfig(layer);
        }

        private static LayerConfig DefaultConfig(int layer)
        {
            // All layers share layer-0 settings for now: dense walls + narrow
            // apertures for an oppressive Backrooms feel.
            var cfg = ScriptableObject.CreateInstance<LayerConfig>();
            cfg.wallDensity=0.75f; cfg.corridorWidth=1; cfg.openZoneChance=0.35f;
            cfg.openZoneSize=4;    cfg.pitChance=0.02f; cfg.pillarChance=0.30f;
            cfg.doubleHeightChance=0.10f; cfg.interLayerConnection=0.08f;
            cfg.minApertureRatio=0.25f;   cfg.maxApertureRatio=0.45f;
            return cfg;
        }
    }
}
