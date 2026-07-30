using System.Collections.Generic;
using BackroomsSurvival.Gameplay;
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
        public GameObject floorSlab;
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
                floorSlab = Resources.Load<GameObject>("GridPrefabs/FloorSlab"),
                wall = Resources.Load<GameObject>("GridPrefabs/Wall"),
                pillar = Resources.Load<GameObject>("GridPrefabs/Pillar"),
                voidEdge = Resources.Load<GameObject>("GridPrefabs/VoidEdge"),
                wallMaterial = Resources.Load<Material>("GridMaterials/GridWall"),
                ceilingMaterial = Resources.Load<Material>("GridMaterials/GridCeiling"),
            };
            if (set.floor == null)
                Debug.LogError("[GridPrefabSet] GridPrefabs not found in Resources. " +
                               "Run Backrooms/Create Grid Prefabs first.");
            // FloorSlab is the shared floor/ceiling plane; fall back to Floor so
            // the build still runs before Create Grid Prefabs is re-run.
            if (set.floorSlab == null)
                set.floorSlab = set.floor;

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
    /// Edge-based visual construction for one chunk: 10×10 render tiles of 5 m,
    /// every tile floored, walls as independent 5×4×0.2 prefab pieces on tile
    /// EDGES (one piece, one wall, like LEGO).
    ///
    /// Fase 4.2: the local-generation path (Build(ChunkData)) was removed; chunks
    /// now come from the backend grid_gen bitmask via <see cref="BuildFromWalls"/>.
    /// No-duplication rule: each tile emits only its +Z and +X panels; the −Z/−X
    /// panels belong to the neighbour tile/chunk, so every edge renders once.
    ///
    /// <see cref="ClassifyTile"/> (cells → TileClass) is retained for the
    /// classification unit tests; it is not used by the runtime render path.
    /// </summary>
    public static class GridChunkBuilder
    {
        private const float Ch = GridVisualConstants.CellHeight;
        private const float Ts = GridVisualConstants.TileSize;
        private const float LayerHeight = GridConstants.LayerHeight;
        // Authored height of the Wall prefab (GridPrefabCreator.BuildWall: a 5 × 4 × 0.2
        // box whose pivot sits on the floor). Any partial-height wall variant scales the
        // root by (wanted / this), which drags the runtime BoxCollider along with it.
        private const float WallPrefabHeight = 2f * Ch;
        private const int Size = GridConstants.ChunkCells;
        // A render tile is a 2×2 block of cells → 10×10 tiles per chunk.
        private const int Tiles = Size / 2;

        // Tile-edge flags: which side of a tile a wall or lip sits on.
        public const byte EdgeSouth = 1; // -z
        public const byte EdgeNorth = 2; // +z
        public const byte EdgeWest  = 4; // -x
        public const byte EdgeEast  = 8; // +x

        // ADR-033/Pillar (enmienda "Opción (c)"): nibble alto de walls[tx,tz] —
        // qué sub-celda de 2.5 m del tile de 5 m es una columna. Mapeo fijado en
        // docs/DECISIONS.md; NO cambiar sin nueva enmienda. Espejo exacto de
        // PILLAR_NW/NE/SW/SE en backend/src/world/grid_gen/tile_walls.rs.
        public const byte PillarNW = 0x10; // (x0, z0)
        public const byte PillarNE = 0x20; // (x1, z0)
        public const byte PillarSW = 0x40; // (x0, z1)
        public const byte PillarSE = 0x80; // (x1, z1)
        public const byte PillarMask = PillarNW | PillarNE | PillarSW | PillarSE;

        // Sub-cell table: flag → fractional offset within the tile (0 = west/north
        // edge of the tile, 1 = east/south edge). SubCellCenter() converts this to
        // a local position centred on the 2.5 m sub-cell.
        private static readonly (byte flag, float sx, float sz)[] PillarSubCellTable =
        {
            (PillarNW, 0f, 0f),
            (PillarNE, 1f, 0f),
            (PillarSW, 0f, 1f),
            (PillarSE, 1f, 1f),
        };

        // Wall pieces: edge flag → local offset (× TileSize) + yaw. The Wall
        // prefab runs along X (yaw 0 → N/S edges); yaw 90 turns it along Z.
        private static readonly (byte flag, float ox, float oz, float yaw)[] WallEdgeTable =
        {
            (EdgeSouth, 0f, -0.5f, 0f),
            (EdgeNorth, 0f, 0.5f, 0f),
            (EdgeWest, -0.5f, 0f, 90f),
            (EdgeEast, 0.5f, 0f, 90f),
        };

        // Fase 5A (Bug #1) — per-layer light isolation. Each macro-layer paints its
        // whole chunk onto a distinct Unity layer; a layer's lamps then illuminate only
        // that layer's geometry (via Light.cullingMask) so point lights never bleed
        // through the thin floor/ceiling slabs into the stacked layers above/below.
        // These 4 Unity layers are ALREADY in the player camera's cullingMask AND the
        // STP motor's collision mask, so reusing them keeps render + grounding intact
        // with zero vendor edits (Default, StaticObject, DynamicObject, Building).
        public static readonly int[] GeoLayers = { 0, 14, 15, 16 };

        /// <summary>The Unity layer a macro-layer's geometry/lamps live on (wraps mod N).</summary>
        public static int GeoLayer(int worldLayer)
        {
            int n = GeoLayers.Length;
            return GeoLayers[((worldLayer % n) + n) % n];
        }

        /// <summary>Bitmask of every per-layer geometry layer (the set GeoLayer can return).</summary>
        public static readonly int GeoMask = BuildGeoMask();

        private static int BuildGeoMask()
        {
            int m = 0;
            foreach (int l in GeoLayers) m |= 1 << l;
            return m;
        }

        /// <summary>Set <paramref name="go"/> and all descendants to <paramref name="layer"/>.</summary>
        public static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            var t = go.transform;
            for (int i = 0; i < t.childCount; i++)
                SetLayerRecursively(t.GetChild(i).gameObject, layer);
        }


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

        /// <summary>
        /// Fase 4.1 — build a chunk from the backend grid_gen tile-wall bitmask
        /// (ServerMessage::ChunkData). Every tile is floored (a roof slab on the top
        /// layer only); walls come straight from
        /// <paramref name="walls"/>[tx,tz] in the BACKEND convention
        /// N=1(−Z) S=2(+Z) E=4(+X) W=8(−X). Since ADR-033/Pillar (enmienda "Opción
        /// (c)") the HIGH nibble additionally marks which of the tile's 4 sub-cells
        /// is a column (<see cref="PillarNW"/>/<see cref="PillarNE"/>/
        /// <see cref="PillarSW"/>/<see cref="PillarSE"/>) — everything else (voids,
        /// shafts, non-pillar cell content) is still NOT representable in this
        /// payload.
        ///
        /// No-duplication rule: each tile emits only the
        /// edges it OWNS — its +Z and +X panels. The shared edge is symmetric in the
        /// bitmask (a tile's +Z bit equals its +Z-neighbour's −Z bit, guaranteed by the
        /// backend crossing rule), so the −Z/−X panels are emitted by the neighbour
        /// tile/chunk → every physical edge renders exactly once.
        ///
        /// Bit translation (backend convention → this builder's internal flags):
        ///   backend S (2, +Z) → EdgeNorth (this tile's +z panel)
        ///   backend E (4, +X) → EdgeEast  (this tile's +x panel)
        ///
        /// Pillars have no such translation: each high-nibble bit already names its
        /// own sub-cell directly (NW/NE/SW/SE), unambiguous per tile — no neighbour
        /// shares or owns a sub-cell, so there is no duplication rule to apply.
        /// </summary>
        public static GameObject BuildFromWalls(byte[,] walls, GridPrefabSet prefabs,
            Vector3 origin, string name, int layerIndex, int layerCount,
            LayerVisualConfig cfg = null, LayerVisualMaterials mats = null,
            int chunkX = 0, int chunkZ = 0)
        {
            var root = new GameObject(name);
            root.transform.position = origin;
            bool isTopLayer = layerIndex >= layerCount - 1;
            // Fase 5A: "styled" = a layer visual config + its shared materials.
            bool styled = cfg != null && mats != null;
            // Zone-kind first pass: multiplied into the layer's floor/wall/ceiling tint
            // below. ZoneRegistry is keyed by XZ chunk coord only (zone_kind ignores
            // vertical layer) — white ("no change") when the chunk hasn't been seen yet
            // (e.g. first frame of a fresh request) or carries no zone data.
            Color zoneTint = styled && ZoneRegistry.TryGetZone(chunkX, chunkZ, out byte zoneKind)
                ? cfg.ZoneTint(zoneKind)
                : Color.white;
            // When the layer draws its own per-tile ceiling, the top-layer roof slab is
            // redundant (coplanar with the ceiling) → suppress it to avoid z-fighting.
            bool roofSlab = isTopLayer && !(styled && cfg.showCeiling);

            for (int tz = 0; tz < Tiles; tz++)
            {
                for (int tx = 0; tx < Tiles; tx++)
                {
                    // One deterministic RNG per tile drives the ±8% HSV-Value jitter so
                    // surface tints vary tile-to-tile WITHOUT a material per tile.
                    System.Random rng = styled ? new System.Random(TileSeed(chunkX, chunkZ, tx, tz)) : null;

                    // Pieza C — per-tile tint palette. Pure hashes in GLOBAL tile coords
                    // (so a palette choice tiles across chunk seams), with their own salts
                    // so they never perturb the jitter rng draw sequence — same discipline
                    // as CeilingHash/MoistureAt. Unauthored palette ⇒ the layer's flat tint.
                    int gx = chunkX * Tiles + tx, gz = chunkZ * Tiles + tz;
                    Color floorBase = styled ? cfg.FloorTintFor(Hash01(gx, gz, TintSaltFloor)) : Color.white;
                    Color wallBase  = styled ? cfg.WallTintFor(Hash01(gx, gz, TintSaltWall))   : Color.white;

                    // Pieza F — damp patches on floor and walls, not just the ceiling.
                    // Own salt pair per surface, so the three fields are uncorrelated.
                    bool floorDamp = styled && MoistureAt(chunkX, chunkZ, tx, tz,
                        MoistSaltFloorCell, MoistSaltFloorJit) < FloorStainThreshold;
                    bool wallDamp = styled && MoistureAt(chunkX, chunkZ, tx, tz,
                        MoistSaltWallCell, MoistSaltWallJit) < WallStainThreshold;

                    // Floor of this layer == ceiling of the layer below (one slab).
                    var floorGo = Instantiate(prefabs.floorSlab, root.transform, TileCenter(tx, tz), 0f);
                    AddColliderIfMissing(floorGo);
                    if (styled)
                    {
                        // Stain multiplies AFTER the jitter, mirroring PlaceCeilingTile.
                        Color t = JitterValue(floorBase * zoneTint, rng);
                        if (floorDamp) t *= FloorStain;
                        Paint(floorGo, mats.floor, t);
                    }

                    if (roofSlab)
                        PlaceFloorSlab(prefabs, root.transform, tx, tz, LayerHeight);

                    // Per-tile ceiling with Fase 5B procedural variety (panel type + moisture
                    // stains). Variety comes from a pure hash so the floor/wall jitter (rng)
                    // is untouched; the ceiling tint still draws one rng value inside so the
                    // draw sequence — and thus floor/wall shades — stays byte-identical.
                    if (styled && cfg.showCeiling)
                        PlaceCeilingTile(prefabs, root.transform, tx, tz, mats, cfg, chunkX, chunkZ, rng, zoneTint);

                    byte b = walls[tx, tz];
                    byte edges = 0;
                    if ((b & 2) != 0) edges |= EdgeNorth; // backend S (+Z) → +z panel
                    if ((b & 4) != 0) edges |= EdgeEast;  // backend E (+X) → +x panel
                    if (edges != 0)
                    {
                        if (styled) PlaceWallsTinted(prefabs, root.transform, edges, tx, tz, mats.wall,
                            Damp(JitterValue(wallBase * zoneTint, rng), wallDamp, WallStain),
                            cfg, gx, gz);
                        else PlaceWalls(prefabs, root.transform, edges, tx, tz);
                    }

                    // ADR-033/Pillar: nibble alto → columnas por sub-celda. Sin
                    // traducción de bits (a diferencia de N/S/E/W): cada bit ya
                    // nombra su propia sub-celda, sin reparto con el vecino.
                    byte pillarBits = (byte)(b & PillarMask);
                    if (pillarBits != 0 && prefabs.pillar != null)
                    {
                        // Primer pase (decisión explícita): mats.wall, SIN material
                        // propio de pilar — pendiente de pasada de arte separada.
                        Color pillarTint = styled
                            ? Damp(JitterValue(wallBase * zoneTint, rng), wallDamp, WallStain)
                            : Color.white;
                        PlacePillars(prefabs, root.transform, pillarBits, tx, tz,
                            styled ? mats.wall : null, pillarTint);
                    }
                    else if (pillarBits != 0 && prefabs.pillar == null && !_loggedMissingPillarPrefab)
                    {
                        // Sin fallback (a diferencia de floorSlab→floor): un chunk
                        // sin este prefab simplemente no dibuja columnas — nunca
                        // NullReference. Logueado una sola vez para no ahogar la
                        // consola en un mundo con muchos tiles PILLAR_HALL.
                        _loggedMissingPillarPrefab = true;
                        Debug.LogError("[GridChunkBuilder] GridPrefabs/Pillar no encontrado en Resources — " +
                                       "las columnas de ADR-033 no se dibujarán. Ejecuta Backrooms/Create Grid Prefabs.");
                    }
                }
            }

            // Fase 5B (Slice 2): overhead pipes per chunk (Layers 1–2 via cfg.ceilingPipes).
            if (styled && cfg.ceilingPipes)
                PlacePipes(root.transform, cfg, mats, chunkX, chunkZ);

            // Fase 5C: procedural props (placeholders) per tile.
            if (styled && cfg.props != null && cfg.props.Length > 0)
                PlaceProps(root.transform, walls, cfg, mats, chunkX, chunkZ);

            // Fase 5A (Bug #1): tag the whole chunk to its macro-layer's Unity layer so
            // per-layer lamp culling isolates it (see GeoLayers). Lamps/luminaires added
            // afterwards by BackroomsLighting set their own layer. Pipes (above) are children
            // of root, so they inherit the layer here too — lit only by this layer's lamps.
            SetLayerRecursively(root, GeoLayer(layerIndex));
            return root;
        }

        private static Vector3 TileCenter(int tx, int tz) =>
            new Vector3((tx + 0.5f) * Ts, 0f, (tz + 0.5f) * Ts);

        /// <summary>
        /// Center of one 2.5 m sub-cell within tile (tx,tz). (sx,sz) ∈ {0,1}²:
        /// 0 = west/north half of the tile, 1 = east/south half — matches
        /// <see cref="PillarSubCellTable"/> and the backend's (x0,z0)/(x1,z1)
        /// convention (tile_walls.rs). Equivalent to TileCenter(tx,tz) offset by
        /// ±1.25 m per axis.
        /// </summary>
        private static Vector3 SubCellCenter(int tx, int tz, float sx, float sz) =>
            TileCenter(tx, tz) + new Vector3(
                (sx - 0.5f) * GridConstants.CellSize,
                0f,
                (sz - 0.5f) * GridConstants.CellSize);

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

        /// <summary>Shared floor/ceiling slab at local height <paramref name="localY"/>.</summary>
        private static void PlaceFloorSlab(GridPrefabSet prefabs, Transform parent,
            int tx, int tz, float localY)
            => AddColliderIfMissing(Instantiate(prefabs.floorSlab, parent,
                TileCenter(tx, tz) + new Vector3(0f, localY, 0f), 0f));

        /// <summary>Independent 5×4×0.2 wall pieces on the flagged tile edges.</summary>
        private static void PlaceWalls(GridPrefabSet prefabs, Transform parent,
            byte edges, int tx, int tz)
        {
            foreach (var (flag, ox, oz, yaw) in WallEdgeTable)
                if ((edges & flag) != 0)
                    AddColliderIfMissing(Instantiate(prefabs.wall, parent,
                        TileCenter(tx, tz) + new Vector3(ox * Ts, 0f, oz * Ts), yaw));
        }

        // Logged once per session (not once per tile/chunk) so a world with many
        // PILLAR_HALL chunks doesn't flood the console — see the call site in
        // BuildFromWalls.
        private static bool _loggedMissingPillarPrefab;

        /// <summary>
        /// ADR-033/Pillar: one <c>prefabs.pillar</c> instance per flagged sub-cell,
        /// centred via <see cref="SubCellCenter"/>. RENDER ONLY — the prefab itself
        /// carries no collider (authored that way in GridPrefabCreator.BuildPillar;
        /// collision stays exclusively in Rust, unaffected by this commit). Caller
        /// guarantees <c>prefabs.pillar != null</c>.
        /// </summary>
        private static void PlacePillars(GridPrefabSet prefabs, Transform parent,
            byte pillarBits, int tx, int tz, Material mat, Color tint)
        {
            foreach (var (flag, sx, sz) in PillarSubCellTable)
            {
                if ((pillarBits & flag) == 0) continue;
                var go = Instantiate(prefabs.pillar, parent, SubCellCenter(tx, tz, sx, sz), 0f);
                if (mat != null) Paint(go, mat, tint);
            }
        }

        // ── Fase 5A — styled (per-layer) variants ──────────────────────────────

        // Reused across all Paint() calls — SetPropertyBlock copies the data, so a
        // single shared block avoids a per-tile allocation.
        private static readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();

        // ── Medias paredes — knee walls ─────────────────────────────────────────

        // Salts for the wall-variety hashes. One per ORIENTATION LANE, not per edge
        // flag: a panel running along X and one running along Z on the same tile are
        // different physical walls and must decide independently. (S shares N's salt
        // and W shares E's because the runtime only ever emits the N/E panels — the
        // no-duplication rule of BuildFromWalls — so the two never coexist on a tile.)
        // Values distinct from every TintSalt*/MoistSalt*/PropSalt* in this file.
        private const uint WallSaltKneeN = 0x4B4E574EU; // "KNWN" — knee, X-running lane
        private const uint WallSaltKneeE = 0x4B4E5745U; // "KNWE" — knee, Z-running lane

        private static uint KneeSaltFor(byte flag) =>
            (flag == EdgeNorth || flag == EdgeSouth) ? WallSaltKneeN : WallSaltKneeE;

        /// <summary>
        /// Wall pieces using the shared layer material + a per-tile tint (MPB).
        /// <paramref name="cfg"/>.wallPanelVariety turns a share of the panels into KNEE
        /// WALLS (partial height, see-over) chosen by a pure hash of the GLOBAL tile coords
        /// — never by <c>rng</c>, so the tint draw sequence of Piezas A-F is untouched.
        /// The knee wall stays untraversable by construction (see
        /// <see cref="LayerVisualConfig.MinKneeWallHeight"/>): the runtime BoxCollider
        /// scales with the transform, so the collider matches the visual exactly — no
        /// invisible full-height barrier, and no new hole either.
        /// </summary>
        private static void PlaceWallsTinted(GridPrefabSet prefabs, Transform parent,
            byte edges, int tx, int tz, Material mat, Color tint,
            LayerVisualConfig cfg, int gx, int gz)
        {
            float variety = cfg != null ? Mathf.Clamp01(cfg.wallPanelVariety) : 0f;
            float kneeScale = cfg != null ? cfg.KneeWallHeightClamped / WallPrefabHeight : 1f;

            foreach (var (flag, ox, oz, yaw) in WallEdgeTable)
                if ((edges & flag) != 0)
                {
                    var go = Instantiate(prefabs.wall, parent,
                        TileCenter(tx, tz) + new Vector3(ox * Ts, 0f, oz * Ts), yaw);
                    if (variety > 0f && Hash01(gx, gz, KneeSaltFor(flag)) < variety)
                        go.transform.localScale = new Vector3(1f, kneeScale, 1f);
                    AddColliderIfMissing(go);
                    Paint(go, mat, tint);
                }
        }

        /// <summary>Assign the shared material to every MeshRenderer and override
        /// <c>_BaseColor</c> per-renderer with <paramref name="tint"/> via a property
        /// block — no material instance is created.</summary>
        private static void Paint(GameObject go, Material mat, Color tint)
        {
            _mpb.Clear();
            _mpb.SetColor(LayerVisualMaterials.BaseColorId, tint);
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
            {
                if (mat != null) r.sharedMaterial = mat;
                r.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>Jitter the HSV Value of <paramref name="baseColor"/> by ±8%
        /// (deterministic via <paramref name="rng"/>) to break tile uniformity.</summary>
        private static Color JitterValue(Color baseColor, System.Random rng)
        {
            if (rng == null) return baseColor;
            Color.RGBToHSV(baseColor, out float h, out float s, out float v);
            float k = 1f + (float)(rng.NextDouble() * 2.0 - 1.0) * 0.08f;
            var c = Color.HSVToRGB(h, Mathf.Clamp01(s), Mathf.Clamp01(v * k));
            c.a = baseColor.a;
            return c;
        }

        /// <summary>Pieza F — <paramref name="tint"/> darkened by <paramref name="stain"/>
        /// when <paramref name="damp"/>. Kept as a one-liner so the wall and pillar call
        /// sites can stay single expressions and not disturb the rng draw order.</summary>
        private static Color Damp(Color tint, bool damp, Color stain) => damp ? tint * stain : tint;

        /// <summary>Deterministic per-tile seed (no UnityEngine.Random).</summary>
        private static int TileSeed(int cx, int cz, int tx, int tz)
        {
            unchecked { return cx * 73856093 ^ cz * 19349663 ^ tx * 83492791 ^ tz; }
        }

        // Pieza C — per-tile tint palette salts. One per surface role so floor, wall
        // and ceiling pick INDEPENDENTLY (a shared salt would make all three switch
        // shade on the same tiles, reading as a grid of coloured boxes).
        private const uint TintSaltFloor   = 0x46544E54U; // "FTNT"
        private const uint TintSaltWall    = 0x57544E54U; // "WTNT"
        private const uint TintSaltCeiling = 0x43544E54U; // "CTNT"

        // ── Fase 5B — procedural ceiling variety ────────────────────────────────

        // Damp-grey multiplier for moisture-stained ceiling tiles.
        private static readonly Color MoistureStain = new Color(0.7f, 0.68f, 0.6f);

        /// <summary>
        /// Per-tile ceiling with variety: normal / sunken / absent / dropped panels plus
        /// moisture stains. Panel type and geometry come from a deterministic hash of
        /// (chunk, tile) — NOT from <paramref name="rng"/> — so floor/wall jitter is
        /// unaffected. One ceiling-tint rng value is still drawn here (unconditionally) to
        /// keep the original draw sequence. <c>ceilingPanelVariety</c> scales the share of
        /// non-normal panels in a fixed 3:2:1 (sunken:absent:dropped) ratio.
        /// </summary>
        private static void PlaceCeilingTile(GridPrefabSet prefabs, Transform parent,
            int tx, int tz, LayerVisualMaterials mats, LayerVisualConfig cfg,
            int chunkX, int chunkZ, System.Random rng, Color zoneTint)
        {
            CeilingHash(chunkX, chunkZ, tx, tz, out float hType, out float hDrop,
                out float hTilt, out float hYaw);

            // Base tint (still draws one rng value so floor/wall shades stay identical),
            // darkened where moisture clusters.
            Color tint = JitterValue(
                cfg.CeilingTintFor(Hash01(chunkX * Tiles + tx, chunkZ * Tiles + tz, TintSaltCeiling))
                * zoneTint, rng);
            if (MoistureAt(chunkX, chunkZ, tx, tz, MoistSaltCeilCell, MoistSaltCeilJit) < 0.20f)
                tint *= MoistureStain;

            float v = Mathf.Clamp01(cfg.ceilingPanelVariety);
            float pSunken  = v * 0.5f;
            float pAbsent  = pSunken + v * (1f / 3f);
            float pDropped = pAbsent + v * (1f / 6f);

            if (hType < pSunken)
            {
                // Sunken panel: dips down into the room, slightly darker.
                var go = Instantiate(prefabs.floorSlab, parent,
                    TileCenter(tx, tz) + new Vector3(0f, LayerHeight - 0.1f, 0f), 0f);
                Paint(go, mats.ceiling, tint * 0.8f);
            }
            else if (hType < pAbsent)
            {
                // "Absent": a near-black panel in place — guaranteed black, rather than
                // revealing the dim underside of the floor above.
                var go = Instantiate(prefabs.floorSlab, parent,
                    TileCenter(tx, tz) + new Vector3(0f, LayerHeight, 0f), 0f);
                Paint(go, mats.ceiling, new Color(0.05f, 0.05f, 0.05f, 1f));
            }
            else if (hType < pDropped)
            {
                // Dropped panel: half-size slab tilted 15–30°, hanging below the ceiling.
                float tilt = 15f + hTilt * 15f;
                float drop = 0.3f + hDrop * 0.3f;
                var go = Instantiate(prefabs.floorSlab, parent,
                    TileCenter(tx, tz) + new Vector3(0f, LayerHeight - drop, 0f), 0f);
                go.transform.localScale    = new Vector3(0.5f, 1f, 0.5f);
                go.transform.localRotation = Quaternion.Euler(tilt, hYaw * 360f, 0f);
                Paint(go, mats.ceiling, tint * 0.85f);
            }
            else
            {
                // Normal panel.
                var go = Instantiate(prefabs.floorSlab, parent,
                    TileCenter(tx, tz) + new Vector3(0f, LayerHeight, 0f), 0f);
                Paint(go, mats.ceiling, tint);
            }
        }

        /// <summary>Deterministic per-tile hash → four floats in [0,1), independent of the
        /// jitter rng (so it never perturbs floor/wall tints).</summary>
        private static void CeilingHash(int cx, int cz, int tx, int tz,
            out float a, out float b, out float c, out float d)
        {
            unchecked
            {
                ulong h = 0x9E3779B97F4A7C15UL;
                h ^= (ulong)(uint)cx * 0xFF51AFD7ED558CCDUL; h ^= h >> 33;
                h ^= (ulong)(uint)cz * 0xC4CEB9FE1A85EC53UL; h ^= h >> 29;
                h ^= (ulong)(uint)tx * 0x165667B19E3779F9UL; h ^= h >> 32;
                h ^= (ulong)(uint)tz * 0x27D4EB2F165667C5UL; h ^= h >> 30;
                h *= 0x9E3779B185EBCA87UL; h ^= h >> 32;
                a = ((h      ) & 0xFFFF) / 65535f;
                b = ((h >> 16) & 0xFFFF) / 65535f;
                c = ((h >> 32) & 0xFFFF) / 65535f;
                d = ((h >> 48) & 0xFFFF) / 65535f;
            }
        }

        /// <summary>Moisture cluster value in [0,1): coarse 2-tile cells (in GLOBAL tile
        /// coords) make adjacent tiles share a base so stains cluster and tile across chunk
        /// seams; a per-tile jitter softens the block edges. Below the surface's threshold
        /// ⇒ stained. Pieza F: the salts are parameters so each surface gets its OWN damp
        /// field — sharing one would stack a floor stain under every ceiling stain, which
        /// reads as a lighting bug rather than water damage.</summary>
        private static float MoistureAt(int cx, int cz, int tx, int tz, uint saltCell, uint saltJit)
        {
            int gx = cx * Tiles + tx, gz = cz * Tiles + tz;
            float cell = Hash01(gx >> 1, gz >> 1, saltCell); // shared by a 2×2 block
            float jit  = Hash01(gx, gz, saltJit);            // per-tile
            return cell * 0.8f + jit * 0.2f;
        }

        // Ceiling salts keep their original literals so the ceiling damp field — already
        // confirmed in playtest — stays byte-identical to before Pieza F.
        private const uint MoistSaltCeilCell = 0x5BD1E995U;
        private const uint MoistSaltCeilJit  = 0x1B873593U;
        private const uint MoistSaltFloorCell = 0x464C4443U; // "FLDC"
        private const uint MoistSaltFloorJit  = 0x464C444AU; // "FLDJ"
        private const uint MoistSaltWallCell  = 0x574C4443U; // "WLDC"
        private const uint MoistSaltWallJit   = 0x574C444AU; // "WLDJ"

        // Damp multipliers per surface. Floor is the strongest (soaked carpet goes dark);
        // walls stain least (water runs down rather than pooling).
        private static readonly Color FloorStain = new Color(0.74f, 0.71f, 0.64f);
        private static readonly Color WallStain  = new Color(0.84f, 0.82f, 0.74f);
        private const float FloorStainThreshold = 0.22f;
        private const float WallStainThreshold  = 0.18f;

        /// <summary>Deterministic [0,1) hash of two ints under <paramref name="salt"/>.
        /// Shared with <see cref="World.BackroomsLighting"/> (Pieza D) so lamp placement
        /// and surface variety draw from the same generator instead of a near-copy that
        /// can drift.</summary>
        internal static float Hash01(int x, int y, uint salt)
        {
            unchecked
            {
                uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ salt;
                h ^= h >> 15; h *= 0x2C1B3C6DU; h ^= h >> 12; h *= 0x297A2D39U; h ^= h >> 15;
                return (h & 0xFFFFFFU) / 16777215f;
            }
        }

        // ── Fase 5B (Slice 2) — overhead pipes ──────────────────────────────────

        /// <summary>
        /// 1–2 decorative pipes spanning the chunk just under the ceiling. Deterministic per
        /// chunk; each pipe runs along X or Z at a random tile lane. Runtime Cube primitives
        /// (no new prefab), collider stripped (Rust owns collision), on the shared layer pipe
        /// material with a slight per-pipe value jitter. They are children of the chunk root,
        /// so <see cref="SetLayerRecursively"/> (called after) puts them on the chunk's Unity
        /// layer → lit only by this layer's lamps.
        /// </summary>
        private static void PlacePipes(Transform parent, LayerVisualConfig cfg,
            LayerVisualMaterials mats, int chunkX, int chunkZ)
        {
            var rng = new System.Random(PipeSeed(chunkX, chunkZ));
            if (rng.NextDouble() > cfg.ceilingPipeChance) return;

            float span = Tiles * Ts;             // full chunk side (50 m)
            float y    = LayerHeight - 0.25f;     // just under the ceiling, above the lamps
            int count  = 1 + (int)(rng.NextDouble() * 2); // 1 or 2

            for (int i = 0; i < count; i++)
            {
                bool alongX   = rng.NextDouble() > 0.5;
                int lane      = (int)(rng.NextDouble() * Tiles);   // 0..Tiles-1
                float lanePos = lane * Ts + Ts * 0.5f;             // tile centre on the cross axis

                Vector3 pos   = alongX ? new Vector3(span * 0.5f, y, lanePos)
                                       : new Vector3(lanePos, y, span * 0.5f);
                Vector3 scale = alongX ? new Vector3(span, 0.2f, 0.2f)
                                       : new Vector3(0.2f, 0.2f, span);

                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Pipe";
                go.transform.SetParent(parent, false);
                go.transform.localPosition = pos;
                go.transform.localScale    = scale;

                var col = go.GetComponent<Collider>();
                if (col != null) Object.Destroy(col); // decorative — Rust owns collision

                var rend = go.GetComponent<MeshRenderer>();
                rend.sharedMaterial = mats.pipe;
                // Slight per-pipe value jitter (±5%) so runs of pipes aren't identical.
                float v = 0.95f + (float)rng.NextDouble() * 0.1f;
                _mpb.Clear();
                _mpb.SetColor(LayerVisualMaterials.BaseColorId, new Color(0.25f * v, 0.25f * v, 0.27f * v, 1f));
                rend.SetPropertyBlock(_mpb);
            }
        }

        /// <summary>Deterministic per-chunk pipe seed ("PIP").</summary>
        private static int PipeSeed(int cx, int cz)
            => unchecked(cx * 92837111 ^ cz * 689287499 ^ 0x504950);

        // ── Fase 5C — procedural props ──────────────────────────────────────────

        private const float PropFloorY = 0.04f;          // floor slab top surface (half of 0.08)
        private const int   MaxPropsPerChunk = 12;        // perf cap (Fase 5C — option a)
        private const uint  PropSaltFine   = 0x50524F50U; // "PROP" — spawn (per-tile)
        private const uint  PropSaltCoarse = 0x434C5354U; // "CLST" — spawn (cluster cell)
        private const uint  PropSaltOrder  = 0x4F524452U; // "ORDR" — cap subsample
        private const uint  PropSaltPick   = 0x5049434BU; // "PICK" — weighted entry
        private const uint  PropSaltYaw    = 0x59415750U; // "YAWP" — yaw
        private const uint  PropSaltVarA   = 0x56415241U; // "VARA" — type variation (cable len)
        private const uint  PropSaltVarB   = 0x56415242U; // "VARB" — chair tip
        private const uint  PropSaltSide   = 0x53494445U; // "SIDE" — Pieza E, wall to hug

        // Pieza E — how far from the tile centre a wall-aligned prop backs off, in
        // metres. The wall panel sits at ±Ts/2 (2.5 m), so 1.9 leaves ~0.6 m for the
        // prop's own depth and keeps it from clipping through the panel.
        private const float PropHugInset = 1.9f;

        /// <summary>Offset direction and facing yaw per wall bit, indexed by BIT POSITION
        /// of the backend edge convention (0 = N/−Z, 1 = S/+Z, 2 = E/+X, 3 = W/−X). The
        /// prop backs against that wall and faces into the room.</summary>
        private static readonly (float ox, float oz, float yaw)[] WallHugTable =
        {
            ( 0f, -1f,   0f), // N (−Z): back to −Z, face +Z
            ( 0f,  1f, 180f), // S (+Z)
            ( 1f,  0f, 270f), // E (+X): back to +X, face −X
            (-1f,  0f,  90f), // W (−X)
        };

        // Reused scratch so the cap subsample allocates nothing per chunk.
        private static readonly List<(float key, int tx, int tz)> _propScratch
            = new List<(float, int, int)>();

        /// <summary>
        /// Place one prop per selected tile from <paramref name="cfg"/>.props. Spawn, cluster
        /// and within-tile choices are pure hashes of (chunk, tile) → deterministic, no order
        /// coupling. Skips fully-walled tiles and the reserved chunk-centre tile, caps the
        /// count per chunk (unbiased subset), and falls back to placeholder primitives when a
        /// PropEntry has no prefab. Props are children of the chunk root, so they inherit the
        /// chunk's Unity layer via SetLayerRecursively (called after) — lit only by this layer.
        /// </summary>
        private static void PlaceProps(Transform parent, byte[,] walls, LayerVisualConfig cfg,
            LayerVisualMaterials mats, int chunkX, int chunkZ)
        {
            float totalWeight = 0f;
            for (int i = 0; i < cfg.props.Length; i++)
                totalWeight += Mathf.Max(0f, cfg.props[i].spawnWeight);
            if (totalWeight <= 0f) return;

            float density = Mathf.Clamp01(cfg.propDensity);
            float bias    = Mathf.Clamp01(cfg.propClusterBias);
            int center    = Tiles / 2;

            // Pass 1: collect candidate tiles (spawn test + constraints). Cluster bias blends
            // per-tile noise (uniform) with coarse 2×2-cell noise (clustered).
            _propScratch.Clear();
            for (int tz = 0; tz < Tiles; tz++)
            {
                for (int tx = 0; tx < Tiles; tx++)
                {
                    if (tx == center && tz == center) continue;      // reserved centre tile
                    if ((walls[tx, tz] & 0x0F) == 0x0F) continue;    // fully enclosed / solid
                    // ADR-033/Pillar: a tile with ANY pillar sub-cell is excluded
                    // outright, not just the sub-cell itself — props spawn at
                    // TileCenter (whole-tile granularity), which would clip inside
                    // the column regardless of which of the 4 sub-cells it occupies.
                    if ((walls[tx, tz] & PillarMask) != 0) continue;

                    int gx = chunkX * Tiles + tx, gz = chunkZ * Tiles + tz;
                    float fine   = Hash01(gx, gz, PropSaltFine);
                    float coarse = Hash01(gx >> 1, gz >> 1, PropSaltCoarse);
                    if (Mathf.Lerp(fine, coarse, bias) >= density) continue;

                    _propScratch.Add((Hash01(gx, gz, PropSaltOrder), tx, tz));
                }
            }

            // Cap: keep an unbiased deterministic subset (sort by order hash, drop the rest).
            if (_propScratch.Count > MaxPropsPerChunk)
            {
                _propScratch.Sort((a, b) => a.key.CompareTo(b.key));
                _propScratch.RemoveRange(MaxPropsPerChunk, _propScratch.Count - MaxPropsPerChunk);
            }

            // Pass 2: place one prop per selected tile.
            for (int i = 0; i < _propScratch.Count; i++)
            {
                int tx = _propScratch[i].tx, tz = _propScratch[i].tz;
                int gx = chunkX * Tiles + tx, gz = chunkZ * Tiles + tz;

                PropEntry e = PickProp(cfg.props, Hash01(gx, gz, PropSaltPick) * totalWeight);
                string type = e.placeholderType;

                GameObject go = e.prefab != null
                    ? Object.Instantiate(e.prefab)
                    : PlaceholderFactory.Create(type, mats.prop, Hash01(gx, gz, PropSaltVarA));
                go.transform.SetParent(parent, false);

                // Y by kind: cables hang from the ceiling; flat decals lift slightly off the
                // floor; the rest sit on the floor surface.
                float y = PropFloorY;
                if (!e.floorOnly && type == "cable")          y = LayerHeight;
                else if (type == "paper" || type == "stain")  y = PropFloorY + 0.005f;
                go.transform.localPosition = TileCenter(tx, tz) + new Vector3(0f, y, 0f);

                // Yaw / tip.
                if (e.canBeRotated)
                {
                    float hYaw = Hash01(gx, gz, PropSaltYaw);
                    if (type == "chair")
                    {
                        float yaw = Mathf.Floor(hYaw * 4f) * 90f;                     // 0/90/180/270
                        float tip = Hash01(gx, gz, PropSaltVarB) < 0.30f ? 90f : 0f;  // 30% tipped over
                        go.transform.localRotation = Quaternion.Euler(0f, yaw, tip);
                    }
                    else
                    {
                        go.transform.localRotation = Quaternion.Euler(0f, hYaw * 360f, 0f);
                    }
                }
                else if (e.floorOnly)
                {
                    // Pieza E — wall-hugging. PropEntry.canBeRotated == false already
                    // MEANS "wall-aligned" per its own tooltip, but nothing ever aligned
                    // anything: those props sat at the tile centre with identity rotation,
                    // furniture floating in the middle of a corridor. Now a floor-standing
                    // wall-aligned prop backs against one of the tile's own walled sides.
                    //
                    // Uses the tile's FULL low nibble, not the two bits BuildFromWalls
                    // renders: N/W panels are drawn by the neighbouring tile (dedup), but
                    // the wall is physically there and this tile's bit says so.
                    byte sides = (byte)(walls[tx, tz] & 0x0F);
                    if (sides != 0)
                    {
                        int count = 0;
                        for (int b = 0; b < 4; b++)
                            if ((sides & (1 << b)) != 0) count++;
                        int pick = Mathf.Clamp((int)(Hash01(gx, gz, PropSaltSide) * count), 0, count - 1);
                        for (int b = 0; b < 4; b++)
                        {
                            if ((sides & (1 << b)) == 0) continue;
                            if (pick-- > 0) continue;
                            var hug = WallHugTable[b];
                            go.transform.localPosition += new Vector3(
                                hug.ox * PropHugInset, 0f, hug.oz * PropHugInset);
                            go.transform.localRotation = Quaternion.Euler(0f, hug.yaw, 0f);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>Weighted pick by cumulative spawnWeight; <paramref name="r"/> ∈ [0,total).</summary>
        private static PropEntry PickProp(PropEntry[] props, float r)
        {
            float acc = 0f;
            for (int i = 0; i < props.Length; i++)
            {
                acc += Mathf.Max(0f, props[i].spawnWeight);
                if (r < acc) return props[i];
            }
            return props[props.Length - 1];
        }
    }
}
