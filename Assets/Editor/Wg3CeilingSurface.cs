#if UNITY_EDITOR
using System.IO;
using BackroomsSurvival.WorldGen3;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// A5 (13-09) — hornea la placa de <see cref="Wg3CeilingPattern"/> a PNG y viste con ella
    /// <c>Assets/Materials/WorldGen3/Wg3_Ceiling.mat</c>. Mismo asset: ni escena ni prefab cambian.
    ///
    /// Lo que se toca: los cuatro mapas con su escala (0,8333, la de antes), las tres palabras clave
    /// y <c>_Smoothness</c> a 1 (con máscara URP/Lit multiplica las dos; la suavidad vive en el alfa).
    /// Lo que NO se toca: <c>_BaseColor</c> (0,80/0,80/0,77). El color medio del patrón está calibrado
    /// contra la textura vieja, así que con el mismo tinte el techo devuelve la misma luz.
    ///
    /// Sin teselado estocástico a propósito: desalinearía la rejilla de placas.
    ///
    /// Menú: Backrooms/WG3/Generate Backrooms Ceiling.
    /// </summary>
    public static class Wg3CeilingSurface
    {
        private const string TextureFolder = "Assets/Art/Wg3/Textures";
        private const string MaterialPath = "Assets/Materials/WorldGen3/Wg3_Ceiling.mat";

        [MenuItem("Backrooms/WG3/Generate Backrooms Ceiling")]
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

            Wg3CeilingPattern.Build(out byte[] albedo, out byte[] normal, out byte[] mask);
            Texture2D albedoTex = Bake(albedo, 3, "Wg3_Ceiling.png", TextureImporterType.Default, true);
            Texture2D normalTex = Bake(normal, 3, "Wg3_Ceiling_Normal.png", TextureImporterType.NormalMap, false);
            Texture2D maskTex = Bake(mask, 4, "Wg3_Ceiling_Mask.png", TextureImporterType.Default, false);

            var s = new Vector2(Wg3CeilingPattern.MaterialScale, Wg3CeilingPattern.MaterialScale);
            mat.SetTexture("_BaseMap", albedoTex);
            mat.SetTextureScale("_BaseMap", s);
            mat.SetTexture("_BumpMap", normalTex);
            mat.SetTextureScale("_BumpMap", s);
            mat.SetFloat("_BumpScale", 1f);
            mat.SetTexture("_MetallicGlossMap", maskTex);
            mat.SetTextureScale("_MetallicGlossMap", s);
            mat.SetTexture("_OcclusionMap", maskTex);
            mat.SetTextureScale("_OcclusionMap", s);
            mat.SetFloat("_OcclusionStrength", 1f);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 1f);
            mat.SetFloat("_SmoothnessTextureChannel", 0f); // 0 = alfa del mapa metálico
            mat.EnableKeyword("_NORMALMAP");
            mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            mat.EnableKeyword("_OCCLUSIONMAP");
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[wg3] placa de techo generada en {TextureFolder} y aplicada a {MaterialPath} " +
                      $"(repite cada {Wg3CeilingPattern.RepeatM} m).");
        }

        private static Texture2D Bake(byte[] data, int channels, string fileName,
                                      TextureImporterType type, bool srgb)
        {
            int size = Wg3CeilingPattern.Size;
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
                importer.anisoLevel = 8; // el techo también se ve a rasante
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
