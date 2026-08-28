using System.Collections.Generic;

// Parte del espejo C# de los tipos de wire del IPC (backend/src/ipc/mod.rs).
// EL CONTRATO DE DECODIFICACION (cabecera de mapa propia, `else r.Skip()` obligatorio,
// defaults por omision, post-fixups fuera del bucle) esta enunciado UNA sola vez, en
// IPCMessages.cs. Leelo alli antes de tocar cualquier Parse de este fichero.

namespace BackroomsSurvival.Net
{
    // ──────────── WorldGen3 (ServerMessage::Wg3Chunk, "wg3_chunk") ────────────
    // ADR-095, wire v46. Independiente por completo de GridChunkDataMsg: WG2 y WG3 conviven
    // hasta el borrado, y compartir estructura obligaría a tocar el camino que se queda el día
    // que se borre el que se va (regla R4).

    /// <summary>
    /// Una pieza colocada. Espejo de <c>ipc::Wg3PlacementWire</c>.
    ///
    /// ONCE BYTES por el cable, y ésa es la propiedad que hace barato el paradigma entero: el
    /// catálogo horneado ya está en el build de las dos partes, así que solo viaja QUÉ pieza,
    /// girada CÓMO y puesta DÓNDE. La geometría no viaja nunca.
    /// </summary>
    public struct Wg3PlacementMsg
    {
        /// <summary>Índice en el catálogo horneado. La cadena `id` NO viaja.</summary>
        public int piece;

        /// <summary>Cuartos de vuelta, horario visto desde +Y. 0..3.</summary>
        public int rotation;

        /// <summary>
        /// Esquina mínima de la huella girada, en CENTÍMETROS ENTEROS.
        ///
        /// Enteros y no float porque este dato se compara entre dos procesos y tiene que
        /// coincidir bit a bit; un flotante acumulado a lo largo de una cadena de piezas no lo
        /// garantiza, y la divergencia saldría como una pared medio metro corrida en un solo
        /// cliente.
        /// </summary>
        public int originXCm;
        public int originZCm;

        /// <summary>ADR-097 — cota del SUELO de la pieza, en centímetros. La resuelve el compositor
        /// del servidor propagándola por el árbol; el cliente la recibe hecha porque no compone.</summary>
        public int originYCm;

        public float OriginX => originXCm * 0.01f;
        public float OriginZ => originZCm * 0.01f;
        public float OriginY => originYCm * 0.01f;

