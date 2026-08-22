using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Recortar un polígono contra la planta de una sala, y saber a qué altura queda cada punto
    /// del resultado.
    ///
    /// El recorte va contra un TRIÁNGULO cada vez, no contra la planta entera, y eso es lo que lo
    /// hace aguantar plantas en L o en U. Sutherland–Hodgman —el recorte de toda la vida, veinte
    /// líneas— solo vale si la región contra la que recortas es CONVEXA; una planta en L no lo es
    /// y devuelve basura. Pero una planta en L SÍ se parte en triángulos, y un triángulo siempre es
    /// convexo. Así que se recorta contra cada triángulo de la planta ya triangulada
    /// (<see cref="PolygonTriangulator"/>, que ya está en el proyecto) y se quedan todos los trozos.
    ///
    /// Los trozos salen disjuntos y pegados entre sí, porque los triángulos de una triangulación
    /// no se solapan y cubren la planta entera. Eso significa que no hay que unirlos: cada uno se
    /// emite como su propio sólido cerrado, y la suma de sólidos cerrados sigue siendo una malla
    /// cerrada. Las caras que quedan entre trozo y trozo caen DENTRO del volumen, donde no se ven.
    ///
    /// La alternativa —un clipper de polígonos general, Greiner–Hormann y compañía— es bastante más
    /// código y bastante más sitio donde fallar con los casos degenerados (vértices que caen justo
    /// sobre una arista, aristas colineales), que aquí son el pan de cada día porque el usuario
    /// arrastra los puntos con el ratón y los pega a las paredes a propósito.
    /// </summary>
    public static class PolygonClipper
    {
        /// <summary>Por debajo de esto, dos puntos del resultado son el mismo y uno sobra: un
        /// polígono con vértices repetidos rompe la triangulación de después.</summary>
        private const float Weld = 1e-4f;

        /// <summary>Área por debajo de la cual un trozo no es geometría, es ruido de recorte —
        /// una astilla de milímetros que solo aportaría caras invisibles.</summary>
        private const float MinPieceArea = 1e-3f;

        private static readonly List<Vector2> _in = new List<Vector2>();
        private static readonly List<Vector2> _out = new List<Vector2>();

        /// <summary>
        /// Los trozos de <paramref name="poly"/> que caen dentro de <paramref name="region"/>.
        ///
        /// Devuelve una lista de anillos: uno solo si la región es convexa y el polígono cabe
        /// entero, y varios si la planta es en L y el polígono la cruza.
        /// </summary>
        public static bool Clip(IList<Vector2> poly, Vector2[] region, List<List<Vector2>> pieces)
        {
            pieces.Clear();
            if (poly == null || poly.Count < 3 || region == null || region.Length < 3) return false;

            var verts = new List<Vector2>();
            var idx = new List<int>();
            if (!PolygonTriangulator.Triangulate(region, null, verts, idx)) return false;

            for (int i = 0; i + 2 < idx.Count; i += 3)
            {
                var piece = ClipToTriangle(poly, verts[idx[i]], verts[idx[i + 1]], verts[idx[i + 2]]);
                if (piece == null) continue;

                // Y se comprueba contra el CONTORNO de verdad, no solo contra el triángulo.
                //
                // Recortar contra la triangulación da por hecho que los triángulos cubren la
                // planta y nada más. En una planta cóncava eso no siempre se cumple —la
                // triangulación por orejas puede sacar alguno que se salga del polígono, y en una
                // sala en L eso es justo el hueco de la L—, y sin esta comprobación la pieza
                // aparecería flotando donde no hay sala. Es barato y no depende de que el
                // triangulador se porte bien.
                if (!Inside(Centroid(piece), region)) continue;

                pieces.Add(piece);
            }
            return pieces.Count > 0;
        }

        /// <summary>
        /// Sutherland–Hodgman contra un triángulo: se recorta el polígono con cada uno de sus tres
        /// semiplanos, uno detrás de otro.
        /// </summary>
        private static List<Vector2> ClipToTriangle(IList<Vector2> poly, Vector2 a, Vector2 b, Vector2 c)
        {
            // El triángulo puede venir con cualquiera de los dos giros según cómo lo haya sacado la
            // triangulación. Los semiplanos se toman siempre hacia DENTRO, así que primero se
            // averigua el giro y luego se usa su signo.
            float turn = Cross(b - a, c - a);
            if (Mathf.Abs(turn) < 1e-9f) return null;      // triángulo degenerado: no encierra nada
            float side = Mathf.Sign(turn);

            _in.Clear();
            for (int i = 0; i < poly.Count; i++) _in.Add(poly[i]);

            if (!ClipHalfPlane(a, b, side)) return null;
            if (!ClipHalfPlane(b, c, side)) return null;
            if (!ClipHalfPlane(c, a, side)) return null;

            var result = new List<Vector2>(_in);
            Weld2(result);
            if (result.Count < 3 || Mathf.Abs(SignedArea(result)) < MinPieceArea) return null;
            return result;
        }

        /// <summary>Deja en <c>_in</c> la parte que cae al lado <paramref name="side"/> de la recta
        /// <paramref name="p"/>→<paramref name="q"/>. Devuelve false si no queda nada.</summary>
        private static bool ClipHalfPlane(Vector2 p, Vector2 q, float side)
        {
            _out.Clear();
            int n = _in.Count;
            if (n == 0) return false;

            for (int i = 0; i < n; i++)
            {
                Vector2 cur = _in[i], nxt = _in[(i + 1) % n];
                float dCur = Cross(q - p, cur - p) * side;
                float dNxt = Cross(q - p, nxt - p) * side;

                // El punto justo SOBRE la recta cuenta como dentro. Es el caso normal aquí, no el
                // raro: el usuario pega los puntos a las paredes a propósito, y tratarlo como fuera
                // dejaría el borde del trozo mordido un pelo hacia dentro.
                bool inCur = dCur >= -1e-6f;
                bool inNxt = dNxt >= -1e-6f;

                if (inCur) _out.Add(cur);
                if (inCur != inNxt)
                {
                    float t = dCur / (dCur - dNxt);
                    _out.Add(Snap(Vector2.Lerp(cur, nxt, Mathf.Clamp01(t))));
                }
            }

            _in.Clear();
            _in.AddRange(_out);
            return _in.Count >= 3;
        }

        /// <summary>
        /// La altura en un punto cualquiera de dentro del polígono, por coordenadas de valor medio
        /// (Floater).
        ///
        /// Hace falta porque al recortar aparecen vértices NUEVOS —las esquinas de la sala que caen
        /// dentro del polígono— y a esos hay que darles una altura que case con la que el autor
        /// puso en los suyos. Interpolar solo por la arista no vale: esas esquinas están en el
        /// interior, no sobre un lado.
        ///
        /// Valor medio y no el inverso de la distancia: reproduce EXACTAMENTE la altura en los
        /// vértices y varía linealmente sobre las aristas, así que dos trozos que compartan borde
        /// coinciden en altura y no queda un escalón entre ellos. El inverso de la distancia no
        /// cumple ninguna de las dos y dejaría costuras.
        /// </summary>
        public static float HeightAt(Vector2 p, IList<Vector2> poly, IList<float> heights)
        {
            int n = poly.Count;
            if (n == 0) return 0f;
            if (n == 1) return heights[0];

            // Sobre el borde se resuelve por la arista y no por valor medio. No es un atajo: en el
            // borde la fórmula de valor medio se indetermina (los dos pesos de esa arista se
            // disparan a la vez), y en coma flotante no sale el límite, sale ruido — daba 1,67
            // donde tocaba 1. Y es el caso NORMAL aquí, no el raro: los vértices que inventa el
            // recorte caen justo sobre las aristas del polígono original.
            int edge = EdgeUnder(p, poly, out float along);
            if (edge >= 0) return Mathf.Lerp(heights[edge], heights[(edge + 1) % n], along);

            float wSum = 0f, acc = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector2 cur = poly[i];
                Vector2 prev = poly[(i - 1 + n) % n];
                Vector2 nxt = poly[(i + 1) % n];

                float d = Vector2.Distance(p, cur);
                if (d < Weld) return heights[i];              // justo encima de un vértice

                float w = (TanHalf(p, prev, cur) + TanHalf(p, cur, nxt)) / d;
                wSum += w;
                acc += w * heights[i];
            }

            // Sobre una arista, los dos pesos que quedan se disparan a la vez y la suma se va a
            // cero por redondeo. Ahí la respuesta correcta es la interpolación sobre esa arista,
            // que es justo lo que da el límite — pero calculado a mano, no por el cociente.
            if (Mathf.Abs(wSum) < 1e-9f) return OnEdgeHeight(p, poly, heights);
            return acc / wSum;
        }

        /// <summary>La arista sobre la que se apoya <paramref name="p"/>, o -1 si está en el
        /// interior. <paramref name="along"/> sale con la fracción recorrida sobre esa arista.</summary>
        private static int EdgeUnder(Vector2 p, IList<Vector2> poly, out float along)
        {
            along = 0f;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % n];
                Vector2 ab = b - a;
                float len2 = ab.sqrMagnitude;
                if (len2 < 1e-12f) continue;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
                if (Vector2.Distance(p, Vector2.Lerp(a, b, t)) <= OnEdge)
                {
                    along = t;
                    return i;
                }
            }
            return -1;
        }

        /// <summary>A qué distancia de una arista se considera que un punto está SOBRE ella. Tiene
        /// que ser mayor que el error del recorte, que es quien deja los puntos ahí.</summary>
        private const float OnEdge = 1e-3f;

        /// <summary>La tangente del medio ángulo que forman dos vértices vistos desde
        /// <paramref name="p"/>. Es la pieza de las coordenadas de valor medio.</summary>
        private static float TanHalf(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 u = a - p, v = b - p;
            float lu = u.magnitude, lv = v.magnitude;
            if (lu < Weld || lv < Weld) return 0f;
            float cross = Cross(u, v), dot = Vector2.Dot(u, v);
            float denom = lu * lv + dot;
            if (Mathf.Abs(denom) < 1e-9f) return 0f;          // opuestos: sobre la arista
            return cross / denom;
        }

        /// <summary>Altura sobre la arista más cercana, interpolada entre sus dos extremos. Es la
        /// salida para cuando el punto cae justo sobre un lado.</summary>
        private static float OnEdgeHeight(Vector2 p, IList<Vector2> poly, IList<float> heights)
        {
            int n = poly.Count;
            float best = float.MaxValue, y = heights[0];
            for (int i = 0; i < n; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % n];
                Vector2 ab = b - a;
                float len2 = ab.sqrMagnitude;
                float t = len2 < 1e-9f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
                float d = Vector2.Distance(p, Vector2.Lerp(a, b, t));
                if (d < best)
                {
                    best = d;
                    y = Mathf.Lerp(heights[i], heights[(i + 1) % n], t);
                }
            }
            return y;
        }

        /// <summary>Quita vértices repetidos y los que quedan en línea recta entre sus vecinos:
        /// el recorte los produce a puñados y la triangulación de después no los tolera.</summary>
        private static void Weld2(List<Vector2> ring)
        {
            for (int i = ring.Count - 1; i >= 0 && ring.Count > 3; i--)
            {
                Vector2 cur = ring[i], nxt = ring[(i + 1) % ring.Count];
                if ((cur - nxt).sqrMagnitude < Weld * Weld) ring.RemoveAt(i);
            }
        }

        /// <summary>El centro de masas del anillo por área, no la media de sus vértices: en un
        /// trozo con vértices apelotonados en un lado, la media se va con ellos y puede acabar
        /// fuera de la propia figura.</summary>
        private static Vector2 Centroid(IList<Vector2> ring)
        {
            float a2 = 0f;
            Vector2 acc = Vector2.zero;
            for (int i = 0; i < ring.Count; i++)
            {
                Vector2 p = ring[i], q = ring[(i + 1) % ring.Count];
                float cross = p.x * q.y - q.x * p.y;
                a2 += cross;
                acc += (p + q) * cross;
            }
            if (Mathf.Abs(a2) < 1e-9f)
            {
                // Degenerado: sin área no hay centro de masas, y la media es lo mejor que hay.
                Vector2 mean = Vector2.zero;
                for (int i = 0; i < ring.Count; i++) mean += ring[i];
                return mean / Mathf.Max(1, ring.Count);
            }
            return acc / (3f * a2);
        }

        /// <summary>Punto dentro de un polígono, por cruces de rayo. Vale para plantas cóncavas,
        /// que es justo el caso que hay que aguantar.</summary>
        private static bool Inside(Vector2 p, Vector2[] poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                Vector2 a = poly[i], b = poly[j];
                if (a.y > p.y != b.y > p.y
                    && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        public static float SignedArea(IList<Vector2> ring)
        {
            float s = 0f;
            for (int i = 0; i < ring.Count; i++)
            {
                Vector2 a = ring[i], b = ring[(i + 1) % ring.Count];
                s += a.x * b.y - b.x * a.y;
            }
            return s * 0.5f;
        }

        /// <summary>
        /// Redondea a una rejilla de décimas de milímetro.
        ///
        /// No es cosmética: dos trozos vecinos calculan los puntos de su costura común por caminos
        /// distintos —cada uno recorta contra su propio triángulo— y el redondeo de la coma
        /// flotante los deja casi iguales pero no iguales. Casi no vale: la tapa de un trozo y la
        /// del vecino tienen que caer EXACTAMENTE en el mismo sitio para que la arista se use dos
        /// veces y no tres, que es lo que separa una malla cerrada de una con costuras.
        ///
        /// La rejilla es mucho más fina que cualquier cosa que se vea (0,1 mm) y mucho más gruesa
        /// que el error que hay que absorber.
        /// </summary>
        public static Vector2 Snap(Vector2 p) =>
            new Vector2(Mathf.Round(p.x * SnapGrid) / SnapGrid, Mathf.Round(p.y * SnapGrid) / SnapGrid);

        private const float SnapGrid = 10000f;

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    }
}
