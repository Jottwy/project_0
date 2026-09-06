using System.Collections.Generic;
using BackroomsSurvival.Gameplay.Audio;
using NUnit.Framework;
using UnityEngine;

using Emitter = BackroomsSurvival.Gameplay.Audio.OfficeAmbienceDirector.Emitter;
using Kind = BackroomsSurvival.Gameplay.Audio.OfficeAmbienceDirector.Kind;
using PropSpec = BackroomsSurvival.Gameplay.Audio.OfficeAmbienceDirector.PropSpec;
using RoomSpec = BackroomsSurvival.Gameplay.Audio.OfficeAmbienceDirector.RoomSpec;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Tests headless de las DOS piezas puras del detalle sonoro de oficina: la
    /// clasificación de salas (qué suena dónde y cada cuánto) y la síntesis de los cuatro
    /// placeholders. Ninguna necesita escena, motor de audio ni Play.
    ///
    /// FUERA DE ALCANCE declarado: el reparto de las seis AudioSource, el desalojo de un
    /// continuo por un episódico y la retirada del lote al descargar un chunk son Play-only
    /// (dependen de Update y de un AudioListener vivo). Lo que sí queda fijado aquí es lo
    /// que rompería en silencio: que el sorteo sea función de la semilla y la COTA, que el
    /// orden del atrezo no cambie el resultado y que los clips sean mono y finitos.
    /// </summary>
    [TestFixture]
    public class OfficeAmbienceTests
    {
        private const int Seed = 42;

        private const byte Desk = 1, Chair = 2, Cabinet = 3, Shelf = 4, Box = 7,
            Monitor = 9, Phone = 11;

        private static RoomSpec Room(int sizeXCm, int sizeZCm, int heightCm,
            int xCm = 1000, int zCm = 2000, int floorYCm = 0) =>
            new RoomSpec
            {
                xCm = xCm, zCm = zCm, sizeXCm = sizeXCm, sizeZCm = sizeZCm,
                floorYCm = floorYCm, heightCm = heightCm,
            };

        private static PropSpec P(byte kind, int xCm, int zCm, int yCm = 0) =>
            new PropSpec { kind = kind, xCm = xCm, zCm = zCm, yCm = yCm };

        private static List<Emitter> Classify(RoomSpec room, List<PropSpec> props, int seed = Seed)
        {
            var into = new List<Emitter>();
            OfficeAmbienceDirector.Classify(seed, room, props, into);
            return into;
        }

        private static bool Has(List<Emitter> es, Kind k)
        {
            foreach (Emitter e in es) if (e.kind == k) return true;
            return false;
        }

        private static Emitter Get(List<Emitter> es, Kind k)
        {
            foreach (Emitter e in es) if (e.kind == k) return e;
            Assert.Fail($"no hay emisor de tipo {k}");
            return default;
        }

        // Un cubículo: cuatro puestos con su mesa, su silla y su teléfono.
        private static List<PropSpec> Cubicles()
        {
            var props = new List<PropSpec>();
            for (int i = 0; i < 4; i++)
            {
                int x = 1100 + i * 260;
                props.Add(P(Desk, x, 2100));
                props.Add(P(Chair, x, 2180));
                props.Add(P(Monitor, x, 2080));
                props.Add(P(Phone, x - 75, 2090));
            }
            return props;
        }

        // ── El falso techo ──────────────────────────────────────────────────────

        [Test]
        public void AirCon_SoloConFalsoTecho()
        {
            // 280 cm es inequívoco: solo la oficina recorta por debajo de 300.
            Assert.IsTrue(Has(Classify(Room(600, 600, 280), Cubicles()), Kind.AirCon));

            // 332 es el forjado, y 380 el techo alto: ninguno lleva rejilla.
            Assert.IsFalse(Has(Classify(Room(600, 600, 332), Cubicles()), Kind.AirCon));
            Assert.IsFalse(Has(Classify(Room(600, 600, 380), Cubicles()), Kind.AirCon));
        }

        [Test]
        public void AirCon_En300ExigeQueLaSalaEsteAmueblada()
        {
            // 300 es CEILING_MIN_CM: media planta del mundo mide eso sin ser oficina. Un
            // espacio vacío a 300 NO puede llevar rejilla o la lleva el mundo entero.
            Assert.IsFalse(Has(Classify(Room(600, 600, 300), new List<PropSpec>()), Kind.AirCon));
            Assert.IsTrue(Has(Classify(Room(600, 600, 300), Cubicles()), Kind.AirCon));
        }

        [Test]
        public void AirCon_NoEnUnCuchitril()
        {
            // 2 × 2 m: un armario de servicios no tiene rejilla propia.
            Assert.IsFalse(Has(Classify(Room(200, 200, 280), Cubicles()), Kind.AirCon));
        }

        [Test]
        public void AirCon_CuelgaDelPlenumYEnElCentro()
        {
            Emitter e = Get(Classify(Room(600, 400, 280)), Kind.AirCon);
            Assert.AreEqual(13.0f, e.position.x, 1e-3f);  // 1000 + 600/2 cm
            Assert.AreEqual(22.0f, e.position.z, 1e-3f);  // 2000 + 400/2 cm
            Assert.AreEqual(2.60f, e.position.y, 1e-3f);  // 2,80 − 0,20 del plenum
            Assert.AreEqual(0f, e.period, "el aire es continuo, no un suceso");
        }

        private static List<Emitter> Classify(RoomSpec room) => Classify(room, Cubicles());

        // ── El papel de la sala ─────────────────────────────────────────────────

        [Test]
        public void Cubiculos_CrujenYNoSuenanAOtraCosa()
        {
            var es = Classify(Room(800, 800, 280), Cubicles());
            Assert.IsTrue(Has(es, Kind.ChairCreak));
            // Cuatro mesas no son un despacho aunque tengan teléfono, ni un archivo.
            Assert.IsFalse(Has(es, Kind.Phone));
            Assert.IsFalse(Has(es, Kind.Printer));
        }

        [Test]
        public void Archivo_ArmariosSinPuestos()
        {
            var props = new List<PropSpec>
            {
                P(Cabinet, 1100, 2100), P(Cabinet, 1300, 2100),
                P(Shelf, 1500, 2100), P(Box, 1700, 2100),
            };
            // El sorteo es 1 de cada 2: se busca la cota que toca en vez de asumir una.
            bool found = false;
            for (int floor = 0; floor < 40 && !found; floor++)
            {
                var room = Room(700, 500, 332, floorYCm: floor * 332);
                var shifted = new List<PropSpec>();
                foreach (PropSpec p in props) shifted.Add(P(p.kind, p.xCm, p.zCm, floor * 332));
                found = Has(Classify(room, shifted), Kind.Printer);
            }
            Assert.IsTrue(found, "ningún archivo de 40 plantas sacó impresora: el sorteo está muerto");
        }

        [Test]
        public void Despacho_UnPuestoConTelefono()
        {
            var props = new List<PropSpec>
            {
                P(Desk, 1100, 2100), P(Chair, 1100, 2180),
                P(Monitor, 1100, 2080), P(Phone, 1025, 2090),
            };
            bool found = false;
            for (int floor = 0; floor < 80 && !found; floor++)
            {
                var room = Room(400, 400, 280, floorYCm: floor * 332);
                var shifted = new List<PropSpec>();
                foreach (PropSpec p in props) shifted.Add(P(p.kind, p.xCm, p.zCm, floor * 332));
                found = Has(Classify(room, shifted), Kind.Phone);
            }
            Assert.IsTrue(found, "ningún despacho de 80 plantas sacó teléfono: el sorteo está muerto");
        }

        [Test]
        public void Despacho_UnaNaveNoEsUnDespacho()
        {
            // Mismo mobiliario, 20 × 20 m: el teléfono de un despacho se oye desde el
            // pasillo, el de una nave sonaría en mitad de la nada.
            var props = new List<PropSpec>
            {
                P(Desk, 1100, 2100), P(Chair, 1100, 2180), P(Phone, 1025, 2090),
            };
            Assert.IsFalse(Has(Classify(Room(2000, 2000, 280), props), Kind.Phone));
        }

        [Test]
        public void SalaVacia_NoSuenaANada()
        {
            Assert.AreEqual(0, Classify(Room(600, 600, 332), new List<PropSpec>()).Count);
        }

        [Test]
        public void ElAtrezoDeOtraPlantaNoCuenta()
        {
            // El mismo mobiliario, tres metros y medio más arriba: es la sala de encima.
            var props = new List<PropSpec>();
            foreach (PropSpec p in Cubicles()) props.Add(P(p.kind, p.xCm, p.zCm, 332));
            var es = Classify(Room(600, 600, 280), props);
            Assert.IsFalse(Has(es, Kind.ChairCreak), "las sillas de arriba hicieron crujir esta sala");
            // El aire SÍ sigue: 280 cm es falso techo se amueble o no la sala, y esa es
            // precisamente la señal que no depende del atrezo.
            Assert.IsTrue(Has(es, Kind.AirCon));
        }

        // ── Determinismo ────────────────────────────────────────────────────────

        [Test]
        public void ElOrdenDelAtrezoNoCambiaNada()
        {
            var room = Room(800, 800, 280);
            var forward = Cubicles();
            var backward = new List<PropSpec>(forward);
            backward.Reverse();

            List<Emitter> a = Classify(room, forward);
            List<Emitter> b = Classify(room, backward);

            Assert.AreEqual(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].kind, b[i].kind);
                Assert.AreEqual(a[i].position, b[i].position, "el ancla se eligió por orden, no por hash");
                Assert.AreEqual(a[i].period, b[i].period, 1e-6f);
            }
        }

        [Test]
        public void LaCotaEntraEnElSorteo()
        {
            // Dos salas superpuestas comparten (x,z). Sin la cota en la mezcla, sortearían
            // idéntico y el edificio entero sonaría igual en todas sus plantas.
            var props = Cubicles();
            var upper = new List<PropSpec>();
            foreach (PropSpec p in props) upper.Add(P(p.kind, p.xCm, p.zCm, 332));

            Emitter low = Get(Classify(Room(800, 800, 280), props), Kind.ChairCreak);
            Emitter high = Get(Classify(Room(800, 800, 280, floorYCm: 332), upper), Kind.ChairCreak);
            Assert.Greater(Mathf.Abs(low.period - high.period), 1e-3f);
        }

        [Test]
        public void LaSemillaCambiaElMundo()
        {
            var room = Room(800, 800, 280);
            var props = Cubicles();
            Emitter a = Get(Classify(room, props, 42), Kind.ChairCreak);
            Emitter b = Get(Classify(room, props, 43), Kind.ChairCreak);
            Assert.Greater(Mathf.Abs(a.period - b.period), 1e-3f);
        }

        [Test]
        public void LosPeriodosEstanEnSuRango()
        {
            // Barrido: ningún suceso puede salir con periodo 0 (sonaría cada pasada) ni por
            // encima de su tope.
            for (int i = 0; i < 200; i++)
            {
                var props = new List<PropSpec>();
                foreach (PropSpec p in Cubicles()) props.Add(P(p.kind, p.xCm + i * 37, p.zCm + i * 53));
                var room = Room(800, 800, 280, xCm: 1000 + i * 37, zCm: 2000 + i * 53);
                foreach (Emitter e in Classify(room, props))
                {
                    if (e.kind == Kind.AirCon) { Assert.AreEqual(0f, e.period); continue; }
                    Assert.Greater(e.period, 1f, $"{e.kind} con periodo {e.period}");
                    Assert.Less(e.period, 500f, $"{e.kind} con periodo {e.period}");
                    Assert.GreaterOrEqual(e.phase01, 0f);
                    Assert.Less(e.phase01, 1f);
                }
            }
        }

        [Test]
        public void ElTelefonoEsRaro()
        {
            // El sorteo existe para que no haya un teléfono por despacho. Se mide sobre 300
            // despachos: si sonaran todos, esto sería 300.
            int rings = 0;
            for (int i = 0; i < 300; i++)
            {
                var props = new List<PropSpec>
                {
                    P(Desk, 1100 + i * 41, 2100), P(Chair, 1100 + i * 41, 2180),
                    P(Phone, 1025 + i * 41, 2090),
                };
                var room = Room(400, 400, 280, xCm: 1000 + i * 41);
                if (Has(Classify(room, props), Kind.Phone)) rings++;
            }
            Assert.Greater(rings, 20, $"{rings}/300: el sorteo dejó el mundo mudo");
            Assert.Less(rings, 120, $"{rings}/300: hay un teléfono en casi cada despacho");
        }

        // ── Los placeholders sintéticos ─────────────────────────────────────────

        private const int Rate = 44100;

        [Test]
        public void LosCuatroClipsSonFinitosYNormalizados()
        {
            foreach (Kind k in new[] { Kind.AirCon, Kind.Printer, Kind.Phone, Kind.ChairCreak })
            {
                float[] data = OfficeAmbienceDirector.RenderSamples(k, Rate);
                Assert.Greater(data.Length, Rate / 4, $"{k}: clip demasiado corto");
                Assert.Less(data.Length, Rate * 4, $"{k}: clip demasiado largo para un hueco del pool");

                float peak = 0f;
                for (int i = 0; i < data.Length; i++)
                {
                    Assert.IsFalse(float.IsNaN(data[i]) || float.IsInfinity(data[i]),
                        $"{k}: muestra no finita en {i}");
                    float a = Mathf.Abs(data[i]);
                    if (a > peak) peak = a;
                }
                Assert.LessOrEqual(peak, 1.0f, $"{k}: recorta");
                Assert.Greater(peak, 0.5f, $"{k}: se quedó sin normalizar o está mudo");
            }
        }

        [Test]
        public void LosClipsSonDeterministas()
        {
            foreach (Kind k in new[] { Kind.AirCon, Kind.Printer, Kind.Phone, Kind.ChairCreak })
            {
                float[] a = OfficeAmbienceDirector.RenderSamples(k, Rate);
                float[] b = OfficeAmbienceDirector.RenderSamples(k, Rate);
                Assert.AreEqual(a.Length, b.Length);
                for (int i = 0; i < a.Length; i += 97) Assert.AreEqual(a[i], b[i], 1e-9f);
            }
        }

        [Test]
        public void ElAireCierraElBucleSinClick()
        {
            // El continuo se reproduce en loop: un salto entre la última muestra y la
            // primera es un chasquido cada dos segundos, y eso se oye desde el otro lado del
            // mapa aunque el volumen sea 0,045.
            float[] data = OfficeAmbienceDirector.RenderAirConSamples(Rate, 2);
            float jump = Mathf.Abs(data[data.Length - 1] - data[0]);
            Assert.Less(jump, 0.08f, $"salto de {jump:F3} en la costura del bucle");
        }

        [Test]
        public void ElTimbreSonDosRafagas()
        {
            // La forma del timbre ES lo que se reconoce desde un pasillo. Si se convierte en
            // un tono continuo deja de leerse como teléfono.
            float[] data = OfficeAmbienceDirector.RenderPhoneRingSamples(Rate);
            float Energy(float fromS, float toS)
            {
                int a = (int)(fromS * Rate), b = Mathf.Min((int)(toS * Rate), data.Length);
                float sum = 0f;
                for (int i = a; i < b; i++) sum += Mathf.Abs(data[i]);
                return sum / Mathf.Max(1, b - a);
            }
            Assert.Greater(Energy(0.1f, 0.9f), 0.05f, "primera ráfaga muda");
            Assert.Less(Energy(1.05f, 1.45f), 0.01f, "el silencio entre ráfagas no está");
            Assert.Greater(Energy(1.6f, 2.4f), 0.05f, "segunda ráfaga muda");
        }
    }
}
