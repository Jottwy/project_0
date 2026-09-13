using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Mapping;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>P0.4 de MAPPING-PROTOTYPE: el plano maestro de la base. Sin UnityEngine: corre también headless.</summary>
    [TestFixture]
    public class MapAtlasTests
    {
        private const int CellsPerChunk = 100;

        private static MapSheet Sheet(int id, int chunkX, int chunkZ, int storey = 0)
        {
            var sheet = new MapSheet(id, id * 31);
            sheet.AssignZone(new MapZone(chunkX, chunkZ, storey));
            return sheet;
        }

        private static void Link(MapSheet sheet, int chunkX, int chunkZ, MapLinkSide side, int storey = 0) =>
            sheet.Links.Add(new MapLink(new MapZone(chunkX, chunkZ, storey), side, 50f, 50f));

        /// <summary>Hoja con un pasillo dibujado de verdad: X = 5..14 en Z = 20, pared a los dos lados.</summary>
        private static MapSheet DrawnSheet(int id)
        {
            var memory = new MapMemory(0.5f, 50f, 3.32f, 60f, 0.5f, 441);
            var xs = new List<int>();
            var zs = new List<int>();
            var kinds = new List<MapCellKind>();
            for (int x = 5; x < 15; x++)
            {
                xs.Add(x); zs.Add(20); kinds.Add(MapCellKind.Floor);
                xs.Add(x); zs.Add(19); kinds.Add(MapCellKind.Wall);
                xs.Add(x); zs.Add(21); kinds.Add(MapCellKind.Wall);
            }

            memory.AddSample(1.0, 0, xs.ToArray(), zs.ToArray(), kinds.ToArray(), xs.Count);
            MapSheet sheet = Sheet(id, 0, 0);
            var pen = new MapPen("boli", 0xFF2A47A8u, 3f, 0.001f, 1f);
            MapSheetLayer layer = sheet.BeginLayer(pen);
            var builder = new MapSheetStrokeBuilder();
            int count = builder.Build(sheet, memory, 1.0, 20.0);
            for (int i = 0; i < count; i++) Assert.IsTrue(builder.Commit(sheet, layer, i, pen, memory));
            return sheet;
        }

        [Test]
        public void TheBaseIsAlwaysPlaced()
        {
            var atlas = new MapAtlas();
            atlas.SetBase(new MapZone(3, 4, 0));
            var placed = new List<MapZone>();
            var unplaced = new List<MapZone>();

            atlas.Place(new List<MapSheet>(), placed, unplaced);

            CollectionAssert.AreEqual(new[] { new MapZone(3, 4, 0) }, placed);
            Assert.AreEqual(0, unplaced.Count);
        }

        [Test]
        public void ASheetWithoutLinkIsNeverPlaced()
        {
            var atlas = new MapAtlas();
            atlas.SetBase(new MapZone(0, 0, 0));
            var placed = new List<MapZone>();
            var unplaced = new List<MapZone>();

            // Justo al lado de la base, pero sin flecha que lo demuestre.
            atlas.Place(new List<MapSheet> { Sheet(1, 1, 0) }, placed, unplaced);

            CollectionAssert.AreEqual(new[] { new MapZone(0, 0, 0) }, placed);
            CollectionAssert.AreEqual(new[] { new MapZone(1, 0, 0) }, unplaced);
        }

        [Test]
        public void ALinkPlacesTheNeighbourAndTheChainFollows()
        {
            var atlas = new MapAtlas();
            atlas.SetBase(new MapZone(0, 0, 0));
            MapSheet baseSheet = Sheet(1, 0, 0);
            Link(baseSheet, 1, 0, MapLinkSide.East);
            MapSheet east = Sheet(2, 1, 0);
            MapSheet farther = Sheet(3, 2, 0);
            // La flecha está en la hoja de la vecina lejana, apuntando hacia atrás: también demuestra.
            Link(farther, 1, 0, MapLinkSide.West);
            MapSheet lonely = Sheet(4, 5, 5);
            var placed = new List<MapZone>();
            var unplaced = new List<MapZone>();

            atlas.Place(new List<MapSheet> { lonely, farther, east, baseSheet }, placed, unplaced);

            CollectionAssert.AreEqual(new[] { new MapZone(0, 0, 0), new MapZone(1, 0, 0), new MapZone(2, 0, 0) }, placed);
            CollectionAssert.AreEqual(new[] { new MapZone(5, 5, 0) }, unplaced);
        }

        [Test]
        public void PlacementOrderIsStable()
        {
            var atlas = new MapAtlas();
            atlas.SetBase(new MapZone(0, 0, 0));
            MapSheet a = Sheet(1, 0, 0);
            Link(a, 0, 1, MapLinkSide.North);
            Link(a, 1, 0, MapLinkSide.East);
            Link(a, 0, 0, MapLinkSide.StoreyUp, 1);
            MapSheet b = Sheet(2, 0, 1);
            MapSheet c = Sheet(3, 1, 0);
            MapSheet d = Sheet(4, 0, 0, 1);

            var first = new List<MapZone>();
            var second = new List<MapZone>();
            var unplaced = new List<MapZone>();
            atlas.Place(new List<MapSheet> { a, b, c, d }, first, unplaced);
            atlas.Place(new List<MapSheet> { d, c, b, a }, second, unplaced);

            CollectionAssert.AreEqual(first, second);
            CollectionAssert.AreEqual(
                new[] { new MapZone(0, 0, 0), new MapZone(1, 0, 0), new MapZone(0, 1, 0), new MapZone(0, 0, 1) }, first);
        }

        [Test]
        public void ANewCleanCoversAndArchivesTheOld()
        {
            var atlas = new MapAtlas();
            var scratch = new List<long>();
            MapSheet source = DrawnSheet(1);
            MapSheet old = MapAtlas.CleanCopy(source, 10, CellsPerChunk, scratch);
            MapSheet newer = MapAtlas.CleanCopy(source, 11, CellsPerChunk, scratch);

            atlas.AddClean(old);
            atlas.AddClean(newer);

            Assert.AreEqual(1, atlas.Cleans.Count);
            Assert.AreSame(newer, atlas.CleanOf(new MapZone(0, 0, 0)));
            Assert.AreEqual(1, atlas.Archived.Count);
            Assert.AreSame(old, atlas.Archived[0]);
        }

        [Test]
        public void CleanCopyKeepsEdgesAndDrawsOneStraightLinePerWall()
        {
            MapSheet source = DrawnSheet(1);
            Link(source, 1, 0, MapLinkSide.East);

            MapSheet clean = MapAtlas.CleanCopy(source, 2, CellsPerChunk, new List<long>());

            Assert.IsTrue(clean.Clean);
            Assert.AreEqual(source.EdgeCount, clean.EdgeCount, "las mismas paredes, para que «Ubicarme» la reconozca");
            Assert.AreEqual(1, clean.Links.Count);
            Assert.AreEqual(1, clean.Layers.Count);
            Assert.AreEqual(2, clean.Layers[0].Strokes.Count, "dos paredes de 10 celdas, una línea cada una");
            foreach (MapStroke stroke in clean.Layers[0].Strokes)
            {
                Assert.IsTrue(stroke.Steady);
                Assert.AreEqual(4, stroke.Points.Length, "recta: dos puntos");
                Assert.AreEqual(stroke.Points[1], stroke.Points[3], "horizontal, sin temblor");
                Assert.IsTrue(System.Math.Abs(stroke.Points[2] - stroke.Points[0] - 10f) < 1e-4f, "10 celdas de largo");
            }
        }

        [Test]
        public void DecodeInvertsEdgeKey()
        {
            foreach (var (vertical, line, pos) in new[] { (true, 0, 0), (false, -7, 123), (true, 40000, -250000) })
            {
                MapAtlas.Decode(MapSheetStrokeBuilder.EdgeKey(vertical, line, pos), out bool v, out int l, out int p);
                Assert.AreEqual(vertical, v);
                Assert.AreEqual(line, l);
                Assert.AreEqual(pos, p);
            }
        }
    }
}
