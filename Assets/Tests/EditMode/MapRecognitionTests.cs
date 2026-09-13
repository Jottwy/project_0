using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Mapping;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>P0.3 de MAPPING-PROTOTYPE: flechas de borde y «Ubicarme». Sin UnityEngine: corre también headless.</summary>
    [TestFixture]
    public class MapRecognitionTests
    {
        // Los números del juego: Wg3ChunkStreamer.ChunkSize y Wg3StoreyLayers.StoreyM (ver MapMemoryTests).
        private static MapMemory NewMemory() => new MapMemory(0.5f, 50f, 3.32f, 60f, 0.5f, 441);

        private static void AddAt(MapMemory memory, double time, int storey, int playerX, int playerZ,
            params (int x, int z, MapCellKind kind)[] cells)
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

            memory.AddSample(time, storey, playerX, playerZ, xs, zs, kinds, cells.Length);
        }

        /// <summary>Un pasillo de X = 5..14 en Z = <paramref name="floorZ"/>, con pared a los dos lados: 20 aristas.</summary>
        private static (int, int, MapCellKind)[] Corridor(int floorZ)
        {
            var cells = new List<(int, int, MapCellKind)>();
            for (int x = 5; x < 15; x++)
            {
                cells.Add((x, floorZ, MapCellKind.Floor));
                cells.Add((x, floorZ - 1, MapCellKind.Wall));
                cells.Add((x, floorZ + 1, MapCellKind.Wall));
            }

            return cells.ToArray();
        }

        private static MapSheet DrawAll(MapMemory memory, double now)
        {
            var sheet = new MapSheet(1, 99);
            sheet.AssignZone(new MapZone(0, 0, 0));
            var pen = new MapPen("boli", 0xFF2A47A8u, 3f, 0.001f, 1f);
            MapSheetLayer layer = sheet.BeginLayer(pen);
            var builder = new MapSheetStrokeBuilder();
            int count = builder.Build(sheet, memory, now, 20.0);
            for (int i = 0; i < count; i++) Assert.IsTrue(builder.Commit(sheet, layer, i, pen, memory));
            memory.Consume(sheet.Zone);
            return sheet;
        }

        [Test]
        public void AWellDrawnSheetIsRecognised()
        {
            MapMemory memory = NewMemory();
            AddAt(memory, 0.0, 0, 8, 5, Corridor(5));
            MapSheet sheet = DrawAll(memory, 1.0);
            AddAt(memory, 30.0, 0, 8, 5, Corridor(5));

            float score = new MapSheetStrokeBuilder().Recognize(sheet, memory, 31.0, 8.0);

            Assert.GreaterOrEqual(score, 0.7, "lo que ves ahora es lo que dibujaste, aunque ese recuerdo ya se pasara al papel");
        }

        [Test]
        public void ABlankZoneIsNotRecognised()
        {
            MapMemory memory = NewMemory();
            AddAt(memory, 0.0, 0, 8, 5, Corridor(5));
            var sheet = new MapSheet(1, 99);
            sheet.AssignZone(new MapZone(0, 0, 0));

            Assert.AreEqual(0f, new MapSheetStrokeBuilder().Recognize(sheet, memory, 1.0, 8.0));
        }

        [Test]
        public void AMovedChunkNoLongerMatches()
        {
            MapMemory memory = NewMemory();
            AddAt(memory, 0.0, 0, 8, 5, Corridor(5));
            MapSheet sheet = DrawAll(memory, 1.0);

            // El mismo sitio con otra geometría: el pasillo está ahora tres celdas más arriba.
            MapMemory moved = NewMemory();
            AddAt(moved, 0.0, 0, 8, 8, Corridor(8));

            Assert.Less(new MapSheetStrokeBuilder().Recognize(sheet, moved, 1.0, 8.0), 0.4);
        }

        [Test]
        public void TooLittleSeenGivesNoVerdict()
        {
            MapMemory memory = NewMemory();
            AddAt(memory, 0.0, 0, 5, 5, (5, 5, MapCellKind.Floor), (6, 5, MapCellKind.Wall), (4, 5, MapCellKind.Wall));
            MapSheet sheet = DrawAll(memory, 1.0);

            Assert.AreEqual(-1f, new MapSheetStrokeBuilder().Recognize(sheet, memory, 1.0, 8.0));
        }

        [Test]
        public void OldMemoryDoesNotCountForRecognition()
        {
            MapMemory memory = NewMemory();
            AddAt(memory, 0.0, 0, 8, 5, Corridor(5));
            MapSheet sheet = DrawAll(memory, 1.0);

            Assert.AreEqual(-1f, new MapSheetStrokeBuilder().Recognize(sheet, memory, 20.0, 8.0),
                "ubicarse es con lo que acabas de ver, no con lo de hace veinte segundos");
        }

        [Test]
        public void CrossingTheBorderAddsOneArrowOnThatSide()
        {
            MapMemory memory = NewMemory();
            AddAt(memory, 0.0, 0, 98, 5);
            AddAt(memory, 0.5, 0, 99, 5);
            AddAt(memory, 1.0, 0, 100, 5);
            AddAt(memory, 1.5, 0, 99, 5); // y vuelta: la misma vecina no da dos flechas

            var sheet = new MapSheet(1, 99);
            sheet.AssignZone(new MapZone(0, 0, 0));
            var builder = new MapSheetStrokeBuilder();

            Assert.AreEqual(1, builder.AddLinks(sheet, memory));
            Assert.AreEqual(MapLinkSide.East, sheet.Links[0].Side);
            Assert.AreEqual(new MapZone(1, 0, 0), sheet.Links[0].To);
            Assert.AreEqual(99.4f, sheet.Links[0].LocalX);
            Assert.AreEqual(5.5f, sheet.Links[0].LocalZ);
            Assert.AreEqual(0, builder.AddLinks(sheet, memory), "volver a dibujar no repite la flecha");
        }

        [Test]
        public void TheNeighbourSheetGetsItsArrowOnTheOppositeSide()
        {
            MapMemory memory = NewMemory();
            AddAt(memory, 0.0, 0, 99, 5);
            AddAt(memory, 0.5, 0, 100, 5);

            var sheet = new MapSheet(2, 99);
            sheet.AssignZone(new MapZone(1, 0, 0));

            Assert.AreEqual(1, new MapSheetStrokeBuilder().AddLinks(sheet, memory));
            Assert.AreEqual(MapLinkSide.West, sheet.Links[0].Side);
            Assert.AreEqual(0.6f, sheet.Links[0].LocalX);
        }

        [Test]
        public void ChangingStoreyAddsAStairLink()
        {
            MapMemory memory = NewMemory();
            AddAt(memory, 0.0, 0, 10, 10);
            AddAt(memory, 0.5, 1, 10, 10);

            var sheet = new MapSheet(1, 99);
            sheet.AssignZone(new MapZone(0, 0, 0));

            Assert.AreEqual(1, new MapSheetStrokeBuilder().AddLinks(sheet, memory));
            Assert.AreEqual(MapLinkSide.StoreyUp, sheet.Links[0].Side);
            Assert.AreEqual(10.5f, sheet.Links[0].LocalX);
            Assert.AreEqual(10.5f, sheet.Links[0].LocalZ);
        }
    }
}
