using NUnit.Framework;
using UnityEngine;
using BackroomsSurvival.Gameplay.Audio;

namespace BackroomsSurvival.Tests.EditMode
{
    /// <summary>
    /// La oclusión de audio pasa de sí/no a CONTAR paredes (FluorescentHumDirector y
    /// OfficeAmbienceDirector, 2026-09-12): estos tests fijan que el caso de UNA pared —el único
    /// que Joel ya validó de oído— sale exactamente igual que antes, y que dos o más paredes
    /// extrapolan sin romperse (ni volumen negativo, ni corte por debajo del suelo).
    /// </summary>
    public sealed class AudioOcclusionMathTests
    {
        [Test]
        public void SinParedes_VolumenPleno()
        {
            Assert.AreEqual(1f, AudioOcclusionMath.VolumeGain(0f, 0.35f), 0.0001f);
        }

        [Test]
        public void UnaPared_ReproduceElValorYaValidado()
        {
            // occluded^1 == occluded: el mismo Mathf.Lerp(1, occluded, 1) de antes.
            Assert.AreEqual(0.35f, AudioOcclusionMath.VolumeGain(1f, 0.35f), 0.0001f);
            Assert.AreEqual(0.45f, AudioOcclusionMath.VolumeGain(1f, 0.45f), 0.0001f);
        }

        [Test]
        public void DosParedes_FiltraMasQueUna()
        {
            float una = AudioOcclusionMath.VolumeGain(1f, 0.35f);
            float dos = AudioOcclusionMath.VolumeGain(2f, 0.35f);
            Assert.AreEqual(0.1225f, dos, 0.0001f); // 0,35²
            Assert.Less(dos, una, "dos paredes tienen que sonar más tapadas que una");
        }

        [Test]
        public void VolumenNuncaNegativoNiPorEncimaDeUno()
        {
            for (float n = -1f; n <= 5f; n += 0.5f)
            {
                float g = AudioOcclusionMath.VolumeGain(n, 0.4f);
                Assert.GreaterOrEqual(g, 0f);
                Assert.LessOrEqual(g, 1f);
            }
        }

        [Test]
        public void SinParedes_CorteAbierto()
        {
            Assert.AreEqual(22000f, AudioOcclusionMath.Cutoff(0f, 22000f, 800f, 200f), 0.01f);
        }

        [Test]
        public void UnaPared_ReproduceElCorteYaValidado()
        {
            Assert.AreEqual(800f, AudioOcclusionMath.Cutoff(1f, 22000f, 800f, 200f), 0.01f);
            Assert.AreEqual(900f, AudioOcclusionMath.Cutoff(1f, 22000f, 900f, 250f), 0.01f);
        }

        [Test]
        public void TresParedes_NoBajaDelSuelo()
        {
            // Extrapolar la recta a wallCount=3 daría un número muy negativo; el suelo lo atrapa.
            float c = AudioOcclusionMath.Cutoff(3f, 22000f, 800f, 200f);
            Assert.AreEqual(200f, c, 0.01f);
        }

        [Test]
        public void ElCorteBajaMonotonoConMasParedes()
        {
            float c0 = AudioOcclusionMath.Cutoff(0f, 22000f, 800f, 200f);
            float c1 = AudioOcclusionMath.Cutoff(1f, 22000f, 800f, 200f);
            float c2 = AudioOcclusionMath.Cutoff(2f, 22000f, 800f, 200f);
            Assert.Greater(c0, c1);
            Assert.GreaterOrEqual(c1, c2); // c2 ya puede estar clavado en el suelo
        }
    }
}
