using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Lo único que de verdad hay que garantizar de un laberinto: que se puede recorrer entero.
    /// Un trozo incomunicado dentro de una sala autorada es espacio muerto que nadie ve hasta
    /// jugarla, y no lo delata ni la malla ni los colliders.
    /// </summary>
    [TestFixture]
    public class MazeCarverTests
    {
        private static bool[,] FullGrid(int nx, int nz)
        {
            var open = new bool[nx, nz];
            for (int z = 0; z < nz; z++)
                for (int x = 0; x < nx; x++) open[x, z] = true;
            return open;
        }

        /// <summary>Celdas alcanzables desde la primera abierta, siguiendo solo los pasos.</summary>
        private static int Reachable(bool[,] open, bool[,] linkX, bool[,] linkZ)
        {
            int nx = open.GetLength(0), nz = open.GetLength(1);
            var seen = new bool[nx, nz];
            var queue = new Queue<Vector2Int>();

            for (int z = 0; z < nz && queue.Count == 0; z++)
                for (int x = 0; x < nx && queue.Count == 0; x++)
                    if (open[x, z]) { seen[x, z] = true; queue.Enqueue(new Vector2Int(x, z)); }

            int count = 0;
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                count++;
                void Visit(int x, int z)
                {
                    if (x < 0 || z < 0 || x >= nx || z >= nz || seen[x, z] || !open[x, z]) return;
                    seen[x, z] = true;
                    queue.Enqueue(new Vector2Int(x, z));
                }
                if (c.x < nx - 1 && linkX[c.x, c.y]) Visit(c.x + 1, c.y);
                if (c.x > 0 && linkX[c.x - 1, c.y]) Visit(c.x - 1, c.y);
                if (c.y < nz - 1 && linkZ[c.x, c.y]) Visit(c.x, c.y + 1);
                if (c.y > 0 && linkZ[c.x, c.y - 1]) Visit(c.x, c.y - 1);
            }
            return count;
        }

        private static (bool[,], bool[,]) Carve(bool[,] open, int seed, float braid = 0f)
        {
            int nx = open.GetLength(0), nz = open.GetLength(1);
            var linkX = new bool[nx, nz];
            var linkZ = new bool[nx, nz];
            MazeCarver.Carve(open, linkX, linkZ, new System.Random(seed), braid);
            return (linkX, linkZ);
        }

        [Test]
        public void EveryCellIsReachable([Values(1, 7, 12345)] int seed)
        {
            var open = FullGrid(12, 9);
            var (linkX, linkZ) = Carve(open, seed);
            Assert.AreEqual(12 * 9, Reachable(open, linkX, linkZ));
        }

        /// <summary>Un árbol perfecto sobre N celdas tiene EXACTAMENTE N−1 pasos. Menos = hay un
        /// trozo suelto; más = hay un bucle donde no debería, y con braid a 0 no debe haberlos.</summary>
        [Test]
        public void PerfectMazeHasNoLoops()
        {
            var open = FullGrid(10, 10);
            var (linkX, linkZ) = Carve(open, 42);

            int links = 0;
            for (int z = 0; z < 10; z++)
                for (int x = 0; x < 10; x++)
                {
                    if (x < 9 && linkX[x, z]) links++;
                    if (z < 9 && linkZ[x, z]) links++;
                }
            Assert.AreEqual(10 * 10 - 1, links);
        }

        /// <summary>Los atajos solo AÑADEN pasos: no pueden dejar el laberinto peor conectado.</summary>
        [Test]
        public void BraidingKeepsEveryCellReachable()
        {
            var open = FullGrid(11, 11);
            var (linkX, linkZ) = Carve(open, 3, braid: 0.4f);
            Assert.AreEqual(11 * 11, Reachable(open, linkX, linkZ));
        }

        /// <summary>Una planta en L: las celdas del hueco no existen y no pueden salir enlazadas,
        /// y las que sí existen tienen que quedar todas comunicadas entre ellas.</summary>
        [Test]
        public void LShapedFootprintIsFullyCarved()
        {
            var open = FullGrid(10, 10);
            int cut = 0;
            for (int z = 5; z < 10; z++)
                for (int x = 5; x < 10; x++) { open[x, z] = false; cut++; }

            var (linkX, linkZ) = Carve(open, 9);
            Assert.AreEqual(100 - cut, Reachable(open, linkX, linkZ));
        }

        /// <summary>Dos mitades separadas por una franja cerrada: cada una se traza entera por su
        /// cuenta. Sin esto, la mitad que no toca la celda semilla saldría maciza.</summary>
        [Test]
        public void DisconnectedIslandsAreEachCarved()
        {
            var open = FullGrid(9, 6);
            for (int z = 0; z < 6; z++) open[4, z] = false;

            var (linkX, linkZ) = Carve(open, 5);

            // El BFS solo alcanza su propia isla: 4 columnas × 6.
            Assert.AreEqual(4 * 6, Reachable(open, linkX, linkZ));

            // Y la otra isla no quedó maciza: tiene sus propios pasos.
            int rightLinks = 0;
            for (int z = 0; z < 6; z++)
                for (int x = 5; x < 9; x++)
                {
                    if (x < 8 && linkX[x, z]) rightLinks++;
                    if (z < 5 && linkZ[x, z]) rightLinks++;
                }
            Assert.AreEqual(4 * 6 - 1, rightLinks);
        }

        /// <summary>La conversión a tabiques tiene que decir EXACTAMENTE lo mismo que los pasos:
        /// tanto metro de tabique como fronteras interiores sin paso. De más = un pasillo tapiado;
        /// de menos = una pared que falta.</summary>
        [Test]
        public void WallRunsCoverEveryMissingLink()
        {
            var open = FullGrid(9, 7);
            var (linkX, linkZ) = Carve(open, 21);

            int missing = 0;
            for (int z = 0; z < 7; z++)
                for (int x = 0; x < 9; x++)
                {
                    if (x < 8 && !linkX[x, z]) missing++;
                    if (z < 6 && !linkZ[x, z]) missing++;
                }

            var runs = new List<MazeCarver.WallRun>();
            MazeCarver.WallRuns(open, linkX, linkZ, runs);

            float total = 0f;
            foreach (var w in runs) total += w.length;
            Assert.AreEqual(missing, Mathf.RoundToInt(total));
        }

        /// <summary>Los tramos seguidos salen fundidos, no celda a celda: sin esto el generador se
        /// come el tope de 200 elementos por grupo del editor.</summary>
        [Test]
        public void CollinearWallsAreMerged()
        {
            // Rejilla de 4×3 con TODOS los pasos abiertos salvo la frontera x=1|x=2, que queda
            // cerrada de arriba abajo: tiene que salir UN tabique de largo 3, no tres de largo 1.
            var open = FullGrid(4, 3);
            var linkX = new bool[4, 3];
            var linkZ = new bool[4, 3];
            for (int z = 0; z < 3; z++)
                for (int x = 0; x < 4; x++)
                {
                    if (x < 3 && x != 1) linkX[x, z] = true;
                    if (z < 2) linkZ[x, z] = true;
                }

            var runs = new List<MazeCarver.WallRun>();
            MazeCarver.WallRuns(open, linkX, linkZ, runs);

            Assert.AreEqual(1, runs.Count);
            Assert.IsTrue(runs[0].alongZ);
            Assert.AreEqual(3f, runs[0].length);
            Assert.AreEqual(2f, runs[0].center.x, 1e-4f);  // frontera entre x=1 y x=2
            Assert.AreEqual(1.5f, runs[0].center.y, 1e-4f); // centrado sobre las 3 celdas
        }

        /// <summary>La frontera con el exterior NO lleva tabique: ahí ya está la pared de la sala.
        /// Doblarla dejaría un tabique clavado dentro del muro.</summary>
        [Test]
        public void NoWallsAgainstClosedCells()
        {
            var open = FullGrid(6, 6);
            for (int z = 0; z < 6; z++) open[5, z] = false;

            var (linkX, linkZ) = Carve(open, 4);
            var runs = new List<MazeCarver.WallRun>();
            MazeCarver.WallRuns(open, linkX, linkZ, runs);

            foreach (var w in runs)
                Assert.Less(w.center.x, 5f, "un tabique en la frontera con celdas cerradas");
        }

        [Test]
        public void SameSeedGivesSameMaze()
        {
            var a = Carve(FullGrid(8, 8), 777);
            var b = Carve(FullGrid(8, 8), 777);
            for (int z = 0; z < 8; z++)
                for (int x = 0; x < 8; x++)
                {
                    Assert.AreEqual(a.Item1[x, z], b.Item1[x, z], $"linkX at {x},{z}");
                    Assert.AreEqual(a.Item2[x, z], b.Item2[x, z], $"linkZ at {x},{z}");
                }
        }
    }
}
