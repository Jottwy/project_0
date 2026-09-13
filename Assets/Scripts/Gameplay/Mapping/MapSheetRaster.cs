using System;

namespace BackroomsSurvival.Gameplay.Mapping
{
    /// <summary>
    /// P0.2 de MAPPING-PROTOTYPE — pinta una hoja en un búfer RGBA. C# puro (sólo BCL) para poder
    /// probarlo sin Unity; la vista lo sube a una <c>Texture2D</c> con <c>LoadRawTextureData</c>, igual que
    /// <c>SprayRenderer.Rasterize</c> rasteriza una vez y no por frame.
    /// </summary>
    /// <remarks>
    /// Convenio de ejes: la hoja cubre la zona entera, de 0 a <c>cellsPerChunk</c> celdas; X crece a la derecha
    /// y Z hacia ARRIBA, y la fila 0 del búfer es la de abajo (así indexa sus filas una textura de Unity).
    /// Un trazo es una fila de sellos circulares; el recuerdo viejo sale más fino, más pálido y discontinuo.
    /// </remarks>
    public sealed class MapSheetRaster
    {
        public const int DefaultSize = 512;

        /// <summary>Tramo pintado y hueco de un trazo viejo, en píxeles.</summary>
        public const float DashOnPx = 6f;
        public const float DashOffPx = 5f;

        public readonly int Size;
        /// <summary>RGBA, 4 bytes por píxel, fila 0 abajo.</summary>
        public readonly byte[] Rgba;

        public MapSheetRaster(int size = DefaultSize)
        {
            if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
            Size = size;
            Rgba = new byte[size * size * 4];
        }

        public void Clear(uint paperArgb)
        {
            byte r = (byte)(paperArgb >> 16), g = (byte)(paperArgb >> 8), b = (byte)paperArgb, a = (byte)(paperArgb >> 24);
            for (int i = 0; i < Rgba.Length; i += 4)
            {
                Rgba[i] = r;
                Rgba[i + 1] = g;
                Rgba[i + 2] = b;
                Rgba[i + 3] = a;
            }
        }

        public void DrawSheet(MapSheet sheet, uint paperArgb, int cellsPerChunk)
        {
            Clear(paperArgb);
            foreach (MapSheetLayer layer in sheet.Layers)
                foreach (MapStroke stroke in layer.Strokes)
                    DrawStroke(stroke, layer.Argb, layer.WidthPx, cellsPerChunk);
        }

        public void DrawStroke(MapStroke stroke, uint argb, float widthPx, int cellsPerChunk)
        {
            float[] p = stroke.Points;
            if (p == null || p.Length < 4) return;

            float scale = Size / (float)cellsPerChunk;
            float radius = Math.Max(0.5f, (stroke.Old ? widthPx * 0.8f : widthPx) * 0.5f);
            float alpha = ((argb >> 24) & 255) / 255f * (stroke.Old ? 0.75f : 1f);
            float spacing = Math.Max(0.5f, radius * 0.5f);
            float travelled = 0f;

            // Presión de boli: el grosor y la carga de tinta cambian a lo largo del trazo. La fase sale de los
            // propios puntos, así que el mismo trazo se pinta siempre igual sin guardar una semilla aparte.
            float phase = (p[0] * 12.9898f + p[1] * 78.233f) % 6.2831855f;
            bool blob = !stroke.Old;

            for (int i = 0; i + 3 < p.Length; i += 2)
            {
                float x0 = p[i] * scale, y0 = p[i + 1] * scale;
                float x1 = p[i + 2] * scale, y1 = p[i + 3] * scale;
                float length = (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                int steps = Math.Max(1, (int)Math.Ceiling(length / spacing));

                for (int s = 0; s <= steps; s++)
                {
                    float t = s / (float)steps;
                    float distance = travelled + length * t;
                    if (stroke.Old && distance % (DashOnPx + DashOffPx) > DashOnPx) continue;

                    float pressure = 0.5f + 0.5f * (float)Math.Sin(phase + distance * 0.21f);
                    float drift = 0.5f + 0.5f * (float)Math.Sin(phase * 1.7f + distance * 0.047f);
                    float stampRadius = radius * (0.78f + 0.37f * (pressure * 0.6f + drift * 0.4f));
                    float stampAlpha = alpha * (0.82f + 0.18f * drift);
                    if (blob)
                    {
                        // Donde se apoya el boli al empezar suelta algo más de tinta.
                        stampRadius *= 1.35f;
                        blob = false;
                    }

                    Stamp(x0 + (x1 - x0) * t, y0 + (y1 - y0) * t, stampRadius, argb, stampAlpha);
                }

                travelled += length;
            }
        }

        private void Stamp(float cx, float cy, float radius, uint argb, float alpha)
        {
            int minX = Math.Max(0, (int)Math.Floor(cx - radius));
            int maxX = Math.Min(Size - 1, (int)Math.Ceiling(cx + radius));
            int minY = Math.Max(0, (int)Math.Floor(cy - radius));
            int maxY = Math.Min(Size - 1, (int)Math.Ceiling(cy + radius));
            float r = (argb >> 16) & 255, g = (argb >> 8) & 255, b = argb & 255;
            float radiusSq = radius * radius;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    if (dx * dx + dy * dy > radiusSq) continue;

                    int index = (y * Size + x) * 4;
                    Rgba[index] = (byte)(Rgba[index] + (r - Rgba[index]) * alpha);
                    Rgba[index + 1] = (byte)(Rgba[index + 1] + (g - Rgba[index + 1]) * alpha);
                    Rgba[index + 2] = (byte)(Rgba[index + 2] + (b - Rgba[index + 2]) * alpha);
                    Rgba[index + 3] = 255;
                }
            }
        }
    }
}
