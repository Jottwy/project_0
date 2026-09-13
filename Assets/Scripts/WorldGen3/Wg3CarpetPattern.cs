#if UNITY_EDITOR
using System;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// La MOQUETA Backrooms del suelo de WG3, píxel a píxel: albedo, normal y máscara.
    /// </summary>
    /// <remarks>
    /// Sustituye al terrazo del pack AK que vestía <c>Wg3_Floor.mat</c> desde <c>5d281833</c>: un
    /// suelo pétreo con árido negro, teñido de oliva, que no era moqueta. Joel, 13-09: «la clásica
    /// moqueta amarilla/beige manchada y húmeda».
    ///
    /// # Qué la hace Backrooms, por capas
    ///
    /// 1. **Pelo de bucle**: grano de hash difuminado + filas de 8 px y columnas de 4 px (4 y 2 mm)
    ///    con un bamboleo lento. Los dos periodos DIVIDEN el lado: un periodo de 5 o 6 px dejaba
    ///    una costura medida al tilear.
    /// 2. **Peinado**: ruido lento de ±5 % de valor, el brillo que cambia según hacia dónde está
    ///    pisado el pelo.
    /// 3. **Moteado**: parches irregulares más oscuros. Es la capa que dice «vieja y húmeda».
    /// 4. **Manchas con cerco de marea**: siete, de tamaños muy repartidos, con el dominio
    ///    deformado por ruido para que ninguna sea un círculo, interior irregular y un cerco tenue
    ///    y ROTO. Un cerco continuo se leía como un aro de café dibujado.
    /// 5. **Zonas mojadas**: grandes y de borde blando; oscurecen, aplastan el pelo (menos relieve
    ///    y menos oclusión) y SUBEN la suavidad, que es lo único que hace que se lea «mojado» y no
    ///    «sucio» con la lámpara encima.
    ///
    /// # El color lo pone el material, como en el papel de pared
    ///
    /// El albedo es crema cálido y claro; el ocre sale de <c>_BaseColor</c>, que el horneador
    /// calcula para que la luminancia efectiva sea la del terrazo teñido al que sustituye (Y lineal
    /// ≈ 0,305). Así la luz que Joel validó devuelve lo mismo, y los tintes por papel de
    /// <c>Wg3StyleMaterials</c> siguen multiplicando encima sin cambiar de orden.
    ///
    /// # La máscara: R metal (0), G oclusión, A suavidad — lo que URP/Lit lee
    ///
    /// Misma textura en <c>_MetallicGlossMap</c> y <c>_OcclusionMap</c>, igual que el papel.
    ///
    /// # Determinismo y periodicidad
    ///
    /// Sin <c>System.Random</c>: todo sale de un hash entero sobre (x, y, semilla). El ruido de valor
    /// envuelve su retícula y las distancias a las manchas se toman con envoltura toroidal, así que
    /// la textura tilea sin costura por construcción.
    ///
    /// Sólo en editor: el PNG horneado es lo que viaja en el build.
    /// </remarks>
    public static class Wg3CarpetPattern
    {
        /// <summary>Lado en píxeles. 2048 sobre 4 m: 1,95 mm por píxel, lo mismo que el papel. El
        /// suelo es lo que más cerca se mira de todo el mundo: a 1024 el pelo no tenía píxeles.</summary>
        public const int Size = 2048;

        /// <summary>Metros de mundo por repetición. Las UV se emiten en metros y
        /// <c>Wg3MeshBuilder.UvPerMetre</c> (0,5) las multiplica: escala 0,5 = 4 m.</summary>
        public const float RepeatM = 4f;

        /// <summary>Escala de textura en el material para que repita cada <see cref="RepeatM"/>.
        /// Es la que ya llevaba el terrazo.</summary>
        public const float MaterialScale = 0.5f;

        /// <summary>El ocre de moqueta húmeda (sRGB) que tiene que devolver el material con la
        /// textura ENCIMA. Su Y lineal (0,307) es la del terrazo teñido que sustituye (0,305).</summary>
        public static readonly byte[] TargetSrgb = { 172, 148, 90 };

        // ── Paleta y pesos ───────────────────────────────────────────────────
        private static readonly double[] Base = { 226.0, 218.0, 198.0 };

        private const int RowPeriod = 8;   // 4 mm; divide a Size
        private const int ColPeriod = 4;   // 2 mm; divide a Size
        private const int StainCount = 7;
        private const double NormalStrength = 1.8;

        private const double AoFloor = 0.74;
        private const double AoWet = 0.06;
        private const double SmoothnessBase = 0.05;  // pelo seco: mate
        private const double SmoothnessWet = 0.30;   // lo mojado brilla
        private const double SmoothnessStain = 0.04;

        /// <summary>
        /// Genera los tres buffers, <c>y = 0</c> en la fila de ABAJO (convención de
        /// <c>Texture2D.SetPixels32</c>). Albedo y normal RGB entrelazado; máscara RGBA.
        /// </summary>
        public static void Build(out byte[] albedoRgb, out byte[] normalRgb, out byte[] maskRgba)
        {
            int n = Size * Size;

            // El grano del pelo necesita a sus vecinos: se difumina antes de usarlo.
            var fib = new double[n];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    fib[y * Size + x] = Hash01(x, y, 7001);
            fib = Blur(fib);

            // Centros, radios y fuerza de las manchas: fijos por semilla, no por ejecución.
            var cx = new double[StainCount];
            var cy = new double[StainCount];
            var rad = new double[StainCount];
            var amp = new double[StainCount];
            for (int k = 0; k < StainCount; k++)
            {
                cx[k] = Hash01(k, 1, 7050) * Size;
                cy[k] = Hash01(k, 2, 7050) * Size;
                double t = Hash01(k, 3, 7050);
                rad[k] = (0.05 + 0.16 * t * t) * Size;
                amp[k] = 0.45 + 0.55 * Hash01(k, 4, 7050);
            }

            albedoRgb = new byte[n * 3];
            var h = new double[n];
            var wetField = new float[n];
            var stainField = new float[n];

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;

                    // 1. Pelo de bucle.
                    double rowWob = (ValueNoise(x, y, 64, 7002) - 0.5) * 3.0;
                    double colWob = (ValueNoise(x, y, 64, 7003) - 0.5) * 3.0;
                    double rows = 0.5 + 0.5 * Math.Cos(2.0 * Math.PI * (y + rowWob) / RowPeriod);
                    double cols = (0.5 + 0.5 * Math.Cos(2.0 * Math.PI * (x + colWob) / ColPeriod)) * 0.6 + 0.2;
                    double tuft = 0.55 * fib[i] + 0.25 * rows * cols + 0.20 * Hash01(x / 2, y / 2, 7004);

                    // 2. Peinado y suciedad fina.
                    double brush = (ValueNoise(x, y, 5, 7010) - 0.5)
                                 + (ValueNoise(x, y, 11, 7011) - 0.5) * 0.5
                                 + (ValueNoise(x, y, 23, 7012) - 0.5) * 0.25;
                    double grime = (ValueNoise(x, y, 37, 7020) - 0.5)
                                 + (ValueNoise(x, y, 97, 7021) - 0.5) * 0.5;

                    // 5. Mojado.
                    double wetN = ValueNoise(x, y, 3, 7030) * 0.6
                                + ValueNoise(x, y, 7, 7031) * 0.3
                                + ValueNoise(x, y, 17, 7032) * 0.1;
                    double wet = Smooth(0.58, 0.74, wetN);

                    // 3. Moteado.
                    double mott = Smooth(0.02, 0.22, Fbm(x, y, 4, 7060));

                    // 4. Manchas: dominio deformado, interior irregular y cerco roto.
                    double wx = Fbm(x, y, 6, 7070) * 0.9;
                    double wy = Fbm(x, y, 6, 7080) * 0.9;
                    double inner = Smooth(-0.15, 0.2, Fbm(x, y, 9, 7090));
                    double broken = Smooth(-0.05, 0.25, Fbm(x, y, 13, 7100));
                    double stain = 0.0, ring = 0.0;
                    for (int k = 0; k < StainCount; k++)
                    {
                        double dx = Wrap(x - cx[k]) + wx * rad[k];
                        double dy = Wrap(y - cy[k]) + wy * rad[k];
                        double d = Math.Sqrt(dx * dx + dy * dy) / rad[k];
                        double s = Smooth(1.05, 0.2, d) * (0.35 + 0.65 * inner) * amp[k];
                        if (s > stain) stain = s;
                        double e = (d - 1.0) / 0.09;
                        double r = Math.Exp(-e * e) * broken;
                        if (r > ring) ring = r;
                    }

                    double v = 1.0 + 0.10 * (tuft - 0.5) * 2.0 + 0.05 * brush + 0.05 * grime;
                    v *= (1 - 0.16 * mott) * (1 - 0.20 * stain) * (1 - 0.06 * ring) * (1 - 0.28 * wet);
                    // La mancha y el agua son PARDAS: pierden azul, y algo de verde. El cerco pierde
                    // poco: con 0,08/0,12 salía ROJIZO y se leía como pelos sobre la moqueta.
                    albedoRgb[i * 3 + 0] = ToByte(Base[0] * v);
                    albedoRgb[i * 3 + 1] = ToByte(Base[1] * v * (1 - 0.05 * stain - 0.03 * ring));
                    albedoRgb[i * 3 + 2] = ToByte(Base[2] * v * (1 - 0.08 * mott - 0.16 * stain - 0.05 * ring - 0.12 * wet));

                    // Lo mojado y lo manchado aplasta el pelo.
                    h[i] = 0.9 * tuft + 0.25 * brush - 0.45 * wet - 0.15 * stain;
                    wetField[i] = (float)wet;
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
                    // n = normalize(−dh/du, −dh/dv, 1): espacio tangente con verde hacia ARRIBA.
                    double nx = -dhx * NormalStrength, ny = -dhy * NormalStrength, nz = 1.0;
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    normalRgb[i * 3 + 0] = ToByte((nx / len * 0.5 + 0.5) * 255.0);
                    normalRgb[i * 3 + 1] = ToByte((ny / len * 0.5 + 0.5) * 255.0);
                    normalRgb[i * 3 + 2] = ToByte((nz / len * 0.5 + 0.5) * 255.0);

                    double hn = (h[i] - hMin) / hSpan;
                    double ao = AoFloor + (1.0 - AoFloor) * hn - AoWet * wetField[i];
                    double smooth = SmoothnessBase + SmoothnessWet * wetField[i] + SmoothnessStain * stainField[i];
                    maskRgba[i * 4 + 0] = 0;                        // metal
                    maskRgba[i * 4 + 1] = ToByte(ao * 255.0);       // oclusión
                    maskRgba[i * 4 + 2] = 0;
                    maskRgba[i * 4 + 3] = ToByte(smooth * 255.0);   // suavidad (lineal)
                }
            }
        }

        // ── Ruido ────────────────────────────────────────────────────────────

        /// <summary>El mismo hash que <see cref="Wg3WallpaperPattern"/>.</summary>
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

        /// <summary>Ruido de valor con la retícula ENVUELTA a nc × nc celdas: periódico por
        /// construcción.</summary>
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

        /// <summary>Cinco octavas centradas en 0, normalizadas por la suma de amplitudes.</summary>
        private static double Fbm(int x, int y, int nc, uint seed)
        {
            double sum = 0.0, a = 1.0, total = 0.0;
            for (int o = 0; o < 5; o++)
            {
                sum += (ValueNoise(x, y, nc << o, seed + (uint)o) - 0.5) * a;
                total += a;
                a *= 0.5;
            }
            return sum / total;
        }

        // ── Utilidades ───────────────────────────────────────────────────────

        /// <summary>Smoothstep que admite <c>a &gt; b</c> (rampa de bajada).</summary>
        private static double Smooth(double a, double b, double x)
        {
            double t = (x - a) / (b - a);
            if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
            return t * t * (3.0 - 2.0 * t);
        }

        /// <summary>Distancia con envoltura toroidal en (−Size/2, Size/2].</summary>
        private static double Wrap(double d)
        {
            double m = (d + Size * 0.5) % Size;
            if (m < 0) m += Size;
            return m - Size * 0.5;
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
