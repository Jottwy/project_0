using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    [TestFixture]
    public class WallGreedyMesherTests
    {
        private const int Size = GridConstants.ChunkCells;
        private const float Cs = GridConstants.CellSize;
        private const float Inset = WallGreedyMesher.Inset;
        private const byte W = WallGreedyMesher.InsetWest;
        private const byte E = WallGreedyMesher.InsetEast;
        private const byte S = WallGreedyMesher.InsetSouth;
        private const byte N = WallGreedyMesher.InsetNorth;

        private static GridCell Wall() => new GridCell(GridCellType.Wall, 0, 0);
        private static GridCell Corridor(byte ceiling = 2) => new GridCell(GridCellType.Corridor, ceiling, 0);

        private static int[] Heights(params (int x, int z, int h)[] walls)
        {
            var grid = new int[Size * Size];
            foreach (var (x, z, h) in walls)
                grid[z * Size + x] = h;
            return grid;
        }

        /// <summary>Wall where height > 0, corridor elsewhere (for synthetic heights).</summary>
        private static GridCell[] CellsFromHeights(int[] heights)
        {
            var cells = new GridCell[heights.Length];
            for (int i = 0; i < heights.Length; i++)
                cells[i] = heights[i] > 0 ? Wall() : Corridor();
            return cells;
        }

        /// <summary>Corridor sea (ceiling 2) with Wall cells at the given coordinates.</summary>
        private static GridCell[] CorridorSeaWithWalls(params (int x, int z)[] walls)
        {
            var cells = new GridCell[Size * Size];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = Corridor();
            foreach (var (x, z) in walls)
                cells[z * Size + x] = Wall();
            return cells;
        }

        private static List<WallRect> Rects(GridCell[] cells)
        {
            int[] heights = WallGreedyMesher.ComputeWallHeights(cells, Size);
            byte[] insets = WallGreedyMesher.ComputeWallInsets(cells, Size);
            return WallGreedyMesher.GreedyRects(heights, insets, Size);
        }

        private static List<WallRect> Rects(int[] heights)
        {
            byte[] insets = WallGreedyMesher.ComputeWallInsets(CellsFromHeights(heights), Size);
            return WallGreedyMesher.GreedyRects(heights, insets, Size);
        }

        /// <summary>World-space footprint of a rect under the thin-wall inset rule.</summary>
        private static (float x0, float x1, float z0, float z1) ExtentsOf(WallRect r)
        {
            return (
                r.x * Cs + ((r.insets & W) != 0 ? Inset : 0f),
                (r.x + r.w) * Cs - ((r.insets & E) != 0 ? Inset : 0f),
                r.z * Cs + ((r.insets & S) != 0 ? Inset : 0f),
                (r.z + r.d) * Cs - ((r.insets & N) != 0 ? Inset : 0f));
        }

        [Test]
        public void RowOfEightWallsCollapsesToOneRect()
        {
            var walls = new (int, int)[8];
            for (int i = 0; i < 8; i++)
                walls[i] = (3 + i, 5);
            var rects = Rects(CorridorSeaWithWalls(walls));

            Assert.AreEqual(1, rects.Count);
            Assert.AreEqual(3, rects[0].x);
            Assert.AreEqual(5, rects[0].z);
            Assert.AreEqual(8, rects[0].w);
            Assert.AreEqual(1, rects[0].d);
            Assert.AreEqual(2, rects[0].heightUnits);
            // Surrounded by corridor on all sides: every face retracts.
            Assert.AreEqual(W | E | S | N, rects[0].insets);
        }

        [Test]
        public void SolidBlockPerimeterRetractsInteriorStaysFull()
        {
            var walls = new List<(int, int)>();
            for (int z = 2; z < 6; z++)
                for (int x = 10; x < 14; x++)
                    walls.Add((x, z));
            var rects = Rects(CorridorSeaWithWalls(walls.ToArray()));

            Assert.AreEqual(1, rects.Count);
            Assert.AreEqual(4, rects[0].w);
            Assert.AreEqual(4, rects[0].d);
            Assert.AreEqual(W | E | S | N, rects[0].insets);

            // Only the exposed perimeter retracts: 4 cells − 2 insets per axis.
            var (x0, x1, z0, z1) = ExtentsOf(rects[0]);
            Assert.AreEqual(4 * Cs - 2 * Inset, x1 - x0, 0.001f);
            Assert.AreEqual(4 * Cs - 2 * Inset, z1 - z0, 0.001f);
        }

        [Test]
        public void DifferentHeightsDoNotMerge()
        {
            var rects = Rects(Heights((0, 0, 2), (1, 0, 4)));
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

            var rects = Rects(heights);

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

            // Profile-aware merging is more restrictive than plain height
            // merging, so only require no inflation here; real compression is
            // asserted by RowOfEightWallsCollapsesToOneRect.
            int wallCells = 0;
            foreach (int h in heights)
                if (h > 0) wallCells++;
            Assert.LessOrEqual(rects.Count, wallCells);
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
        public void BuiltMeshHasFiveQuadsPerRectWithInsetExtents()
        {
            var rects = Rects(Heights((0, 0, 2), (5, 5, 4)));
            Assert.AreEqual(2, rects.Count);
            var mesh = WallGreedyMesher.BuildMesh(rects, Cs);

            // 5 quads (4 sides + top) × 4 verts × 2 rects.
            Assert.AreEqual(40, mesh.vertexCount);
            Assert.AreEqual(60, mesh.triangles.Length); // 5 quads × 2 tris × 3 idx × 2 rects

            // Tallest point = heightUnits × cellSize.
            Assert.AreEqual(4 * Cs, mesh.bounds.max.y, 0.001f);
            // (0,0) sits on the chunk border: west/south faces stay at 0.
            Assert.AreEqual(0f, mesh.bounds.min.x, 0.001f);
            Assert.AreEqual(0f, mesh.bounds.min.z, 0.001f);
            // (5,5) is fully surrounded: its east face retracts from x = 15.
            Assert.AreEqual(6 * Cs - Inset, mesh.bounds.max.x, 0.001f);
            UnityEngine.Object.DestroyImmediate(mesh);
        }

        [Test]
        public void PartitionBetweenCorridorsIsThinAndCentered()
        {
            var rects = Rects(CorridorSeaWithWalls((4, 5), (5, 5), (6, 5)));
            Assert.AreEqual(1, rects.Count);

            var mesh = WallGreedyMesher.BuildMesh(rects, Cs);
            Assert.AreEqual(WallGreedyMesher.WallThickness, mesh.bounds.size.z, 0.001f);
            Assert.AreEqual(5.5f * Cs, mesh.bounds.center.z, 0.001f); // centred in row z=5
            Assert.AreEqual(4 * Cs + Inset, mesh.bounds.min.x, 0.001f);
            Assert.AreEqual(7 * Cs - Inset, mesh.bounds.max.x, 0.001f);
            UnityEngine.Object.DestroyImmediate(mesh);
        }

        [Test]
        public void LCornerLeavesPostAndJoinsFlush()
        {
            // Horizontal run (3..5, z=5) + vertical run (x=5, z=6..7).
            var rects = Rects(CorridorSeaWithWalls((3, 5), (4, 5), (5, 5), (5, 6), (5, 7)));
            Assert.AreEqual(3, rects.Count);

            var corner = rects.Find(r => r.x == 5 && r.z == 5);
            var row = rects.Find(r => r.x == 3 && r.z == 5);
            var column = rects.Find(r => r.x == 5 && r.z == 6);
            Assert.AreEqual(E | S, corner.insets); // west/north closed toward the runs
            Assert.AreEqual(2, row.w);
            Assert.AreEqual(2, column.d);

            // Corner post: Cs − Inset per side.
            var c = ExtentsOf(corner);
            Assert.AreEqual(Cs - Inset, c.x1 - c.x0, 0.001f);
            Assert.AreEqual(Cs - Inset, c.z1 - c.z0, 0.001f);

            // Flush joints: row reaches the corner's west border, column its north.
            Assert.AreEqual(c.x0, ExtentsOf(row).x1, 0.001f);
            Assert.AreEqual(c.z1, ExtentsOf(column).z0, 0.001f);
        }

        [Test]
        public void ChunkBorderWallExtendsToSeam()
        {
            var rects = Rects(CorridorSeaWithWalls((0, 5)));
            Assert.AreEqual(1, rects.Count);

            // No west inset toward the unseen neighbour chunk: reach the seam.
            Assert.AreEqual(E | S | N, rects[0].insets);
            Assert.AreEqual(0f, ExtentsOf(rects[0]).x0, 0.001f);
        }
    }
}
