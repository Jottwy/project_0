using BackroomsSurvival.WorldGen3;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-103 — el espejo C# del campo de identidad.
    ///
    /// Los golden values de <see cref="TheIdentityMirrorGoldenValues"/> están afirmados BIT A BIT
    /// también en Rust (`the_identity_mirror_golden_values`, `wg3/tests.rs`): si cualquiera de los
    /// dos lados se mueve, su test se pone rojo. Es la misma técnica que ata Wg3ScaleField al
    /// oráculo de composición, en pequeño — y aquí el fallo que caza es una identidad que el
    /// servidor resuelve distinta que el cliente, que no rompe geometría (D8: el cliente solo la
    /// usa para atmósfera) pero teñiría una celda de frontera como otro sitio.
    /// </summary>
    [TestFixture]
    public class Wg3IdentityTests
    {
        private const int Seed = 20260901;

        [Test]
        public void TheIdentityMirrorGoldenValues()
        {
            Expect(75f, 75f, Wg3LevelAnchor.RemodeledMess, Wg3LevelAnchor.IcyRooms,
                BitsToFloat(0x3F1E7E7Au)); // 0.6191174
            Expect(975f, 75f, Wg3LevelAnchor.Threshold, Wg3LevelAnchor.Threshold, 0f);
            // Frontera EXACTA de celda: 900 pertenece a la celda 1 (misma que 975) y -900 a la
            // celda -1 (misma que -825) — donde una división truncada espejaría el campo.
            Expect(900f, 75f, Wg3LevelAnchor.Threshold, Wg3LevelAnchor.Threshold, 0f);
            Expect(-900f, 75f, Wg3LevelAnchor.RemodeledMess, Wg3LevelAnchor.IcyRooms,
                BitsToFloat(0x3E53D69Cu));
            Expect(-825f, 75f, Wg3LevelAnchor.RemodeledMess, Wg3LevelAnchor.IcyRooms,
                BitsToFloat(0x3E53D69Cu)); // 0.20687336
            Expect(75f, -825f, Wg3LevelAnchor.RemodeledMess, Wg3LevelAnchor.RemodeledMess, 0f);
            Expect(4575f, 4575f, Wg3LevelAnchor.IcyRooms, Wg3LevelAnchor.IcyRooms, 0f);
            Expect(-4425f, -4425f, Wg3LevelAnchor.Threshold, Wg3LevelAnchor.ZenithStation,
                BitsToFloat(0x3F12B55Cu)); // 0.5730798
            Expect(13575f, -8925f, Wg3LevelAnchor.ZenithStation, Wg3LevelAnchor.ZenithStation, 0f);
            Expect(-13425f, 9075f, Wg3LevelAnchor.RemodeledMess, Wg3LevelAnchor.IcyRooms,
                BitsToFloat(0x3DFDC6BCu)); // 0.12391421
        }

        /// <summary>D7 — la y está en la firma y NO participa en fase 1.</summary>
        [Test]
        public void TheYAxisIsPinnedToZero()
        {
            Wg3LevelMix ground = Wg3Identity.At(Seed, 75f, 0f, 75f);
            Wg3LevelMix upstairs = Wg3Identity.At(Seed, 75f, 332f, 75f);
            Assert.AreEqual(ground, upstairs);
        }

        /// <summary>D4 — la región lee lo mismo en su centro que por dentro: la celda es múltiplo
        /// exacto de la región y ninguna cae a caballo de dos identidades, tampoco en el
        /// hemisferio negativo, donde una división truncada espejaría el campo.</summary>
        [Test]
        public void AnIdentityCellNeverSplitsARegion()
        {
            for (int rx = -7; rx < 7; rx++)
            {
                for (int rz = -7; rz < 7; rz++)
                {
                    Wg3LevelMix centre = Wg3Identity.ForRegion(Seed, rx, rz);
                    float minX = rx * Wg3Identity.RegionM;
                    float minZ = rz * Wg3Identity.RegionM;
                    float maxX = minX + Wg3Identity.RegionM;
                    float maxZ = minZ + Wg3Identity.RegionM;
                    Assert.AreEqual(centre, Wg3Identity.At(Seed, minX + 0.5f, 0f, minZ + 0.5f));
                    Assert.AreEqual(centre, Wg3Identity.At(Seed, maxX - 0.5f, 0f, minZ + 0.5f));
                    Assert.AreEqual(centre, Wg3Identity.At(Seed, minX + 0.5f, 0f, maxZ - 0.5f));
                    Assert.AreEqual(centre, Wg3Identity.At(Seed, maxX - 0.5f, 0f, maxZ - 0.5f));
                }
            }
        }

        /// <summary>D5 — Threshold con todos los factores a 1.0 EXACTAMENTE: la guardia de
        /// regresión. Y una mezcla pura devuelve el perfil del ancla sin ruido de coma
        /// flotante.</summary>
        [Test]
        public void PureThresholdIsAllOnesExactly()
        {
            var pure = new Wg3LevelMix
            {
                Lower = Wg3LevelAnchor.Threshold,
                Upper = Wg3LevelAnchor.Threshold,
                TowardUpper = 0f,
            };
            Wg3LevelProfile p = pure.Profile();
            Assert.AreEqual(1f, p.TargetAreaMul);
            Assert.AreEqual(1f, p.WeirdSpreadMul);
            Assert.AreEqual(1f, p.VoidChanceMul);
            Assert.AreEqual(1f, p.ClearHeightMul);
            Assert.AreEqual(1f, p.BandWidthMul);
            Assert.AreEqual(0f, p.MaxDepthDelta);
        }

        private static void Expect(float x, float z, Wg3LevelAnchor lower, Wg3LevelAnchor upper,
            float towardUpper)
        {
            Wg3LevelMix m = Wg3Identity.At(Seed, x, 0f, z);
            Assert.AreEqual(lower, m.Lower, $"lower en ({x},{z})");
            Assert.AreEqual(upper, m.Upper, $"upper en ({x},{z})");
            Assert.AreEqual(FloatToBits(towardUpper), FloatToBits(m.TowardUpper),
                $"peso en ({x},{z}): esperado {towardUpper}, salió {m.TowardUpper}");
        }

        private static float BitsToFloat(uint bits)
            => System.BitConverter.ToSingle(System.BitConverter.GetBytes(bits), 0);

        private static uint FloatToBits(float v)
            => System.BitConverter.ToUInt32(System.BitConverter.GetBytes(v), 0);
    }
}
