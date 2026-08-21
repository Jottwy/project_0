using System;
using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.GridWorld
{
    /// <summary>
    /// El trazado de un laberinto sobre una rejilla, y NADA más: entra qué celdas existen, sale
    /// por dónde se pasa de una a otra. Ni metros, ni bloques, ni sala — eso lo pone quien llama.
    ///
    /// Vive en la assembly de runtime y no junto a la ventana de autorado por una razón práctica:
    /// <c>Assets/Editor</c> cae en Assembly-CSharp-Editor, que los tests EditMode no pueden
    /// referenciar. Aquí sí, y un laberinto sin test es un laberinto del que nadie sabe si deja
    /// trozos incomunicados.
    /// </summary>
    public static class MazeCarver
    {
        /// <summary>
        /// Backtracker recursivo (iterativo, con pila). Se eligió frente a Prim por una razón
        /// concreta: el backtracker hace pasillos largos y con recodos, que es lo que se recorre
        /// como laberinto; Prim deja una maraña de bifurcaciones cortas que se lee como ruido.
        ///
        /// Las celdas que <paramref name="open"/> marca como fuera no entran en el grafo, así que
        /// una planta en L da un laberinto en L. Los grupos de celdas que la geometría deja
        /// incomunicados se trazan cada uno por su cuenta: recortar un rectángulo a posteriori
        /// dejaría trozos macizos, y un trozo macizo dentro de una sala es espacio muerto.
        /// </summary>
        /// <param name="open">[x, z]: true = la celda existe y hay que trazarla.</param>
        /// <param name="linkX">Salida. [x, z] = se pasa entre (x,z) y (x+1,z).</param>
        /// <param name="linkZ">Salida. [x, z] = se pasa entre (x,z) y (x,z+1).</param>
        /// <param name="braid">Fracción de tabiques interiores que se tira DESPUÉS de trazar.
        /// 0 = árbol perfecto, un solo camino entre dos celdas cualesquiera. Subirlo abre atajos:
        /// un laberinto perfecto se recorre a ciegas pegado a una pared, y eso se nota.</param>
        public static void Carve(bool[,] open, bool[,] linkX, bool[,] linkZ,
            System.Random rng, float braid = 0f)
        {
            if (open == null || linkX == null || linkZ == null || rng == null)
                throw new ArgumentNullException(nameof(open));

            int nx = open.GetLength(0), nz = open.GetLength(1);
            var seen = new bool[nx, nz];
            var stack = new List<Vector2Int>();
            var candidates = new List<int>(4);

            for (int cz = 0; cz < nz; cz++)
                for (int cx = 0; cx < nx; cx++)
                {
                    if (!open[cx, cz] || seen[cx, cz]) continue;
                    seen[cx, cz] = true;
                    stack.Add(new Vector2Int(cx, cz));
                    CarveFrom(stack, open, seen, linkX, linkZ, nx, nz, rng, candidates);
                }

            if (braid <= 0.001f) return;
            for (int cz = 0; cz < nz; cz++)
                for (int cx = 0; cx < nx; cx++)
                {
                    if (cx + 1 < nx && open[cx, cz] && open[cx + 1, cz]
                        && !linkX[cx, cz] && rng.NextDouble() < braid) linkX[cx, cz] = true;
                    if (cz + 1 < nz && open[cx, cz] && open[cx, cz + 1]
                        && !linkZ[cx, cz] && rng.NextDouble() < braid) linkZ[cx, cz] = true;
                }
        }

        /// <summary>Un tabique en coordenadas de CELDA: centro y tamaño, con el grosor todavía sin
        /// poner — quien lo convierta a metros decide el grosor.</summary>
        public struct WallRun
        {
            /// <summary>Centro del tabique, en celdas desde la esquina (0,0) de la rejilla.</summary>
            public Vector2 center;

            /// <summary>Largo, en celdas. El grosor va en el otro eje y lo pone quien convierte.</summary>
            public float length;

            /// <summary>true = paralelo a Z (separa dos celdas vecinas en X).</summary>
            public bool alongZ;
        }

        /// <summary>
        /// Los tabiques que hacen falta: donde dos celdas vecinas EXISTEN las dos y no hay paso
        /// entre ellas. Los tramos seguidos salen FUNDIDOS en un solo tabique — sin fundir, una
        /// sala de 30 m con pasillo de 2,5 m pasa de los 200 elementos que admite un grupo del
        /// editor, y deja una lista imposible de repasar a mano.
        ///
        /// La frontera con el exterior NO sale: ahí ya está la pared de la sala, y doblarla dejaría
        /// un tabique clavado dentro del muro.
        /// </summary>
        public static void WallRuns(bool[,] open, bool[,] linkX, bool[,] linkZ, List<WallRun> into)
        {
            into.Clear();
            int nx = open.GetLength(0), nz = open.GetLength(1);
            Emit(into, open, linkX, linkZ, nx, nz, alongZ: true);
            Emit(into, open, linkX, linkZ, nx, nz, alongZ: false);
        }

        private static void Emit(List<WallRun> into, bool[,] open, bool[,] linkX, bool[,] linkZ,
            int nx, int nz, bool alongZ)
        {
            // Un tabique paralelo a Z vive en la frontera entre (a,b) y (a+1,b), y los tramos
            // seguidos se funden avanzando en b (que es Z). El otro eje es el caso espejado.
            int boundaries = alongZ ? nx - 1 : nz - 1;
            int span = alongZ ? nz : nx;

            for (int a = 0; a < boundaries; a++)
            {
                int runStart = -1;
                for (int b = 0; b <= span; b++)
                {
                    bool wall = b < span && (alongZ
                        ? open[a, b] && open[a + 1, b] && !linkX[a, b]
                        : open[b, a] && open[b, a + 1] && !linkZ[b, a]);

                    if (wall && runStart < 0) runStart = b;
                    if (wall || runStart < 0) continue;

                    float len = b - runStart;
                    float mid = runStart + len * 0.5f;
                    float edge = a + 1;
                    into.Add(new WallRun
                    {
                        center = alongZ ? new Vector2(edge, mid) : new Vector2(mid, edge),
                        length = len,
                        alongZ = alongZ,
                    });
                    runStart = -1;
                }
            }
        }

        private static void CarveFrom(List<Vector2Int> stack, bool[,] open, bool[,] seen,
            bool[,] linkX, bool[,] linkZ, int nx, int nz, System.Random rng, List<int> candidates)
        {
            while (stack.Count > 0)
            {
                var c = stack[stack.Count - 1];
                candidates.Clear();
                if (c.x + 1 < nx && open[c.x + 1, c.y] && !seen[c.x + 1, c.y]) candidates.Add(0);
                if (c.x - 1 >= 0 && open[c.x - 1, c.y] && !seen[c.x - 1, c.y]) candidates.Add(1);
                if (c.y + 1 < nz && open[c.x, c.y + 1] && !seen[c.x, c.y + 1]) candidates.Add(2);
                if (c.y - 1 >= 0 && open[c.x, c.y - 1] && !seen[c.x, c.y - 1]) candidates.Add(3);

                if (candidates.Count == 0) { stack.RemoveAt(stack.Count - 1); continue; }

                int dir = candidates[rng.Next(candidates.Count)];
                Vector2Int next = dir switch
                {
                    0 => new Vector2Int(c.x + 1, c.y),
                    1 => new Vector2Int(c.x - 1, c.y),
                    2 => new Vector2Int(c.x, c.y + 1),
                    _ => new Vector2Int(c.x, c.y - 1),
                };
                if (dir <= 1) linkX[Mathf.Min(c.x, next.x), c.y] = true;
                else linkZ[c.x, Mathf.Min(c.y, next.y)] = true;

                seen[next.x, next.y] = true;
                stack.Add(next);
            }
        }
    }
}
