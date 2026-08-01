using System.Collections.Generic;
using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Headless tests for ChunkStreamer's pure scheduling logic (no Play, no Instantiate):
    /// desired-set construction, queue reconciliation incl. backtrack rescue, and
    /// nearest/farthest-first ordering. The real GameObject build is out of scope.
    /// </summary>
    [TestFixture]
    public class ChunkStreamSchedulerTests
    {
        private static HashSet<(int, int, int)> Set(params (int, int, int)[] keys)
            => new HashSet<(int, int, int)>(keys);

        [Test]
        public void BuildDesiredSet_CoversRingTimesLayers()
        {
            const int viewRadius = 1;
            const int layerCount = 2;
            var desired = new HashSet<(int, int, int)>();
            ChunkStreamer.BuildDesiredSet(cx: 0, cz: 0, viewRadius, layerCount, desired);

            // The ring is a full Chebyshev SQUARE — (2r+1)² columns — replicated on every layer.
            // Derived from the Arrange values rather than hardcoded: the literal that used to sit
            // here said 4 while its own comment said 3×3×2, and the mismatch shipped as a red test.
            int side = 2 * viewRadius + 1;
            Assert.AreEqual(side * side * layerCount, desired.Count);
            Assert.IsTrue(desired.Contains((0, 0, 0)));
            // Diagonal corner (dist² = 2 > r), on the SECOND layer: pins both that the ring is a
            // square and not a diamond, and that every layer index is covered.
            Assert.IsTrue(desired.Contains((1, -1, 1)));
            Assert.IsFalse(desired.Contains((2, 0, 0)));     // outside radius
        }

        [Test]
        public void ReconcileQueues_EnqueuesMissingBuildsAndStaleUnloads()
        {
            var desired = Set((0, 0, 0), (1, 0, 0));
            var loaded = Set((1, 0, 0), (5, 5, 0));          // (1,0,0) stays; (5,5,0) leaves
            var build = new HashSet<(int, int, int)>();
            var unload = new HashSet<(int, int, int)>();

            ChunkStreamer.ReconcileQueues(desired, loaded, build, unload);

            CollectionAssert.AreEquivalent(Set((0, 0, 0)), build);   // desired, not loaded
            CollectionAssert.AreEquivalent(Set((5, 5, 0)), unload);  // loaded, not desired
        }

        [Test]
        public void ReconcileQueues_RescuesBacktrackedBuild()
        {
            var build = Set((9, 9, 0));                       // queued to build earlier
            var unload = new HashSet<(int, int, int)>();
            var loaded = new HashSet<(int, int, int)>();
            var desired = Set((0, 0, 0));                     // (9,9,0) no longer wanted

            ChunkStreamer.ReconcileQueues(desired, loaded, build, unload);

            Assert.IsFalse(build.Contains((9, 9, 0)));        // dropped, never built
            Assert.IsTrue(build.Contains((0, 0, 0)));
        }

        [Test]
        public void ReconcileQueues_RescuesBacktrackedUnload()
        {
            var unload = Set((2, 0, 0));                       // queued to destroy earlier
            var build = new HashSet<(int, int, int)>();
            var loaded = Set((2, 0, 0));
            var desired = Set((2, 0, 0));                      // back in range before destroy

            ChunkStreamer.ReconcileQueues(desired, loaded, build, unload);

            Assert.IsFalse(unload.Contains((2, 0, 0)));        // rescued, not destroyed
            Assert.AreEqual(0, build.Count);                   // already loaded → no build
        }

        [Test]
        public void OrderByDistance_NearestFirstThenFarthestFirst()
        {
            var keys = Set((3, 0, 0), (1, 0, 0), (0, 2, 0));
            var into = new List<(int, int, int)>();

            ChunkStreamer.OrderByDistance(keys, cx: 0, cz: 0, nearestFirst: true, into);
            Assert.AreEqual((1, 0, 0), into[0]);               // dist² 1
            Assert.AreEqual((0, 2, 0), into[1]);               // dist² 4
            Assert.AreEqual((3, 0, 0), into[2]);               // dist² 9

            ChunkStreamer.OrderByDistance(keys, cx: 0, cz: 0, nearestFirst: false, into);
            Assert.AreEqual((3, 0, 0), into[0]);               // farthest first
            Assert.AreEqual((1, 0, 0), into[2]);
        }

        [Test]
        public void ReconcileQueues_IsIdempotentNoDuplicates()
        {
            var desired = Set((0, 0, 0), (1, 0, 0));
            var loaded = new HashSet<(int, int, int)>();
            var build = new HashSet<(int, int, int)>();
            var unload = new HashSet<(int, int, int)>();

            ChunkStreamer.ReconcileQueues(desired, loaded, build, unload);
            ChunkStreamer.ReconcileQueues(desired, loaded, build, unload);

            Assert.AreEqual(2, build.Count);                   // HashSet → no dupes across calls
        }
    }
}
