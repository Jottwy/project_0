using System;
using System.Collections.Generic;
using UnityEngine;

// Parte del espejo C# de los tipos de wire del IPC (backend/src/ipc/mod.rs).
// EL CONTRATO DE DECODIFICACION (cabecera de mapa propia, `else r.Skip()` obligatorio,
// defaults por omision, post-fixups fuera del bucle) esta enunciado UNA sola vez, en
// IPCMessages.cs. Leelo alli antes de tocar cualquier Parse de este fichero.

namespace BackroomsSurvival.Net
{
    // ──────────── grid_gen chunk reply (ServerMessage::ChunkData, "chunk_data") ────────────
    // The live streaming path: ChunkStreamer asks for one chunk, the backend answers with its
    // 5 m tile-wall bitmask (+ ADR-034 room zones). Independent of the legacy ChunkViewMsg below.

    /// <summary>
    /// ADR-034 — tipo de zona estampada por la Fase 4 de grid_gen.
    ///
    /// Los valores son un CONTRATO con el enum `RoomType` del backend
    /// (backend/src/world/grid_gen/generator.rs, ver `RoomType::wire_kind`).
    /// Cambiar uno exige cambiar el Rust en el mismo commit.
    /// </summary>
    public enum RoomZoneKind : byte
    {
        Open = 0,
        SealedRoom = 1,
        CorridorSpine = 2,
    }

    /// <summary>
    /// ADR-034 — un rect estampado en la Fase 4, en coordenadas de CELDA (2.5 m)
    /// locales al chunk, con x1/z1 EXCLUSIVOS. Mirror de `grid_gen::RoomZone`.
    ///
    /// Ojo con la escala: el resto del render cliente trabaja en tiles de 5 m
    /// (`GridChunkDataMsg.Tiles` = 10 por lado); estas coordenadas son de celda
    /// (20 por lado). Un tile `tx` cubre las celdas `2*tx` y `2*tx + 1`.
    /// </summary>
    public struct RoomZoneMsg
    {
        public byte x0, z0, x1, z1;

        /// <summary>Discriminante crudo del wire; ver <see cref="Kind"/>.</summary>
        public byte kindByte;

        /// <summary>
        /// Accesor con tipo. Un valor desconocido (backend más nuevo que este
        /// cliente) colapsa a <see cref="RoomZoneKind.Open"/> — el tipo sin
        /// perímetro sellado, o sea el comportamiento histórico previo a
        /// RoomType. Mismo criterio de degradación que <c>GridCell.Kind</c>.
        /// </summary>
        public RoomZoneKind Kind =>
            kindByte <= (byte)RoomZoneKind.CorridorSpine
                ? (RoomZoneKind)kindByte
                : RoomZoneKind.Open;

        /// <summary>True si la celda (cx, cz) del chunk cae dentro del rect.</summary>
        public bool ContainsCell(int cellX, int cellZ) =>
            cellX >= x0 && cellX < x1 && cellZ >= z0 && cellZ < z1;

        public static RoomZoneMsg Parse(MsgPackReader r)
        {
            var z = new RoomZoneMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "x0")) z.x0 = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "z0")) z.z0 = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "x1")) z.x1 = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "z1")) z.z1 = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "kind")) z.kindByte = (byte)r.ReadInt();
                else r.Skip();
            }
            return z;
        }
    }

    /// <summary>
    /// Fase 4.1 — backend grid_gen chunk reply (ServerMessage::ChunkData, tag
    /// "chunk_data"). A 10×10 grid of 5 m tiles, each an edge-wall bitmask in the
    /// BACKEND convention: N=1 (−Z), S=2 (+Z), E=4 (+X), W=8 (−X). walls[x,z].
    /// </summary>
    public class GridChunkDataMsg
    {
        public const int Tiles = 10;
        public const byte WallN = 1; // −Z
        public const byte WallS = 2; // +Z
        public const byte WallE = 4; // +X
        public const byte WallW = 8; // −X

        /// <summary>Instancia compartida para "sin zonas" — evita alocar por chunk.</summary>
        private static readonly RoomZoneMsg[] NoRoomZones = new RoomZoneMsg[0];

        public int cx;
        public int cz;
        public byte layer;
        public byte[,] walls = new byte[Tiles, Tiles];

        /// <summary>
        /// ADR-034 — rects de Fase 4 con su tipo de sala. NUNCA null: un backend
        /// que no manda la clave (versión anterior al ADR, o chunk sin zonas)
        /// deja el array VACÍO, así que el consumidor solo tiene que tratar el
        /// caso "ninguna zona cubre este tile", no un caso de nulo aparte.
        /// </summary>
        public RoomZoneMsg[] roomZones = NoRoomZones;

        /// <summary>
        /// Root-tagged message (ServerMessage::ChunkData, "type":"chunk_data") — reads the
        /// REMAINING <paramref name="remainingPairs"/> pairs after IPCClient.Dispatch already
        /// consumed the map header and the "type" pair.
        /// </summary>
        public static GridChunkDataMsg Parse(MsgPackReader r, int remainingPairs)
        {
            var m = new GridChunkDataMsg();
            for (int i = 0; i < remainingPairs; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "cx")) m.cx = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "cz")) m.cz = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "layer")) m.layer = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "walls"))
                {
                    // Backend [[u8;10];10]. Consume every row/col the wire actually sent (keeps
                    // the cursor in sync for the fields after this one) but only STORE within the
                    // 10×10 contract — same clamp as the old Mathf.Min(rows.Length, Tiles).
                    int rows = r.ReadArrayHeader();
                    if (rows < 0) rows = 0;
                    for (int x = 0; x < rows; x++)
                    {
                        int cols = r.ReadArrayHeader();
                        if (cols < 0) cols = 0;
                        for (int z = 0; z < cols; z++)
                        {
                            byte v = (byte)r.ReadInt();
                            if (x < Tiles && z < Tiles) m.walls[x, z] = v;
                        }
                    }
                }
                else if (MsgPackReader.Is(k, "room_zones"))
                {
                    // ADR-034: additive key, absent entirely on a chunk with no zones (or a
                    // pre-ADR backend) ⇒ m.roomZones stays the shared NoRoomZones default.
                    int zc = r.ReadArrayHeader();
                    if (zc > 0)
                    {
                        m.roomZones = new RoomZoneMsg[zc];
                        for (int zi = 0; zi < zc; zi++)
                            m.roomZones[zi] = RoomZoneMsg.Parse(r);
                    }
                }
                else r.Skip();
            }
            return m;
        }
    }
}
