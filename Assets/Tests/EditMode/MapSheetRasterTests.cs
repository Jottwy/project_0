using BackroomsSurvival.Gameplay.Mapping;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>P0.2 de MAPPING-PROTOTYPE: la hoja pintada. Sin UnityEngine: corre también headless.</summary>
    [TestFixture]
    public class MapSheetRasterTests
    {
        private const uint Paper = 0xFFF1EFE5u;
        private const uint Ink = 0xFF102030u;
        private const int CellsPerChunk = 100;

        private static int RedAt(MapSheetRaster raster, int x, int y) => raster.Rgba[(y * raster.Size + x) * 4];

        [Test]
        public void ClearPaintsEveryPixelWithPaper()
        {
            var raster = new MapSheetRaster();
            raster.Clear(Paper);

            Assert.AreEqual(0xF1, (int)raster.Rgba[0]);
            Assert.AreEqual(0xEF, (int)raster.Rgba[raster.Rgba.Length - 3]);
            Assert.AreEqual(255, (int)raster.Rgba[raster.Rgba.Length - 1]);
        }

        [Test]
        public void RasterPaintsTheStrokeAndNotBeyondItsWidth()
        {
            var raster = new MapSheetRaster();
            raster.Clear(Paper);
            // Tramo horizontal en Z = 50 celdas, de X = 10 a 20: en píxeles, fila 256 y columnas 51..102.
            raster.DrawStroke(new MapStroke(new[] { 10f, 50f, 20f, 50f }, false), Ink, 4f, CellsPerChunk);

            // La carga de tinta varía a lo largo del trazo (presión), así que el centro no es el color exacto.
            Assert.LessOrEqual(RedAt(raster, 77, 256), 0x40, "en el centro del trazo, tinta");
            Assert.AreEqual(0xF1, RedAt(raster, 77, 256 + 8), "a 8 px de un trazo de 4, papel");
            Assert.AreEqual(0xF1, RedAt(raster, 200, 256), "fuera de su largo, papel");
        }

        private static bool InkedNear(MapSheetRaster raster, int cx, int cy, int window)
        {
            for (int y = cy - window; y <= cy + window; y++)
                for (int x = cx - window; x <= cx + window; x++)
                    if (x >= 0 && y >= 0 && x < raster.Size && y < raster.Size && RedAt(raster, x, y) != 0xF1)
                        return true;
            return false;
        }

        [Test]
        public void ArrowsAndHereMarksArePainted()
        {
            var sheet = new MapSheet(1, 1);
            sheet.AssignZone(new MapZone(0, 0, 0));
            sheet.Links.Add(new MapLink(new MapZone(1, 0, 0), MapLinkSide.East, 99.4f, 50f));
            sheet.Marks.Add(new MapMark(MapMarkKind.Here, 50f, 20f, Ink));

            var raster = new MapSheetRaster();
            raster.DrawSheet(sheet, Paper, CellsPerChunk);

            Assert.IsTrue(InkedNear(raster, 505, 256, 3), "la flecha llega al borde este");
            Assert.IsTrue(InkedNear(raster, 256 + 3, 102, 1), "el círculo de «estás aquí» se pinta");
            Assert.IsFalse(InkedNear(raster, 256, 400, 3), "y lejos de todo, papel");
        }

        [Test]
        public void OldStrokesAreDashed()
        {
            var raster = new MapSheetRaster();
            raster.Clear(Paper);
            raster.DrawStroke(new MapStroke(new[] { 0f, 50f, 100f, 50f }, true), Ink, 3f, CellsPerChunk);

            int inked = 0, paper = 0;
            for (int x = 20; x < 490; x++)
            {
                if (RedAt(raster, x, 256) == 0xF1) paper++;
                else inked++;
            }

            Assert.Greater(inked, 100, "lo viejo se sigue viendo");
            Assert.Greater(paper, 100, "pero a tramos");
        }
    }
}
