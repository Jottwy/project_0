#if UNITY_EDITOR
using System;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// La PLACA DE TECHO Backrooms de WG3, píxel a píxel: albedo, normal y máscara.
    /// </summary>
    /// <remarks>
    /// Sustituye a <c>CeilingTiles.png</c> (512 px heredado de WG2, fibra de ruido plano y el relieve
    /// del YESO del pack prestado como normal) en <c>Wg3_Ceiling.mat</c>. Fase A5, 13-09.
    ///
    /// # Lo que conserva, a propósito
    ///
    /// La rejilla de 60 cm en 4 × 4 por repetición de 2,4 m (escala 0,8333, múltiplo del ancla de UV
    /// de 12 m) y el COLOR MEDIO de la textura vieja (211/204/167 sRGB): el material mantiene su tinte
    /// 0,80/0,80/0,77, así que la luz del techo que Joel validó devuelve lo mismo.
    ///
    /// # Lo que añade
    ///
    /// Junta hundida con canto biselado (la rejilla se lee por su sombra, no por una raya pintada),
    /// fibra mineral con perforación y fisuras cortas y dispersas, un tono ligeramente distinto por
    /// placa y un combado central, y dos goteras de las dieciséis placas con cerco tenue y roto. Un
    /// cerco continuo se leía como un aro dibujado (lo mismo que en la moqueta).
    ///
    /// # Determinismo y periodicidad
    ///
    /// Hash entero sobre (x, y, semilla), ruido de valor con retícula envuelta y la placa dividiendo
    /// el lado: tilea sin costura por construcción. Sólo en editor.
    /// </remarks>
    public static class Wg3CeilingPattern
    {
        /// <summary>1024 sobre 2,4 m: 2,3 mm por píxel. A 512 la junta eran tres píxeles.</summary>
        public const int Size = 1024;

        /// <summary>Una placa de 60 cm. Divide a <see cref="Size"/>: cuatro por repetición.</summary>
        public const int PlatePx = 256;

        /// <summary>Metros de mundo por repetición: cuatro placas de 60 cm.</summary>
        public const float RepeatM = 2.4f;

        /// <summary>Escala del material, la que ya llevaba: 2 / 2,4.</summary>
        public const float MaterialScale = 0.8333f;

        // ── Paleta: media calibrada contra CeilingTiles.png ──────────────────
        private static readonly double[] Base = { 211.9, 205.0, 168.4 };

        private const double GroovePx = 3.0;   // medio ancho de junta (≈ 1,4 cm entera)
        private const double BevelPx = 7.0;
        private const double NormalStrength = 2.4;
        private const double AoFloor = 0.70;
        private const double SmoothnessPlate = 0.04; // la de Wg3_Ceiling antes de A5
        private const double SmoothnessStain = 0.03;

        /// <summary>
        /// Genera los tres buffers, <c>y = 0</c> en la fila de ABAJO (convención de
        /// <c>Texture2D.SetPixels32</c>). Albedo y normal RGB entrelazado; máscara RGBA.
        /// </summary>
        public static void Build(out byte[] albedoRgb, out byte[] normalRgb, out byte[] maskRgba)
        {
            int n = Size * Size;

            var grain = new double[n];
            var pin = new double[n];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    grain[i] = Hash01(x, y, 3201) - 0.5;
                    pin[i] = Hash01(x / 3, y / 3, 3220) > 0.93 ? 1.0 : 0.0;
                }
            grain = Blur(grain);
            pin = Blur(pin);

            albedoRgb = new byte[n * 3];
            var h = new double[n];
            var stainField = new float[n];

            for (int y = 0; y < Size; y++)
            {
                int py = y / PlatePx, ly = y % PlatePx;
                for (int x = 0; x < Size; x++)
                {
                    int px = x / PlatePx, lx = x % PlatePx;
                    int i = y * Size + x;

                    // Junta y canto: distancia al borde de la placa, repartida a los dos lados.
                    int ex = Math.Min(lx, PlatePx - 1 - lx), ey = Math.Min(ly, PlatePx - 1 - ly);
                    double edge = Math.Min(ex, ey);
                    double groove = 1.0 - Smooth(GroovePx - 1.0, GroovePx + 0.5, edge);
                    double bevel = Smooth(GroovePx, GroovePx + BevelPx, edge);

                    // Tono por placa y combado central.
                    double plateTone = (Hash01(px, py, 3101) - 0.5) * 0.06;
                    double cxn = (lx - PlatePx * 0.5) / (PlatePx * 0.5);
                    double cyn = (ly - PlatePx * 0.5) / (PlatePx * 0.5);
                    double sag = (1 - Math.Min(1.0, cxn * cxn)) * (1 - Math.Min(1.0, cyn * cyn));

                    // Fisuras cortas: el cero del ruido, sólo donde otro ruido lo permite.
                    double fis = Math.Abs(Fbm(x, y, 48, 3210, 3));
                    double sparse = Smooth(0.12, 0.30, Fbm(x, y, 6, 3230, 3));
                    double fissure = (1 - Smooth(0.0, 0.022, fis)) * sparse;

                    double stain = 0.0, ring = 0.0;
                    Gotera(x, y, 1, 2, 0.38, 0.55, 0.40, ref stain, ref ring);
                    Gotera(x, y, 3, 0, 0.66, 0.34, 0.28, ref stain, ref ring);

                    double v = 1.0 + plateTone + 0.035 * grain[i] * 2.0 - 0.08 * fissure - 0.06 * pin[i] + 0.02 * sag;
                    v *= (1 - 0.13 * groove) * (0.94 + 0.06 * bevel);
                    v *= (1 - 0.10 * stain) * (1 - 0.06 * ring);
                    // La gotera es PARDA: pierde azul, y algo de verde.
                    albedoRgb[i * 3 + 0] = ToByte(Base[0] * v);
                    albedoRgb[i * 3 + 1] = ToByte(Base[1] * v * (1 - 0.04 * stain - 0.03 * ring));
                    albedoRgb[i * 3 + 2] = ToByte(Base[2] * v * (1 - 0.16 * stain - 0.08 * ring));

                    h[i] = 0.9 * bevel - 0.5 * groove + 0.12 * grain[i] - 0.25 * fissure - 0.2 * pin[i] + 0.08 * sag;
                    stainField[i] = (float)stain;
                }
            }
            h = Blur(h);

            double hMin = double.MaxValue, hMax = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (h[i] < hMin) hMin = h[i];
                if (h[i] > hMax) hMax = h[i];
            }
            double hSpan = Math.Max(1e-6, hMax - hMin);

            normalRgb = new byte[n * 3];
            maskRgba = new byte[n * 4];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    double dhx = (h[At(x + 1, y)] - h[At(x - 1, y)]) * 0.5;
                    double dhy = (h[At(x, y + 1)] - h[At(x, y - 1)]) * 0.5;
                    double nx = -dhx * NormalStrength, ny = -dhy * NormalStrength, nz = 1.0;
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    normalRgb[i * 3 + 0] = ToByte((nx / len * 0.5 + 0.5) * 255.0);
                    normalRgb[i * 3 + 1] = ToByte((ny / len * 0.5 + 0.5) * 255.0);
                    normalRgb[i * 3 + 2] = ToByte((nz / len * 0.5 + 0.5) * 255.0);

                    double ao = AoFloor + (1.0 - AoFloor) * (h[i] - hMin) / hSpan;
                    double smooth = SmoothnessPlate + SmoothnessStain * stainField[i];
                    maskRgba[i * 4 + 0] = 0;
                    maskRgba[i * 4 + 1] = ToByte(ao * 255.0);
                    maskRgba[i * 4 + 2] = 0;
                    maskRgba[i * 4 + 3] = ToByte(smooth * 255.0);
                }
            }
        }

        /// <summary>Una gotera en la placa (<paramref name="plateX"/>, <paramref name="plateY"/>):
        /// mancha con el borde deformado y un cerco roto. Fijas, no sorteadas.</summary>
        private static void Gotera(int x, int y, int plateX, int plateY, double cx, double cy, double radius,
            ref double stain, ref double ring)
        {
            double ccx = (plateX + cx) * PlatePx, ccy = (plateY + cy) * PlatePx, r = radius * PlatePx;
            double dx = x - ccx, dy = y - ccy;
            double d = Math.Sqrt(dx * dx + dy * dy) / r;
            if (d > 2.0) return;
            d += Fbm(x, y, 8, (uint)(3300 + plateX), 3) * 0.5;
            stain = Math.Max(stain, Smooth(1.0, 0.1, d));
            double e = (d - 1.0) / 0.10;
            ring = Math.Max(ring, Math.Exp(-e * e) * Smooth(-0.1, 0.25, Fbm(x, y, 12, (uint)(3350 + plateX), 3)));
        }

        // ── Ruido (mismo hash que Wg3CarpetPattern y Wg3WallpaperPattern) ────

        private static double Hash01(int x, int y, uint seed)
        {
            unchecked
            {
                uint h = (uint)x * 0x8DA6B343u + (uint)y * 0xD8163841u + seed * 0xCB1AB31Fu;
                h ^= h >> 15; h *= 0x2C1B3C6Du;
                h ^= h >> 12; h *= 0x297A2D39u;
                h ^= h >> 15;
                return h / 4294967296.0;
            }
        }

        private static double ValueNoise(int x, int y, int nc, uint seed)
        {
            double fx = (double)x * nc / Size, fy = (double)y * nc / Size;
            int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
            double tx = Smooth(0.0, 1.0, fx - ix), ty = Smooth(0.0, 1.0, fy - iy);

            double c00 = Hash01(ix % nc, iy % nc, seed);
            double c10 = Hash01((ix + 1) % nc, iy % nc, seed);
            double c01 = Hash01(ix % nc, (iy + 1) % nc, seed);
            double c11 = Hash01((ix + 1) % nc, (iy + 1) % nc, seed);

            double a = c00 * (1 - tx) + c10 * tx;
            double b = c01 * (1 - tx) + c11 * tx;
            return a * (1 - ty) + b * ty;
        }

        private static double Fbm(int x, int y, int nc, uint seed, int octaves)
        {
            double sum = 0.0, a = 1.0, total = 0.0;
            for (int o = 0; o < octaves; o++)
            {
                sum += (ValueNoise(x, y, nc << o, seed + (uint)o) - 0.5) * a;
                total += a;
                a *= 0.5;
            }
            return sum / total;
        }

        // ── Utilidades ───────────────────────────────────────────────────────

        private static double Smooth(double a, double b, double x)
        {
            double t = (x - a) / (b - a);
            if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        private static double[] Blur(double[] src)
        {
            var dst = new double[src.Length];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    dst[y * Size + x] =
                        (src[y * Size + x] + src[At(x, y - 1)] + src[At(x, y + 1)]
                         + src[At(x - 1, y)] + src[At(x + 1, y)]) / 5.0;
            return dst;
        }

        private static int At(int x, int y) => ((y & (Size - 1)) * Size) + (x & (Size - 1));

        private static byte ToByte(double v) => v <= 0.0 ? (byte)0 : v >= 255.0 ? (byte)255 : (byte)v;
    }
}
#endif
