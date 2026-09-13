using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Mapping;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// P0.1 de MAPPING-PROTOTYPE: el recuerdo del jugador. Sin UnityEngine, así que también corre en
    /// `tools/dev/headless-tests`.
    /// </summary>
    [TestFixture]
    public class MapMemoryTests
    {
        // Los números del juego: Wg3ChunkStreamer.ChunkSize y Wg3StoreyLayers.StoreyM. Escritos aquí
        // porque esas clases arrastran UnityEngine; el muestreador en escena pasa las constantes.
        private const float CellSizeM = 0.5f;
        private const float ChunkSizeM = 50f;
        private const float StoreyM = 3.32f;
        private const float MemorySeconds = 60f;
        private const float SampleSeconds = 0.5f;
        private const int MaxCells = 441; // radio de 5 m a 0,5 m: rejilla de 21 × 21

        private static MapMemory NewMemory() =>
            new MapMemory(CellSizeM, ChunkSizeM, StoreyM, MemorySeconds, SampleSeconds, MaxCells);

        private static void Add(MapMemory memory, double time, int storey, params (int x, int z, MapCellKind kind)[] cells)
        {
            var xs = new int[cells.Length];
            var zs = new int[cells.Length];
            var kinds = new MapCellKind[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                xs[i] = cells[i].x;
                zs[i] = cells[i].z;
                kinds[i] = cells[i].kind;
            }

            memory.AddSample(time, storey, xs, zs, kinds, cells.Length);
        }

        private static List<RememberedCell> Query(MapMemory memory, MapZone zone, double now, int wallMargin = 0)
        {
            var results = new List<RememberedCell>();
            memory.CellsInZone(zone, now, results, wallMargin);
            return results;
        }

        private static List<string> Coordinates(List<RememberedCell> cells)
        {
            var list = new List<string>();
            foreach (RememberedCell cell in cells) list.Add(cell.X + "," + cell.Z);
            return list;
        }

        [Test]
        public void ASampleOlderThanTheMemoryIsForgotten()
        {
            MapMemory memory = NewMemory();
            var zone = new MapZone(0, 0, 0);
            Add(memory, 0.0, 0, (1, 1, MapCellKind.Floor));

            Assert.AreEqual(1, Query(memory, zone, 60.0).Count, "a los 60 s justos todavía se recuerda");
            Assert.AreEqual(0, Query(memory, zone, 60.5).Count, "pasados los 60 s ya no");

            memory.Prune(60.5);
            Assert.AreEqual(0, memory.SampleCount);
        }

        [Test]
        public void CellsSplitByChunkAndStorey()
        {
            MapMemory memory = NewMemory();
            Add(memory, 0.0, 0, (99, 5, MapCellKind.Floor), (100, 5, MapCellKind.Wall));
            Add(memory, 0.5, 1, (5, 5, MapCellKind.Floor));

            CollectionAssert.AreEqual(new[] { "99,5" }, Coordinates(Query(memory, new MapZone(0, 0, 0), 1.0)));
            CollectionAssert.AreEqual(new[] { "100,5" }, Coordinates(Query(memory, new MapZone(1, 0, 0), 1.0)));
            CollectionAssert.AreEqual(new[] { "5,5" }, Coordinates(Query(memory, new MapZone(0, 0, 1), 1.0)));

            Assert.AreEqual(new MapZone(0, 0, 0), memory.ZoneOf(49.9, 0.0, 1.0));
            Assert.AreEqual(new MapZone(1, 0, 0), memory.ZoneOf(50.0, 0.0, 1.0));
            Assert.AreEqual(1, memory.ZoneOf(1.0, StoreyM, 1.0).Storey);
        }

        [Test]
        public void NegativeCoordinatesLandInTheRightZone()
        {
            MapMemory memory = NewMemory();

            Assert.AreEqual(-1, memory.CellOf(-0.1));
            Assert.AreEqual(new MapZone(-1, -1, 0), memory.ZoneOf(-0.1, 0.0, -0.1));
            Assert.AreEqual(new MapZone(-1, -2, 0), memory.ZoneOfCell(-100, -101, 0));
            Assert.AreEqual(-1, memory.StoreyOf(-0.1));

            Add(memory, 0.0, 0, (-1, -1, MapCellKind.Wall));
            Assert.AreEqual(1, Query(memory, new MapZone(-1, -1, 0), 0.0).Count);
            Assert.AreEqual(0, Query(memory, new MapZone(0, 0, 0), 0.0).Count);
        }

        [Test]
        public void ACellKeepsItsFreshestAge()
        {
            MapMemory memory = NewMemory();
            Add(memory, 0.0, 0, (3, 3, MapCellKind.Floor));
            Add(memory, 10.0, 0, (3, 3, MapCellKind.Wall));

            List<RememberedCell> cells = Query(memory, new MapZone(0, 0, 0), 30.0);

            Assert.AreEqual(1, cells.Count);
            Assert.AreEqual(20.0, cells[0].Age);
            Assert.AreEqual(MapCellKind.Wall, cells[0].Kind, "manda lo último que se vio");
        }

        [Test]
        public void AWallHidesWhatIsBehindIt()
        {
            const int radius = 3;
            int side = 2 * radius + 1;
            var occupied = new bool[side * side];
            occupied[(0 + radius) * side + 1 + radius] = true; // pared justo al este del jugador

            var xs = new int[side * side];
            var zs = new int[side * side];
            var kinds = new MapCellKind[side * side];
            int count = MapMemory.CollectVisible(occupied, radius, 10, 20, xs, zs, kinds);

            var seen = new Dictionary<string, MapCellKind>();
            for (int i = 0; i < count; i++) seen[xs[i] + "," + zs[i]] = kinds[i];

            Assert.IsTrue(seen.ContainsKey("11,20"), "la pared se ve");
            Assert.AreEqual(MapCellKind.Wall, seen["11,20"]);
            Assert.IsFalse(seen.ContainsKey("12,20"), "lo que hay detrás de la pared, no");
            Assert.IsFalse(seen.ContainsKey("13,20"));
            Assert.IsTrue(seen.ContainsKey("10,21"), "hacia otro lado sí");
            Assert.IsTrue(seen.ContainsKey("10,20"), "la celda del jugador siempre");
            Assert.IsFalse(seen.ContainsKey("13,23"), "fuera del disco no se mira");
        }

        [Test]
        public void CellsInZoneComeOutInAStableOrder()
        {
            MapMemory memory = NewMemory();
            Add(memory, 0.0, 0, (5, 2, MapCellKind.Floor), (1, 7, MapCellKind.Wall), (3, 2, MapCellKind.Floor));
            Add(memory, 0.5, 0, (2, 1, MapCellKind.Floor), (9, 7, MapCellKind.Floor));

            CollectionAssert.AreEqual(new[] { "2,1", "3,2", "5,2", "1,7", "9,7" },
                Coordinates(Query(memory, new MapZone(0, 0, 0), 1.0)));
        }

        [Test]
        public void ConsumedCellsLeaveTheMemory()
        {
            MapMemory memory = NewMemory();
            var zoneA = new MapZone(0, 0, 0);
            var zoneB = new MapZone(1, 0, 0);
            Add(memory, 0.0, 0, (1, 1, MapCellKind.Floor), (101, 1, MapCellKind.Floor));

            memory.Consume(zoneA);

            Assert.AreEqual(0, Query(memory, zoneA, 1.0).Count, "lo dibujado ya no está en el recuerdo");
            Assert.AreEqual(1, Query(memory, zoneB, 1.0).Count, "lo de otra zona sigue");

            Add(memory, 2.0, 0, (1, 1, MapCellKind.Floor));
            Assert.AreEqual(1, Query(memory, zoneA, 3.0).Count, "volver a verlo lo recuerda otra vez");
        }

        private static void AddAt(MapMemory memory, double time, int storey, int playerX, int playerZ)
        {
            memory.AddSample(time, storey, playerX, playerZ, new[] { playerX }, new[] { playerZ },
                new[] { MapCellKind.Floor }, 1);
        }

        [Test]
        public void CrossingIntoTheNextChunkIsReported()
        {
            MapMemory memory = NewMemory();
            AddAt(memory, 0.0, 0, 98, 5);
            AddAt(memory, 0.5, 0, 99, 5);
            AddAt(memory, 1.0, 0, 100, 5);

            var crossings = new List<MapCrossing>();
            memory.Crossings(crossings);

            Assert.AreEqual(1, crossings.Count);
            Assert.AreEqual(new MapZone(0, 0, 0), crossings[0].From);
            Assert.AreEqual(new MapZone(1, 0, 0), crossings[0].To);
            Assert.AreEqual(99, crossings[0].FromCellX, "cada muestra recuerda dónde estaba el jugador");
            Assert.AreEqual(5, crossings[0].FromCellZ);
        }

        [Test]
        public void ATeleportIsNotACrossing()
        {
            MapMemory memory = NewMemory();
            AddAt(memory, 0.0, 0, 99, 5);
            AddAt(memory, 5.0, 0, 100, 5);
            memory.AddSample(5.5, 0, new[] { 1 }, new[] { 1 }, new[] { MapCellKind.Floor }, 1);

            var crossings = new List<MapCrossing>();
            memory.Crossings(crossings);

            CollectionAssert.IsEmpty(crossings, "cinco segundos sin muestras no demuestran que se pasara andando, y sin celda de jugador tampoco");
        }

        [Test]
        public void ChangingStoreyIsACrossing()
        {
            MapMemory memory = NewMemory();
            AddAt(memory, 0.0, 0, 10, 10);
            AddAt(memory, 0.5, 1, 10, 10);

            var crossings = new List<MapCrossing>();
            memory.Crossings(crossings);

            Assert.AreEqual(1, crossings.Count);
            Assert.AreEqual(new MapZone(0, 0, 1), crossings[0].To);
        }

        [Test]
        public void ConsumedCellsStillCountForRecognition()
        {
            MapMemory memory = NewMemory();
            var zone = new MapZone(0, 0, 0);
            Add(memory, 0.0, 0, (1, 1, MapCellKind.Wall));
            memory.Consume(zone);

            var results = new List<RememberedCell>();
            memory.CellsInZone(zone, 1.0, results);
            Assert.AreEqual(0, results.Count, "el dibujo no lo vuelve a cobrar");
            memory.CellsInZone(zone, 1.0, results, includeConsumed: true);
            Assert.AreEqual(1, results.Count, "el reconocimiento sí lo ve");
            Assert.AreEqual(MapCellKind.Wall, results[0].Kind, "y con su tipo, sin la marca");
        }

        [Test]
        public void MaxAgeKeepsOnlyTheRecentPart()
        {
            MapMemory memory = NewMemory();
            Add(memory, 0.0, 0, (1, 1, MapCellKind.Floor));
            Add(memory, 10.0, 0, (2, 2, MapCellKind.Floor));

            var results = new List<RememberedCell>();
            memory.CellsInZone(new MapZone(0, 0, 0), 12.0, results, maxAgeSeconds: 5.0);

            CollectionAssert.AreEqual(new[] { "2,2" }, Coordinates(results));
        }

        [Test]
        public void TheWallMarginBringsOnlyTheNeighboursWalls()
        {
            MapMemory memory = NewMemory();
            Add(memory, 0.0, 0, (100, 5, MapCellKind.Floor), (100, 6, MapCellKind.Wall), (101, 6, MapCellKind.Wall));

            CollectionAssert.AreEqual(new[] { "100,6" }, Coordinates(Query(memory, new MapZone(0, 0, 0), 0.0, 1)),
                "la pared a una celda del borde sí; el suelo del vecino y la pared a dos celdas, no");
        }

        [Test]
        public void TheRingBufferDoesNotGrowPastCapacity()
        {
            MapMemory memory = NewMemory();
            Assert.AreEqual(121, memory.Capacity, "60 s a 0,5 s, más la muestra de t = 0");

            for (int i = 0; i < 500; i++)
                Add(memory, 0.0, 0, (i % 100, i / 100, MapCellKind.Floor));

            Assert.AreEqual(memory.Capacity, memory.SampleCount);
            List<string> cells = Coordinates(Query(memory, new MapZone(0, 0, 0), 0.0));
            Assert.AreEqual(memory.Capacity, cells.Count);
            CollectionAssert.DoesNotContain(cells, "0,0", "la más vieja se pisó");
            Assert.IsTrue(cells.Contains("99,4"), "la última está");
        }
    }
}
