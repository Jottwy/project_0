using BackroomsSurvival.Gameplay.Building;
using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Contract tests for <see cref="GridWallSnap"/> — the snapping a player-built wall shares with
    /// the procedural walls. The pose it returns is what the client sends over the wire, so every
    /// instance must derive the same one from the same aim point.
    /// </summary>
    [TestFixture]
    public class GridWallSnapTests
    {
        private const float Ts = GridVisualConstants.TileSize;   // 5
        private const float Lh = GridConstants.LayerHeight;      // 4
        private const float Eps = 1e-4f;

        // Chunk side in metres, mirroring ProceduralWorldGenerator's own `Side`.
        private const float ChunkSide = GridConstants.ChunkCells * GridConstants.CellSize; // 50

        [Test]
        public void SnapMatchesGridChunkBuilderEdgeConvention()
        {
            // A player-built wall has to land on the exact slot the generator would have used, or
            // the two read as two different grids. Recomputed here the long way — chunk origin plus
            // TileCenter plus the WallEdgeTable offset — so a change to either convention breaks
            // this test instead of only showing up in a playtest.
            const int chunkX = 3, chunkZ = -2, layer = 1;
            const int tileX = 4, tileZ = 7;

            var origin = new Vector3(chunkX * ChunkSide, layer * Lh, chunkZ * ChunkSide);
            var tileCentre = new Vector3((tileX + 0.5f) * Ts, 0f, (tileZ + 0.5f) * Ts);
            var expected = origin + tileCentre + new Vector3(0f, 0f, 0.5f * Ts); // EdgeNorth, yaw 0

            // Aim at the floor just inside that tile, close to its north edge.
            var aim = new Vector3(expected.x, layer * Lh + 0.04f, expected.z - 0.3f);
            var pose = GridWallSnap.Snap(aim, upwardSurface: true);

            Assert.AreEqual(expected.x, pose.Position.x, Eps);
            Assert.AreEqual(expected.y, pose.Position.y, Eps);
            Assert.AreEqual(expected.z, pose.Position.z, Eps);
            Assert.AreEqual(0f, pose.Yaw, Eps, "a panel on a ±Z edge runs along X → yaw 0");
            Assert.AreEqual(layer, pose.Layer);
        }

        [Test]
        public void SnapPicksTheNearestEdge()
        {
            // Tile (0,0) spans [0,5]×[0,5]; its centre is (2.5, ·, 2.5).
            var floor = 0.04f;

            var east = GridWallSnap.Snap(new Vector3(4.6f, floor, 2.5f), true);
            Assert.AreEqual(Ts, east.Position.x, Eps);
            Assert.AreEqual(2.5f, east.Position.z, Eps);
            Assert.AreEqual(90f, east.Yaw, Eps, "a panel on a ±X edge runs along Z → yaw 90");

            var west = GridWallSnap.Snap(new Vector3(0.4f, floor, 2.5f), true);
            Assert.AreEqual(0f, west.Position.x, Eps);
            Assert.AreEqual(90f, west.Yaw, Eps);

            var south = GridWallSnap.Snap(new Vector3(2.5f, floor, 0.4f), true);
            Assert.AreEqual(0f, south.Position.z, Eps);
            Assert.AreEqual(0f, south.Yaw, Eps);

            var north = GridWallSnap.Snap(new Vector3(2.5f, floor, 4.6f), true);
            Assert.AreEqual(Ts, north.Position.z, Eps);
            Assert.AreEqual(0f, north.Yaw, Eps);
        }

        [Test]
        public void SnapResolvesTheCentreTieDeterministically()
        {
            // At the exact tile centre all four edges are 2.5 m away. Which one wins is arbitrary;
            // that it is always the SAME one is not — two clients aiming at the same spot must not
            // disagree about where the wall goes.
            var first = GridWallSnap.Snap(new Vector3(2.5f, 0.04f, 2.5f), true);
            var second = GridWallSnap.Snap(new Vector3(2.5f, 0.04f, 2.5f), true);

            Assert.AreEqual(first.Position, second.Position);
            Assert.AreEqual(first.Yaw, second.Yaw, Eps);
            Assert.AreEqual(0f, first.Position.x, Eps, "documented tie order W → E → S → N");
            Assert.AreEqual(90f, first.Yaw, Eps);
        }

        [Test]
        public void SnapHandlesNegativeCoordinates()
        {
            // A cast to int truncates toward zero, folding tile -1 into tile 0 and mirroring the
            // whole grid across the origin. Only FloorToInt is correct here.
            var pose = GridWallSnap.Snap(new Vector3(-0.4f, 0.04f, -2.5f), true);

            // Point is in tile (-1,-1), spanning [-5,0]×[-5,0]; nearest edge is its east one (x = 0).
            Assert.AreEqual(0f, pose.Position.x, Eps);
            Assert.AreEqual(-2.5f, pose.Position.z, Eps);
            Assert.AreEqual(90f, pose.Yaw, Eps);
        }

        [Test]
        public void LayerAtRoundsFloorHitsAndFloorsVerticalHits()
        {
            // A floor slab is 0.08 m thick and centred on the layer plane, so a floor hit is within
            // a few centimetres of layer*LayerHeight → rounding nails it.
            Assert.AreEqual(0, GridWallSnap.LayerAt(0.04f, upwardSurface: true));
            Assert.AreEqual(1, GridWallSnap.LayerAt(Lh + 0.04f, upwardSurface: true));
            Assert.AreEqual(2, GridWallSnap.LayerAt(2f * Lh - 0.04f, upwardSurface: true));

            // A wall is hit anywhere across the 4 m of its own layer. These two are the whole reason
            // the vertical case exists: rounding would report layer 1 for BOTH, sending a wall aimed
            // at the top of a layer-0 wall up to the floor above.
            Assert.AreEqual(0, GridWallSnap.LayerAt(3.99f, upwardSurface: false));
            Assert.AreEqual(1, GridWallSnap.LayerAt(4.01f, upwardSurface: false));
            Assert.AreEqual(0, GridWallSnap.LayerAt(0.5f, upwardSurface: false));
        }

        [Test]
        public void SnapAlwaysLandsOnAGridSlot()
        {
            // Sweep the neighbourhood of the origin: whatever the aim point, the pose must be a
            // legal slot — one axis on a tile boundary (multiple of 5), the other on a tile centre
            // (odd multiple of 2.5), pivot on a layer floor, yaw only 0 or 90.
            for (float x = -12f; x <= 12f; x += 0.37f)
            {
                for (float z = -12f; z <= 12f; z += 0.41f)
                {
                    var pose = GridWallSnap.Snap(new Vector3(x, 0.04f, z), true);

                    Assert.AreEqual(0f, pose.Position.y, Eps, $"aim ({x},{z}) must sit on layer 0's floor");
                    Assert.That(pose.Yaw, Is.EqualTo(0f).Within(Eps).Or.EqualTo(90f).Within(Eps),
                        $"aim ({x},{z}) produced an off-grid yaw");

                    bool runsAlongX = Mathf.Abs(pose.Yaw) < Eps;
                    float onBoundary = runsAlongX ? pose.Position.z : pose.Position.x;
                    float onCentre = runsAlongX ? pose.Position.x : pose.Position.z;

                    Assert.AreEqual(0f, Mathf.Abs(onBoundary % Ts), 1e-3f,
                        $"aim ({x},{z}) is not on a tile boundary");
                    Assert.AreEqual(0.5f * Ts, Mathf.Abs(onCentre % Ts), 1e-3f,
                        $"aim ({x},{z}) is not centred on its tile");
                }
            }
        }

        [Test]
        public void SnapCarriesTheLayerFloorIntoThePose()
        {
            for (int layer = 0; layer < 4; layer++)
            {
                var pose = GridWallSnap.Snap(new Vector3(7.3f, layer * Lh + 0.04f, 2.1f), true);
                Assert.AreEqual(layer, pose.Layer);
                Assert.AreEqual(layer * Lh, pose.Position.y, Eps);
            }
        }
    }
}
