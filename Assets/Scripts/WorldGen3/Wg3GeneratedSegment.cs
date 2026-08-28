using System;
using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// Una boca de un tramo generada. MISMA parametrización que <see cref="Wg3Socket"/>: lado más
    /// offset recorriendo el perímetro en horario desde (0, D), en centímetros enteros.
    /// </summary>
    [Serializable]
    public struct Wg3SegmentOpening
    {
        /// <summary>0 = N (+Z), 1 = E (+X), 2 = S (−Z), 3 = O (−X).</summary>
        public int side;

        /// <summary>Centímetros a lo largo del lado, hasta el CENTRO de la boca.</summary>
        public int offsetCm;

        public int widthCm;

        public Wg3SegmentOpening(int side, int offsetCm, int widthCm)
        {
            this.side = side;
            this.offsetCm = offsetCm;
            this.widthCm = widthCm;
        }
    }

    /// <summary>
    /// ADR-098 — un rectángulo GENERADO por el servidor, con sus bocas.
    ///
    /// Es lo que el catálogo no puede dar: un conector con la longitud, el quiebro y el ancho que
    /// hagan falta, tendido entre dos bocas que no se alinean. Los tres síntomas que arregla —islas,
    /// bucles y juntas que no llevan a ninguna parte— son el mismo problema: dos bocas solo se
    /// conectan si coinciden CLAVADAS, y no coinciden nunca (medido en la enmienda 2 de ADR-096: de
    /// los 23–258 pares que llegan a mirarse de frente, ninguno con desvío lateral menor de 2 cm).
    ///
    /// EN CENTÍMETROS ENTEROS, igual que <see cref="Wg3PlacementMsg"/>: esto viaja por el cable y se
    /// compara entre dos procesos. Una cadena de sumas en float no garantiza que coincidan.
    /// </summary>
    [Serializable]
    public sealed class Wg3Segment
    {
        public int xCm;
        public int zCm;
        public int sizeXCm;
        public int sizeZCm;

        /// <summary>Cota del SUELO, en centímetros de mundo (ADR-097).</summary>
        public int floorYCm;

        /// <summary>Altura LIBRE, de suelo a techo. La losa de techo va por encima.</summary>
        public int heightCm;

        public Wg3SegmentOpening[] openings = Array.Empty<Wg3SegmentOpening>();

        /// <summary>Aspecto. El servidor no lo interpreta: es el gancho para que el cliente vista
        /// los conectores y el mundo no se lea generado.</summary>
        public byte style;

        public float MinX => xCm * 0.01f;
        public float MinZ => zCm * 0.01f;
        public float SizeX => sizeXCm * 0.01f;
        public float SizeZ => sizeZCm * 0.01f;
        public float FloorY => floorYCm * 0.01f;
        public float Height => heightCm * 0.01f;

        public Vector3 Origin => new Vector3(MinX, FloorY, MinZ);
    }

    /// <summary>
    /// De tramo a volúmenes. **No reimplementa nada**: construye una <see cref="Wg3Piece"/>
    /// sintética y llama a <see cref="Wg3Geometry.Build"/>, que es la fuente única (R2).
    ///
    /// POR QUÉ ASÍ Y NO ESCRIBIENDO LA GEOMETRÍA AQUÍ. La regla —losa de suelo, losa de techo, y en
    /// cada lado la pared partida por sus bocas— ya está escrita, probada y es la que da el aspecto
    /// del catálogo: mismo grosor de losa, mismo grosor de pared, mismo rodapié. Una segunda copia
    /// se desviaría el día que alguien toque una de las dos, y el síntoma sería una junta visible
    /// entre pieza y conector, que es justo lo que R31 dice que delata un mundo modular.
    ///
    /// Lo que sí está escrito dos veces es la versión de Rust (`wg3::cell`), porque el servidor tiene
    /// que rasterizar la colisión sin ver una malla (R1). Esa partida doble la vigila el oráculo de
    /// conectores, y este lado es el AUTOR: el fixture sale de aquí.
    /// </summary>
    public static class Wg3GeneratedSegment
    {
        /// <summary>Lado máximo de un tramo. Espejo de `wg3::cell::MAX_SEGMENT_M`.
        ///
        /// **Es lo que deja intacto el reparto por chunk**: «una pieza, un chunk» se sostiene sobre
        /// que nada llega a los 50 m del chunk, así que centrado nunca asoma más allá de los vecinos
        /// inmediatos de su dueño. Una ruta larga se parte en más tramos, que es gratis.</summary>
        public const float MaxSegmentMeters = 25f;

        /// <summary>La pieza sintética equivalente a un tramo. Sin volúmenes horneados, sin
        /// pilares, sin bloques: así <see cref="Wg3Geometry.Build"/> la construye por la regla
        /// general, que es exactamente lo que se quiere.</summary>
        public static Wg3Piece PieceFor(Wg3Segment cell)
        {
            var sockets = new Wg3Socket[cell.openings != null ? cell.openings.Length : 0];
            for (int i = 0; i < sockets.Length; i++)
            {
                Wg3SegmentOpening o = cell.openings[i];
                sockets[i] = new Wg3Socket(o.side, o.offsetCm * 0.01f, o.widthCm * 0.01f,
                    Wg3SocketType.Corridor, 0f, cell.Height);
            }

            return new Wg3Piece
            {
                id = "__cell",
                sizeX = cell.SizeX,
                sizeZ = cell.SizeZ,
                heightMeters = cell.Height,
                sockets = sockets
            };
        }

        /// <summary>
        /// Los volúmenes de un tramo YA en coordenadas de mundo.
        ///
        /// ORDEN: suelo, techo y luego los lados 0, 1, 2 y 3 con sus tramos de menor a mayor offset
        /// —el que impone <see cref="Wg3Geometry.Build"/>—, con el rodapié de cada pared detrás. El
        /// orden es parte del contrato del oráculo, que compara caja a caja.
        /// </summary>
        public static List<Wg3Volume> Build(Wg3Segment cell)
        {
            var volumes = new List<Wg3Volume>(8);
            if (cell == null) return volumes;

            List<Wg3Volume> local = Wg3Geometry.Build(PieceFor(cell));
            Vector3 origin = cell.Origin;
            for (int i = 0; i < local.Count; i++)
            {
                Wg3Volume v = local[i];
                v.center += origin;
                volumes.Add(v);
            }
            return volumes;
        }
    }
}
