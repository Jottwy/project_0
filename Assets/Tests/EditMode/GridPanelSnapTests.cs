using BackroomsSurvival.Gameplay.Building;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Contract tests for <see cref="GridPanelSnap"/> — where an infill piece lands when aimed at a
    /// placed grid wall. The pose it returns is what the client sends over the wire, so the two faces
    /// of one cell must never collapse into a single slot.
    /// </summary>
    [TestFixture]
    public class GridPanelSnapTests
    {
        private const int Columns = 5;
        private const int Rows = 4;
        private const float CellW = GridPanelSnap.SlotLength / Columns;  // 1
        private const float CellH = GridPanelSnap.SlotHeight / Rows;     // 1
        private const float HalfT = GridPanelSnap.SlotHalfThickness;     // 0.1
        private const float Offset = 0.067f;
        private const float Eps = 1e-4f;

        // A frame on a ±Z tile edge: runs along X, so its faces look up and down Z.
        private static readonly Vector3 Pivot = new(15f, 4f, 20f);
        private const float Yaw = 0f;

        /// <summary>Aim point on the front face, at the centre of cell (column, row).</summary>
        private static Vector3 FrontAim(int column, int row) => Pivot + new Vector3(
            -GridPanelSnap.SlotLength * 0.5f + (column + 0.5f) * CellW,
            (row + 0.5f) * CellH,
            HalfT);

        /// <summary>A 1 × 1 piece — the simplest footprint, used wherever the span is not the subject.</summary>
        private static bool SnapSingle(Vector3 hit, Vector3 normal, out GridPanelPose pose) =>
            GridPanelSnap.TrySnap(Pivot, Yaw, Columns, Rows, 1, 1, hit, normal, Offset, out pose);

        [Test]
        public void CellCentreIsDerivedFromTheColliderSpanNotTheMesh()
        {
            // The 5 x 4 collider, not the 4.749 x 3.712 Steel Frame Grid mesh. Using the mesh would
            // shrink every cell and player infill would stop lining up with the procedural walls
            // sharing the same tile edge — invisible until two walls meet at a corner.
            Assert.IsTrue(SnapSingle(FrontAim(0, 0), Vector3.forward, out var pose));

            Assert.AreEqual(0, pose.Column);
            Assert.AreEqual(0, pose.Row);
            Assert.AreEqual(Pivot.x - 2f, pose.Position.x, Eps, "leftmost of 5 columns across 5 m");
            Assert.AreEqual(Pivot.y + 0.5f, pose.Position.y, Eps, "bottom of 4 rows across 4 m");
            Assert.AreEqual(Pivot.z + HalfT + Offset, pose.Position.z, Eps, "pushed off the face by its own half-thickness");
            Assert.AreEqual(Pivot.z + HalfT, pose.SlotCentre.z, Eps, "the probe anchor stays ON the face");
            Assert.AreEqual(CellW, pose.Size.x, Eps);
            Assert.AreEqual(CellH, pose.Size.y, Eps);
        }

        [Test]
        public void EveryCellOfTheGridResolvesToItsOwnIndex()
        {
            for (int column = 0; column < Columns; column++)
            {
                for (int row = 0; row < Rows; row++)
                {
                    Assert.IsTrue(SnapSingle(FrontAim(column, row), Vector3.forward, out var pose));
                    Assert.AreEqual(column, pose.Column, $"column of cell ({column},{row})");
                    Assert.AreEqual(row, pose.Row, $"row of cell ({column},{row})");
                }
            }
        }

        [Test]
        public void TheTwoFacesOfOneCellDoNotCollapseIntoTheSameReservationSlot()
        {
            // The regression this whole design exists to prevent. GridWallReservations quantises
            // position at 0.25 m; the two faces are ~0.33 m apart in Z here but a piece's face offset
            // is free to shrink, and on any yaw the two can round to the same position cell. Only the
            // 180 in the yaw guarantees they stay distinct keys.
            Assert.IsTrue(SnapSingle(FrontAim(2, 1), Vector3.forward, out var front));
            Assert.IsTrue(SnapSingle(Pivot + new Vector3(0f, 1.5f, -HalfT), Vector3.back, out var back));

            Assert.AreEqual(front.Column, back.Column, "same cell, opposite sides");
            Assert.AreEqual(front.Row, back.Row);
            Assert.IsTrue(front.FrontFace);
            Assert.IsFalse(back.FrontFace);

            Assert.AreEqual(0f, front.Yaw, Eps);
            Assert.AreEqual(180f, back.Yaw, Eps);
            Assert.AreEqual(Pivot.z - HalfT - Offset, back.Position.z, Eps, "clad outward, on the far side");
        }

        [Test]
        public void ARotatedFrameResolvesCellsInItsOwnSpace()
        {
            // A frame on a ±X tile edge (yaw 90) runs along Z. Its local +X maps to world -Z, so a
            // column index has to be read in frame space or the grid mirrors on half the walls.
            const float rotated = 90f;
            var aim = Pivot + new Vector3(HalfT, 0.5f, 2f);

            Assert.IsTrue(GridPanelSnap.TrySnap(Pivot, rotated, Columns, Rows, 1, 1,
                aim, Vector3.right, Offset, out var pose));

            Assert.AreEqual(0, pose.Column, "the frame's local +X is world -Z at yaw 90 → an aim 2 m up +Z is column 0");
            Assert.AreEqual(0, pose.Row);
            Assert.IsTrue(pose.FrontFace, "local +Z is world +X at yaw 90");
            Assert.AreEqual(90f, pose.Yaw, Eps);
            Assert.AreEqual(Pivot.x + HalfT + Offset, pose.Position.x, Eps, "clad along world +X");
            Assert.AreEqual(Pivot.z + 2f, pose.Position.z, Eps, "column 0's centre is 2 m along world +Z");
        }

        [Test]
        public void AHitOnTheTopOrSideEdgeIsNotACell()
        {
            // The collider is a solid box, so a ray can land on its 0.2 m rim or its top. Those are
            // not cell-bearing faces and must not silently snap to the nearest one.
            Assert.IsFalse(SnapSingle(Pivot + new Vector3(0f, GridPanelSnap.SlotHeight, 0f), Vector3.up, out _),
                "top edge");
            Assert.IsFalse(SnapSingle(Pivot + new Vector3(GridPanelSnap.SlotLength * 0.5f, 2f, 0f), Vector3.right, out _),
                "side rim");
        }

        [Test]
        public void AHitOffTheFrameIsRejectedButTheRimItselfStillResolves()
        {
            // Well past the end: no cell.
            Assert.IsFalse(SnapSingle(
                Pivot + new Vector3(GridPanelSnap.SlotLength, 2f, HalfT), Vector3.forward, out _));

            // Below the floor: no cell.
            Assert.IsFalse(SnapSingle(Pivot + new Vector3(0f, -0.5f, HalfT), Vector3.forward, out _));

            // Exactly on the outer corner of the face. A BoxCollider returns boundary points, and
            // rejecting them would make the outermost centimetre of every frame unbuildable.
            Assert.IsTrue(SnapSingle(
                Pivot + new Vector3(GridPanelSnap.SlotLength * 0.5f, GridPanelSnap.SlotHeight, HalfT),
                Vector3.forward, out var corner));
            Assert.AreEqual(Columns - 1, corner.Column, "clamped into the last column, not overflowed");
            Assert.AreEqual(Rows - 1, corner.Row);
        }

        [Test]
        public void NegativeWorldCoordinatesKeepTheSameCellLayout()
        {
            // Frames exist at negative chunk coordinates and nothing here may truncate toward zero.
            var pivot = new Vector3(-35f, 0f, -60f);
            var aim = pivot + new Vector3(-2.4f, 3.9f, HalfT);

            Assert.IsTrue(GridPanelSnap.TrySnap(pivot, 0f, Columns, Rows, 1, 1,
                aim, Vector3.forward, Offset, out var pose));
            Assert.AreEqual(0, pose.Column);
            Assert.AreEqual(Rows - 1, pose.Row);
            Assert.AreEqual(pivot.x - 2f, pose.Position.x, Eps);
            Assert.AreEqual(pivot.y + 3.5f, pose.Position.y, Eps);
        }

        [Test]
        public void ADegenerateSubdivisionIsRefusedRatherThanDividedByZero()
        {
            Assert.IsFalse(GridPanelSnap.TrySnap(Pivot, Yaw, 0, Rows, 1, 1,
                FrontAim(0, 0), Vector3.forward, Offset, out _));
            Assert.IsFalse(GridPanelSnap.TrySnap(Pivot, Yaw, Columns, 0, 1, 1,
                FrontAim(0, 0), Vector3.forward, Offset, out _));
        }

        [Test]
        public void CellSizeIsSquareAtFiveByFour()
        {
            // 1 m cells are the only subdivision that divides the frame's 5 × 4 m span exactly, which
            // is why the sheet spans cells instead of the grid being sized to the sheet.
            var cell = GridPanelSnap.CellSize(Columns, Rows);
            Assert.AreEqual(1f, cell.x, Eps);
            Assert.AreEqual(1f, cell.y, Eps);
        }

        // ── Footprint ────────────────────────────────────────────────────────────

        [Test]
        public void ATwoByTwoSheetCoversFourCellsAndIsCentredOnThem()
        {
            Assert.IsTrue(GridPanelSnap.TrySnap(Pivot, Yaw, Columns, Rows, 2, 2,
                FrontAim(2, 1), Vector3.forward, Offset, out var pose));

            Assert.AreEqual(2, pose.Column, "origin cell, not the aimed one shifted");
            Assert.AreEqual(1, pose.Row);
            Assert.AreEqual(2f, pose.Size.x, Eps);
            Assert.AreEqual(2f, pose.Size.y, Eps);

            // Centre of cells 2..3 across a 5 m span starting at −2.5: −2.5 + 3 = 0.5.
            Assert.AreEqual(Pivot.x + 0.5f, pose.Position.x, Eps);
            Assert.AreEqual(Pivot.y + 2f, pose.Position.y, Eps);
        }

        [Test]
        public void ASheetNearTheEdgeSticksInsteadOfVanishing()
        {
            // Aimed at the very last column, where a 2-wide span cannot start. It must clamp back to
            // the last position where it fits — the ghost stays visible as the crosshair sweeps past.
            Assert.IsTrue(GridPanelSnap.TrySnap(Pivot, Yaw, Columns, Rows, 2, 2,
                FrontAim(Columns - 1, Rows - 1), Vector3.forward, Offset, out var pose));

            Assert.AreEqual(Columns - 2, pose.Column, "clamped so the span ends on the last column");
            Assert.AreEqual(Rows - 2, pose.Row);
        }

        [Test]
        public void AdjacentAimCellsGiveAdjacentOriginsSoJointsCanBeStaggered()
        {
            // Free cell-by-cell placement: sliding the crosshair one cell moves the sheet one cell, so
            // the row above can be offset against the row below. A coarse lattice would snap both aims
            // onto the same origin and make that impossible.
            Assert.IsTrue(GridPanelSnap.TrySnap(Pivot, Yaw, Columns, Rows, 2, 2,
                FrontAim(0, 0), Vector3.forward, Offset, out var first));
            Assert.IsTrue(GridPanelSnap.TrySnap(Pivot, Yaw, Columns, Rows, 2, 2,
                FrontAim(1, 0), Vector3.forward, Offset, out var second));

            Assert.AreEqual(0, first.Column);
            Assert.AreEqual(1, second.Column);
            Assert.AreEqual(CellW, second.Position.x - first.Position.x, Eps, "one cell apart, not one span");
        }

        [Test]
        public void ATallStripSpansTheWholeHeightAndCannotDrift()
        {
            // The 1 × 4 filler for the leftover column: its span equals the grid height, so every aim
            // clamps to the only origin that fits.
            foreach (int aimedRow in new[] { 0, 1, 2, 3 })
            {
                Assert.IsTrue(GridPanelSnap.TrySnap(Pivot, Yaw, Columns, Rows, 1, Rows,
                    FrontAim(4, aimedRow), Vector3.forward, Offset, out var pose));

                Assert.AreEqual(0, pose.Row, $"aimed at row {aimedRow}");
                Assert.AreEqual(4, pose.Column);
                Assert.AreEqual(GridPanelSnap.SlotHeight, pose.Size.y, Eps);
                Assert.AreEqual(Pivot.y + GridPanelSnap.SlotHeight * 0.5f, pose.Position.y, Eps);
            }
        }

        [Test]
        public void AFootprintBiggerThanTheFrameIsClampedRatherThanRefused()
        {
            Assert.IsTrue(GridPanelSnap.TrySnap(Pivot, Yaw, Columns, Rows, 99, 99,
                FrontAim(2, 2), Vector3.forward, Offset, out var pose));

            Assert.AreEqual(0, pose.Column);
            Assert.AreEqual(0, pose.Row);
            Assert.AreEqual(GridPanelSnap.SlotLength, pose.Size.x, Eps, "covers the whole face");
            Assert.AreEqual(GridPanelSnap.SlotHeight, pose.Size.y, Eps);
        }

        [Test]
        public void TwoSheetsTileTheFrameAndLeaveExactlyOneColumn()
        {
            // The arithmetic the whole footprint design exists for: 5 is not a multiple of 2, so two
            // 2-wide sheets cover 4 m and leave a 1 m strip. That strip is a slot for a different
            // piece, not a gap — this test pins the number so nobody "fixes" it into 2.5 m cells.
            Assert.IsTrue(GridPanelSnap.TrySnap(Pivot, Yaw, Columns, Rows, 2, 2,
                FrontAim(0, 0), Vector3.forward, Offset, out var left));
            Assert.IsTrue(GridPanelSnap.TrySnap(Pivot, Yaw, Columns, Rows, 2, 2,
                FrontAim(2, 0), Vector3.forward, Offset, out var right));

            Assert.AreEqual(0, left.Column);
            Assert.AreEqual(2, right.Column);

            int covered = right.Column + 2;
            Assert.AreEqual(4, covered, "two sheets reach column 4 exclusive");
            Assert.AreEqual(1, Columns - covered, "exactly one 1 m column left over");
        }
    }
}
