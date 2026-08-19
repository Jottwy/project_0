using System.Collections.Generic;
using UnityEngine;

// La malla y los colliders resuelven los boquetes con EL MISMO tipo y el MISMO fundido de
// solapes: si cada uno tuviera el suyo podrían divergir, que es el fallo de "veo una puerta y
// me choco contra ella".
using HoleRect = BackroomsSurvival.Gameplay.GridWorld.RoomDefinition.HoleRect;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Convierte un <see cref="RoomDefinition"/> en UNA sola malla.
    ///
    /// Una malla y no un montón de primitivas porque es la diferencia entre una sala y cientos
    /// de draw calls: el mundo puede tener muchas salas cargadas a la vez. Los tres materiales
    /// (suelo / pared / techo) se separan en SUBMALLAS, que es lo que permite tener tres
    /// materiales distintos sin partir el objeto en tres.
    ///
    /// PURO: mismo definition ⇒ misma malla, sin estado ni aleatoriedad. Es lo que deja
    /// reconstruir en vivo mientras se arrastra un parámetro y, más adelante, derivar los
    /// colliders de la misma fuente sin que puedan discrepar.
    ///
    /// FORMA DEL SÓLIDO: la sala es un volumen con grosor, no una superficie. Se emiten DOS
    /// cascarones cerrados y anidados — el exterior (de −t a h+t sobre el contorno exterior) y
    /// el interior, que es la cavidad donde se anda. Cada uno cierra por su cuenta, así que la
    /// malla es estanca y cada arista tiene exactamente una opuesta.
    ///
    /// El primer intento NO era así: mezclaba una tapa de suelo, una banda de pared y un anillo
    /// de corona que compartían las mismas aristas — tres caras por arista, geometría no
    /// manifold, 48 aristas duplicadas en el test. Se conserva escrito porque la versión ingenua
    /// parece razonable hasta que se comprueba.
    ///
    /// Los vértices NO se comparten entre caras: cada quad lleva los suyos con su normal. En
    /// arquitectura las aristas tienen que verse duras, y compartir vértices las suavizaría.
    /// Cuesta vértices, pero una sala de 64 lados sigue siendo trivial para la GPU.
    /// </summary>
    public static class RoomMeshBuilder
    {
        public const int SubmeshFloor = 0;
        public const int SubmeshWall = 1;
        public const int SubmeshCeiling = 2;
        public const int SubmeshCount = 3;

        private static readonly List<Vector3> _verts = new List<Vector3>();
        private static readonly List<Vector3> _normals = new List<Vector3>();
        private static readonly List<Vector2> _uvs = new List<Vector2>();
        private static readonly List<int>[] _tris =
        {
            new List<int>(), new List<int>(), new List<int>(),
        };

        /// <summary>
        /// Construye la malla de <paramref name="def"/>. Si se pasa <paramref name="into"/> se
        /// REUTILIZA ese Mesh en vez de crear otro — la ventana reconstruye en cada cambio de
        /// parámetro y crear un Mesh por pulsación de tecla fuga memoria hasta el próximo GC.
        /// </summary>
        /// <summary>
        /// True si la ÚLTIMA construcción tuvo que recurrir al respaldo del suelo sin recortar.
        /// Cuando pasa, la malla y los colliders NO describen lo mismo, así que esa sala no se
        /// debe hornear. Lo consultan los tests y el guardado.
        /// </summary>
        public static bool TriangulationFailed { get; private set; }

        public static Mesh Build(RoomDefinition def, Mesh into = null)
        {
            TriangulationFailed = false;
            float t = Mathf.Max(0.001f, def.wallThickness);
            float h = Mathf.Max(0.01f, def.heightMeters);

            Vector2[] inner = def.InnerContour();
            Vector2[] outer = RoomDefinition.OffsetOutward(inner, t);

            // El suelo y el techo tienen el MISMO grosor que la pared: la sala ocupa una región
            // que el mundo deja vacía, así que trae su propio suelo y su propio techo y no hay
            // losa del chunk debajo con la que pelearse. (Cuando la sala se metía dentro de un
            // chunk ya generado, esto era justo al revés.)
            float yBottom = -t, yFloor = 0f, yCeil = h, yTop = h + t;

            Clear();

            // Boquetes y cortes POR LADO, resueltos una sola vez y compartidos entre las paredes
            // y las tapas. Compartirlos no es una optimización: si la tapa emite una arista por
            // lado y la pared está subdividida por los cortes de un boquete, las aristas no
            // coinciden y quedan T-junctions — 30 aristas sin opuesta en el test, y grietas
            // finas en pantalla. Las dos superficies tienen que cortar por los MISMOS sitios.
            int n = inner.Length;
            float minCeil = def.MinCeilingOver(inner);
            var sideHoles = new List<HoleRect>[n];
            var sideCuts = new List<float>[n];
            for (int i = 0; i < n; i++) sideHoles[i] = new List<HoleRect>();

            // Contra el techo MAS BAJO menos un dintel minimo, no contra la altura nominal.
            //
            // Dos razones. Con el techo inclinado una ventana alta en el lado bajo asomaria por
            // encima del techo. Y sobre todo: la fila SUPERIOR de la pared es la que se estira
            // para seguir la pendiente, asi que un hueco que la alcanzase se estiraria con ella y
            // una ventana se convertiria en una ranura abierta hasta el techo -- 6 aristas
            // abiertas, y en pantalla una sala sin dintel.
            def.ResolveHoles(inner, yFloor, minCeil - MinLintel, sideHoles);

            for (int i = 0; i < n; i++) sideCuts[i] = UCuts(sideHoles[i]);

            // Los mismos cortes, expresados sobre la cara EXTERIOR. Hace falta porque esa cara es
            // más larga que la interior: reusar la misma fracción ensancharía el hueco por fuera
            // —3 cm en una puerta de 1,6 m— y dejaría la jamba abocinada. Los EXTREMOS se quedan
            // en 0 y 1 aunque la proyección diga otra cosa: por el inglete, la cara exterior
            // sobresale de la interior en las esquinas, y mapear también los extremos dejaba el
            // panel sin cubrir su propio lado — 24 aristas abiertas en el fondo exterior.
            var sideCutsOut = new List<float>[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 a0 = inner[i], a1 = inner[(i + 1) % n];
                Vector2 b0 = outer[i], b1 = outer[(i + 1) % n];
                var mapped = new List<float> { 0f };
                for (int k = 1; k < sideCuts[i].Count - 1; k++)
                    mapped.Add(MapU(sideCuts[i][k], a0, a1, b0, b1));
                mapped.Add(1f);
                sideCutsOut[i] = mapped;
            }

            // Cortes de ALTURA compartidos por TODAS las paredes, no por la pared que tiene el
            // hueco. Suena excesivo y no lo es: la arista vertical de una esquina la comparten
            // dos paredes, así que si una se parte a 2,2 m y la vecina no, la esquina queda con
            // una T-junction. Lo mismo pasa entre una celda de pared y la jamba de al lado. El
            // único esquema que cierra sin casos especiales es que todo corte igual.
            var yCuts = new List<float>();
            for (int i = 0; i < n; i++)
                for (int k = 0; k < sideHoles[i].Count; k++)
                {
                    yCuts.Add(sideHoles[i][k].y0);
                    yCuts.Add(sideHoles[i][k].y1);
                }
            var innerYCuts = LevelCuts(yCuts, yFloor, minCeil);
            var outerYCuts = LevelCuts(yCuts, yBottom, yTop);

            // Tapas: los boquetes van en pared, así que suelo y techo no se parten en altura —
            // pero sí siguen los cortes horizontales de las paredes.
            // Los pozos atraviesan las DOS losas del suelo (la cara que se pisa y el fondo
            // exterior), así que las dos tapas se recortan con los mismos rectángulos. Recortar
            // solo la de arriba dejaría el fondo tapado y el pozo sin salida por abajo.
            // Un pozo es una CAJA que cuelga por debajo de la losa, no un tubo de una sola cara:
            // con 2,5 m de profundidad sobre una losa de 0,2 m, la mayor parte del pozo está al
            // aire y también se ve por fuera. Por eso lleva su rectángulo interior y otro
            // exterior, igual que la sala.
            var pitsIn = PitRects(def, f => true, f => 0f);
            // La tapa EXTERIOR (la que se ve desde debajo del edificio) se corta para TODOS los
            // pozos, con fondo o sin él. La versión anterior se la saltaba para los bottomless
            // razonando que "rematan dentro de sí mismos y nunca llegan a tocarla": es falso, y
            // por eso el pozo salía tapado. El tubo va de `yShaft = -max(Depth, t)` hasta el
            // suelo de la sala, y esta losa está a `-t` — como `max(Depth, t) >= t`, el tubo la
            // atraviesa SIEMPRE. Sin recortarla quedaba una chapa cruzando el pozo justo debajo
            // del borde: el agujero se veía abierto arriba y tapado un palmo más abajo.
            var pitsOut = PitRects(def, f => true, f => t);

            // Techo inclinado: la altura pasa a ser funcion del punto. Con tilt 0 estas dos
            // funciones devuelven la constante de siempre y no cambia nada.
            System.Func<Vector2, float> ceilAt = q => def.CeilingYAt(q);
            System.Func<Vector2, float> topAt = q => def.CeilingYAt(q) + t;

            // UV de una tapa en pendiente: la componente a lo largo de la caida se estira por
            // 1/cos, que es lo que la superficie real mide de mas respecto a su sombra en planta.
            // La pendiente REAL de la malla, medida sobre ella misma: pedir 40 grados en una sala
            // baja da menos, y usar la pedida estiraria la textura de mas.
            float yawRad0 = def.ceilingTiltYaw * Mathf.Deg2Rad;
            var down0 = new Vector2(Mathf.Sin(yawRad0), Mathf.Cos(yawRad0));
            float realTan = def.ceilingTilt <= 0.001f ? 0f
                : (def.CeilingYAt(-down0) - def.CeilingYAt(down0)) * 0.5f;
            float slopeStretch = Mathf.Sqrt(1f + realTan * realTan);
            float yawRad = yawRad0;
            var slopeDir = new Vector2(Mathf.Sin(yawRad), Mathf.Cos(yawRad));
            System.Func<Vector2, Vector2> ceilUV = def.ceilingTilt <= 0.001f ? (System.Func<Vector2, Vector2>)null
                : q =>
                {
                    float alongSlope = Vector2.Dot(q, slopeDir) * slopeStretch;
                    float across = q.x * slopeDir.y - q.y * slopeDir.x;
                    return new Vector2(across, alongSlope) / GridVisualConstants.TileSize;
                };

            AddCap(outer, yBottom, Vector3.down, SubmeshWall, sideCutsOut, pitsOut);
            AddCap(outer, yTop, Vector3.up, SubmeshWall, sideCutsOut, null, topAt, ceilUV);
            AddCap(inner, yFloor, Vector3.up, SubmeshFloor, sideCuts, pitsIn);
            AddCap(inner, yCeil, Vector3.down, SubmeshCeiling, sideCuts, null, ceilAt, ceilUV);
            AddPits(def, yFloor, yBottom, t, pitsIn, pitsOut);

            // Paredes: cara interior, cara exterior y las jambas que las cosen a través del
            // grosor en cada boquete. Van juntas y no en dos pasadas independientes porque un
            // hueco deja de ser "dos cascarones anidados" y pasa a ser una sola superficie con
            // un túnel — separarlas dejaría el túnel sin cerrar.
            AddWalls(inner, outer, sideHoles, sideCuts, sideCutsOut, innerYCuts, outerYCuts, t,
                ceilAt, topAt, def.trimEnabled);

            // Columnas: cada una es su propio prisma cerrado dentro de la cavidad, así que no
            // interfieren con la estanqueidad del resto.
            if (def.pillars != null)
                foreach (var p in def.pillars)
                    if (p != null && p.size > 0.001f)
                    {
                        // `size` es el ancho ENTRE CARAS, no la diagonal: dividir a secas por 2
                        // daba el radio al VÉRTICE, y una columna de 4 lados pedida de 0,7 m
                        // salía de 0,49. Corregir por cos(π/N) hace que el ancho medido sea el
                        // que se escribió, con cualquier número de lados.
                        int ps = Mathf.Clamp(p.sides, 3, 32);
                        float radius = p.size * 0.5f / Mathf.Cos(Mathf.PI / ps);
                        // Del suelo de SU piso al techo de SU piso: una columna sostiene la losa
                        // de encima, no la atraviesa. Sin entreplantas los dos devuelven el
                        // suelo y el techo de la sala y todo queda como siempre.
                        AddPrism(p.position, radius, ps,
                            def.StoreyBaseY(p.level, minCeil), def.StoreyCeilingY(p.level, minCeil),
                            p.yawDegrees);
                    }

            if (def.pillarGrids != null)
                foreach (var g in def.pillarGrids)
                {
                    if (g == null || g.size <= 0.001f) continue;
                    int gs = Mathf.Clamp(g.sides, 3, 32);
                    float radius = g.size * 0.5f / Mathf.Cos(Mathf.PI / gs);
                    float gy0 = def.StoreyBaseY(g.level, minCeil);
                    float gy1 = def.StoreyCeilingY(g.level, minCeil);
                    for (int ix = 0; ix < g.countX; ix++)
                        for (int iz = 0; iz < g.countZ; iz++)
                            AddPrism(g.PositionOf(ix, iz), radius, gs, gy0, gy1, g.yawDegrees);
                }

            if (def.blocks != null)
                foreach (var b in def.blocks)
                    if (b != null && b.sizeX > 0.001f && b.sizeZ > 0.001f && b.height > 0.001f)
                    {
                        float off = def.StoreyBaseY(b.level, minCeil);
                        if (b.holes != null && b.holes.Length > 0)
                            AddBlockWithHoles(b, off);
                        else
                            AddClosedPrism(BoxCorners(b.position, b.sizeX, b.sizeZ, b.yawDegrees),
                                off + b.baseY, off + b.baseY + b.height);
                    }

            if (def.stairs != null)
                foreach (var s in def.stairs)
                    AddStairs(s, def.StoreyBaseY(s?.level ?? 0, minCeil));

            // Entreplantas: una losa por nivel, del ancho de la sala entera y con hueco donde
            // una escalera llegue lo bastante alto para atravesarla. Van DESPUÉS de las
            // escaleras y no antes por orden de lectura nada más — no comparten geometría, cada
            // una es su propio cascarón cerrado.
            if (def.levels != null)
                foreach (var lvl in def.levels)
                    if (lvl != null)
                        AddLevelSlab(def, inner, lvl, t, minCeil);

            var mesh = into != null ? into : new Mesh();
            mesh.Clear();
            mesh.name = $"Room_{def.tilesX}x{def.tilesZ}_{def.sides}s";
            // Una sala grande y muy detallada puede pasar de 65 535 vértices; con índices de 32
            // bits deja de ser un límite del que preocuparse.
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.SetVertices(_verts);
            mesh.SetNormals(_normals);
            mesh.SetUVs(0, _uvs);
            mesh.subMeshCount = SubmeshCount;
            for (int s = 0; s < SubmeshCount; s++)
                mesh.SetTriangles(_tris[s], s);

            mesh.RecalculateBounds();
            return mesh;
        }

        private static void Clear()
        {
            _verts.Clear();
            _normals.Clear();
            _uvs.Clear();
            for (int s = 0; s < SubmeshCount; s++) _tris[s].Clear();
        }

        /// <summary>
        /// Tapa horizontal, como abanico desde el CENTROIDE del polígono. El abanico vale porque
        /// la planta que produce <see cref="RoomDefinition.InnerContour"/> es siempre convexa;
        /// una planta cóncava pediría triangulación de verdad.
        ///
        /// El centro sale del polígono y NO se asume el origen: las columnas usan esta misma
        /// función y están descentradas, así que abanicar desde (0,0) les daba tapas retorcidas
        /// que el test cazó como 28 aristas duplicadas.
        ///
        /// <paramref name="sideCuts"/> subdivide cada lado por los mismos sitios que la pared
        /// correspondiente; null (columnas) deja cada lado de una pieza.
        /// </summary>
        /// <param name="yAt">Altura por punto. Null = plana a <paramref name="y"/>. Es lo que
        /// deja que el techo se incline sin que la tapa deje de ser la misma triangulacion: la
        /// planta en XZ no cambia, solo la altura de cada vertice.</param>
        private static void AddCap(Vector2[] poly, float y, Vector3 normal, int submesh,
            List<float>[] sideCuts = null, List<Vector2[]> pits = null,
            System.Func<Vector2, float> yAt = null, System.Func<Vector2, Vector2> uvAt = null)
        {
            float Y(Vector2 q) => yAt != null ? yAt(q) : y;
            // Una tapa PLANA se mide en XZ y ya. Una INCLINADA no: recorrerla cuesta mas que su
            // sombra en planta, asi que medir en XZ comprime la textura cuesta arriba -- un 10 %
            // a 25 grados. `uvAt` estira la coordenada a lo largo de la pendiente para que la
            // textura siga midiendo lo mismo sobre la superficie real.
            Vector2 UV(Vector2 q) => uvAt != null ? uvAt(q) : PlanarUV(q);
            // Borde de la tapa, subdividido igual que la pared que tiene debajo. Se construye
            // siempre, con o sin pozos, para que el borde sea EL MISMO en los dos caminos.
            _capRing.Clear();
            int n = poly.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = poly[i], p1 = poly[(i + 1) % n];
                List<float> cuts = sideCuts != null ? sideCuts[i] : null;
                int steps = cuts != null ? cuts.Count - 1 : 1;
                for (int s = 0; s < steps; s++)
                {
                    float ua = cuts != null ? cuts[s] : 0f;
                    _capRing.Add(Vector2.Lerp(p0, p1, ua));
                }
            }

            // Dedup por POSICIÓN y en anillo (el último contra el primero), no por parámetro.
            // Comparar fracciones no basta: dos lados casi paralelos de una planta muy
            // subdividida pueden aportar puntos que coinciden en el mundo aunque sus `u` no se
            // parezcan, y ese par produce un triángulo de área cero — aristas de longitud cero en
            // el techo de una sala aleatoria de cada cuarenta, invisibles hasta que se miden.
            for (int i = _capRing.Count - 1; i >= 0 && _capRing.Count > 3; i--)
            {
                Vector2 prev = _capRing[(i - 1 + _capRing.Count) % _capRing.Count];
                if ((_capRing[i] - prev).sqrMagnitude < 1e-8f) _capRing.RemoveAt(i);
            }

            _capHoles.Clear();
            if (pits != null)
                foreach (var pit in pits) _capHoles.Add(pit);

            // SIEMPRE por el triangulador, tenga pozos o no. Antes había un atajo de abanico
            // desde el centroide para el caso sin pozos: es más barato, pero solo vale en plantas
            // CONVEXAS — en una planta en L el abanico cruza por fuera de la sala. Un solo camino
            // también quita la posibilidad de que las dos rutas cosan distinto con las paredes.
            if (!PolygonTriangulator.Triangulate(_capRing, _capHoles, _triVerts, _triIdx))
            {
                // RUIDOSO a propósito. Este respaldo dibuja la tapa entera sin recortar, y los
                // colliders SÍ abren el pozo: el jugador vería suelo sólido y se caería por él.
                // Estuvo fallando en silencio y por eso pasó desapercibido. Si esto vuelve a
                // saltar, la incoherencia es el bug, no el respaldo.
                Debug.LogError("[RoomMeshBuilder] La triangulación de la tapa FALLÓ. Si la sala " +
                               "tiene pozos, la malla los dibuja tapados mientras la colisión los " +
                               "abre — malla y colisión NO coinciden, no uses esta sala.");
                TriangulationFailed = true;
                FanCap(Y, UV, normal, submesh);
                return;
            }

            int baseIdx = _verts.Count;
            for (int i = 0; i < _triVerts.Count; i++)
                AddVertex(new Vector3(_triVerts[i].x, Y(_triVerts[i]), _triVerts[i].y), normal, UV(_triVerts[i]));

            // El sentido se decide UNA VEZ para toda la tapa, no triángulo a triángulo.
            //
            // El triangulador devuelve todo con el mismo giro, así que la tapa entera va junta.
            // Y hace falta: los triángulos PLANOS tienen producto vectorial ~0, o sea que la
            // prueba por triángulo es una moneda al aire con ellos. En una tapa que mira hacia
            // abajo eso volteaba los triángulos reales y dejaba los planos como estaban — 12
            // aristas sin pareja, porque justamente los planos son los que sostienen el borde
            // contra la pared.
            bool flip = false;
            for (int i = 0; i + 2 < _triIdx.Count; i += 3)
            {
                Vector3 a = _verts[baseIdx + _triIdx[i]];
                Vector3 b = _verts[baseIdx + _triIdx[i + 1]];
                Vector3 c = _verts[baseIdx + _triIdx[i + 2]];
                Vector3 geo = Vector3.Cross(b - a, c - a);
                if (geo.sqrMagnitude < 1e-12f) continue;   // plano: no dice nada del sentido
                flip = Vector3.Dot(geo, normal) < 0f;
                break;
            }

            var tris = _tris[submesh];
            for (int i = 0; i + 2 < _triIdx.Count; i += 3)
            {
                int ia = baseIdx + _triIdx[i], ib = baseIdx + _triIdx[i + 1], ic = baseIdx + _triIdx[i + 2];
                tris.Add(ia);
                tris.Add(flip ? ic : ib);
                tris.Add(flip ? ib : ic);
            }
        }

        /// <summary>Respaldo: abanico desde el centroide del borde ya construido. Solo correcto en
        /// plantas convexas, y por eso NO es el camino normal — existe para que un fallo del
        /// triangulador deje algo dibujado y diagnosticable en vez de un agujero al vacío.</summary>
        private static void FanCap(System.Func<Vector2, float> Y, System.Func<Vector2, Vector2> UV,
            Vector3 normal, int submesh)
        {
            Vector2 c = Vector2.zero;
            for (int i = 0; i < _capRing.Count; i++) c += _capRing[i];
            c /= Mathf.Max(1, _capRing.Count);

            int centre = _verts.Count;
            AddVertex(new Vector3(c.x, Y(c), c.y), normal, UV(c));
            for (int i = 0; i < _capRing.Count; i++)
            {
                Vector2 a = _capRing[i], b = _capRing[(i + 1) % _capRing.Count];
                int ia = _verts.Count;
                AddVertex(new Vector3(a.x, Y(a), a.y), normal, UV(a));
                AddVertex(new Vector3(b.x, Y(b), b.y), normal, UV(b));
                Tri(submesh, centre, ia, ia + 1, normal);
            }
        }

        /// <summary>Rectángulos de los pozos, ya girados y engordados <paramref name="grow"/> por
        /// cada lado. Vacío si la sala no tiene ninguno.</summary>
        private static List<Vector2[]> PitRects(RoomDefinition def,
            System.Func<RoomDefinition.FloorHole, bool> include,
            System.Func<RoomDefinition.FloorHole, float> growFor)
        {
            var list = new List<Vector2[]>();
            if (def.floorHoles == null) return list;
            foreach (var f in def.floorHoles)
                if (IsValidPit(f) && include(f))
                {
                    float grow = growFor(f);
                    list.Add(BoxCorners(f.position, f.sizeX + grow * 2f, f.sizeZ + grow * 2f, f.yawDegrees));
                }
            return list;
        }

        /// <summary>La MISMA condición en los dos sitios que recorren <c>floorHoles</c>: aquí y en
        /// <see cref="AddPits"/>. Si se separaran, un pozo podría colarse en una lista y no en la
        /// otra, y los índices de <c>pitsIn</c>/<c>pitsOut</c> dejarían de corresponder al pozo
        /// que tocan.</summary>
        private static bool IsValidPit(RoomDefinition.FloorHole f) =>
            f != null && f.sizeX > 0.01f && f.sizeZ > 0.01f && (f.bottomless || f.depth > 0.01f);

        /// <summary>
        /// El pozo: una caja hueca colgando bajo la losa.
        ///
        /// Cuatro superficies, y las cuatro hacen falta para que cierre:
        ///  · el tubo INTERIOR (del fondo al suelo de la sala) — lo que se ve al asomarse;
        ///  · el tubo EXTERIOR (de bajo el fondo a la cara inferior de la losa) — el pozo visto
        ///    desde fuera, que existe porque cuelga al aire;
        ///  · el fondo por arriba, donde se cae de pie;
        ///  · el fondo por abajo, que se ve desde debajo de la sala.
        ///
        /// El primer intento era UN tubo de una cara partido a la altura de la losa. Los dos
        /// trozos se emparejaban entre sí y dejaban el borde de la tapa exterior sin pareja:
        /// 4 aristas abiertas por pozo. Un tubo sin grosor no es un sólido.
        ///
        /// Uno SIN FONDO es la misma caja hueca, solo que en vez del fondo lleva el canto del
        /// muro (<see cref="AddPitRim"/>): sigue cerrando, pero por la boca no hay nada. Durante
        /// un tiempo se resolvió dejándole aristas sin pareja a propósito y una lista de
        /// excepciones que los tests tenían que conocer; ya no hace falta ninguna — la malla
        /// vuelve a ser un cascarón cerrado SIEMPRE, y cualquier arista suelta es un bug otra vez.
        /// </summary>
        private static void AddPits(RoomDefinition def, float yFloor, float yBottom, float t,
            List<Vector2[]> pitsIn, List<Vector2[]> pitsOut)
        {
            if (pitsIn.Count == 0) return;
            int pIn = 0, pOut = 0;
            foreach (var f in def.floorHoles)
            {
                if (!IsValidPit(f)) continue;
                var rin = pitsIn[pIn++];

                var rout = pitsOut[pOut++];

                if (f.bottomless)
                {
                    // Tiene paredes hasta Depth metros, igual que un pozo con fondo — Depth
                    // SIGUE mandando. Y al final de esas paredes, literalmente NADA que cruce la
                    // boca: por ahí se sigue cayendo.
                    //
                    // Mismo principio que una puerta: un boquete es un agujero A TRAVÉS de un
                    // muro, y lo que lo hace sólido es que el canto va forrado por dentro (la
                    // jamba). Aquí igual — el pozo es un agujero a través de la losa y un tubo
                    // colgando debajo, con sus DOS caras (la de dentro, que se ve al asomarse, y
                    // la de fuera, que se ve desde debajo del edificio) y el canto de abajo
                    // rematado. Antes solo se emitía la cara de dentro: un tubo de una sola cara
                    // no es un sólido, y encima la losa exterior se quedaba sin recortar y
                    // cruzaba el pozo por delante.
                    float yShaft = yFloor - Mathf.Max(f.depth, t);
                    AddPitTube(rin, yShaft, yFloor, inward: true);
                    AddPitTube(rout, yShaft, yBottom, inward: false);
                    AddPitRim(rin, rout, yShaft);
                    continue;
                }

                float yPit = yFloor - f.depth;   // cara pisable del fondo
                float yUnder = yPit - t;         // cara inferior del fondo

                AddCap(rin, yPit, Vector3.up, SubmeshFloor);
                AddCap(rout, yUnder, Vector3.down, SubmeshWall);

                AddPitTube(rin, yPit, yFloor, inward: true);
                AddPitTube(rout, yUnder, yBottom, inward: false);
            }
        }

        /// <summary>
        /// El canto de ABAJO de la pared de un pozo sin fondo: el anillo entre el tubo interior y
        /// el exterior, donde el muro se acaba.
        ///
        /// NO es una tapa: no cruza la boca del pozo, solo remata el grosor del muro — el mismo
        /// borde que tiene por abajo cualquier pared. La boca (el rectángulo interior) sigue
        /// enteramente abierta, que es lo que se pidió: "en el último metro se abre el fondo".
        /// Sin este anillo los dos tubos terminarían en dos bordes sueltos.
        /// </summary>
        private static void AddPitRim(Vector2[] rin, Vector2[] rout, float y)
        {
            for (int i = 0; i < 4; i++)
                Quad(SubmeshWall, V(rin[i], y), V(rin[(i + 1) % 4], y),
                    V(rout[(i + 1) % 4], y), V(rout[i], y), Vector3.down);
        }

        private static void AddPitTube(Vector2[] rect, float y0, float y1, bool inward)
        {
            if (y1 - y0 < 1e-4f) return;
            Vector2 c = (rect[0] + rect[1] + rect[2] + rect[3]) * 0.25f;

            for (int i = 0; i < 4; i++)
            {
                Vector2 a = rect[i], b = rect[(i + 1) % 4];
                Vector3 e = new Vector3(b.x - a.x, 0f, b.y - a.y);
                var nrm = new Vector3(e.z, 0f, -e.x).normalized;
                Vector2 mid = (a + b) * 0.5f;
                // Fuera del pozo por defecto; `inward` la gira hacia el hueco.
                if (Vector2.Dot(new Vector2(nrm.x, nrm.z), mid - c) < 0f) nrm = -nrm;
                if (inward) nrm = -nrm;

                Quad(SubmeshWall, V(a, y0), V(b, y0), V(b, y1), V(a, y1), nrm);
            }
        }

        /// <summary>
        /// Una entreplanta: dos tapas (suelo pisable arriba, techo visto desde abajo debajo) del
        /// ancho de TODA la sala, con hueco donde una escalera la atraviese, y un remate en el
        /// borde exterior que la sella contra la pared — la misma losa que trae el suelo de la
        /// sala, solo que a media altura.
        /// </summary>
        private static void AddLevelSlab(RoomDefinition def, Vector2[] contour,
            RoomDefinition.Level lvl, float t, float minCeil)
        {
            float top = def.ClampLevelHeight(lvl.height, minCeil);
            float bottom = top - t;

            _levelHoles.Clear();
            foreach (var s in def.StairsReaching(top, minCeil))
                _levelHoles.Add(BoxCorners(s.FootprintCentre(), s.width, s.FootprintLength(), s.yawDegrees));

            AddCap(contour, bottom, Vector3.down, SubmeshCeiling, null, _levelHoles);
            AddCap(contour, top, Vector3.up, SubmeshFloor, null, _levelHoles);

            // El agujero de cada escalera: el mismo tubo recto que un pozo sin inglete, porque
            // aquí tampoco hay nada por debajo contra lo que encajarlo — las dos tapas de la
            // losa usan EL MISMO rectángulo.
            foreach (var hole in _levelHoles)
                AddPitTube(hole, bottom, top, inward: true);

            AddSlabRim(contour, bottom, top);
        }

        /// <summary>
        /// El canto exterior de una losa que ocupa TODA la planta de la sala: una tira vertical
        /// por cada lado del contorno, sellando la losa contra la pared. Normal del sentido de
        /// giro del contorno y no del centro — en una planta en L el centro puede caer fuera y
        /// esa tira saldría del revés.
        /// </summary>
        private static void AddSlabRim(Vector2[] poly, float y0, float y1)
        {
            int n = poly.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % n];
                Vector2 n2 = RoomDefinition.OutwardNormal(a, b);
                var nrm = new Vector3(n2.x, 0f, n2.y);
                Quad(SubmeshWall, V(a, y0), V(b, y0), V(b, y1), V(a, y1), nrm);
            }
        }

        private static readonly List<Vector2[]> _levelHoles = new List<Vector2[]>();

        private static readonly List<Vector2> _capRing = new List<Vector2>();
        private static readonly List<IList<Vector2>> _capHoles = new List<IList<Vector2>>();
        private static readonly List<Vector2> _triVerts = new List<Vector2>();
        private static readonly List<int> _triIdx = new List<int>();

        /// <summary>
        /// Todas las paredes: por cada lado, su cara interior, su cara exterior y una jamba por
        /// boquete cosiendo las dos a través del grosor.
        /// </summary>
        private static void AddWalls(Vector2[] inner, Vector2[] outer, List<HoleRect>[] sideHoles,
            List<float>[] sideCuts, List<float>[] sideCutsOut,
            List<float> innerYCuts, List<float> outerYCuts, float thickness,
            System.Func<Vector2, float> ceilAt, System.Func<Vector2, float> topAt, bool trim)
        {
            int n = inner.Length;
            for (int i = 0; i < n; i++)
            {
                Vector2 i0 = inner[i], i1 = inner[(i + 1) % n];
                Vector2 o0 = outer[i], o1 = outer[(i + 1) % n];
                var holes = sideHoles[i];
                var uCuts = sideCuts[i];

                var uCutsOut = sideCutsOut[i];

                _holesOut.Clear();
                for (int k = 0; k < holes.Count; k++)
                    _holesOut.Add(new HoleRect
                    {
                        u0 = MapUEnds(holes[k].u0, i0, i1, o0, o1),
                        u1 = MapUEnds(holes[k].u1, i0, i1, o0, o1),
                        y0 = holes[k].y0,
                        y1 = holes[k].y1,
                        openStart = holes[k].openStart,
                        openEnd = holes[k].openEnd,
                    });

                AddPanel(i0, i1, uCuts, innerYCuts, inward: true, holes, ceilAt);
                AddPanel(o0, o1, uCutsOut, outerYCuts, inward: false, _holesOut, topAt);

                for (int k = 0; k < holes.Count; k++)
                {
                    AddJamb(i0, i1, o0, o1, holes[k], uCuts, innerYCuts);
                    AddGrate(i0, i1, o0, o1, holes[k], holes[k].bars, thickness);
                }

                if (trim)
                {
                    AddBaseboard(i0, i1, holes);
                    for (int k = 0; k < holes.Count; k++)
                        AddHoleCasing(i0, i1, holes[k]);
                }
            }
        }

        /// <summary>Niveles de corte dentro de [lo, hi]: los extremos más los valores de
        /// <paramref name="raw"/> que caigan estrictamente dentro, ordenados y sin repetidos.</summary>
        private static List<float> LevelCuts(List<float> raw, float lo, float hi)
        {
            var cuts = new List<float> { lo, hi };
            for (int i = 0; i < raw.Count; i++)
                if (raw[i] > lo && raw[i] < hi) cuts.Add(raw[i]);
            cuts.Sort();
            // Tolerancia FISICA (1 mm), no epsilon numerico. Con varios boquetes, dos niveles de
            // corte pueden caer a decimas de milimetro y generan una franja de pared invisible de
            // ese grosor: vertices que nadie ve y geometria degenerada. Una sala con 7 huecos
            // producia una franja de 0,4 mm.
            for (int i = cuts.Count - 1; i > 0; i--)
                if (cuts[i] - cuts[i - 1] < CutTolerance) cuts.RemoveAt(i);
            return cuts;
        }

        /// <summary>Dintel minimo: ningun boquete puede llegar al techo. La fila de arriba de la
        /// pared es la que sigue a la pendiente, y un hueco dentro de ella se estiraria con ella.
        /// Internal: <see cref="RoomColliderBuilder"/> tiene que recortar los huecos contra el
        /// MISMO techo, o el hueco de colision llega mas arriba que el de la malla en una sala
        /// con techo inclinado -- pared solida que se atraviesa, o al reves.</summary>
        internal const float MinLintel = 0.05f;

        /// <summary>Dos cortes mas juntos que esto son el mismo corte: 1 mm, que es lo mas fino
        /// que puede llegar a significar algo en una sala de metros. Internal por el mismo motivo
        /// que <see cref="MinLintel"/>: <see cref="RoomColliderBuilder"/> reimplementa su propia
        /// lista de cortes sobre los mismos <c>HoleRect</c> y necesita la MISMA tolerancia para
        /// fundir cortes casi-coincidentes, o deja una caja de colision en forma de loncha.</summary>
        internal const float CutTolerance = 0.001f;

        /// <summary>Igual que <see cref="CutTolerance"/> pero en fraccion de pared (eje U), no en
        /// metros: 1e-4 ronda el milimetro en una pared de unos metros. Nombrada para no repetir
        /// el mismo literal en <see cref="UCuts"/> y en el corte horizontal de <see cref="AddJamb"/>
        /// con valores que puedan divergir sin querer.</summary>
        internal const float UCutTolerance = 1e-4f;

        /// <summary>Sublista de <paramref name="cuts"/> recortada a [lo, hi], con los extremos
        /// incluidos — lo que hace que un trozo de jamba corte por los mismos sitios que la
        /// celda de pared con la que comparte arista.</summary>
        /// <param name="eps">Cuanto tiene que separarse un corte de <paramref name="lo"/>/<paramref name="hi"/>
        /// para contar como propio. Con dos huecos en la sala, <paramref name="lo"/>/<paramref name="hi"/>
        /// llegan con la precision NATIVA del hueco, pero <paramref name="cuts"/> ya viene fusionado
        /// por <see cref="LevelCuts"/> a <see cref="CutTolerance"/> -- un vecino a decimas de milimetro
        /// deja un resto de ese mismo tamano que un epsilon mas fino que esa fusion no filtra, y sale
        /// una loncha de jamba de espesor invisible (mismo bug que <see cref="LevelCuts"/> ya resuelve
        /// para la lista compartida, pero sin heredarlo aqui).</param>
        private static void SubCuts(List<float> cuts, float lo, float hi, List<float> into, float eps = 1e-5f)
        {
            into.Clear();
            into.Add(lo);
            for (int i = 0; i < cuts.Count; i++)
                if (cuts[i] > lo + eps && cuts[i] < hi - eps) into.Add(cuts[i]);
            into.Add(hi);
        }

        /// <summary>Cortes horizontales de un lado: los dos extremos más los bordes de cada
        /// boquete, ordenados y sin repetidos.</summary>
        private static List<float> UCuts(List<HoleRect> holes)
        {
            var cuts = new List<float> { 0f, 1f };
            for (int i = 0; i < holes.Count; i++)
            {
                if (holes[i].u0 > 0f && holes[i].u0 < 1f) cuts.Add(holes[i].u0);
                if (holes[i].u1 > 0f && holes[i].u1 < 1f) cuts.Add(holes[i].u1);
            }
            cuts.Sort();
            // En fraccion de pared: la misma idea que CutTolerance, pero u va de 0 a 1 sobre un
            // lado que suele medir unos metros, asi que 1e-4 ronda el milimetro.
            for (int i = cuts.Count - 1; i > 0; i--)
                if (cuts[i] - cuts[i - 1] < UCutTolerance) cuts.RemoveAt(i);
            return cuts;
        }


        /// <summary>
        /// Una cara de pared con sus huecos. En vez de partir el rectángulo en los 4 trozos que
        /// rodean UN hueco —que solo funciona con uno—, se cortan todas las coordenadas de todos
        /// los huecos en los dos ejes y se emite la rejilla resultante saltando las celdas que
        /// caen dentro de alguno. Así N huecos por pared salen bien, y dos que se solapen
        /// tampoco rompen nada: la celda está dentro de alguno y no se emite, y punto.
        /// </summary>
        /// <param name="topAt">Altura del REMATE de la pared por punto. Null = plano al último
        /// corte. Con techo inclinado la fila de arriba deja de ser un rectángulo y pasa a ser un
        /// trapecio: sus dos esquinas superiores están a alturas distintas. Si el remate no
        /// siguiera exactamente la misma función que la tapa del techo, quedaría una rendija
        /// entre pared y techo por toda la sala.</param>
        private static void AddPanel(Vector2 p0, Vector2 p1, List<float> uCuts, List<float> vCuts,
            bool inward, List<HoleRect> holes, System.Func<Vector2, float> topAt = null)
        {
            // Del sentido de giro del contorno, no de su centro: en una planta en L el centro
            // puede caer fuera y esa pared se giraría del revés.
            Vector2 n2 = RoomDefinition.OutwardNormal(p0, p1);
            var nrm = new Vector3(n2.x, 0f, n2.y);
            if (inward) nrm = -nrm;

            float len = Vector2.Distance(p0, p1);
            int topRow = vCuts.Count - 2;
            for (int ui = 0; ui < uCuts.Count - 1; ui++)
            {
                float ua = uCuts[ui], ub = uCuts[ui + 1];
                if (ub - ua < 1e-5f) continue;

                Vector2 a = Vector2.Lerp(p0, p1, ua);
                Vector2 b = Vector2.Lerp(p0, p1, ub);

                for (int vi = 0; vi < vCuts.Count - 1; vi++)
                {
                    float va = vCuts[vi], vb = vCuts[vi + 1];
                    if (vb - va < 1e-5f) continue;
                    if (HoleRect.InsideAny(holes, (ua + ub) * 0.5f, (va + vb) * 0.5f)) continue;

                    // Solo la fila de arriba sigue al techo; las de abajo son horizontales.
                    float vbA = vb, vbB = vb;
                    if (topAt != null && vi == topRow) { vbA = topAt(a); vbB = topAt(b); }
                    if (vbA - va < 1e-5f && vbB - va < 1e-5f) continue;

                    Quad(SubmeshWall,
                        new Vector3(a.x, va, a.y), new Vector3(b.x, va, b.y),
                        new Vector3(b.x, vbB, b.y), new Vector3(a.x, vbA, a.y),
                        nrm,
                        WallUV(ua * len, va), WallUV(ub * len, va),
                        WallUV(ub * len, vbB), WallUV(ua * len, vbA));
                }
            }
        }

        /// <summary>
        /// Jamba: las cuatro caras que forran el grosor de la pared alrededor de un hueco. Sin
        /// ellas se vería el interior hueco del muro por el canto del boquete.
        /// </summary>
        private static void AddJamb(Vector2 i0, Vector2 i1, Vector2 o0, Vector2 o1, HoleRect hr,
            List<float> uCuts, List<float> yCuts)
        {
            Vector3 along = new Vector3(i1.x - i0.x, 0f, i1.y - i0.y).normalized;

            // Alféizar y dintel, partidos por los cortes horizontales que caen dentro del hueco:
            // su arista interior la comparten con la celda de pared (o con la tapa de suelo, si
            // el hueco llega al suelo), y esa está partida por los mismos sitios.
            SubCuts(uCuts, hr.u0, hr.u1, _jambCuts, UCutTolerance);
            for (int k = 0; k < _jambCuts.Count - 1; k++)
            {
                float ua = _jambCuts[k], ub = _jambCuts[k + 1];
                if (ub - ua < 1e-5f) continue;
                Vector2 ia = Vector2.Lerp(i0, i1, ua), ib = Vector2.Lerp(i0, i1, ub);
                Vector2 oa = Vector2.Lerp(o0, o1, MapUEnds(ua, i0, i1, o0, o1));
                Vector2 ob = Vector2.Lerp(o0, o1, MapUEnds(ub, i0, i1, o0, o1));

                Quad(SubmeshWall, V(ia, hr.y0), V(ib, hr.y0), V(ob, hr.y0), V(oa, hr.y0), Vector3.up);
                Quad(SubmeshWall, V(ia, hr.y1), V(ib, hr.y1), V(ob, hr.y1), V(oa, hr.y1), Vector3.down);
            }

            // Los dos costados, partidos por los cortes de altura por el mismo motivo.
            SubCuts(yCuts, hr.y0, hr.y1, _jambCuts, CutTolerance);
            Vector2 sa = Vector2.Lerp(i0, i1, hr.u0), sb = Vector2.Lerp(i0, i1, hr.u1);
            Vector2 ta = Vector2.Lerp(o0, o1, MapUEnds(hr.u0, i0, i1, o0, o1));
            Vector2 tb = Vector2.Lerp(o0, o1, MapUEnds(hr.u1, i0, i1, o0, o1));
            for (int k = 0; k < _jambCuts.Count - 1; k++)
            {
                float va = _jambCuts[k], vb = _jambCuts[k + 1];
                if (vb - va < 1e-5f) continue;

                // Un costado que cae en una esquina por la que la abertura SIGUE no se emite:
                // ahí no hay canto que forrar, hay hueco. El que lo emitiera estaría cerrando la
                // puerta por la mitad con un tabique del grosor del muro. Los dos alféizares se
                // encuentran solos en la arista del inglete, que es la misma para las dos paredes.
                if (!hr.openStart) Quad(SubmeshWall, V(sa, va), V(sa, vb), V(ta, vb), V(ta, va), along);
                if (!hr.openEnd) Quad(SubmeshWall, V(sb, va), V(sb, vb), V(tb, vb), V(tb, va), -along);
            }
        }

        /// <summary>Columna regular de N lados.</summary>
        private static void AddPrism(Vector2 centre, float radius, int sides, float y0, float y1,
            float yawDegrees = 0f)
        {
            float off = yawDegrees * Mathf.Deg2Rad;
            var poly = new Vector2[sides];
            for (int i = 0; i < sides; i++)
            {
                float th = Mathf.PI / sides + i * 2f * Mathf.PI / sides - off;
                poly[i] = centre + new Vector2(Mathf.Cos(th), Mathf.Sin(th)) * radius;
            }
            AddClosedPrism(poly, y0, y1);
        }

        /// <summary>
        /// Prisma CERRADO por sí solo: tapa inferior, banda lateral y tapa superior. Columnas,
        /// bloques y peldaños salen todos de aquí, y por eso ninguno puede romper la estanqueidad
        /// del resto — cada uno es su propia superficie cerrada dentro de la cavidad.
        /// </summary>
        private static void AddClosedPrism(Vector2[] poly, float y0, float y1)
        {
            int n = poly.Length;
            Vector2 centre = Vector2.zero;
            for (int i = 0; i < n; i++) centre += poly[i];
            centre /= n;

            AddCap(poly, y0, Vector3.down, SubmeshWall);
            AddCap(poly, y1, Vector3.up, SubmeshWall);

            for (int i = 0; i < n; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % n];
                Vector3 e = new Vector3(b.x - a.x, 0f, b.y - a.y);
                var nrm = new Vector3(e.z, 0f, -e.x).normalized;
                Vector2 mid = (a + b) * 0.5f;
                if (Vector2.Dot(new Vector2(nrm.x, nrm.z), mid - centre) < 0f) nrm = -nrm;

                Quad(SubmeshWall, V(a, y0), V(b, y0), V(b, y1), V(a, y1), nrm);
            }
        }

        private static readonly List<HoleRect> _blockHolesA = new List<HoleRect>();
        private static readonly List<HoleRect> _blockHolesB = new List<HoleRect>();
        private static readonly List<HoleRect> _blockHolesC = new List<HoleRect>();
        private static readonly List<HoleRect> _blockHolesD = new List<HoleRect>();
        private static readonly List<float>[] _blockSideCuts = new List<float>[4];

        /// <summary>
        /// Un bloque con boquetes: en vez del prisma macizo de siempre, un cascarón hueco con
        /// un túnel por cada boquete. Los boquetes de lado 0/2 comparten el MISMO túnel (a
        /// través de Z) y los de 1/3 el otro (a través de X) — un bloque solo tiene dos caras
        /// enfrentadas por eje, así que un boquete en cualquiera de las dos perfora las DOS.
        ///
        /// Sin inglete: a diferencia de un <see cref="RoomDefinition.WallHole"/>, las dos caras
        /// de un bloque miden exactamente lo mismo (un bloque no tiene "grosor de muro" que
        /// ensanche la cara exterior), así que el hueco se mapea igual en las dos, solo
        /// ESPEJADO en la cara opuesta — recorrerla de p0 a p1 va en el sentido contrario.
        /// </summary>
        private static void AddBlockWithHoles(RoomDefinition.Block b, float baseOffset)
        {
            var corners = BoxCorners(b.position, b.sizeX, b.sizeZ, b.yawDegrees);
            float y0 = baseOffset + b.baseY, y1 = y0 + b.height;

            // Resolución COMPARTIDA con los colliders — la misma que evita que una puerta se
            // vea en un sitio y bloquee en otro. Llega sin espejar (en la "u" de la cara par de
            // cada eje); espejar para la cara impar es cosa de la malla, no de la resolución.
            b.ResolveHoles(_blockHolesA, _blockHolesC, baseOffset);
            _blockHolesB.Clear();
            foreach (var h in _blockHolesA)
                _blockHolesB.Add(new HoleRect { u0 = 1f - h.u1, u1 = 1f - h.u0, y0 = h.y0, y1 = h.y1 });
            _blockHolesD.Clear();
            foreach (var h in _blockHolesC)
                _blockHolesD.Add(new HoleRect { u0 = 1f - h.u1, u1 = 1f - h.u0, y0 = h.y0, y1 = h.y1 });

            // Cortes de ALTURA compartidos por las CUATRO caras, no solo por la del eje con
            // hueco — el mismo motivo que en la sala: la arista vertical de una esquina la
            // comparten dos caras, y si una se parte por su hueco y la otra no, la esquina
            // queda con una T-junction (28 aristas abiertas por bloque, todas justo en las
            // esquinas — así se detectó).
            _blockYCutsRaw.Clear();
            foreach (var h in _blockHolesA) { _blockYCutsRaw.Add(h.y0); _blockYCutsRaw.Add(h.y1); }
            foreach (var h in _blockHolesC) { _blockYCutsRaw.Add(h.y0); _blockYCutsRaw.Add(h.y1); }
            var yCuts = LevelCuts(_blockYCutsRaw, y0, y1);

            AddBlockFace(corners[0], corners[1], yCuts, _blockHolesA);
            AddBlockFace(corners[2], corners[3], yCuts, _blockHolesB);
            AddBlockFace(corners[1], corners[2], yCuts, _blockHolesC);
            AddBlockFace(corners[3], corners[0], yCuts, _blockHolesD);

            // Cortes en U compartidos entre cada cara y la tapa: si la cara de un lado tiene
            // jamba+hueco+jamba y la tapa solo lleva su esquina de siempre, el borde compartido
            // entre las dos deja de casar (T-junction otra vez, esta vez cara↔tapa).
            //
            // ORDEN A, C, B, D — NO es una errata pese a que las AddBlockFace de arriba van en
            // A, B, C, D. AddCap indexa por ARISTA del polígono (`sideCuts[i]` es el borde
            // corners[i]→corners[i+1]), mientras que las cuatro listas de boquetes están
            // nombradas por PAR de caras enfrentadas: A/B son las dos del eje Z y C/D las del
            // eje X. Recorriendo el contorno, las aristas salen A (0→1), C (1→2), B (2→3),
            // D (3→0). "Arreglarlo" a A, B, C, D le da a la tapa los cortes de la cara de
            // enfrente y el borde vuelve a no casar.
            _blockSideCuts[0] = UCuts(_blockHolesA);
            _blockSideCuts[1] = UCuts(_blockHolesC);
            _blockSideCuts[2] = UCuts(_blockHolesB);
            _blockSideCuts[3] = UCuts(_blockHolesD);

            float r = b.yawDegrees * Mathf.Deg2Rad;
            var ax = new Vector2(Mathf.Cos(r), -Mathf.Sin(r));
            var az = new Vector2(Mathf.Sin(r), Mathf.Cos(r));
            float hx = b.sizeX * 0.5f, hz = b.sizeZ * 0.5f;

            // Las tapas siempre son macizas, sin recorte — la jamba mínima que reserva
            // ResolveHoles bajo el dintel garantiza que ningún túnel llega a la de ARRIBA. La de
            // ABAJO sí puede coincidir con un boquete que llegue al propio suelo del bloque (una
            // puerta, no una ventana): ahí no hace falta recortarla porque no hay nada que
            // recortar EN LA TAPA — el suelo de la sala ya está justo debajo, al mismo nivel, y
            // es él quien se pisa. Ver <see cref="BlockDoorFloorOpenings"/> para el hueco que
            // eso deja adrede en el propio umbral.
            AddCap(corners, y0, Vector3.down, SubmeshWall, _blockSideCuts);
            AddCap(corners, y1, Vector3.up, SubmeshWall, _blockSideCuts);

            foreach (var hole in _blockHolesA)
                AddBlockTunnel(b.position, ax, az, hx, hz, y0, hole, throughZ: true);
            foreach (var hole in _blockHolesC)
                AddBlockTunnel(b.position, ax, az, hx, hz, y0, hole, throughZ: false);
        }

        /// <summary>Una cara plana del bloque con sus huecos ya resueltos: exactamente lo mismo
        /// que la pared de una sala, así que reusa <see cref="AddPanel"/> en vez de reinventar
        /// la rejilla de cortes. <paramref name="yCuts"/> viene COMPARTIDO por las cuatro caras
        /// — ver el porqué en <see cref="AddBlockWithHoles"/>.</summary>
        private static void AddBlockFace(Vector2 p0, Vector2 p1, List<float> yCuts, List<HoleRect> holes)
        {
            var uCuts = UCuts(holes);
            AddPanel(p0, p1, uCuts, yCuts, inward: false, holes);
        }

        private static readonly List<float> _blockYCutsRaw = new List<float>();

        /// <summary>
        /// El túnel de un boquete de bloque: dos jambas laterales, un dintel siempre y un
        /// umbral solo si el hueco NO llega al propio suelo del bloque — igual que un boquete
        /// de pared no lleva umbral cuando es una puerta a ras de suelo. Cuando sí llega, el
        /// umbral no hace ninguna falta: el suelo de la sala ya está ahí, al mismo nivel, y es
        /// él quien se pisa — meter una losa propia solo la duplicaría (y con normales opuestas
        /// a la tapa del bloque, que da geometría sin pareja: la primera versión de esto
        /// intentaba cerrarlo con una losa arriba Y un recorte en la tapa abajo, dos piezas para
        /// la misma costura, y "0 dup" se volvía "2 dup" en vez de cerrar nada).
        /// </summary>
        private static void AddBlockTunnel(Vector2 centre, Vector2 ax, Vector2 az, float hx, float hz,
            float blockY0, HoleRect hole, bool throughZ)
        {
            float half = throughZ ? hx : hz;
            float thru = throughZ ? hz : hx;
            Vector2 uDir = throughZ ? ax : az;
            Vector2 vDir = throughZ ? az : ax;

            float uA = -half + hole.u0 * (2f * half);
            float uB = -half + hole.u1 * (2f * half);

            Vector2 P(float u, float v) => centre + uDir * u + vDir * v;

            var nA = new Vector3(uDir.x, 0f, uDir.y);
            Quad(SubmeshWall, V(P(uA, -thru), hole.y0), V(P(uA, thru), hole.y0),
                V(P(uA, thru), hole.y1), V(P(uA, -thru), hole.y1), nA);
            Quad(SubmeshWall, V(P(uB, thru), hole.y0), V(P(uB, -thru), hole.y0),
                V(P(uB, -thru), hole.y1), V(P(uB, thru), hole.y1), -nA);

            Quad(SubmeshWall, V(P(uA, -thru), hole.y1), V(P(uB, -thru), hole.y1),
                V(P(uB, thru), hole.y1), V(P(uA, thru), hole.y1), Vector3.down);

            if (hole.y0 > blockY0 + 1e-4f)
                Quad(SubmeshWall, V(P(uA, -thru), hole.y0), V(P(uA, thru), hole.y0),
                    V(P(uB, thru), hole.y0), V(P(uB, -thru), hole.y0), Vector3.up);
        }

        /// <summary>
        /// El rectángulo y la altura de cada boquete de bloque que llega al propio suelo del
        /// bloque: la ÚNICA abertura que esta malla deja sin cerrar a propósito. Ahí no hace
        /// falta nada porque el suelo de la sala ya está justo debajo, tocándola.
        ///
        /// Los tests la usan para no confundir esta abertura pedida con una fuga de verdad en
        /// cualquier otro sitio. Un pozo sin fondo tuvo su propia excepción durante un tiempo y
        /// ya no: se cierra solo con el canto del muro (<see cref="AddPitRim"/>), que es lo que
        /// debería haber hecho desde el principio.
        /// </summary>
        public static IEnumerable<(Vector2[] rect, float y)> BlockDoorFloorOpenings(RoomDefinition def)
        {
            if (def.blocks == null) yield break;
            float minCeil = def.MinCeilingOver(def.InnerContour());
            foreach (var b in def.blocks)
            {
                if (b == null || b.holes == null || b.holes.Length == 0) continue;
                if (b.sizeX <= 0.001f || b.sizeZ <= 0.001f || b.height <= 0.001f) continue;

                float r = b.yawDegrees * Mathf.Deg2Rad;
                var ax = new Vector2(Mathf.Cos(r), -Mathf.Sin(r));
                var az = new Vector2(Mathf.Sin(r), Mathf.Cos(r));
                float hx = b.sizeX * 0.5f, hz = b.sizeZ * 0.5f;

                // El MISMO offset de piso que usa la malla del bloque: el borde abierto está
                // donde el bloque apoya de verdad, no siempre en el suelo de la sala.
                float off = def.StoreyBaseY(b.level, minCeil);
                float floorY = off + b.baseY;

                var zHoles = new List<HoleRect>();
                var xHoles = new List<HoleRect>();
                b.ResolveHoles(zHoles, xHoles, off);

                foreach (var h in zHoles)
                    if (h.y0 <= floorY + 1e-4f)
                        yield return (BlockHoleFootprint(b.position, ax, az, hx, hz, h, throughZ: true), floorY);
                foreach (var h in xHoles)
                    if (h.y0 <= floorY + 1e-4f)
                        yield return (BlockHoleFootprint(b.position, ax, az, hx, hz, h, throughZ: false), floorY);
            }
        }

        /// <summary>El rectángulo que un boquete de bloque ocupa en planta, del ancho de TODO el
        /// espesor del bloque en la dirección perpendicular al túnel.</summary>
        private static Vector2[] BlockHoleFootprint(Vector2 centre, Vector2 ax, Vector2 az,
            float hx, float hz, HoleRect hole, bool throughZ)
        {
            float half = throughZ ? hx : hz;
            float thru = throughZ ? hz : hx;
            Vector2 uDir = throughZ ? ax : az;
            Vector2 vDir = throughZ ? az : ax;
            float uA = -half + hole.u0 * (2f * half);
            float uB = -half + hole.u1 * (2f * half);
            return new[]
            {
                centre + uDir * uA - vDir * thru,
                centre + uDir * uB - vDir * thru,
                centre + uDir * uB + vDir * thru,
                centre + uDir * uA + vDir * thru,
            };
        }

        /// <summary>
        /// Las cuatro esquinas de una caja girada. El eje X local es (cos, −sin) en (x, z), el
        /// MISMO convenio de yaw que usa <c>RoomColliderBuilder</c>: si se separaran, la caja de
        /// colisión saldría girada al revés que la que se ve.
        /// </summary>
        public static Vector2[] BoxCorners(Vector2 centre, float sizeX, float sizeZ, float yawDegrees)
        {
            float r = yawDegrees * Mathf.Deg2Rad;
            var ax = new Vector2(Mathf.Cos(r), -Mathf.Sin(r));
            var az = new Vector2(Mathf.Sin(r), Mathf.Cos(r));
            float hx = sizeX * 0.5f, hz = sizeZ * 0.5f;
            return new[]
            {
                centre - ax * hx - az * hz,
                centre + ax * hx - az * hz,
                centre + ax * hx + az * hz,
                centre - ax * hx + az * hz,
            };
        }

        /// <summary>
        /// Escalera: cada peldaño es una caja MACIZA desde el suelo hasta su huella, no una losa
        /// flotante. Así se sube andando sin lógica ninguna, y la colisión sale exactamente de la
        /// misma forma que se ve.
        /// </summary>
        private static void AddStairs(RoomDefinition.Stairs s, float baseY)
        {
            if (s == null || !s.IsValid()) return;

            var forward = s.Forward();
            for (int i = 0; i < s.steps; i++)
            {
                Vector2 c = s.position + forward * (s.run * (i + 0.5f));
                // Cada peldaño macizo desde el suelo de SU piso, no siempre el de la sala.
                AddClosedPrism(BoxCorners(c, s.width, s.run, s.yawDegrees),
                    baseY, baseY + s.rise * (i + 1));
            }
        }

        /// <summary>
        /// Barrotes de una rejilla: llenan un boquete ya abierto con montantes verticales a media
        /// pared. Reutiliza el hueco en vez de ser un tipo de feature aparte — la abertura ya
        /// está hecha, aquí solo se rellena.
        /// </summary>
        private static void AddGrate(Vector2 i0, Vector2 i1, Vector2 o0, Vector2 o1,
            HoleRect hr, int bars, float thickness)
        {
            if (bars < 1) return;
            const float BarWidth = 0.06f;

            for (int k = 0; k < bars; k++)
            {
                float u = Mathf.Lerp(hr.u0, hr.u1, (k + 0.5f) / bars);
                Vector2 a = Vector2.Lerp(i0, i1, u);
                Vector2 b = Vector2.Lerp(o0, o1, MapU(u, i0, i1, o0, o1));
                Vector2 centre = (a + b) * 0.5f;

                Vector2 dir = (i1 - i0).normalized;
                float yaw = Mathf.Atan2(-dir.y, dir.x) * Mathf.Rad2Deg;
                AddClosedPrism(BoxCorners(centre, BarWidth, thickness, yaw), hr.y0, hr.y1);
            }
        }

        /// <summary>Alto del rodapié. Público porque los tests lo necesitan para sondear una
        /// altura exacta sin duplicar el número a mano.</summary>
        public const float TrimHeight = 0.12f;
        private const float TrimDepth = 0.02f;
        private const float TrimWidth = 0.05f;

        /// <summary>
        /// Separación mínima para que una tira de moldura no coincida arista por arista con
        /// algo que YA está ahí — el remate suelo-pared de la sala, o el forro de una jamba de
        /// pared (<see cref="AddJamb"/>). Los dos son cierres de verdad, no huecos: una tapa de
        /// más justo encima duplica esa arista en vez de cerrar nada. 5 mm es invisible.
        /// </summary>
        private const float TrimSeamGap = 0.005f;

        /// <summary>Cuánto para el rodapié antes de la esquina de su propia pared — ver el
        /// porqué en <see cref="AddBaseboard"/>.</summary>
        private const float TrimCornerGap = 0.03f;

        private static readonly List<float> _baseboardCuts = new List<float>();

        /// <summary>
        /// Rodapié: una tira maciza al pie de la pared, hacia dentro de la sala. Los boquetes
        /// que llegan al suelo (puertas) lo interrumpen; los que no (ventanas) lo dejan pasar
        /// por debajo sin cortarlo — un rodapié no se para para una ventana.
        ///
        /// Reusa <see cref="AddClosedPrism"/> para el volumen: es EL MISMO problema que un
        /// bloque (una caja cerrada, sola, sin compartir aristas con nadie), así que no hace
        /// falta reinventar el cierre.
        /// </summary>
        private static void AddBaseboard(Vector2 p0, Vector2 p1, List<HoleRect> holes)
        {
            float len = Vector2.Distance(p0, p1);
            if (len < 1e-4f) return;

            // Cada pared para su rodapié un poco ANTES de su propia esquina: la pieza de esta
            // pared y la de la vecina, sin este margen, comparten el mismo vértice de esquina
            // en su tapa del extremo -- dos cajas independientes con una arista IDÉNTICA ahí,
            // que es duplicarla, no cerrarla (visto primero como 8 aristas duplicadas en una
            // caja lisa de 4 esquinas, ninguna abierta). Con el margen cada pieza es su propia
            // caja cerrada de verdad, sin vértice que le pueda coincidir a nadie más.
            float cornerMargin = Mathf.Clamp(TrimCornerGap / len, 0.001f, 0.45f);

            _baseboardCuts.Clear();
            _baseboardCuts.Add(cornerMargin);
            _baseboardCuts.Add(1f - cornerMargin);
            foreach (var h in holes)
                if (h.y0 <= 1e-4f)
                {
                    _baseboardCuts.Add(Mathf.Clamp(h.u0, cornerMargin, 1f - cornerMargin));
                    _baseboardCuts.Add(Mathf.Clamp(h.u1, cornerMargin, 1f - cornerMargin));
                }
            _baseboardCuts.Sort();
            for (int i = _baseboardCuts.Count - 1; i > 0; i--)
                if (_baseboardCuts[i] - _baseboardCuts[i - 1] < 1e-4f) _baseboardCuts.RemoveAt(i);

            for (int k = 0; k < _baseboardCuts.Count - 1; k++)
            {
                float ua = _baseboardCuts[k], ub = _baseboardCuts[k + 1];
                if (ub - ua < 1e-4f) continue;

                float mid = (ua + ub) * 0.5f;
                bool blocked = false;
                foreach (var h in holes)
                    if (h.y0 <= 1e-4f && mid > h.u0 && mid < h.u1) { blocked = true; break; }
                if (blocked) continue;

                Vector2 a = Vector2.Lerp(p0, p1, ua), b = Vector2.Lerp(p0, p1, ub);
                // Empieza un pelo por encima del suelo, no exactamente en él: a Y=0 el borde
                // exterior de esta caja (de `a` a `b`) cae en las MISMAS dos esquinas que ya usa
                // el remate suelo-pared de la sala -- mismo segmento, mismos extremos -- y una
                // segunda tapa ahí duplica esa arista en vez de cerrar nada (30 aristas
                // duplicadas en una caja lisa, ninguna abierta: el triángulo de más, no de
                // menos). Con el hueco no se ve: es más fino que el rodapié se separa del propio
                // rodapié real.
                AddClosedPrism(TrimRect(a, b, TrimDepth), TrimSeamGap, TrimHeight);
            }
        }

        /// <summary>
        /// Moldura alrededor de un boquete: dos jambas y un dintel, más un umbral si no llega al
        /// suelo (una puerta no lleva umbral — es donde se anda). Cuatro cajas sueltas, no una
        /// pieza en L: es como se ve una moldura de verdad, a tope o a inglete pero nunca de una
        /// sola pieza continua.
        /// </summary>
        private static void AddHoleCasing(Vector2 p0, Vector2 p1, HoleRect hr)
        {
            Vector2 along = (p1 - p0).normalized;
            // Un pelo separada del propio borde del hueco: justo ahí ya hay una jamba (el forro
            // del grosor del muro, AddJamb) con una arista exactamente en ese punto — pegar la
            // moldura al ras la duplicaría en vez de apoyarse en ella.
            Vector2 ia = Vector2.Lerp(p0, p1, hr.u0) - along * TrimSeamGap;
            Vector2 ib = Vector2.Lerp(p0, p1, hr.u1) + along * TrimSeamGap;

            // Un lado por el que la abertura SIGUE a la pared vecina (spanCorners) no tiene jamba
            // real ahi -- AddJamb ya lo sabe (openStart/openEnd) y esto tiene que estar de
            // acuerdo, o sale una moldura flotando en el aire justo donde el hueco continua.
            if (!hr.openStart)
                AddClosedPrism(TrimRect(ia - along * TrimWidth, ia, TrimDepth), hr.y0, hr.y1);
            if (!hr.openEnd)
                AddClosedPrism(TrimRect(ib, ib + along * TrimWidth, TrimDepth), hr.y0, hr.y1);

            // El dintel y el umbral, separados de las jambas por el mismo hueco: sin él, la
            // esquina donde jamba y dintel se tocan comparte una arista EXACTA entre las dos
            // cajas (misma Y, mismas dos esquinas en planta) — duplicarla, no cerrarla. Una
            // moldura de verdad tampoco es una sola pieza en L por cada esquina; se ve bien con
            // el hueco.
            AddClosedPrism(TrimRect(ia - along * TrimWidth, ib + along * TrimWidth, TrimDepth),
                hr.y1 + TrimSeamGap, hr.y1 + TrimWidth);
            if (hr.y0 > 0.01f)
                AddClosedPrism(TrimRect(ia - along * TrimWidth, ib + along * TrimWidth, TrimDepth),
                    hr.y0 - TrimWidth, hr.y0 - TrimSeamGap);
        }

        /// <summary>El footprint de una tira de moldura: de <paramref name="a"/> a
        /// <paramref name="b"/> por la cara de la pared, y otro tanto hacia DENTRO de la sala.
        /// El sentido de giro no importa — <see cref="AddClosedPrism"/> decide la normal de
        /// cada cara por su propio centroide, no por cómo vengan estas cuatro esquinas.</summary>
        private static Vector2[] TrimRect(Vector2 a, Vector2 b, float depth)
        {
            Vector2 outward = RoomDefinition.OutwardNormal(a, b);
            Vector2 aIn = a - outward * depth, bIn = b - outward * depth;
            return new[] { a, b, bIn, aIn };
        }

        private static Vector3 V(Vector2 p, float y) => new Vector3(p.x, y, p.y);

        private static Vector2 WallUV(float alongMetres, float y) =>
            new Vector2(alongMetres, y) / GridVisualConstants.TileSize;


        /// <summary>
        /// La misma posición a lo largo de la pared, expresada sobre la cara EXTERIOR. Como el
        /// desplazamiento entre las dos caras es perpendicular a la pared, proyectar sobre su
        /// dirección no lo ve, y el resultado cae exactamente enfrente del punto interior.
        /// </summary>
        private static float MapU(float u, Vector2 i0, Vector2 i1, Vector2 o0, Vector2 o1)
        {
            Vector2 dir = o1 - o0;
            float len2 = dir.sqrMagnitude;
            if (len2 < 1e-8f) return u;
            return Mathf.Clamp01(Vector2.Dot(Vector2.Lerp(i0, i1, u) - o0, dir) / len2);
        }

        /// <summary>
        /// Como <see cref="MapU"/>, pero los EXTREMOS se quedan en 0 y en 1 en vez de proyectarse.
        ///
        /// Por el inglete, la cara exterior sobresale de la interior en las esquinas, así que
        /// proyectar el extremo de la pared cae ANTES del vértice exterior. Mientras ningún hueco
        /// llegaba a la esquina daba igual; en cuanto una abertura la dobla, su alféizar terminaba
        /// unos centímetros antes que el de la pared vecina y quedaba una ranura justo en el
        /// rincón — 12 aristas abiertas por esquina doblada. Los paneles ya lo hacían así; esto
        /// pone a la jamba en el mismo convenio.
        /// </summary>
        private static float MapUEnds(float u, Vector2 i0, Vector2 i1, Vector2 o0, Vector2 o1)
        {
            if (u <= 1e-6f) return 0f;
            if (u >= 1f - 1e-6f) return 1f;
            return MapU(u, i0, i1, o0, o1);
        }

        private static readonly List<float> _jambCuts = new List<float>();
        private static readonly List<HoleRect> _holesOut = new List<HoleRect>();

        /// <summary>UV planar en escala de mundo: la textura mide un tile, así que no se estira
        /// al cambiar el tamaño de la sala.</summary>
        private static Vector2 PlanarUV(Vector2 p) => p / GridVisualConstants.TileSize;

        /// <summary>
        /// Quad con UV deducidas de la POSICIÓN EN EL MUNDO, no de las esquinas.
        ///
        /// Es la diferencia entre una textura que mide lo mismo en todas partes y una que se
        /// estira. Con UV 0..1 por cara, un bloque de 4 m y una columna de 0,5 m gastan la misma
        /// textura entera: en el bloque sale reventada y en la columna apretada. Aquí la unidad
        /// es siempre el tile de 5 m, así que dos piezas pegadas comparten grano y el tamaño de
        /// la trama dice el tamaño real de la cosa.
        ///
        /// Una cara horizontal se mapea por (x, z); una vertical, por (distancia a lo largo de la
        /// cara, altura). El eje horizontal sale de la propia normal, así que una pared en
        /// diagonal se mapea igual de bien que una recta.
        /// </summary>
        private static void Quad(int submesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            Vector3 normal)
        {
            const float T = GridVisualConstants.TileSize;
            Vector2 UV(Vector3 p)
            {
                if (Mathf.Abs(normal.y) > 0.707f) return new Vector2(p.x, p.z) / T;
                var dir = new Vector2(-normal.z, normal.x).normalized;
                return new Vector2(p.x * dir.x + p.z * dir.y, p.y) / T;
            }
            Quad(submesh, a, b, c, d, normal, UV(a), UV(b), UV(c), UV(d));
        }

        private static void Quad(int submesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            Vector3 normal, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            int i0 = _verts.Count;
            AddVertex(a, normal, ua);
            AddVertex(b, normal, ub);
            AddVertex(c, normal, uc);
            AddVertex(d, normal, ud);
            Tri(submesh, i0, i0 + 1, i0 + 2, normal);
            Tri(submesh, i0, i0 + 2, i0 + 3, normal);
        }

        private static void AddVertex(Vector3 p, Vector3 n, Vector2 uv)
        {
            _verts.Add(p);
            _normals.Add(n);
            _uvs.Add(uv);
        }

        /// <summary>
        /// Emite un triángulo con el winding DERIVADO de la normal, no supuesto por quien llama.
        /// Cada cara ya sabe hacia dónde mira, así que el orden correcto se puede calcular en vez
        /// de acertarlo — y acertarlo a mano en seis superficies con dos sentidos de giro es
        /// exactamente donde salen las caras invisibles vistas desde el lado bueno.
        /// </summary>
        private static void Tri(int submesh, int a, int b, int c, Vector3 normal)
        {
            Vector3 geo = Vector3.Cross(_verts[b] - _verts[a], _verts[c] - _verts[a]);
            var t = _tris[submesh];
            if (Vector3.Dot(geo, normal) >= 0f) { t.Add(a); t.Add(b); t.Add(c); }
            else                                { t.Add(a); t.Add(c); t.Add(b); }
        }
    }
}
