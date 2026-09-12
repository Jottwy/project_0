using System;
using System.Linq;
using BackroomsSurvival.WorldGen3;
using NUnit.Framework;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El papel de pared de WG3 tiene que (a) conservar la luminancia media del yeso al que
    /// sustituye —el tono lo ponen el material y el papel de cada espacio, no esta textura—,
    /// (b) dibujar el motivo como un susurro y no como un cartel, (c) tilear sin costura,
    /// (d) llevar relieve de verdad y (e) una máscara en el formato que URP/Lit lee.
    /// </summary>
    [TestFixture]
    public class Wg3WallpaperPatternTests
    {
        private static byte[] _albedo, _normal, _mask;

        [OneTimeSetUp]
        public void BuildOnce()
        {
            Wg3WallpaperPattern.Build(out _albedo, out _normal, out _mask);
        }

        private static double Luma(byte[] rgb, int i) =>
            0.299 * rgb[i * 3] + 0.587 * rgb[i * 3 + 1] + 0.114 * rgb[i * 3 + 2];

        [Test]
        public void BuffersHaveTheAdvertisedShape()
        {
            int n = Wg3WallpaperPattern.Size * Wg3WallpaperPattern.Size;
            Assert.AreEqual(n * 3, _albedo.Length);
            Assert.AreEqual(n * 3, _normal.Length);
            Assert.AreEqual(n * 4, _mask.Length);
        }

        /// <summary>Misma claridad que el yeso (≈230/255): sustituirlo no cambia lo validado.</summary>
        [Test]
        public void MeanLuminanceMatchesThePlasterItReplaces()
        {
            int n = Wg3WallpaperPattern.Size * Wg3WallpaperPattern.Size;
            double sum = 0;
            for (int i = 0; i < n; i++) sum += Luma(_albedo, i);
            double mean = sum / n;
            Assert.That(mean, Is.InRange(218.0, 238.0), "luminancia media del albedo");
        }

        /// <summary>El motivo se lee, pero como grabado: entre 8 y 60 de luma del p5 al p95.</summary>
        [Test]
        public void MotifIsAWhisperNotAPoster()
        {
            int n = Wg3WallpaperPattern.Size * Wg3WallpaperPattern.Size;
            var lumas = new double[n];
            for (int i = 0; i < n; i++) lumas[i] = Luma(_albedo, i);
            Array.Sort(lumas);
            double spread = lumas[(int)(n * 0.95)] - lumas[(int)(n * 0.05)];
            Assert.That(spread, Is.InRange(8.0, 60.0), "p95 − p5 de luma");
        }

        /// <summary>
        /// Periódico: el motivo se repite cada celda de 128 px y la celda divide al lado, así que
        /// la columna x y la x+128 llevan el MISMO dibujo (sólo cambian mancha y grano) — también
        /// en el borde, que es lo que hace que tilee sin costura. Compararlo con el vecino no vale:
        /// una franja está centrada en la columna 1 a propósito y la 0 y la 1023 difieren por diseño.
        /// </summary>
        [Test]
        public void TilesWithoutASeam()
        {
            int size = Wg3WallpaperPattern.Size;
            const int cell = 128;
            double ColumnMean(int x)
            {
                double s = 0;
                for (int y = 0; y < size; y++) s += Luma(_albedo, y * size + x);
                return s / size;
            }
            double RowMean(int y)
            {
                double s = 0;
                for (int x = 0; x < size; x++) s += Luma(_albedo, y * size + x);
                return s / size;
            }
            foreach (int x in new[] { 0, 1, 2, size - 1, size - 2 })
                Assert.LessOrEqual(Math.Abs(ColumnMean(x) - ColumnMean((x + cell) % size)), 3.0,
                    $"columna {x} y su repetición a una celda");
            foreach (int y in new[] { 0, 1, size - 1 })
                Assert.LessOrEqual(Math.Abs(RowMean(y) - RowMean((y + cell) % size)), 3.0,
                    $"fila {y} y su repetición a una celda");
        }

        /// <summary>La normal apunta hacia fuera de media y tiene relieve (no es un plano azul).</summary>
        [Test]
        public void NormalMapHasReliefAndPointsOutward()
        {
            int n = Wg3WallpaperPattern.Size * Wg3WallpaperPattern.Size;
            double sumB = 0, sumR = 0, sumR2 = 0;
            for (int i = 0; i < n; i++)
            {
                double r = _normal[i * 3];
                sumR += r; sumR2 += r * r;
                sumB += _normal[i * 3 + 2];
            }
            double meanB = sumB / n;
            double meanR = sumR / n;
            double stdR = Math.Sqrt(Math.Max(0, sumR2 / n - meanR * meanR));
            Assert.Greater(meanB, 200.0, "la media de la normal mira hacia fuera");
            Assert.That(meanR, Is.InRange(120.0, 136.0), "sin sesgo lateral");
            Assert.Greater(stdR, 4.0, "hay relieve");
        }

        /// <summary>R metal a 0, G oclusión entre el suelo y 1, A suavidad de papel mate.</summary>
        [Test]
        public void MaskIsInUrpLitLayout()
        {
            int n = Wg3WallpaperPattern.Size * Wg3WallpaperPattern.Size;
            byte gMin = 255, gMax = 0, aMin = 255, aMax = 0;
            for (int i = 0; i < n; i++)
            {
                Assert.AreEqual(0, _mask[i * 4], "canal R (metal) tiene que ser 0");
                byte g = _mask[i * 4 + 1], a = _mask[i * 4 + 3];
                if (g < gMin) gMin = g; if (g > gMax) gMax = g;
                if (a < aMin) aMin = a; if (a > aMax) aMax = a;
            }
            Assert.That(gMin, Is.InRange(170, 200), "suelo de oclusión ≈ 0,72");
            Assert.GreaterOrEqual(gMax, 250, "lo más alto del relieve no se ocluye");
            Assert.That(aMin, Is.InRange(18, 30), "suavidad base ≈ 0,09");
            Assert.That(aMax, Is.InRange(28, 40), "la mancha sube la suavidad, poco");
        }
    }
}
