using System;
using System.Collections.Generic;

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

        /// <summary>Lápiz gris de las flechas de borde.</summary>
        public const uint PencilArgb = 0xFF5F5F66u;

        public void DrawSheet(MapSheet sheet, uint paperArgb, int cellsPerChunk)
        {
            Clear(paperArgb);
            foreach (MapSheetLayer layer in sheet.Layers)
                foreach (MapStroke stroke in layer.Strokes)
                    DrawStroke(stroke, layer.Argb, layer.WidthPx, cellsPerChunk);
            foreach (MapLink link in sheet.Links)
                DrawLink(link, cellsPerChunk);
            foreach (MapMark mark in sheet.Marks)
                DrawMark(mark, cellsPerChunk);
        }

        /// <summary>P0.3 — flecha a lápiz hacia el borde por donde se sale, o peldaños si sube o baja de planta.</summary>
        public void DrawLink(MapLink link, int cellsPerChunk) =>
            DrawLinkAt(link, Size / (float)cellsPerChunk, 0f, 0f, 1.8f);

        private void DrawLinkAt(MapLink link, float scale, float offsetX, float offsetY, float widthPx)
        {
            float x = link.LocalX, z = link.LocalZ;
            if (link.Side == MapLinkSide.StoreyUp || link.Side == MapLinkSide.StoreyDown)
            {
                float s = link.Side == MapLinkSide.StoreyUp ? 1f : -1f;
                DrawStrokeAt(new MapStroke(new[]
                {
                    x - 0.8f, z - 0.6f * s, x - 0.8f, z - 0.2f * s, x - 0.3f, z - 0.2f * s,
                    x - 0.3f, z + 0.2f * s, x + 0.2f, z + 0.2f * s, x + 0.2f, z + 0.6f * s, x + 0.8f, z + 0.6f * s,
                }, false), PencilArgb, widthPx, scale, offsetX, offsetY);
                return;
            }

            float ux = link.Side == MapLinkSide.East ? 1f : link.Side == MapLinkSide.West ? -1f : 0f;
            float uz = link.Side == MapLinkSide.North ? 1f : link.Side == MapLinkSide.South ? -1f : 0f;
            DrawStrokeAt(new MapStroke(new[] { x - ux * 1.6f, z - uz * 1.6f, x, z }, false), PencilArgb, widthPx, scale,
                offsetX, offsetY);
            DrawStrokeAt(new MapStroke(new[]
            {
                x - ux * 0.55f - uz * 0.5f, z - uz * 0.55f + ux * 0.5f, x, z,
                x - ux * 0.55f + uz * 0.5f, z - uz * 0.55f - ux * 0.5f,
            }, false), PencilArgb, widthPx, scale, offsetX, offsetY);
        }

        /// <summary>P0.3 — «estás aquí»: círculo pequeño si te reconociste, grande y discontinuo si sólo te suena.</summary>
        public void DrawMark(MapMark mark, int cellsPerChunk)
        {
            bool unsure = mark.Kind == MapMarkKind.HereUnsure;
            float radius = unsure ? 2.2f : 0.5f;
            const int segments = 20;
            var points = new float[(segments + 1) * 2];
            for (int i = 0; i <= segments; i++)
            {
                double angle = i * Math.PI * 2.0 / segments;
                points[2 * i] = mark.LocalX + radius * (float)Math.Cos(angle);
                points[2 * i + 1] = mark.LocalZ + radius * (float)Math.Sin(angle);
            }

            DrawStroke(new MapStroke(points, unsure), mark.Argb, 2.6f, cellsPerChunk);
        }

        public void DrawStroke(MapStroke stroke, uint argb, float widthPx, int cellsPerChunk) =>
            DrawStrokeAt(stroke, argb, widthPx, Size / (float)cellsPerChunk, 0f, 0f);

        /// <summary>Papel de las zonas sin mapear del plano.</summary>
        public const uint BlankArgb = 0xFFD8D4C8u;
        private const uint HatchArgb = 0xFFC2BDAFu;
        private const uint TileBorderArgb = 0xFFB9B4A6u;
        private const uint BaseBorderArgb = 0xFFA03028u;
        /// <summary>Cuánto se lava hacia el papel un borrador en el plano: fantasma, no tinta.</summary>
        public const float DraftWash = 0.6f;

        /// <summary>
        /// P0.4 — el plano maestro de una planta: fondo «sin mapear» con trama, y por cada zona colocada una loseta de
        /// <paramref name="tilePx"/> con su limpia opaca o, si no la hay, su borrador lavado. Centrado en
        /// (<paramref name="centreChunkX"/>, <paramref name="centreChunkZ"/>), en chunks.
        /// </summary>
        public void DrawAtlas(MapAtlas atlas, IReadOnlyList<MapZone> placed, IReadOnlyList<MapSheet> drafts, int storey,
            float centreChunkX, float centreChunkZ, int tilePx, uint paperArgb, int cellsPerChunk)
        {
            Clear(BlankArgb);
            for (int y = 0; y < Size; y++)
                for (int x = (y % 12 + 12) % 12; x < Size; x += 12)
                    Stamp(x + 0.5f, y + 0.5f, 0.6f, HatchArgb, 1f);

            float scale = tilePx / (float)cellsPerChunk;
            float widthScale = tilePx / (float)DefaultSize;
            foreach (MapZone zone in placed)
            {
                if (zone.Storey != storey) continue;
                int x0 = (int)Math.Round(Size * 0.5f + (zone.ChunkX - centreChunkX) * tilePx);
                int y0 = (int)Math.Round(Size * 0.5f + (zone.ChunkZ - centreChunkZ) * tilePx);
                if (x0 >= Size || y0 >= Size || x0 + tilePx <= 0 || y0 + tilePx <= 0) continue;

                FillRect(x0, y0, x0 + tilePx, y0 + tilePx, paperArgb, 1f);
                MapSheet sheet = atlas.CleanOf(zone);
                bool clean = sheet != null;
                if (!clean) sheet = LastDraftOf(drafts, zone);
                if (sheet != null)
                {
                    foreach (MapSheetLayer layer in sheet.Layers)
                        foreach (MapStroke stroke in layer.Strokes)
                            DrawStrokeAt(stroke, layer.Argb, Math.Max(1.5f, layer.WidthPx * widthScale), scale, x0, y0);
                    foreach (MapLink link in sheet.Links)
                        DrawLinkAt(link, scale, x0, y0, Math.Max(1.5f, 1.8f * widthScale));
                    if (!clean) FillRect(x0, y0, x0 + tilePx, y0 + tilePx, paperArgb, DraftWash);
                }

                bool isBase = atlas.HasBase && atlas.Base.Equals(zone);
                uint border = isBase ? BaseBorderArgb : TileBorderArgb;
                int thick = isBase ? 2 : 1;
                FillRect(x0, y0, x0 + tilePx, y0 + thick, border, 1f);
                FillRect(x0, y0 + tilePx - thick, x0 + tilePx, y0 + tilePx, border, 1f);
                FillRect(x0, y0, x0 + thick, y0 + tilePx, border, 1f);
                FillRect(x0 + tilePx - thick, y0, x0 + tilePx, y0 + tilePx, border, 1f);
            }
        }

        private static MapSheet LastDraftOf(IReadOnlyList<MapSheet> drafts, MapZone zone)
        {
            if (drafts == null) return null;
            for (int i = drafts.Count - 1; i >= 0; i--)
                if (drafts[i].HasZone && drafts[i].Zone.Equals(zone)) return drafts[i];
            return null;
        }

        private void FillRect(int x0, int y0, int x1, int y1, uint argb, float alpha)
        {
            x0 = Math.Max(0, x0);
            y0 = Math.Max(0, y0);
            x1 = Math.Min(Size, x1);
            y1 = Math.Min(Size, y1);
            float r = (argb >> 16) & 255, g = (argb >> 8) & 255, b = argb & 255;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int index = (y * Size + x) * 4;
                    Rgba[index] = (byte)(Rgba[index] + (r - Rgba[index]) * alpha);
                    Rgba[index + 1] = (byte)(Rgba[index + 1] + (g - Rgba[index + 1]) * alpha);
                    Rgba[index + 2] = (byte)(Rgba[index + 2] + (b - Rgba[index + 2]) * alpha);
                    Rgba[index + 3] = 255;
                }
            }
        }

        private void DrawStrokeAt(MapStroke stroke, uint argb, float widthPx, float scale, float offsetX, float offsetY)
        {
            float[] p = stroke.Points;
            if (p == null || p.Length < 4) return;

            float radius = Math.Max(0.5f, (stroke.Old ? widthPx * 0.8f : widthPx) * 0.5f);
            float alpha = ((argb >> 24) & 255) / 255f * (stroke.Old ? 0.75f : 1f);
            float spacing = Math.Max(0.5f, radius * 0.5f);
            float travelled = 0f;

            // Presión de boli: el grosor y la carga de tinta cambian a lo largo del trazo. La fase sale de los
            // propios puntos, así que el mismo trazo se pinta siempre igual sin guardar una semilla aparte.
            float phase = (p[0] * 12.9898f + p[1] * 78.233f) % 6.2831855f;
            bool blob = !stroke.Old && !stroke.Steady;

            for (int i = 0; i + 3 < p.Length; i += 2)
            {
                float x0 = offsetX + p[i] * scale, y0 = offsetY + p[i + 1] * scale;
                float x1 = offsetX + p[i + 2] * scale, y1 = offsetY + p[i + 3] * scale;
                float length = (float)Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
                int steps = Math.Max(1, (int)Math.Ceiling(length / spacing));

                for (int s = 0; s <= steps; s++)
                {
                    float t = s / (float)steps;
                    float distance = travelled + length * t;
                    if (stroke.Old && distance % (DashOnPx + DashOffPx) > DashOnPx) continue;

                    float pressure = 0.5f + 0.5f * (float)Math.Sin(phase + distance * 0.21f);
                    float drift = 0.5f + 0.5f * (float)Math.Sin(phase * 1.7f + distance * 0.047f);
                    // Pasado a limpio: rotulador fino, sin presión.
                    float stampRadius = stroke.Steady ? radius : radius * (0.78f + 0.37f * (pressure * 0.6f + drift * 0.4f));
                    float stampAlpha = stroke.Steady ? alpha : alpha * (0.82f + 0.18f * drift);
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
