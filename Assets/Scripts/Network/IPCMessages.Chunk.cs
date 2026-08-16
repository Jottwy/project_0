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
    /// ADR-068 — un trazo de spray: el recorrido continuo de la boquilla mientras el
    /// jugador mantiene el gatillo. Mirror de <c>world::spray::SprayStroke</c>.
    /// </summary>
    public struct SprayStrokeMsg
    {
        /// <summary>Índice en la paleta del backend (&lt; 16).</summary>
        public byte color;

        /// <summary>Grosor de boquilla, en la misma retícula de 256 que los puntos.</summary>
        public byte width;

        /// <summary>
        /// Puntos en espacio de LIENZO, X e Y INTERCALADOS (<c>[x0,y0,x1,y1,…]</c>) sobre
        /// una retícula de 256×256 — así que la longitud es siempre PAR y hay
        /// <c>points.Length / 2</c> puntos.
        ///
        /// Viaja como <c>bin</c> y no como lista de enteros, igual que la voz de ADR-046:
        /// un array de enteros cuesta ~1,5× los mismos bytes. Se lee con
        /// <see cref="MsgPackReader.ReadBin"/>, el mismo método.
        /// </summary>
        public byte[] points;

        /// <summary>Puntos reales del trazo — la MITAD de la longitud del blob.</summary>
        public int PointCount => points == null ? 0 : points.Length / 2;

        public static SprayStrokeMsg Parse(MsgPackReader r)
        {
            var s = new SprayStrokeMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "color")) s.color = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "width")) s.width = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "points")) s.points = r.ReadBin();
                else r.Skip();
            }
            return s;
        }
    }

    /// <summary>
    /// ADR-078 — un trozo del trazo que otro jugador está pintando AHORA. Mirror de
    /// <c>ipc::SprayDraftView</c>.
    ///
    /// A diferencia de <see cref="SprayMsg"/>, el ancla viaja en coordenadas de MUNDO: esto
    /// vive tres segundos y muere, así que no se paga la traducción a chunk-local (ADR-078
    /// decisión 4). Los puntos son pares (u, v) en MILÍMETROS sobre el plano que definen el
    /// ancla y el giro, como <c>i16</c> little-endian: 4 bytes por punto.
    /// </summary>
    public class SprayDraftMsg
    {
        public int painter;
        public long placeId;
        public byte layer;
        public float ax, ay, az;
        public float yaw;
        public byte color;
        public float width;

        /// <summary>
        /// Índice del primer punto DENTRO DEL TRAZO. Cero significa trazo NUEVO: es como el
        /// receptor sabe que hay que abrir una polilínea en vez de continuar la anterior, sin
        /// gastar un campo en decirlo.
        /// </summary>
        public int firstIndex;

        public byte[] pointsMm;

        public Vector3 Anchor => new Vector3(ax, ay, az);

        /// <summary>Puntos reales: cada uno son cuatro bytes (u, v como i16).</summary>
        public int PointCount => pointsMm == null ? 0 : pointsMm.Length / 4;

        /// <summary>Lee el punto <paramref name="i"/> en milímetros.</summary>
        public void PointAt(int i, out short u, out short v)
        {
            int o = i * 4;
            u = (short)(pointsMm[o] | (pointsMm[o + 1] << 8));
            v = (short)(pointsMm[o + 2] | (pointsMm[o + 3] << 8));
        }

        public static SprayDraftMsg Parse(MsgPackReader r, int remainingPairs)
        {
            var d = new SprayDraftMsg();
            for (int i = 0; i < remainingPairs; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "painter")) d.painter = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "place_id")) d.placeId = r.ReadInt();
                else if (MsgPackReader.Is(k, "layer")) d.layer = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "anchor"))
                {
                    int n = r.ReadArrayHeader();
                    for (int a = 0; a < n; a++)
                    {
                        float f = r.ReadFloat();
                        if (a == 0) d.ax = f;
                        else if (a == 1) d.ay = f;
                        else if (a == 2) d.az = f;
                    }
                }
                else if (MsgPackReader.Is(k, "yaw")) d.yaw = r.ReadFloat();
                else if (MsgPackReader.Is(k, "color")) d.color = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "width")) d.width = r.ReadFloat();
                else if (MsgPackReader.Is(k, "first_index")) d.firstIndex = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "points_mm")) d.pointsMm = r.ReadBin();
                else r.Skip();
            }
            return d;
        }
    }

    /// <summary>
    /// ADR-068 — una pintada colocada. Mirror de <c>world::spray::Spray</c>.
    ///
    /// OJO con las coordenadas: el backend la guarda ANCLADA AL CHUNK (<c>local_pos</c> con
    /// X/Z relativos al origen del chunk) porque con el displacement de ADR-067 la geometría
    /// viaja. El cliente NO deshace ese anclaje por su cuenta: usa
    /// <see cref="WorldPos"/>, que lo traduce en el punto de lectura igual que hace el
    /// backend. La Y no es chunk-local — los chunks parten el mundo en X/Z.
    /// </summary>
    public class SprayMsg
    {
        /// <summary>Lado del chunk en unidades de mundo — espejo de <c>utils::CHUNK_SIZE</c>.</summary>
        public const float ChunkSize = 50f;

        public uint id;
        public int cx;
        public int cz;
        public byte layer;

        /// <summary>X/Z LOCALES al chunk; Y es altura de mundo. Ver <see cref="WorldPos"/>.</summary>
        public float lx, ly, lz;

        /// <summary>
        /// Giro del lienzo en grados. CONVENIO, fijado por el render de S2: es la dirección hacia
        /// la que MIRA la pintada — hacia quien la lee, no hacia la pared. Quien pinte apuntando a
        /// una pared manda su propio yaw + 180.
        /// </summary>
        public float yaw;

        /// <summary>Ancho y alto del lienzo, en metros.</summary>
        public float sizeX = 1f, sizeY = 1f;

        /// <summary>PeerId del autor. Informativo — no concede derechos sobre la pintada.</summary>
        public ushort author;

        /// <summary>
        /// Tick del host al aceptarla. ES el orden de render: la más nueva se dibuja encima,
        /// que es cómo funciona tapar la pintada de otro (ADR-068 decisión 4).
        /// </summary>
        public ulong tick;

        /// <summary>NUNCA null — una pintada sin trazos no la acepta el host.</summary>
        public SprayStrokeMsg[] strokes = new SprayStrokeMsg[0];

        /// <summary>Posición de mundo de esta pintada HOY, derivada del anclaje al chunk.</summary>
        public UnityEngine.Vector3 WorldPos => new UnityEngine.Vector3(
            lx + cx * ChunkSize,
            ly,
            lz + cz * ChunkSize);

        /// <summary>Anidada (dentro de <c>chunk_data.sprays</c>): la pintada trae su map header.</summary>
        public static SprayMsg Parse(MsgPackReader r) => ParseFields(r, r.ReadMapHeader());

        /// <summary>
        /// Raíz (frame <c>spray_placed</c>): el map header y el par "type" YA los consumió
        /// <c>IPCClient.Dispatch</c>, y serde aplana la variante de tupla — los campos de la
        /// pintada vienen sueltos junto al tag, no anidados bajo una clave. Es la misma forma
        /// que ya tienen <c>world_state</c>, <c>chunk_data</c> y el <c>hello</c> que el backend
        /// fija byte a byte en su propio test.
        /// </summary>
        public static SprayMsg ParseFields(MsgPackReader r, int n)
        {
            var s = new SprayMsg();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "id")) s.id = (uint)r.ReadInt();
                else if (MsgPackReader.Is(k, "cx")) s.cx = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "cz")) s.cz = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "layer")) s.layer = (byte)r.ReadInt();
                else if (MsgPackReader.Is(k, "local_pos"))
                {
                    int c = r.ReadArrayHeader();
                    for (int j = 0; j < c; j++)
                    {
                        float v = r.ReadFloat();
                        if (j == 0) s.lx = v;
                        else if (j == 1) s.ly = v;
                        else if (j == 2) s.lz = v;
                    }
                }
                else if (MsgPackReader.Is(k, "yaw")) s.yaw = r.ReadFloat();
                else if (MsgPackReader.Is(k, "size"))
                {
                    int c = r.ReadArrayHeader();
                    for (int j = 0; j < c; j++)
                    {
                        float v = r.ReadFloat();
                        if (j == 0) s.sizeX = v;
                        else if (j == 1) s.sizeY = v;
                    }
                }
                else if (MsgPackReader.Is(k, "author")) s.author = (ushort)r.ReadInt();
                else if (MsgPackReader.Is(k, "tick")) s.tick = (ulong)r.ReadInt();
                else if (MsgPackReader.Is(k, "strokes"))
                {
                    int sc = r.ReadArrayHeader();
                    if (sc > 0)
                    {
                        s.strokes = new SprayStrokeMsg[sc];
                        for (int si = 0; si < sc; si++)
                            s.strokes[si] = SprayStrokeMsg.Parse(r);
                    }
                }
                else r.Skip();
            }
            return s;
        }
    }

    /// <summary>
    /// ADR-068 — frame raíz <c>spray_placed</c>: UNA pintada que el host acaba de aceptar.
    /// Llega suelta y no dentro de un roster, por lo mismo que en el backend: una pintada
    /// son ~1,9 KB y un puñado no cabría en el transporte.
    ///
    /// Es la ÚNICA vía por la que una pintada ajena aparece sin recargar el chunk — el
    /// <c>chunk_data</c> de esa pared ya viajó.
    /// </summary>
    public static class SprayPlacedMsg
    {
        public static SprayMsg Parse(MsgPackReader r, int remainingPairs) =>
            SprayMsg.ParseFields(r, remainingPairs);
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

        /// <summary>Instancia compartida para "sin pintadas" — la mayoría de los chunks.</summary>
        private static readonly SprayMsg[] NoSprays = new SprayMsg[0];

        /// <summary>
        /// ADR-068 — las pintadas de este chunk, en ORDEN DE RENDER (de la más antigua a la más
        /// nueva, para que la última tape a las anteriores). NUNCA null, mismo criterio que
        /// <see cref="roomZones"/>.
        ///
        /// A diferencia de <see cref="walls"/>, esto NO se deriva del seed: es estado de jugador
        /// que el host posee. Un chunk que nadie ha pintado no trae ni la clave.
        /// </summary>
        public SprayMsg[] sprays = NoSprays;

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
                else if (MsgPackReader.Is(k, "sprays"))
                {
                    // ADR-068: clave aditiva, ausente entera en un chunk sin pintar ⇒ queda el
                    // NoSprays compartido. Mismo patrón que room_zones.
                    int sc = r.ReadArrayHeader();
                    if (sc > 0)
                    {
                        m.sprays = new SprayMsg[sc];
                        for (int si = 0; si < sc; si++)
                            m.sprays[si] = SprayMsg.Parse(r);
                    }
                }
                else r.Skip();
            }
            return m;
        }
    }
}
