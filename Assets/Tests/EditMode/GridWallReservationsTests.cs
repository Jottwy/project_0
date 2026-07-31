using BackroomsSurvival.Gameplay.Building;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Covers the slot identity of <see cref="GridWallReservations"/> — the part that decides whether
    /// two poses mean the same wall slot.
    ///
    /// Expiry is deliberately NOT covered: <c>Time.time</c> does not advance in EditMode, so every
    /// reservation made here stays live for the whole run. That is also why each test uses its own
    /// coordinates — the registry is static and nothing clears it between tests.
    /// </summary>
    [TestFixture]
    public class GridWallReservationsTests
    {
        [Test]
        public void ReservedSlotReadsBackAsReserved()
        {
            var slot = new Vector3(100f, 0f, 100f);
            Assert.IsFalse(GridWallReservations.IsReserved(slot, 0f), "clean slot before reserving");

            GridWallReservations.Reserve(slot, 0f);
            Assert.IsTrue(GridWallReservations.IsReserved(slot, 0f));
        }

        [Test]
        public void ReservationDoesNotLeakToNeighbouringSlots()
        {
            var slot = new Vector3(200f, 0f, 200f);
            GridWallReservations.Reserve(slot, 0f);

            // The adjacent tile edge, 5 m away — reserving one wall must not block the next one.
            Assert.IsFalse(GridWallReservations.IsReserved(slot + new Vector3(0f, 0f, 5f), 0f));
            // Same line, other layer.
            Assert.IsFalse(GridWallReservations.IsReserved(slot + new Vector3(0f, 4f, 0f), 0f));
            // Same point, the perpendicular lane — a different physical wall.
            Assert.IsFalse(GridWallReservations.IsReserved(slot, 90f));
        }

        [Test]
        public void YawWrapsSoBothSidesAgreeOnTheSameSlot()
        {
            // The reserving side passes transform.rotation.eulerAngles.y, the querying side passes
            // the snapper's literal yaw. A quaternion round-trip can turn 0 into 359.99998; without
            // wrapping, that lands in cell 360 and the reservation matches nothing.
            var slot = new Vector3(300f, 0f, 300f);
            GridWallReservations.Reserve(slot, 359.99998f);

            Assert.IsTrue(GridWallReservations.IsReserved(slot, 0f),
                "a yaw that wrapped just below a full turn must name the same slot as 0");
        }

        [Test]
        public void YawWrapsInBothDirections()
        {
            var slot = new Vector3(400f, 0f, 400f);
            GridWallReservations.Reserve(slot, 0f);

            Assert.IsTrue(GridWallReservations.IsReserved(slot, 360f));
            Assert.IsTrue(GridWallReservations.IsReserved(slot, -0.00002f));
        }
    }
}
