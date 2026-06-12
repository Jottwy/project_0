using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    [TestFixture]
    public class WallGreedyMesherTests
    {
        private const int Size = GridConstants.ChunkCells;

        private static GridCell Wall() => new GridCell(GridCellType.Wall, 0, 0);
        private static GridCell Corridor(byte ceiling = 2) => new GridCell(GridCellType.Corridor, ceiling, 0);

        private static int[] Heights(params (int x, int z, int h)[] walls)
        {
            var grid = new int[Size * Size];
            foreach (var (x, z, h) in walls)
                grid[z * Size + x] = h;
            return grid;
        }

        [Test]
        public void RowOfEightWallsCollapsesToOneRect()
        {
            var walls = new (int, int, int)[8];
            for (int i = 0; i < 8; i++)
                walls[i] = (3 + i, 5, 2);
            var rects = WallGreedyMesher.GreedyRects(Heights(walls), Size);

            Assert.AreEqual(1, rects.Count);
            Assert.AreEqual(3, rects[0].x);
            Assert.AreEqual(5, rects[0].z);
            Assert.AreEqual(8, rects[0].w);
            Assert.AreEqual(1, rects[0].d);
            Assert.AreEqual(2, rects[0].heightUnits);
        }

        [Test]
        public void SolidBlockCollapsesToOneRect()
        {
            var walls = new List<(int, int, int)>();
            for (int z = 2; z < 6; z++)
                for (int x = 10; x < 14; x++)
                    walls.Add((x, z, 4));
            var rects = WallGreedyMesher.GreedyRects(Heights(walls.ToArray()), Size);

            Assert.AreEqual(1, rects.Count);
            Assert.AreEqual(4, rects[0].w);
            Assert.AreEqual(4, rects[0].d);
        }

        [Test]
        public void DifferentHeightsDoNotMerge()
        {
            var rects = WallGreedyMesher.GreedyRects(
                Heights((0, 0, 2), (1, 0, 4)), Size);
            Assert.AreEqual(2, rects.Count);
        }

        [Test]
        public void RectsCoverMaskExactlyWithoutOverlap()
        {
            // Deterministic pseudo-random wall pattern with mixed heights.
            var heights = new int[Size * Size];
            uint rng = 12345;
            for (int i = 0; i < heights.Length; i++)
            {
                rng = rng * 1664525u + 1013904223u;
                if ((rng >> 16) % 3 == 0)
                    heights[i] = 2 + (int)((rng >> 8) % 3);
            }

            var rects = WallGreedyMesher.GreedyRects(heights, Size);

            var covered = new int[Size * Size];
            foreach (var r in rects)
            {
                Assert.Greater(r.heightUnits, 0);
                for (int dz = 0; dz < r.d; dz++)
                {
                    for (int dx = 0; dx < r.w; dx++)
                    {
                        int i = (r.z + dz) * Size + r.x + dx;
                        covered[i]++;
                        Assert.AreEqual(heights[i], r.heightUnits,
                            "rect height must match every covered cell");
                    }
                }
            }

            for (int i = 0; i < heights.Length; i++)
            {
                int expected = heights[i] > 0 ? 1 : 0;
                Assert.AreEqual(expected, covered[i],
                    $"cell {i}: walls covered exactly once, non-walls never");
            }

            // Greedy must beat naive one-rect-per-cell substantially.
            int wallCells = 0;
            foreach (int h in heights)
                if (h > 0) wallCells++;
            Assert.Less(rects.Count, wallCells,
                "greedy meshing must produce fewer rects than wall cells");
        }

        [Test]
        public void WallHeightComesFromTallestAdjacentWalkableCell()
        {
            var cells = new GridCell[Size * Size];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = Wall();
            cells[5 * Size + 4] = Corridor(2);
            cells[5 * Size + 6] = new GridCell(GridCellType.Open, 5, 1);

            var heights = WallGreedyMesher.ComputeWallHeights(cells, Size);

            // Wall between a 2-unit corridor and a 5-unit open zone reaches 5.
            Assert.AreEqual(5, heights[5 * Size + 5]);
            // Wall touching only the corridor reaches the corridor ceiling (min 2).
            Assert.AreEqual(2, heights[5 * Size + 3]);
            // Interior wall with no walkable neighbour inherits the chunk max.
            Assert.AreEqual(5, heights[0]);
            // Walkable cells get no wall height.
            Assert.AreEqual(0, heights[5 * Size + 4]);
        }

        [Test]
        public void BuiltMeshHasFiveQuadsPerRect()
        {
            var rects = WallGreedyMesher.GreedyRects(Heights((0, 0, 2), (5, 5, 4)), Size);
            var mesh = WallGreedyMesher.BuildMesh(rects, GridConstants.CellSize);

            // 5 quads (4 sides + top) × 4 verts × 2 rects.
            Assert.AreEqual(40, mesh.vertexCount);
            Assert.AreEqual(60, mesh.triangles.Length); // 5 quads × 2 tris × 3 idx × 2 rects

            // Tallest point = heightUnits × cellSize.
            Assert.AreEqual(4 * 2.5f, mesh.bounds.max.y, 0.001f);
            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
}
