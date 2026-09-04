using NUnit.Framework;
using UnityEngine;
using BackroomsSurvival.WorldGen3;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// El ritmo de los fluorescentes: que sea el MISMO siempre, que dependa de la semilla y que los
    /// porcentajes salgan donde se pidieron.
    ///
    /// Lo que estos tests protegen no se ve en una captura: dos clientes con lámparas fundidas
    /// distintas se parecen mucho a dos clientes iguales hasta que uno mira al techo del otro.
    /// </summary>
    public sealed class Wg3LightCadenceTests
    {
        private static Wg3Fixture At(int seed, int i) =>
            Wg3LightCadence.Resolve(seed, 12.5f + i * 9f, 43.25f, i, 9f, 9f, null);

        [Test]
        public void MismaSemillaYMismoSitio_DanElMismoFixture()
        {
            for (int i = 0; i < 64; i++)
            {
                Wg3Fixture a = At(42, i);
                Wg3Fixture b = At(42, i);
                Assert.AreEqual(a.lit, b.lit, $"fixture {i}: encendido");
                Assert.AreEqual(a.flickers, b.flickers, $"fixture {i}: parpadeo");
                Assert.AreEqual(a.offset, b.offset, $"fixture {i}: jitter");
                Assert.AreEqual(a.tint, b.tint, $"fixture {i}: tinte");
            }
        }

        [Test]
        public void OtraSemilla_CambiaElReparto()
        {
            int diferentes = 0;
            for (int i = 0; i < 256; i++)
                if (At(42, i).lit != At(1337, i).lit) diferentes++;

            // Con ~12 % apagadas en cada mundo, dos semillas independientes discrepan en torno al
            // 21 % de las lámparas. Se pide MUCHO menos para que el test no dependa de la
            // distribución exacta: lo que estaría roto de verdad es cero.
            Assert.Greater(diferentes, 8, "dos semillas dieron prácticamente el mismo techo");
        }

        [Test]
        public void ElIndiceNoOrdenaNada()
        {
            // El síntoma que motivó todo esto: el estado salía del índice, así que el fixture 0 de
            // todos los tramos era igual. Aquí se comprueba lo contrario — el mismo índice en
            // sitios distintos NO da el mismo estado.
            int apagadas = 0, total = 0;
            for (int x = 0; x < 32; x++)
                for (int z = 0; z < 32; z++)
                {
                    total++;
                    if (!Wg3LightCadence.Resolve(42, x * 9f + 4.5f, z * 9f + 4.5f, 0, 9f, 9f, null).lit)
                        apagadas++;
                }

            Assert.Greater(apagadas, 0, "con el fixture 0 en 1024 sitios, ninguna apagada");
            Assert.Less(apagadas, total, "con el fixture 0 en 1024 sitios, todas apagadas");
        }

        [Test]
        public void LosPorcentajesCaenDondeSePiden()
        {
            const int N = 4000;
            int apagadas = 0, parpadeando = 0;
            for (int i = 0; i < N; i++)
            {
                Wg3Fixture f = Wg3LightCadence.Resolve(42, i * 3.25f, i * 1.75f, i % 4, 9f, 9f, null);
                if (!f.lit) apagadas++;
                if (f.flickers) parpadeando++;
            }

            // 12 % y 8 % con margen de tres puntos: el test vigila el orden de las tiradas y el
            // reparto del intervalo, no la calidad estadística del mixer.
            Assert.That(apagadas / (float)N, Is.EqualTo(0.12f).Within(0.03f));
            Assert.That(parpadeando / (float)N, Is.EqualTo(0.08f).Within(0.03f));
        }

        [Test]
        public void ElJitterNoSaleDeSuCelda()
        {
            var s = new Wg3LightCadenceSettings();
            float tope = Mathf.Min(9f * s.jitterFraction, s.jitterMaxMeters);
            for (int i = 0; i < 512; i++)
            {
                Wg3Fixture f = At(42, i);
                Assert.LessOrEqual(Mathf.Abs(f.offset.x), tope + 1e-4f);
                Assert.LessOrEqual(Mathf.Abs(f.offset.y), tope + 1e-4f);
            }
        }

        [Test]
        public void SinDesviacion_ElColorValidadoNoSeMueve()
        {
            // La garantía que hace que esto no sea un cambio de color encubierto: a ±0 K el tinte es
            // exactamente 1 y el color de la Light sale bit a bit igual que antes.
            Color tint = Wg3LightCadence.TintFor(3500f, 3500f);
            Assert.AreEqual(1f, tint.r, 1e-5f);
            Assert.AreEqual(1f, tint.g, 1e-5f);
            Assert.AreEqual(1f, tint.b, 1e-5f);
        }

        [Test]
        public void LaTemperaturaSeMueveDentroDeLoPedido()
        {
            // ±200 K sobre 3500 K es un empujón pequeño; lo que se comprueba es que EXISTE y que no
            // se desmadra. El extremo frío tira a azul y el cálido a rojo, así que el cociente
            // azul/rojo es el que separa los dos.
            Color frio = Wg3LightCadence.TintFor(3500f, 3700f);
            Color calido = Wg3LightCadence.TintFor(3500f, 3300f);
            Assert.Greater(frio.b / frio.r, calido.b / calido.r);
            Assert.That(frio.b, Is.EqualTo(1f).Within(0.25f), "±200 K no debería ser un tinte fuerte");
        }
    }
}
