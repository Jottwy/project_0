using BackroomsSurvival.Gameplay.GridWorld;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Edge-based WorldGenerator: walls live on tile edges, every tile is floored.
    /// Guards the Unity-only render contract — no Wall cells leak into the cell
    /// array (the Rust wire format stays intact), the maze stays connected, and
    /// generation is deterministic per seed/coords.
    /// </summary>
    [TestFixture]
    public class EdgeWorldGeneratorTests
    {
        private const int Cells = GridConstants.ChunkCells; // 20
        private const int Tiles = Cells / 2;                // 10

        private static LayerConfig Cfg()
        {
            var c = ScriptableObject.CreateInstance<LayerConfig>();
            c.wallDensity = 0.6f;
            c.pillarChance = 0.3f;
            c.minApertureRatio = 0.3f;
            c.maxApertureRatio = 0.6f;
            return c;
        }

        [Test]
        public void GenerateChunk_HasExpectedArrayLengths()
        {
            var data = WorldGenerator.GenerateChunk(42, 0, 0, 0, Cfg());
            Assert.AreEqual(Cells * Cells, data.cells.Length);
            Assert.AreEqual(Tiles * Tiles, data.walls.Length);
        }

        [Test]
        public void GenerateChunk_EmitsNoWallCells()
        {
            // Walls are edges now: the cell array must never contain a Wall
            // (it stays Corridor/Pillar), so the Rust cell contract is untouched.
            var data = WorldGenerator.GenerateChunk(42, 3, -2, 1, Cfg());
            foreach (var cell in data.cells)
            {
                Assert.AreNotEqual(GridCellType.Wall, cell.Kind);
                Assert.AreNotEqual(GridCellType.Void, cell.Kind);
                Assert.AreNotEqual(GridCellType.Pit, cell.Kind);
                Assert.IsTrue(cell.Kind == GridCellType.Corridor
                              || cell.Kind == GridCellType.Pillar);
            }
        }

        [Test]
        public void GenerateChunk_EveryTileReachableFromOrigin()
        {
            for (int layer = 0; layer < 4; layer++)
            {
                var data = WorldGenerator.GenerateChunk(42, 1, 1, layer, Cfg());
                var reached = Flood(data.walls);
                for (int i = 0; i < reached.Length; i++)
                    Assert.IsTrue(reached[i],
                        $"tile {i % Tiles},{i / Tiles} unreachable on layer {layer}");
            }
        }

        [Test]
        public void GenerateChunk_BorderSeamsHaveOpenings()
        {
            var data = WorldGenerator.GenerateChunk(42, 0, 0, 0, Cfg());
            int[] slots = { 1, 5, 9 };
            foreach (int s in slots)
            {
                Assert.IsFalse(data.walls[(Tiles - 1) * Tiles + s].N,
                    $"north seam slot {s} must be open");
                Assert.IsFalse(data.walls[s * Tiles + (Tiles - 1)].E,
                    $"east seam slot {s} must be open");
            }
        }

        [Test]
        public void GenerateChunk_IsDeterministic()
        {
            var a = WorldGenerator.GenerateChunk(7, 2, 5, 2, Cfg());
            var b = WorldGenerator.GenerateChunk(7, 2, 5, 2, Cfg());

            for (int i = 0; i < a.walls.Length; i++)
            {
                Assert.AreEqual(a.walls[i].N, b.walls[i].N);
                Assert.AreEqual(a.walls[i].S, b.walls[i].S);
                Assert.AreEqual(a.walls[i].E, b.walls[i].E);
                Assert.AreEqual(a.walls[i].W, b.walls[i].W);
            }
            for (int i = 0; i < a.cells.Length; i++)
                Assert.AreEqual(a.cells[i].cellType, b.cells[i].cellType);
        }

        // BFS over tiles: cross an edge only when it carries no wall.
        private static bool[] Flood(TileWalls[] w)
        {
            var visited = new bool[Tiles * Tiles];
            var stack = new System.Collections.Generic.Stack<int>();
            visited[0] = true;
            stack.Push(0);
            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                int x = idx % Tiles, z = idx / Tiles;
                var t = w[idx];
                if (!t.N && z + 1 < Tiles) Push(visited, stack, x, z + 1);
                if (!t.S && z - 1 >= 0)    Push(visited, stack, x, z - 1);
                if (!t.E && x + 1 < Tiles) Push(visited, stack, x + 1, z);
                if (!t.W && x - 1 >= 0)    Push(visited, stack, x - 1, z);
            }
            return visited;
        }

        private static void Push(bool[] visited,
            System.Collections.Generic.Stack<int> stack, int x, int z)
        {
            int idx = z * Tiles + x;
            if (visited[idx]) return;
            visited[idx] = true;
            stack.Push(idx);
        }
    }
}
