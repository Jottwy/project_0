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
            Assert.AreEqual(4, so.FindProperty("_ids").arraySize);
            foreach (var field in new[] { "_sections", "_grids", "_foldMarks" })
                for (int i = 0; i < 2; i++)
                    Assert.IsNotNull(so.FindProperty(field).GetArrayElementAtIndex(i).objectReferenceValue, $"{field}[{i}] sin asignar");

            var headers = variant.GetComponentsInChildren<BackroomsSectionHeader>(true);
            Assert.AreEqual(4, headers.Length, "cada sección necesita su franja de cabecera");
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

            // Joel (2026-09-14): en juego solo huecos, visibles al usarlo y desvanecidos a los 2 s.
            Assert.AreEqual(1f, BackroomsBeltHud.FadeTarget(true, 10f, 0f, 0.85f), "con TAB, entero");
            Assert.AreEqual(0.85f, BackroomsBeltHud.FadeTarget(false, 10f, 11f, 0.85f), "recién usado, se ve");
            Assert.AreEqual(0f, BackroomsBeltHud.FadeTarget(false, 12f, 11f, 0.85f), "pasado el rato, desaparece");
            var fade = new SerializedObject(hud);
            foreach (var field in new[] { "_strap", "_slotsGroup", "_frameGroup" })
                Assert.IsNotNull(fade.FindProperty(field).objectReferenceValue, $"BackroomsBeltHud.{field} sin asignar");
            Assert.AreEqual(0f, new SerializedObject(hud.GetComponent("HotbarUI"))
                .FindProperty("_holsterVisibleDuration").floatValue, "el vendor no debe esconderlo a los 5 s: lo lleva el fundido");
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
            Assert.IsNotNull(so.FindProperty("_rotationHandler").objectReferenceValue, "el zoom no puede dar la vuelta al muñeco");
            Assert.AreEqual(180f, BackroomsPreviewZoom.Zones[BackroomsPreviewZoom.IndexOf("Back")].Facing, "Backpack enseña la espalda");

            var backdrop = variant.GetComponentInChildren<BackroomsBodyViewToggle>(true).transform.Find("BR_PreviewBackdrop");
            Assert.IsNotNull(backdrop, "el render no llena la caja: falta BR_PreviewBackdrop");
            var backdropImage = backdrop.GetComponent<UnityEngine.UI.RawImage>();
            Assert.IsFalse(backdropImage.raycastTarget, "el fondo del render no puede tapar los clics a los slots");
            Assert.IsNotNull(backdropImage.texture, "el fondo sin el render del preview");
            Assert.Less(backdrop.GetSiblingIndex(), backdrop.parent.Find("Containers").GetSiblingIndex(), "el render va detrás de los slots");
            var headers = variant.GetComponentsInChildren<BackroomsZoneHeader>(true);
            Assert.AreEqual(10, headers.Length, "una cinta con zoom por slot de equipo (Cara, Encima y un guante por mano)");
            foreach (var header in headers)
            {
                Assert.GreaterOrEqual(BackroomsPreviewZoom.IndexOf(header.Zone), 0, $"cinta con zona desconocida '{header.Zone}'");
                Assert.AreEqual(zoom, new SerializedObject(header).FindProperty("_zoom").objectReferenceValue);
            }
        }

        [Test]
        public void ElPulidoFundeLosBordesYSuavizaLasSecciones()
        {
            Assert.AreEqual(1f, BackroomsEdgeFade.Edge(0f, 48f), 1e-4f, "en el borde, el color de la caja");
            Assert.AreEqual(0f, BackroomsEdgeFade.Edge(48f, 48f), 1e-4f, "pasado el fundido, el render limpio");
            Assert.AreEqual(0f, BackroomsEdgeFade.Edge(10f, 0f), "sin ancho no hay fundido");

            float t = BackroomsInventorySections.Approach01(14f, 1f / 60f);
            Assert.That(t, Is.InRange(0.1f, 0.35f), "un frame a 60 fps recorre una parte, no todo");
            Assert.AreEqual(1f - (1f - t) * (1f - t), BackroomsInventorySections.Approach01(14f, 1f / 30f), 1e-4f,
                "un frame a 30 fps recorre lo mismo que dos a 60");
            Assert.AreEqual(100f, BackroomsInventorySections.Settle(99.8f, 100f, t, 0.5f), "cerca del destino se clava");
            Assert.Less(BackroomsInventorySections.Settle(0f, 100f, t, 0.5f), 100f, "lejos, avanza sin saltar");

            foreach (var zone in BackroomsPreviewZoom.Zones)
                Assert.IsNotEmpty(zone.Label, $"la zona {zone.Id} no tiene rótulo");

            var variant = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/BR_UI_Player.prefab");
            var zoom = new SerializedObject(variant.GetComponentInChildren<BackroomsPreviewZoom>(true));
            Assert.IsNotNull(zoom.FindProperty("_zoneTag").objectReferenceValue, "sin cinta con la zona enfocada");
            Assert.IsNotNull(zoom.FindProperty("_zoneText").objectReferenceValue);
            var fade = variant.GetComponentInChildren<BackroomsEdgeFade>(true);
            Assert.IsNotNull(fade, "sin fundido de bordes en el preview");
            var fadeImage = fade.GetComponent<UnityEngine.UI.RawImage>();
            Assert.IsFalse(fadeImage.raycastTarget, "el fundido no puede tapar clics");
            Assert.IsFalse(fadeImage.enabled, "apagado hasta tener textura: sin ella pinta un bloque blanco");
        }

        [Test]
        public void AbrirCerrarYMoverObjetosTienenRespuesta()
        {
            Assert.IsTrue(BackroomsInventoryTransition.IsFlip(0f, 1f), "abrir de golpe se suaviza");
            Assert.IsTrue(BackroomsInventoryTransition.IsFlip(1f, 0f), "cerrar de golpe se suaviza");
            Assert.IsFalse(BackroomsInventoryTransition.IsFlip(0.4f, 1f), "lo que ya se anima solo (secciones, zoom) no se toca");

            Assert.AreEqual(1f, BackroomsSlotFeedback.PopScale(0f), 1e-4f);
            Assert.AreEqual(1f, BackroomsSlotFeedback.PopScale(1f), 1e-4f, "el rebote acaba en su tamaño");
            Assert.Greater(BackroomsSlotFeedback.PopScale(0.3f), 1.05f, "el rebote se ve");

            Assert.AreEqual(0f, BackroomsSlotFeedback.LoadWarning(0.5f, 0.85f, 1f), "carga normal, sin aviso");
            Assert.AreEqual(1f, BackroomsSlotFeedback.LoadWarning(1f, 0.85f, 0f), "en el máximo, rojo fijo");
            Assert.That(BackroomsSlotFeedback.LoadWarning(0.9f, 0.85f, 0.5f), Is.InRange(0.3f, 0.95f), "cerca del máximo, parpadeo");

            var variant = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/UI/BR_UI_Player.prefab");
            var transition = variant.GetComponentInChildren<BackroomsInventoryTransition>(true);
            Assert.IsNotNull(transition, "sin BackroomsInventoryTransition");
            var ts = new SerializedObject(transition);
            var columns = ts.FindProperty("_columns");
            Assert.AreEqual(3, columns.arraySize, "las tres columnas se deslizan");
            for (int i = 0; i < columns.arraySize; i++)
                Assert.IsNotNull(columns.GetArrayElementAtIndex(i).objectReferenceValue, $"columna {i} sin asignar");
            Assert.AreEqual(3, ts.FindProperty("_slides").arraySize);

            var feedback = variant.GetComponentInChildren<BackroomsSlotFeedback>(true);
            Assert.IsNotNull(feedback, "sin BackroomsSlotFeedback");
            var fs = new SerializedObject(feedback);
            foreach (var field in new[] { "_rejectTag", "_rejectGroup", "_rejectText", "_loadText", "_loadFill" })
                Assert.IsNotNull(fs.FindProperty(field).objectReferenceValue, $"BackroomsSlotFeedback.{field} sin asignar");
            var tagGroup = (CanvasGroup)fs.FindProperty("_rejectGroup").objectReferenceValue;
            Assert.AreEqual(0f, tagGroup.alpha, "la cinta de rechazo empieza oculta");
            Assert.IsFalse(tagGroup.blocksRaycasts, "la cinta de rechazo no tapa clics");
        }
    }
}
