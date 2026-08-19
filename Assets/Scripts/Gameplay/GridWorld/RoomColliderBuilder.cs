using System.Collections.Generic;
using UnityEngine;

// Mismo tipo y mismo fundido de solapes que la malla: si divergieran, se vería una puerta
// donde hay muro o al reves.
using HoleRect = BackroomsSurvival.Gameplay.GridWorld.RoomDefinition.HoleRect;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Deriva el proxy de colisión de un <see cref="RoomDefinition"/>: una lista de cajas
    /// (con giro) que representan suelo, techo, paredes y columnas.
    ///
    /// Sale del MODELO y no de la malla, y esa es la decisión importante. Sabiendo que un lado
    /// es "un trozo recto de 3 m con una puerta en medio" salen tres cajas exactas; deducirlo de
    /// una sopa de triángulos sería adivinar, y encima la malla lleva detalle visual (molduras,
    /// salientes) que NO debe costar colisión.
    ///
    /// PURO y sin estado, igual que <see cref="RoomMeshBuilder"/>: los dos leen la misma fuente,
    /// así que lo que se ve y lo que bloquea no pueden separarse.
    ///
    /// A diferencia de la malla, aquí los cortes son POR LADO y no compartidos entre todos: los
    /// cortes globales existen para que las aristas casen y no queden T-junctions, un problema
    /// de render que a la colisión no le afecta. Compartirlos aquí solo multiplicaría las cajas.
    /// </summary>
    public static class RoomColliderBuilder
    {
        public static List<RoomPool.CollisionBox> Build(RoomDefinition def)
        {
            var boxes = new List<RoomPool.CollisionBox>();

            float t = Mathf.Max(0.001f, def.wallThickness);
            float h = Mathf.Max(0.01f, def.heightMeters);
            float yBottom = -t, yFloor = 0f, yCeil = h, yTop = h + t;

            Vector2[] inner = def.InnerContour();
            Vector2[] outer = RoomDefinition.OffsetOutward(inner, t);

            // Suelo y techo como una losa cada uno, del tamaño del rectángulo envolvente. Que
            // sobresalga en una planta redonda no importa: las esquinas que sobran caen FUERA de
            // la sala, detrás de las paredes, donde el jugador no puede llegar igualmente.
            Bounds bb = XZBounds(outer);
            // El suelo se parte en tiras alrededor de los pozos. Una sola losa dejaría el hueco
            // tapado por colisión: se vería el pozo y no se podría bajar, que es el peor fallo
            // posible aquí porque el jugador ve una cosa y el juego hace otra.
            AddFloorSlab(boxes, def, bb, t, yBottom, yFloor);
            AddCeilingSlab(boxes, def, bb, t);
            AddPitBoxes(boxes, def, yFloor, t);
            AddLevelBoxes(boxes, def, bb, t, def.MinCeilingOver(inner));

            int n = inner.Length;
            // Contra el techo MAS BAJO menos el mismo dintel minimo que usa la malla
            // (RoomMeshBuilder.MinLintel), no contra la altura nominal: con techo inclinado la
            // malla recorta ahi un hueco alto, y si aqui se recortara contra yCeil el hueco de
            // colision llegaria mas arriba que el de la malla -- pared solida que se atraviesa,
            // o al reves, justo en el remate del boquete.
            float minCeil = def.MinCeilingOver(inner);

            // La MISMA resolución que usa la malla, no una copia: cuando eran dos bucles gemelos,
            // cualquier arreglo había que aplicarlo dos veces, y olvidarse de uno es exactamente
            // el fallo de "veo una puerta y me choco contra ella".
            var sideHoles = new List<HoleRect>[n];
            for (int i = 0; i < n; i++) sideHoles[i] = new List<HoleRect>();
            def.ResolveHoles(inner, yFloor, minCeil - RoomMeshBuilder.MinLintel, sideHoles);

            // Una reja bloquea el paso aunque la malla dibuje un hueco real con barrotes
            // cruzandolo: para la colision NO es una abertura, es pared entera. Es la unica
            // discrepancia adrede entre malla y colision de este archivo -- todo lo demas se
            // resuelve igual a proposito.
            for (int i = 0; i < n; i++) sideHoles[i].RemoveAll(h => h.bars > 0);

            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = inner[i], p1 = inner[(i + 1) % n];
                float len = Vector2.Distance(p0, p1);
                if (len < 1e-4f) continue;

                Vector2 dir = (p1 - p0) / len;
                Vector2 nrm = RoomDefinition.OutwardNormal(p0, p1);
                float yaw = Mathf.Atan2(-dir.y, dir.x) * Mathf.Rad2Deg;

                AddWallBoxes(boxes, p0, p1, dir, nrm, yaw, len, t, yFloor, yCeil, sideHoles[i]);
            }

            if (def.pillars != null)
                foreach (var p in def.pillars)
                {
                    if (p == null || p.size <= 0.001f) continue;
                    // `size` es el ancho ENTRE CARAS, así que una columna de 4 lados da una caja
                    // de ese ancho exacto. Con más lados la caja la circunscribe de sobra, que es
                    // el lado correcto por el que equivocarse en colisión.
                    // Del suelo de SU piso al techo de SU piso, EXACTAMENTE igual que la malla.
                    float py0 = def.StoreyBaseY(p.level, minCeil);
                    float py1 = def.StoreyCeilingY(p.level, minCeil);
                    boxes.Add(Box(new Vector3(p.position.x, (py0 + py1) * 0.5f, p.position.y),
                        new Vector3(p.size, py1 - py0, p.size), p.yawDegrees));
                }

            if (def.pillarGrids != null)
                foreach (var g in def.pillarGrids)
                {
                    if (g == null || g.size <= 0.001f) continue;
                    float gy0 = def.StoreyBaseY(g.level, minCeil);
                    float gy1 = def.StoreyCeilingY(g.level, minCeil);
                    for (int ix = 0; ix < g.countX; ix++)
                        for (int iz = 0; iz < g.countZ; iz++)
                        {
                            Vector2 pos = g.PositionOf(ix, iz);
                            boxes.Add(Box(new Vector3(pos.x, (gy0 + gy1) * 0.5f, pos.y),
                                new Vector3(g.size, gy1 - gy0, g.size), g.yawDegrees));
                        }
                }

            if (def.blocks != null)
                foreach (var b in def.blocks)
                {
                    if (b == null || b.sizeX <= 0.001f || b.sizeZ <= 0.001f || b.height <= 0.001f) continue;
                    AddBlockBoxes(boxes, b, def.StoreyBaseY(b.level, minCeil));
                }

            // Escaleras: una caja por peldaño, calcada de la malla. Es escalonada y no una rampa
            // lisa a propósito — que la colisión sea EXACTAMENTE lo que se ve es más importante
            // que que sea suave, porque una rampa invisible deja al jugador flotando sobre el
            // borde de los peldaños.
            if (def.stairs != null)
                foreach (var s in def.stairs)
                {
                    if (s == null || s.steps < 1 || s.width <= 0.001f
                        || s.rise <= 0.001f || s.run <= 0.001f) continue;

                    // Cada peldaño desde el suelo de SU piso, igual que la malla.
                    float baseY = def.StoreyBaseY(s.level, minCeil);
                    float rad = s.yawDegrees * Mathf.Deg2Rad;
                    var forward = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
                    for (int i = 0; i < s.steps; i++)
                    {
                        Vector2 c = s.position + forward * (s.run * (i + 0.5f));
                        float top = s.rise * (i + 1);
                        boxes.Add(Box(new Vector3(c.x, baseY + top * 0.5f, c.y),
                            new Vector3(s.width, top, s.run), s.yawDegrees));
                    }
                }

            return boxes;
        }

        /// <summary>
        /// Las cajas de un lado. Se recorren las franjas de altura y, dentro de cada una, los
        /// tramos macizos de izquierda a derecha, FUSIONANDO los contiguos: una pared con una
        /// puerta son 3 cajas (izquierda, derecha, dintel) y no las 6 celdas de la rejilla.
        /// </summary>
        private static void AddWallBoxes(List<RoomPool.CollisionBox> into,
            Vector2 p0, Vector2 p1, Vector2 dir, Vector2 nrm, float yaw,
            float len, float t, float yFloor, float yCeil, List<HoleRect> holes)
        {
            var uCuts = Cuts(0f, 1f, holes, horizontal: true);
            var yCuts = Cuts(yFloor, yCeil, holes, horizontal: false);

            for (int vi = 0; vi < yCuts.Count - 1; vi++)
            {
                float ya = yCuts[vi], yb = yCuts[vi + 1];
                if (yb - ya < 1e-4f) continue;

                int runStart = -1;
                for (int ui = 0; ui < uCuts.Count - 1; ui++)
                {
                    bool solid = !HoleRect.InsideAny(holes,
                        (uCuts[ui] + uCuts[ui + 1]) * 0.5f, (ya + yb) * 0.5f);

                    if (solid && runStart < 0) runStart = ui;
                    bool runEnds = !solid || ui == uCuts.Count - 2;
                    if (runStart < 0 || !runEnds) continue;

                    int last = solid ? ui : ui - 1;
                    Emit(into, p0, p1, nrm, yaw, len, t,
                        uCuts[runStart], uCuts[last + 1], ya, yb);
                    runStart = -1;
                }
            }
        }

        private static void Emit(List<RoomPool.CollisionBox> into, Vector2 p0, Vector2 p1,
            Vector2 nrm, float yaw, float len, float t, float ua, float ub, float ya, float yb)
        {
            if (ub - ua < 1e-5f) return;
            // El muro ocupa desde el contorno interior hacia AFUERA, así que el centro de la caja
            // está medio grosor por fuera de la cara que se ve desde dentro de la sala.
            Vector2 mid = Vector2.Lerp(p0, p1, (ua + ub) * 0.5f) + nrm * (t * 0.5f);
            into.Add(Box(new Vector3(mid.x, (ya + yb) * 0.5f, mid.y),
                new Vector3((ub - ua) * len, yb - ya, t), yaw));
        }

        /// <summary>
        /// Caja única si el bloque no tiene boquetes (lo de siempre); si tiene, una tira por eje
        /// CON hueco — un eje sin boquete no necesita tira propia, ya lo cubre por completo la
        /// tira del otro eje (que abarca el espesor entero en esa dirección).
        /// </summary>
        private static void AddBlockBoxes(List<RoomPool.CollisionBox> boxes, RoomDefinition.Block b,
            float baseOffset)
        {
            var holesZ = new List<HoleRect>();
            var holesX = new List<HoleRect>();
            // El MISMO offset de piso que la malla — mismo motivo de siempre.
            b.ResolveHoles(holesZ, holesX, baseOffset);
            float y0 = baseOffset + b.baseY, y1 = y0 + b.height;

            if (holesZ.Count == 0 && holesX.Count == 0)
            {
                boxes.Add(Box(new Vector3(b.position.x, y0 + b.height * 0.5f, b.position.y),
                    new Vector3(b.sizeX, b.height, b.sizeZ), b.yawDegrees));
                return;
            }

            float r = b.yawDegrees * Mathf.Deg2Rad;
            var ax = new Vector2(Mathf.Cos(r), -Mathf.Sin(r));
            var az = new Vector2(Mathf.Sin(r), Mathf.Cos(r));

            if (holesZ.Count > 0)
                AddBlockAxisBoxes(boxes, b.position, ax, b.sizeX, b.sizeZ, b.yawDegrees, y0, y1, holesZ);
            if (holesX.Count > 0)
                AddBlockAxisBoxes(boxes, b.position, az, b.sizeZ, b.sizeX, b.yawDegrees - 90f, y0, y1, holesX);
        }

        /// <summary>Tiras a lo largo de <paramref name="uDir"/>, cada una del espesor COMPLETO
        /// <paramref name="sizeV"/> perpendicular — el túnel atraviesa el bloque entero en esa
        /// dirección, así que no hace falta partir también por ahí.</summary>
        private static void AddBlockAxisBoxes(List<RoomPool.CollisionBox> boxes, Vector2 centre,
            Vector2 uDir, float sizeU, float sizeV, float yaw, float y0, float y1, List<HoleRect> holes)
        {
            var uCuts = Cuts(0f, 1f, holes, horizontal: true);
            var yCuts = Cuts(y0, y1, holes, horizontal: false);

            for (int vi = 0; vi < yCuts.Count - 1; vi++)
            {
                float ya = yCuts[vi], yb = yCuts[vi + 1];
                if (yb - ya < 1e-4f) continue;

                int runStart = -1;
                for (int ui = 0; ui < uCuts.Count - 1; ui++)
                {
                    bool solid = !HoleRect.InsideAny(holes,
                        (uCuts[ui] + uCuts[ui + 1]) * 0.5f, (ya + yb) * 0.5f);
                    if (solid && runStart < 0) runStart = ui;
                    bool runEnds = !solid || ui == uCuts.Count - 2;
                    if (runStart < 0 || !runEnds) continue;

                    int last = solid ? ui : ui - 1;
                    float ua = uCuts[runStart], ub = uCuts[last + 1];
                    if (ub - ua >= 1e-5f)
                    {
                        Vector2 mid = centre + uDir * ((ua + ub) * 0.5f * sizeU - sizeU * 0.5f);
                        boxes.Add(Box(new Vector3(mid.x, (ya + yb) * 0.5f, mid.y),
                            new Vector3((ub - ua) * sizeU, yb - ya, sizeV), yaw));
                    }
                    runStart = -1;
                }
            }
        }

        private static List<float> Cuts(float lo, float hi, List<HoleRect> holes, bool horizontal)
        {
            var cuts = new List<float> { lo, hi };
            for (int i = 0; i < holes.Count; i++)
            {
                float a = horizontal ? holes[i].u0 : holes[i].y0;
                float b = horizontal ? holes[i].u1 : holes[i].y1;
                if (a > lo && a < hi) cuts.Add(a);
                if (b > lo && b < hi) cuts.Add(b);
            }
            cuts.Sort();
            // MISMA tolerancia que la malla usa para fundir estos mismos HoleRect
            // (RoomMeshBuilder.UCutTolerance/CutTolerance) -- un epsilon mas fino que esa fusion
            // deja una caja de colision en forma de loncha entre dos cortes casi-coincidentes.
            float eps = horizontal ? RoomMeshBuilder.UCutTolerance : RoomMeshBuilder.CutTolerance;
            for (int i = cuts.Count - 1; i > 0; i--)
                if (cuts[i] - cuts[i - 1] < eps) cuts.RemoveAt(i);
            return cuts;
        }

        private static Bounds XZBounds(Vector2[] poly)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var p in poly) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            var c = (min + max) * 0.5f;
            var s = max - min;
            return new Bounds(new Vector3(c.x, 0f, c.y), new Vector3(s.x, 0f, s.y));
        }

        /// <summary>
        /// La losa del suelo, partida en tiras para dejar libres los pozos.
        ///
        /// Corta en REJILLA por los bordes de los pozos y emite las celdas que no caen dentro de
        /// ninguno. Los pozos girados se tratan por su envolvente alineada a los ejes: eso deja
        /// un poco de suelo de menos en las esquinas de un pozo torcido, y es el lado correcto
        /// por el que equivocarse — sobra hueco, nunca suelo invisible sobre el que caminar.
        /// </summary>
        private static void AddFloorSlab(List<RoomPool.CollisionBox> boxes, RoomDefinition def,
            Bounds bb, float t, float yBottom, float yFloor)
        {
            var pits = new List<Bounds>();
            if (def.floorHoles != null)
                foreach (var f in def.floorHoles)
                {
                    if (f == null || f.sizeX <= 0.01f || f.sizeZ <= 0.01f
                        || (!f.bottomless && f.depth <= 0.01f)) continue;
                    pits.Add(XZBounds(RoomMeshBuilder.BoxCorners(f.position, f.sizeX, f.sizeZ, f.yawDegrees)));
                }
            AddSlabWithHoles(boxes, bb, (yBottom + yFloor) * 0.5f, t, pits);
        }

        /// <summary>
        /// Las losas de las entreplantas: la misma losa completa que trae el suelo de la sala,
        /// solo que a media altura y con el hueco donde una escalera la atraviese en vez del de
        /// un pozo. Comparte la partición en tiras con <see cref="AddFloorSlab"/> — es la misma
        /// idea (una losa, unos huecos rectangulares) aplicada a otra altura.
        /// </summary>
        private static void AddLevelBoxes(List<RoomPool.CollisionBox> boxes, RoomDefinition def,
            Bounds bb, float t, float minCeil)
        {
            if (def.levels == null) return;
            foreach (var lvl in def.levels)
            {
                if (lvl == null) continue;
                float top = def.ClampLevelHeight(lvl.height, minCeil);

                var holes = new List<Bounds>();
                foreach (var s in def.StairsReaching(top, minCeil))
                    holes.Add(XZBounds(RoomMeshBuilder.BoxCorners(
                        s.FootprintCentre(), s.width, s.FootprintLength(), s.yawDegrees)));

                AddSlabWithHoles(boxes, bb, top - t * 0.5f, t, holes);
            }
        }

        /// <summary>
        /// Una losa horizontal partida en tiras alrededor de <paramref name="holes"/>, saltando
        /// las celdas que caigan dentro de alguno. Sin huecos, una sola caja — partir de más no
        /// aporta nada y multiplica cajas sin necesidad.
        /// </summary>
        private static void AddSlabWithHoles(List<RoomPool.CollisionBox> boxes, Bounds bb, float y,
            float t, List<Bounds> holes)
        {
            if (holes.Count == 0)
            {
                boxes.Add(Box(new Vector3(bb.center.x, y, bb.center.z),
                    new Vector3(bb.size.x, t, bb.size.z), 0f));
                return;
            }

            var xs = new List<float> { bb.min.x, bb.max.x };
            var zs = new List<float> { bb.min.z, bb.max.z };
            foreach (var h in holes)
            {
                xs.Add(h.min.x); xs.Add(h.max.x);
                zs.Add(h.min.z); zs.Add(h.max.z);
            }
            Tidy(xs, bb.min.x, bb.max.x);
            Tidy(zs, bb.min.z, bb.max.z);

            for (int i = 0; i < xs.Count - 1; i++)
                for (int k = 0; k < zs.Count - 1; k++)
                {
                    float cx = (xs[i] + xs[i + 1]) * 0.5f, cz = (zs[k] + zs[k + 1]) * 0.5f;
                    bool inHole = false;
                    foreach (var h in holes)
                        if (cx > h.min.x && cx < h.max.x && cz > h.min.z && cz < h.max.z) inHole = true;
                    if (inHole) continue;

                    boxes.Add(Box(new Vector3(cx, y, cz),
                        new Vector3(xs[i + 1] - xs[i], t, zs[k + 1] - zs[k]), 0f));
                }
        }

        /// <summary>
        /// La losa del techo. Plana es UNA caja; inclinada se aproxima con una escalera de tiras
        /// a lo largo de la pendiente.
        ///
        /// Escalonada y no una caja girada porque <see cref="RoomPool.CollisionBox"/> solo lleva
        /// yaw: representar una pendiente pediria pitch, y con el pitch la caja deja de ser
        /// AABB-en-mundo cuando la sala gira 90 grados, que es justo la propiedad por la que se
        /// eligieron cajas. Cada tira se coloca a la altura MAS BAJA de su tramo: se choca un
        /// poco antes de tocar el techo pintado, nunca se atraviesa.
        /// </summary>
        /// <summary>
        /// Lo que hay DENTRO de un pozo: las cuatro paredes del hueco, para no salirse de lado
        /// mientras se está dentro, y — solo si tiene fondo — la losa en la que se aterriza.
        /// <see cref="AddFloorSlab"/> ya abre el agujero en la losa de arriba; esto es lo que hay
        /// dentro de ese agujero.
        ///
        /// Un pozo <see cref="RoomDefinition.FloorHole.bottomless"/> SÍ tiene paredes — hasta
        /// `Depth` metros, igual que uno con fondo — pero nunca losa: a partir de ahí no hay
        /// nada, ni suelo ni pared, y se sigue cayendo.
        /// </summary>
        private static void AddPitBoxes(List<RoomPool.CollisionBox> boxes, RoomDefinition def, float yFloor, float t)
        {
            if (def.floorHoles == null) return;
            foreach (var f in def.floorHoles)
            {
                if (f == null || f.sizeX <= 0.01f || f.sizeZ <= 0.01f) continue;
                if (!f.bottomless && f.depth <= 0.01f) continue;

                // El fondo de la pared: el propio suelo del pozo si lo tiene, o hasta donde
                // llegue `Depth` (con un mínimo de `t`, igual que en la malla) si no.
                float yBase = f.bottomless ? yFloor - Mathf.Max(f.depth, t) : yFloor - f.depth;

                if (!f.bottomless)
                    boxes.Add(Box(new Vector3(f.position.x, yBase - t * 0.5f, f.position.y),
                        new Vector3(f.sizeX, t, f.sizeZ), f.yawDegrees));

                var corners = RoomMeshBuilder.BoxCorners(f.position, f.sizeX, f.sizeZ, f.yawDegrees);
                for (int i = 0; i < 4; i++)
                {
                    Vector2 a = corners[i], b = corners[(i + 1) % 4];
                    float len = Vector2.Distance(a, b);
                    if (len < 1e-4f) continue;
                    Vector2 dir = (b - a) / len;
                    float yaw = Mathf.Atan2(-dir.y, dir.x) * Mathf.Rad2Deg;
                    Vector2 mid = (a + b) * 0.5f;
                    boxes.Add(Box(new Vector3(mid.x, (yBase + yFloor) * 0.5f, mid.y),
                        new Vector3(len, yFloor - yBase, t), yaw));
                }
            }
        }

        private static void AddCeilingSlab(List<RoomPool.CollisionBox> boxes, RoomDefinition def,
            Bounds bb, float t)
        {
            if (def.ceilingTilt <= 0.001f)
            {
                float y = def.heightMeters + t * 0.5f;
                boxes.Add(Box(new Vector3(bb.center.x, y, bb.center.z),
                    new Vector3(bb.size.x, t, bb.size.z), 0f));
                return;
            }

            float r = def.ceilingTiltYaw * Mathf.Deg2Rad;
            var down = new Vector2(Mathf.Sin(r), Mathf.Cos(r));
            bool alongX = Mathf.Abs(down.x) >= Mathf.Abs(down.y);

            float span = alongX ? bb.size.x : bb.size.z;
            int steps = Mathf.Clamp(Mathf.CeilToInt(span / 2f), 1, 32);   // una tira cada ~2 m
            float step = span / steps;
            float lo = alongX ? bb.min.x : bb.min.z;

            for (int i = 0; i < steps; i++)
            {
                float a = lo + i * step, b = a + step;
                // Lo mas bajo del tramo, muestreando sus dos extremos sobre el eje de la pendiente.
                Vector2 pa = alongX ? new Vector2(a, bb.center.z) : new Vector2(bb.center.x, a);
                Vector2 pb = alongX ? new Vector2(b, bb.center.z) : new Vector2(bb.center.x, b);
                float y = Mathf.Min(def.CeilingYAt(pa), def.CeilingYAt(pb)) + t * 0.5f;

                Vector3 c = alongX
                    ? new Vector3((a + b) * 0.5f, y, bb.center.z)
                    : new Vector3(bb.center.x, y, (a + b) * 0.5f);
                Vector3 size = alongX
                    ? new Vector3(step, t, bb.size.z)
                    : new Vector3(bb.size.x, t, step);
                boxes.Add(Box(c, size, 0f));
            }
        }

        private static void Tidy(List<float> v, float lo, float hi)
        {
            for (int i = v.Count - 1; i >= 0; i--)
                if (v[i] < lo || v[i] > hi) v.RemoveAt(i);
            v.Add(lo); v.Add(hi);
            v.Sort();
            for (int i = v.Count - 1; i > 0; i--)
                if (v[i] - v[i - 1] < 1e-4f) v.RemoveAt(i);
        }

        private static RoomPool.CollisionBox Box(Vector3 centre, Vector3 size, float yaw) =>
            new RoomPool.CollisionBox { center = centre, size = size, yawDegrees = yaw };
    }
}
