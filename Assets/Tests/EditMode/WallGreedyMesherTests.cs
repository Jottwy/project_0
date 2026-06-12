using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Tile classification for the 5 m tile render system (ADR-001). Kept in
    /// WallGreedyMesherTests.cs for git-history continuity; the old greedy-mesher
    /// tests were removed when the mesher was deprecated.
    /// </summary>
    [TestFixture]
    public class GridTileClassificationTests
    {
        private const int Size = GridConstants.ChunkCells;

        private const GridCellType WALL = GridCellType.Wall;
        private const GridCellType COR  = GridCellType.Corridor;
        private const GridCellType OPEN = GridCellType.Open;
        private const GridCellType VOID = GridCellType.Void;
        private const GridCellType PILL = GridCellType.Pillar;

        private static GridCell Cell(GridCellType t) =>
            new GridCell(t, t == GridCellType.Wall ? (byte)0 : (byte)2, 0);

        /// <summary>
        /// All-Corridor chunk with tile (tx,tz)'s 2×2 cells overridden:
        /// sw, se (south row) then nw, ne (north row).
        /// </summary>
        private static GridCell[] ChunkWithTile(int tx, int tz,
            GridCellType sw, GridCellType se, GridCellType nw, GridCellType ne)
        {
            var cells = new GridCell[Size * Size];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = Cell(GridCellType.Corridor);
            int cx = tx * 2, cz = tz * 2;
            cells[cz * Size + cx]           = Cell(sw);
            cells[cz * Size + cx + 1]       = Cell(se);
            cells[(cz + 1) * Size + cx]     = Cell(nw);
            cells[(cz + 1) * Size + cx + 1] = Cell(ne);
            return cells;
        }

        [Test]
        public void FourWallCells_SolidTile()
        {
            var cells = ChunkWithTile(3, 3, WALL, WALL, WALL, WALL);
            Assert.AreEqual(TileKind.Solid, GridChunkBuilder.ClassifyTile(cells, 3, 3).Kind);
        }

        [Test]
        public void NoWallCells_OpenTile()
        {
            var cells = ChunkWithTile(3, 3, COR, COR, OPEN, OPEN);
            var tc = GridChunkBuilder.ClassifyTile(cells, 3, 3);
            Assert.AreEqual(TileKind.Open, tc.Kind);
            Assert.IsFalse(tc.HasPillar, "no pillar cell → no pillar");
        }

        [Test]
        public void MixedCells_BorderTile_CorrectEdges()
        {
            // South row both Wall, north row open → wall on the SOUTH edge only.
            var cells = ChunkWithTile(3, 3, WALL, WALL, COR, COR);
            var tc = GridChunkBuilder.ClassifyTile(cells, 3, 3);
            Assert.AreEqual(TileKind.Border, tc.Kind);
            Assert.AreEqual(GridChunkBuilder.EdgeSouth, tc.WallEdges,
                "only the south tile edge (both south cells Wall) carries a wall");
        }

        [Test]
        public void TwoVoidCells_HollowTile()
        {
            var cells = ChunkWithTile(3, 3, VOID, VOID, COR, COR);
            Assert.AreEqual(TileKind.Hollow, GridChunkBuilder.ClassifyTile(cells, 3, 3).Kind);
        }

        [Test]
        public void PillarCell_OpenTileWithPillar()
        {
            var cells = ChunkWithTile(3, 3, PILL, COR, COR, COR);
            var tc = GridChunkBuilder.ClassifyTile(cells, 3, 3);
            Assert.AreEqual(TileKind.Open, tc.Kind);
            Assert.IsTrue(tc.HasPillar, "a pillar cell in an open tile adds a pillar");
        }
    }
}
