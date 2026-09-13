#if UNITY_EDITOR
using System;

namespace BackroomsSurvival.WorldGen3
{
    /// <summary>
    /// A5 (13-09) — la máscara URP/Lit del REMATE de WG3 (rodapié, marcos, submalla de decoración),
    /// sacada del normal del yeso que ya lleva <c>Wg3_Trim.mat</c>.
    /// </summary>
    /// <remarks>
    /// El remate no tiene campo de alturas propio: es el <c>Plaster</c> del pack AK. La oclusión se
    /// saca de la INCLINACIÓN del normal —lo que se aparta del plano es poro o bulto, y se ocluye—, y
    /// la suavidad es la constante que el material traía (0,22), ahora en el alfa. Lo que gana: el
    /// mismo juego de palabras clave que suelo, papel y techo (un lote del SRP Batcher) y un yeso con
    /// algo de cavidad. Lo que no toca: tinte ni escala.
    /// </remarks>
    public static class Wg3TrimMaskPattern
    {
        /// <summary>La suavidad que <c>Wg3_Trim.mat</c> tenía en <c>_Smoothness</c>.</summary>
        public const double Smoothness = 0.22;

        /// <summary>Lo más ocluido: un normal a 45° o más.</summary>
        public const double AoFloor = 0.80;

        /// <summary>
        /// Máscara RGBA (R metal 0, G oclusión, A suavidad) desde un normal empaquetado RGB
        /// (<c>n = rgb/255·2 − 1</c>, espacio tangente), en el mismo orden de píxeles.
        /// </summary>
        public static byte[] FromNormal(byte[] normalRgb)
        {
            if (normalRgb == null || normalRgb.Length % 3 != 0)
                throw new ArgumentException("normal RGB entrelazado", nameof(normalRgb));

            int n = normalRgb.Length / 3;
            var mask = new byte[n * 4];
            byte a = ToByte(Smoothness * 255.0);
            for (int i = 0; i < n; i++)
            {
                double nx = normalRgb[i * 3] / 255.0 * 2.0 - 1.0;
                double ny = normalRgb[i * 3 + 1] / 255.0 * 2.0 - 1.0;
                // El azul de un normal comprimido no es fiable: se reconstruye z desde x e y.
                double nz = Math.Sqrt(Math.Max(0.0, 1.0 - nx * nx - ny * ny));
                // cos 45° = 0,707 → suelo; plano (z = 1) → sin oclusión.
                double t = Math.Max(0.0, Math.Min(1.0, (nz - 0.7071) / (1.0 - 0.7071)));
                double ao = AoFloor + (1.0 - AoFloor) * t;
                mask[i * 4 + 0] = 0;
                mask[i * 4 + 1] = ToByte(ao * 255.0);
                mask[i * 4 + 2] = 0;
                mask[i * 4 + 3] = a;
            }
            return mask;
        }

        private static byte ToByte(double v) => v <= 0.0 ? (byte)0 : v >= 255.0 ? (byte)255 : (byte)Math.Round(v);
    }
}
#endif
