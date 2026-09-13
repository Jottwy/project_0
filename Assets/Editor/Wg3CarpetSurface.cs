#if UNITY_EDITOR
using System.IO;
using BackroomsSurvival.WorldGen3;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// Hornea la moqueta de <see cref="Wg3CarpetPattern"/> a PNG y viste con ella
    /// <c>Assets/Materials/WorldGen3/Wg3_Floor.mat</c>, el material de SUELO que usan el prefab
    /// <c>GridTestWorld</c> y <c>STP_Showcase</c>. Mismo asset: ni la escena ni el prefab cambian.
    ///
    /// Lo que se toca del material: los cuatro mapas y sus escalas, las tres palabras clave,
    /// <c>_Smoothness</c> a 1 y <c>_BaseColor</c>. **<c>_Smoothness</c> va a 1 a propósito**: con
    /// máscara, URP/Lit hace <c>specGloss.a *= _Smoothness</c> (<c>LitInput.hlsl</c>), así que
    /// cualquier otro valor escala hacia abajo la suavidad que la máscara ya lleva.
    ///
    /// <c>_BaseColor</c> se CALCULA, no se escribe a mano: el tinte lineal es el ocre objetivo
    /// (<see cref="Wg3CarpetPattern.TargetSrgb"/>) dividido por el albedo medio lineal de la textura
    /// recién horneada. Así la luminancia efectiva del suelo se queda en la del terrazo al que
    /// sustituye aunque alguien retoque el patrón.
    ///
    /// Menú: Backrooms/WG3/Generate Backrooms Carpet.
    /// </summary>
    public static class Wg3CarpetSurface
    {
        private const string TextureFolder = "Assets/Art/Wg3/Textures";
        private const string MaterialPath = "Assets/Materials/WorldGen3/Wg3_Floor.mat";

        private const string AlbedoName = "Wg3_Carpet.png";
        private const string NormalName = "Wg3_Carpet_Normal.png";
        private const string MaskName = "Wg3_Carpet_Mask.png";

        [MenuItem("Backrooms/WG3/Generate Backrooms Carpet")]
        public static void Generate()
        {
            if (!AssetDatabase.IsValidFolder(TextureFolder))
            {
                Debug.LogError($"[wg3] no existe {TextureFolder}: ejecuta antes Backrooms/WG3/Generate Office Surfaces");
                return;
            }
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                Debug.LogError($"[wg3] no está {MaterialPath}");
                return;
            }

            Wg3CarpetPattern.Build(out byte[] albedo, out byte[] normal, out byte[] mask);
            Texture2D albedoTex = Bake(albedo, 3, AlbedoName, TextureImporterType.Default, true);
            Texture2D normalTex = Bake(normal, 3, NormalName, TextureImporterType.NormalMap, false);
            Texture2D maskTex = Bake(mask, 4, MaskName, TextureImporterType.Default, false);

            Color tint = TintFor(albedo);

            var s = new Vector2(Wg3CarpetPattern.MaterialScale, Wg3CarpetPattern.MaterialScale);
            mat.SetTexture("_BaseMap", albedoTex);
            mat.SetTextureScale("_BaseMap", s);
            mat.SetTexture("_BumpMap", normalTex);
            mat.SetTextureScale("_BumpMap", s);
            mat.SetFloat("_BumpScale", 1f);
            // Una sola textura para las dos ranuras: URP/Lit toma metal (R) y suavidad (A) de
            // `_MetallicGlossMap` y la oclusión (G) de `_OcclusionMap`.
            mat.SetTexture("_MetallicGlossMap", maskTex);
            mat.SetTextureScale("_MetallicGlossMap", s);
            mat.SetTexture("_OcclusionMap", maskTex);
            mat.SetTextureScale("_OcclusionMap", s);
            mat.SetFloat("_OcclusionStrength", 1f);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 1f);
            mat.SetFloat("_SmoothnessTextureChannel", 0f); // 0 = alfa del mapa metálico
            mat.SetColor("_BaseColor", tint);
            mat.SetColor("_Color", tint);
            // Las palabras clave las pone el inspector al asignar a mano; por script hay que
            // ponerlas, o el shader compila la variante sin mapas y no se ve ninguno.
            mat.EnableKeyword("_NORMALMAP");
            mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            mat.EnableKeyword("_OCCLUSIONMAP");
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[wg3] moqueta generada en {TextureFolder} y aplicada a {MaterialPath} " +
                      $"(repite cada {Wg3CarpetPattern.RepeatM} m, _BaseColor {tint}).");
        }

        /// <summary>Tinte (sRGB, como lo guarda el material) que lleva el albedo medio al ocre
        /// objetivo en espacio LINEAL, que es donde URP multiplica.</summary>
        private static Color TintFor(byte[] albedoRgb)
        {
            int n = albedoRgb.Length / 3;
            double r = 0, g = 0, b = 0;
            for (int i = 0; i < n; i++)
            {
                r += Mathf.GammaToLinearSpace(albedoRgb[i * 3] / 255f);
                g += Mathf.GammaToLinearSpace(albedoRgb[i * 3 + 1] / 255f);
                b += Mathf.GammaToLinearSpace(albedoRgb[i * 3 + 2] / 255f);
            }
            byte[] t = Wg3CarpetPattern.TargetSrgb;
            float tr = Mathf.GammaToLinearSpace(t[0] / 255f) / (float)(r / n);
            float tg = Mathf.GammaToLinearSpace(t[1] / 255f) / (float)(g / n);
            float tb = Mathf.GammaToLinearSpace(t[2] / 255f) / (float)(b / n);
            return new Color(Mathf.LinearToGammaSpace(tr), Mathf.LinearToGammaSpace(tg),
                             Mathf.LinearToGammaSpace(tb), 1f);
        }

        private static Texture2D Bake(byte[] data, int channels, string fileName,
                                      TextureImporterType type, bool srgb)
        {
            int size = Wg3CarpetPattern.Size;
            var px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++)
            {
                byte r = data[i * channels], g = data[i * channels + 1], b = data[i * channels + 2];
                byte a = channels == 4 ? data[i * channels + 3] : (byte)255;
                px[i] = new Color32(r, g, b, a);
            }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.SetPixels32(px);
            tex.Apply();

            string path = $"{TextureFolder}/{fileName}";
            File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), path), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(path) is TextureImporter importer)
            {
                importer.textureType = type;
                importer.sRGBTexture = srgb;
                importer.alphaIsTransparency = false;
                importer.alphaSource = channels == 4 ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = 8; // el suelo se ve casi siempre a rasante
                importer.mipmapEnabled = true;
                importer.maxTextureSize = size;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
    }
}
#endif
