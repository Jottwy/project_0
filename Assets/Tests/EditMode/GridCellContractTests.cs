using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Mirror of the Rust `cell_contract_values_are_stable` test
    /// (backend/src/world/grid_gen/cell.rs). These values are a CONTRACT
    /// between Unity and the backend; if one side changes, both tests and
    /// both enums change in the same commit.
    /// </summary>
    [TestFixture]
    public class GridCellContractTests
    {
        [Test]
        public void CellTypeContractValuesAreStable()
        {
            Assert.AreEqual(0, (byte)GridCellType.Wall);
            Assert.AreEqual(1, (byte)GridCellType.Corridor);
            Assert.AreEqual(2, (byte)GridCellType.Open);
            Assert.AreEqual(3, (byte)GridCellType.Pillar);
            Assert.AreEqual(4, (byte)GridCellType.Stair);
            Assert.AreEqual(5, (byte)GridCellType.Pit);
            Assert.AreEqual(6, (byte)GridCellType.Void);
            Assert.AreEqual(7, (byte)GridCellType.Anomaly);
        }

        [Test]
        public void GridConstantsMatchBackend()
        {
            Assert.AreEqual(2.5f, GridConstants.CellSize);
            Assert.AreEqual(20, GridConstants.ChunkCells);
            Assert.AreEqual(15f, GridConstants.LayerHeight);
            Assert.AreEqual(6, GridConstants.MaxCeilingUnits);
            // LAYER_HEIGHT_M == MAX_CEILING_UNITS × CELL_SIZE_M (same invariant as Rust).
            Assert.AreEqual(GridConstants.LayerHeight,
                GridConstants.MaxCeilingUnits * GridConstants.CellSize);
        }

        [Test]
        public void WalkabilityIsConsistentWithBackend()
        {
            Assert.IsFalse(new GridCell(GridCellType.Wall, 0, 0).IsWalkable);
            Assert.IsFalse(new GridCell(GridCellType.Pillar, 2, 0).IsWalkable);
            Assert.IsFalse(new GridCell(GridCellType.Void, 0, 0).IsWalkable);
            Assert.IsTrue(new GridCell(GridCellType.Corridor, 2, 0).IsWalkable);
            Assert.IsTrue(new GridCell(GridCellType.Open, 4, 1).IsWalkable);
            Assert.IsTrue(new GridCell(GridCellType.Stair, 2, 0).IsWalkable);
            Assert.IsTrue(new GridCell(GridCellType.Pit, 2, 0).IsWalkable);
            Assert.IsTrue(new GridCell(GridCellType.Anomaly, 2, 0).IsWalkable);
        }

        [Test]
        public void UnknownCellTypeCollapsesToWall()
        {
            var cell = new GridCell { cellType = 200, ceilingHeight = 0, zoneId = 0 };
            Assert.AreEqual(GridCellType.Wall, cell.Kind);
            Assert.IsTrue(cell.IsSolid);
        }
    }
}
