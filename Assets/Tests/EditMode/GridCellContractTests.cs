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
            // GridCellType NO tiene un caso SealedWall (Rust cell.rs, valor 8):
            // el fallback de Kind (cellType <= Anomaly ? ... : Wall) ya lo
            // colapsa a Wall sin que el enum C# necesite conocerlo. Ver
            // UnknownCellTypeCollapsesToWall más abajo (usa 200, no 8 — 8 ya
            // no es "valor desconocido" del lado Rust, pero sigue colapsando
            // igual del lado C#, que es lo que ese test verifica).
        }

        [Test]
        public void GridConstantsMatchBackend()
        {
            Assert.AreEqual(2.5f, GridConstants.CellSize);
            Assert.AreEqual(20, GridConstants.ChunkCells);
            Assert.AreEqual(4f, GridConstants.LayerHeight);
            Assert.AreEqual(6, GridConstants.MaxCeilingUnits);
            // ADR-007: LayerHeight (4 m) is decoupled from ceiling_height; the old
            // MAX_CEILING_UNITS × CELL_SIZE_M invariant no longer holds.
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

        /// <summary>
        /// Rust cell.rs añadió CellType::SealedWall = 8 (perímetro protegido de
        /// RoomType). Unity no necesita distinguirlo: el fallback existente de
        /// Kind (cellType &lt;= Anomaly ? ... : Wall) ya lo colapsa a Wall, así
        /// que un chunk con SealedWall se renderiza como muro normal sin tocar
        /// una línea de C#. Este test fija ese comportamiento a propósito.
        /// </summary>
        [Test]
        public void SealedWallByteCollapsesToWallOnClient()
        {
            var cell = new GridCell { cellType = 8, ceilingHeight = 0, zoneId = 3 };
            Assert.AreEqual(GridCellType.Wall, cell.Kind);
            Assert.IsTrue(cell.IsSolid);
            Assert.IsFalse(cell.IsWalkable);
        }

        [Test]
        public void ChunkBytesParseLittleEndianZoneIds()
        {
            var data = new byte[GridChunkData.ChunkByteSize];
            // Cell 0: Open, ceiling 4, zone 0x0102 (LE: 02 01).
            data[0] = 2;
            data[1] = 4;
            data[2] = 0x02;
            data[3] = 0x01;

            var cells = GridChunkData.FromBytes(data);
            Assert.AreEqual(GridConstants.ChunkCells * GridConstants.ChunkCells, cells.Length);
            Assert.AreEqual(GridCellType.Open, cells[0].Kind);
            Assert.AreEqual(4, cells[0].ceilingHeight);
            Assert.AreEqual(0x0102, cells[0].zoneId);
            // Untouched bytes are Wall(0,0,0).
            Assert.AreEqual(GridCellType.Wall, cells[1].Kind);
        }

        [Test]
        public void ChunkBytesRejectWrongPayloadSize()
        {
            Assert.Throws<System.ArgumentException>(
                () => GridChunkData.FromBytes(new byte[10]));
            Assert.Throws<System.ArgumentException>(
                () => GridChunkData.FromBytes(null));
        }
    }
}
