using BackroomsSurvival.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>D13 — secciones plegables y reordenables, con orden y plegado guardados en el PC.</summary>
    public class BackroomsInventorySectionsTests
    {
        private static readonly string[] Ids = { "bag", "pockets" };

        [Test]
        public void ElOrdenGuardadoSeLeeYLoRaroSeIgnora()
        {
            CollectionAssert.AreEqual(new[] { 0, 1 }, BackroomsInventorySections.ParseOrder("", Ids), "sin guardar: orden por defecto");
            CollectionAssert.AreEqual(new[] { 1, 0 }, BackroomsInventorySections.ParseOrder("pockets,bag", Ids));
            CollectionAssert.AreEqual(new[] { 0, 1 }, BackroomsInventorySections.ParseOrder("basura,bag,bag", Ids),
                "ids desconocidos y repetidos fuera; lo que falta, al final");
            CollectionAssert.AreEqual(new[] { false, true }, BackroomsInventorySections.ParseFolded("pockets,otra", Ids));
        }

        [Test]
        public void MoverSacaEInsertaSinPerderNada()
        {
            var order = new[] { 0, 1, 2 };
            BackroomsInventorySections.Move(order, 0, 2);
            CollectionAssert.AreEqual(new[] { 1, 2, 0 }, order);
            BackroomsInventorySections.Move(order, 2, 0);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, order);
        }

        [Test]
        public void LaAlturaSigueALosHuecosVisibles()
        {
            // Cabecera 34, relleno 8/16, casilla 72, separación 8, 10 columnas (las medidas del greybox).
            Assert.AreEqual(42f, BackroomsInventorySections.SectionHeight(0, 10, 34f, 8f, 16f, 72f, 8f), "sin mochila: solo la cabecera");
            Assert.AreEqual(130f, BackroomsInventorySections.SectionHeight(6, 10, 34f, 8f, 16f, 72f, 8f), "bolsa de tela: una fila");
            Assert.AreEqual(290f, BackroomsInventorySections.SectionHeight(27, 10, 34f, 8f, 16f, 72f, 8f), "mochila de montaña: tres filas");
        }

        [Test]
        public void LaVarianteCableaLasDosSeccionesYSusCabeceras()
        {
            var variant = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/BR_UI_Player.prefab");
            Assert.IsNotNull(variant, "falta BR_UI_Player: Backrooms/UI/Build Inventory Variant");
            var sections = variant.GetComponentInChildren<BackroomsInventorySections>(true);
            Assert.IsNotNull(sections, "sin BackroomsInventorySections en el centro");
            var so = new SerializedObject(sections);
            Assert.AreEqual(2, so.FindProperty("_ids").arraySize);
            foreach (var field in new[] { "_sections", "_grids", "_foldMarks" })
                for (int i = 0; i < 2; i++)
                    Assert.IsNotNull(so.FindProperty(field).GetArrayElementAtIndex(i).objectReferenceValue, $"{field}[{i}] sin asignar");

            var headers = variant.GetComponentsInChildren<BackroomsSectionHeader>(true);
            Assert.AreEqual(2, headers.Length, "cada sección necesita su franja de cabecera");
            foreach (var header in headers)
                Assert.AreEqual(sections, new SerializedObject(header).FindProperty("_owner").objectReferenceValue);
        }

        [Test]
        public void ElCinturonDeJuegoEsMasGrandeYSinRotulo()
        {
            var game = BackroomsBeltHud.Measure(6, 0f, 88f, 72f, 16f, 44f, 16f, 8f);
            var inventory = BackroomsBeltHud.Measure(6, 1f, 88f, 72f, 16f, 44f, 16f, 8f);
            Assert.AreEqual(88f, game.cell, "en juego los huecos crecen");
            Assert.AreEqual(72f, inventory.cell, "con TAB, las casillas del greybox");
            Assert.AreEqual(6 * 88f + 5 * 8f + 32f, game.width, "la caja se ajusta a lo que ocupan los huecos");
            Assert.Less(game.top, inventory.top, "sin rótulo, los huecos suben");

            var variant = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/BR_UI_Player.prefab");
            var hud = variant.GetComponentInChildren<BackroomsBeltHud>(true);
            Assert.IsNotNull(hud, "sin BackroomsBeltHud en el cinturón");
            var so = new SerializedObject(hud);
            foreach (var field in new[] { "_box", "_layout", "_title", "_selectionFrame" })
                Assert.IsNotNull(so.FindProperty(field).objectReferenceValue, $"BackroomsBeltHud.{field} sin asignar");
        }

        [Test]
        public void ElZoomPorZonaEncuadraUnHuesoQueExisteYEstaCableado()
        {
            float wholeBody = 2f * 5.9f * Mathf.Tan(9f * Mathf.Deg2Rad);
            Assert.AreEqual(18f, BackroomsPreviewZoom.FovForSpan(wholeBody, 5.9f), 0.01f, "el cuerpo entero es el fov del vendor");
            Assert.Less(BackroomsPreviewZoom.FovForSpan(0.7f, 5.9f), 18f, "la cabeza se ve más cerca");

            var rig = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/PolymindGames/STP/Prefabs/UI/Inventory/Preview/STP_UI_CharacterPreview.prefab");
            Assert.IsNotNull(rig, "falta el rig del preview del vendor");
            var names = new System.Collections.Generic.HashSet<string>();
            foreach (var t in rig.GetComponentsInChildren<Transform>(true)) names.Add(t.name);
            foreach (var zone in BackroomsPreviewZoom.Zones)
                foreach (var bone in zone.Bones)
                    Assert.IsTrue(names.Contains(bone), $"la zona {zone.Id} apunta a '{bone}', que el rig no tiene");

            var variant = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/BR_UI_Player.prefab");
            var zoom = variant.GetComponentInChildren<BackroomsPreviewZoom>(true);
            Assert.IsNotNull(zoom, "sin BackroomsPreviewZoom en el preview");
            var so = new SerializedObject(zoom);
            Assert.IsNotNull(so.FindProperty("_camera").objectReferenceValue, "el zoom no tiene la cámara del vendor");
            Assert.IsNotNull(so.FindProperty("_characterVisuals").objectReferenceValue, "el zoom no tiene CharacterVisuals");
            var headers = variant.GetComponentsInChildren<BackroomsZoneHeader>(true);
            Assert.AreEqual(6, headers.Length, "una cinta con zoom por slot de equipo");
            foreach (var header in headers)
            {
                Assert.GreaterOrEqual(BackroomsPreviewZoom.IndexOf(header.Zone), 0, $"cinta con zona desconocida '{header.Zone}'");
                Assert.AreEqual(zoom, new SerializedObject(header).FindProperty("_zoom").objectReferenceValue);
            }
        }
    }
}
