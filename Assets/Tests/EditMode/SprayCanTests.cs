using BackroomsSurvival.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-068 S3 — la economía del bote (decisión 8: el bote se gasta). Se prueba el
    /// componente suelto, sin jugador ni wieldable: lo que puede fallar aquí es la aritmética
    /// del gasto, y eso no necesita escena.
    /// </summary>
    [TestFixture]
    public class SprayCanTests
    {
        private GameObject _go;
        private SprayCan _can;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("can");
            _can = _go.AddComponent<SprayCan>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_go);

        [Test]
        public void ANewCanStartsFull()
        {
            Assert.IsFalse(_can.IsEmpty);
            Assert.AreEqual(1f, _can.PaintFraction, 1e-3f);
        }

        [Test]
        public void SpendingDrainsExactlyWhatWasAsked()
        {
            float before = _can.PaintMeters;
            float spent = _can.Spend(3f);

            Assert.AreEqual(3f, spent, 1e-4f);
            Assert.AreEqual(before - 3f, _can.PaintMeters, 1e-4f);
        }

        /// <summary>
        /// EL CASO QUE IMPORTA: quedarse sin pintura A MITAD de un tramo. Se gasta lo que
        /// quedaba y se devuelve eso, no lo pedido — así el trazo se corta donde se acabó la
        /// pintura, en vez de dibujarse entero gratis o desaparecer entero.
        /// </summary>
        [Test]
        public void RunningOutMidStrokeSpendsOnlyWhatWasLeft()
        {
            _can.Spend(_can.PaintMeters - 0.5f);
            Assert.AreEqual(0.5f, _can.PaintMeters, 1e-3f);

            float spent = _can.Spend(10f);

            Assert.AreEqual(0.5f, spent, 1e-3f, "solo se puede gastar lo que quedaba");
            Assert.IsTrue(_can.IsEmpty);
        }

        [Test]
        public void AnEmptyCanSpendsNothingMore()
        {
            _can.Spend(1000f);
            Assert.AreEqual(0f, _can.Spend(5f), 1e-4f);
            Assert.IsTrue(_can.IsEmpty, "y no se queda en negativo");
            Assert.AreEqual(0f, _can.PaintMeters, 1e-4f);
        }

        [Test]
        public void SpendingNothingOrLessIsANoOp()
        {
            float before = _can.PaintMeters;
            Assert.AreEqual(0f, _can.Spend(0f), 1e-4f);
            Assert.AreEqual(0f, _can.Spend(-5f), 1e-4f, "un gasto negativo no puede RELLENAR");
            Assert.AreEqual(before, _can.PaintMeters, 1e-4f);
        }

        [Test]
        public void RefillingNeverOverfills()
        {
            _can.Spend(10f);
            _can.Refill(1000f);

            Assert.AreEqual(_can.CapacityMeters, _can.PaintMeters, 1e-3f);
            Assert.AreEqual(1f, _can.PaintFraction, 1e-3f);
        }

        /// <summary>
        /// El lienzo autorado se acota a lo que el host acepta ANTES de salir por el cable: un
        /// valor fuera de rango se rechazaría entero y el jugador vería desaparecer su pintada
        /// sin ninguna explicación.
        /// </summary>
        [Test]
        public void TheCanvasSizeIsClampedToWhatTheHostAccepts()
        {
            Assert.GreaterOrEqual(_can.CanvasMeters, SprayCanvas.MinCanvasMeters);
            Assert.LessOrEqual(_can.CanvasMeters, SprayCanvas.MaxCanvasMeters);
        }

        /// <summary>
        /// El índice de color tiene que caer dentro de la paleta del cliente; si no, el
        /// renderizador lo envolvería con % y pintaría de otro color en vez de fallar.
        /// </summary>
        [Test]
        public void TheAuthoredColourIndexIsInsideThePalette()
        {
            Assert.Less(_can.ColorIndex, SprayRenderer.PaletteLen);
        }
    }
}
