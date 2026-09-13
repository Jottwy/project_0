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
    }
}
