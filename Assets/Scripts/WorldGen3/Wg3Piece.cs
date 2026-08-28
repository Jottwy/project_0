using System;
using UnityEngine;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>Clase de escala de un espacio. Es lo que lee el campo de escala para decidir qué
    /// pieza quiere en cada sitio (L20 y R28 son la misma regla; aquí están fundidas).</summary>
    public enum Wg3Scale
    {
        Narrow = 0,
        Medium = 1,
        Large = 2,
        Weird = 3
    }

    /// <summary>
    /// Tipo de conexión. REGLA L5/L10 — no hay comodín: dos piezas encajan solo si el tipo
    /// coincide. Un socket "vale para todo" convierte la composición en ruido, y es la regla que
    /// más tienta romper cuando falta una transición en el catálogo. La respuesta correcta a esa
    /// falta es autorar la transición, no relajar el tipo.
    ///
    /// **ADR-098 matiza esto, y conviene leer QUÉ matiza.** Un CONECTOR generado sí puede empezar a
    /// 2,4 m y acabar a 5,0: la transición deja de tener que autorarse porque la pone el generador,
    /// como un tramo con dos bocas de anchura distinta. Lo que NO cambia es esta regla: ninguna
    /// pieza del catálogo gana una boca que valga para todo, y dos piezas siguen encajando solo si
    /// coinciden. En el catálogo de código anchura y tipo van atados —<c>Sock()</c> da 5,0 m a
    /// <see cref="Wg3SocketType.Wide"/> y 2,4 a <see cref="Wg3SocketType.Corridor"/>—, así que sin
    /// esa concesión los dos anchos serían dos mundos que no se mezclan. <c>Service</c> queda fuera:
    /// solo conecta consigo mismo, porque es la clase semántica y no una medida.
    /// </summary>
    public enum Wg3SocketType
    {
        Corridor = 0,
        Wide = 1,
        Service = 2
    }

    /// <summary>
    /// Una boca de la pieza.
    ///
    /// CONTRATO DE PARAMETRIZACIÓN: el socket no guarda un punto, guarda <see cref="side"/> más
    /// <see cref="offset"/> recorriendo el perímetro EN SENTIDO HORARIO desde la esquina (0, D):
    ///
    /// <code>
    ///   (0,D) ────N───► (W,D)
    ///     ▲                │
    ///     W                E
    ///     │                ▼
    ///   (0,0) ◄───S──── (W,0)
    /// </code>
    ///
    /// De ahí sale la propiedad que sostiene todo el emparejado: <b>girar la pieza deja el
    /// <c>offset</c> intacto y convierte el lado en <c>(side + r) % 4</c></b>. Es decir, el giro
    /// no toca los sockets — se demuestra en <c>Wg3PieceTests.RotatingAPieceKeepsSocketOffsets</c>.
    /// Guardar un punto libre (x,z) obligaría a rotar coordenadas en cada candidata y a comparar
    /// flotantes para saber en qué lado cayó.
    ///
    /// L5 se cumple igual: el offset es libre dentro del lado, así que una boca puede estar pegada
    /// a una esquina. Lo único que se pierde es la posibilidad de un socket que no esté en el
    /// perímetro, y eso no es una boca, es un agujero interior.
    /// </summary>
    [Serializable]
    public struct Wg3Socket
    {
        /// <summary>0 = N (+Z), 1 = E (+X), 2 = S (−Z), 3 = O (−X).</summary>
        public int side;

        /// <summary>Metros a lo largo del lado, hasta el CENTRO de la boca.</summary>
        public float offset;

        /// <summary>Anchura libre en metros. L6: la anchura es parte de la compatibilidad, no un
        /// detalle visual — dos anchuras distintas exigen pieza de transición.</summary>
        public float width;

        public Wg3SocketType type;

        /// <summary>Cota del suelo en la boca, relativa al origen de la pieza. F0 la deja siempre
        /// a 0 y el validador exige que case; el campo viaja desde el día uno porque el formato
        /// capaz primero y el lector después es más barato que migrar el formato con el mundo en
        /// marcha (es exactamente el error que dejó <c>ceiling_height</c> viajando por el wire
        /// meses sin que nadie lo leyera).</summary>
        public float floorY;

        /// <summary>Cota del techo en la boca, relativa al origen de la pieza.</summary>
        public float ceilingY;

        public Wg3Socket(int side, float offset, float width, Wg3SocketType type,
            float floorY = 0f, float ceilingY = 3.2f)
        {
            this.side = side;
            this.offset = offset;
            this.width = width;
            this.type = type;
            this.floorY = floorY;
            this.ceilingY = ceilingY;
        }
    }

    /// <summary>
    /// Una pieza del catálogo: la unidad de construcción de WG3.
    ///
    /// Aquí NO hay geometría. La composición trabaja solo con huella y bocas, así que se puede
    /// testear sin abrir Unity y —lo que importa de verdad— es lo mismo que sabrá Rust en F2: id,
    /// tamaño, bocas y la chuleta de colisión. <see cref="geometryId"/> es el único puente hacia
    /// la malla, y lo resuelve el ensamblador de la tanda 2.
    /// </summary>
    [Serializable]
    public sealed class Wg3Piece
    {
        /// <summary>Identificador estable. Entra en el hash de decisión, así que renombrarlo
        /// cambia el mundo generado.</summary>
        public string id;

        /// <summary>Huella en METROS, no en tiles. WG3 no hereda la retícula de 5 m de WG2: es
        /// precisamente la restricción de la que se está saliendo.</summary>
        public float sizeX;
        public float sizeZ;

        public float heightMeters = 3.2f;

        public Wg3Scale scale = Wg3Scale.Medium;

        /// <summary>Peso base del sorteo, antes de que lo modulen el campo de escala, la rareza y
        /// la penalización de repetición.</summary>
        public float weight = 1f;

        /// <summary>Profundidad mínima de rama para que la pieza sea elegible (R29/R30). Bajo A1
        /// esto se sustituirá por distancia a un ancla, que sí es función pura de la posición.</summary>
        public int minDepth;

        /// <summary>Callejón sin salida a propósito (L12). Se marca para poder MEDIRLOS: un mundo
        /// sin ninguno se lee como pasillo de metro, y uno con demasiados, como laberinto.</summary>
        public bool isDeadEnd;

        /// <summary>Clave del modelo que la dibuja. Hoy es informativa —la geometría sale de los
        /// campos de abajo—; queda como gancho para cuando el autorado venga de un asset y no de
        /// código. La composición nunca la mira.</summary>
        public string geometryId;

        public Wg3Socket[] sockets = Array.Empty<Wg3Socket>();

        // ── geometría autorada (L18: la irregularidad se autora, no se genera) ──────────────
        //
        // NO se reutiliza `RoomDefinition`, y no por capricho: se mide en TILES DE 5 m
        // (`tilesX`/`tilesZ`), así que un pasillo de 11 × 2,4 m no se puede expresar con él.
        // Colgar WG3 de ese modelo sería devolverlo a la retícula de la que viene huyendo. Estos
        // campos son la versión en METROS de lo mismo, y son la ÚNICA fuente de la que salen tanto
        // la malla como la colisión (R2).

        /// <summary>Grosor de las paredes, hacia DENTRO de la huella. Hacia dentro y no centradas
        /// en el borde para que la huella sea el extremo exterior: dos piezas encajadas dejan sus
        /// dos paredes espalda contra espalda en vez de solapándose.</summary>
        public float wallThickness = 0.15f;

        /// <summary>Columnas interiores. REGLA L14: son ESTRUCTURA, no decoración — bloquean la
        /// vista, parten la sala y obligan a rodearlas, y por eso llevan colisión exacta.</summary>
        public Wg3Pillar[] pillars = Array.Empty<Wg3Pillar>();

        /// <summary>Volúmenes macizos interiores. Cubren de una vez L4 (habitación dentro de
        /// habitación, con un bloque grande) y L13 (paredes parciales, con uno estrecho y largo):
        /// son el mismo dato con proporciones distintas, y separarlos en dos tipos solo duplicaría
        /// el código que los talla.</summary>
        public Wg3Block[] blocks = Array.Empty<Wg3Block>();

        /// <summary>Tramos de escalera. En F0 solo aportan silueta y colisión escalonada; el
        /// desnivel de verdad —que las dos bocas queden a cotas distintas— es F5.</summary>
        public Wg3StairRun[] stairs = Array.Empty<Wg3StairRun>();

        /// <summary>
        /// Volúmenes YA HORNEADOS de una pieza AUTORADA. Cuando no está vacío manda sobre
        /// <see cref="wallThickness"/>, <see cref="pillars"/>, <see cref="blocks"/> y
        /// <see cref="stairs"/>: la geometría no se genera, se leyó de un modelo dibujado a mano.
        ///
        /// El comentario de arriba sigue en pie — WG3 NO cuelga su modelo de <c>RoomDefinition</c>,
        /// que se mide en tiles de 5 m. Lo que cambia es de dónde sale este array: el editor de
        /// salas es el TABLERO DE DIBUJO, y al hornear el dibujo se traduce a metros y a esquina
        /// mínima. Aquí no llega un tile, ni un pivote centrado, ni el tipo del sistema viejo: solo
        /// cajas, que es lo único que WG3 y Rust entienden. Por eso F7 puede borrar WG2 sin tocar
        /// una pieza ya horneada.
        /// </summary>
        public Wg3Volume[] bakedVolumes = Array.Empty<Wg3Volume>();

        /// <summary>
        /// La malla autorada que dibuja esta pieza. SOLO CLIENTE: no viaja en el manifiesto, no
        /// entra en el digest y el servidor no sabe que existe. Nula = la pieza se dibuja con las
        /// cajas de <see cref="bakedVolumes"/>, que es lo que pasa con todo el catálogo de código.
        ///
        /// Es la línea de R25 llevada a su conclusión: lo que no bloquea, no cruza la frontera de
        /// autoridad. El detalle visual de una pieza —molduras, rodapiés, props— vive aquí y el
        /// servidor sigue colisionando contra las mismas cajas de siempre.
        /// </summary>
        public GameObject visualPrefab;

        /// <summary>
        /// Dónde cae el pivote del prefab en coordenadas locales de la pieza, en metros.
        ///
        /// Hace falta porque los dos contratos de pivote NO coinciden: el editor de salas centra el
        /// prefab en su footprint y WG3 pone el origen en la esquina mínima. Sin este desplazamiento
        /// la malla sale corrida media pieza respecto a su colisión, y en una captura se ve
        /// perfectamente normal — hasta que atraviesas una pared que se dibuja un metro más allá.
        /// </summary>
        public Vector2 visualPivot;

        /// <summary>Longitud del lado <paramref name="side"/> de una pieza de <paramref name="w"/>
        /// por <paramref name="d"/>. N y S corren en X; E y O corren en Z.</summary>
        public static float SideLength(int side, float w, float d) =>
            (side == 0 || side == 2) ? w : d;

        /// <summary>
        /// Punto local del socket dentro de una pieza de <paramref name="w"/> × <paramref name="d"/>,
        /// según el recorrido horario documentado en <see cref="Wg3Socket"/>. Se le pasan las
        /// dimensiones porque al colocar la pieza girada las dimensiones son las YA giradas.
        /// </summary>
        public static Vector2 LocalPoint(int side, float offset, float w, float d)
        {
            switch (((side % 4) + 4) % 4)
            {
                case 0: return new Vector2(offset, d);
                case 1: return new Vector2(w, d - offset);
                case 2: return new Vector2(w - offset, 0f);
                default: return new Vector2(0f, offset);
            }
        }

        /// <summary>Normal hacia AFUERA del lado, en XZ.</summary>
        public static Vector2 OutwardNormal(int side)
        {
            switch (((side % 4) + 4) % 4)
            {
                case 0: return new Vector2(0f, 1f);
                case 1: return new Vector2(1f, 0f);
                case 2: return new Vector2(0f, -1f);
                default: return new Vector2(-1f, 0f);
            }
        }

        /// <summary>El lado que tiene que ofrecer la pieza vecina para casar con este.</summary>
        public static int OppositeSide(int side) => (((side % 4) + 4) % 4 + 2) % 4;
    }

    /// <summary>
    /// Una pieza ya colocada en el mundo.
    ///
    /// CONTRATO DE ORIGEN: <see cref="originX"/>/<see cref="originZ"/> es la ESQUINA MÍNIMA de la
    /// huella girada, no el centro. Es lo contrario del contrato de <c>RoomPool.RoomEntry</c>, que
    /// pone el pivote en el centro del footprint — la conversión la hace el ensamblador de la
    /// tanda 2 y está anotada allí. Aquí manda la esquina porque con ella el giro y el solape son
    /// aritmética de rectángulos sin un solo caso especial.
    /// </summary>
    public sealed class Wg3Placement
    {
        public Wg3Piece piece;

        /// <summary>Giro en cuartos de vuelta, horario visto desde +Y. 0..3.</summary>
        public int rotation;

        public float originX;
        public float originZ;

        /// <summary>
        /// Cota del SUELO de la pieza, en metros de mundo. ADR-097.
        ///
        /// Hasta F5 toda pieza estaba a 0 y la verticalidad solo existía DENTRO de una —una escalera
        /// que sube a una plataforma que no lleva a ningún sitio—. Es el mismo agujero que fundó
        /// WG3: en WG2 la altura del suelo era función del índice de capa, así que rampas y medias
        /// plantas no es que faltaran, es que no había dónde escribirlas.
        ///
        /// La decide el compositor por propagación: la semilla va a 0, y cada hija se coloca a la
        /// altura que haga coincidir su boca con la del padre. **Cambiar de nivel solo puede hacerlo
        /// una pieza cuyas dos bocas estén a cotas distintas** — o sea, el desnivel se AUTORA, no se
        /// genera (L18).
        /// </summary>
        public float originY;

        /// <summary>Profundidad de rama desde la pieza semilla.</summary>
        public int depth;

        /// <summary>Índice de la pieza a la que se enganchó, o −1 si es la semilla. Es la "memoria
        /// contextual" de R26 en su forma compatible con A1: mirar la RAMA local, que se deriva de
        /// la posición, en vez de un historial global del recorrido.</summary>
        public int parentIndex = -1;

        /// <summary>Estado por socket: 0 = abierto, 1 = conectado, 2 = taponado.</summary>
        public byte[] socketState;

        public float SizeX => (rotation % 2 == 0) ? piece.sizeX : piece.sizeZ;
        public float SizeZ => (rotation % 2 == 0) ? piece.sizeZ : piece.sizeX;

        public float MaxX => originX + SizeX;
        public float MaxZ => originZ + SizeZ;

        /// <summary>Lado del socket <paramref name="index"/> visto en coordenadas de mundo.</summary>
        public int WorldSide(int index) => (piece.sockets[index].side + rotation) % 4;

        /// <summary>Punto de mundo del socket <paramref name="index"/>. El offset no se toca al
        /// girar (ver <see cref="Wg3Socket"/>), solo cambian el lado y las dimensiones.</summary>
        public Vector2 WorldPoint(int index)
        {
            Wg3Socket s = piece.sockets[index];
            Vector2 local = Wg3Piece.LocalPoint(WorldSide(index), s.offset, SizeX, SizeZ);
            return new Vector2(originX + local.x, originZ + local.y);
        }

        /// <summary>Solape estricto de huellas. El epsilon existe porque dos piezas encajadas
        /// COMPARTEN el plano de la junta: tocarse es correcto, penetrar no.</summary>
        public bool Overlaps(float x, float z, float w, float d, float eps = 0.02f) =>
            originX < x + w - eps && originX + SizeX - eps > x &&
            originZ < z + d - eps && originZ + SizeZ - eps > z;
    }
}
