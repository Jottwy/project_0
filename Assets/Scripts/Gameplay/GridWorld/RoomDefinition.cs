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
        /// Rodapié al pie de cada pared y molduras alrededor de cada boquete. Apagado (lo de
        /// siempre) no cambia nada — ni una sala ya horneada, ni el resto de esta clase.
        /// Encendido es geometría de más, no un parámetro que otra cosa lea: es lo que separa
        /// "caja con agujeros" de "edificio".
        /// </summary>
        public bool trimEnabled;

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
        /// <summary>
        /// <c>Manual</c> es la planta dibujada a mano, punto por punto, con tiradores en la
        /// vista de escena — para cuando ni el polígono ni las muescas dan la forma que hace
        /// falta.
        /// </summary>
        public enum PlanMode { Polygon, Blocks, Manual }

        public PlanMode planMode = PlanMode.Polygon;

        /// <summary>
        /// La planta dibujada a mano. Solo se usa en <see cref="PlanMode.Manual"/>, y con menos
        /// de 3 puntos degrada al <see cref="PolygonContour"/> de siempre — un contorno a medio
        /// dibujar no es una sala, y sacar geometría de él sería adivinar.
        ///
        /// NO pasa por <see cref="ApplyIrregularity"/>: quien está tocando cada vértice a mano
        /// ya está pidiendo el control exacto que la irregularidad existe para NO tener que
        /// pedir — perturbarle los puntos que acaba de colocar sería quitarle con una mano lo
        /// que la otra le da.
        /// </summary>
        public Vector2[] manualContour = Array.Empty<Vector2>();

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

            /// <summary>Altura del borde INFERIOR sobre el suelo, en metros. 0 = puerta.
            /// Se mide desde el suelo de SU piso (<see cref="level"/>), no desde el de la sala.</summary>
            public float baseY;

            /// <summary>
            /// Piso al que pertenece la abertura: 0 = planta baja, k = sobre la k-ésima losa
            /// contando desde abajo EN ALTURA. Solo mueve el ORIGEN de <see cref="baseY"/>; el
            /// recorte por arriba lo sigue poniendo el techo de la sala, porque la pared es una
            /// superficie continua y no se parte por pisos — una ventana del piso 1 se sigue
            /// abriendo en la misma pared que la puerta de abajo.
            ///
            /// Existe para que subir una entreplanta se lleve sus ventanas con ella. Antes había
            /// que rehacer a mano el `baseY` de cada una, y olvidarse de una dejaba una ventana a
            /// la altura de las rodillas del piso de arriba.
            /// </summary>
            [Min(0)] public int level;

            /// <summary>En METROS, no en fracción: se piensa en "una puerta de 1,2 m".</summary>
            public float width = 1.6f;
            public float height = 2.2f;

            /// <summary>Barrotes que cruzan el hueco. 0 = hueco limpio. Convierte una ventana en
            /// rejilla sin ser un tipo de feature aparte: la abertura ya está, solo se llena.</summary>
            [Range(0, 20)] public int grateBars;

            /// <summary>
            /// Deja que la abertura DOBLE LA ESQUINA. Con esto puesto, <see cref="width"/> deja de
            /// medirse sobre la pared y pasa a medirse sobre el CONTORNO: el hueco avanza por el
            /// perímetro y se lleva por delante los vértices que le queden dentro, así que una
            /// puerta puede empezar en una pared y salir por la de al lado.
            ///
            /// Apagado (lo de siempre) la abertura se recorta contra su propia pared. No es
            /// timidez: un hueco que se come la esquina deja la pared vecina entera justo ahí, las
            /// dos aristas dejan de casar y salen 22 aristas abiertas. Encenderlo no levanta esa
            /// restricción — reparte el hueco en un trozo por pared y quita la jamba solo en las
            /// esquinas por las que de verdad sigue.
            ///
            /// El otro sitio donde hace falta, y no se ve venir: en planta redonda cada faceta
            /// mide poco más de un metro, así que sin esto NO se puede pedir una puerta de 4 m en
            /// una rotonda — el recorte contra la faceta se la come.
            /// </summary>
            public bool spanCorners;
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

            /// <summary>Piso al que pertenece — ver <see cref="StoreyBaseY"/>. La columna va del
            /// suelo de SU piso al techo de SU piso (la losa siguiente, o el techo de la sala si
            /// no hay más): una columna es del piso que sostiene, no un mástil que cruza losas.</summary>
            [Min(0)] public int level;
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

            /// <summary>Sobre el suelo de SU piso (<see cref="level"/>), no siempre el de la
            /// sala: con level 0 son lo mismo y todo sigue como antes.</summary>
            public float baseY;
            public float height = 2f;
            public float yawDegrees;

            /// <summary>Piso al que pertenece — ver <see cref="StoreyBaseY"/>.</summary>
            [Min(0)] public int level;

            /// <summary>
            /// Boquetes que atraviesan el bloque de lado a lado — lo que hace falta para que un
            /// tabique tenga puerta. Vacío = el bloque sigue siendo el prisma macizo de siempre.
            /// </summary>
            public BlockHole[] holes = Array.Empty<BlockHole>();

            /// <summary>Jamba mínima que un boquete de bloque reserva en cada extremo y bajo el
            /// dintel: 5 cm, lo mismo que un boquete de pared. Sin esto un hueco pedido al ras de
            /// una esquina se comería el machón entero.</summary>
            private const float MinJamb = 0.05f;

            /// <summary>
            /// Los boquetes de este bloque, resueltos y repartidos en dos listas por eje: a
            /// través de Z (side 0/2) y a través de X (side 1/3) — un bloque solo tiene dos
            /// caras enfrentadas por eje, así que un boquete en cualquiera de las dos perfora
            /// las DOS. Las dos listas quedan en la "u" de la cara PAR de su eje (side 0 o 1);
            /// espejar para la cara impar es cosa de quien llame, no de esta resolución.
            ///
            /// Vive aquí y no en la malla ni en los colliders porque los dos necesitan EL MISMO
            /// hueco en el mismo sitio — el motivo de siempre: "veo una puerta y me choco".
            /// </summary>
            /// <param name="baseOffset">El suelo del PISO del bloque (<see cref="StoreyBaseY"/>),
            /// que los dos llamadores ya conocen. Va por parámetro y no se calcula aquí porque
            /// necesita el contorno y el techo de la sala, que un bloque no tiene.</param>
            public void ResolveHoles(List<HoleRect> throughZ, List<HoleRect> throughX,
                float baseOffset = 0f)
            {
                throughZ.Clear();
                throughX.Clear();
                if (holes == null) return;

                float y0 = baseOffset + baseY, y1 = y0 + height;
                foreach (var h in holes)
                {
                    if (h == null || h.width <= 0.001f || h.height <= 0.001f) continue;
                    int side = ((h.side % 4) + 4) % 4;
                    bool throughThisZ = side == 0 || side == 2;
                    float faceLen = throughThisZ ? sizeX : sizeZ;
                    if (faceLen < 1e-4f) continue;

                    float along = (side == 0 || side == 1) ? h.along : 1f - h.along;
                    float margin = Mathf.Clamp(MinJamb / faceLen, 0.001f, 0.45f);
                    float half = h.width * 0.5f / faceLen;
                    float u0 = Mathf.Clamp(along - half, margin, 1f - margin);
                    float u1 = Mathf.Clamp(along + half, margin, 1f - margin);
                    float hy0 = Mathf.Clamp(y0 + h.baseY, y0, y1 - MinJamb);
                    float hy1 = Mathf.Clamp(y0 + h.baseY + h.height, y0, y1 - MinJamb);
                    if (u1 - u0 < 1e-4f || hy1 - hy0 < 1e-4f) continue;

                    (throughThisZ ? throughZ : throughX).Add(
                        new HoleRect { u0 = u0, u1 = u1, y0 = hy0, y1 = hy1 });
                }
                MergeOverlapping(throughZ);
                MergeOverlapping(throughX);
            }
        }

        /// <summary>
        /// Un boquete en un BLOQUE: mismo concepto que <see cref="WallHole"/> pero para un
        /// tabique suelto, no para el perímetro de la sala. Atraviesa siempre hasta la cara
        /// OPUESTA a <see cref="side"/> — un bloque es solo una caja, así que no hace falta el
        /// mapeo a una cara exterior más ancha que un <see cref="WallHole"/> sí necesita: las
        /// dos caras de un bloque son EXACTAMENTE del mismo tamaño, sin inglete que abocine el
        /// hueco.
        /// </summary>
        [Serializable]
        public sealed class BlockHole
        {
            /// <summary>Cara de la que sale, 0..3 en el mismo orden que las esquinas del bloque
            /// (<see cref="RoomMeshBuilder.BoxCorners"/>).</summary>
            public int side;

            /// <summary>Centro a lo largo de esa cara: 0 = una esquina, 1 = la otra.</summary>
            [Range(0f, 1f)] public float along = 0.5f;

            /// <summary>Sobre el propio suelo del bloque (<c>baseY</c> del bloque), no el de la
            /// sala. 0 = puerta.</summary>
            public float baseY;
            public float width = 1.6f;
            public float height = 2.2f;
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

            /// <summary>Piso del que ARRANCA — ver <see cref="StoreyBaseY"/>. El primer peldaño
            /// apoya en el suelo de ese piso, y <see cref="TopHeight"/> se mide desde ahí.</summary>
            [Min(0)] public int level;

            /// <summary>Hacia dónde sube, como vector unitario en XZ.</summary>
            public Vector2 Forward()
            {
                float r = yawDegrees * Mathf.Deg2Rad;
                return new Vector2(Mathf.Sin(r), Mathf.Cos(r));
            }

            /// <summary>Cuánto mide el tramo en planta, de la base al último peldaño.</summary>
            public float FootprintLength() => run * steps;

            /// <summary>Centro del rectángulo que ocupa el tramo entero en planta — lo que hace
            /// falta para abrir el hueco correspondiente en la losa de un nivel.</summary>
            public Vector2 FootprintCentre() => position + Forward() * (FootprintLength() * 0.5f);

            /// <summary>Altura del último peldaño: hasta dónde llega subiendo.</summary>
            public float TopHeight() => rise * steps;

            /// <summary>Descartable: sin peldaños, sin ancho o sin medidas no hay tramo que
            /// construir. La misma condición la usan la malla, los colliders y las losas de
            /// nivel — separarla evita que una acabe considerando un tramo que la otra ignora.</summary>
            public bool IsValid() => steps >= 1 && width > 0.001f && rise > 0.001f && run > 0.001f;
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

            /// <summary>Piso al que pertenece la retícula entera — ver <see cref="StoreyBaseY"/>.
            /// Mismo criterio que <see cref="Pillar.level"/>.</summary>
            [Min(0)] public int level;

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

            /// <summary>Lados del pozo. 0 (o menos de 3) = el rectángulo exacto de siempre
            /// (<c>sizeX</c> × <c>sizeZ</c>). 3+ = un N-ágono, mismo blend círculo/rectángulo que
            /// <see cref="squareness"/> y el mismo generador angular que <see cref="Pillar.sides"/>
            /// usa para una columna. Un pozo guardado ANTES de este campo deserializa esto a 0 —
            /// el default de C# — así que sigue siendo el rectángulo de siempre sin tocarlo.</summary>
            [Range(0, 32)] public int sides;

            /// <summary>0 = polígono inscrito puro (redondo), 1 = el rectángulo
            /// <c>sizeX</c> × <c>sizeZ</c> exacto. Sin efecto si <see cref="sides"/> &lt; 3.</summary>
            [Range(0f, 1f)] public float squareness = 1f;

            /// <summary>Cuánto cuelga por debajo de la losa de <see cref="level"/>, en metros. Solo
            /// se lee con <see cref="PitMode.Well"/>/<see cref="PitMode.Bottomless"/> — sin efecto
            /// en <see cref="PitMode.Through"/>.</summary>
            public float depth = 2.5f;

            public float yawDegrees;

            /// <summary>LEGADO — solo de migración. Un pozo de planta baja (<see cref="level"/> 0)
            /// guardado ANTES de que <see cref="mode"/> existiera trae aquí si colgaba sin fondo;
            /// <see cref="EffectiveMode"/> lo lee UNA VEZ para derivar el modo real. No leer esto
            /// directamente en ningún otro sitio.</summary>
            public bool bottomless;

            /// <summary>
            /// En qué SUELO se abre: 0 = el de la sala, k = la losa de la entreplanta k-ésima
            /// contando desde abajo en altura. Solo decide el ANCLA vertical — qué losa perfora y,
            /// si <see cref="EffectiveMode"/> cuelga, desde qué losa cuelga. Ortogonal al modo: antes
            /// un piso alto forzaba SIEMPRE el agujero pasante; ahora el modo se elige aparte.
            /// </summary>
            [Min(0)] public int level;

            /// <summary>El modo elegido. NO LEER DIRECTAMENTE fuera de <see cref="EffectiveMode"/> —
            /// un pozo guardado antes de que este campo existiera tiene esto a
            /// <see cref="PitMode.Legacy"/>.</summary>
            public PitMode mode;

            /// <summary>
            /// El modo real de este pozo. Un <see cref="PitMode.Legacy"/> (el valor por defecto de
            /// C#, así que también el de cualquier pozo guardado antes de este campo) se resuelve a
            /// partir de <see cref="level"/> y <see cref="bottomless"/> — EXACTAMENTE el
            /// comportamiento de antes de que el modo fuera elegible piso a piso: piso alto era
            /// siempre pasante, planta baja colgaba con o sin fondo según <see cref="bottomless"/>.
            /// Barato y determinista — no hace falta persistir el resultado, se deriva cada vez.
            /// </summary>
            public PitMode EffectiveMode() => mode != PitMode.Legacy ? mode
                : level > 0 ? PitMode.Through
                : bottomless ? PitMode.Bottomless
                : PitMode.Well;

            /// <summary>Tamaño real y, si cuelga con fondo, con algo de profundidad — un Well de
            /// 0 m no es un pozo, es ruido. Through/Bottomless no necesitan <see cref="depth"/>
            /// positiva (Bottomless la sustituye por el grosor de la losa como mínimo).</summary>
            public bool IsValid() => sizeX > 0.01f && sizeZ > 0.01f
                && (EffectiveMode() != PitMode.Well || depth > 0.01f);
        }

        /// <summary>
        /// Cómo se abre un pozo respecto a la losa de SU piso (<see cref="FloorHole.level"/>).
        ///
        /// <c>Legacy</c> es un SENTINEL, no un modo real: solo lo lleva un pozo guardado ANTES de
        /// que este campo existiera (deserializa a 0, el primer valor del enum, cuando el nodo no
        /// estaba en el JSON/YAML). Nunca lo elige la herramienta a propósito — el pit nuevo nace
        /// en <see cref="Well"/>. Ver <see cref="FloorHole.EffectiveMode"/>.
        ///
        /// <c>Through</c>: agujero recto de lado a lado de la losa, sin fondo propio ni paredes
        /// propias — el grosor de la losa YA hace de pared. Es lo que tenía SIEMPRE un pozo con
        /// <c>level&gt;0</c>, y ahora también se puede pedir en planta baja.
        ///
        /// <c>Well</c>/<c>Bottomless</c>: cuelga <see cref="FloorHole.depth"/> metros por DEBAJO de
        /// la losa de su piso, con paredes propias de grosor <c>wallThickness</c>. Es lo que tenía
        /// SIEMPRE un pozo de planta baja (con/sin fondo), y ahora también se puede pedir en
        /// cualquier entreplanta — cuelga bajo ESA losa, no bajo el suelo de la sala.
        /// </summary>
        public enum PitMode { Legacy, Through, Well, Bottomless }

        /// <summary>
        /// Una entreplanta: una losa horizontal a media altura, del ancho de toda la sala, con
        /// hueco automático donde una escalera llegue lo bastante alto para atravesarla. Es lo
        /// que convierte "una sala alta" en "una sala de dos pisos" sin que haga falta describir
        /// su propio contorno — usa el de la sala, igual que el suelo y el techo.
        /// </summary>
        [Serializable]
        public sealed class Level
        {
            /// <summary>Altura del canto superior de la losa sobre el suelo de la sala, en
            /// metros. Se recorta para dejar paso por debajo y por encima — ver
            /// <see cref="ClampLevelHeight"/>.</summary>
            public float height = 3f;
        }

        /// <summary>Qué es un <see cref="Marker"/>: no cambia geometría, solo dónde va algo que
        /// otro sistema resuelve después. <see cref="Light"/> es LEGADO — las luces tienen grupo
        /// propio ahora (<see cref="RoomDefinition.Light"/>/<see cref="lights"/>); el valor se
        /// queda en el enum solo para no correr el riesgo de que un <c>kind</c> guardado como
        /// entero se lea como otra cosa (`JsonUtility` serializa enums por índice), pero la
        /// ventana no lo ofrece — ver <see cref="MigrateLegacyLightMarkers"/>.</summary>
        public enum MarkerKind { Prop, Light, Spawn }

        /// <summary>
        /// Un punto guardado con la sala que NO es geometría: dónde va una luz, un prop o un
        /// punto de aparición. Vive como hijo del prefab, no dentro de la malla única — la malla
        /// es la que se optimiza a un solo draw call; esto es justo lo contrario, sitios que
        /// otro sistema tiene que poder encontrar y recorrer por su cuenta.
        ///
        /// <see cref="tag"/> y no una referencia directa a prefab: <see cref="RoomDefinition"/>
        /// viaja por <c>JsonUtility</c> (la sala guardada se clona así al volver a cargarla en
        /// el editor — ver <c>RoomAuthoringWindow.LoadRoom</c>), y una referencia a
        /// <c>UnityEngine.Object</c> no sobrevive ese viaje de ida y vuelta. Un texto libre que
        /// un catálogo resuelva después es el mismo indirection que ya usa
        /// <c>LayerVisualConfig.PropEntry.placeholderType</c> para el resto de props del mundo.
        /// </summary>
        [Serializable]
        public sealed class Marker
        {
            public MarkerKind kind = MarkerKind.Prop;

            /// <summary>En metros, relativo al centro de la sala — mismo convenio que todo lo
            /// demás.</summary>
            public Vector2 position;

            /// <summary>Altura sobre el suelo de SU piso (<see cref="level"/>), en metros.</summary>
            public float y;

            public float yawDegrees;

            /// <summary>Piso al que pertenece — ver <see cref="StoreyBaseY"/>.</summary>
            [Min(0)] public int level;

            /// <summary>Para Prop: qué prop, resuelto luego por un catálogo. Para Spawn: una
            /// etiqueta libre ("player", "loot_common"...).</summary>
            public string tag = "";

            // LEGADO — solo de migración. Un marcador kind==Light guardado antes de que las
            // luces tuvieran grupo propio trae aquí sus valores; MigrateLegacyLightMarkers los
            // traslada a una entrada de `lights` y ya no se leen desde ningún otro sitio.
            public Color lightColor = Color.white;
            [Min(0f)] public float lightIntensity = 1f;
            [Min(0f)] public float lightRange = 5f;
        }

        /// <summary>Cómo se comporta una <see cref="Light"/> autorada. <c>Steady</c> es la de
        /// siempre. <c>Flicker</c>/<c>Dead</c> reusan <c>BackroomsLighting.LampFlicker</c>, el
        /// mismo comportamiento visual que ya tienen las lámparas proceduales del laberinto —
        /// sin engancharse a su audio de zumbido, que es un sistema por-chunk aparte.</summary>
        public enum LightBehavior { Steady, Flicker, Dead }

        /// <summary>
        /// Una luz autorada, con grupo propio (a diferencia de antes, cuando era un
        /// <see cref="Marker"/> más mezclado con Prop/Spawn). Mismos campos posicionales que
        /// <see cref="Marker"/> porque resuelve el mismo problema — dónde va algo, en qué piso —
        /// pero una luz SÍ tiene comportamiento propio que editar, y mezclarla con props/spawns
        /// era lo que impedía dársela sin liar las otras dos listas.
        /// </summary>
        [Serializable]
        public sealed class Light
        {
            /// <summary>En metros, relativo al centro de la sala.</summary>
            public Vector2 position;

            /// <summary>Altura sobre el suelo de SU piso (<see cref="level"/>), en metros.</summary>
            public float y;

            public float yawDegrees;

            /// <summary>Piso al que pertenece — ver <see cref="StoreyBaseY"/>.</summary>
            [Min(0)] public int level;

            public Color lightColor = Color.white;
            [Min(0f)] public float lightIntensity = 1f;
            [Min(0f)] public float lightRange = 5f;

            public LightBehavior behavior = LightBehavior.Steady;

            /// <summary>Frecuencia del parpadeo en Hz. Solo se lee con
            /// <see cref="LightBehavior.Flicker"/>.</summary>
            [Min(0.05f)] public float flickerHz = 2f;
        }

        public WallHole[] holes = Array.Empty<WallHole>();
        public Pillar[] pillars = Array.Empty<Pillar>();

        /// <summary>LEGADO — solo de migración. La ventana ya no crea ni edita retículas: al
        /// cargar una sala, <see cref="MigrateLegacyPillarGrids"/> expande cualquier entrada de
        /// aquí a pilares sueltos en <see cref="pillars"/> y vacía este array. Se queda declarado
        /// (no se borra el campo) para que una sala horneada ANTES de esa migración (confirmado:
        /// hay una en el pool con 18 pilares repartidos en dos retículas) siga deserializando su
        /// contenido en vez de perderlo en silencio.</summary>
        public PillarGrid[] pillarGrids = Array.Empty<PillarGrid>();

        public Block[] blocks = Array.Empty<Block>();
        public Stairs[] stairs = Array.Empty<Stairs>();
        public FloorHole[] floorHoles = Array.Empty<FloorHole>();
        public Level[] levels = Array.Empty<Level>();
        public Marker[] markers = Array.Empty<Marker>();
        public Light[] lights = Array.Empty<Light>();

        /// <summary>
        /// Estancias dentro de la sala. Es AUTORADO puro: nada de esto viaja por el wire ni sale en
        /// el manifiesto — se declara la partición y se pulsa Build, y salen tabiques
        /// (<see cref="Block"/>) como los que se pondrían a mano, con su vano donde dos estancias
        /// se tocan. La sala sigue siendo UNA sala para el mundo.
        ///
        /// Es la vía que hay hoy para un complejo de varias estancias conectadas: el emplazamiento
        /// prohíbe que dos salas compartan pared (hace falta un tile macizo entre reservas), pero
        /// una sola sala llega a 16 × 16 tiles y sus pisos ya dan la verticalidad.
        /// </summary>
        public SubRoom[] subRooms = Array.Empty<SubRoom>();

        /// <summary>Una estancia: un rectángulo de TILES dentro de la sala, en un piso. En tiles y
        /// no en metros porque dos estancias solo se comunican si comparten borde exactamente, y
        /// eso con metros sueltos no cuadra nunca.</summary>
        [Serializable]
        public sealed class SubRoom
        {
            public string label = "room";
            [Min(0)] public int tileX;
            [Min(0)] public int tileZ;
            [Min(1)] public int tilesX = 2;
            [Min(1)] public int tilesZ = 2;

            /// <summary>Piso al que pertenece — ver <see cref="StoreyBaseY"/>.</summary>
            [Min(0)] public int level;

            /// <summary>Alto del tabique sobre el suelo de su piso. 0 = hasta el techo.</summary>
            [Min(0f)] public float wallHeight;

            /// <summary>Ancho del vano donde esta estancia toca a otra. 0 = tabique macizo, sin
            /// paso — dos estancias pegadas y selladas la una de la otra.</summary>
            [Min(0f)] public float doorWidth = 1.6f;
        }

        /// <summary>
        /// Traslada cualquier <see cref="Marker"/> de <see cref="MarkerKind.Light"/> a una entrada
        /// de <see cref="lights"/> y lo quita de <see cref="markers"/>. Idempotente. Mismo patrón y
        /// mismo punto de llamada que <see cref="MigrateLegacyPillarGrids"/> —
        /// <c>RoomAuthoringWindow.LoadRoom</c>, la única puerta de entrada de una sala ya guardada
        /// al editor.
        /// </summary>
        public void MigrateLegacyLightMarkers()
        {
            if (markers == null || markers.Length == 0) return;
            var kept = new List<Marker>();
            var moved = new List<Light>(lights ?? Array.Empty<Light>());
            bool any = false;
            foreach (var m in markers)
            {
                if (m != null && m.kind == MarkerKind.Light)
                {
                    any = true;
                    moved.Add(new Light
                    {
                        position = m.position,
                        y = m.y,
                        yawDegrees = m.yawDegrees,
                        level = m.level,
                        lightColor = m.lightColor,
                        lightIntensity = m.lightIntensity,
                        lightRange = m.lightRange,
                    });
                }
                else
                {
                    kept.Add(m);
                }
            }
            if (!any) return;
            markers = kept.ToArray();
            lights = moved.ToArray();
        }

        /// <summary>
        /// Expande cada <see cref="PillarGrid"/> a sus pilares individuales y vacía
        /// <see cref="pillarGrids"/>. Idempotente — no-op si ya está vacío, así que llamarla dos
        /// veces (o sobre una sala que nunca tuvo retículas) no duplica nada.
        ///
        /// Se llama UNA vez, al cargar la sala en la ventana (<c>RoomAuthoringWindow.LoadRoom</c>):
        /// es el único punto de entrada de una <see cref="RoomDefinition"/> ya guardada al editor.
        /// Deliberadamente NO se toca <c>RoomMeshBuilder</c>/<c>RoomColliderBuilder</c> — siguen
        /// sabiendo dibujar <see cref="pillarGrids"/> si algún día llega uno sin migrar, así que
        /// esto es una migración de AUTORÍA (qué se puede editar), no del generador.
        /// </summary>
        public void MigrateLegacyPillarGrids()
        {
            if (pillarGrids == null || pillarGrids.Length == 0) return;

            var expanded = new List<Pillar>(pillars ?? Array.Empty<Pillar>());
            foreach (var grid in pillarGrids)
            {
                if (grid == null) continue;
                for (int ix = 0; ix < grid.countX; ix++)
                    for (int iz = 0; iz < grid.countZ; iz++)
                        expanded.Add(new Pillar
                        {
                            position = grid.PositionOf(ix, iz),
                            size = grid.size,
                            sides = grid.sides,
                            yawDegrees = grid.yawDegrees,
                            level = grid.level,
                        });
            }
            pillars = expanded.ToArray();
            pillarGrids = Array.Empty<PillarGrid>();
        }

        /// <summary>
        /// Altura de nivel ya recortada: al menos <see cref="MinCeilingHeight"/> de hueco por
        /// debajo (más el grosor de la propia losa) y al menos <see cref="MinCeilingHeight"/>
        /// bajo <paramref name="minCeiling"/> por encima. Una losa sin hueco por el que pasar
        /// por alguno de los dos lados no es un piso, es un techo bajado.
        /// </summary>
        public float ClampLevelHeight(float requested, float minCeiling)
        {
            float t = Mathf.Max(0.001f, wallThickness);
            float lo = MinCeilingHeight + t;
            float hi = Mathf.Max(lo, minCeiling - MinCeilingHeight);
            return Mathf.Clamp(requested, lo, hi);
        }

        /// <summary>
        /// Alturas del canto SUPERIOR de cada losa, ya recortadas y ORDENADAS de abajo a arriba.
        /// El orden es lo que da sentido a "piso k": el array <see cref="levels"/> se edita en
        /// cualquier orden, pero el k-ésimo piso es el que queda k-ésimo EN ALTURA, no el que se
        /// añadió en k-ésimo lugar — reordenar la lista en el editor no puede cambiar la sala.
        /// </summary>
        public void SortedLevelTops(float minCeiling, List<float> into)
        {
            into.Clear();
            if (levels == null) return;
            foreach (var l in levels)
                if (l != null) into.Add(ClampLevelHeight(l.height, minCeiling));
            into.Sort();
        }

        /// <summary>
        /// Cuántos pisos hay POR ENCIMA de la planta baja — las losas que de verdad cuentan, que
        /// no es lo mismo que <c>levels.Length</c>: <see cref="SortedLevelTops"/> descarta las
        /// entradas nulas, así que un hueco en el array inflaba el recuento y desplazaba en uno la
        /// frontera "¿es este el piso de arriba?". El piso de arriba dejaba de reconocerse como
        /// tal y se le colgaban las cosas de un techo PLANO en vez del techo inclinado real.
        /// </summary>
        public int StoreyCount
        {
            get
            {
                if (levels == null) return 0;
                int n = 0;
                foreach (var l in levels) if (l != null) n++;
                return n;
            }
        }

        /// <summary>
        /// El suelo del piso <paramref name="storey"/>: 0 = el de la sala; k = el canto superior
        /// de la k-ésima losa contando desde abajo. Pedir un piso que no existe recorta al último
        /// que sí — un marcador guardado con level 3 en una sala a la que luego se le quita una
        /// losa cae al piso de arriba real, no al vacío.
        ///
        /// Vive aquí, con <paramref name="minCeiling"/> por parámetro igual que
        /// <see cref="ClampLevelHeight"/>, porque malla y colliders tienen que anclar cada
        /// feature EXACTAMENTE al mismo suelo — el motivo de siempre.
        /// </summary>
        public float StoreyBaseY(int storey, float minCeiling)
        {
            if (storey <= 0 || levels == null || levels.Length == 0) return 0f;
            SortedLevelTops(minCeiling, _storeyScratch);
            if (_storeyScratch.Count == 0) return 0f;
            return _storeyScratch[Mathf.Min(storey, _storeyScratch.Count) - 1];
        }

        /// <summary>
        /// El techo del piso <paramref name="storey"/>: el canto INFERIOR de la primera losa que
        /// quede por encima de su suelo, o la altura nominal de la sala si no hay más losas.
        /// Es lo que hace que una columna "del piso 1" pare justo bajo la losa del piso 2 en vez
        /// de atravesarla.
        /// </summary>
        public float StoreyCeilingY(int storey, float minCeiling)
        {
            float baseY = StoreyBaseY(storey, minCeiling);
            SortedLevelTops(minCeiling, _storeyScratch);
            float t = Mathf.Max(0.001f, wallThickness);
            foreach (float top in _storeyScratch)
                if (top > baseY + 0.001f) return top - t;
            return heightMeters;
        }

        // Scratch de StoreyBaseY/StoreyCeilingY. Estático y reutilizado como los de los
        // builders: se llama varias veces por reconstrucción y las salas se construyen de una
        // en una (misma no-reentrancia que _jambCuts y compañía).
        private static readonly List<float> _storeyScratch = new List<float>();

        /// <summary>
        /// Los tramos de escalera cuyo último peldaño llega a esta losa (o más arriba): son los
        /// que la atraviesan y por tanto los que le abren hueco. Vive aquí y no en cada
        /// generador porque la malla y los colliders tienen que abrir EXACTAMENTE el mismo hueco
        /// en el mismo sitio, igual que un boquete de pared.
        /// </summary>
        /// <param name="minCeiling">Para resolver el suelo del piso del que arranca cada tramo
        /// (<see cref="Stairs.level"/>): su altura real es base del piso + subida propia. Un
        /// tramo nunca abre hueco en su PROPIA losa ni en las de más abajo — perforaría el
        /// suelo en el que apoya.</param>
        public IEnumerable<Stairs> StairsReaching(float levelHeight, float minCeiling)
        {
            if (stairs == null) yield break;
            // Tolerancia de 5 cm: el mismo margen que usa la malla para "el ultimo peldaño llega
            // AL TECHO", aqui aplicado a "llega A LA LOSA". Sin margen, un tramo calculado para
            // llegar EXACTO se quedaria un pelo corto por redondeo y perderia su hueco.
            const float margin = 0.05f;
            foreach (var s in stairs)
            {
                if (s == null || !s.IsValid()) continue;
                float baseY = StoreyBaseY(s.level, minCeiling);
                if (levelHeight <= baseY + margin) continue; // su propio suelo, o uno de más abajo
                if (baseY + s.TopHeight() >= levelHeight - margin)
                    yield return s;
            }
        }

        /// <summary>
        /// Los pozos anclados a la losa cuyo canto SUPERIOR está a <paramref name="slabTop"/>,
        /// cualquiera que sea su <see cref="FloorHole.EffectiveMode"/> — Through, Well o
        /// Bottomless. El gemelo de <see cref="StairsReaching"/> y por el mismo motivo: malla y
        /// colliders tienen que abrir el mismo hueco o se ve un pozo por el que no se cae.
        ///
        /// Se casa por ALTURA y no por índice de array porque el piso de un pozo se cuenta en
        /// altura (<see cref="FloorHole.level"/>), igual que todo lo demás; el orden en que estén
        /// las losas en la lista no significa nada.
        ///
        /// Los de planta baja (<see cref="FloorHole.level"/> 0) NO salen por aquí: esos cuelgan del
        /// suelo de la sala, no de la losa de una entreplanta, y los resuelve el camino del suelo.
        /// </summary>
        public IEnumerable<FloorHole> PitsAtSlab(float slabTop, float minCeiling)
        {
            if (floorHoles == null) yield break;
            foreach (var f in floorHoles)
            {
                if (f == null || f.level <= 0 || !f.IsValid()) continue;
                if (Mathf.Abs(StoreyBaseY(f.level, minCeiling) - slabTop) > 1e-3f) continue;
                yield return f;
            }
        }

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
            /// El borde en <c>u0</c> / <c>u1</c> NO es el final de la abertura: es una esquina por
            /// la que sigue en la pared vecina. Ese borde no lleva jamba — poner una sería un
            /// tabique cruzado en mitad del hueco. La jamba de ese lado la pone el trozo de la
            /// otra pared, y las dos se encuentran exactamente en la arista del inglete.
            /// </summary>
            public bool openStart, openEnd;

            /// <summary>
            /// Solape O CONTACTO. La tolerancia no es paranoia numérica: dos aberturas que se
            /// tocan justo en el borde dejarían entre ellas un machón de grosor cero, y ahí las
            /// dos jambas caen una encima de otra. Un pilar de 0 mm no es geometría — si las has
            /// puesto pegadas, lo que quieres es una abertura más ancha.
            /// </summary>
            public bool Overlaps(HoleRect o) =>
                u0 <= o.u1 + 1e-4f && o.u0 <= u1 + 1e-4f
                && y0 <= o.y1 + 1e-3f && o.y0 <= y1 + 1e-3f;

            /// <summary>
            /// ¿Cae (u, y) en el vacío de alguno de estos boquetes? Vive aquí por el mismo motivo
            /// que el tipo: la malla decide con esto qué celda de la rejilla se salta y los
            /// colliders qué tramo NO emiten. Con una copia en cada uno, un cambio de criterio en
            /// el borde solo en uno de los dos vuelve a abrir el "veo una puerta y me choco".
            ///
            /// ESTRICTO en los cuatro bordes: un punto justo en el canto pertenece a la pared, no
            /// al hueco. Los dos llamadores preguntan por el CENTRO de una celda, y con los cortes
            /// puestos en los bordes del boquete un centro nunca cae encima de uno.
            /// </summary>
            public static bool InsideAny(List<HoleRect> holes, float u, float y)
            {
                for (int i = 0; i < holes.Count; i++)
                    if (u > holes[i].u0 && u < holes[i].u1 && y > holes[i].y0 && y < holes[i].y1)
                        return true;
                return false;
            }
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
                            // El borde del fundido hereda de QUIEN lo aporta, no del primero: si
                            // el que llega más a la izquierda venía doblando una esquina, el
                            // fundido sigue doblándola. Copiarlo de rects[i] a secas pondría una
                            // jamba dentro del hueco de la pared vecina.
                            openStart = (rects[i].u0 <= rects[j].u0 ? rects[i] : rects[j]).openStart,
                            openEnd = (rects[i].u1 >= rects[j].u1 ? rects[i] : rects[j]).openEnd,
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

        /// <summary>
        /// Todos los boquetes resueltos contra la planta y repartidos por pared: de "una puerta de
        /// 1,6 m centrada en la pared 0" a la fracción 0..1 y los metros de altura que usan la
        /// malla y los colliders.
        ///
        /// Vive aquí y no en cada generador porque lo piden LOS DOS, y cuando eran dos copias del
        /// mismo bucle cualquier arreglo tenía que aplicarse dos veces o el juego dibujaba una
        /// puerta contra la que te chocas.
        /// </summary>
        /// <param name="yCeilCap">Techo efectivo para recortar la altura. La malla pasa el techo
        /// MÁS BAJO menos un dintel mínimo, no la altura nominal: la fila superior de la pared es
        /// la que se estira con la pendiente y un hueco dentro de ella se estiraría con ella.</param>
        public void ResolveHoles(Vector2[] inner, float yFloor, float yCeilCap, List<HoleRect>[] perSide)
        {
            int n = inner.Length;
            for (int i = 0; i < n; i++) perSide[i].Clear();
            if (holes == null) return;

            var len = new float[n];
            var startAt = new float[n];
            float perim = 0f;
            for (int i = 0; i < n; i++)
            {
                startAt[i] = perim;
                len[i] = Vector2.Distance(inner[i], inner[(i + 1) % n]);
                perim += len[i];
            }
            if (perim < 1e-4f) return;

            float t = Mathf.Max(0.001f, wallThickness);
            // El suelo de cada piso, para el origen de `baseY`. Se saca del MISMO contorno que ya
            // recibe la función en vez de pedirlo por parámetro: los dos llamadores calculan su
            // `minCeil` así, y derivarlo aquí quita la posibilidad de que uno pase otro número.
            float minCeil = MinCeilingOver(inner);

            foreach (var hole in holes)
            {
                if (hole == null) continue;
                if (hole.width <= 0.001f || hole.height <= 0.001f) continue;
                // El índice de lado se envuelve, así que bajar `sides` reubica los huecos en vez
                // de perderlos.
                int side = ((hole.side % n) + n) % n;
                if (len[side] < 1e-4f) continue;

                // `baseY` cuenta desde el suelo de SU piso. El recorte por arriba sigue siendo el
                // techo de la sala: la pared no se parte por pisos, así que una ventana alta del
                // piso de arriba se recorta contra el mismo dintel que todo lo demás.
                float storeyBase = StoreyBaseY(hole.level, minCeil);
                float y0 = Mathf.Clamp(storeyBase + hole.baseY, yFloor, yCeilCap);
                float y1 = Mathf.Clamp(storeyBase + hole.baseY + hole.height, yFloor, yCeilCap);
                if (y1 - y0 < 1e-4f) continue;

                if (!hole.spanCorners)
                {
                    // Recortado contra los bordes de SU pared: un hueco más ancho que ella se
                    // queda en la pared entera en vez de desbordarse a la vecina.
                    float m = Mathf.Clamp(t / len[side], 0.001f, 0.45f);
                    float half = hole.width * 0.5f / len[side];
                    float a = Mathf.Clamp(hole.along - half, m, 1f - m);
                    float b = Mathf.Clamp(hole.along + half, m, 1f - m);
                    if (b - a < 1e-4f) continue;
                    perSide[side].Add(new HoleRect
                    {
                        u0 = a, u1 = b, y0 = y0, y1 = y1, bars = hole.grateBars,
                    });
                    continue;
                }

                float centre = startAt[side] + Mathf.Clamp01(hole.along) * len[side];
                // Un arco no puede dar la vuelta entera: queda siempre macizo el grosor de un muro
                // a cada lado. Sin este tope, una anchura absurda deja una sala sin ninguna pared
                // y ya no hay dónde apoyar las jambas.
                float halfArc = Mathf.Min(hole.width * 0.5f, (perim - 2f * t) * 0.5f);
                if (halfArc <= 1e-4f) continue;

                AddArc(len, startAt, perim, t, centre - halfArc, centre + halfArc,
                    y0, y1, hole.grateBars, perSide);
            }

            for (int i = 0; i < n; i++) MergeOverlapping(perSide[i]);
        }

        /// <summary>
        /// Reparte un arco del contorno en un trozo por pared. Los bordes interiores del reparto
        /// —los que caen justo en una esquina— se marcan como abiertos: por ahí la abertura sigue,
        /// y quien pusiera una jamba estaría cerrando el hueco por la mitad.
        ///
        /// Los bordes LIBRES sí se recortan contra su pared, igual que un boquete normal: si el
        /// arco termina a un milímetro de la esquina, sin recorte quedaría un machón de un
        /// milímetro, que no es un pilar sino una astilla.
        /// </summary>
        private static void AddArc(float[] len, float[] startAt, float perim, float t,
            float s0, float s1, float y0, float y1, int bars, List<HoleRect>[] perSide)
        {
            int n = len.Length;
            float span = s1 - s0;
            s0 -= Mathf.Floor(s0 / perim) * perim;      // a [0, perim)
            s1 = s0 + span;

            // Dos vueltas al contorno: el arco puede cruzar el punto donde el contorno cierra, y
            // ahí el segundo trozo tiene arclength mayor que el perímetro.
            for (int k = 0; k < n * 2; k++)
            {
                int i = k % n;
                if (len[i] < 1e-4f) continue;
                float a0 = startAt[i] + (k >= n ? perim : 0f);
                float a1 = a0 + len[i];

                float lo = Mathf.Max(s0, a0), hi = Mathf.Min(s1, a1);
                if (hi - lo < 1e-5f) continue;

                bool openA = s0 < a0 - 1e-5f;   // el arco venía de la pared anterior
                bool openB = s1 > a1 + 1e-5f;   // y sigue en la siguiente

                float ua = (lo - a0) / len[i], ub = (hi - a0) / len[i];
                float m = Mathf.Clamp(t / len[i], 0.001f, 0.45f);
                if (!openA) ua = Mathf.Clamp(ua, m, 1f - m);
                if (!openB) ub = Mathf.Clamp(ub, m, 1f - m);
                if (ub - ua < 1e-4f) continue;

                perSide[i].Add(new HoleRect
                {
                    u0 = ua, u1 = ub, y0 = y0, y1 = y1, bars = bars,
                    openStart = openA, openEnd = openB,
                });
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

        public Vector2[] InnerContour()
        {
            if (planMode == PlanMode.Manual && manualContour != null && manualContour.Length >= 3)
                return EnsureContourCCW(manualContour);
            return ApplyIrregularity(planMode == PlanMode.Blocks ? BlockContour() : PolygonContour());
        }

        /// <summary>La planta que daría el modo Polygon o Blocks ahora mismo — el punto de
        /// partida de "Start from current shape" al pasar a <see cref="PlanMode.Manual"/>, para
        /// no tener que dibujar los primeros puntos desde cero. Se llama ANTES de cambiar
        /// <see cref="planMode"/> a <c>Manual</c>: llamado después ya no sabría si el punto de
        /// partida "correcto" era el polígono o las muescas.</summary>
        public Vector2[] ProceduralContour() =>
            ApplyIrregularity(planMode == PlanMode.Blocks ? BlockContour() : PolygonContour());

        /// <summary>
        /// Todo contorno de este generador se espera ANTIHORARIO — es la base de
        /// <see cref="OutwardNormal"/> y de todo lo que sale de ella. Un contorno dibujado a mano
        /// en la vista de escena no viene con esa garantía: depende del orden en que se hayan
        /// puesto los puntos, y pedirle al usuario que dibuje "en sentido antihorario" es pedirle
        /// algo que la herramienta debería resolver sola. Se detecta por el área con signo y se
        /// invierte si hace falta.
        /// </summary>
        private static Vector2[] EnsureContourCCW(Vector2[] pts)
        {
            float area = 0f;
            for (int i = 0; i < pts.Length; i++)
            {
                Vector2 c = pts[i], n = pts[(i + 1) % pts.Length];
                area += c.x * n.y - n.x * c.y;
            }
            if (area >= 0f) return pts;
            var rev = (Vector2[])pts.Clone();
            Array.Reverse(rev);
            return rev;
        }

        /// <summary>
        /// True si dos aristas NO adyacentes de <paramref name="p"/> se cruzan (contorno en forma
        /// de lazo/mariposa). Solo hace falta para <see cref="PlanMode.Manual"/>: un contorno
        /// procedural nunca puede salir cruzado por construcción, pero uno dibujado a mano arrastrando
        /// vértices en la vista de escena sí — y el recorte de orejas de <c>PolygonTriangulator</c>
        /// solo comprueba que cada oreja candidata esté vacía de OTROS vértices, no que el polígono
        /// sea simple, así que un contorno cruzado puede triangular "bien" y colarse como sala válida.
        /// </summary>
        public static bool ContourSelfIntersects(Vector2[] p)
        {
            int n = p.Length;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i || (j + 1) % n == i || (i + 1) % n == j) continue;
                    if (SegmentsCross(p[i], p[(i + 1) % n], p[j], p[(j + 1) % n])) return true;
                }
            return false;
        }

        private static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float Side(Vector2 p0, Vector2 p1, Vector2 p2) =>
                (p1.x - p0.x) * (p2.y - p0.y) - (p1.y - p0.y) * (p2.x - p0.x);
            float d1 = Side(a, b, c), d2 = Side(a, b, d), d3 = Side(c, d, a), d4 = Side(c, d, b);
            return ((d1 > 0f) != (d2 > 0f)) && ((d3 > 0f) != (d4 > 0f));
        }

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
