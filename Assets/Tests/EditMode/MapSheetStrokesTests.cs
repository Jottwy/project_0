using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Mapping;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>P0.2 de MAPPING-PROTOTYPE: de lo recordado a trazos. Sin UnityEngine: corre también headless.</summary>
    [TestFixture]
    public class MapSheetStrokesTests
    {
        // Los números del juego: Wg3ChunkStreamer.ChunkSize y Wg3StoreyLayers.StoreyM (ver MapMemoryTests).
        private static MapMemory NewMemory() => new MapMemory(0.5f, 50f, 3.32f, 60f, 0.5f, 441);

        private static readonly MapZone ZoneZero = new MapZone(0, 0, 0);

        private static void Add(MapMemory memory, double time, params (int x, int z, MapCellKind kind)[] cells)
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

            memory.AddSample(time, 0, xs, zs, kinds, cells.Length);
        }

        private static MapSheet NewSheet(int seed = 1234)
        {
            var sheet = new MapSheet(1, seed);
            sheet.AssignZone(ZoneZero);
            return sheet;
        }

        private static MapPen NewPen(float ink = 1f) => new MapPen("boli", 0xFF2A47A8u, 3f, 0.05f, ink);

        private static string Describe(PendingStroke p) => (p.Vertical ? "V" : "H") + ":" + p.Line + ":" + p.From + "-" + p.To;

        [Test]
        public void AWallNextToRememberedFloorBecomesAnEdge()
        {
            MapMemory memory = NewMemory();
            Add(memory, 0.0, (5, 5, MapCellKind.Floor), (6, 5, MapCellKind.Wall), (20, 20, MapCellKind.Wall));

            var builder = new MapSheetStrokeBuilder();
            Assert.AreEqual(1, builder.Build(NewSheet(), memory, 1.0, 20.0), "la pared suelta, sin suelo al lado, no es arista");
            Assert.AreEqual("V:6:5-6", Describe(builder.Pending[0]));
        }

        [Test]
        public void BorderWallsOfTheNeighbourChunkCloseTheSheet()
        {
            MapMemory memory = NewMemory();
            Add(memory, 0.0, (99, 5, MapCellKind.Floor), (100, 5, MapCellKind.Wall));

            var builder = new MapSheetStrokeBuilder();
            Assert.AreEqual(1, builder.Build(NewSheet(), memory, 1.0, 20.0));
            Assert.AreEqual("V:100:5-6", Describe(builder.Pending[0]));
        }

        [Test]
        public void CollinearEdgesMergeIntoOneStroke()
        {
            MapMemory memory = NewMemory();
            Add(memory, 0.0,
                (5, 5, MapCellKind.Floor), (5, 6, MapCellKind.Floor), (5, 7, MapCellKind.Floor),
                (6, 5, MapCellKind.Wall), (6, 6, MapCellKind.Wall), (6, 7, MapCellKind.Wall));

            var builder = new MapSheetStrokeBuilder();
            Assert.AreEqual(1, builder.Build(NewSheet(), memory, 1.0, 20.0));
            Assert.AreEqual("V:6:5-8", Describe(builder.Pending[0]));
        }

        [Test]
        public void DrawingTheSameMemoryTwiceCostsNothing()
        {
            MapMemory memory = NewMemory();
            Add(memory, 0.0, (5, 5, MapCellKind.Floor), (6, 5, MapCellKind.Wall), (5, 4, MapCellKind.Wall));
            MapSheet sheet = NewSheet();
            MapPen pen = NewPen();
            MapSheetLayer layer = sheet.BeginLayer(pen);

            var builder = new MapSheetStrokeBuilder();
            int count = builder.Build(sheet, memory, 1.0, 20.0);
            for (int i = 0; i < count; i++) Assert.IsTrue(builder.Commit(sheet, layer, i, pen, memory));
            float inkAfterFirst = pen.Ink;

            Assert.AreEqual(2, sheet.EdgeCount);
            Assert.AreEqual(0, builder.Build(sheet, memory, 1.0, 20.0), "lo ya dibujado no vuelve a quedar pendiente");
            Assert.AreEqual(inkAfterFirst, pen.Ink);
        }

        [Test]
        public void OldMemoryDrawsShakyWithGaps()
        {
            MapMemory memory = NewMemory();
            Add(memory, 0.0, (5, 5, MapCellKind.Floor), (6, 5, MapCellKind.Wall));

            var builder = new MapSheetStrokeBuilder();
            builder.Build(NewSheet(), memory, 10.0, 20.0);
            Assert.IsFalse(builder.Pending[0].Old, "a los 10 s es reciente");
            builder.Build(NewSheet(), memory, 30.0, 20.0);
            Assert.IsTrue(builder.Pending[0].Old, "a los 30 s ya es viejo");

            // Cincuenta tramos viejos separados: alguno se queda sin llegar al papel.
            MapMemory wide = NewMemory();
            var cells = new List<(int, int, MapCellKind)>();
            for (int i = 0; i < 50; i++)
            {
                cells.Add((i * 2, 10, MapCellKind.Floor));
                cells.Add((i * 2, 11, MapCellKind.Wall));
            }
            Add(wide, 0.0, cells.ToArray());
            MapSheet sheet = NewSheet();
            MapPen pen = NewPen();
            MapSheetLayer layer = sheet.BeginLayer(pen);
            int count = builder.Build(sheet, wide, 30.0, 20.0);
            for (int i = 0; i < count; i++) builder.Commit(sheet, layer, i, pen, wide);

            Assert.AreEqual(50, count);
            Assert.Less(layer.Strokes.Count, 50, "con un 28 % de huecos no llegan los cincuenta");
            Assert.Greater(layer.Strokes.Count, 20);
        }

        [Test]
        public void RunningOutOfInkCutsTheStroke()
        {
            MapMemory memory = NewMemory();
            Add(memory, 0.0, (5, 5, MapCellKind.Floor), (6, 5, MapCellKind.Wall));
            MapSheet sheet = NewSheet();
            MapPen pen = NewPen(ink: 0.001f);
            MapSheetLayer layer = sheet.BeginLayer(pen);

            var builder = new MapSheetStrokeBuilder();
            builder.Build(sheet, memory, 1.0, 20.0);

            Assert.IsFalse(builder.Commit(sheet, layer, 0, pen, memory));
            Assert.AreEqual(0, layer.Strokes.Count);
            Assert.AreEqual(0, sheet.EdgeCount, "sin tinta la arista sigue pendiente");
        }

        [Test]
        public void SameMemorySameSeedSameDrawing()
        {
            List<float> Draw()
            {
                MapMemory memory = NewMemory();
                Add(memory, 0.0, (5, 5, MapCellKind.Floor), (5, 6, MapCellKind.Floor), (6, 5, MapCellKind.Wall),
                    (6, 6, MapCellKind.Wall), (4, 5, MapCellKind.Wall));
                MapSheet sheet = NewSheet(seed: 777);
                MapPen pen = NewPen();
                MapSheetLayer layer = sheet.BeginLayer(pen);
                var builder = new MapSheetStrokeBuilder();
                int count = builder.Build(sheet, memory, 1.0, 20.0);
                for (int i = 0; i < count; i++) builder.Commit(sheet, layer, i, pen, memory);
                var points = new List<float>();
                foreach (MapStroke stroke in layer.Strokes) points.AddRange(stroke.Points);
                return points;
            }

            List<float> first = Draw();
            Assert.IsNotEmpty(first);
            CollectionAssert.AreEqual(first, Draw());
        }

        [Test]
        public void StrokesComeOutInAStableOrder()
        {
            MapMemory memory = NewMemory();
            Add(memory, 0.0,
                (10, 5, MapCellKind.Wall), (7, 8, MapCellKind.Wall), (4, 5, MapCellKind.Wall),
                (9, 5, MapCellKind.Floor), (7, 7, MapCellKind.Floor), (5, 5, MapCellKind.Floor));

            var builder = new MapSheetStrokeBuilder();
            int count = builder.Build(NewSheet(), memory, 1.0, 20.0);

            var order = new List<string>();
            for (int i = 0; i < count; i++) order.Add(Describe(builder.Pending[i]));
            CollectionAssert.AreEqual(new[] { "H:8:7-8", "V:5:5-6", "V:10:5-6" }, order);
        }
    }
}
