using System;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// La placa de techo de WG3 (A5, 13-09) tiene que (a) devolver la MISMA luminancia que la
    /// `CeilingTiles.png` de 512 a la que sustituye —el material conserva su tinte y la luz validada
    /// no se mueve—, (b) repetir la rejilla de 60 cm exacta, (c) tilear sin costura, (d) llevar
    /// relieve y (e) máscara URP/Lit.
    /// </summary>
    [TestFixture]
    public class Wg3CeilingPatternTests
    {
        private static byte[] _albedo, _normal, _mask;
        private static int Size => Wg3CeilingPattern.Size;

        [OneTimeSetUp]
        public void BuildOnce()
        {
            Wg3CeilingPattern.Build(out _albedo, out _normal, out _mask);
        }

        private static double Luma(byte[] rgb, int i) =>
            0.299 * rgb[i * 3] + 0.587 * rgb[i * 3 + 1] + 0.114 * rgb[i * 3 + 2];

        [Test]
        public void BuffersHaveTheAdvertisedShape()
        {
            int n = Size * Size;
            Assert.AreEqual(n * 3, _albedo.Length);
            Assert.AreEqual(n * 3, _normal.Length);
            Assert.AreEqual(n * 4, _mask.Length);
            Assert.AreEqual(0, Size % Wg3CeilingPattern.PlatePx, "la placa divide el lado: 4 placas por repetición");
        }

        [Test]
        public void IsDeterministic()
        {
            Wg3CeilingPattern.Build(out byte[] a, out byte[] nrm, out byte[] m);
            Assert.IsTrue(a.AsSpan().SequenceEqual(_albedo), "albedo");
            Assert.IsTrue(nrm.AsSpan().SequenceEqual(_normal), "normal");
            Assert.IsTrue(m.AsSpan().SequenceEqual(_mask), "máscara");
        }

        /// <summary>La media sRGB de `CeilingTiles.png` es (211,2 / 204,2 / 167,3). Dentro de ±3 por
        /// canal, el tinte del material (0,80/0,80/0,77) devuelve lo mismo que antes.</summary>
        [Test]
        public void MeanColourMatchesTheTextureItReplaces()
        {
            int n = Size * Size;
            double r = 0, g = 0, b = 0;
            for (int i = 0; i < n; i++) { r += _albedo[i * 3]; g += _albedo[i * 3 + 1]; b += _albedo[i * 3 + 2]; }
            Assert.That(r / n, Is.EqualTo(211.2).Within(3.0), "R medio");
            Assert.That(g / n, Is.EqualTo(204.2).Within(3.0), "G medio");
            Assert.That(b / n, Is.EqualTo(167.3).Within(3.0), "B medio");
        }

        /// <summary>La junta es lo que se lee desde abajo: la columna del borde de una placa es más
        /// oscura que el centro de la placa, en todas las placas.</summary>
        [Test]
        public void TheGridIsReadable()
        {
            int plate = Wg3CeilingPattern.PlatePx;
            double Column(int x)
            {
                double s = 0;
                for (int y = 0; y < Size; y++) s += Luma(_albedo, y * Size + x);
                return s / Size;
            }
            for (int k = 0; k < Size / plate; k++)
                Assert.Less(Column(k * plate) + 8.0, Column(k * plate + plate / 2), $"junta de la placa {k}");
        }

        /// <summary>El borde (1023 → 0) es una junta partida en dos, igual que la de 255 → 256: se
        /// compara contra ESA pareja, no contra dos columnas del centro de una placa.</summary>
        [Test]
        public void TilesWithoutASeam()
        {
            int plate = Wg3CeilingPattern.PlatePx;
            double ColDiff(int x0, int x1)
            {
                double s = 0;
                for (int y = 0; y < Size; y++) s += Math.Abs(Luma(_albedo, y * Size + x0) - Luma(_albedo, y * Size + x1));
                return s / Size;
            }
            double RowDiff(int y0, int y1)
            {
                double s = 0;
                for (int x = 0; x < Size; x++) s += Math.Abs(Luma(_albedo, y0 * Size + x) - Luma(_albedo, y1 * Size + x));
                return s / Size;
            }
            double innerCol = (ColDiff(plate - 1, plate) + ColDiff(2 * plate - 1, 2 * plate)) / 2.0;
            double innerRow = (RowDiff(plate - 1, plate) + RowDiff(2 * plate - 1, 2 * plate)) / 2.0;
            Assert.LessOrEqual(ColDiff(Size - 1, 0), innerCol * 1.5 + 1.0, "costura vertical");
            Assert.LessOrEqual(RowDiff(Size - 1, 0), innerRow * 1.5 + 1.0, "costura horizontal");
        }

        [Test]
        public void NormalMapHasReliefAndPointsOutward()
        {
            int n = Size * Size;
            double sumB = 0, sumR = 0, sumR2 = 0;
            for (int i = 0; i < n; i++)
            {
                double r = _normal[i * 3];
                sumR += r; sumR2 += r * r;
                sumB += _normal[i * 3 + 2];
            }
            double meanR = sumR / n;
            Assert.Greater(sumB / n, 200.0, "la media de la normal mira hacia fuera");
            Assert.That(meanR, Is.InRange(120.0, 136.0), "sin sesgo lateral");
            Assert.Greater(Math.Sqrt(Math.Max(0, sumR2 / n - meanR * meanR)), 3.0, "hay relieve");
        }

        [Test]
        public void MaskIsInUrpLitLayout()
        {
            int n = Size * Size;
            byte gMin = 255, gMax = 0, aMin = 255, aMax = 0;
            for (int i = 0; i < n; i++)
            {
                Assert.AreEqual(0, _mask[i * 4], "canal R (metal) tiene que ser 0");
                byte g = _mask[i * 4 + 1], a = _mask[i * 4 + 3];
                if (g < gMin) gMin = g; if (g > gMax) gMax = g;
                if (a < aMin) aMin = a; if (a > aMax) aMax = a;
            }
            Assert.That(gMin, Is.InRange(165, 190), "suelo de oclusión ≈ 0,70");
            Assert.GreaterOrEqual(gMax, 250, "lo más alto del relieve no se ocluye");
            Assert.That(aMin, Is.InRange(8, 14), "suavidad de placa ≈ 0,04 (la de hoy)");
            Assert.That(aMax, Is.InRange(8, 30), "la gotera brilla poco");
        }
    }
}
