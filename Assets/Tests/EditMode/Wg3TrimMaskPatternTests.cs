using System;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// A5 (13-09) — la máscara del remate sale del normal del yeso: plano = sin oclusión, inclinado
    /// = ocluido hasta el suelo, y la suavidad es la 0,22 que el material tenía en su float.
    /// </summary>
    [TestFixture]
    public class Wg3TrimMaskPatternTests
    {
        private static byte[] Normal(params (double x, double y)[] ns)
        {
            var rgb = new byte[ns.Length * 3];
            for (int i = 0; i < ns.Length; i++)
            {
                double z = Math.Sqrt(Math.Max(0, 1 - ns[i].x * ns[i].x - ns[i].y * ns[i].y));
                rgb[i * 3] = (byte)Math.Round((ns[i].x * 0.5 + 0.5) * 255);
                rgb[i * 3 + 1] = (byte)Math.Round((ns[i].y * 0.5 + 0.5) * 255);
                rgb[i * 3 + 2] = (byte)Math.Round((z * 0.5 + 0.5) * 255);
            }
            return rgb;
        }

        [Test]
        public void FlatNormal_IsUnoccluded_WithTheOldSmoothness()
        {
            byte[] m = Wg3TrimMaskPattern.FromNormal(Normal((0, 0)));
            Assert.AreEqual(0, m[0], "metal");
            Assert.GreaterOrEqual(m[1], 250, "plano: oclusión ≈ 1");
            Assert.AreEqual(56, m[3], "suavidad 0,22 · 255");
        }

        [Test]
        public void SteepNormal_HitsTheFloor()
        {
            byte[] m = Wg3TrimMaskPattern.FromNormal(Normal((0.8, 0.0)));
            Assert.That(m[1], Is.EqualTo((int)Math.Round(Wg3TrimMaskPattern.AoFloor * 255)).Within(1));
        }

        [Test]
        public void OcclusionIsMonotonicInTilt()
        {
            byte[] m = Wg3TrimMaskPattern.FromNormal(Normal((0.0, 0.0), (0.3, 0.0), (0.5, 0.0), (0.7, 0.0)));
            Assert.GreaterOrEqual(m[1], m[5]);
            Assert.GreaterOrEqual(m[5], m[9]);
            Assert.GreaterOrEqual(m[9], m[13]);
            Assert.Greater(m[1], m[13], "algo de cavidad tiene que haber");
        }

        [Test]
        public void RejectsMalformedInput()
        {
            Assert.Throws<ArgumentException>(() => Wg3TrimMaskPattern.FromNormal(new byte[4]));
        }
    }
}
