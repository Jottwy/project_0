using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// Qué huecos de una tapa se pueden abrir de verdad, decidido UNA vez y consumido por la malla
    /// y por la colisión.
    ///
    /// Existe por el modo de fallo que dejaba salas inservibles: <see cref="PolygonTriangulator"/>
    /// es todo o nada, así que UN hueco imposible de coser —una escalera pegada a la pared, dos
    /// escaleras solapadas, un pozo medio fuera de la planta— tumbaba la tapa ENTERA. El respaldo
    /// de abanico dibujaba entonces el suelo sin NINGÚN hueco mientras los colliders sí los abrían.
    /// De ahí las dos caras del mismo bug: "no se puede guardar la sala" y "a veces la escalera no
    /// hace agujero" — nunca era esa escalera, era que la tapa se rendía y se los llevaba a todos.
    ///
    /// La regla aquí es que un hueco que no se puede abrir se queda CERRADO, en los dos sitios. Eso
    /// es un problema de contenido —la escalera choca contra una losa maciza— y se avisa; lo que no
    /// puede pasar es que la malla y la colisión discrepen, que es un problema de motor y se cuela
    /// hasta que alguien se cae por un suelo sólido.
    ///
    /// El criterio de aceptación es EL PROPIO TRIANGULADOR, no una heurística que lo imite: se
    /// añaden los huecos de uno en uno y se conserva el que deja el conjunto triangulable. Es
    /// O(n²) triangulaciones, pero n son los huecos de una losa —cuatro o cinco— y a cambio no hay
    /// forma de que lo que este archivo acepta y lo que el triangulador aguanta se separen.
    /// </summary>
    public static class RoomHoleSet
    {
        /// <summary>Por debajo de esto un hueco no es un hueco, es ruido numérico.</summary>
        private const float MinHoleArea = 1e-4f;

        private const float Eps = 1e-7f;

        // Reutilizados entre llamadas: esto corre en el bucle de horneado de cada losa.
        private static readonly List<Vector2> _triVerts = new List<Vector2>();
        private static readonly List<int> _triIdx = new List<int>();
        private static readonly List<IList<Vector2>> _trial = new List<IList<Vector2>>();

        /// <summary>
        /// Los índices de <paramref name="candidates"/> que se pueden abrir a la vez dentro de
        /// <paramref name="contour"/>. <paramref name="accepted"/> sale en orden creciente;
        /// <paramref name="rejected"/> recoge el resto para que el llamador pueda avisar de CUÁL
        /// se ha quedado cerrado en vez de un "algo falló".
        /// </summary>
        public static void Accept(Vector2[] contour, IList<Vector2[]> candidates,
            List<int> accepted, List<int> rejected)
        {
            accepted.Clear();
            rejected.Clear();
            if (candidates == null || candidates.Count == 0) return;
            if (contour == null || contour.Length < 3)
            {
                for (int i = 0; i < candidates.Count; i++) rejected.Add(i);
                return;
            }

            _trial.Clear();
            for (int i = 0; i < candidates.Count; i++)
            {
                var cand = candidates[i];

                // Descartes baratos primero. No son redundantes con la prueba del triangulador:
                // le ahorran la mayoría de los casos y, sobre todo, un hueco que asoma fuera de la
                // planta a veces SÍ se deja coser y produce suelo donde no hay sala — el
                // triangulador no puede distinguirlo, esto sí.
                if (!IsSaneRing(cand)) { rejected.Add(i); continue; }
                if (!RingInside(cand, contour)) { rejected.Add(i); continue; }
                if (OverlapsAny(cand, candidates, accepted)) { rejected.Add(i); continue; }

                _trial.Add(cand);
                if (PolygonTriangulator.Triangulate(contour, _trial, _triVerts, _triIdx))
                {
                    accepted.Add(i);
                }
                else
                {
                    _trial.RemoveAt(_trial.Count - 1);
                    rejected.Add(i);
                }
            }
        }

        /// <summary>Se queda con los elementos de <paramref name="src"/> cuyo índice está en
        /// <paramref name="keep"/>, en el mismo orden. <paramref name="keep"/> tiene que venir
        /// creciente, que es como lo devuelve <see cref="Accept"/>.</summary>
        public static void Filter<T>(List<T> src, List<int> keep)
        {
            int w = 0;
            for (int k = 0; k < keep.Count; k++) src[w++] = src[keep[k]];
            src.RemoveRange(w, src.Count - w);
        }

        /// <summary>Interseca dos conjuntos de índices ya crecientes.</summary>
        public static void Intersect(List<int> a, List<int> b, List<int> into)
        {
            into.Clear();
            int i = 0, j = 0;
            while (i < a.Count && j < b.Count)
            {
                if (a[i] == b[j]) { into.Add(a[i]); i++; j++; }
                else if (a[i] < b[j]) i++;
                else j++;
            }
        }

        private static bool IsSaneRing(Vector2[] ring)
        {
            if (ring == null || ring.Length < 3) return false;
            return Mathf.Abs(SignedArea(ring)) >= MinHoleArea;
        }

        /// <summary>¿El anillo entero cae dentro del contorno? Vértices dentro Y ninguna arista
        /// cruzando: un rectángulo puede tener las cuatro esquinas dentro de una planta en L y
        /// aun así atravesar la muesca por el medio.
        ///
        /// Pública porque también es la pregunta que decide si una columna, un bloque o un
        /// peldaño hay que RECORTARLOS contra la planta o se pueden emitir tal cual. Ese camino
        /// rápido importa: recortar por sistema partiría en trozos hasta lo que cabe de sobra,
        /// cambiando geometría que ya está validada.</summary>
        public static bool RingInside(Vector2[] ring, Vector2[] contour)
        {
            for (int i = 0; i < ring.Length; i++)
                if (!PointInside(ring[i], contour)) return false;

            for (int i = 0; i < ring.Length; i++)
            {
                Vector2 a = ring[i], b = ring[(i + 1) % ring.Length];
                for (int j = 0; j < contour.Length; j++)
                {
                    Vector2 c = contour[j], d = contour[(j + 1) % contour.Length];
                    if (SegmentsCross(a, b, c, d)) return false;
                }
            }
            return true;
        }

        private static bool OverlapsAny(Vector2[] ring, IList<Vector2[]> all, List<int> taken)
        {
            for (int k = 0; k < taken.Count; k++)
                if (RingsOverlap(ring, all[taken[k]])) return true;
            return false;
        }

        /// <summary>Dos anillos se estorban si se cruzan o si uno contiene al otro. TOCARSE por un
        /// borde también cuenta: dos huecos pegados dejan entre ellos un tabique de grosor cero
        /// que el puente del triangulador convierte en un polígono que se pisa a sí mismo.</summary>
        private static bool RingsOverlap(Vector2[] a, Vector2[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                Vector2 a0 = a[i], a1 = a[(i + 1) % a.Length];
                for (int j = 0; j < b.Length; j++)
                {
                    Vector2 b0 = b[j], b1 = b[(j + 1) % b.Length];
                    if (SegmentsTouchOrCross(a0, a1, b0, b1)) return true;
                }
            }
            // Sin cruces: o son ajenos, o uno está dentro del otro. Basta un punto de cada.
            return PointInside(a[0], b) || PointInside(b[0], a);
        }

        private static float SignedArea(Vector2[] p)
        {
            float s = 0f;
            for (int i = 0; i < p.Length; i++)
            {
                Vector2 c = p[i], n = p[(i + 1) % p.Length];
                s += c.x * n.y - n.x * c.y;
            }
            return s * 0.5f;
        }

        /// <summary>Cruce por el ruedo. Un punto EXACTAMENTE sobre el borde sale a cara o cruz, y
        /// se deja así a propósito: ese caso —una escalera apoyada en la pared— lo cazan la prueba
        /// de aristas de <see cref="RingInside"/> y, si se colara, la triangulación de prueba. No
        /// hay una tercera respuesta que dar aquí.</summary>
        private static bool PointInside(Vector2 p, Vector2[] poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                Vector2 a = poly[i], b = poly[j];
                if ((a.y > p.y) != (b.y > p.y)
                    && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        /// <summary>Cruce PROPIO, el mismo criterio que usa el triangulador: tocarse en un
        /// extremo es legítimo y frecuente.</summary>
        private static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float d1 = Cross(b - a, c - a), d2 = Cross(b - a, d - a);
            float d3 = Cross(d - c, a - c), d4 = Cross(d - c, b - c);
            return ((d1 > Eps && d2 < -Eps) || (d1 < -Eps && d2 > Eps))
                && ((d3 > Eps && d4 < -Eps) || (d3 < -Eps && d4 > Eps));
        }

        /// <summary>Como <see cref="SegmentsCross"/> pero contando también el roce: entre dos
        /// HUECOS, apoyarse uno en otro ya es un problema.</summary>
        private static bool SegmentsTouchOrCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            if (SegmentsCross(a, b, c, d)) return true;
            return OnSegment(a, b, c) || OnSegment(a, b, d)
                || OnSegment(c, d, a) || OnSegment(c, d, b);
        }

        private static bool OnSegment(Vector2 a, Vector2 b, Vector2 p)
        {
            if (Mathf.Abs(Cross(b - a, p - a)) > Eps) return false;
            return Vector2.Dot(p - a, p - b) <= Eps;
        }
    }
}
