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

        public float OriginX => originXCm * 0.01f;
        public float OriginZ => originZCm * 0.01f;

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
    public class Wg3ChunkMsg
    {
        public int cx;
        public int cz;
        public readonly List<Wg3PlacementMsg> placements = new List<Wg3PlacementMsg>();

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
                else r.Skip();
            }
            return m;
        }
    }
}
