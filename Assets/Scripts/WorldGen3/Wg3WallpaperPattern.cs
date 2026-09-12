#if UNITY_EDITOR
using System;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// El papel pintado de la ESTRUCTURA de WG3, píxel a píxel: albedo, normal y máscara.
    /// </summary>
    /// <remarks>
    /// Sustituye al yeso del pack (albedo blanco liso + normal de bultos a 4 m de repetición) que
    /// vestía <c>Wg3_Structure.mat</c>: bajo la lámpara se leía plano y el «grano» que se veía era
    /// la normal del yeso a luz rasante, no un papel. Joel, 12-09: las texturas de pared se ven
    /// pequeñas y sin volumen.
    ///
    /// # El motivo es el canon de Level 0, al DOBLE de tamaño que <c>WallpaperPattern</c>
    ///
    /// Franjas verticales finas y columnas de chevrons hacia arriba, con una columna de chevrons
    /// pequeños intercalada. La celda mide 25 cm (128 px sobre 2 m de repetición) y no los 15,6 cm
    /// del papel de WG2: a 3 m de distancia el de WG2 se funde en grano y éste todavía se lee como
    /// dibujo. Sigue siendo un susurro en el albedo —una veintena de luma sobre 230— porque quien
    /// hace el trabajo es el RELIEVE: motivo grabado, gotelé fino y una ondulación de 25 cm del
    /// muro bajo el papel, que es lo que da «volumen» con luz rasante de plafón.
    ///
    /// # El color es NEUTRO a propósito: el tono lo pone el papel de cada espacio
    ///
    /// <c>Wg3StyleMaterials</c> separa los papeles (espina, pasillo, oficina…) por TONO sobre el
    /// material base, y <c>_BaseColor</c> del material lleva su crema. Un albedo amarillo aquí se
    /// multiplicaría con los dos y giraría la paleta que Joel validó a ojo. Se autora en crema casi
    /// blanca con la MISMA luminancia media que el yeso al que sustituye (≈230/255), y el amarillo
    /// del canon sale, como hasta ahora, del tinte y de la lámpara a 3 500 K.
    ///
    /// # La máscara: R metal (0), G oclusión, A suavidad — el formato que URP/Lit lee
    ///
    /// Es la misma textura en <c>_MetallicGlossMap</c> y en <c>_OcclusionMap</c>: URP toma R y A
    /// de la primera y G de la segunda. La oclusión sale del propio campo de alturas (hondo =
    /// oscuro), que es lo que hace que el motivo se vea también desde el frente y no sólo a
    /// rasante. La suavidad es baja (papel mate) y sube un poco donde el papel está manchado.
    ///
    /// # Determinismo
    ///
    /// Sin <c>System.Random</c>: todo el ruido sale de un hash entero sobre (x, y, semilla), así que
    /// el PNG es reproducible byte a byte y no depende del orden de recorrido. Periódico por
    /// construcción: el motivo va por módulo de celda y el ruido de valor con la retícula envuelta.
    ///
    /// Sólo en editor: el PNG horneado es lo que viaja en el build, este código no.
    /// </remarks>
    public static class Wg3WallpaperPattern
    {
        /// <summary>Lado en píxeles. 1024 sobre 2 m: 1,95 mm por píxel.</summary>
        public const int Size = 1024;

        /// <summary>Metros de mundo por repetición. Las UV se emiten en metros y
        /// <c>Wg3MeshBuilder.UvPerMetre</c> (0,5) las multiplica una vez: escala 1 = 2 m.</summary>
        public const float RepeatM = 2f;

        /// <summary>Escala de textura en el material para que repita cada <see cref="RepeatM"/>.</summary>
        public const float MaterialScale = 1f;

        // ── Geometría del motivo, en píxeles de textura ──────────────────────
        private const int Cell = 128;                   // 25 cm
        private const double StripeW = 3.0;             // ≈ 6 mm
        private const double Slope = 1.0;               // textura cuadrada sobre mundo cuadrado
        private static readonly double[] StripeX = { 1.0, 69.0 };

        // centro, semiancho, grosor de trazo, periodo vertical, fase
        private const double BigCx = 36.0, BigHw = 26.0, BigT = 4.0, BigP = 64.0, BigPh = 12.0;
        private const double SmlCx = 100.0, SmlHw = 13.0, SmlT = 3.0, SmlP = 32.0, SmlPh = 28.0;

        // ── Paleta (sRGB) ────────────────────────────────────────────────────
        private static readonly double[] Base = { 236.0, 231.0, 214.0 };       // luma 230,6
        private static readonly double[] InkChevron = { 216.0, 210.0, 190.0 }; // −19 de luma
        private static readonly double[] InkStripe = { 211.0, 205.0, 184.0 };  // −24 de luma

        private const double StainAmp = 0.075;      // papel viejo: ±6 % de luma en manchas
        private const double StainShape = 1.6;      // separa manchas de degradado
        private const double NormalStrength = 2.2;  // relieve; el de oficina va de 2,2 a 3,0

        private const double AoFloor = 0.72;        // lo más hondo del relieve
        private const double SmoothnessBase = 0.09; // papel mate
        private const double SmoothnessStain = 0.05; // la mancha brilla un poco más

        /// <summary>
        /// Genera los tres buffers, <c>y = 0</c> en la fila de ABAJO (convención de
        /// <c>Texture2D.SetPixels32</c>). Albedo y normal RGB entrelazado; máscara RGBA.
        /// </summary>
        public static void Build(out byte[] albedoRgb, out byte[] normalRgb, out byte[] maskRgba)
        {
            int n = Size * Size;
            var big = new double[n];
            var sml = new double[n];
            var stp = new double[n];
            var stainField = new double[n];

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    double lx = x % Cell, ly = y % Cell;
                    big[i] = Chevron(lx, ly, BigCx, BigHw, BigT, BigP, BigPh);
                    sml[i] = Chevron(lx, ly, SmlCx, SmlHw, SmlT, SmlP, SmlPh);
                    stp[i] = Stripe(lx);

                    double stain = (ValueNoise(x, y, 6, 6, 9101) - 0.5) * 1.0;
                    stain += (ValueNoise(x, y, 13, 13, 9102) - 0.5) * 0.5;
                    stain += (ValueNoise(x, y, 29, 29, 9103) - 0.5) * 0.25;
                    stain += (ValueNoise(x, y, 47, 47, 9107) - 0.5) * 0.18;
                    stain /= 0.965;
                    stainField[i] = (stain < 0 ? -1.0 : 1.0) * Math.Pow(Math.Abs(stain), StainShape);
                }
            }

            // ── Albedo: base → tinta (sólo valor), mancha, grano de pulpa ────────
            albedoRgb = new byte[n * 3];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    double chev = Math.Max(big[i], sml[i]);
                    double grain = (Hash01(x, y, 9104) - 0.5) * 4.0;
                    for (int k = 0; k < 3; k++)
                    {
                        double c = Base[k] * (1 - chev) + InkChevron[k] * chev;
                        c = c * (1 - stp[i]) + InkStripe[k] * stp[i];
                        double extra = k == 2 ? 0.35 : 0.0; // la mancha pierde algo más de azul
                        c *= 1.0 + StainAmp * stainField[i] * (1.0 + extra);
                        albedoRgb[i * 3 + k] = ToByte(c + grain);
                    }
                }
            }

            // ── Altura: motivo grabado + ondulación del muro + gotelé + poro ─────
            var h = new double[n];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    double v = 1.00 * big[i] + 0.85 * sml[i] + 0.55 * stp[i];
                    // La ondulación es lo que da cuerpo a rasante: un muro de verdad no es plano
                    // a 25 cm, y con un plafón a 2,7 m casi toda la pared se ve a rasante.
                    v += (ValueNoise(x, y, 8, 8, 9105) - 0.5) * 1.6;
                    v += (ValueNoise(x, y, 128, 128, 9108) - 0.5) * 0.35; // gotelé
                    v += (Hash01(x, y, 9106) - 0.5) * 0.18;               // poro
                    h[i] = v;
                }
            }
            h = Blur(Blur(h));

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
                    double ao = AoFloor + (1.0 - AoFloor) * hn;
                    double smooth = SmoothnessBase + SmoothnessStain * Math.Max(0.0, stainField[i]);
                    maskRgba[i * 4 + 0] = 0;                        // metal
                    maskRgba[i * 4 + 1] = ToByte(ao * 255.0);       // oclusión
                    maskRgba[i * 4 + 2] = 0;
                    maskRgba[i * 4 + 3] = ToByte(smooth * 255.0);   // suavidad (lineal)
                }
            }
        }

        // ── Motivo ───────────────────────────────────────────────────────────

        private static double Chevron(double lx, double ly, double cx, double hw,
                                      double thick, double period, double phase)
        {
            double adx = Math.Abs(lx - cx);
            double line = Slope * hw - Slope * adx;
            double t = (ly - phase) % period;
            if (t < 0) t += period;
            double dt = Math.Abs(t - line);
            dt = Math.Min(dt, period - dt);
            double m = 1.0 - Smoothstep(thick * 0.5 - 0.75, thick * 0.5 + 0.75, dt);
            return m * (1.0 - Smoothstep(hw - 1.0, hw, adx));
        }

        private static double Stripe(double lx)
        {
            double m = 0.0;
            foreach (double cx in StripeX)
            {
                double d = (lx - cx + Cell * 0.5) % Cell;
                if (d < 0) d += Cell;
                d = Math.Abs(d - Cell * 0.5);
                m = Math.Max(m, 1.0 - Smoothstep(StripeW * 0.5 - 0.5, StripeW * 0.5 + 0.5, d));
            }
            return m;
        }

        // ── Ruido ────────────────────────────────────────────────────────────

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

        /// <summary>Ruido de valor con la retícula ENVUELTA a nx × ny celdas: periódico por
        /// construcción, sin costura al tilear.</summary>
        private static double ValueNoise(int x, int y, int nx, int ny, uint seed)
        {
            double fx = (double)x * nx / Size, fy = (double)y * ny / Size;
            int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
            double tx = Smoothstep(0.0, 1.0, fx - ix), ty = Smoothstep(0.0, 1.0, fy - iy);

            double c00 = Hash01(ix % nx, iy % ny, seed);
            double c10 = Hash01((ix + 1) % nx, iy % ny, seed);
            double c01 = Hash01(ix % nx, (iy + 1) % ny, seed);
            double c11 = Hash01((ix + 1) % nx, (iy + 1) % ny, seed);

            double a = c00 * (1 - tx) + c10 * tx;
            double b = c01 * (1 - tx) + c11 * tx;
            return a * (1 - ty) + b * ty;
        }

        // ── Utilidades ───────────────────────────────────────────────────────

        private static double Smoothstep(double a, double b, double x)
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
