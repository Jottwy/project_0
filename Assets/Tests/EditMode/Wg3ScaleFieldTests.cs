using BackroomsSurvival.WorldGen3;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-119 D1 — el espejo C# del campo de escala.
    ///
    /// <para>
    /// <b>El campo de escala llevaba desde ADR-095 escrito dos veces —`scale.rs` y
    /// <see cref="Wg3ScaleField"/>— sin un solo valor atado entre los dos idiomas.</b> Lo que había
    /// eran pruebas de que el campo es puro y de que no se espeja en el origen, que son propiedades
    /// de CADA lado por separado: los dos podían derivar juntos y seguir en verde. Y este campo
    /// decide el TAMAÑO de los espacios, así que una deriva no sale como un error sino como que el
    /// cliente y el servidor pidan arquitecturas distintas en el mismo sitio.
    /// </para>
    /// <para>
    /// Los valores de <see cref="TheScaleMirrorGoldenValues"/> están afirmados BIT A BIT también en
    /// Rust (`the_scale_mirror_golden_values`, `wg3/scale.rs`). Misma técnica que
    /// <see cref="Wg3DensityFieldTests"/> y <see cref="Wg3IdentityTests"/>.
    /// </para>
    /// </summary>
    [TestFixture]
    public class Wg3ScaleFieldTests
    {
        private const int Seed = 42;

        /// <summary>
        /// `(bits, clase)` y no sólo la clase: los bits atan el CAMPO —pesos, tamaño de celda,
        /// `FloorDiv`, el hash— y la clase ata los UMBRALES. Con sólo la clase, mover un peso y
        /// compensar con un umbral pasaría el test y produciría otro mundo.
        /// </summary>
        [Test]
        public void TheScaleMirrorGoldenValues()
        {
            Expect(0f, 0f, 0x3F14EA4Fu, Wg3Scale.Large);                // 0.58170027
            Expect(75f, 75f, 0x3ECD568Cu, Wg3Scale.Medium);             // 0.40105093
            // Frontera EXACTA de celda gruesa: −45,9 y −46,0 caen en la misma celda −1 porque
            // −46/46 = −1 justo; una división truncada las separaría.
            Expect(-45.9f, 12.5f, 0x3F3FB848u, Wg3Scale.Large);         // 0.74890566
            Expect(-46f, 12.5f, 0x3F3FB848u, Wg3Scale.Large);
            Expect(1234.5f, -678.9f, 0x3F14A37Eu, Wg3Scale.Large);      // 0.5806197
            Expect(-4425f, -4425f, 0x3EB3B176u, Wg3Scale.Medium);       // 0.3509633
            Expect(13575f, -8925f, 0x3F04D6B7u, Wg3Scale.Medium);       // 0.5189013
            Expect(19f, -19f, 0x3F3EA6F8u, Wg3Scale.Large);             // 0.74473524
            // Las dos clases que los ocho de arriba no tocaban: las cuatro ramas de `ScaleAt` tienen
            // que estar cubiertas o los umbrales se pueden mover sin que nada se ponga rojo.
            Expect(-3700f, -530f, 0x3E2F2A5Cu, Wg3Scale.Narrow);        // 0.17106003
            Expect(-3404f, -530f, 0x3F4EF014u, Wg3Scale.Weird);         // 0.8083508
        }

        /// <summary>
        /// El REPARTO del campo, que es lo que ADR-119 D1 vino a mover y lo que ningún test miraba.
        ///
        /// <para>Con los umbrales de ADR-095 —0,34 / 0,70 / 0,92— salía `Weird` al <b>1,4 %</b>, o
        /// sea que toda la rareza del mundo vivía en una centésima parte de él. Las bandas de aquí
        /// son anchas a propósito: lo que se vigila es que nadie devuelva el mundo a aquello, no el
        /// segundo decimal.</para>
        /// </summary>
        [Test]
        public void TheFieldSpreadsTheFourClasses()
        {
            var n = new int[4];
            for (int i = 0; i < 160; i++)
            {
                for (int j = 0; j < 160; j++)
                {
                    float x = i * 37f - 2960f;
                    float z = j * 53f - 4240f;
                    n[(int)Wg3ScaleField.ScaleAt(Seed, x, z)]++;
                }
            }
            const float Total = 160f * 160f;
            float narrow = n[0] * 100f / Total;
            float medium = n[1] * 100f / Total;
            float large = n[2] * 100f / Total;
            float weird = n[3] * 100f / Total;
            UnityEngine.Debug.Log(
                $"[scale-spread] narrow {narrow:F1} % medium {medium:F1} % large {large:F1} % weird {weird:F1} %");
            Assert.That(narrow, Is.InRange(11f, 22f), "narrow");
            Assert.That(medium, Is.InRange(35f, 48f), "medium");
            Assert.That(large, Is.InRange(28f, 40f), "large");
            Assert.That(weird, Is.InRange(5f, 14f), "weird");
        }

        [Test]
        public void TheFieldIsPurePosition()
        {
            Assert.AreEqual(Wg3ScaleField.ValueAt(Seed, 17.3f, -211.9f),
                Wg3ScaleField.ValueAt(Seed, 17.3f, -211.9f));
            Assert.AreEqual(Wg3ScaleField.ScaleAt(Seed, 1234.5f, -678.9f),
                Wg3ScaleField.ScaleAt(Seed, 1234.5f, -678.9f));
        }

        private static void Expect(float x, float z, uint bits, Wg3Scale klass)
        {
            float v = Wg3ScaleField.ValueAt(Seed, x, z);
            Assert.AreEqual(bits, FloatToBits(v),
                $"({x},{z}) vale {v} = 0x{FloatToBits(v):X8}, no 0x{bits:X8}");
            Assert.AreEqual(klass, Wg3ScaleField.ScaleAt(Seed, x, z), $"({x},{z}) clase");
        }

        private static uint FloatToBits(float v)
            => System.BitConverter.ToUInt32(System.BitConverter.GetBytes(v), 0);
    }
}
