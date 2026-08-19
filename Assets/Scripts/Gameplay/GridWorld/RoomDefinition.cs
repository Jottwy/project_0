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
        /// Cuánto se inclina el techo, en grados. 0 = plano, y entonces todo se comporta como
        /// antes. Es lo que separa una nave de un desván o un semisótano.
        /// </summary>
        [Range(0f, 40f)] public float ceilingTilt;

        /// <summary>Hacia dónde BAJA el techo.</summary>
        public float ceilingTiltYaw;

        /// <summary>Nunca menos que esto, por muy inclinado que esté: un techo que corta el suelo
        /// deja la sala partida por dentro y sin forma de saber por qué.</summary>
        public const float MinCeilingHeight = 1.2f;

        /// <summary>
        /// Altura del techo sobre el punto <paramref name="p"/> (XZ, relativo al centro de la
        /// sala). Con <see cref="ceilingTilt"/> a 0 devuelve <see cref="heightMeters"/> y punto.
        ///
        /// La inclinación PIVOTA sobre el centro, así que subir el mando no sube ni baja la sala:
        /// un lado gana lo que el otro pierde y <see cref="heightMeters"/> sigue siendo la altura
        /// media. Inclinar desde una esquina obligaría a recolocar la altura cada vez.
        /// </summary>
        public float CeilingYAt(Vector2 p)
        {
            if (ceilingTilt <= 0.001f) return heightMeters;
            float r = ceilingTiltYaw * Mathf.Deg2Rad;
            var down = new Vector2(Mathf.Sin(r), Mathf.Cos(r));
            return heightMeters - EffectiveTilt() * Vector2.Dot(p, down);
        }

        /// <summary>
        /// La pendiente REAL, limitada para que el punto mas bajo del techo no baje de
        /// <see cref="MinCeilingHeight"/>.
        ///
        /// Se limita la PENDIENTE y no la altura punto a punto, y la diferencia importa: recortar
        /// cada punto por separado aplana un lado y el techo deja de ser un plano, asi que el
        /// estirado de textura de la pendiente ya no corresponde y la trama se rompe justo en la
        /// zona aplanada. Limitando la pendiente el techo sigue siendo un plano entero.
        /// </summary>
        private float EffectiveTilt()
        {
            float want = Mathf.Tan(ceilingTilt * Mathf.Deg2Rad);
            float r = ceilingTiltYaw * Mathf.Deg2Rad;
            var down = new Vector2(Mathf.Sin(r), Mathf.Cos(r));
            // Lo mas lejos que se puede llegar del centro en la direccion de la caida, contando
            // el grosor: la cara exterior sobresale del footprint.
            float reach = Mathf.Abs(down.x) * (WidthMeters * 0.5f + wallThickness)
                        + Mathf.Abs(down.y) * (DepthMeters * 0.5f + wallThickness);
            if (reach < 0.01f) return want;
            float allowed = Mathf.Max(0f, heightMeters - MinCeilingHeight) / reach;
            return Mathf.Min(want, allowed);
        }

        /// <summary>El punto más bajo del techo sobre el contorno. Los boquetes se recortan contra
        /// esto: una ventana que asome por encima del techo en la parte baja seria un agujero al
        /// vacio.</summary>
        public float MinCeilingOver(Vector2[] contour)
        {
            float lo = float.MaxValue;
            for (int i = 0; i < contour.Length; i++) lo = Mathf.Min(lo, CeilingYAt(contour[i]));
            return lo == float.MaxValue ? heightMeters : lo;
        }

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
        /// Cómo se decide la planta.
        ///
        /// <c>Polygon</c> es lo de siempre: un polígono de N lados mezclado con el rectángulo del
        /// footprint. Siempre CONVEXO, y es lo que da las plantas redondas.
        ///
        /// <c>Blocks</c> parte el footprint en celdas de tile y deja quitar bloques enteros
        /// (<see cref="Notch"/>). Ahí salen las plantas en L, T, U o cruz — no convexas.
        ///
        /// Son dos modos y no un solo camino unificado a propósito: restar un rectángulo a un
        /// polígono redondo pide booleanas de polígonos, que son frágiles justo en los casos
        /// raros (el corte tangente, la esquina exacta). Sobre una rejilla de celdas la misma
        /// operación es quitar celdas de una lista, y el contorno sale de recorrer el borde: no
        /// hay caso raro que resolver.
        /// </summary>
        public enum PlanMode { Polygon, Blocks }

        public PlanMode planMode = PlanMode.Polygon;

        /// <summary>
        /// Un mordisco al footprint, en TILES. Solo en <see cref="PlanMode.Blocks"/>.
        /// Quitar una esquina da una L; dos opuestas, una U o una T.
        /// </summary>
        [Serializable]
        public sealed class Notch
        {
            public int tileX;
            public int tileZ;
            [Min(1)] public int tilesX = 1;
            [Min(1)] public int tilesZ = 1;
        }

        public Notch[] notches = Array.Empty<Notch>();

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

        /// <summary>
        /// Cuánto se tuercen las paredes. 0 = geometría perfecta. Subirlo desplaza cada vértice
        /// de la planta y las paredes dejan de ser exactas: esquinas que no son rectas del todo,
        /// muros ligeramente fuera de escuadra. Es lo que separa "generado" de "construido".
        /// </summary>
        [Range(0f, 1f)] public float irregularity;

        /// <summary>Semilla de la irregularidad. Misma semilla, misma planta torcida.</summary>
        public int irregularitySeed = 1;

        public Vector2[] InnerContour() => ApplyIrregularity(
            planMode == PlanMode.Blocks ? BlockContour() : PolygonContour());

        /// <summary>
        /// Desplaza cada vértice a lo largo de su propia normal (la bisectriz de sus dos aristas).
        /// A lo largo de la normal y no en cualquier dirección: mover un vértice de lado lo
        /// arrastra hacia sus vecinos y es la forma rápida de que dos paredes se crucen.
        ///
        /// El tope es una fracción de la arista MÁS CORTA que toca ese vértice. Un desplazamiento
        /// fijo en metros se comería una pared de medio metro y dejaría la planta hecha un nudo,
        /// mientras que en una nave de 40 m no se notaría; atado a la arista, el efecto es el
        /// mismo en una sala pequeña que en una grande y nunca puede plegar la geometría.
        ///
        /// DETERMINISTA por (índice de vértice, semilla), sin estado compartido: esta función la
        /// llaman la malla, los colliders y los tests por separado, y todos tienen que ver
        /// exactamente la misma planta.
        /// </summary>
        private Vector2[] ApplyIrregularity(Vector2[] pts)
        {
            if (irregularity <= 0.001f || pts == null || pts.Length < 3) return pts;

            int n = pts.Length;
            var outPts = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 prev = pts[(i - 1 + n) % n], cur = pts[i], next = pts[(i + 1) % n];

                Vector2 nrm = (OutwardNormal(prev, cur) + OutwardNormal(cur, next)).normalized;
                if (nrm.sqrMagnitude < 0.5f) { outPts[i] = cur; continue; } // aristas opuestas

                float shortest = Mathf.Min(Vector2.Distance(prev, cur), Vector2.Distance(cur, next));
                float limit = Mathf.Min(1.5f, shortest * 0.25f) * irregularity;

                outPts[i] = cur + nrm * ((Hash01(i, irregularitySeed) * 2f - 1f) * limit);
            }
            return outPts;
        }

        /// <summary>Hash entero → [0,1). Sin `System.Random`: hace falta que el valor dependa SOLO
        /// del índice y la semilla, no del orden en que se pidan.</summary>
        private static float Hash01(int i, int seed)
        {
            unchecked
            {
                uint h = (uint)(i * 374761393) + (uint)(seed * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                return (h ^ (h >> 16)) / (float)uint.MaxValue;
            }
        }

        /// <summary>
        /// Contorno del modo BLOQUES: el footprint en celdas de tile menos las muescas, recorrido
        /// por su borde.
        ///
        /// Se recogen las aristas de celda donde cambia dentro/fuera y se encadenan por sus
        /// extremos hasta cerrar el bucle. Sin booleanas y sin casos raros: una arista de borde lo
        /// es o no lo es. Los tramos rectos consecutivos se funden en uno solo — si no, una pared
        /// de 6 tiles serían seis "lados" y cada boquete solo podría vivir dentro de uno.
        ///
        /// Degrada al polígono si las muescas dejan la sala vacía o partida en dos: media sala
        /// suelta sería una sala que no se puede recorrer, y es mejor ignorar la muesca que
        /// entregar geometría imposible.
        /// </summary>
        private Vector2[] BlockContour()
        {
            int nx = Mathf.Max(1, tilesX), nz = Mathf.Max(1, tilesZ);
            var solid = new bool[nx, nz];
            for (int x = 0; x < nx; x++)
                for (int z = 0; z < nz; z++) solid[x, z] = true;

            if (notches != null)
                foreach (var t in notches)
                {
                    if (t == null) continue;
                    for (int x = t.tileX; x < t.tileX + Mathf.Max(1, t.tilesX); x++)
                        for (int z = t.tileZ; z < t.tileZ + Mathf.Max(1, t.tilesZ); z++)
                            if (x >= 0 && x < nx && z >= 0 && z < nz) solid[x, z] = false;
                }

            if (!SingleConnectedRegion(solid, nx, nz)) return PolygonContour();

            const float T = GridVisualConstants.TileSize;
            float ox = -nx * T * 0.5f, oz = -nz * T * 0.5f;
            Vector2 P(int gx, int gz) => new Vector2(ox + gx * T, oz + gz * T);

            // Aristas de borde, orientadas de modo que el interior quede a la IZQUIERDA: así el
            // bucle sale antihorario, que es lo que asume el resto del generador.
            var next = new Dictionary<Vector2, Vector2>();
            void Edge(Vector2 a, Vector2 b) { if (!next.ContainsKey(a)) next[a] = b; }

            for (int x = 0; x < nx; x++)
                for (int z = 0; z < nz; z++)
                {
                    if (!solid[x, z]) continue;
                    bool left  = x > 0 && solid[x - 1, z];
                    bool right = x < nx - 1 && solid[x + 1, z];
                    bool down  = z > 0 && solid[x, z - 1];
                    bool up    = z < nz - 1 && solid[x, z + 1];

                    if (!down)  Edge(P(x, z),         P(x + 1, z));
                    if (!right) Edge(P(x + 1, z),     P(x + 1, z + 1));
                    if (!up)    Edge(P(x + 1, z + 1), P(x, z + 1));
                    if (!left)  Edge(P(x, z + 1),     P(x, z));
                }

            if (next.Count < 4) return PolygonContour();

            var loop = new List<Vector2>();
            Vector2 start = default;
            foreach (var kv in next) { start = kv.Key; break; }
            Vector2 cur = start;
            for (int guard = 0; guard <= next.Count; guard++)
            {
                loop.Add(cur);
                if (!next.TryGetValue(cur, out Vector2 nxt)) return PolygonContour();
                cur = nxt;
                if (cur == start) break;
            }
            if (loop.Count < 4) return PolygonContour();

            // Funde tramos rectos: sin esto cada tile sería un "lado" y un boquete no podría
            // cruzar de uno al siguiente.
            var merged = new List<Vector2>();
            for (int i = 0; i < loop.Count; i++)
            {
                Vector2 prev = loop[(i - 1 + loop.Count) % loop.Count];
                Vector2 here = loop[i];
                Vector2 after = loop[(i + 1) % loop.Count];
                Vector2 a = (here - prev).normalized, b = (after - here).normalized;
                if (Mathf.Abs(a.x * b.y - a.y * b.x) > 1e-4f) merged.Add(here); // esquina real
            }
            return merged.Count >= 3 ? merged.ToArray() : PolygonContour();
        }

        /// <summary>¿Las celdas macizas forman UNA sola pieza pegada? Una muesca que parte la sala
        /// en dos dejaría un trozo inalcanzable.</summary>
        private static bool SingleConnectedRegion(bool[,] solid, int nx, int nz)
        {
            int total = 0, sx = -1, sz = -1;
            for (int x = 0; x < nx; x++)
                for (int z = 0; z < nz; z++)
                    if (solid[x, z]) { total++; if (sx < 0) { sx = x; sz = z; } }
            if (total < 1) return false;

            var seen = new bool[nx, nz];
            var stack = new Stack<(int, int)>();
            stack.Push((sx, sz));
            seen[sx, sz] = true;
            int found = 0;
            while (stack.Count > 0)
            {
                var (x, z) = stack.Pop();
                found++;
                void Try(int a, int b)
                {
                    if (a < 0 || a >= nx || b < 0 || b >= nz) return;
                    if (!solid[a, b] || seen[a, b]) return;
                    seen[a, b] = true;
                    stack.Push((a, b));
                }
                Try(x - 1, z); Try(x + 1, z); Try(x, z - 1); Try(x, z + 1);
            }
            return found == total;
        }

        private Vector2[] PolygonContour()
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
