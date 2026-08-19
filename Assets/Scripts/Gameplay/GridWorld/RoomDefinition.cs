using System;
using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// El MODELO de una sala autorada: qué forma tiene, no cómo está hecha. La malla se deriva
    /// de aquí (<see cref="RoomMeshBuilder"/>) y los colliders también, así que esto es la única
    /// fuente de verdad — mover un parámetro y volver a construir da siempre lo mismo.
    ///
    /// Que la malla se DERIVE en vez de editarse a mano es lo que permite lo que pidió Joel:
    /// arrastrar un boquete o una columna y ver la geometría rehacerse en vivo. Editar
    /// triángulos directamente haría eso imposible.
    ///
    /// Tamaño en TILES de 5 m (<see cref="GridVisualConstants.TileSize"/>), la misma unidad en
    /// la que piensa el mundo, no en metros sueltos.
    ///
    /// Fase A: solo el cascarón (planta, altura, grosor). Los features editables —boquetes,
    /// columnas, escaleras, rejillas— entran en las fases B/E y cuelgan de este mismo tipo.
    /// </summary>
    [Serializable]
    public sealed class RoomDefinition
    {
        [Min(1)] public int tilesX = 4;
        [Min(1)] public int tilesZ = 4;

        public float heightMeters = 4f;

        /// <summary>
        /// Lados del polígono de la planta. 4 = caja; subirlo redondea. Junto a
        /// <see cref="squareness"/> es el mando "más cuadrado ↔ más circular".
        /// </summary>
        [Range(MinSides, MaxSides)] public int sides = 4;

        /// <summary>
        /// 0 = polígono inscrito puro (redondo), 1 = rectángulo del footprint. Los valores
        /// intermedios interpolan, que es como se consigue una planta achaflanada o abombada
        /// sin tener dos generadores distintos.
        /// </summary>
        [Range(0f, 1f)] public float squareness = 1f;

        public float wallThickness = GridVisualConstants.WallThickness;

        /// <summary>
        /// Un boquete en una pared: puerta, ventana, paso roto.
        ///
        /// NO se coloca con x/y/z sueltos, y es deliberado: con coordenadas libres se puede
        /// dejar el hueco flotando fuera de la pared y ver el resultado sin entender por qué
        /// está mal. Aquí se dice EN QUÉ pared, en qué punto de ella y cuánto mide, así que
        /// siempre cae dentro; lo que se sale se recorta contra los bordes de la pared.
        /// </summary>
        [Serializable]
        public sealed class WallHole
        {
            /// <summary>Índice de pared, 0..sides-1. Se envuelve si la sala pierde lados.</summary>
            public int side;

            /// <summary>Centro a lo largo de la pared: 0 = una esquina, 1 = la otra.</summary>
            [Range(0f, 1f)] public float along = 0.5f;

            /// <summary>Altura del borde INFERIOR sobre el suelo, en metros. 0 = puerta.</summary>
            public float baseY;

            /// <summary>En METROS, no en fracción: se piensa en "una puerta de 1,2 m".</summary>
            public float width = 1.6f;
            public float height = 2.2f;

            /// <summary>Barrotes que cruzan el hueco. 0 = hueco limpio. Convierte una ventana en
            /// rejilla sin ser un tipo de feature aparte: la abertura ya está, solo se llena.</summary>
            [Range(0, 20)] public int grateBars;
        }

        /// <summary>Una columna dentro de la sala, del suelo al techo.</summary>
        [Serializable]
        public sealed class Pillar
        {
            /// <summary>Posición en XZ, en metros y relativa al centro de la sala.</summary>
            public Vector2 position;

            /// <summary>Diámetro en metros.</summary>
            public float size = 0.7f;

            /// <summary>4 = cuadrada, subirlo la redondea. Mismo mando que la planta.</summary>
            [Range(3, 32)] public int sides = 4;

            public float yawDegrees;
        }

        /// <summary>
        /// Un bloque suelto: caja con giro y altura propias. Es el comodín de la geometría rara
        /// —tabiques a medias, salientes, plataformas, poyetes, mesetas— sin necesitar un tipo de
        /// feature por cada cosa. Un bloque bajo es un escalón; uno alto y estrecho, un machón.
        /// </summary>
        [Serializable]
        public sealed class Block
        {
            public Vector2 position;
            public float sizeX = 2f;
            public float sizeZ = 0.6f;
            public float baseY;
            public float height = 2f;
            public float yawDegrees;
        }

        /// <summary>
        /// Un tramo de escalera. Cada peldaño es una caja maciza desde el suelo hasta su huella,
        /// no una losa flotante: así se sube andando sin lógica ninguna y la colisión sale de la
        /// misma forma que se ve.
        /// </summary>
        [Serializable]
        public sealed class Stairs
        {
            /// <summary>Centro del peldaño más bajo, en metros y relativo al centro de la sala.</summary>
            public Vector2 position;

            /// <summary>Hacia dónde sube.</summary>
            public float yawDegrees;

            public float width = 2f;
            [Range(1, 40)] public int steps = 8;

            /// <summary>Alto y fondo de cada peldaño. 0,18 × 0,28 es cómodo de subir.</summary>
            public float rise = 0.18f;
            public float run = 0.28f;
        }

        /// <summary>
        /// Una RETÍCULA de columnas como una sola cosa. Diez columnas sueltas se colocan bien una
        /// vez y son un infierno en cuanto hay que moverlas, alinearlas o cambiarles el tamaño:
        /// diez ediciones para un solo cambio de intención. Aquí se mueve el centro y se mueven
        /// las diez, se toca el paso y se reparten solas.
        ///
        /// Las columnas sueltas (<see cref="Pillar"/>) siguen existiendo para las excepciones —
        /// la del medio de una rotonda no es una retícula de 1×1.
        /// </summary>
        [Serializable]
        public sealed class PillarGrid
        {
            public Vector2 center;

            [Range(1, 12)] public int countX = 3;
            [Range(1, 12)] public int countZ = 3;

            /// <summary>Distancia entre ejes de columna, en metros.</summary>
            public float spacingX = 5f;
            public float spacingZ = 5f;

            public float size = 0.7f;
            [Range(3, 32)] public int sides = 4;

            /// <summary>Gira la retícula ENTERA, no cada columna por su cuenta.</summary>
            public float yawDegrees;

            /// <summary>Posición de la columna (ix, iz), ya girada y centrada.</summary>
            public Vector2 PositionOf(int ix, int iz)
            {
                float lx = (ix - (countX - 1) * 0.5f) * spacingX;
                float lz = (iz - (countZ - 1) * 0.5f) * spacingZ;
                float r = yawDegrees * Mathf.Deg2Rad;
                var ax = new Vector2(Mathf.Cos(r), -Mathf.Sin(r));
                var az = new Vector2(Mathf.Sin(r), Mathf.Cos(r));
                return center + ax * lx + az * lz;
            }
        }

        /// <summary>
        /// Un hueco en el SUELO: un pozo por el que se baja. Tamaño, profundidad y giro
        /// editables, igual que un bloque.
        ///
        /// A diferencia de un boquete de pared, éste obliga a triangular el suelo de verdad
        /// (<see cref="PolygonTriangulator"/>): el suelo es un polígono que puede ser redondo y
        /// sacarle un rectángulo no se arregla cortando en rejilla.
        /// </summary>
        [Serializable]
        public sealed class FloorHole
        {
            public Vector2 position;
            public float sizeX = 3f;
            public float sizeZ = 3f;

            /// <summary>Cuánto se baja desde el suelo de la sala, en metros.</summary>
            public float depth = 2.5f;

            public float yawDegrees;
        }

        public WallHole[] holes = Array.Empty<WallHole>();
        public Pillar[] pillars = Array.Empty<Pillar>();
        public PillarGrid[] pillarGrids = Array.Empty<PillarGrid>();
        public Block[] blocks = Array.Empty<Block>();
        public Stairs[] stairs = Array.Empty<Stairs>();
        public FloorHole[] floorHoles = Array.Empty<FloorHole>();

        /// <summary>
        /// Un boquete ya resuelto sobre una pared concreta: fracción a lo largo (u) y metros de
        /// altura (y). Vive aquí y no dentro de un generador porque lo usan LOS DOS —la malla y
        /// los colliders— y si cada uno tuviera el suyo podrían divergir, que es exactamente el
        /// fallo de "veo una puerta y me choco".
        /// </summary>
        public struct HoleRect
        {
            public float u0, u1, y0, y1;
            public int bars;

            /// <summary>
            /// Solape O CONTACTO. La tolerancia no es paranoia numérica: dos aberturas que se
            /// tocan justo en el borde dejarían entre ellas un machón de grosor cero, y ahí las
            /// dos jambas caen una encima de otra. Un pilar de 0 mm no es geometría — si las has
            /// puesto pegadas, lo que quieres es una abertura más ancha.
            /// </summary>
            public bool Overlaps(HoleRect o) =>
                u0 <= o.u1 + 1e-4f && o.u0 <= u1 + 1e-4f
                && y0 <= o.y1 + 1e-3f && o.y0 <= y1 + 1e-3f;
        }

        /// <summary>
        /// Funde los boquetes que se solapen en una misma pared, quedándose con el rectángulo que
        /// los envuelve.
        ///
        /// Hace falta porque cada boquete emite su propia JAMBA (el forro del grosor del muro), y
        /// dos huecos superpuestos emiten jambas que se cruzan dentro del hueco del otro: geometría
        /// que se pisa, y por ahí es por donde se cuela una grieta. La rejilla de la pared sí
        /// aguantaba el solape; las jambas no. Lo destapó una sala aleatoria que puso dos ventanas
        /// encima — a mano tampoco hay nada que lo impida.
        ///
        /// Fundir en vez de descartar uno: si has pedido dos aberturas que se tocan, lo que
        /// quieres es una abertura más grande, no perder una.
        /// </summary>
        public static void MergeOverlapping(List<HoleRect> rects)
        {
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < rects.Count && !merged; i++)
                    for (int j = i + 1; j < rects.Count && !merged; j++)
                    {
                        if (!rects[i].Overlaps(rects[j])) continue;
                        rects[i] = new HoleRect
                        {
                            u0 = Mathf.Min(rects[i].u0, rects[j].u0),
                            u1 = Mathf.Max(rects[i].u1, rects[j].u1),
                            y0 = Mathf.Min(rects[i].y0, rects[j].y0),
                            y1 = Mathf.Max(rects[i].y1, rects[j].y1),
                            // Los barrotes del fundido: si alguno los tenía, el hueco resultante
                            // los lleva. Perderlos al fundir dejaría una abertura pelada donde se
                            // había pedido rejilla.
                            bars = Mathf.Max(rects[i].bars, rects[j].bars),
                        };
                        rects.RemoveAt(j);
                        merged = true;
                    }
            }
        }

        public const int MinSides = 3;
        public const int MaxSides = 64;

        public float WidthMeters => tilesX * GridVisualConstants.TileSize;
        public float DepthMeters => tilesZ * GridVisualConstants.TileSize;

        /// <summary>
        /// Contorno INTERIOR de la sala, en sentido antihorario y centrado en el origen — el
        /// pivote es el centro del footprint, que es el contrato que asume el colocador para
        /// poder girar la pieza 0/90/180/270° sin descolocarla.
        ///
        /// La forma se resuelve en un espacio NORMALIZADO (cuadrado unidad) y solo al final se
        /// escala por el footprint. Eso no es una comodidad, es lo que hace que la planta
        /// cuadrada salga bien: muestreando rayos en el espacio real, un footprint 20 × 15 con
        /// `sides = 4` pone el rayo de 45° contra el borde corto y devuelve un CUADRADO de 15,
        /// no el rectángulo — costó un test rojo. En espacio unidad el rayo de 45° cae en la
        /// esquina (1, 1) por construcción, y al escalar da la esquina real (a, b).
        ///
        /// Cada vértice mezcla dos plantas: el círculo inscrito (redondo) y el cuadrado
        /// (anguloso). El ángulo de arranque es π/N para que con N = 4 los cuatro vértices
        /// caigan justo en las esquinas y `squareness = 1` dé el rectángulo EXACTO, no un rombo.
        /// </summary>
        /// <summary>
        /// Rellena este modelo con una sala al azar a partir de <paramref name="seed"/>.
        ///
        /// DETERMINISTA: la misma semilla da la misma sala siempre. Eso es lo que hace que
        /// "genérame una y la retoco" funcione — puedes volver a una que te gustó anotando el
        /// número, y un test puede fijar una semilla y comprobar el resultado.
        ///
        /// Sesgado a lo habitable, no a lo uniforme: plantas más anchas que altas, casi siempre
        /// cuadradas (una sala redonda es la excepción, no la norma, en unos Backrooms), y
        /// siempre AL MENOS una puerta a ras de suelo — una sala sin salida no sirve para nada.
        /// </summary>
        /// <summary>
        /// Cinco ARQUETIPOS. No es adorno: sorteando cada parametro por separado salian salas
        /// que se parecian todas entre si -- siempre medianas, siempre con algo de todo -- y el
        /// jugador no distingue una de otra. Eligiendo primero QUE ES la sala y decidiendo el
        /// resto en consecuencia, cada tirada sale reconocible: una nave de columnas no se
        /// confunde con un pasillo largo ni con una rotonda.
        /// </summary>
        public enum Archetype
        {
            PillarHall,     // nave ancha, reticula de columnas
            Partitioned,    // planta de oficinas: tabiques sueltos, ventanas
            MachineRoom,    // industrial: rejillas altas, escalera, bloques
            Rotunda,        // planta redonda, alta, columna central
            Corridor,       // largo y estrecho, puertas en los extremos
        }

        public Archetype LastArchetype { get; private set; }

        public void Randomize(int seed)
        {
            var rng = new System.Random(seed);
            float Range(float a, float b) => a + (float)rng.NextDouble() * (b - a);
            bool Chance(float pr) => rng.NextDouble() < pr;

            var kind = (Archetype)rng.Next(5);
            LastArchetype = kind;

            wallThickness = GridVisualConstants.WallThickness;
            sides = 4;
            squareness = Range(0.9f, 1f);

            switch (kind)
            {
                case Archetype.PillarHall:
                    tilesX = rng.Next(5, 9); tilesZ = rng.Next(4, 8);
                    heightMeters = Range(4.5f, 6.5f);
                    break;
                case Archetype.Partitioned:
                    tilesX = rng.Next(4, 8); tilesZ = rng.Next(3, 6);
                    heightMeters = Range(3.2f, 4.2f);
                    break;
                case Archetype.MachineRoom:
                    tilesX = rng.Next(3, 7); tilesZ = rng.Next(3, 7);
                    heightMeters = Range(5f, 7f);
                    break;
                case Archetype.Rotunda:
                    tilesX = tilesZ = rng.Next(4, 8);
                    heightMeters = Range(5f, 8f);
                    sides = rng.Next(10, 25);
                    squareness = Range(0f, 0.25f);
                    break;
                default: // Corridor
                    tilesX = rng.Next(6, 12); tilesZ = 2;
                    heightMeters = Range(3f, 4f);
                    break;
            }

            var newHoles = new List<WallHole>();
            var used = new List<int>();

            // Puertas. SIEMPRE al menos una a ras de suelo: una sala sin salida no sirve para
            // nada, y es la unica garantia que este generador no puede permitirse fallar.
            int doors = kind == Archetype.Corridor ? 2 : (Chance(0.45f) ? 2 : 1);
            for (int i = 0; i < doors; i++)
            {
                int side = kind == Archetype.Corridor
                    ? (i == 0 ? 1 : 3)          // los dos extremos del pasillo
                    : rng.Next(sides);
                if (used.Contains(side)) continue;
                used.Add(side);
                newHoles.Add(new WallHole
                {
                    side = side,
                    along = Range(0.3f, 0.7f),
                    baseY = 0f,
                    width = Range(1.3f, 2.2f),
                    height = Range(2.1f, 2.5f),
                });
            }

            // Aberturas altas. La MachineRoom es la que las lleva enrejadas: que un tipo de sala
            // tenga su firma es lo que las hace distinguibles de un vistazo.
            int highOpenings = kind == Archetype.MachineRoom ? rng.Next(2, 5)
                             : kind == Archetype.Partitioned ? rng.Next(1, 4)
                             : kind == Archetype.Rotunda ? rng.Next(2, 6)
                             : rng.Next(0, 2);
            for (int i = 0; i < highOpenings; i++)
            {
                float top = Mathf.Max(1.2f, heightMeters - 1.4f);
                newHoles.Add(new WallHole
                {
                    side = rng.Next(sides),
                    along = Range(0.2f, 0.8f),
                    baseY = Range(1.1f, top),
                    width = Range(1.0f, 2.4f),
                    height = Range(0.6f, 1.2f),
                    grateBars = kind == Archetype.MachineRoom
                        ? rng.Next(3, 7)
                        : (Chance(0.25f) ? rng.Next(3, 6) : 0),
                });
            }
            holes = newHoles.ToArray();

            // Columnas en RETICULA, que es como se sostiene un forjado de verdad: repartirlas al
            // azar daba un bosque sin sentido estructural.
            var newPillars = new List<Pillar>();
            if (kind == Archetype.PillarHall || (kind == Archetype.MachineRoom && Chance(0.5f)))
            {
                int cols = Mathf.Max(2, tilesX / 2), rows = Mathf.Max(2, tilesZ / 2);
                float size = Range(0.5f, 1.0f);
                int psides = Chance(0.25f) ? 16 : 4;
                for (int cx = 0; cx < cols; cx++)
                    for (int cz = 0; cz < rows; cz++)
                        newPillars.Add(new Pillar
                        {
                            position = new Vector2(
                                (cx / (float)(cols - 1) - 0.5f) * WidthMeters * 0.62f,
                                (cz / (float)(rows - 1) - 0.5f) * DepthMeters * 0.55f),
                            size = size,
                            sides = psides,
                        });
            }
            else if (kind == Archetype.Rotunda)
            {
                // Una sola columna gorda en el centro: es lo que hace que una rotonda se lea como
                // rotonda y no como una sala redonda vacia.
                newPillars.Add(new Pillar
                {
                    position = Vector2.zero,
                    size = Range(1.2f, 2.2f),
                    sides = Chance(0.5f) ? 16 : 8,
                });
            }
            pillars = newPillars.ToArray();

            // Bloques: tabiques y poyetes. Alineados a los ejes casi siempre -- un tabique
            // torcido llama la atencion cuando es raro, y deja de hacerlo si lo estan todos.
            var newBlocks = new List<Block>();
            int blockCount = kind == Archetype.Partitioned ? rng.Next(2, 6)
                           : kind == Archetype.MachineRoom ? rng.Next(1, 4)
                           : kind == Archetype.Corridor ? rng.Next(0, 2)
                           : rng.Next(0, 3);
            for (int i = 0; i < blockCount; i++)
            {
                bool partition = kind == Archetype.Partitioned || Chance(0.4f);
                newBlocks.Add(new Block
                {
                    position = new Vector2(
                        Range(-WidthMeters * 0.32f, WidthMeters * 0.32f),
                        Range(-DepthMeters * 0.32f, DepthMeters * 0.32f)),
                    sizeX = partition ? Range(2f, Mathf.Max(2.5f, WidthMeters * 0.35f)) : Range(0.8f, 2f),
                    sizeZ = partition ? Range(0.25f, 0.5f) : Range(0.6f, 1.6f),
                    baseY = 0f,
                    height = partition ? Range(1.6f, Mathf.Min(2.6f, Mathf.Max(1.7f, heightMeters - 0.4f)))
                                       : Range(0.45f, 1.1f),
                    yawDegrees = rng.Next(2) * 90f + (Chance(0.2f) ? Range(-20f, 20f) : 0f),
                });
            }
            blocks = newBlocks.ToArray();

            // Escalera: firma de la MachineRoom, y rareza en el resto. Si apareciera en cada sala
            // dejaria de leerse como hallazgo.
            var newStairs = new List<Stairs>();
            bool wantStairs = kind == Archetype.MachineRoom ? Chance(0.75f) : Chance(0.15f);
            if (wantStairs && tilesZ >= 3)
            {
                // Contra una pared y subiendo hacia el centro: una escalera suelta en mitad de la
                // sala se lee como un error, no como arquitectura.
                newStairs.Add(new Stairs
                {
                    position = new Vector2(Range(-WidthMeters * 0.2f, WidthMeters * 0.2f),
                                           -DepthMeters * 0.5f + wallThickness + 0.1f),
                    yawDegrees = 0f,
                    width = Range(1.3f, 2.2f),
                    steps = rng.Next(6, 13),
                    rise = Range(0.16f, 0.20f),
                    run = Range(0.26f, 0.32f),
                });
            }
            stairs = newStairs.ToArray();
        }

        public Vector2[] InnerContour()
        {
            int n = Mathf.Clamp(sides, MinSides, MaxSides);
            float a = WidthMeters * 0.5f;
            float b = DepthMeters * 0.5f;
            float sq = Mathf.Clamp01(squareness);

            var pts = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float th = Mathf.PI / n + i * 2f * Mathf.PI / n;
                float cos = Mathf.Cos(th), sin = Mathf.Sin(th);

                // Cuadrado unidad: el rayo se estira hasta que la coordenada dominante vale 1.
                // El epsilon solo cubre el caso degenerado de un rayo nulo, que no puede darse.
                float m = Mathf.Max(Mathf.Abs(cos), Mathf.Abs(sin));
                if (m < 1e-6f) m = 1e-6f;

                float ux = Mathf.Lerp(cos, cos / m, sq);
                float uy = Mathf.Lerp(sin, sin / m, sq);

                pts[i] = new Vector2(ux * a, uy * b);
            }
            return pts;
        }

        /// <summary>
        /// Normal EXTERIOR de la arista p0→p1, deducida del sentido de giro del contorno.
        ///
        /// Todo contorno de este generador es ANTIHORARIO, así que el interior queda siempre a la
        /// izquierda de cada arista y la normal exterior es su perpendicular derecha. Punto.
        ///
        /// Antes esto se comprobaba contra el CENTRO del polígono, y funcionaba solo por ser
        /// convexo: en una planta en L el centro puede caer fuera, o en el lado equivocado de un
        /// entrante, y esa pared se giraría del revés. Deducirlo del giro no tiene ese problema
        /// y vale para cualquier forma.
        /// </summary>
        public static Vector2 OutwardNormal(Vector2 p0, Vector2 p1) =>
            new Vector2(p1.y - p0.y, -(p1.x - p0.x)).normalized;

        /// <summary>
        /// Contorno EXTERIOR: el interior desplazado <see cref="wallThickness"/> hacia afuera.
        ///
        /// Se desplaza en INGLETE (por la bisectriz, con la longitud corregida por
        /// 1/cos del semiángulo) y no simplemente alejando cada vértice del centro. La
        /// diferencia se ve justo donde más: en una planta rectangular, alejar del centro deja
        /// las esquinas achaflanadas y la pared más fina justo ahí, mientras que el inglete da
        /// la esquina exacta (a+t, b+t). El inglete también vale en un vértice entrante (una
        /// esquina de planta en L), así que esto no exige que la planta sea convexa.
        /// </summary>
        public static Vector2[] OffsetOutward(Vector2[] inner, float thickness)
        {
            int n = inner.Length;
            var outer = new Vector2[n];

            var edgeN = new Vector2[n];
            for (int i = 0; i < n; i++)
                edgeN[i] = OutwardNormal(inner[i], inner[(i + 1) % n]);

            for (int i = 0; i < n; i++)
            {
                Vector2 nPrev = edgeN[(i - 1 + n) % n];
                Vector2 nNext = edgeN[i];
                Vector2 miter = (nPrev + nNext).normalized;
                float scale = Vector2.Dot(miter, nNext);
                // Ángulo casi invertido (planta degenerada): sin la guarda, 1/scale explota.
                if (scale < 1e-3f) scale = 1e-3f;
                outer[i] = inner[i] + miter * (thickness / scale);
            }
            return outer;
        }
    }
}
