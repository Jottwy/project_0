using System;
using System.Collections.Generic;
using System.IO;
using BackroomsSurvival.Gameplay.Mapping;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-154 P1a: el registro guardable de una hoja y su JSON. El golden común con Rust vive en
    /// <c>tools/dev/fixtures/map_sheet_record.golden.json</c>. Sin UnityEngine: corre también headless.
    /// </summary>
    [TestFixture]
    public class MapSheetRecordTests
    {
        private const uint Paper = 0xFFF1EFE5u;
        private const int CellsPerChunk = 100;
        private const string GoldenRelative = "tools/dev/fixtures/map_sheet_record.golden.json";

        /// <summary>Un registro escrito a mano (no sale de Sketch): lo que tiene que escribir igual el backend.</summary>
        private static MapSheetRecord Handmade()
        {
            var record = new MapSheetRecord
            {
                Id = 42,
                Rev = 3,
                HasZone = true,
                Zone = new MapZone(1, -2, 0),
                Seed = 305419896,
                Clean = false,
                Label = "Pasillo \"B\"",
            };

            var first = new MapSheetRecordLayer { Key = 0, Argb = 0xF22A47A8u, WidthCentiPx = 300 };
            first.Runs.Add(new MapRun(false, -150, 140, 152, false, 0));
            first.Runs.Add(new MapRun(true, 160, -195, -190, true, 2));
            var second = new MapSheetRecordLayer { Key = 2, Argb = 0xFFB02020u, WidthCentiPx = 250 };
            second.Runs.Add(new MapRun(true, 101, -160, -151, false, 0));
            record.Layers.Add(first);
            record.Layers.Add(second);

            record.Links.Add(new MapLink(new MapZone(2, -2, 0), MapLinkSide.East, 99.4f, 40.5f));
            record.Links.Add(new MapLink(new MapZone(1, -2, 1), MapLinkSide.StoreyUp, 20.5f, 30.5f));
            record.Marks.Add(new MapMark(MapMarkKind.HereUnsure, 60.5f, 70.5f, 0xF22A47A8u));
            return record;
        }

        private static string GoldenPath()
        {
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (DirectoryInfo dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, GoldenRelative);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            Assert.IsTrue(false, $"no encuentro {GoldenRelative} subiendo desde el directorio actual");
            return null;
        }

        private static string Golden() => File.ReadAllText(GoldenPath()).TrimEnd('\r', '\n');

        [Test]
        public void WriterMatchesTheSharedGolden()
        {
            string json = Handmade().ToJson();
            Assert.AreEqual(Golden(), json, "si cambia el formato, cambia también el golden y el codec de Rust");
        }

        [Test]
        public void TheGoldenReadsBackToTheSameJson()
        {
            string golden = Golden();
            MapSheetRecord record = MapSheetRecord.FromJson(golden);

            Assert.AreEqual(42UL, record.Id);
            Assert.AreEqual("Pasillo \"B\"", record.Label);
            Assert.AreEqual(3, record.RunCount);
            Assert.AreEqual(golden, record.ToJson());
        }

        [Test]
        public void ADrawnSheetSurvivesTheRoundTripPixelForPixel()
        {
            var memory = new MapMemory(0.5f, 50f, 3.32f, 60f, 0.5f, 441);
            var xs = new List<int>();
            var zs = new List<int>();
            var kinds = new List<MapCellKind>();
            for (int x = 5; x < 25; x++)
            {
                xs.Add(x); zs.Add(20); kinds.Add(MapCellKind.Floor);
                xs.Add(x); zs.Add(19); kinds.Add(MapCellKind.Wall);
                xs.Add(x); zs.Add(21); kinds.Add(MapCellKind.Wall);
            }

            memory.AddSample(1.0, 0, xs.ToArray(), zs.ToArray(), kinds.ToArray(), xs.Count);
            var sheet = new MapSheet(5, 987654);
            sheet.AssignZone(new MapZone(0, 0, 0));
            var pen = new MapPen("boli", 0xF22A47A8u, 3f, 0.001f, 1f);
            MapSheetLayer layer = sheet.BeginLayer(pen);
            var builder = new MapSheetStrokeBuilder();
            int count = builder.Build(sheet, memory, 1.0, 20.0);
            for (int i = 0; i < count; i++) Assert.IsTrue(builder.Commit(sheet, layer, i, pen, memory));
            sheet.Links.Add(new MapLink(new MapZone(1, 0, 0), MapLinkSide.East, 99.4f, 20.5f));
            sheet.Marks.Add(new MapMark(MapMarkKind.Here, 10.5f, 20.5f, pen.Argb));

            string json = MapSheetRecord.FromSheet(sheet, 5, 1).ToJson();
            MapSheet loaded = MapSheetRecord.FromJson(json).ToSheet(CellsPerChunk);

            Assert.AreEqual(sheet.EdgeCount, loaded.EdgeCount);
            Assert.AreEqual(sheet.NextLayerKey, loaded.NextLayerKey);
            var before = new MapSheetRaster();
            before.DrawSheet(sheet, Paper, CellsPerChunk);
            var after = new MapSheetRaster();
            after.DrawSheet(loaded, Paper, CellsPerChunk);
            CollectionAssert.AreEqual(before.Rgba, after.Rgba, "misma hoja tras guardar y cargar");
        }

        [Test]
        public void ValidateRejectsWhatPassesTheCaps()
        {
            MapSheetRecord record = Handmade();
            Assert.IsTrue(record.Validate(out _));

            var big = new MapSheetRecordLayer { Key = 5, Argb = 1u, WidthCentiPx = 100 };
            for (int i = 0; i < MapSheetRecord.MaxRuns; i++) big.Runs.Add(new MapRun(false, i, 0, 1, false, i));
            record.Layers.Add(big);
            Assert.IsFalse(record.Validate(out string runsReason));
            StringAssert.Contains("tramos", runsReason);

            MapSheetRecord labelled = Handmade();
            labelled.Label = new string('x', MapSheetRecord.MaxLabelChars + 1);
            Assert.IsFalse(labelled.Validate(out string labelReason));
            StringAssert.Contains("etiqueta", labelReason);

            MapSheetRecord unordered = Handmade();
            unordered.Layers.Reverse();
            Assert.IsFalse(unordered.Validate(out string orderReason), "las capas no se reordenan");
            StringAssert.Contains("clave", orderReason);
        }

        /// <summary>El shim headless de NUnit no tiene <c>Assert.Throws</c>: se comprueba a mano.</summary>
        private static bool Rejects(string json)
        {
            try
            {
                MapSheetRecord.FromJson(json);
                return false;
            }
            catch (FormatException)
            {
                return true;
            }
        }

        [Test]
        public void MalformedJsonIsRejected()
        {
            Assert.IsTrue(Rejects("{\"id\":1"), "cortado");
            Assert.IsTrue(Rejects("[1,2]"), "no es un objeto");
            Assert.IsTrue(Rejects(Golden().Replace("\"rev\":3", "\"rev\":3.5")), "decimales");
            Assert.IsTrue(Rejects(Golden().Replace("\"seed\"", "\"semilla\"")), "falta una clave");
        }
    }
}
