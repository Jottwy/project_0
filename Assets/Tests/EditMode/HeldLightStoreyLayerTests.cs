using BackroomsSurvival.Gameplay;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-130 — la luz que alguien lleva en la mano tiene que estar en la capa de render de la
    /// planta en la que está, o no ilumina NADA de esa planta. El mundo WG3 reparte las capas con
    /// <see cref="Wg3StoreyLayers"/> y desplaza tres sótanos, así que la capa por defecto de Unity
    /// (bit 0) es B3 y ninguna otra: una luz que se quede con la del prefab alumbra en el aire en
    /// toda la calle y en B1/B2, sin un solo error. Lo cazó la linterna de manivela, y lo tenían
    /// igual la antorcha del vendor, el fogonazo del Marlin y la luz de mano de los peers.
    ///
    /// Se prueba el helper compartido, <see cref="TorchShadowCaster.ApplyStoreyLayer"/>, con luces
    /// sueltas: el hook necesita el rig de STP y eso es PlayMode. El contrato aquí es que la capa
    /// escrita coincida EXACTAMENTE con la que el mundo da a sus paredes en esa cota, no un número.
    /// </summary>
    [TestFixture]
    public class HeldLightStoreyLayerTests
    {
        private GameObject _go;
        private Light _light;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("HeldLightUnderTest");
            _light = _go.AddComponent<Light>();
            _light.type = LightType.Point;
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                Object.DestroyImmediate(_go);
        }

        /// <summary>El caso real: capa del prefab (bit 0) con el jugador en la calle.</summary>
        [Test]
        public void StreetLevelLightLeavesTheDefaultLayer()
        {
            _light.renderingLayerMask = 1;
            _go.transform.position = new Vector3(3f, 1.4f, 14f); // a la altura de la mano

            bool wrote = TorchShadowCaster.ApplyStoreyLayer(_light);

            Assert.IsTrue(wrote, "la capa por defecto es B3, no la calle: tenía que reescribirse");
            Assert.AreEqual((int)Wg3StoreyLayers.ForLight(1.4f), _light.renderingLayerMask,
                "la luz tiene que llevar la misma capa que el mundo da a las paredes de su cota");
            Assert.AreNotEqual(1, _light.renderingLayerMask,
                "con tres sótanos el bit 0 es B3; una luz de la calle no puede quedarse en él");
        }

        /// <summary>La capa sigue a la cota: la misma luz en cada planta servida da una capa distinta,
        /// y siempre la de <c>ForLight</c>.</summary>
        [Test]
        public void LayerFollowsTheStorey()
        {
            int previous = -1;
            for (int storey = -Wg3StoreyLayers.BasementLayers; storey <= 2; storey++)
            {
                float y = storey * Wg3StoreyLayers.StoreyM + 1.4f;
                _go.transform.position = new Vector3(0f, y, 0f);

                TorchShadowCaster.ApplyStoreyLayer(_light);

                Assert.AreEqual((int)Wg3StoreyLayers.ForLight(y), _light.renderingLayerMask,
                    $"planta {storey}: la capa de la luz no es la de su cota");
                Assert.AreNotEqual(previous, _light.renderingLayerMask,
                    $"planta {storey}: dos plantas consecutivas no pueden compartir capa");
                previous = _light.renderingLayerMask;
            }
        }

        /// <summary>Se escribe sólo al cruzar de planta: es un entero comparado cada frame y no
        /// puede costar una escritura por frame en cada luz del wieldable.</summary>
        [Test]
        public void SecondCallOnTheSameStoreyWritesNothing()
        {
            _go.transform.position = new Vector3(0f, -4f, 0f); // B2
            Assert.IsTrue(TorchShadowCaster.ApplyStoreyLayer(_light));

            _go.transform.position = new Vector3(2f, -5f, 1f); // sigue en B2
            Assert.IsFalse(TorchShadowCaster.ApplyStoreyLayer(_light),
                "misma planta: no hay nada que escribir");
        }

        /// <summary>Una luz destruida lee null por el == de Unity; escribirle lanzaría
        /// MissingReferenceException en mitad del Update del hook.</summary>
        [Test]
        public void DestroyedLightIsANoOp()
        {
            Object.DestroyImmediate(_go);
            _go = null;

            Assert.DoesNotThrow(() => TorchShadowCaster.ApplyStoreyLayer(_light));
            Assert.IsFalse(TorchShadowCaster.ApplyStoreyLayer(_light));
        }
    }
}
