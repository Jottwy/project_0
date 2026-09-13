using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Mapping;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>P0.3b de MAPPING-PROTOTYPE: el rayo de la mirada en celdas. Sin UnityEngine: corre también headless.</summary>
    [TestFixture]
    public class MapVisionFanTests
    {
        private const double Cell = 0.5;

        private static (int x, int z, MapCellKind kind)[] Trace(double ox, double oz, double dx, double dz,
            double max, double hit, HashSet<long> seen = null)
        {
            int size = MapVisionFan.MaxCellsPerRay(Cell, max);
            var xs = new int[size];
            var zs = new int[size];
            var kinds = new MapCellKind[size];
            int count = MapVisionFan.Trace(Cell, ox, oz, dx, dz, max, hit, seen ?? new HashSet<long>(), xs, zs, kinds, 0);
            var result = new (int, int, MapCellKind)[count];
            for (int i = 0; i < count; i++) result[i] = (xs[i], zs[i], kinds[i]);
            return result;
        }

        [Test]
        public void RayStopsAtTheWallAndTheCellInFrontIsFloor()
        {
            // Desde el centro de la celda (0,0) hacia +X; la pared empieza en X = 5 m (celda 10): impacto a 4,75 m.
            var cells = Trace(0.25, 0.25, 1, 0, 20, 4.75);

            Assert.AreEqual(11, cells.Length);
            for (int i = 0; i < 10; i++)
            {
                Assert.AreEqual(i, cells[i].x);
                Assert.AreEqual(MapCellKind.Floor, cells[i].kind, $"celda {i}");
            }

            Assert.AreEqual(10, cells[10].x);
            Assert.AreEqual(MapCellKind.Wall, cells[10].kind);
        }

        [Test]
        public void WithoutHitTheRayIsFloorUpToItsDistance()
        {
            var cells = Trace(0.25, 0.25, 0, -1, 3, -1);

            Assert.AreEqual(7, cells.Length, "de 0,25 a -2,75 m: celdas 0 a -6");
            Assert.AreEqual(-6, cells[6].z);
            foreach (var cell in cells) Assert.AreEqual(MapCellKind.Floor, cell.kind);
        }

        [Test]
        public void DiagonalRayVisitsEdgeAdjacentCells()
        {
            var cells = Trace(0.1, 0.3, 1, 0.7, 6, -1);

            for (int i = 1; i < cells.Length; i++)
            {
                int step = System.Math.Abs(cells[i].x - cells[i - 1].x) + System.Math.Abs(cells[i].z - cells[i - 1].z);
                Assert.AreEqual(1, step, "sin saltar celdas ni cruzar por la esquina");
            }
        }

        [Test]
        public void CellsAlreadySeenInTheSampleAreNotRepeated()
        {
            var seen = new HashSet<long>();
            var first = Trace(0.25, 0.25, 1, 0, 3, -1, seen);
            var second = Trace(0.25, 0.25, 1, 0.01, 3, -1, seen);

            Assert.Greater(first.Length, 0);
            Assert.AreEqual(0, second.Length, "el mismo recorrido ya estaba escrito");
        }
    }
}
