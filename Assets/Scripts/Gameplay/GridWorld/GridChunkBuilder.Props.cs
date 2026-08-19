// Partición de GridChunkBuilder: props procedurales (Fase 5C / ADR-036) —
// densidad por RoomType, colocación y selección ponderada. El resto vive en
// GridChunkBuilder.cs (raíz), .Placement.cs, .WallVariants.cs y .Tinting.cs.
// TODOS los campos estáticos (incl. los PropSalt* y _propScratch) viven en el
// raíz: el orden de inicialización de estáticos entre partials es indefinido.
using System.Collections.Generic;
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
            LayerVisualMaterials mats, int chunkX, int chunkZ, int zoneKind, RoomZoneMsg[] roomZones,
            OfficeStairs.Plan stairPlan, List<RoomPlan> authoredRooms)
        {
            // Catálogo, densidad y tope por zona (OFFICE), con caída a los de la capa. Se
            // resuelve UNA vez por chunk, no por tile: `zone_kind` es constante en todo el
            // chunk (ZoneRegistry lo keyea por (cx,cz)), así que consultarlo dentro del bucle
            // solo repetiría el mismo recorrido.
            bool hasZoneSet = cfg.TryGetZonePropSet(zoneKind, out var zoneSet);
            PropEntry[] props = hasZoneSet ? zoneSet.props : cfg.props;
            if (props == null || props.Length == 0) return;

            float totalWeight = 0f;
            for (int i = 0; i < props.Length; i++)
                totalWeight += Mathf.Max(0f, props[i].spawnWeight);
            if (totalWeight <= 0f) return;

            // El tope por zona es lo que de verdad amueblaba mal una planta de oficinas: el
            // global (12) es para TODO el chunk, así que repartido entre 4 despachos más el
            // maze deja ~3 objetos en una sala de 10×10 m. 0 = sin autorar ⇒ el global.
            int maxProps = hasZoneSet && zoneSet.maxPropsPerChunk > 0
                ? zoneSet.maxPropsPerChunk
                : MaxPropsPerChunk;

            // `densityScale` multiplica ANTES del Clamp01 de propDensity para que una zona
            // pueda subir de verdad; 0 = sin autorar ⇒ 1, sin cambio.
            float zoneScale = hasZoneSet && zoneSet.densityScale > 0f ? zoneSet.densityScale : 1f;
            float baseDensity = Mathf.Clamp01(cfg.propDensity * zoneScale);
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
                    // ZONE_OFFICE: el tile de la escalera está ocupado por geometría con
                    // collider de suelo a rellano. Un prop ahí spawnearía DENTRO de ella —
                    // mismo motivo por el que se excluye un tile con columna, unas líneas
                    // más abajo.
                    if (stairPlan.valid && tx == stairPlan.tx && tz == stairPlan.tz) continue;
                    // Fase 2: el interior de una sala autorada lo amuebla la propia pieza. Un
                    // prop procedural ahí se metería dentro de su geometría, exactamente el
                    // mismo caso que la escalera de arriba. Los hashes de este tile son puros
                    // (no hay secuencia que desplazar), así que saltar aquí no mueve ni un
                    // prop de los tiles restantes.
                    if (authoredRooms != null && IsAuthoredRoomTile(authoredRooms, tx, tz)) continue;
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

            // Props por TILE. Subir `maxPropsPerChunk` deja de servir en cuanto la retícula
            // de 5 m se satura: con densidad ~1 en sala sellada, cada tile elegible ya tiene
            // su objeto, y un despacho de 10×10 m son 4 tiles ⇒ 4 objetos y no más. Los
            // extra van a las sub-celdas de 2.5 m, la misma subdivisión que usan las
            // columnas. 0/1 = comportamiento de siempre.
            int perTile = Mathf.Clamp(hasZoneSet && zoneSet.propsPerTile > 0
                ? zoneSet.propsPerTile : 1, 1, PropSlotOffsets.Length);

            // El tope sigue contando PROPS, no tiles, así que el cupo de tiles se divide
            // entre los slots — si no, `propsPerTile` multiplicaría el tope por la espalda.
            int maxTiles = Mathf.Max(1, maxProps / perTile);
            if (_propScratch.Count > maxTiles)
            {
                _propScratch.Sort((a, b) => a.key.CompareTo(b.key));
                _propScratch.RemoveRange(maxTiles, _propScratch.Count - maxTiles);
            }

            // Pass 2: place `perTile` props per selected tile.
            for (int i = 0; i < _propScratch.Count; i++)
            for (int slot = 0; slot < perTile; slot++)
            {
                int tx = _propScratch[i].tx, tz = _propScratch[i].tz;
                int gx = chunkX * Tiles + tx, gz = chunkZ * Tiles + tz;

                // El slot 0 usa los salts TAL CUAL, así que una capa sin `propsPerTile`
                // reproduce prop por prop lo que colocaba antes de esta pieza; los slots
                // extra sacan sus decisiones de salts derivados.
                PropEntry e = PickProp(props, Hash01(gx, gz, SlotSalt(PropSaltPick, slot)) * totalWeight);
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
                    go = PlaceholderFactory.Create(type, mats.prop,
                        Hash01(gx, gz, SlotSalt(PropSaltVarA, slot)));
                    go.transform.SetParent(parent, false);
                }

                // Y by kind: cables hang from the ceiling; flat decals lift slightly off the
                // floor; the rest sit on the floor surface.
                float y = PropFloorY;
                if (!e.floorOnly && type == "cable")          y = LayerHeight;
                else if (type == "paper" || type == "stain")  y = PropFloorY + 0.005f;
                // El slot 0 se queda en el centro del tile (posición histórica); los extra se
                // reparten por las sub-celdas, lo bastante lejos del centro para no clavarse
                // dentro del que ya está ahí.
                var slotOff = PropSlotOffsets[slot];
                go.transform.localPosition = TileCenter(tx, tz)
                    + new Vector3(slotOff.x, y, slotOff.y);

                // Yaw / tip.
                if (e.canBeRotated)
                {
                    float hYaw = Hash01(gx, gz, SlotSalt(PropSaltYaw, slot));
                    if (type == "chair")
                    {
                        float yaw = Mathf.Floor(hYaw * 4f) * 90f;                     // 0/90/180/270
                        float tip = Hash01(gx, gz, SlotSalt(PropSaltVarB, slot)) < 0.30f ? 90f : 0f;
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
                        int pick = Mathf.Clamp(
                            (int)(Hash01(gx, gz, SlotSalt(PropSaltSide, slot)) * count), 0, count - 1);
                        for (int b = 0; b < 4; b++)
                        {
                            if ((sides & (1 << b)) == 0) continue;
                            if (pick-- > 0) continue;
                            var hug = WallHugTable[b];
                            // El DESPLAZAMIENTO solo para el slot central. Un slot de
                            // sub-celda ya está a 1.25 m del centro; sumarle los 1.9 m del
                            // hug daría 3.15 m, o sea fuera del tile de 5 m — el prop
                            // acabaría en el tile vecino o dentro de la pared. La ROTACIÓN sí
                            // se aplica siempre: un prop de sub-celda ya está pegado a un
                            // lado, lo que le faltaba era mirar hacia donde toca.
                            if (slot == 0)
                                go.transform.localPosition += new Vector3(
                                    hug.ox * PropHugInset, 0f, hug.oz * PropHugInset);
                            go.transform.localRotation = Quaternion.Euler(0f, hug.yaw, 0f);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Salt del slot <paramref name="slot"/> dentro de un tile. El slot 0 devuelve el salt
        /// SIN TOCAR — es lo que hace que una capa sin `propsPerTile` reproduzca prop por prop
        /// lo que colocaba antes de que esto existiera. Los demás mezclan con la constante de
        /// Knuth, igual que el resto de derivaciones de hash del fichero.
        /// </summary>
        private static uint SlotSalt(uint salt, int slot) =>
            slot == 0 ? salt : salt ^ (uint)(slot * 0x9E3779B1u);

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
