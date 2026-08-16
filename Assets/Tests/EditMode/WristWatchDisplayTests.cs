using System.Reflection;
using BackroomsSurvival.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Guardia contra el patrón inerte de las barras del reloj: un <c>Image.Type.Filled</c>
    /// sin Source Image pinta el rect entero SIEMPRE, sin error ni warning — barras llenas e
    /// inmóviles que parecen un fallo de datos, no de UI. Y el único sprite builtin
    /// (UI/Skin/UISprite.psd) vive en unity_builtin_extra, donde
    /// <c>Resources.GetBuiltinResource</c> no llega en runtime: devuelve null en silencio.
    /// La cura validada es relleno por geometría (<c>anchorMax.x</c>); estos tests fallan si
    /// alguien reintroduce <c>Filled</c> o vuelve a colgar el relleno de un sprite.
    ///
    /// La cara se construye entera por código en <c>BuildFace</c> (privado, lo dispara Awake
    /// en Play), así que aquí se invoca por reflexión — en EditMode, AddComponent no llama a
    /// Awake.
    /// </summary>
    [TestFixture]
    public class WristWatchDisplayTests
    {
        private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

        private GameObject _go;
        private WristWatchDisplay _display;
        private Image[] _fills;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TestWatch");
            _display = _go.AddComponent<WristWatchDisplay>();

            // El propio GO como hueso ancla: ResolveAnchor lo encuentra por nombre y no
            // emite el warning de "hueso no encontrado" en cada test.
            _display.anchorBoneName = _go.name;

            typeof(WristWatchDisplay).GetMethod("BuildFace", Priv).Invoke(_display, null);
            _fills = (Image[])typeof(WristWatchDisplay).GetField("_fills", Priv).GetValue(_display);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_go);
        }

        private void SetBar(int index, float value0To100) =>
            typeof(WristWatchDisplay).GetMethod("SetBar", Priv)
                .Invoke(_display, new object[] { index, value0To100 });

        [Test]
        public void FillsNacenVacios_SinFilledNiSprite()
        {
            Assert.AreEqual(5, _fills.Length);

            foreach (var fill in _fills)
            {
                Assert.IsNotNull(fill);
                Assert.AreEqual(Image.Type.Simple, fill.type, fill.name);
                Assert.IsNull(fill.sprite, fill.name);
                Assert.AreEqual(0f, fill.rectTransform.anchorMax.x, 1e-5f,
                    fill.name + " debe nacer vacío");
            }
        }

        [Test]
        public void NingunImageUsaFilledSinSprite()
        {
            foreach (var img in _go.GetComponentsInChildren<Image>(true))
                Assert.IsFalse(img.type == Image.Type.Filled && img.sprite == null,
                    img.name + ": Filled sin sprite es inerte (pinta el rect entero siempre)");
        }

        [Test]
        public void SetBarRecortaAnchorMax()
        {
            SetBar(0, 50f);
            Assert.AreEqual(0.5f, _fills[0].rectTransform.anchorMax.x, 1e-5f);

            SetBar(0, 0f);
            Assert.AreEqual(0f, _fills[0].rectTransform.anchorMax.x, 1e-5f);
        }

        [Test]
        public void SetBarSaturaFueraDeRango()
        {
            SetBar(1, 150f);
            Assert.AreEqual(1f, _fills[1].rectTransform.anchorMax.x, 1e-5f);

            SetBar(1, -20f);
            Assert.AreEqual(0f, _fills[1].rectTransform.anchorMax.x, 1e-5f);
        }
    }
}
