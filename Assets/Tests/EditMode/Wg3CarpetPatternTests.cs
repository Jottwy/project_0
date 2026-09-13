using System;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// La moqueta de WG3 tiene que (a) ser clara y cálida en el albedo —el ocre y la luminancia
    /// validada los pone <c>_BaseColor</c>, no esta textura—, (b) estar manchada sin ser un cartel,
    /// (c) tilear sin costura, (d) llevar relieve de pelo y (e) una máscara URP/Lit donde lo mojado
    /// brilla más que lo seco.
    /// </summary>
    [TestFixture]
    public class Wg3CarpetPatternTests
    {
        private static byte[] _albedo, _normal, _mask;
        private static int Size => Wg3CarpetPattern.Size;

        [OneTimeSetUp]
        public void BuildOnce()
        {
            Wg3CarpetPattern.Build(out _albedo, out _normal, out _mask);
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
            Assert.AreEqual(0, Size & (Size - 1), "el lado es potencia de dos: `At` enmascara");
        }

        /// <summary>Dos construcciones dan los mismos bytes: sin RNG ni orden de recorrido.</summary>
        [Test]
        public void IsDeterministic()
        {
            Wg3CarpetPattern.Build(out byte[] a, out byte[] nrm, out byte[] m);
            Assert.IsTrue(a.AsSpan().SequenceEqual(_albedo), "albedo");
            Assert.IsTrue(nrm.AsSpan().SequenceEqual(_normal), "normal");
            Assert.IsTrue(m.AsSpan().SequenceEqual(_mask), "máscara");
        }

        /// <summary>Crema cálido y claro: R ≥ G ≥ B de media y luma alta. Si alguien mete el ocre en
        /// el albedo, el tinte del material lo multiplica otra vez y el suelo sale marrón.</summary>
        [Test]
        public void AlbedoIsWarmLightCream()
        {
            int n = Size * Size;
            double r = 0, g = 0, b = 0, l = 0;
            for (int i = 0; i < n; i++)
            {
                r += _albedo[i * 3]; g += _albedo[i * 3 + 1]; b += _albedo[i * 3 + 2];
                l += Luma(_albedo, i);
            }
            r /= n; g /= n; b /= n; l /= n;
            Assert.That(l, Is.InRange(190.0, 220.0), "luma media");
            Assert.GreaterOrEqual(r, g, "R ≥ G");
            Assert.Greater(g, b, "G > B: cálido");
        }

        /// <summary>Manchada de verdad pero sin cartel: p95 − p5 de luma entre 25 y 90.</summary>
        [Test]
        public void StainsAreVisibleButNotAPoster()
        {
            int n = Size * Size;
            var lumas = new double[n];
            for (int i = 0; i < n; i++) lumas[i] = Luma(_albedo, i);
            Array.Sort(lumas);
            double spread = lumas[(int)(n * 0.95)] - lumas[(int)(n * 0.05)];
            Assert.That(spread, Is.InRange(25.0, 90.0), "p95 − p5 de luma");
        }

        /// <summary>
        /// Sin costura: la diferencia media entre la última columna y la primera (y la última fila y
        /// la primera) no supera a la de dos columnas vecinas cualesquiera. Con el periodo de pelo de
        /// 5 px del prototipo la costura salía y este test es el que la ve.
        /// </summary>
        [Test]
        public void TilesWithoutASeam()
        {
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
            // Pares IMPAR → PAR, como el del borde (2047 → 0): el grano va en bloques de 2 px, y un
            // par dentro del mismo bloque (100 → 101) diferencia menos por construcción. Con esos
            // pares de referencia el test daba costura (4,06 contra 3,87) donde no la hay.
            double neighbourCols = 0, neighbourRows = 0;
            int pairs = 0;
            for (int k = 1; k < Size - 1; k += 128)
            {
                neighbourCols += ColDiff(k, k + 1);
                neighbourRows += RowDiff(k, k + 1);
                pairs++;
            }
            neighbourCols /= pairs;
            neighbourRows /= pairs;
            Assert.LessOrEqual(ColDiff(Size - 1, 0), neighbourCols * 1.3, "costura vertical");
            Assert.LessOrEqual(RowDiff(Size - 1, 0), neighbourRows * 1.3, "costura horizontal");
        }

        [Test]
        public void NormalMapHasPileReliefAndPointsOutward()
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
            double stdR = Math.Sqrt(Math.Max(0, sumR2 / n - meanR * meanR));
            Assert.Greater(sumB / n, 200.0, "la media de la normal mira hacia fuera");
            Assert.That(meanR, Is.InRange(120.0, 136.0), "sin sesgo lateral");
            Assert.Greater(stdR, 4.0, "hay relieve de pelo");
        }

        /// <summary>R metal 0; G oclusión en su banda; A suavidad: mate en seco y bastante más alta
        /// en lo mojado, que es lo único que lo hace leerse «mojado» bajo la lámpara.</summary>
        [Test]
        public void MaskIsInUrpLitLayoutAndWetShines()
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
            Assert.That(gMin, Is.InRange(160, 195), "suelo de oclusión ≈ 0,74 − mojado");
            Assert.GreaterOrEqual(gMax, 245, "lo más alto del pelo no se ocluye");
            Assert.That(aMin, Is.InRange(10, 16), "suavidad seca ≈ 0,05");
            Assert.That(aMax, Is.InRange(80, 105), "lo mojado llega a ≈ 0,35 (+ mancha)");
        }
    }
}
