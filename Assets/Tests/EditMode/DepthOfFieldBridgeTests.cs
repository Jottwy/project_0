using BackroomsSurvival.Gameplay;
using NUnit.Framework;
using UnityEngine.Rendering.Universal;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El puente de la profundidad de campo (13-09): Gaussian lejano con la opción encendida, y el
    /// modo autorado mientras un efecto del vendor (libro, inventario) anima el enfoque. Lo que
    /// fija: lo cercano nunca se difumina por la opción, y el efecto del libro no se pierde.
    /// </summary>
    [TestFixture]
    public class DepthOfFieldBridgeTests
    {
        private const float AuthoredFocus = 10f;
        private const float AuthoredAperture = 5.6f;

        [Test]
        public void OptionOnAndNoEffect_IsGaussian()
        {
            Assert.AreEqual(DepthOfFieldMode.Gaussian,
                BackroomsDepthOfFieldBridge.ModeFor(true, false, DepthOfFieldMode.Bokeh));
        }

        [Test]
        public void OptionOnButBookOpen_KeepsTheAuthoredBokeh()
        {
            Assert.AreEqual(DepthOfFieldMode.Bokeh,
                BackroomsDepthOfFieldBridge.ModeFor(true, true, DepthOfFieldMode.Bokeh));
        }

        [Test]
        public void OptionOff_LeavesTheAuthoredModeForTheEffects()
        {
            Assert.AreEqual(DepthOfFieldMode.Bokeh,
                BackroomsDepthOfFieldBridge.ModeFor(false, false, DepthOfFieldMode.Bokeh));
            Assert.AreEqual(DepthOfFieldMode.Bokeh,
                BackroomsDepthOfFieldBridge.ModeFor(false, true, DepthOfFieldMode.Bokeh));
        }

        [Test]
        public void AuthoredValues_AreNotAnEffect()
        {
            Assert.IsFalse(BackroomsDepthOfFieldBridge.IsAnimatedByEffect(
                AuthoredFocus, AuthoredAperture, AuthoredFocus, AuthoredAperture));
        }

        /// <summary>El libro anima el foco de 10 a 0,3 m y la apertura de 5,6 a 50: cualquier punto
        /// de ese recorrido, incluido el primer fotograma, es «efecto en marcha».</summary>
        [TestCase(9.9f, 5.6f)]
        [TestCase(0.3f, 50f)]
        [TestCase(10f, 6f)]
        public void AnyStepOfTheBookAnimation_IsAnEffect(float focus, float aperture)
        {
            Assert.IsTrue(BackroomsDepthOfFieldBridge.IsAnimatedByEffect(
                focus, aperture, AuthoredFocus, AuthoredAperture));
        }

        [Test]
        public void GaussianNeverTouchesWhatIsInHand()
        {
            Assert.Greater(BackroomsDepthOfFieldBridge.GaussianStartM, 2f,
                "el desenfoque tiene que empezar lejos de lo que se lleva en la mano");
            Assert.Greater(BackroomsDepthOfFieldBridge.GaussianEndM, BackroomsDepthOfFieldBridge.GaussianStartM);
        }
    }
}
