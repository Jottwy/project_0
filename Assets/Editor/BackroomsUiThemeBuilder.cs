#if UNITY_EDITOR
using System;
using System.IO;
using BackroomsSurvival.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Genera los assets de la piel «Bajo el fluorescente» (D12): los sprites 9-slice por escritura
    /// de píxeles (como <c>TextureGenerator</c>), los SDF de TextMeshPro a partir de las TTF de
    /// <c>Assets/Art/Fonts</c> (OFL, descargadas el 2026-09-13) y el asset de tema bajo Resources
    /// que los ata. Menú Backrooms/UI/Build Theme.
    ///
    /// DETERMINISTA E IDEMPOTENTE: relanzarlo sobrescribe los PNG con los mismos bytes y deja el
    /// tema apuntando a lo mismo. Por eso el tema no se rellena a mano — el builder es la fuente.
    ///
    /// LOS SDF SON DINÁMICOS (<see cref="AtlasPopulationMode.Dynamic"/>): el atlas se rellena
    /// con los glifos que la UI use, así que no hay que decidir hoy qué caracteres caben. A
    /// cambio, la TTF de origen viaja al build referenciada desde el asset; son ~110 KB cada una.
    /// </summary>
    public static class BackroomsUiThemeBuilder
    {
        public const string SpriteFolder = "Assets/Art/UI/Backrooms";
        public const string FontFolder = "Assets/Art/Fonts";
        public const string SdfFolder = "Assets/Art/Fonts/SDF";
        public const string ThemeFolder = "Assets/Resources/UI";
        public const string ThemePath = ThemeFolder + "/BackroomsUiTheme.asset";

        // Cuánto muerde el 9-slice en cada sprite: (izq, abajo, dcha, arriba), en píxeles.
        private static readonly Vector4 TapeBorder = new Vector4(6, 4, 6, 4);
        private static readonly Vector4 SlotBorder = new Vector4(12, 12, 12, 12);
        private static readonly Vector4 PaperBorder = new Vector4(8, 8, 8, 8);
        private static readonly Vector4 StrapBorder = new Vector4(0, 4, 0, 4);

        [MenuItem("Backrooms/UI/Build Theme")]
        public static void Build()
        {
            EnsureFolder(SpriteFolder);
            EnsureFolder(SdfFolder);
            EnsureFolder(ThemeFolder);

            var theme = AssetDatabase.LoadAssetAtPath<BackroomsUiTheme>(ThemePath);
            bool created = theme == null;
            if (created)
            {
                theme = ScriptableObject.CreateInstance<BackroomsUiTheme>();
                AssetDatabase.CreateAsset(theme, ThemePath);
            }

            theme.DymoTape = BakeSprite("DymoTape.png", 64, 24, TapeBorder, TapePixels(0x121212, 0x1A1A1A));
            theme.DymoTapeRed = BakeSprite("DymoTapeRed.png", 64, 24, TapeBorder, TapePixels(0x8F231C, 0x9A2C24));
            theme.SunkenSlot = BakeSprite("SunkenSlot.png", 64, 64, SlotBorder, SunkenSlotPixels());
            theme.WarehouseLabel = BakeSprite("WarehouseLabel.png", 64, 64, PaperBorder, PaperPixels());
            theme.LabelHole = BakeSprite("LabelHole.png", 16, 16, Vector4.zero, HolePixels());
            theme.NylonStrap = BakeSprite("NylonStrap.png", 24, 24, StrapBorder, StrapPixels());

            theme.DisplayRegular = BakeFont("BarlowSemiCondensed-Regular");
            theme.Display = BakeFont("BarlowSemiCondensed-SemiBold");
            theme.DisplayBold = BakeFont("BarlowSemiCondensed-Bold");
            theme.Mono = BakeFont("IBMPlexMono-Regular");
            theme.MonoBold = BakeFont("IBMPlexMono-SemiBold");

            EditorUtility.SetDirty(theme);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[BackroomsUiThemeBuilder] Tema {(created ? "creado" : "actualizado")} en {ThemePath}: 6 sprites, 5 SDF.");
        }

        // ─── Sprites ────────────────────────────────────────────────────────

        private static Sprite BakeSprite(string file, int w, int h, Vector4 border, Func<int, int, int, int, Color32> pixel)
        {
            string path = $"{SpriteFolder}/{file}";
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = pixel(x, y, w, h);
            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spriteBorder = border;
            importer.spritePixelsPerUnit = 100f;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.spriteMeshType = SpriteMeshType.FullRect;
            importer.SetTextureSettings(settings);
            importer.SaveAndReimport();

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null) throw new InvalidOperationException($"'{path}' no importó como Sprite");
            return sprite;
        }

        /// Cinta Dymo: fondo con rayas verticales de 1 px cada 3 (el relieve de la cinta) y un
        /// borde de 1 px algo más claro arriba, que es donde la cinta refleja el fluorescente.
        private static Func<int, int, int, int, Color32> TapePixels(int baseRgb, int stripeRgb)
        {
            Color32 b = Rgb(baseRgb), s = Rgb(stripeRgb);
            return (x, y, w, h) =>
            {
                if (y == h - 1) return Lerp(b, Rgb(0xFFFFFF), 0.08f);
                if (y == 0) return Lerp(b, Rgb(0x000000), 0.6f);
                return (x % 3 == 2) ? s : b;
            };
        }

        /// Hueco hundido: rebaje casi negro, borde de 1 px apenas visible, sombra interior que cae
        /// desde arriba (9 px) y un filo claro de 1 px abajo — así se lee como agujero, no como caja.
        private static Func<int, int, int, int, Color32> SunkenSlotPixels()
        {
            Color32 top = Rgb(0x12110B), bottom = Rgb(0x090805), edge = Rgb(0xEBE7D3);
            return (x, y, w, h) =>
            {
                bool border = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                if (border) return Lerp(bottom, edge, 0.09f);
                float t = (float)y / (h - 1);
                var c = Lerp(bottom, top, t);
                int fromTop = h - 2 - y;
                if (fromTop < 9) c = Lerp(c, Rgb(0x000000), 0.8f * (1f - fromTop / 9f));
                if (y == 1) c = Lerp(c, edge, 0.04f);
                return c;
            };
        }

        /// Papel de almacén: degradado vertical de crema a beige con un grano muy leve
        /// determinista (hash de la posición), para que no parezca un rectángulo plano.
        private static Func<int, int, int, int, Color32> PaperPixels()
        {
            Color32 hi = Rgb(0xE9E2C7), lo = Rgb(0xDDD4B4);
            return (x, y, w, h) =>
            {
                var c = Lerp(lo, hi, (float)y / (h - 1));
                int n = (x * 73856093 ^ y * 19349663) & 7;
                return Lerp(c, Rgb(0x000000), n / 7f * 0.03f);
            };
        }

        /// El agujero de la etiqueta: círculo negro con aro de refuerzo beige.
        private static Func<int, int, int, int, Color32> HolePixels()
        {
            return (x, y, w, h) =>
            {
                float dx = x + 0.5f - w / 2f, dy = y + 0.5f - h / 2f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                if (r <= 4.5f) return Rgb(0x15140C);
                if (r <= 6.5f) return Rgb(0xB9AE8A);
                return new Color32(0, 0, 0, 0);
            };
        }

        /// Cincha de nylon: rayas verticales de 3 px, más un canto oscuro arriba y abajo.
        private static Func<int, int, int, int, Color32> StrapPixels()
        {
            Color32 a = Rgb(0x2B2A22), b = Rgb(0x23221B);
            return (x, y, w, h) =>
            {
                if (y < 2 || y >= h - 2) return Lerp(b, Rgb(0x000000), 0.5f);
                return ((x / 3) % 2 == 0) ? a : b;
            };
        }

        // ─── Fuentes ────────────────────────────────────────────────────────

        private static TMP_FontAsset BakeFont(string ttfName)
        {
            string ttfPath = $"{FontFolder}/{ttfName}.ttf";
            string sdfPath = $"{SdfFolder}/{ttfName} SDF.asset";

            var existing = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(sdfPath);
            if (existing != null) return existing;

            var font = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
            if (font == null) throw new FileNotFoundException($"falta la TTF '{ttfPath}' (OFL, ver Assets/Art/Fonts/OFL-*.txt)");

            // Mismos parámetros que el creador de TMP por defecto (90 pt, padding 9, SDFAA, 1024²),
            // en modo dinámico: el atlas se puebla con lo que la UI use.
            var asset = TMP_FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024,
                AtlasPopulationMode.Dynamic, true);
            if (asset == null) throw new InvalidOperationException($"TMP no pudo crear el SDF de '{ttfName}'");

            asset.name = $"{ttfName} SDF";
            AssetDatabase.CreateAsset(asset, sdfPath);
            asset.material.name = $"{asset.name} Material";
            asset.atlasTexture.name = $"{asset.name} Atlas";
            AssetDatabase.AddObjectToAsset(asset.material, asset);
            AssetDatabase.AddObjectToAsset(asset.atlasTexture, asset);
            EditorUtility.SetDirty(asset);
            return asset;
        }

        // ─── Utilidades ─────────────────────────────────────────────────────

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static Color32 Rgb(int rgb) =>
            new Color32((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF), 255);

        private static Color32 Lerp(Color32 a, Color32 b, float t) => Color32.Lerp(a, b, Mathf.Clamp01(t));
    }
}
#endif
