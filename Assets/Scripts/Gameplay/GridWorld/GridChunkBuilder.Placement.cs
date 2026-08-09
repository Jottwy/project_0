// Partición de GridChunkBuilder: colocación de geometría — suelos/techos,
// paneles de pared, pilares, dinteles y tuberías. El resto vive en
// GridChunkBuilder.cs (raíz), .WallVariants.cs, .Tinting.cs y .Props.cs.
// TODOS los campos estáticos viven en el fichero raíz: el orden de
// inicialización de estáticos entre ficheros de una clase partial es indefinido.
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    public static partial class GridChunkBuilder
    {
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
            // List overload: same traversal and order as the array one, but it fills a reusable
            // buffer instead of allocating an array per call. This runs once per instantiated
            // wall/floor/ceiling piece — several hundred times per chunk.
            go.GetComponentsInChildren(_rendererScratch);
            for (int i = 0; i < _rendererScratch.Count; i++)
            {
                var r = _rendererScratch[i];
                if (r.TryGetComponent<Collider>(out _)) continue;
                if (!r.TryGetComponent<MeshFilter>(out var mf) || mf.sharedMesh == null) continue;
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

        /// <summary>
        /// Independent 5×4×0.2 wall pieces on the flagged tile edges. Unstyled path: sin
        /// <c>cfg</c> no hay sets de variantes que consultar, así que
        /// <see cref="ResolveWallPrefab"/> devuelve siempre <c>prefabs.wall</c> — comparte
        /// la ruta de selección con <see cref="PlaceWallsTinted"/> sin cambiar de resultado.
        /// </summary>
        private static void PlaceWalls(GridPrefabSet prefabs, Transform parent,
            byte edges, int tx, int tz, int gx, int gz, int zoneKind, RoomZoneMsg[] roomZones)
        {
            foreach (var (flag, ox, oz, yaw) in WallEdgeTable)
                if ((edges & flag) != 0)
                    AddColliderIfMissing(Instantiate(
                        ResolveWallPrefab(prefabs, null, false, zoneKind, roomZones,
                            tx, tz, gx, gz, flag),
                        parent, TileCenter(tx, tz) + new Vector3(ox * Ts, 0f, oz * Ts), yaw));
        }

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

        /// <summary>
        /// Hanging lintel beams over DOORWAYS. A doorway is an edge this tile owns that is
        /// OPEN while the same wall line continues — walled — on both collinear neighbours;
        /// that distinguishes a real gap in a wall from plain open floor, where a beam
        /// would read as a random girder in the middle of a room.
        ///
        /// Deliberately restricted to already-open edges. Hanging a beam on a WALLED edge
        /// would leave a gap at floor level in an edge the backend marks solid, and the
        /// phantom (ADR-016) collides against that same grid_gen layout — render and
        /// phantom would disagree, which is exactly what ADR-018 guarantees against. So a
        /// lintel never changes traversal in either direction (see
        /// <see cref="LayerVisualConfig.MinLintelClearance"/> for the other half).
        ///
        /// Border tiles (0 / Tiles-1) are skipped: their collinear neighbours live in the
        /// adjacent chunk, which this builder cannot see — same criterion as the pillar
        /// border invariant of ADR-033.
        /// </summary>
        private static void PlaceLintels(GridPrefabSet prefabs, Transform parent, byte[,] walls,
            LayerVisualConfig cfg, int tx, int tz, int gx, int gz, Material mat, Color tint)
        {
            float chance = Mathf.Clamp01(cfg.lintelChance);
            if (chance <= 0f) return;
            if (tx == 0 || tx == Tiles - 1 || tz == 0 || tz == Tiles - 1) return;

            float clearance = cfg.LintelClearanceClamped;
            float scaleY = (WallPrefabHeight - clearance) / WallPrefabHeight;
            if (scaleY <= 0f) return; // clearance authored at/above the ceiling — nothing to hang

            byte b = walls[tx, tz];

            if ((b & BackendBitS) == 0
                && (walls[tx - 1, tz] & BackendBitS) != 0
                && (walls[tx + 1, tz] & BackendBitS) != 0
                && Hash01(gx, gz, WallSaltLintelN) < chance)
                PlaceLintelPiece(prefabs, parent, tx, tz, 0f, 0.5f, 0f, clearance, scaleY, mat, tint);

            if ((b & BackendBitE) == 0
                && (walls[tx, tz - 1] & BackendBitE) != 0
                && (walls[tx, tz + 1] & BackendBitE) != 0
                && Hash01(gx, gz, WallSaltLintelE) < chance)
                PlaceLintelPiece(prefabs, parent, tx, tz, 0.5f, 0f, 90f, clearance, scaleY, mat, tint);
        }

        /// <summary>One beam spanning a doorway, occupying [clearance, LayerHeight]. Named
        /// "Lintel" (not "Wall(Clone)") so it is distinguishable in the hierarchy and in
        /// tests.</summary>
        private static void PlaceLintelPiece(GridPrefabSet prefabs, Transform parent,
            int tx, int tz, float ox, float oz, float yaw, float clearance, float scaleY,
            Material mat, Color tint)
        {
            var go = Instantiate(prefabs.wall, parent,
                TileCenter(tx, tz) + new Vector3(ox * Ts, clearance, oz * Ts), yaw);
            go.name = "Lintel";
            go.transform.localScale = new Vector3(1f, scaleY, 1f);
            AddColliderIfMissing(go);
            Paint(go, mat, tint);
        }

        /// <summary>
        /// Wall pieces using the shared layer material + a per-tile tint (MPB).
        /// <paramref name="cfg"/>.wallPanelVariety turns a share of the panels into KNEE
        /// WALLS (partial height, see-over) chosen by a pure hash of the GLOBAL tile coords
        /// — never by <c>rng</c>, so the tint draw sequence of Piezas A-F is untouched.
        /// Los paneles de perímetro SELLADO (SealedRoom/CorridorSpine) quedan EXCLUIDOS
        /// del sorteo — ver el comentario en el sitio de la comprobación.
        /// The knee wall stays untraversable by construction (see
        /// <see cref="LayerVisualConfig.MinKneeWallHeight"/>): the runtime BoxCollider
        /// scales with the transform, so the collider matches the visual exactly — no
        /// invisible full-height barrier, and no new hole either.
        /// </summary>
        private static void PlaceWallsTinted(GridPrefabSet prefabs, Transform parent,
            byte edges, int tx, int tz, Material mat, Color tint,
            LayerVisualConfig cfg, int gx, int gz, int zoneKind, RoomZoneMsg[] roomZones)
        {
            // One aliveness check instead of two: `!= null` on a UnityEngine.Object is an
            // overloaded operator that calls into native code, not a reference compare.
            bool hasCfg = cfg != null;
            float variety = hasCfg ? Mathf.Clamp01(cfg.wallPanelVariety) : 0f;
            float kneeScale = hasCfg ? cfg.KneeWallHeightClamped / WallPrefabHeight : 1f;

            foreach (var (flag, ox, oz, yaw) in WallEdgeTable)
                if ((edges & flag) != 0)
                {
                    // ADR-035: `hasCfg` ya resuelto arriba — ResolveWallPrefab no repite
                    // el `!= null` nativo por panel. El RoomType se resuelve POR PANEL.
                    var go = Instantiate(
                        ResolveWallPrefab(prefabs, cfg, hasCfg, zoneKind, roomZones,
                            tx, tz, gx, gz, flag),
                        parent, TileCenter(tx, tz) + new Vector3(ox * Ts, 0f, oz * Ts), yaw);
                    // Un knee wall NUNCA perfora un perímetro sellado: una SealedRoom o
                    // CorridorSpine que se puede ver por encima deja de leerse como sala
                    // cerrada, que es justo su razón de existir frente a una zona Open.
                    // `Open` cubre tanto "dentro de una zona Open" como "en ningún rect"
                    // (el fallback de RoomTypeForPanel), o sea el maze — ahí sí aplica.
                    //
                    // ORDEN DELIBERADO: `Hash01` es aritmética pura y RoomTypeForPanel
                    // recorre `roomZones` (hasta 2 RoomTypeForTile por panel). Con el
                    // sorteo primero, la resolución de sala solo corre para la fracción
                    // que ya pasó el roll (~8% con el perfil de layer 0), no para los ~94
                    // paneles del chunk. Ambas condiciones son puras, así que el
                    // corto-circuito no cambia el resultado, solo el coste.
                    //
                    // Hoy `ResolveWallPrefab` corta antes de resolver la sala porque
                    // `wallVariantSets` está vacío en los 4 assets, así que esta es la
                    // ÚNICA llamada. Cuando se autoren modelos de pared (ADR-035) el
                    // mismo panel resolverá su RoomType dos veces — merece cachearlo
                    // entonces, no ahora (sería una abstracción sin consumidor).
                    if (variety > 0f && Hash01(gx, gz, KneeSaltFor(flag)) < variety
                        && RoomTypeForPanel(roomZones, tx, tz, flag) == RoomZoneKind.Open)
                        go.transform.localScale = new Vector3(1f, kneeScale, 1f);
                    AddColliderIfMissing(go);
                    Paint(go, mat, tint);
                }
        }

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

                if (go.TryGetComponent<Collider>(out var col))
                    Object.Destroy(col); // decorative — Rust owns collision

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
    }
}