        public static Wg3PlacementMsg Parse(MsgPackReader r)
        {
            var p = new Wg3PlacementMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "piece")) p.piece = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "rotation")) p.rotation = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "origin_x_cm")) p.originXCm = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "origin_z_cm")) p.originZCm = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "origin_y_cm")) p.originYCm = (int)r.ReadInt();
                else r.Skip();
            }
            return p;
        }
    }

    /// <summary>
    /// El chunk de WorldGen3: qué piezas hay puestas y dónde. Espejo de <c>ipc::Wg3ChunkView</c>.
    ///
    /// SIN `layer`, y no es un olvido: con columnas de tramos (ADR-095 D2) la capa deja de existir
    /// como restricción de geometría, así que un chunk de WG3 es uno solo y cubre toda la altura.
    ///
    /// Una lista VACÍA es un resultado válido y frecuente —un chunk donde no cae ninguna pieza—, y
    /// el consumidor tiene que distinguirlo de "todavía no ha llegado". Es lo que separa un mundo
    /// con huecos de un mundo a medio cargar, y confundirlos deja al jugador esperando geometría
    /// que nunca va a existir.
    /// </summary>
    /// <summary>
    /// ADR-098 — una boca de un tramo generado. Espejo de <c>ipc::Wg3OpeningWire</c>.
    /// </summary>
    public struct Wg3OpeningMsg
    {
        /// <summary>0 = N (+Z), 1 = E (+X), 2 = S (−Z), 3 = O (−X).</summary>
        public int side;

        /// <summary>Centímetros a lo largo del lado, hasta el CENTRO de la boca.</summary>
        public int offsetCm;

        public int widthCm;

        public static Wg3OpeningMsg Parse(MsgPackReader r)
        {
            var o = new Wg3OpeningMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "side")) o.side = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "offset_cm")) o.offsetCm = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "width_cm")) o.widthCm = (int)r.ReadInt();
                else r.Skip();
            }
            return o;
        }
    }

    /// <summary>
    /// ADR-098 — un TRAMO generado. Espejo de <c>ipc::Wg3SegmentWire</c>.
    ///
    /// **Es lo único de WG3 que no es un índice de catálogo.** Un conector no se elige de la
    /// biblioteca: lo genera el servidor con la longitud, los quiebros y el ancho que hagan falta
    /// para unir dos bocas que no se alinean, así que ninguna de las dos partes puede tenerlo
    /// horneado. Viajan los NÚMEROS; la geometría la deriva cada lado con la misma regla —aquí,
    /// <see cref="BackroomsSurvival.WorldGen3.Wg3GeneratedSegment"/>— y de que no se desvíen responde
    /// el oráculo de conectores.
    ///
    /// No confundir con la celda del ráster de colisión (0,5 m) ni con la celda de rejilla de WG2:
    /// esto es una pieza rectangular que nadie dibujó.
    /// </summary>
    public class Wg3SegmentMsg
    {
        public int xCm;
        public int zCm;
        public int sizeXCm;
        public int sizeZCm;

        /// <summary>Cota del SUELO, en centímetros (ADR-097).</summary>
        public int floorYCm;

        /// <summary>Altura LIBRE, de suelo a techo.</summary>
        public int heightCm;

        /// <summary>Aspecto. El servidor no lo interpreta; el cliente sí puede.</summary>
        public int style;

        public readonly List<Wg3OpeningMsg> openings = new List<Wg3OpeningMsg>();

        public static Wg3SegmentMsg Parse(MsgPackReader r)
        {
            var s = new Wg3SegmentMsg();
            int n = r.ReadMapHeader();
            for (int i = 0; i < n; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "x_cm")) s.xCm = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "z_cm")) s.zCm = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "size_x_cm")) s.sizeXCm = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "size_z_cm")) s.sizeZCm = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "floor_y_cm")) s.floorYCm = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "height_cm")) s.heightCm = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "style")) s.style = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "openings"))
                {
                    int c = r.ReadArrayHeader();
                    for (int j = 0; j < c; j++) s.openings.Add(Wg3OpeningMsg.Parse(r));
                }
                else r.Skip();
            }
            return s;
        }
    }

    public class Wg3ChunkMsg
    {
        public int cx;
        public int cz;
        public readonly List<Wg3PlacementMsg> placements = new List<Wg3PlacementMsg>();

        /// <summary>ADR-098 — los tramos generados de los que este chunk es dueño. Vacío en la
        /// inmensa mayoría: un conector cruza el mundo de vez en cuando, no siempre.</summary>
        public readonly List<Wg3SegmentMsg> segments = new List<Wg3SegmentMsg>();

        public static Wg3ChunkMsg Parse(MsgPackReader r, int remainingPairs)
        {
            var m = new Wg3ChunkMsg();
            for (int i = 0; i < remainingPairs; i++)
            {
                var k = r.ReadKey();
                if (MsgPackReader.Is(k, "cx")) m.cx = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "cz")) m.cz = (int)r.ReadInt();
                else if (MsgPackReader.Is(k, "placements"))
                {
                    int c = r.ReadArrayHeader();
                    for (int j = 0; j < c; j++) m.placements.Add(Wg3PlacementMsg.Parse(r));
                }
                else if (MsgPackReader.Is(k, "segments"))
                {
                    int c = r.ReadArrayHeader();
                    for (int j = 0; j < c; j++) m.segments.Add(Wg3SegmentMsg.Parse(r));
                }
                else r.Skip();
            }
            return m;
        }
    }
}
