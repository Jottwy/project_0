using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Triangula un polígono CON AGUJEROS por recorte de orejas, con puentes.
    ///
    /// Existe por los huecos de SUELO. Un boquete de pared es un rectángulo dentro de otro
    /// rectángulo, y por eso allí basta una rejilla de cortes; el suelo es un polígono que puede
    /// ser redondo, y sacarle un rectángulo no se resuelve cortando en rejilla — o se triangula
    /// de verdad, o el suelo se parte por sitios que la pared no comparte y aparecen grietas.
    ///
    /// Conserva EXACTAMENTE los vértices del contorno que se le pasan: es lo que hace que el
    /// borde del suelo siga casando con el pie de las paredes, que están subdivididas por los
    /// cortes de sus propios boquetes. Un triangulador que reubicara puntos del borde rompería
    /// esa costura.
    ///
    /// El contorno exterior se espera en sentido ANTIHORARIO y cada agujero en HORARIO; el
    /// puente que une cada agujero al exterior es lo que convierte todo en un solo polígono
    /// simple que el recorte de orejas ya sabe tratar.
    /// </summary>
    public static class PolygonTriangulator
    {
        /// <summary>
        /// Triangula <paramref name="outer"/> menos <paramref name="holes"/>. Devuelve índices de
        /// tres en tres sobre <paramref name="vertices"/>, que la propia función rellena.
        /// Devuelve false si la entrada es degenerada — el llamador debe degradar, no asumir.
        /// </summary>
        public static bool Triangulate(IList<Vector2> outer, IList<IList<Vector2>> holes,
            List<Vector2> vertices, List<int> triangles)
        {
            vertices.Clear();
            triangles.Clear();
            if (outer == null || outer.Count < 3) return false;

            var poly = new List<Vector2>(outer);
            EnsureWinding(poly, wantCCW: true);

            if (holes != null)
            {
                // De DERECHA a izquierda. No es cosmético: cada puente sale del vértice más a la
                // derecha de su agujero hacia el contorno, así que cosiendo primero el agujero
                // más a la derecha ningún puente posterior tiene que cruzarlo. En orden de array
                // el segundo puente cruzaba el primer agujero, el recorte se rendía y el suelo
                // salía sin pozos.
                var sorted = new List<List<Vector2>>();
                foreach (var hole in holes)
                {
                    if (hole == null || hole.Count < 3) continue;
                    var h = new List<Vector2>(hole);
                    EnsureWinding(h, wantCCW: false); // horario: al insertarlo invierte el área
                    sorted.Add(h);
                }
                sorted.Sort((x, y) => MaxX(y).CompareTo(MaxX(x)));

                foreach (var h in sorted)
                    if (!BridgeHole(poly, h)) return false;
            }

            vertices.AddRange(poly);
            return EarClip(poly, triangles);
        }

        private static float MaxX(List<Vector2> p)
        {
            float m = float.MinValue;
            for (int i = 0; i < p.Count; i++) if (p[i].x > m) m = p[i].x;
            return m;
        }

        private static void EnsureWinding(List<Vector2> poly, bool wantCCW)
        {
            if (SignedArea(poly) < 0f == wantCCW) poly.Reverse();
        }

        private static float SignedArea(List<Vector2> p)
        {
            float a = 0f;
            for (int i = 0; i < p.Count; i++)
            {
                Vector2 c = p[i], n = p[(i + 1) % p.Count];
                a += c.x * n.y - n.x * c.y;
            }
            return a * 0.5f;
        }

        /// <summary>
        /// Cose un agujero al contorno con un puente: se busca el vértice del agujero más a la
        /// derecha y el punto del exterior visible desde él, y se inserta la lista del agujero
        /// entre medias duplicando los dos extremos del puente. El resultado es un polígono
        /// simple, con una hendidura infinitamente fina por donde entra el puente — que el
        /// recorte de orejas trata sin enterarse.
        /// </summary>
        private static bool BridgeHole(List<Vector2> outer, List<Vector2> hole)
        {
            int hi = 0;
            for (int i = 1; i < hole.Count; i++)
                if (hole[i].x > hole[hi].x) hi = i;

            // Vértice del exterior al que coser, por distancia — pero comprobando que el puente
            // no cruce nada. Con UN agujero bastaba el más cercano, porque el contorno es convexo
            // y no hay nada en medio. Con DOS deja de valer: el polígono ya lleva el primer
            // agujero dentro, y el camino recto al vértice más cercano puede atravesarlo. Se
            // prueban por cercanía y se coge el primero que no cruce ninguna arista.
            var order = new List<int>(outer.Count);
            for (int i = 0; i < outer.Count; i++) order.Add(i);
            order.Sort((x, y) => (outer[x] - hole[hi]).sqrMagnitude
                .CompareTo((outer[y] - hole[hi]).sqrMagnitude));

            int oi = -1;
            foreach (int cand in order)
                if (BridgeIsClear(outer, cand, hole[hi]) && BridgeMissesHole(hole, hi, outer[cand]))
                {
                    oi = cand;
                    break;
                }
            if (oi < 0) return false;

            var bridged = new List<Vector2>(outer.Count + hole.Count + 2);
            for (int i = 0; i <= oi; i++) bridged.Add(outer[i]);
            for (int k = 0; k < hole.Count; k++) bridged.Add(hole[(hi + k) % hole.Count]);
            bridged.Add(hole[hi]);
            for (int i = oi; i < outer.Count; i++) bridged.Add(outer[i]);

            outer.Clear();
            outer.AddRange(bridged);
            return true;
        }

        /// <summary>¿El puente de <paramref name="from"/> al vértice <paramref name="oi"/> cruza
        /// alguna arista del polígono? Las dos aristas que tocan ese vértice se excluyen: lo
        /// tocan por definición y no son un cruce.</summary>
        private static bool BridgeIsClear(List<Vector2> poly, int oi, Vector2 from)
        {
            Vector2 to = poly[oi];
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                if (i == oi || j == oi) continue;
                if (SegmentsCross(from, to, poly[i], poly[j])) return false;
            }
            return true;
        }

        /// <summary>
        /// ¿El puente se mete por el AGUJERO QUE VA A COSER? <see cref="BridgeIsClear"/> no puede
        /// verlo: mira las aristas del polígono, y este agujero todavía no está dentro.
        ///
        /// Lo destapa un agujero DESCENTRADO. El puente sale del vértice más a la derecha del
        /// agujero hacia el vértice del contorno MÁS CERCANO, y con el agujero corrido a la
        /// izquierda el más cercano pasa a ser una esquina del otro lado: el camino recto vuelve
        /// a entrar por el hueco. El polígono "simple" resultante se cruza a sí mismo, el recorte
        /// de orejas no encuentra ninguna válida —contra el teorema de las dos orejas, que solo
        /// vale para polígonos simples— y la sala entera se queda sin suelo. Centrado no pasaba
        /// porque el vértice más cercano cae al lado contrario del hueco.
        ///
        /// Con esto la candidata se descarta y se prueba la siguiente por cercanía, que es
        /// exactamente lo que ya hacía cuando el estorbo era OTRO agujero ya cosido — de ahí que
        /// añadir un segundo agujero al lado a veces lo arreglara por accidente.
        ///
        /// Igual que <see cref="BridgeIsClear"/>, solo detecta cruces PROPIOS: un puente que
        /// saliera rozando un vértice del agujero pasa el filtro. No se ha visto en la práctica y
        /// tratarlo pediría decidir de qué lado del vértice se va, que es otra discusión.
        /// </summary>
        private static bool BridgeMissesHole(List<Vector2> hole, int hi, Vector2 to)
        {
            Vector2 from = hole[hi];
            int n = hole.Count;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                // Las dos aristas que nacen del extremo del puente lo tocan por definición.
                if (i == hi || j == hi) continue;
                if (SegmentsCross(from, to, hole[i], hole[j])) return false;
            }
            return true;
        }

        private static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float d1 = Cross(b - a, c - a), d2 = Cross(b - a, d - a);
            float d3 = Cross(d - c, a - c), d4 = Cross(d - c, b - c);
            // Solo cruce PROPIO: tocarse en un extremo es legítimo y frecuente aquí.
            return ((d1 > Eps && d2 < -Eps) || (d1 < -Eps && d2 > Eps))
                && ((d3 > Eps && d4 < -Eps) || (d3 < -Eps && d4 > Eps));
        }

        private const float Eps = 1e-7f;
        private const float SameSq = 1e-10f;

        private static bool EarClip(List<Vector2> poly, List<int> triangles)
        {
            int n = poly.Count;
            var idx = new List<int>(n);
            for (int i = 0; i < n; i++) idx.Add(i);

            // Tope de vueltas: sin él, una entrada degenerada deja el bucle girando para siempre
            // y cuelga el editor.
            int guard = n * n + 16;
            while (idx.Count > 3 && guard-- > 0)
            {
                // DOS pasadas. En la primera solo valen orejas con área; solo si no queda
                // ninguna se admite una plana.
                //
                // Importa en las plantas con entrantes. En una T hay cuatro vértices alineados
                // (los dos brazos y las dos esquinas del pie), y en cuanto el recorte junta dos
                // de ellos aparece una oreja plana que PUENTEA por debajo del vástago. Aceptarla
                // pronto se lleva el vástago por delante sin triangularlo y deja un triángulo de
                // 50 m² sobre la muesca: suelo donde no hay sala. Dejándola para el final, el
                // vástago ya está resuelto y quitar ese vértice es inofensivo.
                bool clipped = ClipOnce(poly, triangles, idx, allowFlat: false)
                            || ClipOnce(poly, triangles, idx, allowFlat: true);
                if (!clipped) return false; // sin orejas: entrada no simple
            }

            if (idx.Count == 3)
            {
                triangles.Add(idx[0]); triangles.Add(idx[1]); triangles.Add(idx[2]);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Recorta UNA oreja si encuentra alguna valida. <paramref name="allowFlat"/> decide si se
        /// admiten las de area cero.
        /// </summary>
        private static bool ClipOnce(List<Vector2> poly, List<int> triangles, List<int> idx,
            bool allowFlat)
        {
            for (int i = 0; i < idx.Count; i++)
            {
                int ia = idx[(i - 1 + idx.Count) % idx.Count];
                int ib = idx[i];
                int ic = idx[(i + 1) % idx.Count];

                Vector2 a = poly[ia], b = poly[ib], c = poly[ic];
                float cross = Cross(b - a, c - a);
                if (cross < -Eps) continue;   // reflejo: no es oreja

                bool flat = cross <= Eps;
                if (flat != allowFlat) continue;

                // Una oreja PLANA se emite igualmente, no se descarta el vertice. El puente que
                // cose cada agujero duplica posiciones a proposito, y un lado recto subdividido
                // por sus boquetes tiene todos los vertices intermedios colineales: quitar el
                // punto sin emitir el triangulo lo borraria del borde del suelo mientras la pared
                // sigue partida ahi -- una T-junction. Un triangulo de area cero no dibuja nada y
                // deja el borde intacto.
                if (!flat)
                {
                    bool blocked = false;
                    for (int k = 0; k < idx.Count && !blocked; k++)
                    {
                        int ip = idx[k];
                        if (ip == ia || ip == ib || ip == ic) continue;
                        Vector2 p = poly[ip];
                        // Un vertice que COINCIDE con una esquina de la oreja no la invalida: el
                        // puente duplica posiciones, y contarlas como "dentro" invalidaba TODAS
                        // las orejas -- el recorte se rendia y el suelo salia sin recortar
                        // mientras el collider si abria el hueco.
                        if ((p - a).sqrMagnitude < SameSq) continue;
                        if ((p - b).sqrMagnitude < SameSq) continue;
                        if ((p - c).sqrMagnitude < SameSq) continue;
                        if (StrictlyInside(p, a, b, c)) blocked = true;
                    }
                    if (blocked) continue;
                }

                triangles.Add(ia); triangles.Add(ib); triangles.Add(ic);
                idx.RemoveAt(i);
                return true;
            }
            return false;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        /// <summary>Estrictamente dentro: un punto EN el borde no bloquea la oreja. Con puentes
        /// hay vértices sobre las aristas por construcción.</summary>
        private static bool StrictlyInside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(b - a, p - a);
            float d2 = Cross(c - b, p - b);
            float d3 = Cross(a - c, p - c);
            return (d1 > Eps && d2 > Eps && d3 > Eps) || (d1 < -Eps && d2 < -Eps && d3 < -Eps);
        }
    }
}
