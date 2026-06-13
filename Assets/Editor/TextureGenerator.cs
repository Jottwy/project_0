#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Generates the three Backrooms surface textures procedurally (no external
    /// deps — pure Texture2D pixel writes), saves them as PNG assets under
    /// Resources/Textures, and wires them into the existing grid materials.
    /// Deterministic: a fixed RNG seed per texture, so re-running is idempotent.
    /// Menu: Backrooms/Generate Textures.
    /// </summary>
    public static class TextureGenerator
    {
        private const string ResourcesRoot = "Assets/Resources";
        private const string TextureFolder = "Assets/Resources/Textures";
        private const string MaterialFolder = "Assets/Resources/GridMaterials";
        private const int Size = 512;

        [MenuItem("Backrooms/Generate Textures")]
        public static void GenerateTextures()
        {
            EnsureFolders();

            var wallpaper = Bake(BuildWallpaperYellow(), "WallpaperYellow.png");
            var carpet    = Bake(BuildCarpetBeige(),     "CarpetBeige.png");
            var ceiling   = Bake(BuildCeilingTiles(),    "CeilingTiles.png");

            ApplyTexture($"{MaterialFolder}/GridWall.mat",    wallpaper, 2f, 2f);
            ApplyTexture($"{MaterialFolder}/GridFloor.mat",   carpet,    4f, 4f);
            ApplyTexture($"{MaterialFolder}/GridCeiling.mat", ceiling,   2f, 2f);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[TextureGenerator] Generated WallpaperYellow / CarpetBeige / " +
                      "CeilingTiles and applied them to GridWall/GridFloor/GridCeiling.");
        }

        // ─── Texture 1: dirty-yellow wallpaper with an argyle diamond pattern ──
        private static Color32[] BuildWallpaperYellow()
        {
            var rng = new System.Random(1001);
            var bg     = new Color32(210, 195, 140, 255);
            var fill   = new Color32(185, 170, 115, 255);
            var border = new Color32(165, 150, 95, 255);

            // Diamond cell: 32 px wide, 48 px tall. A pixel belongs to the diamond
            // when |dx|/16 + |dy|/24 <= 1; scaled to integers: |dx|*24 + |dy|*16.
            // Edge band ≈ 2 px → D within ~58 of the 384 boundary (|grad| ≈ 28.8).
            const int cellW = 32, cellH = 48, halfW = 16, halfH = 24;
            const int edge = halfW * halfH;      // 384 — diamond boundary
            const int borderBand = 58;           // ~2 px constant-width rim

            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    int lx = x % cellW, ly = y % cellH;
                    int d = Mathf.Abs(lx - halfW) * halfH + Mathf.Abs(ly - halfH) * halfW;

                    Color32 c = d > edge ? bg : (d >= edge - borderBand ? border : fill);
                    px[y * Size + x] = AddNoise(c, rng, 8);
                }
            }
            return px;
        }

        // ─── Texture 2: brown carpet, woven diagonal fibres ──────────────────
        private static Color32[] BuildCarpetBeige()
        {
            var rng = new System.Random(2002);
            var bg    = new Color32(120, 95, 65, 255);
            var fibre = new Color32(140, 112, 78, 255);

            // Thin diagonal fibres every 3 px; the diagonal flips direction every
            // 6 px band, giving an interlocked weave.
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                bool flip = (y / 6) % 2 == 1;
                for (int x = 0; x < Size; x++)
                {
                    int diag = flip ? (x - y) : (x + y);
                    int m = ((diag % 3) + 3) % 3;
                    Color32 c = m == 0 ? fibre : bg;
                    px[y * Size + x] = AddNoise(c, rng, 12);
                }
            }
            return px;
        }

        // ─── Texture 3: dirty-white drop-ceiling tiles ───────────────────────
        private static Color32[] BuildCeilingTiles()
        {
            var rng = new System.Random(3003);
            var bg     = new Color32(225, 220, 205, 255);
            var border = new Color32(180, 175, 160, 255);
            var dot    = new Color32(120, 115, 100, 255);

            // 128 px plates (4 across). 3 px grid line on the top/left of each
            // plate; a 4×4 dark dot at every plate corner. Interior carries faint
            // mineral-fibre noise.
            const int plate = 128;
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                int ly = y % plate;
                for (int x = 0; x < Size; x++)
                {
                    int lx = x % plate;
                    Color32 c;
                    if (lx < 4 && ly < 4)            c = dot;                   // corner dot
                    else if (lx < 3 || ly < 3)       c = border;                // plate edge
                    else                             c = AddNoise(bg, rng, 5);  // mineral fibre
                    px[y * Size + x] = c;
                }
            }
            return px;
        }

        // ─── Helpers ─────────────────────────────────────────────────────────

        private static Color32 AddNoise(Color32 c, System.Random rng, int amp)
        {
            int n = rng.Next(-amp, amp + 1);
            return new Color32(
                (byte)Mathf.Clamp(c.r + n, 0, 255),
                (byte)Mathf.Clamp(c.g + n, 0, 255),
                (byte)Mathf.Clamp(c.b + n, 0, 255),
                255);
        }

        private static Texture2D Bake(Color32[] pixels, string fileName)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.SetPixels32(pixels);
            tex.Apply();

            string path = $"{TextureFolder}/{fileName}";
            File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), path), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            ConfigureImporter(path);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static void ConfigureImporter(string path)
        {
            if (AssetImporter.GetAtPath(path) is not TextureImporter importer) return;
            importer.textureType   = TextureImporterType.Default;
            importer.sRGBTexture   = true;
            importer.wrapMode      = TextureWrapMode.Repeat;
            importer.filterMode    = FilterMode.Bilinear;
            importer.mipmapEnabled = true;
            importer.maxTextureSize = Size;
            importer.SaveAndReimport();
        }

        private static void ApplyTexture(string matPath, Texture2D tex, float tileX, float tileY)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                Debug.LogError($"[TextureGenerator] Material not found: {matPath}");
                return;
            }

            var scale = new Vector2(tileX, tileY);
            if (mat.HasProperty("_BaseMap")) { mat.SetTexture("_BaseMap", tex); mat.SetTextureScale("_BaseMap", scale); }
            if (mat.HasProperty("_MainTex")) { mat.SetTexture("_MainTex", tex); mat.SetTextureScale("_MainTex", scale); }

            // Neutralise the existing colour tint so the texture reads true.
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);
            if (mat.HasProperty("_Color"))     mat.SetColor("_Color", Color.white);

            EditorUtility.SetDirty(mat);
        }

        private static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder(ResourcesRoot))
                AssetDatabase.CreateFolder("Assets", "Resources");
            if (!AssetDatabase.IsValidFolder(TextureFolder))
                AssetDatabase.CreateFolder(ResourcesRoot, "Textures");
        }
    }
}
#endif
