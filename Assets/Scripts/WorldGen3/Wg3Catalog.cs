using System.Collections.Generic;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// Catálogo de arranque de WG3: las piezas mínimas para que la composición tenga algo que
    /// componer en F0.
    ///
    /// EN CÓDIGO A PROPÓSITO, y solo por ahora. El catálogo definitivo vive en assets autorados con
    /// la herramienta de salas (REGLA L18: la irregularidad se autora, no se genera). Definirlo aquí
    /// deja F0 sin dependencia de ningún asset, así que los tests corren en frío y una máquina
    /// recién clonada reproduce el mismo mundo. La tanda 2 le cuelga a cada entrada su
    /// <c>RoomDefinition</c> por <see cref="Wg3Piece.geometryId"/> sin tocar una línea de aquí.
    ///
    /// El catálogo ya ejercita las reglas que la composición tiene que respetar:
    ///  · L1  bocas descentradas y a distinta altura del lado en casi todas las salas;
    ///  · L6  dos anchuras (pasillo 2,4 m y ancho 5 m) con su pieza de transición obligatoria;
    ///  · L10 cuatro clases de escala, de 2,4 m de ancho a una nave de 42 × 30;
    ///  · L12 callejones y una alcoba que no llevan a ningún sitio;
    ///  · L21 las dos piezas de una sola boca son, además, los tapones de sus tipos.
    ///
    /// LO QUE NO EJERCITA: cotas. Todas las bocas nacen a 0 m. Las rampas son F5, y hasta entonces
    /// el validador exige que las cotas casen, así que un descuido aquí sale como pieza no colocada,
    /// no como agujero.
    /// </summary>
    public static class Wg3Catalog
    {
        /// <summary>Anchura de paso de pasillo. Dos personas no se cruzan: es una medida de
        /// tensión, no de tránsito.</summary>
        public const float CorridorWidth = 2.4f;

        /// <summary>Vano ancho. Cinco metros es lo que hace que una sala se lea como continuación
        /// del espacio y no como una habitación a la que se entra.</summary>
        public const float WideWidth = 5.0f;

        private const float DefaultCeiling = 3.2f;

        private static Wg3Socket Sock(int side, float offset, Wg3SocketType type) =>
            new Wg3Socket(side, offset,
                type == Wg3SocketType.Wide ? WideWidth : CorridorWidth,
                type, 0f, DefaultCeiling);

        /// <summary>El catálogo de F0. El PRIMER elemento es la pieza semilla: cambiarlo mueve
        /// todos los mundos ya generados, así que no se reordena a la ligera.</summary>
        public static List<Wg3Piece> Build()
        {
            var c = new List<Wg3Piece>();

            c.Add(new Wg3Piece
            {
                id = "cor_straight", geometryId = "cor_straight",
                sizeX = 11f, sizeZ = CorridorWidth, heightMeters = DefaultCeiling,
                scale = Wg3Scale.Narrow, weight = 1.5f,
                sockets = new[]
                {
                    Sock(3, CorridorWidth * 0.5f, Wg3SocketType.Corridor),
                    Sock(1, CorridorWidth * 0.5f, Wg3SocketType.Corridor)
                }
            });

            c.Add(new Wg3Piece
            {
                id = "cor_long", geometryId = "cor_long",
                sizeX = 26f, sizeZ = CorridorWidth, heightMeters = DefaultCeiling,
                scale = Wg3Scale.Narrow, weight = 1.2f,
                sockets = new[]
                {
                    Sock(3, CorridorWidth * 0.5f, Wg3SocketType.Corridor),
                    Sock(1, CorridorWidth * 0.5f, Wg3SocketType.Corridor)
                }
            });

            // L11 — el recodo es la pieza que corta la línea de visión sin construir un laberinto.
            c.Add(new Wg3Piece
            {
                id = "cor_bend", geometryId = "cor_bend",
                sizeX = 9f, sizeZ = 9f, heightMeters = DefaultCeiling,
                scale = Wg3Scale.Narrow, weight = 1.3f,
                sockets = new[]
                {
                    Sock(3, 6.4f, Wg3SocketType.Corridor),
                    Sock(2, 5.8f, Wg3SocketType.Corridor)
                }
            });

            // L6 — sin esta pieza, pasillo y vano ancho viven en mundos separados y el catálogo
            // se parte en dos grafos que no se tocan.
            c.Add(new Wg3Piece
            {
                id = "cor_transition", geometryId = "cor_transition",
                sizeX = 9f, sizeZ = WideWidth, heightMeters = DefaultCeiling,
                scale = Wg3Scale.Narrow, weight = 0.8f,
                sockets = new[]
                {
                    Sock(3, WideWidth * 0.5f, Wg3SocketType.Corridor),
                    Sock(1, WideWidth * 0.5f, Wg3SocketType.Wide)
                }
            });

            c.Add(new Wg3Piece
            {
                id = "cor_wide", geometryId = "cor_wide",
                sizeX = 16f, sizeZ = WideWidth, heightMeters = 3.6f,
                scale = Wg3Scale.Medium, weight = 0.9f,
                sockets = new[]
                {
                    Sock(3, WideWidth * 0.5f, Wg3SocketType.Wide),
                    Sock(1, WideWidth * 0.5f, Wg3SocketType.Wide)
                }
            });

            // L1 — entra por abajo a la izquierda y sale por arriba a la derecha. La sala nunca
            // está centrada respecto al pasillo que la sirve.
            c.Add(new Wg3Piece
            {
                id = "room_small", geometryId = "room_small",
                sizeX = 13f, sizeZ = 10f, heightMeters = DefaultCeiling,
                scale = Wg3Scale.Medium, weight = 1.4f,
                sockets = new[]
                {
                    Sock(3, 2.2f, Wg3SocketType.Corridor),
                    Sock(1, 2.4f, Wg3SocketType.Corridor)
                }
            });

            c.Add(new Wg3Piece
            {
                id = "room_pillars", geometryId = "room_pillars",
                sizeX = 20f, sizeZ = 15f, heightMeters = 3.6f,
                scale = Wg3Scale.Medium, weight = 1.1f,
                sockets = new[]
                {
                    Sock(3, 3.4f, Wg3SocketType.Corridor),
                    Sock(1, 7.0f, Wg3SocketType.Wide),
                    Sock(2, 14.5f, Wg3SocketType.Corridor)
                }
            });

            // L4 — la que lleva una estructura cerrada dentro. Profundidad mínima 1 para que no
            // salga de semilla: como primera pieza del mundo se lee como error de geometría.
            c.Add(new Wg3Piece
            {
                id = "room_core", geometryId = "room_core",
                sizeX = 23f, sizeZ = 18f, heightMeters = 3.6f,
                scale = Wg3Scale.Medium, weight = 0.9f, minDepth = 1,
                sockets = new[]
                {
                    Sock(3, 10.5f, Wg3SocketType.Corridor),
                    Sock(1, 3.5f, Wg3SocketType.Corridor),
                    Sock(0, 16f, Wg3SocketType.Corridor)
                }
            });

            c.Add(new Wg3Piece
            {
                id = "hall_large", geometryId = "hall_large",
                sizeX = 34f, sizeZ = 24f, heightMeters = 4.5f,
                scale = Wg3Scale.Large, weight = 1.0f, minDepth = 1,
                sockets = new[]
                {
                    Sock(3, 9f, Wg3SocketType.Wide),
                    Sock(1, 4.5f, Wg3SocketType.Corridor),
                    Sock(0, 21f, Wg3SocketType.Corridor)
                }
            });

            // L2 — demasiado grande para lo que hace. Es el punto entero de la pieza.
            c.Add(new Wg3Piece
            {
                id = "hall_void", geometryId = "hall_void",
                sizeX = 42f, sizeZ = 30f, heightMeters = 5.5f,
                scale = Wg3Scale.Large, weight = 0.7f, minDepth = 2,
                sockets = new[]
                {
                    Sock(3, 13f, Wg3SocketType.Wide),
                    Sock(1, 20f, Wg3SocketType.Wide),
                    Sock(2, 33f, Wg3SocketType.Corridor)
                }
            });

            // L8 — la escalera vive DENTRO de una sala, no es un tubo de transporte vertical. En
            // F0 sus dos bocas siguen a cota 0: la altura la enciende F5, y hasta entonces la
            // pieza aporta su silueta y su altura libre, no su desnivel.
            c.Add(new Wg3Piece
            {
                id = "room_stair", geometryId = "room_stair",
                sizeX = 21f, sizeZ = 17f, heightMeters = 6.0f,
                scale = Wg3Scale.Weird, weight = 0.5f, minDepth = 2,
                sockets = new[]
                {
                    Sock(3, 4.2f, Wg3SocketType.Corridor),
                    Sock(1, 4.5f, Wg3SocketType.Corridor)
                }
            });

            // R30 — la rareza se raciona. Si todo es raro, lo raro es la norma y el mundo vuelve
            // a leerse como aleatorio, que es justo lo que L22 quiere evitar.
            c.Add(new Wg3Piece
            {
                id = "room_weird", geometryId = "room_weird",
                sizeX = 18f, sizeZ = 18f, heightMeters = 4.0f,
                scale = Wg3Scale.Weird, weight = 0.35f, minDepth = 3,
                sockets = new[]
                {
                    Sock(3, 9f, Wg3SocketType.Corridor),
                    Sock(0, 4.5f, Wg3SocketType.Corridor)
                }
            });

            // L12 + L21 — dead space Y tapón de pasillo. Que el sello sea una pieza habitable en
            // vez de una pared lisa es lo que hace que un callejón parezca arquitectura sobrante
            // en lugar de un borde del mapa.
            c.Add(new Wg3Piece
            {
                id = "dead_corridor", geometryId = "dead_corridor",
                sizeX = 9f, sizeZ = 7f, heightMeters = DefaultCeiling,
                scale = Wg3Scale.Narrow, weight = 0.55f, minDepth = 2, isDeadEnd = true,
                sockets = new[] { Sock(3, 3.5f, Wg3SocketType.Corridor) }
            });

            // El tapón del tipo ancho. Sin él, el validador de catálogo protesta y con razón: un
            // vano de 5 m sin nada que lo cierre acaba abierto al vacío.
            c.Add(new Wg3Piece
            {
                id = "alcove_wide", geometryId = "alcove_wide",
                sizeX = 8f, sizeZ = WideWidth, heightMeters = 3.6f,
                scale = Wg3Scale.Medium, weight = 0.4f, minDepth = 1, isDeadEnd = true,
                sockets = new[] { Sock(3, WideWidth * 0.5f, Wg3SocketType.Wide) }
            });

            return c;
        }
    }
}
