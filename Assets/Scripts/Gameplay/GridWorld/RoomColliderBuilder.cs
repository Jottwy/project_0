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

            int n = inner.Length;
            var holes = new List<HoleRect>();
            for (int i = 0; i < n; i++)
            {
                Vector2 p0 = inner[i], p1 = inner[(i + 1) % n];
                float len = Vector2.Distance(p0, p1);
                if (len < 1e-4f) continue;

                Vector2 dir = (p1 - p0) / len;
                Vector2 nrm = RoomDefinition.OutwardNormal(p0, p1);
                float yaw = Mathf.Atan2(-dir.y, dir.x) * Mathf.Rad2Deg;

                CollectHoles(def, i, n, len, yFloor, yCeil, holes);
                AddWallBoxes(boxes, p0, p1, dir, nrm, yaw, len, t, yFloor, yCeil, holes);
            }

            if (def.pillars != null)
                foreach (var p in def.pillars)
                {
                    if (p == null || p.size <= 0.001f) continue;
                    // `size` es el ancho ENTRE CARAS, así que una columna de 4 lados da una caja
                    // de ese ancho exacto. Con más lados la caja la circunscribe de sobra, que es
                    // el lado correcto por el que equivocarse en colisión.
                    boxes.Add(Box(new Vector3(p.position.x, (yFloor + yCeil) * 0.5f, p.position.y),
                        new Vector3(p.size, h, p.size), p.yawDegrees));
                }

            if (def.pillarGrids != null)
                foreach (var g in def.pillarGrids)
                {
                    if (g == null || g.size <= 0.001f) continue;
                    for (int ix = 0; ix < g.countX; ix++)
                        for (int iz = 0; iz < g.countZ; iz++)
                        {
                            Vector2 pos = g.PositionOf(ix, iz);
                            boxes.Add(Box(new Vector3(pos.x, (yFloor + yCeil) * 0.5f, pos.y),
                                new Vector3(g.size, h, g.size), g.yawDegrees));
                        }
                }

            if (def.blocks != null)
                foreach (var b in def.blocks)
                {
                    if (b == null || b.sizeX <= 0.001f || b.sizeZ <= 0.001f || b.height <= 0.001f) continue;
                    boxes.Add(Box(new Vector3(b.position.x, b.baseY + b.height * 0.5f, b.position.y),
                        new Vector3(b.sizeX, b.height, b.sizeZ), b.yawDegrees));
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

                    float rad = s.yawDegrees * Mathf.Deg2Rad;
                    var forward = new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
                    for (int i = 0; i < s.steps; i++)
                    {
                        Vector2 c = s.position + forward * (s.run * (i + 0.5f));
                        float top = s.rise * (i + 1);
                        boxes.Add(Box(new Vector3(c.x, top * 0.5f, c.y),
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
                    bool solid = !InsideAnyHole(holes,
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

        private static void CollectHoles(RoomDefinition def, int side, int sides, float len,
            float yFloor, float yCeil, List<HoleRect> into)
        {
            into.Clear();
            if (def.holes == null) return;

            foreach (var hole in def.holes)
            {
                if (hole == null) continue;
                if (((hole.side % sides) + sides) % sides != side) continue;
                if (hole.width <= 0.001f || hole.height <= 0.001f) continue;

                // El hueco NO puede llegar al final de la pared: si se come la esquina, la
                // pared vecina sigue entera justo ahi y las dos aristas dejan de casar -- 22
                // aristas abiertas en una sala aleatoria que pidio una ventana mas ancha que su
                // muro. Se reserva una jamba de al menos el grosor del muro en cada extremo,
                // que ademas es lo que tiene sentido constructivo.
                float margin = Mathf.Clamp(def.wallThickness / len, 0.001f, 0.45f);
                float half = hole.width * 0.5f / len;
                float u0 = Mathf.Clamp(hole.along - half, margin, 1f - margin);
                float u1 = Mathf.Clamp(hole.along + half, margin, 1f - margin);
                float v0 = Mathf.Clamp(hole.baseY, yFloor, yCeil);
                float v1 = Mathf.Clamp(hole.baseY + hole.height, yFloor, yCeil);
                if (u1 - u0 < 1e-4f || v1 - v0 < 1e-4f) continue;

                into.Add(new HoleRect { u0 = u0, u1 = u1, y0 = v0, y1 = v1, bars = hole.grateBars });
            }

            RoomDefinition.MergeOverlapping(into);
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
            for (int i = cuts.Count - 1; i > 0; i--)
                if (cuts[i] - cuts[i - 1] < 1e-5f) cuts.RemoveAt(i);
            return cuts;
        }

        private static bool InsideAnyHole(List<HoleRect> holes, float u, float y)
        {
            for (int i = 0; i < holes.Count; i++)
                if (u > holes[i].u0 && u < holes[i].u1 && y > holes[i].y0 && y < holes[i].y1)
                    return true;
            return false;
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
            float y = (yBottom + yFloor) * 0.5f;

            var pits = new List<Bounds>();
            if (def.floorHoles != null)
                foreach (var f in def.floorHoles)
                {
                    if (f == null || f.sizeX <= 0.01f || f.sizeZ <= 0.01f || f.depth <= 0.01f) continue;
                    var corners = RoomMeshBuilder.BoxCorners(f.position, f.sizeX, f.sizeZ, f.yawDegrees);
                    var mn = new Vector2(float.MaxValue, float.MaxValue);
                    var mx = new Vector2(float.MinValue, float.MinValue);
                    foreach (var c in corners) { mn = Vector2.Min(mn, c); mx = Vector2.Max(mx, c); }
                    pits.Add(new Bounds(new Vector3((mn.x + mx.x) * 0.5f, 0f, (mn.y + mx.y) * 0.5f),
                        new Vector3(mx.x - mn.x, 1f, mx.y - mn.y)));
                }

            if (pits.Count == 0)
            {
                boxes.Add(Box(new Vector3(bb.center.x, y, bb.center.z),
                    new Vector3(bb.size.x, t, bb.size.z), 0f));
                return;
            }

            var xs = new List<float> { bb.min.x, bb.max.x };
            var zs = new List<float> { bb.min.z, bb.max.z };
            foreach (var p in pits)
            {
                xs.Add(p.min.x); xs.Add(p.max.x);
                zs.Add(p.min.z); zs.Add(p.max.z);
            }
            Tidy(xs, bb.min.x, bb.max.x);
            Tidy(zs, bb.min.z, bb.max.z);

            for (int i = 0; i < xs.Count - 1; i++)
                for (int k = 0; k < zs.Count - 1; k++)
                {
                    float cx = (xs[i] + xs[i + 1]) * 0.5f, cz = (zs[k] + zs[k + 1]) * 0.5f;
                    bool inPit = false;
                    foreach (var p in pits)
                        if (cx > p.min.x && cx < p.max.x && cz > p.min.z && cz < p.max.z) inPit = true;
                    if (inPit) continue;

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
