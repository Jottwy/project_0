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
    /// recién clonada reproduce el mismo mundo. Cuando el autorado venga de un asset, esta clase se
    /// borra entera y nada más cambia: la composición ya solo mira huella, bocas y volúmenes.
    ///
    /// El catálogo ejercita a propósito las reglas que el sistema tiene que respetar:
    ///  · L1  bocas descentradas y a distinta altura del lado en casi todas las salas;
    ///  · L2  una nave de 42 × 30 vacía, demasiado grande para lo que hace;
    ///  · L4  una sala con núcleo cerrado que hay que rodear;
    ///  · L6  dos anchuras (pasillo 2,4 m y ancho 5 m) con su pieza de transición obligatoria;
    ///  · L8  una escalera DENTRO de una sala, que sube a una plataforma que no lleva a ningún sitio;
    ///  · L10 cuatro clases de escala, de 2,4 m de ancho a 42 m;
    ///  · L12 callejones y una alcoba que no llevan a ningún sitio;
    ///  · L13 paredes parciales que no cierran ningún rectángulo;
    ///  · L14 columnas con colisión exacta, que parten salas y tapan vistas;
    ///  · L21 las dos piezas de una sola boca son, además, los tapones de sus tipos.
    ///
    /// LO QUE NO EJERCITA: cotas de conexión. Todas las bocas nacen a 0 m. Las rampas son F5, y
    /// hasta entonces el validador exige que casen, así que un descuido aquí sale como pieza que no
    /// se coloca, no como agujero por el que caerse.
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

            // L11 + L13 — el recodo corta la línea de visión con dos paredes que no cierran nada.
            // La de x = 5,6 no llega al techo del plano: acaba contra el aire, y es lo que produce
            // la esquina ciega en la que no sabes si hay algo.
            c.Add(new Wg3Piece
            {
                id = "cor_bend", geometryId = "cor_bend",
                sizeX = 9f, sizeZ = 9f, heightMeters = DefaultCeiling,
                scale = Wg3Scale.Narrow, weight = 1.3f,
                sockets = new[]
                {
                    Sock(3, 6.4f, Wg3SocketType.Corridor),
                    Sock(2, 5.8f, Wg3SocketType.Corridor)
                },
                blocks = new[]
                {
                    new Wg3Block(5.6f, 2.6f, 0.16f, 5.2f, DefaultCeiling),
                    new Wg3Block(1.2f, 4.0f, 2.4f, 0.16f, DefaultCeiling)
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
                },
                pillars = new[]
                {
                    new Wg3Pillar(5.3f, 2.5f, 0.35f),
                    new Wg3Pillar(10.7f, 2.5f, 0.35f)
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
                },
                pillars = new[] { new Wg3Pillar(9.5f, 3.0f, 0.5f) }
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
                },
                pillars = new[]
                {
                    new Wg3Pillar(5f, 4f, 0.55f), new Wg3Pillar(5f, 11f, 0.55f),
                    new Wg3Pillar(13f, 4f, 0.55f), new Wg3Pillar(13f, 11f, 0.55f)
                }
            });

            // L4 — la que lleva una estructura cerrada dentro y obliga a rodearla. Profundidad
            // mínima 1 para que no salga de semilla: como primera pieza del mundo se lee como
            // error de geometría en vez de como decisión.
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
                },
                blocks = new[] { new Wg3Block(11.5f, 9f, 8f, 7f, 3.6f) }
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
                },
                blocks = new[]
                {
                    new Wg3Block(10f, 19.5f, 0.18f, 9f, 4.5f),
                    new Wg3Block(22f, 3.5f, 0.18f, 7f, 4.5f)
                },
                pillars = new[] { new Wg3Pillar(16f, 11f, 0.6f), new Wg3Pillar(26f, 17f, 0.6f) }
            });

            // L2 — demasiado grande para lo que hace, y vacía salvo cuatro columnas. Ese es el
            // punto entero de la pieza: no hay nada que "aprovechar" el espacio.
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
                },
                pillars = new[]
                {
                    new Wg3Pillar(12f, 10f, 0.65f), new Wg3Pillar(26f, 10f, 0.65f),
                    new Wg3Pillar(12f, 21f, 0.65f), new Wg3Pillar(26f, 21f, 0.65f)
                }
            });

            // L8 — la escalera vive DENTRO de una sala y sube a una plataforma que no conecta con
            // nada. En F0 las dos bocas siguen a cota 0 (el desnivel entre PIEZAS es F5), pero la
            // verticalidad DENTRO de la pieza ya es real y ya colisiona: se sube andando.
            c.Add(new Wg3Piece
            {
                id = "room_stair", geometryId = "room_stair",
                sizeX = 21f, sizeZ = 17f, heightMeters = 6.0f,
                scale = Wg3Scale.Weird, weight = 0.5f, minDepth = 2,
                sockets = new[]
                {
                    Sock(3, 4.2f, Wg3SocketType.Corridor),
                    Sock(1, 4.5f, Wg3SocketType.Corridor)
                },
                stairs = new[] { new Wg3StairRun(7f, 5.5f, 0f, 3f, 12) },
                blocks = new[] { new Wg3Block(8.5f, 11.5f, 6f, 5f, 0.22f, 2.16f) }
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
                },
                blocks = new[]
                {
                    new Wg3Block(11f, 13f, 0.18f, 10f, 4f),
                    new Wg3Block(14.5f, 8f, 7f, 0.18f, 4f)
                },
                pillars = new[] { new Wg3Pillar(6f, 13f, 0.5f) }
            });

            // L12 + L21 — dead space Y tapón de pasillo. Que el sello sea una pieza habitable en
            // vez de una pared lisa es lo que hace que un callejón parezca arquitectura sobrante
            // en lugar de un borde del mapa.
            c.Add(new Wg3Piece
            {
                id = "dead_corridor", geometryId = "dead_corridor",
                sizeX = 9f, sizeZ = 7f, heightMeters = DefaultCeiling,
                scale = Wg3Scale.Narrow, weight = 0.55f, minDepth = 2, isDeadEnd = true,
                sockets = new[] { Sock(3, 3.5f, Wg3SocketType.Corridor) },
                blocks = new[] { new Wg3Block(7f, 3.5f, 1.2f, 3f, DefaultCeiling) }
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
