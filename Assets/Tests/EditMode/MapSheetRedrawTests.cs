using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Mapping;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-154 P1a: una hoja rehecha desde sus tramos guardados se pinta IDÉNTICA a la dibujada. Sin UnityEngine: corre
    /// también headless.
    /// </summary>
    [TestFixture]
    public class MapSheetRedrawTests
    {
        private const uint Paper = 0xFFF1EFE5u;
        private const int CellsPerChunk = 100;

        /// <summary>
        /// Un pasillo reciente (X = 5..14, Z = 20) visto en t = 49 y otro viejo (X = 30..44, Z = 60) visto en t = 1: con
        /// «ahora» = 50 y viejo a partir de 20 s salen trazos frescos y viejos (con su sorteo de huecos).
        /// </summary>
        private static MapMemory Memory()
        {
            var memory = new MapMemory(0.5f, 50f, 3.32f, 60f, 0.5f, 441);
            AddCorridor(memory, 1.0, 30, 45, 60);
            AddCorridor(memory, 49.0, 5, 15, 20);
            return memory;
        }

        private static void AddCorridor(MapMemory memory, double time, int fromX, int toX, int z)
        {
            var xs = new List<int>();
            var zs = new List<int>();
            var kinds = new List<MapCellKind>();
            for (int x = fromX; x < toX; x++)
            {
                xs.Add(x); zs.Add(z); kinds.Add(MapCellKind.Floor);
                xs.Add(x); zs.Add(z - 1); kinds.Add(MapCellKind.Wall);
                xs.Add(x); zs.Add(z + 1); kinds.Add(MapCellKind.Wall);
            }

            memory.AddSample(time, 0, xs.ToArray(), zs.ToArray(), kinds.ToArray(), xs.Count);
        }

        private static MapSheet DrawTwoLayers()
        {
            MapMemory memory = Memory();
            var sheet = new MapSheet(7, 123457);
            sheet.AssignZone(new MapZone(0, 0, 0));
            var builder = new MapSheetStrokeBuilder();
            var pen = new MapPen("boli", 0xF22A47A8u, 3f, 0.001f, 1f);

            // Primera capa: solo el pasillo viejo (el reciente aún no se ha visto).
            var early = new MapMemory(0.5f, 50f, 3.32f, 60f, 0.5f, 441);
            AddCorridor(early, 1.0, 30, 45, 60);
            MapSheetLayer first = sheet.BeginLayer(pen);
            int count = builder.Build(sheet, early, 50.0, 20.0);
            for (int i = 0; i < count; i++) Assert.IsTrue(builder.Commit(sheet, first, i, pen, early));

            // Segunda capa: lo que falta.
            MapSheetLayer second = sheet.BeginLayer(new MapPen("rojo", 0xFFB02020u, 2.5f, 0.001f, 1f));
            count = builder.Build(sheet, memory, 50.0, 20.0);
            for (int i = 0; i < count; i++) Assert.IsTrue(builder.Commit(sheet, second, i, pen, memory));
            return sheet;
        }

        /// <summary>Copia lo que se guardaría (zona, semilla, capas con clave y tramos, enlaces) sin ningún trazo.</summary>
        private static MapSheet CopyRuns(MapSheet source)
        {
            var copy = new MapSheet(source.Id, source.Seed, source.Clean);
            copy.AssignZone(source.Zone);
            foreach (MapSheetLayer layer in source.Layers)
            {
                MapSheetLayer target = copy.AddLayer(layer.Key, layer.Argb, layer.WidthPx);
                target.Runs.AddRange(layer.Runs);
            }

            copy.Links.AddRange(source.Links);
            copy.Marks.AddRange(source.Marks);
            return copy;
        }

        private static byte[] Paint(MapSheet sheet)
        {
            var raster = new MapSheetRaster();
            raster.DrawSheet(sheet, Paper, CellsPerChunk);
            return raster.Rgba;
        }

        [Test]
        public void LayerKeysFollowTheOrderTheLayersWereBegun()
        {
            MapSheet sheet = DrawTwoLayers();

            Assert.AreEqual(2, sheet.Layers.Count);
            Assert.AreEqual(0, sheet.Layers[0].Key);
            Assert.AreEqual(1, sheet.Layers[1].Key);
            Assert.AreEqual(2, sheet.NextLayerKey);
            Assert.Greater(sheet.Layers[0].Runs.Count, 0, "la primera capa guarda sus tramos");
            Assert.Greater(sheet.Layers[1].Runs.Count, 0, "y la segunda los suyos");
        }

        [Test]
        public void RedrawFromRunsPaintsTheSamePixels()
        {
            MapSheet drawn = DrawTwoLayers();
            MapSheet loaded = CopyRuns(drawn);

            MapSheetStrokeBuilder.Redraw(loaded, CellsPerChunk);

            Assert.AreEqual(drawn.EdgeCount, loaded.EdgeCount, "las mismas paredes, para «Ubicarme»");
            for (int l = 0; l < drawn.Layers.Count; l++)
                Assert.AreEqual(drawn.Layers[l].Strokes.Count, loaded.Layers[l].Strokes.Count, $"trazos de la capa {l}");
            CollectionAssert.AreEqual(Paint(drawn), Paint(loaded), "rasterización idéntica píxel a píxel");
        }

        [Test]
        public void OldRunsThatFellInAGapAreNotStored()
        {
            MapSheet drawn = DrawTwoLayers();

            int storedCells = 0;
            foreach (MapSheetLayer layer in drawn.Layers)
                foreach (MapRun run in layer.Runs)
                    storedCells += run.LengthCells;

            Assert.AreEqual(drawn.EdgeCount, storedCells, "cada arista marcada sale de un tramo guardado y viceversa");
        }

        [Test]
        public void ACleanCopyRedrawsStraight()
        {
            MapSheet clean = MapAtlas.CleanCopy(DrawTwoLayers(), 9, CellsPerChunk, new List<long>());
            MapSheet loaded = CopyRuns(clean);

            MapSheetStrokeBuilder.Redraw(loaded, CellsPerChunk);

            Assert.AreEqual(clean.EdgeCount, loaded.EdgeCount);
            CollectionAssert.AreEqual(Paint(clean), Paint(loaded));
        }
    }
}
