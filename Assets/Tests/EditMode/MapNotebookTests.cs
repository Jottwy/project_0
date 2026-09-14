using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Mapping;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>P0.5 de MAPPING-PROTOTYPE: la libreta sin pantalla. Sin UnityEngine: corre también headless.</summary>
    [TestFixture]
    public class MapNotebookTests
    {
        private static readonly MapHere Here = new MapHere(true, 10, 20, 0);

        /// <summary>Recuerdo de un pasillo de X = 5..14 en Z = 20 con pared a los dos lados: 2 trazos, 20 aristas.</summary>
        private static MapMemory CorridorMemory(double time)
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

            memory.AddSample(time, 0, Here.CellX, Here.CellZ, xs.ToArray(), zs.ToArray(), kinds.ToArray(), xs.Count);
            return memory;
        }

        private static MapNotebook NewNotebook() => new MapNotebook(new MapPen("boli", 0xFF2A47A8u, 3f, 0.001f, 1f));

        [Test]
        public void TakeSheetAssignsTheCurrentZone()
        {
            MapNotebook notebook = NewNotebook();
            MapMemory memory = CorridorMemory(1.0);

            Assert.IsFalse(notebook.TakeSheet(memory, new MapHere(false, 0, 0, 0)), "sin recuerdo no hay hoja");
            Assert.IsTrue(notebook.TakeSheet(memory, new MapHere(true, 250, -3, 2)));

            Assert.AreEqual(1, notebook.Sheets.Count);
            Assert.AreEqual(0, notebook.CurrentIndex);
            Assert.AreEqual(new MapZone(2, -1, 2), notebook.CurrentSheet.Zone);
        }

        [Test]
        public void DrawingCommitsOverTimeAndConsumesOnlyWhenComplete()
        {
            MapNotebook notebook = NewNotebook();
            MapMemory memory = CorridorMemory(1.0);
            notebook.TakeSheet(memory, Here);

            notebook.StartDrawing(memory, 1.0, 20.0);
            Assert.IsTrue(notebook.Drawing);
            Assert.AreEqual(2, notebook.DrawCount);
            int version = notebook.Version;

            notebook.Step(memory, 10f);

            Assert.IsFalse(notebook.Drawing);
            Assert.AreEqual(2, notebook.DrawCommitted);
            Assert.Greater(notebook.Version, version, "la hoja cambió: hay que repintar");
            Assert.AreEqual(20, notebook.CurrentSheet.EdgeCount);
            var cells = new List<RememberedCell>();
            memory.CellsInZone(notebook.CurrentSheet.Zone, 1.0, cells);
            Assert.AreEqual(0, cells.Count, "dibujado entero: sale del recuerdo");
        }

        [Test]
        public void CancelKeepsWhatWasDrawn()
        {
            MapNotebook notebook = NewNotebook();
            MapMemory memory = CorridorMemory(1.0);
            notebook.TakeSheet(memory, Here);
            notebook.StartDrawing(memory, 1.0, 20.0);

            // A 12 trazos por segundo, 0,09 s dan un trazo y no dos.
            notebook.Step(memory, 0.09f);
            notebook.Cancel("Te has movido.");

            Assert.IsFalse(notebook.Drawing);
            Assert.AreEqual(1, notebook.DrawCommitted);
            Assert.AreEqual(10, notebook.CurrentSheet.EdgeCount, "lo trazado se queda");
            var cells = new List<RememberedCell>();
            memory.CellsInZone(notebook.CurrentSheet.Zone, 1.0, cells);
            Assert.Greater(cells.Count, 0, "lo que faltaba sigue en el recuerdo");
            Assert.AreEqual("Te has movido.", notebook.Status);
        }

        [Test]
        public void LocateMarksAndRemembersTheFix()
        {
            MapNotebook notebook = NewNotebook();
            MapMemory memory = CorridorMemory(1.0);

            Assert.AreEqual(MapLocateResult.Blank, notebook.Locate(memory, Here, 2.0, 5f), "sin hojas, en blanco");
            Assert.IsTrue(notebook.OfferMapHere);

            notebook.TakeSheet(memory, Here);
            notebook.StartDrawing(memory, 1.0, 20.0);
            notebook.Step(memory, 10f);

            Assert.AreEqual(MapLocateResult.Sure, notebook.Locate(memory, Here, 2.0, 7f));
            Assert.IsTrue(notebook.HasFix);
            Assert.AreEqual(0, notebook.FixSheetIndex);
            Assert.AreEqual(new MapZone(0, 0, 0), notebook.FixZone);
            Assert.AreEqual(10.5f, notebook.FixLocalX);
            Assert.AreEqual(20.5f, notebook.FixLocalZ);
            Assert.AreEqual(7f, notebook.FixTime);
            Assert.AreEqual(1, notebook.CurrentSheet.Marks.Count);
            Assert.IsFalse(notebook.OfferMapHere);
        }
    }
}
