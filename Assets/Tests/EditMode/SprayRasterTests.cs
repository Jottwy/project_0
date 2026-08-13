using BackroomsSurvival.Gameplay;
using BackroomsSurvival.Net;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-068 S2 — el rasterizado de trazos. Se prueba la función pura (trazos → téxeles), no
    /// el <c>GameObject</c>: lo que puede salir mal aquí es el dibujo, y eso se puede leer píxel
    /// a píxel sin escena, sin materiales y sin sesión.
    /// </summary>
    [TestFixture]
    public class SprayRasterTests
    {
        private static SprayMsg Spray(byte color, byte width, params byte[] points) =>
            new SprayMsg
            {
                id = 1,
                sizeX = 1f,
                sizeY = 1f,
                strokes = new[]
                {
                    new SprayStrokeMsg { color = color, width = width, points = points }
                }
            };

        /// <summary>
        /// La tabla de colores del cliente tiene que cubrir exactamente el rango que el backend
        /// admite. Si el backend subiera <c>PALETTE_LEN</c> y esta tabla no, los índices nuevos
        /// se envolverían con <c>%</c> y saldrían pintados de otro color en vez de fallar.
        /// </summary>
        [Test]
        public void ThePaletteCoversEveryIndexTheBackendAccepts()
        {
            Assert.AreEqual(SprayRenderer.PaletteLen, SprayRenderer.PaletteCount);
        }

        [Test]
        public void AnEmptyStrokePaintsNothingAtAll()
        {
            Assert.IsNull(SprayRenderer.Rasterize(Spray(0, 4)), "sin puntos no hay textura");
            Assert.IsNull(SprayRenderer.Rasterize(new SprayMsg { id = 1 }), "sin trazos tampoco");
        }

        /// <summary>
        /// El fondo es TRANSPARENTE y solo la pintura tiene alfa. Si el fondo saliera opaco, cada
        /// pintada sería un rectángulo blanco tapando la pared entera en vez de un dibujo.
        /// </summary>
        [Test]
        public void TheCanvasIsTransparentExceptWhereItWasPainted()
        {
            var tex = SprayRenderer.Rasterize(Spray(2, 4, 128, 128));
            Assert.IsNotNull(tex);

            var px = tex.GetPixels32();
            Assert.AreEqual(0, px[0].a, "la esquina, lejos del trazo, queda transparente");

            var center = px[128 * SprayRenderer.CanvasPixels + 128];
            Assert.AreEqual(255, center.a, "donde se pintó, opaco");
            var expected = SprayRenderer.ColorOf(2);
            Assert.AreEqual(expected.r, center.r);
            Assert.AreEqual(expected.g, center.g);
            Assert.AreEqual(expected.b, center.b);

            Object.DestroyImmediate(tex);
        }

        /// <summary>
        /// Dos puntos consecutivos se UNEN con una línea. Es la diferencia entre un trazo y una
        /// fila de lunares: el bote se mueve continuo y el muestreo va a saltos, así que sin unir
        /// una mano rápida dibujaría puntitos sueltos.
        /// </summary>
        [Test]
        public void ConsecutivePointsAreJoinedInsteadOfLeftAsDots()
        {
            var tex = SprayRenderer.Rasterize(Spray(1, 2, 40, 128, 200, 128));
            var px = tex.GetPixels32();

            // Punto medio del segmento: nadie lo mandó, tiene que estar pintado por la línea.
            var mid = px[128 * SprayRenderer.CanvasPixels + 120];
            Assert.AreEqual(255, mid.a, "el hueco entre dos puntos debe quedar unido");

            // Y fuera del segmento, nada.
            var above = px[200 * SprayRenderer.CanvasPixels + 120];
            Assert.AreEqual(0, above.a, "la línea no puede sangrar por todo el lienzo");

            Object.DestroyImmediate(tex);
        }

        /// <summary>
        /// Un trazo que roza el borde no puede desbordar el array ni reaparecer por el lado
        /// contrario. El host acota los puntos a 0..255, así que el caso límite es exactamente
        /// la esquina.
        /// </summary>
        [Test]
        public void PaintingAtTheVeryEdgeStaysInsideTheCanvas()
        {
            Texture2D tex = null;
            Assert.DoesNotThrow(() => tex = SprayRenderer.Rasterize(Spray(4, 16, 0, 0, 255, 255)));
            Assert.IsNotNull(tex);

            var px = tex.GetPixels32();
            Assert.AreEqual(255, px[0].a, "la esquina 0,0 se pinta");
            Assert.AreEqual(255, px[px.Length - 1].a, "y la 255,255 también");

            Object.DestroyImmediate(tex);
        }

        /// <summary>
        /// Grosor 0 debe dar un trazo FINO, no una pintada invisible: el radio se clampa a 1
        /// téxel. Un jugador con un bote mal configurado tiene que ver algo.
        /// </summary>
        [Test]
        public void AZeroWidthStrokeStillPaintsSomething()
        {
            var tex = SprayRenderer.Rasterize(Spray(5, 0, 100, 100));
            var px = tex.GetPixels32();
            Assert.AreEqual(255, px[100 * SprayRenderer.CanvasPixels + 100].a);
            Object.DestroyImmediate(tex);
        }

        /// <summary>
        /// Varios trazos de colores distintos conviven en UNA pintada: el color va en los
        /// téxeles, no en el tinte del material, que es lo que lo permite.
        /// </summary>
        [Test]
        public void OneSprayCanCarryStrokesOfDifferentColours()
        {
            var spray = new SprayMsg
            {
                id = 1,
                strokes = new[]
                {
                    new SprayStrokeMsg { color = 2, width = 4, points = new byte[] { 60, 60 } },
                    new SprayStrokeMsg { color = 6, width = 4, points = new byte[] { 190, 190 } },
                }
            };

            var px = SprayRenderer.Rasterize(spray).GetPixels32();
            var first = px[60 * SprayRenderer.CanvasPixels + 60];
            var second = px[190 * SprayRenderer.CanvasPixels + 190];

            Assert.AreEqual(SprayRenderer.ColorOf(2).r, first.r);
            Assert.AreEqual(SprayRenderer.ColorOf(6).b, second.b);
            Assert.AreNotEqual(first.r, second.r, "los dos trazos no pueden salir del mismo color");
        }

        /// <summary>
        /// Un blob de longitud impar no puede ocurrir — el host lo rechaza — pero si llegara, el
        /// último byte suelto se ignora en vez de leerse fuera del array.
        /// </summary>
        [Test]
        public void AnOddPointBlobDoesNotReadPastTheEnd()
        {
            Assert.DoesNotThrow(() => SprayRenderer.Rasterize(Spray(0, 2, 10, 10, 20)));
        }
    }
}
