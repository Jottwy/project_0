using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    [TestFixture]
    public class WallGreedyMesherTests
    {
        private const int Size = GridConstants.ChunkCells;
        private const float Cs   = GridConstants.CellSize;
        private const float Inset = GridVisualConstants.WallInset;
        private const byte  W = WallGreedyMesher.InsetWest;
        private const byte  E = WallGreedyMesher.InsetEast;
        private const byte  S = WallGreedyMesher.InsetSouth;
        private const byte  N = WallGreedyMesher.InsetNorth;

        private static GridCell Wall()              => new GridCell(GridCellType.Wall,     0, 0);
        private static GridCell Corridor(byte h=2)  => new GridCell(GridCellType.Corridor, h, 0);

        /// <summary>Corridor sea with Wall cells at the given (x,z) coordinates.</summary>
        private static GridCell[] CorridorSeaWithWalls(params (int x, int z)[] walls)
        {
            var cells = new GridCell[Size * Size];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = Corridor();
            foreach (var (x, z) in walls)
                cells[z * Size + x] = Wall();
            return cells;
        }

        /// <summary>
        /// Compute insets for a single Wall cell at (wx, wz) in a corridor sea.
        /// Returns the inset byte for that cell.
        /// </summary>
        private static byte CellInset(GridCell[] cells, int wx, int wz)
        {
            byte[] insets = WallGreedyMesher.ComputeWallInsets(cells, Size);
            return insets[wz * Size + wx];
        }

        /// <summary>
        /// World-space footprint of a single Wall cell given its inset flags.
        /// </summary>
        private static (float x0, float x1, float z0, float z1) Extents(int cx, int cz, byte ins)
        {
            return (
                cx * Cs + ((ins & W) != 0 ? Inset : 0f),
                (cx + 1) * Cs - ((ins & E) != 0 ? Inset : 0f),
                cz * Cs + ((ins & S) != 0 ? Inset : 0f),
                (cz + 1) * Cs - ((ins & N) != 0 ? Inset : 0f));
        }

        // ------------------------------------------------------------------ //
        // Geometry / inset tests (no UnityEngine.Mesh needed)                 //
        // ------------------------------------------------------------------ //

        [Test]
        public void SingleWallCell_Corridor_Both_Sides()
        {
            // Wall at (0,0) with corridor neighbours on east and north only
            // (west and south are chunk border → no inset).
            // Use a wall that has corridors on ALL in-chunk sides by placing
            // it somewhere interior.
            var cells = CorridorSeaWithWalls((5, 5));
            byte ins = CellInset(cells, 5, 5);

            // All 4 neighbours are corridor → all 4 sides retract.
            Assert.AreEqual(W | E | S | N, ins);

            var (x0, x1, z0, z1) = Extents(5, 5, ins);

            // Thickness in both axes = WallThickness.
            Assert.AreEqual(GridVisualConstants.WallThickness, x1 - x0, 0.001f);
            Assert.AreEqual(GridVisualConstants.WallThickness, z1 - z0, 0.001f);

            // Centred in the cell.
            Assert.AreEqual(5.5f * Cs, (x0 + x1) / 2f, 0.001f);
            Assert.AreEqual(5.5f * Cs, (z0 + z1) / 2f, 0.001f);
        }

        [Test]
        public void SingleWallCell_ChunkBorder_NoInset()
        {
            // Wall at (0, 5): west border of chunk.  East neighbour is corridor.
            var cells = CorridorSeaWithWalls((0, 5));
            byte ins = CellInset(cells, 0, 5);

            // West is chunk border → no west inset.
            Assert.AreEqual(0, ins & W, "west side at chunk border must NOT retract");
            // East neighbour is corridor → east retracts.
            Assert.AreEqual(E, ins & E, "east side toward corridor must retract");

            var (x0, x1, _, _) = Extents(0, 5, ins);
            Assert.AreEqual(0f, x0, 0.001f,  "west face must reach the chunk seam");
            Assert.AreEqual(Cs - Inset, x1, 0.001f, "east face must retract by Inset");
        }

        [Test]
        public void CornerL_TwoExposedSides()
        {
            // Wall at (5,5) with walls to its east (6,5) and north (5,6),
            // so only west and south are exposed to corridor.
            var cells = CorridorSeaWithWalls((5, 5), (6, 5), (5, 6));
            byte ins = CellInset(cells, 5, 5);

            // West and south face corridor → retract; east and north face wall → no retract.
            Assert.AreEqual(W | S, ins);

            var (x0, x1, z0, z1) = Extents(5, 5, ins);

            // The pillar footprint is (Cs - Inset) on the exposed axes.
            Assert.AreEqual(Cs - Inset, x1 - x0, 0.001f);
            Assert.AreEqual(Cs - Inset, z1 - z0, 0.001f);

            // The corner cell's non-inset edges must be flush with the
            // neighbouring wall cell extents (no gap).
            byte insEast  = CellInset(cells, 6, 5);
            byte insNorth = CellInset(cells, 5, 6);
            var (ex0, _, _, _) = Extents(6, 5, insEast);
            var (_, _, nz0, _) = Extents(5, 6, insNorth);

            Assert.AreEqual(x1, ex0, 0.001f, "corner east edge must be flush with east-cell west edge");
            Assert.AreEqual(z1, nz0, 0.001f, "corner north edge must be flush with north-cell south edge");
        }

        [Test]
        public void SolidBlock_OnlyPerimeterInset()
        {
            // 3×3 block of walls at (10..12, 10..12).
            var wallCoords = new List<(int, int)>();
            for (int z = 10; z <= 12; z++)
                for (int x = 10; x <= 12; x++)
                    wallCoords.Add((x, z));
            var cells = CorridorSeaWithWalls(wallCoords.ToArray());

            byte[] insets = WallGreedyMesher.ComputeWallInsets(cells, Size);

            // Interior cell (11,11): all 4 neighbours are Wall → no insets.
            Assert.AreEqual(0, insets[11 * Size + 11], "interior cell must have no insets");

            // Corner cell (10,10): west + south expose to corridor.
            Assert.AreEqual(W | S, insets[10 * Size + 10]);
            // Corner cell (12,12): east + north expose to corridor.
            Assert.AreEqual(E | N, insets[12 * Size + 12]);

            // Every cell is either interior (inset=0) or perimeter (inset≠0 toward outside).
            // Verify no cell has an inset toward a Wall neighbour.
            for (int z = 10; z <= 12; z++)
            {
                for (int x = 10; x <= 12; x++)
                {
                    byte ins = insets[z * Size + x];
                    if (x > 10) Assert.AreEqual(0, ins & W, $"cell ({x},{z}) must not retract west toward wall");
                    if (x < 12) Assert.AreEqual(0, ins & E, $"cell ({x},{z}) must not retract east toward wall");
                    if (z > 10) Assert.AreEqual(0, ins & S, $"cell ({x},{z}) must not retract south toward wall");
                    if (z < 12) Assert.AreEqual(0, ins & N, $"cell ({x},{z}) must not retract north toward wall");
                }
            }
        }

        [Test]
        public void WallHeight_ComesFromTallestAdjacent()
        {
            var cells = new GridCell[Size * Size];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = Wall();
            cells[5 * Size + 4] = Corridor(2);
            cells[5 * Size + 6] = new GridCell(GridCellType.Open, 5, 1);

            int[] heights = WallGreedyMesher.ComputeWallHeights(cells, Size);

            Assert.AreEqual(2, heights[5 * Size + 5], "every wall cell is a fixed 2 units");
            Assert.AreEqual(2, heights[5 * Size + 3], "wall touching only h=2 corridor must reach 2");
            Assert.AreEqual(2, heights[0],             "interior wall is also a fixed 2 units");
            Assert.AreEqual(0, heights[5 * Size + 4],  "walkable cells must not get a wall height");
        }

        // ------------------------------------------------------------------ //
        // Mesh tests (require UnityEngine — run in Unity Test Runner only)    //
        // ------------------------------------------------------------------ //

        [Test]
        public void TwoAdjacentWalls_FuseIntoFiveMeters()
        {
            // Run of 6 walls along x at z=5: pairs (4,5) (6,7) (8,9) fuse.
            var cells = CorridorSeaWithWalls((4, 5), (5, 5), (6, 5), (7, 5), (8, 5), (9, 5));
            int[] heights = WallGreedyMesher.ComputeWallHeights(cells, Size);
            byte[] insets  = WallGreedyMesher.ComputeWallInsets(cells, Size);

            var mesh = WallGreedyMesher.BuildChunkMesh(cells, Size, heights, insets);

            // 6 cells → 3 fused boxes: half the quads of per-cell meshing.
            Assert.AreEqual(3 * 24, mesh.GetTriangles(0).Length,
                "6 fused wall cells must emit 3 boxes of side quads");
            Assert.AreEqual(3 * 6, mesh.GetTriangles(1).Length,
                "6 fused wall cells must emit 3 top caps");

            // Middle box = vertices 20..39 (20 per box, emitted in x order).
            // Both its x ends are flush to neighbour walls → exactly 5 m.
            var verts = mesh.vertices;
            float minX = float.MaxValue, maxX = float.MinValue, maxY = 0f;
            for (int v = 20; v < 40; v++)
            {
                if (verts[v].x < minX) minX = verts[v].x;
                if (verts[v].x > maxX) maxX = verts[v].x;
                if (verts[v].y > maxY) maxY = verts[v].y;
            }
            Assert.AreEqual(6 * Cs, minX, 0.001f, "middle box must start at cell 6");
            Assert.AreEqual(8 * Cs, maxX, 0.001f, "middle box must end at cell 8");
            Assert.AreEqual(GridVisualConstants.TileSize, maxX - minX, 0.001f,
                "fused pair must span one 5 m tile");
            Assert.AreEqual(2f * GridVisualConstants.CellHeight, maxY, 0.001f,
                "fused wall must keep the uniform 4 m height");

            UnityEngine.Object.DestroyImmediate(mesh);
        }

        [Test]
        public void SubmeshAssignment()
        {
            // Single wall cell fully surrounded by corridor → all 4 sides retract.
            var cells = CorridorSeaWithWalls((3, 3));
            int[] heights = WallGreedyMesher.ComputeWallHeights(cells, Size);
            byte[] insets  = WallGreedyMesher.ComputeWallInsets(cells, Size);

            var mesh = WallGreedyMesher.BuildChunkMesh(cells, Size, heights, insets);

            Assert.AreEqual(2, mesh.subMeshCount, "mesh must have exactly 2 submeshes");

            // 1 cell × 4 side quads × 6 indices = 24; submesh 0 = side faces.
            Assert.AreEqual(24, mesh.GetTriangles(0).Length,
                "submesh 0 (wall material) must hold the 4 side-face quads");

            // 1 cell × 1 top quad × 6 indices = 6; submesh 1 = top caps.
            Assert.AreEqual(6, mesh.GetTriangles(1).Length,
                "submesh 1 (ceiling material) must hold the top cap quad");

            UnityEngine.Object.DestroyImmediate(mesh);
        }
    }
}
