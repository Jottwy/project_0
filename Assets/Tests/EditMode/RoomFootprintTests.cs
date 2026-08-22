using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Planta frente a footprint (<see cref="RoomFootprint"/>). El caso que lo motivó: room_1,
    /// planta manual de ~20 × 24 m descentrada, horneada con 10 × 10 tiles tecleados — 50 × 50 m
    /// de reserva alrededor de una sala que llenaba el 19 %.
    /// </summary>
    [TestFixture]
    public class RoomFootprintTests
    {
        // Los puntos reales de room_1 tal como quedaron en el pool (x ∈ [-10, 10], z ∈ [-10, 14]).
        private static readonly Vector2[] Room1Contour =
        {
            new Vector2(10f, 10f), new Vector2(2.7554512f, 13.973108f), new Vector2(0.8614483f, 14.043532f),
            new Vector2(-10f, 10f), new Vector2(-9.697112f, -9.845834f), new Vector2(9.57222f, -9.965614f),
        };

        [Test]
        public void Room1ManualPlanFitsFourBySixNotTenByTen()
        {
            RoomFootprint.FitTilesToPlan(Room1Contour, out int tx, out int tz);
            // 20 m de ancho → 4 tiles; z llega a +14 y el footprint es simétrico → 28 m → 6 tiles.
            Assert.AreEqual(4, tx);
            Assert.AreEqual(6, tz);
        }

        [Test]
        public void Room1CoverageOfTenByTenIsTheHole()
        {
            float c = RoomFootprint.PlanCoverage(Room1Contour, 10, 10);
            Assert.Less(c, 0.25f, "la planta llena menos de un cuarto de 50 × 50 m");
            float fitted = RoomFootprint.PlanCoverage(Room1Contour, 4, 6);
            Assert.Greater(fitted, 0.6f, "ajustada a 4 × 6 llena la mayor parte del footprint");
        }

        [Test]
        public void ExactRectangleOfTwelveTilesFitsTwelveNotThirteen()
        {
            // Mismo criterio que PolygonContour: el contorno interior llega al borde del footprint.
            float half = 12 * GridVisualConstants.TileSize * 0.5f;
            var rect = new[]
            {
                new Vector2(-half, -half), new Vector2(half, -half), new Vector2(half, half), new Vector2(-half, half),
            };
            RoomFootprint.FitTilesToPlan(rect, out int tx, out int tz);
            Assert.AreEqual(12, tx);
            Assert.AreEqual(12, tz);
            Assert.IsFalse(RoomFootprint.PlanExceedsFootprint(rect, 12, 12));
            Assert.AreEqual(1f, RoomFootprint.PlanCoverage(rect, 12, 12), 1e-4f);
        }

        [Test]
        public void PlanPokingOutOfFootprintIsDetected()
        {
            Assert.IsTrue(RoomFootprint.PlanExceedsFootprint(Room1Contour, 4, 4),
                "z = 14 se sale de 4 tiles (±10 m)");
            Assert.IsFalse(RoomFootprint.PlanExceedsFootprint(Room1Contour, 4, 6));
        }

        [Test]
        public void DegenerateContourFitsOneByOne()
        {
            RoomFootprint.FitTilesToPlan(null, out int tx, out int tz);
            Assert.AreEqual(1, tx);
            Assert.AreEqual(1, tz);
            Assert.AreEqual(0f, RoomFootprint.PlanCoverage(new Vector2[0], 3, 3));
        }
    }
}
