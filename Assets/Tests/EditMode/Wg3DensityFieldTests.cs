using BackroomsSurvival.WorldGen3;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// Auditoría 2026-09-02, Fase 6 — el espejo C# del campo de densidad.
    ///
    /// Los golden values de <see cref="TheDensityMirrorGoldenValues"/> están afirmados BIT A BIT
    /// también en Rust (`the_density_mirror_golden_values`, `wg3/density.rs`): si cualquiera de los
    /// dos lados se mueve, su test se pone rojo. Misma técnica que <see cref="Wg3IdentityTests"/>.
    /// </summary>
    [TestFixture]
    public class Wg3DensityFieldTests
    {
        private const int Seed = 42;

        [Test]
        public void TheDensityMirrorGoldenValues()
        {
            Expect(0f, 0f, 0x3E800054u, Wg3Density.Empty);             // 0.2500025
            Expect(75f, 75f, 0x3EEA776Au, Wg3Density.Sparse);          // 0.4579423
            // Frontera EXACTA de celda gruesa: −37,9 y −38,0 caen en la misma celda −1 porque
            // −38/38 = −1 justo; una división truncada las separaría.
            Expect(-37.9f, 12.5f, 0x3F0D6FDCu, Wg3Density.Sparse);     // 0.5524881
            Expect(-38f, 12.5f, 0x3F0D6FDCu, Wg3Density.Sparse);
            Expect(1234.5f, -678.9f, 0x3E8F9552u, Wg3Density.Empty);   // 0.2804361
            Expect(-4425f, -4425f, 0x3F2B8C8Au, Wg3Density.Structured); // 0.6701132
            Expect(13575f, -8925f, 0x3E9CDC26u, Wg3Density.Empty);     // 0.3063671
            Expect(19f, -19f, 0x3ED95E1Eu, Wg3Density.Sparse);         // 0.42454618
        }

        [Test]
        public void TheFieldIsPurePosition()
        {
            Assert.AreEqual(Wg3DensityField.ValueAt(Seed, 17.3f, -211.9f),
                Wg3DensityField.ValueAt(Seed, 17.3f, -211.9f));
            Assert.AreEqual(Wg3DensityField.ClassAt(Seed, 1234.5f, -678.9f),
                Wg3DensityField.ClassAt(Seed, 1234.5f, -678.9f));
        }

        /// <summary>Frontera EXACTA de celda: −38 pertenece a la celda −1, no a la 0 — donde una
        /// división truncada espejaría el campo en el origen.</summary>
        [Test]
        public void TheOriginDoesNotMirror()
        {
            float a = Wg3DensityField.ValueAt(Seed, -37.9f, 12.5f);
            float b = Wg3DensityField.ValueAt(Seed, -38.0f, 12.5f);
            // Mismo valor fino (celda fina de 13 m desplazada), distinta celda gruesa: no pueden
            // coincidir salvo por la casualidad de un hash igual, que los golden descartan.
            Assert.AreNotEqual(a, b);
        }

        [Test]
        public void WeirdScalePromotesEverythingButTheVoid()
        {
            for (int i = 0; i < 500; i++)
            {
                float x = i * 13.7f, z = i * 5.3f - 900f;
                Wg3Density baseClass = Wg3DensityField.ClassAt(Seed, x, z);
                Wg3Density weird = Wg3DensityField.ForSpace(Seed, x, z, Wg3Scale.Weird);
                Wg3Density normal = Wg3DensityField.ForSpace(Seed, x, z, Wg3Scale.Medium);
                Assert.AreEqual(baseClass, normal);
                if (baseClass == Wg3Density.Empty || baseClass == Wg3Density.Anomalous)
                    Assert.AreEqual(baseClass, weird);
                else
                    Assert.AreEqual(baseClass + 1, weird);
            }
        }

        private static void Expect(float x, float z, uint bits, Wg3Density klass)
        {
            float v = Wg3DensityField.ValueAt(Seed, x, z);
            Assert.AreEqual(bits, FloatToBits(v),
                $"({x},{z}) vale {v} = 0x{FloatToBits(v):X8}, no 0x{bits:X8}");
            Assert.AreEqual(klass, Wg3DensityField.ClassAt(Seed, x, z));
        }

        private static uint FloatToBits(float v)
            => System.BitConverter.ToUInt32(System.BitConverter.GetBytes(v), 0);
    }
}
