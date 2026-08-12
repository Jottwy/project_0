using System;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// El papel pintado de PARED de Level 0, píxel a píxel. Devuelve dos buffers RGB
    /// crudos —albedo y mapa de normales— y NO toca ningún tipo de Unity: es lo que
    /// permite compilar este archivo suelto con csc y comparar su salida byte a byte
    /// con el prototipo antes de meter los PNG en el proyecto.
    /// <see cref="TextureGenerator"/> es quien los convierte en textura y los guarda.
    ///
    /// MOTIVO (canon de Level 0, no el damasco del Manila Room): franjas verticales
    /// finas alternando con columnas de chevrons apuntando hacia arriba, con una
    /// columna de chevrons más pequeños intercalada. Repetición densa y regular.
    ///
    /// CÓMO SE LEE: SOLO por diferencia de valor sobre el mismo tono base — 6 de 255
    /// de luminancia en el chevron, 7 en la franja. A 5 m es un susurro y a 10 m no
    /// está. Quien tenga que hacer el trabajo visual es el RELIEVE (el mapa de
    /// normales), que en luz rasante saca el grabado del papel; el albedo casi no
    /// participa. Un motivo legible de lejos es el fallo, no el objetivo.
    ///
    /// COLOR: crema AMARILLO pálido — saturación 0.28, luminancia 198/255, tono 52°.
    /// Aislada, la textura se lee amarilla; solo algo más apagada que la fotografía
    /// de referencia, que está tomada bajo luz cálida.
    ///
    /// CORRECCIÓN (2026-08-12) de la iteración anterior, que se fue al extremo
    /// contrario. Aquel paso leyó el canon como "el color real es beige amarronado y
    /// el amarillo lo pone la iluminación" y bajó la saturación a 0.14; para
    /// compensar, la lámpara subió a un amarillo casi neón, y el resultado fue el
    /// reparto invertido: emisores amarillo saturado sobre superficies neutras, o
    /// sea techo verde oliva y paredes marrones. En el canon es al revés — el
    /// AMARILLO VIVE EN LA SUPERFICIE y las lámparas son casi blancas. El papel ES
    /// amarillo pálido; la luz solo lo intensifica.
    ///
    /// ESCALA: el tile son 1024 px con tiling 2×2 sobre el panel de 5 m × 4 m, así
    /// que cubre 2.5 m × 2.0 m — 2.44 mm por píxel en horizontal, 1.95 en vertical.
    /// La celda de 64 × 64 px es un motivo de 15.6 cm × 12.5 cm y el chevron grande
    /// mide 6.3 cm de ancho: papel pintado real, no papel pintado ampliado. Las
    /// pendientes van a 1.25 px/px para compensar la anisotropía y salir a ~45°
    /// físicos.
    ///
    /// DETERMINISMO: sin <c>System.Random</c>. Todo el ruido sale de un hash entero
    /// de 32 bits sobre (x, y, semilla), así que el resultado no depende del orden en
    /// que se recorren los píxeles y es reproducible fuera de Unity.
    /// </summary>
    internal static class WallpaperPattern
    {
        public const int Size = 1024;

        // ── Geometría del motivo, en píxeles de textura ──────────────────────
        private const int CellW = 64, CellH = 64;      // 15.6 cm × 12.5 cm
        private const double StripeW = 2.0;            // 5 mm
        private const double Slope = 1.25;             // ≈45° físicos tras la anisotropía
        private static readonly double[] StripeX = { 0.5, 34.5 };

        // centro, semiancho, grosor de trazo, periodo vertical, fase
        private const double BigCx = 18.0, BigHw = 13.0, BigT = 2.6, BigP = 32.0, BigPh = 6.0;
        private const double SmlCx = 50.0, SmlHw = 6.5, SmlT = 2.0, SmlP = 16.0, SmlPh = 14.0;

        // ── Paleta ───────────────────────────────────────────────────────────
        private static readonly double[] Base = { 208.0, 200.0, 150.0 };  // luma 198.1, sat 0.279
        private static readonly double[] InkChevron = { 203.0, 194.0, 141.0 };  // −6.0 de luma
        private static readonly double[] InkStripe = { 202.0, 193.0, 139.0 };  // −7.1 de luma

        private const double StainAmp = 0.040;     // mancha de papel viejo: ±3 % de luma
        private const double NormalStrength = 0.6; // relieve: 11.6° de inclinación máxima

        /// <summary>
        /// Genera los dos buffers, RGB entrelazado, <c>y = 0</c> en la fila de ABAJO
        /// (la convención de <c>Texture2D.SetPixels32</c>).
        /// </summary>
        public static void Build(out byte[] albedoRgb, out byte[] normalRgb)
        {
            int n = Size * Size;
            var big = new double[n];
            var sml = new double[n];
            var stp = new double[n];

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    double lx = x % CellW, ly = y % CellH;
                    big[i] = Chevron(lx, ly, BigCx, BigHw, BigT, BigP, BigPh);
                    sml[i] = Chevron(lx, ly, SmlCx, SmlHw, SmlT, SmlP, SmlPh);
                    stp[i] = Stripe(lx);
                }
            }

            // ── Albedo: base → tinta (solo valor), mancha de fBm, grano de pulpa ──
            albedoRgb = new byte[n * 3];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    double chev = Math.Max(big[i], sml[i]);

                    double stain = (ValueNoise(x, y, 6, 3, 7101) - 0.5) * 1.0;
                    stain += (ValueNoise(x, y, 13, 7, 7102) - 0.5) * 0.5;
                    stain += (ValueNoise(x, y, 29, 17, 7103) - 0.5) * 0.25;
                    stain /= 1.75;

                    double grain = (Hash01(x, y, 7104) - 0.5) * 2.0 * 2.0;

                    for (int k = 0; k < 3; k++)
                    {
                        double c = Base[k] * (1 - chev) + InkChevron[k] * chev;
                        c = c * (1 - stp[i]) + InkStripe[k] * stp[i];
                        // el papel viejo pierde algo más de azul donde está manchado
                        double extra = k == 2 ? 0.35 : 0.0;
                        c *= 1.0 + StainAmp * stain * (1.0 + extra);
                        albedoRgb[i * 3 + k] = ToByte(c + grain);
                    }
                }
            }

            // ── Altura → normal ──────────────────────────────────────────────
            // El grabado del papel: el motivo en relieve más el poro de la pulpa.
            var h = new double[n];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    double v = 1.00 * big[i] + 0.85 * sml[i] + 0.55 * stp[i];
                    v += (ValueNoise(x, y, 256, 256, 7105) - 0.5) * 0.22;
                    v += (Hash01(x, y, 7106) - 0.5) * 0.16;
                    h[i] = v;
                }
            }
            h = Blur(Blur(h));

            normalRgb = new byte[n * 3];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    double dhx = (h[At(x + 1, y)] - h[At(x - 1, y)]) * 0.5;
                    double dhy = (h[At(x, y + 1)] - h[At(x, y - 1)]) * 0.5;

                    // n = normalize(−dh/du, −dh/dv, 1) — tangent space con verde
                    // hacia ARRIBA, que es lo que espera Unity.
                    double nx = -dhx * NormalStrength, ny = -dhy * NormalStrength, nz = 1.0;
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    normalRgb[i * 3 + 0] = ToByte((nx / len * 0.5 + 0.5) * 255.0);
                    normalRgb[i * 3 + 1] = ToByte((ny / len * 0.5 + 0.5) * 255.0);
                    normalRgb[i * 3 + 2] = ToByte((nz / len * 0.5 + 0.5) * 255.0);
                }
            }
        }

        // ── Motivo ───────────────────────────────────────────────────────────

        /// <summary>Un brazo de chevron apuntando hacia arriba: el vértice está en lo
        /// alto y los brazos bajan hasta <paramref name="hw"/>. Se repite cada
        /// <paramref name="period"/> píxeles, con borde suave de ~1.5 px para que el
        /// mip no lo convierta en escalera.</summary>
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
                double d = (lx - cx + CellW * 0.5) % CellW;
                if (d < 0) d += CellW;
                d = Math.Abs(d - CellW * 0.5);
                m = Math.Max(m, 1.0 - Smoothstep(StripeW * 0.5 - 0.5, StripeW * 0.5 + 0.5, d));
            }
            return m;
        }

        // ── Ruido ────────────────────────────────────────────────────────────

        /// <summary>Hash entero de 32 bits → [0,1). Sustituye a <c>System.Random</c>
        /// para que el valor de un píxel no dependa de cuántos se hayan pedido antes.</summary>
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

        /// <summary>Ruido de valor con la retícula ENVUELTA a <paramref name="nx"/> ×
        /// <paramref name="ny"/> celdas: periódico por construcción, así que la
        /// mancha no deja costura al tilear.</summary>
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

        /// <summary>Media de 5 muestras con envoltura — suaviza el escalón del motivo
        /// para que la normal salga en rampa y no en pared vertical.</summary>
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
