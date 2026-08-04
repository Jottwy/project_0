// Partición de GridChunkBuilder: props procedurales (Fase 5C / ADR-036) —
// densidad por RoomType, colocación y selección ponderada. El resto vive en
// GridChunkBuilder.cs (raíz), .Placement.cs, .WallVariants.cs y .Tinting.cs.
// TODOS los campos estáticos (incl. los PropSalt* y _propScratch) viven en el
// raíz: el orden de inicialización de estáticos entre partials es indefinido.
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    public static partial class GridChunkBuilder
    {
        private static float PropDensityMultiplier(RoomZoneKind roomType)
        {
            switch (roomType)
            {
                case RoomZoneKind.SealedRoom: return PropDensitySealedRoom;
                case RoomZoneKind.CorridorSpine: return PropDensityCorridorSpine;
                default: return PropDensityOpen;
            }
        }

        /// <summary>
        /// Place one prop per selected tile from <paramref name="cfg"/>.props. Spawn, cluster
        /// and within-tile choices are pure hashes of (chunk, tile) → deterministic, no order
        /// coupling. Skips fully-walled tiles and the reserved chunk-centre tile, caps the
        /// count per chunk (unbiased subset), and falls back to placeholder primitives when a
        /// PropEntry has no prefab. Props are children of the chunk root, so they inherit the
        /// chunk's Unity layer via SetLayerRecursively (called after) — lit only by this layer.
        /// </summary>
        private static void PlaceProps(Transform parent, byte[,] walls, LayerVisualConfig cfg,
            LayerVisualMaterials mats, int chunkX, int chunkZ, RoomZoneMsg[] roomZones)
        {
            float totalWeight = 0f;
            for (int i = 0; i < cfg.props.Length; i++)
                totalWeight += Mathf.Max(0f, cfg.props[i].spawnWeight);
            if (totalWeight <= 0f) return;

            float baseDensity = Mathf.Clamp01(cfg.propDensity);
            float bias    = Mathf.Clamp01(cfg.propClusterBias);
            int center    = Tiles / 2;
            // ADR-036: sin zonas (backend anterior a ADR-034, o chunk sin ellas) ni se
            // consulta el array — el gate queda literalmente el de antes.
            bool hasZones = roomZones != null && roomZones.Length > 0;

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
                    // ADR-036: el RoomType del TILE (no del panel — un prop se coloca
                    // dentro del tile, no en su frontera) escala la densidad. Los hashes
                    // se calculan igual y en el mismo orden que antes: lo único que cambia
                    // es el umbral contra el que se comparan, así que un tile Open sigue
                    // decidiéndose exactamente igual que antes de este ADR.
                    float density = hasZones
                        ? Mathf.Clamp01(baseDensity *
                            PropDensityMultiplier(RoomTypeForTile(roomZones, tx, tz)))
                        : baseDensity;
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

                // Instantiating straight under `parent` avoids the scene-root spawn + reparent
                // (two transform-hierarchy updates) the SetParent path costs. The placeholder
                // branch has no such overload, and it still draws PropSaltVarA only when it is
                // the branch taken — same hash sequence as before.
                GameObject go;
                if (e.prefab != null)
                {
                    go = Object.Instantiate(e.prefab, parent, false);
                }
                else
                {
                    go = PlaceholderFactory.Create(type, mats.prop, Hash01(gx, gz, PropSaltVarA));
                    go.transform.SetParent(parent, false);
                }

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
