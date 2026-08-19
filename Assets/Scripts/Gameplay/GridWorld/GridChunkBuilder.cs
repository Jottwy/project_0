using System.Collections.Generic;
using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
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
            if (set.wall == null)
                Debug.LogError("[GridPrefabSet] GridPrefabs/Wall not found in Resources. " +
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
    ///
    /// PARTICIÓN: los métodos viven repartidos entre este fichero y
    /// GridChunkBuilder.Placement.cs / .WallVariants.cs / .Tinting.cs / .Props.cs
    /// (misma carpeta). TODOS los campos estáticos permanecen AQUÍ, en su orden
    /// textual: el orden de inicialización de estáticos entre ficheros de una
    /// clase partial es indefinido en C# (GeoMask = BuildGeoMask() lee GeoLayers).
    /// </summary>
    public static partial class GridChunkBuilder
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
        // Public alias of Tiles: the single source of truth for "how many render tiles fit
        // along a chunk side", so callers outside the builder (ChunkStreamer, when it lights
        // a chunk) stop re-deriving ChunkCells / 2 on their own. Same compile-time value;
        // every internal use keeps reading Tiles, untouched.
        public const int TilesPerChunk = Tiles;

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
            int chunkX = 0, int chunkZ = 0, RoomZoneMsg[] roomZones = null,
            int buildRoomTileX = -1, int buildRoomTileZ = -1)
        {
            var root = new GameObject(name);
            root.transform.position = origin;
            bool isTopLayer = layerIndex >= layerCount - 1;
            // Fase 5A: "styled" = a layer visual config + its shared materials.
            bool styled = cfg != null && mats != null;
            // Superficie física del chunk: decide a qué suena cada paso que se dé aquí.
            // Se resuelve una vez por chunk y NO depende de `styled` — una capa sin
            // materiales de render sigue teniendo suelo que pisar.
            UseSurfacesOf(cfg);
            // Zone-kind first pass: multiplied into the layer's floor/wall/ceiling tint
            // below. ZoneRegistry is keyed by XZ chunk coord only (zone_kind ignores
            // vertical layer) — white ("no change") when the chunk hasn't been seen yet
            // (e.g. first frame of a fresh request) or carries no zone data.
            //
            // ADR-035: el mismo `zoneKind` alimenta ahora la elección de MODELO de pared,
            // así que se conserva en un local. `zoneKindQuery` es −1 cuando la zona no se
            // conoce todavía: un set de variantes específico de zona NO casa con eso (solo
            // uno comodín), de modo que el chunk cae al panel de siempre en vez de hornear
            // el modelo de una zona equivocada — misma degradación que ya toma su tinte.
            byte zoneKind = 0;
            bool zoneKnown = styled && ZoneRegistry.TryGetZone(chunkX, chunkZ, out zoneKind);
            Color zoneTint = zoneKnown ? cfg.ZoneTint(zoneKind) : Color.white;
            int zoneKindQuery = zoneKnown ? zoneKind : -1;
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
                    float wallMoist = MoistureAt(chunkX, chunkZ, tx, tz,
                        MoistSaltWallCell, MoistSaltWallJit);
                    bool wallDamp = styled && wallMoist < WallStainThreshold;
                    // El SEGUNDO tono lo elige la misma muestra de humedad, no un hash nuevo:
                    // así el tono fuerte cae siempre DENTRO del débil y la mancha tiene núcleo
                    // y halo, que es como se seca una filtración. Con un hash independiente
                    // saldrían dos parches sin relación y volvería a leerse como retícula. Sin
                    // draws de rng ni hashes extra: el orden de consumo no se mueve.
                    Color wallStain = wallMoist < WallStainDeepThreshold ? WallStainDeep : WallStain;

                    // Floor of this layer == ceiling of the layer below (one slab).
                    var floorGo = Instantiate(prefabs.floorSlab, root.transform, TileCenter(tx, tz), 0f);
                    // ESTE es el suelo que el jugador pisa de verdad — PlaceFloorSlab solo
                    // construye la losa de TECHO. Olvidar el material físico justo aquí dejó
                    // los pasos sonando al default del vendor mientras el resto del chunk ya
                    // estaba bien, o sea el arreglo entero sin el único sitio que se nota.
                    AddColliderIfMissing(floorGo, _floorPhys);
                    if (styled)
                    {
                        // Stain multiplies AFTER the jitter, mirroring PlaceCeilingTile.
                        Color t = JitterValue(floorBase * zoneTint, rng);
                        if (floorDamp) t *= FloorStain;
                        // ADR-081 enmienda 5: dentro de la habitación construible, material propio y
                        // plano. El jitter se calcula IGUAL aunque se descarte — consume su draw del
                        // `rng`, y saltárselo movería el tinte de todos los tiles siguientes del
                        // chunk (misma disciplina que PlaceLintels con su rng).
                        if (IsBuildRoomTile(tx, tz, buildRoomTileX, buildRoomTileZ))
                            Paint(floorGo, BuildRoomMaterial(), Color.white);
                        else
                            Paint(floorGo, mats.floor, t);
                    }

                    if (roofSlab)
                    {
                        var roofGo = PlaceFloorSlab(prefabs, root.transform, tx, tz, LayerHeight);
                        // Misma paleta base que el techo por tile: esta losa ES el techo de
                        // la capa superior cuando la capa no dibuja techo propio. Sin tinte
                        // por tile a propósito — no consume una draw del rng, así que los
                        // tintes de suelo/pared del tile no se mueven (misma disciplina que
                        // PlaceLintels).
                        if (styled)
                        {
                            if (IsBuildRoomTile(tx, tz, buildRoomTileX, buildRoomTileZ))
                                Paint(roofGo, BuildRoomMaterial(), Color.white);
                            else
                                Paint(roofGo, mats.ceiling, zoneTint);
                        }
                    }

                    // Per-tile ceiling with Fase 5B procedural variety (panel type + moisture
                    // stains). Variety comes from a pure hash so the floor/wall jitter (rng)
                    // is untouched; the ceiling tint still draws one rng value inside so the
                    // draw sequence — and thus floor/wall shades — stays byte-identical.
                    if (styled && cfg.showCeiling)
                        PlaceCeilingTile(prefabs, root.transform, tx, tz, mats, cfg, chunkX,
                            chunkZ, rng, zoneTint, zoneKindQuery);

                    byte b = walls[tx, tz];
                    byte edges = 0;
                    if ((b & BackendBitS) != 0) edges |= EdgeNorth; // backend S (+Z) → +z panel
                    if ((b & BackendBitE) != 0) edges |= EdgeEast;  // backend E (+X) → +x panel
                    if (edges != 0)
                    {
                        // ADR-035: `roomZones` baja entero, no un RoomType ya resuelto —
                        // el tipo se decide POR PANEL (ver RoomTypeForPanel): un panel vive
                        // en la frontera entre dos tiles, y los muros oeste/norte de una
                        // sala los emite el tile de fuera.
                        if (styled) PlaceWallsTinted(prefabs, root.transform, edges, tx, tz, mats.wall,
                            Damp(JitterValue(wallBase * zoneTint, rng, WallValueJitter), wallDamp, wallStain),
                            cfg, gx, gz, zoneKindQuery, roomZones);
                        else PlaceWalls(prefabs, root.transform, edges, tx, tz,
                            gx, gz, zoneKindQuery);
                    }

                    // Medias paredes (2): dintel sobre vano. Va FUERA del `if (edges != 0)`
                    // porque un dintel vive justo donde NO hay panel. Su tinte NO pasa por
                    // JitterValue a propósito: una draw extra del rng aquí correría la
                    // secuencia y cambiaría el tinte de los pilares de este tile — la misma
                    // disciplina que mantiene PlaceCeilingTile.
                    if (styled)
                        PlaceLintels(prefabs, root.transform, walls, cfg, tx, tz, gx, gz,
                            mats.wall, Damp(wallBase * zoneTint, wallDamp, wallStain));

                    // ADR-033/Pillar: nibble alto → columnas por sub-celda. Sin
                    // traducción de bits (a diferencia de N/S/E/W): cada bit ya
                    // nombra su propia sub-celda, sin reparto con el vecino.
                    byte pillarBits = (byte)(b & PillarMask);
                    if (pillarBits != 0 && prefabs.pillar != null)
                    {
                        // Primer pase (decisión explícita): mats.wall, SIN material
                        // propio de pilar — pendiente de pasada de arte separada.
                        // Mismo WallValueJitter que el panel: el pilar se pinta con el
                        // material de pared y con su mismo tinte base, así que dejarlo
                        // jitterado lo habría convertido en lo ÚNICO que varía por tile en
                        // esa superficie — más visible aún ahora que sus vecinos son planos.
                        Color pillarTint = styled
                            ? Damp(JitterValue(wallBase * zoneTint, rng, WallValueJitter), wallDamp, wallStain)
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
            // ZONE_OFFICE — escalera decorativa. Se planifica ANTES de los props para que
            // ambos vean el MISMO tile reservado: el plan es puro, así que llamarlo dos veces
            // da lo mismo, pero pasarlo evita que un cambio futuro en uno de los dos criterios
            // deje un archivador dentro de la escalera.
            var stairPlan = OfficeStairs.PlanFor(zoneKindQuery, roomZones, walls, chunkX, chunkZ);

            // Fase 2 — salas autoradas. Se PLANIFICAN aquí, antes de los props, por el mismo
            // motivo que la escalera: los props tienen que ver el espacio ya reservado o
            // spawnearían dentro de la geometría autorada. Se instancian después del plan
            // para no meter nada entre el plan y su consumidor.
            PlanAuthoredRooms(walls, roomZones, chunkX, chunkZ, stairPlan, _roomPlanScratch);
            if (_roomPlanScratch.Count > 0)
                PlaceAuthoredRooms(root.transform, _roomPlanScratch);
            if (styled && stairPlan.valid)
            {
                // Tinte SIN pasar por JitterValue, misma disciplina que PlaceLintels: el
                // `rng` de esta clase es por tile y su secuencia decide el jitter HSV de
                // suelo/pared/pilares. Consumir una draw aquí re-tintaría el chunk entero,
                // y esto no es una pieza por tile.
                int sgx = chunkX * Tiles + stairPlan.tx, sgz = chunkZ * Tiles + stairPlan.tz;
                OfficeStairs.Build(root.transform, mats.wall,
                    cfg.WallTintFor(Hash01(sgx, sgz, TintSaltWall)) * zoneTint,
                    TileCenter(stairPlan.tx, stairPlan.tz), stairPlan.yaw);
            }

            // El gate sigue mirando `cfg.props` (el catálogo de CAPA) a propósito: un
            // `zonePropSets` autorado sin catálogo de capa detrás sería una zona con muebles
            // en un mundo sin ellos, y este gate es el interruptor histórico de "esta capa
            // tiene props". `PlaceProps` resuelve dentro cuál usar de verdad.
            if (styled && cfg.props != null && cfg.props.Length > 0)
                PlaceProps(root.transform, walls, cfg, mats, chunkX, chunkZ, zoneKindQuery,
                    roomZones, stairPlan, _roomPlanScratch);

            // Enmienda a ADR-081 — el cartel que anuncia que en esta sala se puede construir. Va
            // fuera del gate de `cfg.props` a propósito: los props son decorado y pueden faltar en
            // una capa entera, pero este cartel es la ÚNICA señal de una regla del juego, y una capa
            // sin catálogo de muebles no debe quedarse sin ella.
            if (styled)
                BuildZoneSign.Place(root.transform, walls, buildRoomTileX, buildRoomTileZ, chunkX, chunkZ);

            // Fase 5A (Bug #1): tag the whole chunk to its macro-layer's Unity layer so
            // per-layer lamp culling isolates it (see GeoLayers). Lamps/luminaires added
            // afterwards by BackroomsLighting set their own layer. Pipes (above) are children
            // of root, so they inherit the layer here too — lit only by this layer's lamps.
            SetLayerRecursively(root, GeoLayer(layerIndex));
            return root;
        }

        /// <summary>
        /// ADR-081 enmienda 5 — ¿es (tx, tz) uno de los 3 × 3 tiles de la habitación construible de
        /// este chunk? `buildRoomTileX < 0` significa "este chunk no tiene", que es la inmensa
        /// mayoría.
        /// </summary>
        private static bool IsBuildRoomTile(int tx, int tz, int roomTileX, int roomTileZ) =>
            roomTileX >= 0
            && tx >= roomTileX && tx < roomTileX + GridChunkDataMsg.BuildRoomTiles
            && tz >= roomTileZ && tz < roomTileZ + GridChunkDataMsg.BuildRoomTiles;

        /// <summary>
        /// Material de la habitación construible: liso, SIN TEXTURA, deliberadamente distinto de
        /// todo lo demás. Es la señal de "aquí sí" que pidió Joel, y es provisional — la textura
        /// definitiva es trabajo de arte.
        ///
        /// Se crea una vez y se comparte entre todos los chunks: un material por sala rompería el
        /// batching y no aportaría nada, porque todas se pintan igual.
        /// </summary>
        private static Material BuildRoomMaterial() =>
            _buildRoomMat != null
                ? _buildRoomMat
                : _buildRoomMat = MaterialHelper.MakeLit(BuildRoomColour);

        private static Material _buildRoomMat;

        /// <summary>Gris claro neutro, a propósito más frío y plano que el amarillo sucio del resto
        /// del nivel: se tiene que leer como "sitio preparado", no como más Backrooms.</summary>
        private static readonly Color BuildRoomColour = new Color(0.62f, 0.63f, 0.60f);

        // Logged once per session (not once per tile/chunk) so a world with many
        // PILLAR_HALL chunks doesn't flood the console — see the call site in
        // BuildFromWalls.
        private static bool _loggedMissingPillarPrefab;

        // ── Fase 5A — styled (per-layer) variants ──────────────────────────────

        // Reused across all Paint() calls — SetPropertyBlock copies the data, so a
        // single shared block avoids a per-tile allocation.
        private static readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();

        /// Reusable buffer for the GetComponentsInChildren(List) overload, shared by
        /// AddColliderIfMissing and Paint (they never run nested). Chunk building called the
        /// array-returning overload twice per instantiated piece, so this was the single largest
        /// source of per-chunk garbage in the builder.
        private static readonly List<MeshRenderer> _rendererScratch = new List<MeshRenderer>();

        // ── Medias paredes — knee walls ─────────────────────────────────────────

        // Salts for the wall-variety hashes. One per ORIENTATION LANE, not per edge
        // flag: a panel running along X and one running along Z on the same tile are
        // different physical walls and must decide independently. (S shares N's salt
        // and W shares E's because the runtime only ever emits the N/E panels — the
        // no-duplication rule of BuildFromWalls — so the two never coexist on a tile.)
        // Values distinct from every TintSalt*/MoistSalt*/PropSalt* in this file.
        private const uint WallSaltKneeN = 0x4B4E574EU; // "KNWN" — knee, X-running lane
        private const uint WallSaltKneeE = 0x4B4E5745U; // "KNWE" — knee, Z-running lane

        // ── ADR-035 — variantes de modelo de pared ──────────────────────────────

        // Salts de la elección de MODELO, por carril de orientación igual que los de
        // knee wall — así los dos paneles físicos de un tile eligen su modelo de forma
        // independiente. Distintos de KneeSalt*/LintelSalt*/TintSalt*/MoistSalt*/PropSalt*:
        // compartir uno haría que "este panel es knee wall" y "este panel usa el modelo B"
        // salieran siempre juntos.
        private const uint WallSaltVariantN = 0x5756524EU; // "WVRN" — modelo, carril en X
        private const uint WallSaltVariantE = 0x57565245U; // "WVRE" — modelo, carril en Z

        // ── Medias paredes — dinteles sobre vano ────────────────────────────────

        // Backend bit convention of the low nibble of walls[tx,tz]
        // (backend/src/world/grid_gen/tile_walls.rs). BuildFromWalls consumes only S and
        // E: the −Z/−X panels belong to the neighbour tile (no-duplication rule), so
        // these two bits are also the only ones whose ABSENCE this tile may read as a
        // doorway it owns.
        private const byte BackendBitS = 2; // +Z → this tile's EdgeNorth panel
        private const byte BackendBitE = 4; // +X → this tile's EdgeEast  panel

        private const uint WallSaltLintelN = 0x4C4E544EU; // "LNTN" — lintel, X-running lane
        private const uint WallSaltLintelE = 0x4C4E5445U; // "LNTE" — lintel, Z-running lane

        // Pieza C — per-tile tint palette salts. One per surface role so floor, wall
        // and ceiling pick INDEPENDENTLY (a shared salt would make all three switch
        // shade on the same tiles, reading as a grid of coloured boxes).
        private const uint TintSaltFloor   = 0x46544E54U; // "FTNT"
        private const uint TintSaltWall    = 0x57544E54U; // "WTNT"
        private const uint TintSaltCeiling = 0x43544E54U; // "CTNT"

        // ── Fase 5B — procedural ceiling variety ────────────────────────────────

        /// <summary>
        /// Damp-grey multiplier for moisture-stained ceiling tiles. DESACTIVADO desde
        /// 2026-08-13 (ver <see cref="CeilingStainThreshold"/>); el color se conserva
        /// porque el defecto no era el tono.
        /// </summary>
        private static readonly Color MoistureStain = new Color(0.7f, 0.68f, 0.6f);

        /// <summary>
        /// Umbral de la mancha de techo, 0.20 → 0 (2026-08-13). Era un literal dentro de
        /// <c>PlaceCeilingTile</c>; se saca a constante con nombre para apagarlo con la
        /// misma disciplina que <see cref="FloorStainThreshold"/> y los dos jitter — el
        /// camino de código sigue entero y subir el umbral lo reactiva.
        ///
        /// Cierra el barrido de multiplicadores por TILE con borde duro: primero el jitter
        /// de pared, luego el de suelo y techo, luego la mancha de suelo y ahora esta. En
        /// una retícula de 5 m todos producen el mismo artefacto —un escalón de valor que
        /// cae exactamente en el límite del tile y se lee como la costura del tiling en vez
        /// de como desgaste. Aquí eran −30 % de luminancia sobre el 13 % de las placas.
        ///
        /// Sobrevive SOLO la mancha de pared, que es la excepción con motivo: los paneles
        /// son piezas separadas de 5 m, así que su escalón cae sobre una junta real.
        /// </summary>
        private const float CeilingStainThreshold = 0f;

        // Atenuación de la placa "ausente" (ver PlaceCeilingTile). Es el panel MÁS oscuro
        // de todo el techo, así que fija el suelo del criterio "ninguna superficie de mundo
        // se lee como negro puro bajo una lámpara": con el albedo medido de
        // M_Backrooms_Ceiling (0.793 en sRGB) queda en 0.357, muy por encima de negro, y
        // sigue separándose de la placa hundida (×0.80) y de la caída (×0.85).
        private const float AbsentPanelDim = 0.45f;

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
        /// <summary>
        /// Mancha de suelo. DESACTIVADA desde 2026-08-13 (ver <see cref="FloorStainThreshold"/>);
        /// el color se conserva porque el defecto no era el tono sino dónde caía su borde.
        /// </summary>
        private static readonly Color FloorStain = new Color(0.74f, 0.71f, 0.64f);
        private static readonly Color WallStain  = new Color(0.84f, 0.82f, 0.74f);
        /// <summary>Segundo tono de mancha de pared: el núcleo de la filtración, más
        /// oscuro (−33 % de luminancia frente al −18 % de <see cref="WallStain"/>) y más
        /// amarronado. Va DENTRO de la mancha débil, no en parches propios — lo garantiza
        /// que el umbral de abajo sea más bajo y que los dos lean la misma muestra de
        /// humedad.</summary>
        private static readonly Color WallStainDeep = new Color(0.72f, 0.67f, 0.55f);
        /// <summary>
        /// 0.22 → 0 (2026-08-13): la mancha de suelo se apaga. `MoistureAt` nunca devuelve
        /// menos de 0, así que con el umbral a 0 ningún tile es húmedo y el multiplicador
        /// deja de aplicarse.
        ///
        /// POR QUÉ, y por qué la de PARED se queda: la mancha es un multiplicador por TILE
        /// con borde duro. En pared eso funciona —los paneles son piezas separadas de 5 m y
        /// el agua corre por ellas, así que el escalón cae donde ya hay una junta real. El
        /// suelo son losas COPLANARES que forman una superficie continua: el mismo escalón
        /// no tiene junta donde esconderse y se lee como una raya recta pintada sobre el
        /// suelo. Y era el residuo grande — −28.9 % de luminancia sobre el 15 % de las
        /// baldosas, agrupadas en bloques de 2×2 (10 m), o sea 3.6× lo que valía el jitter
        /// que se quitó en 3dece7c precisamente por producir ese mismo artefacto.
        ///
        /// No se borra el camino: subir este umbral la reactiva. Pero devolverla tal cual
        /// devuelve el defecto — la forma correcta de ensuciar un suelo continuo es en el
        /// espacio de la TEXTURA, no por tile, y eso es trabajo de shader.
        ///
        /// El equivalente del techo (<see cref="MoistureStain"/>, umbral 0.20 en
        /// <c>PlaceCeilingTile</c>) queda intacto a propósito: fuera de alcance aquí, y el
        /// techo sí tiene junta dibujada por la propia textura de placa.
        /// </summary>
        private const float FloorStainThreshold = 0f;
        /// <summary>0.18 → 0.35 (2026-08-13). Con el jitter por tile fuera de las paredes,
        /// la mancha es lo ÚNICO que varía de panel a panel, y a 0.18 tocaba ~10 % de los
        /// tiles: una pared de seis paneles salía con cinco idénticos. A 0.35 son ~31 %.
        /// El umbral NO es la fracción de tiles manchados — la humedad es
        /// <c>0.8 · hash(bloque 2×2) + 0.2 · hash(tile)</c>, así que la fracción sale de
        /// integrar esa mezcla, no de leer el número.</summary>
        private const float WallStainThreshold  = 0.35f;
        /// <summary>~4.5 % de los tiles, o sea uno de cada siete manchados: el núcleo.</summary>
        private const float WallStainDeepThreshold = 0.12f;

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

        // ── Fase 2 — salas autoradas (RoomPool) ────────────────────────────────
        //
        // Estáticos AQUÍ y no en GridChunkBuilder.AuthoredRooms.cs por la regla del
        // encabezado de esta clase: el orden de inicialización de estáticos entre ficheros
        // de un partial es indefinido.

        private const uint RoomSaltPick = 0x524F4F4DU; // "ROOM" — qué sala del pool y con qué giro

        // El pool es un asset OPCIONAL: sin `Resources/Rooms/RoomPool.asset` (o vacío) la
        // función entera queda inerte y el chunk se construye exactamente como antes de
        // Fase 2. Se carga una vez por sesión — Resources.Load recorre el índice y esto
        // corre por chunk. `_roomPoolLoaded` distingue "aún no lo he buscado" de "lo busqué
        // y no hay", que es el caso normal hasta que se hornee la primera sala.
        private static RoomPool _roomPool;
        private static bool _roomPoolLoaded;

        // Tabla (entrada del pool, giro) → (footprint ya girado, lado al que mira la puerta).
        // Depende SOLO del pool: ni del chunk ni de la zona. Antes se rehacía entera —
        // `pool.rooms.Length` × 4 `Quaternion.Euler` — por cada zona sellada de cada chunk.
        // `_roomVariantsSource` es la salvaguarda de editor: hornear una sala reemplaza el
        // array `rooms` del asset (ArrayUtility.Add), así que comparar la REFERENCIA detecta
        // cualquier reescritura del pool sin esperar a una recarga de dominio.
        private static readonly List<RoomVariant> _roomVariants = new List<RoomVariant>();
        private static RoomPool.RoomEntry[] _roomVariantsSource;

        // Scratch de PlanAuthoredRooms/PlaceAuthoredRooms, reutilizado: se construye una
        // lista por chunk y los chunks se construyen de uno en uno (mismo patrón y misma
        // no-reentrancia que _propScratch).
        private static readonly List<RoomPlan> _roomPlanScratch = new List<RoomPlan>();
        private static readonly List<(int entry, float yaw)> _roomFitScratch
            = new List<(int, float)>();

        // Pieza E — how far from the tile centre a wall-aligned prop backs off, in
        // metres. The wall panel sits at ±Ts/2 (2.5 m), so 1.9 leaves ~0.6 m for the
        // prop's own depth and keeps it from clipping through the panel.
        private const float PropHugInset = 1.9f;

        // Offsets de los slots de prop dentro de un tile de 5 m. El slot 0 es el CENTRO —
        // la posición histórica, que es lo que mantiene byte-idéntica una capa sin
        // `propsPerTile`. Los 4 restantes son los centros de sub-celda (±1.25 m), la misma
        // subdivisión de 2.5 m que usan las columnas; a esa distancia un escritorio en el
        // centro (1.2 m de ancho) y un archivador en una esquina (0.42 m) no se solapan.
        private static readonly Vector2[] PropSlotOffsets =
        {
            new Vector2( 0f,     0f),
            new Vector2(-1.25f, -1.25f),
            new Vector2( 1.25f, -1.25f),
            new Vector2(-1.25f,  1.25f),
            new Vector2( 1.25f,  1.25f),
        };

        // ── ADR-036 — densidad de props por RoomType ────────────────────────────
        //
        // Multiplican `cfg.propDensity` en el gate de spawn, NADA MÁS: el catálogo
        // (`cfg.props`), la selección ponderada, el sesgo de cluster, el pegado a pared y
        // los salts siguen exactamente como estaban. Son constantes y no campos de
        // `LayerVisualConfig` a propósito: es un primer pase de sensación, no una palanca
        // de autoría por capa. TODO(balance) — calibrar en playtest.
        //
        // SealedRoom 2.5: con el `propDensity` 0.18 de layer 0 sube a 0.45, o sea ~7 de
        // los 16 tiles de una sala de 8×8 celdas. Es "habitada" sin llegar a almacén. 2.0
        // (0.36) apenas se distingue del baseline en una sala tan pequeña —6 tiles frente
        // a 3— y 3.0 (0.54) pasa de la mitad de los tiles y lee como trastero.
        //
        // CorridorSpine 0.15: NO cero. Un pasillo literalmente vacío lee como zona sin
        // terminar, no como tensión. A 0.15 × 0.18 = 0.027 la mayoría de los spines salen
        // vacíos de verdad y uno de cada tantos tiene un objeto suelto, que es lo que hace
        // que el vacío se lea como deliberado. Poner 0.0 aquí es un cambio de una cifra si
        // en playtest gana el vacío absoluto.
        private const float PropDensityOpen = 1.0f;  // sin cambio — comportamiento histórico
        private const float PropDensitySealedRoom = 2.5f;
        private const float PropDensityCorridorSpine = 0.15f;

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

    }
}
