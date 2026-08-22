using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Propiedades de la geometría que produce <see cref="RoomMeshBuilder"/>, y las fábricas de
    /// salas de ejemplo que usan los tests.
    ///
    /// Vive aparte de los tests porque son afirmaciones sobre la MALLA, no sobre un caso: las
    /// mismas tres —estanqueidad, caras no invertidas, textura sin estirar— son las que han
    /// cazado todos los bugs de esta serie, y se le exigen a cada sala nueva que se añada.
    /// </summary>
    internal static class RoomGeometry
    {
        // ── fábricas ──────────────────────────────────────────────────────────

        internal static RoomDefinition Box(int x, int z) =>
            new RoomDefinition { tilesX = x, tilesZ = z, heightMeters = 4f, sides = 4, squareness = 1f };

        internal static RoomDefinition.WallHole Door(int side, float along) =>
            new RoomDefinition.WallHole { side = side, along = along, baseY = 0f, width = 1.6f, height = 2.2f };

        internal static RoomDefinition.WallHole Window(int side, float along) =>
            new RoomDefinition.WallHole { side = side, along = along, baseY = 1.2f, width = 2f, height = 1.2f };

        internal static RoomDefinition.FloorHole Pit(Vector2 p, float sx, float sz, float d, float yaw) =>
            new RoomDefinition.FloorHole { position = p, sizeX = sx, sizeZ = sz, depth = d, yawDegrees = yaw };

        internal static RoomDefinition Blocks(int x, int z, params RoomDefinition.Notch[] notches)
        {
            var d = Box(x, z);
            d.planMode = RoomDefinition.PlanMode.Blocks;
            d.notches = notches;
            return d;
        }

        /// <summary>
        /// La misma sala sin sus sólidos sueltos. Los sólidos que se TOCAN comparten caras
        /// coincidentes por construcción (peldaños contiguos) y esas caras son interiores: la
        /// estanqueidad estricta se le exige al CASCARÓN, que es donde un hueco sí sería una
        /// grieta visible.
        /// </summary>
        internal static RoomDefinition ShellOnly(RoomDefinition d) => new RoomDefinition
        {
            tilesX = d.tilesX, tilesZ = d.tilesZ, heightMeters = d.heightMeters,
            sides = d.sides, squareness = d.squareness, wallThickness = d.wallThickness,
            planMode = d.planMode, notches = d.notches,
            holes = d.holes, floorHoles = d.floorHoles,
        };

        // ── la batería estándar ───────────────────────────────────────────────

        /// <summary>Construye la sala y le exige las tres propiedades. Devuelve la malla para que
        /// el test siga sondeando lo suyo sin volver a construirla.</summary>
        internal static Mesh AssertRoom(string name, RoomDefinition def)
        {
            var m = RoomMeshBuilder.Build(def);
            Assert.Greater(m.vertexCount, 0, $"{name}: no geometry");
            Assert.IsFalse(RoomMeshBuilder.TriangulationFailed, $"{name}: triangulation fell back");
            Assert.IsTrue(ClosedManifold(m, ExpectedOpenEdges(def), out string why),
                $"{name}: not a closed shell :: {why}");
            Assert.IsTrue(WindingOk(m, out int bw), $"{name}: {bw} triangles face the wrong way");
            Assert.IsTrue(UvWorldScale(m, out int bu), $"{name}: {bu} edges with stretched texture");
            return m;
        }

        /// <summary>
        /// Las aristas que un boquete de bloque que llega al suelo del bloque deja SIN pareja a
        /// propósito. Es la única abertura que el generador admite a sabiendas — cualquier otra
        /// arista sin pareja sigue siendo un bug. (Un pozo <c>bottomless</c> también estuvo aquí;
        /// ya no hace falta: cierra solo por el canto del muro.)
        /// </summary>
        internal static HashSet<(int, int, int, int, int, int)> ExpectedOpenEdges(RoomDefinition def)
        {
            var set = new HashSet<(int, int, int, int, int, int)>();
            (int, int, int) K(Vector2 p, float y) => (Mathf.RoundToInt(p.x * 1000f),
                Mathf.RoundToInt(y * 1000f), Mathf.RoundToInt(p.y * 1000f));
            void AddRect(Vector2[] rect, float y)
            {
                for (int i = 0; i < rect.Length; i++)
                {
                    var (ax, ay, az) = K(rect[i], y);
                    var (bx, by, bz) = K(rect[(i + 1) % rect.Length], y);
                    // No se sabe de antemano en qué sentido queda sin pareja la arista del
                    // rectángulo, así que se admiten los dos.
                    set.Add((ax, ay, az, bx, by, bz));
                    set.Add((bx, by, bz, ax, ay, az));
                }
            }
            foreach (var (rect, y) in RoomMeshBuilder.BlockDoorFloorOpenings(def)) AddRect(rect, y);
            return set;
        }

        // ── propiedades ───────────────────────────────────────────────────────

        internal static bool ClosedManifold(Mesh m, out string why) => ClosedManifold(m, null, out why);

        /// <summary>
        /// Cada arista dirigida exactamente una vez ⇒ cerrada y con winding coherente.
        ///
        /// Se emparejan por POSICIÓN (el generador duplica vértices a propósito) y con clave
        /// EXACTA: con un hash XOR las colisiones acusaban en falso a las plantas redondas.
        ///
        /// <paramref name="allowedOpen"/> son las únicas aristas a las que SÍ se les permite
        /// quedar sin pareja (ver <see cref="ExpectedOpenEdges"/>). Cualquier otra arista sin
        /// pareja sigue siendo un fallo.
        /// </summary>
        internal static bool ClosedManifold(Mesh m,
            HashSet<(int, int, int, int, int, int)> allowedOpen, out string why)
        {
            var v = m.vertices;
            var seen = new Dictionary<(int, int, int, int, int, int), int>();
            (int, int, int) K(Vector3 p) => (Mathf.RoundToInt(p.x * 1000f),
                Mathf.RoundToInt(p.y * 1000f), Mathf.RoundToInt(p.z * 1000f));

            void Edge(Vector3 a, Vector3 b)
            {
                var (ax, ay, az) = K(a); var (bx, by, bz) = K(b);
                var k = (ax, ay, az, bx, by, bz);
                seen.TryGetValue(k, out int c); seen[k] = c + 1;
            }

            for (int s = 0; s < m.subMeshCount; s++)
            {
                var t = m.GetTriangles(s);
                for (int i = 0; i < t.Length; i += 3)
                {
                    // Los triángulos PLANOS sí cuentan. Parece que no —no dibujan nada— pero son
                    // los que sostienen los puntos intermedios del borde contra la pared, así que
                    // ignorarlos deja sin pareja a las aristas de sus vecinos reales. Se probó
                    // saltarlos y aparecieron 12 aristas abiertas donde no había ningún agujero.
                    Edge(v[t[i]], v[t[i + 1]]);
                    Edge(v[t[i + 1]], v[t[i + 2]]);
                    Edge(v[t[i + 2]], v[t[i]]);
                }
            }

            int open = 0, dup = 0, expected = 0;
            var samples = new List<string>();
            foreach (var kv in seen)
            {
                var k = kv.Key;
                if (kv.Value != 1)
                {
                    dup++;
                    if (samples.Count < 3) samples.Add($"x{kv.Value} ({k.Item1},{k.Item2},{k.Item3})->({k.Item4},{k.Item5},{k.Item6})");
                    continue;
                }
                var rev = (k.Item4, k.Item5, k.Item6, k.Item1, k.Item2, k.Item3);
                if (!seen.TryGetValue(rev, out int back) || back != 1)
                {
                    if (allowedOpen != null && allowedOpen.Contains(k)) { expected++; continue; }
                    open++;
                    if (samples.Count < 3) samples.Add($"open ({k.Item1},{k.Item2},{k.Item3})->({k.Item4},{k.Item5},{k.Item6})");
                }
            }
            why = open == 0 && dup == 0 ? "" : $"{open} open, {dup} dup | {string.Join(" ; ", samples)}";
            if (why.Length == 0 && expected > 0) why = $"({expected} open edges allowed: block door onto the floor)";
            return open == 0 && dup == 0;
        }

        internal static bool WindingOk(Mesh m, out int bad)
        {
            bad = 0;
            var v = m.vertices; var n = m.normals;
            for (int s = 0; s < m.subMeshCount; s++)
            {
                var t = m.GetTriangles(s);
                for (int i = 0; i < t.Length; i += 3)
                {
                    Vector3 geo = Vector3.Cross(v[t[i + 1]] - v[t[i]], v[t[i + 2]] - v[t[i]]);
                    float area = geo.magnitude * 0.5f;
                    if (geo.sqrMagnitude < 1e-12f) continue; // plano a proposito
                    float dot = Vector3.Dot(geo.normalized, n[t[i]]);

                    // Dos cosas distintas, medidas distinto -- y confundirlas costo una vuelta:
                    //
                    //  · Una cara INVERTIDA apunta al reves: dot ~ -1. Eso es un bug de verdad
                    //    pase el tamaño que pase, y es lo que tenia la planta en T (50 m2 y -1).
                    //  · Una ASTILLA (2,7 mm2) da dot ~ 0 porque su normal geometrica es ruido
                    //    numerico. No es una inversion y no puede verse a ninguna distancia.
                    //
                    // Asi que se acusa la inversion a cualquier tamaño, y el desalineamiento
                    // suave solo cuando hay superficie de verdad detras.
                    bool inverted = dot < -0.5f;
                    bool misaligned = dot < 0.5f && area >= 1e-5f;
                    if (inverted || misaligned) bad++;
                }
            }
            return bad == 0;
        }

        /// <summary>Sin estirar ⇒ distancia en textura × tile = distancia real. Se mide arista a
        /// arista; medir el rango de UV de la malla entera da el mismo número con y sin
        /// estiramiento porque la sala ahoga a la pieza.</summary>
        internal static bool UvWorldScale(Mesh m, out int bad)
        {
            bad = 0;
            var v = m.vertices; var uv = m.uv;
            for (int s = 0; s < m.subMeshCount; s++)
            {
                var t = m.GetTriangles(s);
                for (int i = 0; i < t.Length; i += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int a = t[i + e], b = t[i + (e + 1) % 3];
                        float world = Vector3.Distance(v[a], v[b]);
                        if (world < 0.01f) continue;
                        float tex = Vector2.Distance(uv[a], uv[b]) * GridVisualConstants.TileSize;
                        if (Mathf.Abs(tex - world) > 0.02f + world * 0.02f) bad++;
                    }
            }
            return bad == 0;
        }

        // ── sondas ────────────────────────────────────────────────────────────

        internal static bool Inside(List<RoomPool.CollisionBox> boxes, Vector3 p)
        {
            foreach (var b in boxes)
            {
                Vector3 l = Quaternion.Euler(0f, -b.yawDegrees, 0f) * (p - b.center);
                if (Mathf.Abs(l.x) <= b.size.x * 0.5f && Mathf.Abs(l.y) <= b.size.y * 0.5f
                    && Mathf.Abs(l.z) <= b.size.z * 0.5f) return true;
            }
            return false;
        }

        /// <summary>
        /// ¿Cruza algún triángulo la vertical por <paramref name="xz"/> entre esas dos alturas?
        /// Sonda directa de "el agujero está de verdad abierto": por la boca de un pozo sin fondo
        /// no puede pasar NADA, ni siquiera una cara que no se vea por estar de espaldas.
        /// </summary>
        internal static bool CrossesVertical(Mesh m, Vector2 xz, float yLo, float yHi)
        {
            var v = m.vertices;
            for (int s = 0; s < m.subMeshCount; s++)
            {
                var t = m.GetTriangles(s);
                for (int i = 0; i < t.Length; i += 3)
                {
                    Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                    // Baricéntricas en PLANTA: el triángulo visto desde arriba. Uno vertical
                    // (área nula en planta) no puede cruzar una vertical, y se descarta solo.
                    float d = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                    if (Mathf.Abs(d) < 1e-9f) continue;
                    float w0 = ((b.z - c.z) * (xz.x - c.x) + (c.x - b.x) * (xz.y - c.z)) / d;
                    float w1 = ((c.z - a.z) * (xz.x - c.x) + (a.x - c.x) * (xz.y - c.z)) / d;
                    float w2 = 1f - w0 - w1;
                    if (w0 < 0f || w1 < 0f || w2 < 0f) continue;
                    float y = w0 * a.y + w1 * b.y + w2 * c.y;
                    if (y >= yLo && y <= yHi) return true;
                }
            }
            return false;
        }

        /// <summary>Primera altura, subiendo desde la cabeza de un jugador, en la que algo
        /// bloquea. float.MaxValue si nada lo hace.</summary>
        internal static float FirstBlockedGoingUp(List<RoomPool.CollisionBox> boxes, Vector2 xz)
        {
            for (float y = 1.8f; y < 30f; y += 0.02f)
                if (Inside(boxes, new Vector3(xz.x, y, xz.y))) return y;
            return float.MaxValue;
        }

        /// <summary>Altura del vertice de techo mas cercano a (x,z): sonda directa de que el
        /// techo esta de verdad a distinta altura en cada extremo.</summary>
        internal static float CeilingYNear(Mesh m, float x, float z)
        {
            float best = float.MaxValue, y = 0f;
            foreach (var v in m.vertices)
            {
                if (v.y < 1f) continue;                       // suelo y zocalo, fuera
                float d = (v.x - x) * (v.x - x) + (v.z - z) * (v.z - z);
                if (d < best) { best = d; y = v.y; }
            }
            return y;
        }

        internal static bool HasVertex(Mesh m, float x, float y, float z)
        {
            var w = new Vector3(x, y, z);
            foreach (var v in m.vertices) if ((v - w).sqrMagnitude < 0.0001f) return true;
            return false;
        }

        internal static bool HasVertexY(Mesh m, float y)
        {
            foreach (var v in m.vertices) if (Mathf.Abs(v.y - y) < 0.01f) return true;
            return false;
        }

        /// <summary>Hay algún triángulo de esa submalla concreta con algún vértice a esa altura.
        /// Distingue "la geometría llega hasta aquí" (cualquier submalla) de "hay un SUELO
        /// pisable aquí" (justo la submalla de suelo) — un pozo sin fondo tiene lo primero y no
        /// lo segundo en su remate.</summary>
        internal static bool HasTriangleNearY(Mesh m, int submesh, float y)
        {
            var v = m.vertices;
            var t = m.GetTriangles(submesh);
            for (int i = 0; i < t.Length; i++)
                if (Mathf.Abs(v[t[i]].y - y) < 0.01f) return true;
            return false;
        }

        internal static Vector2 WallMid(RoomDefinition d, int side, float along)
        {
            var inner = d.InnerContour();
            int n = inner.Length;
            Vector2 p0 = inner[side], p1 = inner[(side + 1) % n];
            var nrm = new Vector2(p1.y - p0.y, -(p1.x - p0.x)).normalized;
            if (Vector2.Dot(nrm, (p0 + p1) * 0.5f) < 0f) nrm = -nrm;
            return Vector2.Lerp(p0, p1, along) + nrm * (d.wallThickness * 0.5f);
        }

        // ── planta ────────────────────────────────────────────────────────────

        /// <summary>Cuanto se han movido los vertices entre dos plantas del mismo numero de
        /// puntos. Grande = distintas.</summary>
        internal static float ContourDiff(Vector2[] a, Vector2[] b)
        {
            if (a.Length != b.Length) return float.MaxValue;
            float d = 0f;
            for (int i = 0; i < a.Length; i++) d = Mathf.Max(d, Vector2.Distance(a[i], b[i]));
            return d;
        }

        /// <summary>Dos aristas NO contiguas que se cruzan: la planta se ha plegado sobre si
        /// misma y todo lo que venga despues sale mal.</summary>
        internal static bool SelfIntersects(Vector2[] p)
        {
            int n = p.Length;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i || (j + 1) % n == i || (i + 1) % n == j) continue;
                    if (Cross2(p[i], p[(i + 1) % n], p[j], p[(j + 1) % n])) return true;
                }
            return false;
        }

        private static bool Cross2(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float S(Vector2 p, Vector2 q, Vector2 r) => (q.x - p.x) * (r.y - p.y) - (q.y - p.y) * (r.x - p.x);
            float d1 = S(a, b, c), d2 = S(a, b, d), d3 = S(c, d, a), d4 = S(c, d, b);
            return ((d1 > 0f) != (d2 > 0f)) && ((d3 > 0f) != (d4 > 0f));
        }

        /// <summary>Todos los giros del mismo signo ⇒ convexo. Sirve para comprobar que la muesca
        /// se aplicó de verdad y no salió otra caja.</summary>
        internal static bool IsConvex(Vector2[] p)
        {
            int sign = 0;
            for (int i = 0; i < p.Length; i++)
            {
                Vector2 a = p[(i + 1) % p.Length] - p[i];
                Vector2 b = p[(i + 2) % p.Length] - p[(i + 1) % p.Length];
                float cr = a.x * b.y - a.y * b.x;
                if (Mathf.Abs(cr) < 1e-6f) continue;
                int s = cr > 0f ? 1 : -1;
                if (sign == 0) sign = s;
                else if (s != sign) return false;
            }
            return true;
        }
    }
}
