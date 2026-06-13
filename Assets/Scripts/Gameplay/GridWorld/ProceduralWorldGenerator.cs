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
        [Range(0f, 1f)] public float wallDensity      = 0.50f;
        [Range(0f, 1f)] public float openZoneChance  = 0.20f;
        [Range(2, 6)]   public int   openZoneSize    = 3;
        // Chance per chunk of a vertical shaft (a floorless tile that drops
        // through every layer). Must be uniform across layers for the shaft to
        // punch cleanly (see WorldGenerator.GenerateChunk).
        [Range(0f, 1f)] public float shaftChance     = 0.03f;
        [Range(0f, 1f)] public float pillarChance    = 0.20f;
        // Aperture ratio range: how open the gaps in dividing walls are.
        [Range(0f, 1f)] public float minApertureRatio = 0.30f;
        [Range(0f, 1f)] public float maxApertureRatio = 0.70f;
    }

    // ─────────────────────────────────────────────────────────────────
    // StructureDefinition — JSON schema for Resources/Structures/
    // default_structures.json. Kept as the authoring contract (validated by
    // BackroomsSurvival.EditorTools.StructureValidator). The edge-based
    // generator below does not place structures; the type documents the format.
    // ─────────────────────────────────────────────────────────────────

    [Serializable]
    public sealed class StructureDefinition
    {
        public string     id;
        public float      probability;
        public int        layersTall = 1;
        public string[][] pattern;   // [row][col], row 0 = south
    }

    // ─────────────────────────────────────────────────────────────────
    // Edge-based chunk data (Unity render layer — NOT the Rust wire format).
    //
    // Paradigm: every tile always has a floor and a ceiling. Walls are 0.2 m
    // panels that sit on the EDGES between tiles, not on tiles themselves.
    // A wall on the east edge of tile (x,z) is the same physical panel as the
    // west edge of tile (x+1,z); both flags are set, the builder emits it once.
    //
    // This lives entirely client-side. GridCellType.Wall and the GridChunkData
    // wire format (the Rust contract) are untouched: ChunkData.cells carries no
    // Wall cells, only Corridor/Pillar (and Pit/Void survive for ADR-008).
    // ─────────────────────────────────────────────────────────────────

    /// <summary>Which of a tile's four edges carry a wall panel.</summary>
    public struct TileWalls
    {
        public bool N, S, E, W;
        public bool Any => N || S || E || W;
    }

    /// <summary>One chunk: render cells (floor/ceiling/pillar) + edge walls.</summary>
    public struct ChunkData
    {
        public GridCell[]  cells;   // Cells×Cells, row-major. Corridor/Pillar only.
        public TileWalls[] walls;   // Tiles×Tiles edge flags.
        public bool[]      shafts;  // Tiles×Tiles. true = floorless vertical shaft.
    }

    // ─────────────────────────────────────────────────────────────────
    // WorldGenerator — deterministic edge-based chunk generation.
    // Principle: everything is open floored space; wall LINES (sequences of
    // shared tile edges) divide it into a labyrinth.
    // ─────────────────────────────────────────────────────────────────

    public static class WorldGenerator
    {
        private const int Cells = GridConstants.ChunkCells; // 20
        private const int Tiles = Cells / 2;                // 10

        // Fixed border slots where openings are guaranteed so neighbouring
        // chunks always connect (mirrors the seam contract of the old system).
        private static readonly int[] BorderSlots = { 1, 5, 9 };

        // Layer-independent salt for shaft RNG: the same column yields the same
        // shaft tile on every layer (distinct from any real layer index 0..3).
        private const int ShaftSalt = 0x5A;

        public static ChunkData GenerateChunk(
            long seed, int chunkX, int chunkZ, int layer, LayerConfig cfg)
        {
            var rng = new System.Random(DeriveRngSeed(seed, chunkX, chunkZ, layer));

            // Step 1 — no walls; every tile is open floored space.
            var walls = new TileWalls[Tiles * Tiles];

            int numLines = Mathf.RoundToInt(Mathf.Lerp(2f, 5f, Mathf.Clamp01(cfg.wallDensity)));
            float minAp = Mathf.Clamp01(cfg.minApertureRatio);
            float maxAp = Mathf.Clamp01(Mathf.Max(minAp, cfg.maxApertureRatio));

            // Steps 2 & 4 — horizontal wall lines (N/S edges), from south then north.
            PlaceHorizontalLines(rng, walls, numLines, minAp, maxAp, fromFar: false);
            PlaceHorizontalLines(rng, walls, numLines, minAp, maxAp, fromFar: true);
            // Steps 3 & 4 — vertical wall lines (E/W edges), from west then east.
            PlaceVerticalLines(rng, walls, numLines, minAp, maxAp, fromFar: false);
            PlaceVerticalLines(rng, walls, numLines, minAp, maxAp, fromFar: true);

            // Step 2.5 — carve open zones (before connectivity so the BFS sees them)
            PlaceOpenZones(rng, cfg, walls);

            // Step 6 — guaranteed border apertures (before connectivity so the
            // BFS sees the seams open).
            OpenBorderApertures(walls);

            // Step 5 — guarantee every tile is reachable from (0,0).
            EnsureConnectivity(walls);

            // Step 6.5 — vertical shafts. Seeded WITHOUT the layer index so the
            // same column picks the same shaft tile on every layer → a clean
            // hole that drops straight through. Opens the tile's walls so it is
            // fully exposed. (Replaces random Pits with designed verticality.)
            var shaftRng = new System.Random(DeriveRngSeed(seed, chunkX, chunkZ, ShaftSalt));
            var shafts = new bool[Tiles * Tiles];
            PlaceShafts(shaftRng, cfg, walls, shafts);

            // Cells: all corridor (floor + ceiling everywhere). Step 7 adds pillars.
            var cells = new GridCell[Cells * Cells];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = new GridCell(GridCellType.Corridor, 2, 0);
            PlacePillars(rng, cfg, walls, shafts, cells);

            return new ChunkData { cells = cells, walls = walls, shafts = shafts };
        }

        // ─── Seeding ──────────────────────────────────────────────────

        private static int DeriveRngSeed(long seed, int cx, int cz, int layer)
        {
            unchecked
            {
                long s = seed ^ ((long)cx * 73856093L) ^ ((long)cz * 19349663L) ^ ((long)layer * 2654435761L);
                return (int)(s ^ (s >> 32));
            }
        }

        // ─── Edge marking (symmetric: both sides of the shared edge) ─────

        // Wall on the south edge of tile (x,z) == north edge of tile (x,z-1).
        private static void MarkHWall(TileWalls[] w, int x, int z)
        {
            w[z * Tiles + x].S = true;
            if (z - 1 >= 0) w[(z - 1) * Tiles + x].N = true;
        }

        // Wall on the west edge of tile (x,z) == east edge of tile (x-1,z).
        private static void MarkVWall(TileWalls[] w, int x, int z)
        {
            w[z * Tiles + x].W = true;
            if (x - 1 >= 0) w[z * Tiles + (x - 1)].E = true;
        }

        // ─── Wall lines ──────────────────────────────────────────────

        // Horizontal line at tile row z (a run of south-edge walls along X) with
        // one passable aperture. fromFar runs it inward from the east border.
        private static void PlaceHorizontalLines(System.Random rng, TileWalls[] w,
            int count, float minAp, float maxAp, bool fromFar)
        {
            foreach (int z in PickLinePositions(rng, count))
            {
                if (z <= 0) continue; // needs row z-1 for the shared edge
                int span = rng.Next(5, Tiles + 1); // [5, 10]
                int startX = fromFar ? Mathf.Max(0, Tiles - span) : 0;
                int endX   = fromFar ? Tiles : Mathf.Min(span, Tiles);
                CarveLine(rng, minAp, maxAp, startX, endX,
                    (x) => MarkHWall(w, x, z));
            }
        }

        // Vertical line at tile column x (a run of west-edge walls along Z).
        private static void PlaceVerticalLines(System.Random rng, TileWalls[] w,
            int count, float minAp, float maxAp, bool fromFar)
        {
            foreach (int x in PickLinePositions(rng, count))
            {
                if (x <= 0) continue; // needs column x-1 for the shared edge
                int span = rng.Next(5, Tiles + 1);
                int startZ = fromFar ? Mathf.Max(0, Tiles - span) : 0;
                int endZ   = fromFar ? Tiles : Mathf.Min(span, Tiles);
                CarveLine(rng, minAp, maxAp, startZ, endZ,
                    (z) => MarkVWall(w, x, z));
            }
        }

        // Fill [start, end) with wall edges, leaving one random aperture so the
        // line is passable. Aperture width is a ratio of the line length.
        private static void CarveLine(System.Random rng, float minAp, float maxAp,
            int start, int end, Action<int> mark)
        {
            int len = end - start;
            if (len <= 0) return;

            float ratio = minAp + (float)rng.NextDouble() * (maxAp - minAp);
            int apLen   = Mathf.Clamp(Mathf.RoundToInt(len * ratio), 2, len - 1);
            int apStart = start + rng.Next(0, len - apLen + 1);

            for (int i = start; i < end; i++)
                if (i < apStart || i >= apStart + apLen)
                    mark(i);
        }

        // Interior positions [2, Tiles-2], min separation 2 between lines.
        private static List<int> PickLinePositions(System.Random rng, int count)
        {
            var candidates = new List<int>();
            for (int i = 2; i <= Tiles - 2; i++) candidates.Add(i);
            Shuffle(candidates, rng);

            var chosen = new List<int>(count);
            foreach (int c in candidates)
            {
                if (chosen.Count >= count) break;
                bool tooClose = false;
                foreach (int p in chosen)
                    if (Mathf.Abs(c - p) < 2) { tooClose = true; break; }
                if (!tooClose) chosen.Add(c);
            }
            return chosen;
        }

        // ─── Open zones ───────────────────────────────────────────────

        // Clear all interior edges of a random size×size tile rectangle, leaving
        // the zone's outer border walls intact. Creates the large open rooms
        // characteristic of Backrooms level 0.
        private static void PlaceOpenZones(System.Random rng, LayerConfig cfg, TileWalls[] walls)
        {
            if (rng.NextDouble() >= cfg.openZoneChance) return;
            int size = Mathf.Clamp(cfg.openZoneSize, 2, 6);
            int maxOx = Tiles - size - 1;
            int maxOz = Tiles - size - 1;
            if (maxOx < 1 || maxOz < 1) return;
            int ox = rng.Next(1, maxOx + 1);
            int oz = rng.Next(1, maxOz + 1);

            // Interior horizontal seams (N edge of row z = S edge of row z+1).
            for (int z = oz; z < oz + size - 1; z++)
                for (int x = ox; x < ox + size; x++)
                {
                    walls[z * Tiles + x].N = false;
                    walls[(z + 1) * Tiles + x].S = false;
                }

            // Interior vertical seams (E edge of col x = W edge of col x+1).
            for (int z = oz; z < oz + size; z++)
                for (int x = ox; x < ox + size - 1; x++)
                {
                    walls[z * Tiles + x].E = false;
                    walls[z * Tiles + (x + 1)].W = false;
                }
        }

        // ─── Border apertures ─────────────────────────────────────────

        // Open the fixed slots on the two seams this chunk owns: the north edge
        // of its top row and the east edge of its last column (the builder emits
        // only N/E walls — see GridChunkBuilder). The south/west seams are owned
        // and opened by the neighbouring chunks at the same slots, so passages
        // line up deterministically across the whole world.
        private static void OpenBorderApertures(TileWalls[] w)
        {
            foreach (int s in BorderSlots)
            {
                if (s >= Tiles) continue;
                w[(Tiles - 1) * Tiles + s].N = false; // north seam opening
                w[s * Tiles + (Tiles - 1)].E = false; // east seam opening
            }
        }

        // ─── Connectivity ─────────────────────────────────────────────

        // BFS over tiles (passable across an edge with no wall). While any tile
        // is unreachable from (0,0), find an isolated tile that borders the
        // reached region and knock out the wall between them. The reached set
        // grows by at least one each pass, so this ends in ≤ tile-count passes.
        private static void EnsureConnectivity(TileWalls[] w)
        {
            int guard = Tiles * Tiles + 1;
            while (guard-- > 0)
            {
                var reached = Reachable(w);
                if (!OpenFrontier(w, reached)) return; // all reached
            }
        }

        // Open one wall between an isolated tile and a reached neighbour.
        // Returns false when every tile is already reached.
        private static bool OpenFrontier(TileWalls[] w, bool[] reached)
        {
            for (int idx = 0; idx < reached.Length; idx++)
            {
                if (reached[idx]) continue;
                int x = idx % Tiles, z = idx / Tiles;
                if (z + 1 < Tiles && reached[(z + 1) * Tiles + x]) { ClearEdgeN(w, x, z); return true; }
                if (z - 1 >= 0    && reached[(z - 1) * Tiles + x]) { ClearEdgeS(w, x, z); return true; }
                if (x + 1 < Tiles && reached[z * Tiles + (x + 1)]) { ClearEdgeE(w, x, z); return true; }
                if (x - 1 >= 0    && reached[z * Tiles + (x - 1)]) { ClearEdgeW(w, x, z); return true; }
            }
            return false; // nothing isolated borders the reached set → fully connected
        }

        private static bool[] Reachable(TileWalls[] w)
        {
            var visited = new bool[Tiles * Tiles];
            var queue = new Queue<int>();
            visited[0] = true;
            queue.Enqueue(0);
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int x = idx % Tiles, z = idx / Tiles;
                var t = w[idx];
                if (!t.N && z + 1 < Tiles) Visit(visited, queue, x, z + 1);
                if (!t.S && z - 1 >= 0)    Visit(visited, queue, x, z - 1);
                if (!t.E && x + 1 < Tiles) Visit(visited, queue, x + 1, z);
                if (!t.W && x - 1 >= 0)    Visit(visited, queue, x - 1, z);
            }
            return visited;
        }

        private static void Visit(bool[] visited, Queue<int> queue, int x, int z)
        {
            int idx = z * Tiles + x;
            if (visited[idx]) return;
            visited[idx] = true;
            queue.Enqueue(idx);
        }

        private static void ClearEdgeN(TileWalls[] w, int x, int z)
        { w[z * Tiles + x].N = false; if (z + 1 < Tiles) w[(z + 1) * Tiles + x].S = false; }
        private static void ClearEdgeS(TileWalls[] w, int x, int z)
        { w[z * Tiles + x].S = false; if (z - 1 >= 0) w[(z - 1) * Tiles + x].N = false; }
        private static void ClearEdgeE(TileWalls[] w, int x, int z)
        { w[z * Tiles + x].E = false; if (x + 1 < Tiles) w[z * Tiles + (x + 1)].W = false; }
        private static void ClearEdgeW(TileWalls[] w, int x, int z)
        { w[z * Tiles + x].W = false; if (x - 1 >= 0) w[z * Tiles + (x - 1)].E = false; }

        // ─── Shafts ───────────────────────────────────────────────────

        // With probability shaftChance, mark one interior tile as a vertical
        // shaft and clear its four edges so the hole is fully exposed. Picked
        // from a layer-independent RNG so the same tile is a shaft in every
        // layer (uniform shaftChance required — DefaultConfig keeps it uniform).
        private static void PlaceShafts(System.Random rng, LayerConfig cfg,
            TileWalls[] walls, bool[] shafts)
        {
            if (rng.NextDouble() >= cfg.shaftChance) return;

            int tx = 1 + rng.Next(Tiles - 2);
            int tz = 1 + rng.Next(Tiles - 2);
            shafts[tz * Tiles + tx] = true;

            ClearEdgeN(walls, tx, tz);
            ClearEdgeS(walls, tx, tz);
            ClearEdgeE(walls, tx, tz);
            ClearEdgeW(walls, tx, tz);
        }

        // ─── Pillars ──────────────────────────────────────────────────

        // Pillars sit only in fully open tiles (no adjacent wall, not a shaft),
        // spaced out.
        private static void PlacePillars(System.Random rng, LayerConfig cfg,
            TileWalls[] walls, bool[] shafts, GridCell[] cells)
        {
            for (int tz = 1; tz < Tiles - 1; tz++)
            {
                for (int tx = 1; tx < Tiles - 1; tx++)
                {
                    int idx = tz * Tiles + tx;
                    if (shafts[idx]) continue;
                    if (walls[idx].Any) continue;
                    if ((tx + tz * 3) % 4 != 0) continue;
                    if (rng.NextDouble() >= cfg.pillarChance) continue;
                    int cx = tx * 2, cz = tz * 2;
                    cells[cz * Cells + cx] = new GridCell(GridCellType.Pillar, 2, 0);
                }
            }
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
        private readonly Dictionary<(int, int, int), ChunkData> _cache
            = new Dictionary<(int, int, int), ChunkData>();

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
                _cache.Remove(key);
            }

            for (int dz = -viewRadius; dz <= viewRadius; dz++)
                for (int dx = -viewRadius; dx <= viewRadius; dx++)
                    EnsureColumn(cx + dx, cz + dz);
        }

        private void EnsureColumn(int ccx, int ccz)
        {
            for (int layer = 0; layer < layerCount; layer++)
            {
                var key = (ccx, ccz, layer);
                if (!_cache.ContainsKey(key))
                    _cache[key] = WorldGenerator.GenerateChunk(
                        seed, ccx, ccz, layer, GetConfig(layer));
            }

            for (int layer = 0; layer < layerCount; layer++)
            {
                var key = (ccx, ccz, layer);
                if (_loaded.ContainsKey(key)) continue;

                var origin = new Vector3(ccx * Side, layer * GridConstants.LayerHeight, ccz * Side);
                var go = GridChunkBuilder.Build(_cache[key], _prefabs, origin,
                    $"Chunk_L{layer}_{ccx}_{ccz}", layer, layerCount);
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
            var cfg = ScriptableObject.CreateInstance<LayerConfig>();
            cfg.shaftChance = 0.03f; // uniform across layers so shafts punch through cleanly
            switch (layer)
            {
                case 0: // Most oppressive: dense walls, narrow passages
                    cfg.wallDensity=0.75f; cfg.minApertureRatio=0.25f; cfg.maxApertureRatio=0.40f;
                    cfg.openZoneChance=0.15f; cfg.openZoneSize=3; cfg.pillarChance=0.30f;
                    break;
                case 1:
                    cfg.wallDensity=0.65f; cfg.minApertureRatio=0.30f; cfg.maxApertureRatio=0.50f;
                    cfg.openZoneChance=0.25f; cfg.openZoneSize=4; cfg.pillarChance=0.25f;
                    break;
                case 2:
                    cfg.wallDensity=0.55f; cfg.minApertureRatio=0.35f; cfg.maxApertureRatio=0.55f;
                    cfg.openZoneChance=0.35f; cfg.openZoneSize=5; cfg.pillarChance=0.20f;
                    break;
                default: // Most open: unsettling emptiness of higher levels
                    cfg.wallDensity=0.45f; cfg.minApertureRatio=0.40f; cfg.maxApertureRatio=0.65f;
                    cfg.openZoneChance=0.50f; cfg.openZoneSize=6; cfg.pillarChance=0.15f;
                    break;
            }
            return cfg;
        }
    }
}
