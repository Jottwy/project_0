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

        // ─────────────────────────────────────────────────────────────────────────────────────
        // ADR-130 rebanada 2b — el decaimiento del CLIENTE.
        //
        // Ninguno de estos llama a `Resolve`, y es a propósito: `Resolve` pasa por `TintFor`, que
        // es `Mathf.CorrelatedColorTemperatureToRGB` —una ECall nativa— y por eso los siete tests
        // de arriba no corren en el arnés .NET sin editor. Los de abajo son aritmética y hash, así
        // que corren en los dos sitios; el que necesita el editor va marcado.
        // ─────────────────────────────────────────────────────────────────────────────────────

        [Test]
        public void ElDecaimiento_EsCeroEnLaCalleYUnoEnElFondo()
        {
            // El espejo de `fill::decay_of_floor`. Si estas cuatro cuentas dejan de cuadrar, el
            // servidor decae el relleno de una planta y el cliente le apaga las luces a otra.
            float storey = Wg3StoreyLayers.StoreyM;
            int fondo = Wg3StoreyLayers.BasementLayers;

            Assert.AreEqual(0f, Wg3StoreyLayers.DecayOfFloor(0f), 1e-6f, "la calle no decae");
            Assert.AreEqual(0f, Wg3StoreyLayers.DecayOfFloor(storey * 2f), 1e-6f,
                "una planta ALTA tampoco: el decaimiento sólo baja");
            Assert.AreEqual(1f, Wg3StoreyLayers.DecayOfFloor(-storey * fondo), 1e-5f,
                "el sótano más hondo servido es el extremo, y vale 1");

            // El cuadrado de D4, y no una rampa: a media profundidad hay que estar en 0,25 y no en
            // 0,50. Es la diferencia entre «los sótanos se oscurecen» y «B1 ya está a oscuras».
            Assert.AreEqual(0.25f, Wg3StoreyLayers.DecayOfFloor(-storey * fondo * 0.5f), 1e-5f);

            // Y no se pasa de 1 por debajo del fondo servido: con 30 sótanos en el plan y 3 aquí,
            // una cota más honda de la cuenta no puede dar un decaimiento mayor que el extremo.
            Assert.AreEqual(1f, Wg3StoreyLayers.DecayOfFloor(-storey * (fondo + 5)), 1e-5f);
        }

        [Test]
        public void ElColorDecaido_PierdeTonoYBrilloSinTocarLaCalle()
        {
            var calido = new Color(1f, 0.96f, 0.78f);

            // Lo que hace que esto no sea un cambio de color encubierto, igual que el test del
            // tinte: en la calle sale el color validado sin tocar.
            Assert.AreEqual(calido, Wg3LightCadence.Decayed(calido, 0f));

            Color fondo = Wg3LightCadence.Decayed(calido, 1f);

            // Gris: los tres canales iguales. Con el cálido de partida, el azul es el que más lejos
            // está, así que si algo sobrevive del tono se ve aquí.
            Assert.AreEqual(fondo.r, fondo.b, 1e-5f, "en el fondo el color es gris, sin tono");
            Assert.AreEqual(fondo.r, fondo.g, 1e-5f);

            // Y más apagado, por el factor que se mide.
            float luz = calido.r * 0.2126f + calido.g * 0.7152f + calido.b * 0.0722f;
            Assert.AreEqual(luz * (1f - Wg3LightCadence.DecayDimShare), fondo.r, 1e-5f);
            Assert.Less(fondo.r, calido.r, "el fondo tiene que ser MÁS oscuro que la calle");

            // Monótono: no hay un sótano intermedio más brillante que el de encima.
            float previo = calido.r;
            for (float d = 0.1f; d <= 1.0001f; d += 0.1f)
            {
                float actual = Wg3LightCadence.Decayed(calido, d).r;
                Assert.LessOrEqual(actual, previo + 1e-5f, $"decaimiento {d:0.0} sube el brillo");
                previo = actual;
            }
        }

        [Test]
        public void LaLuminariaQueFalta_NoExisteEnLaCalleYEsUnaDeCadaDosEnElFondo()
        {
            // La calle no pierde ni una: si esto se rompe, el mundo entero cambia de aspecto y no
            // sólo los sótanos.
            for (int i = 0; i < 500; i++)
                Assert.IsFalse(Wg3LightCadence.PanelMissing(i * 240, 300, 1700, 0f),
                    "en la calle no falta ninguna luminaria");

            int faltan = 0, total = 0;
            for (int ix = 0; ix < 40; ix++)
                for (int iz = 0; iz < 40; iz++, total++)
                    if (Wg3LightCadence.PanelMissing(ix * 240, -996, iz * 240, 1f)) faltan++;

            float share = (float)faltan / total;
            Assert.That(share, Is.EqualTo(Wg3LightCadence.DecayPanelMissingShare).Within(0.06f),
                $"en el fondo debería faltar el {Wg3LightCadence.DecayPanelMissingShare:P0}, " +
                $"y faltan {faltan} de {total}");
        }

        [Test]
        public void LaLuminariaQueFalta_NoArrancaLaMismaColumnaEnCadaPlanta()
        {
            // La trampa que ya mordió a los MONITORES (ADR-105 enm. 19): la retícula del falso techo
            // de una planta se reparte igual que la de la de abajo, así que sin la cota en el hash
            // se arrancaría el mismo hueco en las tres o cuatro plantas del edificio — y en vez de
            // un techo desconchado saldría una chimenea.
            int iguales = 0, total = 0;
            for (int ix = 0; ix < 30; ix++)
                for (int iz = 0; iz < 30; iz++, total++)
                {
                    bool b1 = Wg3LightCadence.PanelMissing(ix * 240, -32, iz * 240, 1f);
                    bool b3 = Wg3LightCadence.PanelMissing(ix * 240, -996, iz * 240, 1f);
                    if (b1 == b3) iguales++;
                }

            // Independientes, la coincidencia esperada es p² + (1−p)² ≈ 0,505 con p = 0,45. Muy por
            // debajo del 1,0 que daría un hash sin la cota.
            float coincidencia = (float)iguales / total;
            Assert.Less(coincidencia, 0.70f,
                $"B1 y B3 coinciden en el {coincidencia:P0} de la retícula: la cota no entra en el hash");
        }

        [Test]
        public void LaProfundidad_ApagaCasiTodoSinCorrerLasTiradas()
        {
            // ESTE necesita el editor: `Resolve` pasa por `TintFor`, que es una ECall nativa.
            //
            // Es el test que protege la decisión de fondo de la r2b: el decaimiento mueve los
            // UMBRALES sobre la misma tirada, no añade una tirada nueva. Por eso una lámpara que
            // está apagada en la calle sigue apagada abajo — sólo se le suman las que estaban cerca
            // del borde. Si alguien mete una tirada por en medio, la implicación se rompe y esto se
            // pone rojo antes de que nadie tenga que mirar una captura.
            int vivasCalle = 0, vivasFondo = 0;
            for (int i = 0; i < 400; i++)
            {
                var calle = Wg3LightCadence.Resolve(42, 12.5f + i * 9f, 43.25f, i, 9f, 9f, null, 0f);
                var fondo = Wg3LightCadence.Resolve(42, 12.5f + i * 9f, 43.25f, i, 9f, 9f, null, 1f);

                if (calle.lit) vivasCalle++;
                if (fondo.lit) vivasFondo++;

                Assert.IsFalse(!calle.lit && fondo.lit,
                    $"fixture {i}: apagado en la calle y encendido en el fondo — el umbral se movió " +
                    "al revés, o alguien metió una tirada nueva");

                // El jitter y el tinte NO los toca la profundidad: son tiradas posteriores del mismo
                // flujo y tienen que salir idénticas.
                Assert.AreEqual(calle.offset, fondo.offset, $"fixture {i}: el jitter se movió");
                Assert.AreEqual(calle.tint, fondo.tint, $"fixture {i}: el tinte se movió");
            }

            Assert.That(vivasCalle / 400f, Is.EqualTo(0.88f).Within(0.06f), "la calle no cambia");
            // 1 − (0,12 + 0,88·0,80) = 0,176.
            Assert.That(vivasFondo / 400f, Is.EqualTo(0.176f).Within(0.06f),
                $"en el fondo deberían quedar ~18 de cada 100, y quedan {vivasFondo} de 400");
        }
    }
}
