// Partición de GridChunkBuilder: salas AUTORADAS a mano (Fase 2 del RoomPool). El resto
// vive en GridChunkBuilder.cs (raíz), .Placement.cs, .WallVariants.cs, .Tinting.cs y
// .Props.cs. TODOS los campos estáticos (incl. _roomPool y los scratch) viven en el raíz:
// el orden de inicialización de estáticos entre partials es indefinido.
using System.Collections.Generic;
using BackroomsSurvival.Net;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    public static partial class GridChunkBuilder
    {
        /// <summary>
        /// Una sala del pool ya resuelta contra un rect concreto del chunk: qué prefab, dónde
        /// y con qué giro. Rect en TILES de 5 m, con `tx1`/`tz1` EXCLUSIVOS (misma convención
        /// que <see cref="RoomZoneMsg"/>, que sí viene en celdas de 2.5 m).
        /// </summary>
        private struct RoomPlan
        {
            public GameObject prefab;
            public Vector3 localCenter;
            public float yaw;
            public int tx0, tz0, tx1, tz1;

            public bool ContainsTile(int tx, int tz) =>
                tx >= tx0 && tx < tx1 && tz >= tz0 && tz < tz1;
        }

        /// <summary>El pool autorado, o null si nadie ha horneado una sala todavía.</summary>
        private static RoomPool AuthoredRoomPool()
        {
            if (_roomPoolLoaded)
                return _roomPool;
            _roomPoolLoaded = true;
            _roomPool = Resources.Load<RoomPool>("Rooms/RoomPool");
            return _roomPool;
        }

        /// <summary>
        /// Decide qué salas autoradas entran en este chunk. PURO: no instancia nada y no toca
        /// el `rng` por tile de <see cref="BuildFromWalls"/> — se resuelve con los mismos
        /// hashes deterministas que ya usan props y variantes de pared, así que dos clientes
        /// con la misma seed colocan la MISMA sala con el MISMO giro sin que nada viaje por el
        /// wire. Un `Random.Range` aquí sería un desync visual entre jugadores.
        ///
        /// Solo se consideran zonas <see cref="RoomZoneKind.SealedRoom"/>: son las que el
        /// backend talla con perímetro cerrado y apertura(s) conocidas, o sea las únicas donde
        /// meter geometría autorada no puede contradecir lo que Rust cree que es caminable.
        ///
        /// La sala autorada AMUEBLA el interior; NO sustituye suelo, techo ni las paredes del
        /// perímetro. Esas las sigue poniendo el generador desde el bitmask del backend, que
        /// es la autoridad de colisión (ver GridCell.cs). Sustituirlas sería la Fase 3 y pide
        /// su propia decisión.
        /// </summary>
        private static void PlanAuthoredRooms(byte[,] walls, RoomZoneMsg[] roomZones,
            int chunkX, int chunkZ, List<RoomPlan> into)
        {
            into.Clear();
            if (roomZones == null || roomZones.Length == 0) return;
            var pool = AuthoredRoomPool();
            if (pool == null || pool.rooms == null || pool.rooms.Length == 0) return;

            for (int i = 0; i < roomZones.Length; i++)
            {
                var z = roomZones[i];
                if (z.Kind != RoomZoneKind.SealedRoom) continue;

                // Celdas de 2.5 m → tiles de 5 m. Un rect que no cae en frontera de tile no se
                // puede amueblar con una pieza autorada (quedaría a media pared), y descartarlo
                // es correcto: el generador procedural lo llena como siempre.
                if ((z.x0 & 1) != 0 || (z.z0 & 1) != 0 || (z.x1 & 1) != 0 || (z.z1 & 1) != 0)
                    continue;

                int tx0 = z.x0 / 2, tz0 = z.z0 / 2;
                int tw = (z.x1 - z.x0) / 2, th = (z.z1 - z.z0) / 2;
                if (tw < 1 || th < 1) continue;

                byte apertures = ApertureSides(walls, tx0, tz0, tw, th);
                if (apertures == 0) continue; // sala sin entrada conocida — no se amuebla

                // Candidatas: (entrada del pool, giro) cuyo footprint casa con el rect Y cuya
                // puerta mira a un lado que de verdad está abierto.
                _roomFitScratch.Clear();
                for (int e = 0; e < pool.rooms.Length; e++)
                {
                    var entry = pool.rooms[e];
                    if (entry == null || entry.prefab == null) continue;
                    if (entry.tilesX < 1 || entry.tilesZ < 1) continue;

                    for (int q = 0; q < 4; q++)
                    {
                        float yaw = q * 90f;
                        bool swapped = q == 1 || q == 3;
                        int fw = swapped ? entry.tilesZ : entry.tilesX;
                        int fh = swapped ? entry.tilesX : entry.tilesZ;
                        if (fw != tw || fh != th) continue;

                        byte side = SideOf(Quaternion.Euler(0f, yaw, 0f) * entry.doorLocalForward);
                        if (side == 0 || (apertures & side) == 0) continue;

                        _roomFitScratch.Add((e, yaw));
                    }
                }
                if (_roomFitScratch.Count == 0) continue;

                // Coordenadas de tile GLOBALES, igual que los hashes de props: así la elección
                // depende de DÓNDE está la sala en el mundo y no de qué chunk la trae.
                int gx = chunkX * Tiles + tx0, gz = chunkZ * Tiles + tz0;
                int pick = Mathf.Clamp(
                    (int)(Hash01(gx, gz, RoomSaltPick) * _roomFitScratch.Count),
                    0, _roomFitScratch.Count - 1);
                var (entryIndex, chosenYaw) = _roomFitScratch[pick];

                into.Add(new RoomPlan
                {
                    prefab = pool.rooms[entryIndex].prefab,
                    // El pivote de la sala es el CENTRO de su footprint (contrato de la
                    // herramienta de autoría): con el centro, girar 90° no descoloca la pieza.
                    localCenter = new Vector3((tx0 + tw * 0.5f) * Ts, 0f, (tz0 + th * 0.5f) * Ts),
                    yaw = chosenYaw,
                    tx0 = tx0,
                    tz0 = tz0,
                    tx1 = tx0 + tw,
                    tz1 = tz0 + th,
                });
            }
        }

        /// <summary>
        /// Qué lados del rect [tx0,tx0+tw) × [tz0,tz0+th) tienen AL MENOS un tile sin pared, o
        /// sea por dónde se entra. Devuelve una combinación de EdgeSouth/North/West/East.
        ///
        /// El bitmask del backend solo da los paneles +Z (<c>BackendBitS</c>) y +X
        /// (<c>BackendBitE</c>) de cada tile: el panel −Z de un tile es el +Z de su vecino de
        /// abajo, y el −X es el +X del vecino de la izquierda (regla de no-duplicación, ver
        /// BuildFromWalls). Por eso los lados sur y oeste se leen en `tz0−1` / `tx0−1`, y si
        /// esa fila cae fuera del chunk el lado NO se declara abierto: el vecino no ha llegado
        /// y afirmar una apertura que no existe pondría la puerta contra un muro.
        /// </summary>
        private static byte ApertureSides(byte[,] walls, int tx0, int tz0, int tw, int th)
        {
            byte sides = 0;
            int tx1 = tx0 + tw, tz1 = tz0 + th;
            if (tx0 < 0 || tz0 < 0 || tx1 > Tiles || tz1 > Tiles) return 0;

            for (int tx = tx0; tx < tx1; tx++)
            {
                if ((walls[tx, tz1 - 1] & BackendBitS) == 0) sides |= EdgeNorth;
                if (tz0 - 1 >= 0 && (walls[tx, tz0 - 1] & BackendBitS) == 0) sides |= EdgeSouth;
            }
            for (int tz = tz0; tz < tz1; tz++)
            {
                if ((walls[tx1 - 1, tz] & BackendBitE) == 0) sides |= EdgeEast;
                if (tx0 - 1 >= 0 && (walls[tx0 - 1, tz] & BackendBitE) == 0) sides |= EdgeWest;
            }
            return sides;
        }

        /// <summary>
        /// Lado del tile al que apunta <paramref name="forward"/>, por su eje dominante.
        /// 0 si el vector es degenerado (una puerta autorada sin dirección) — el llamador lo
        /// trata como "esta orientación no vale", nunca como un lado por defecto.
        /// </summary>
        private static byte SideOf(Vector3 forward)
        {
            float ax = Mathf.Abs(forward.x), az = Mathf.Abs(forward.z);
            if (ax < 1e-3f && az < 1e-3f) return 0;
            if (ax >= az) return forward.x >= 0f ? EdgeEast : EdgeWest;
            return forward.z >= 0f ? EdgeNorth : EdgeSouth;
        }

        /// <summary>Instancia las salas ya planificadas bajo el root del chunk.</summary>
        private static void PlaceAuthoredRooms(Transform parent, List<RoomPlan> plans)
        {
            for (int i = 0; i < plans.Count; i++)
                Instantiate(plans[i].prefab, parent, plans[i].localCenter, plans[i].yaw);
        }

        /// <summary>True si (tx, tz) cae dentro de alguna sala autorada de este chunk.</summary>
        private static bool IsAuthoredRoomTile(List<RoomPlan> plans, int tx, int tz)
        {
            for (int i = 0; i < plans.Count; i++)
                if (plans[i].ContainsTile(tx, tz)) return true;
            return false;
        }
    }
}
