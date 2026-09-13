#if UNITY_EDITOR
using System.IO;
using BackroomsSurvival.WorldGen3;
using UnityEditor;
using UnityEngine;

namespace BackroomsSurvival.EditorTools
{
    /// <summary>
    /// A5 (13-09) — hornea la máscara de <see cref="Wg3TrimMaskPattern"/> desde el normal del yeso y
    /// se la pone a <c>Assets/Materials/WorldGen3/Wg3_Trim.mat</c> con <c>_Smoothness</c> a 1 (la
    /// suavidad 0,22 pasa al alfa). Tinte, escala, albedo y normal del remate no cambian.
    ///
    /// Menú: Backrooms/WG3/Generate Trim Mask.
    /// </summary>
    public static class Wg3TrimSurface
    {
        private const string MaterialPath = "Assets/Materials/WorldGen3/Wg3_Trim.mat";
        private const string MaskPath = "Assets/Art/Wg3/Textures/Wg3_Trim_Mask.png";

        [MenuItem("Backrooms/WG3/Generate Trim Mask")]
        public static void Generate()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                Debug.LogError($"[wg3] no está {MaterialPath}");
                return;
            }
            var normalAsset = mat.GetTexture("_BumpMap") as Texture2D;
            string normalPath = normalAsset != null ? AssetDatabase.GetAssetPath(normalAsset) : null;
            if (string.IsNullOrEmpty(normalPath) || !File.Exists(normalPath))
            {
                Debug.LogError($"[wg3] {MaterialPath} no tiene un normal en disco del que sacar la máscara");
                return;
            }

            // Del PNG en disco y no del Texture2D importado: el importado no es legible y además
            // viene comprimido. El PNG es la fuente exacta.
            var src = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            if (!src.LoadImage(File.ReadAllBytes(normalPath)))
            {
                Object.DestroyImmediate(src);
                Debug.LogError($"[wg3] {normalPath} no decodifica como imagen");
                return;
            }
            int w = src.width, h = src.height;
            Color32[] px = src.GetPixels32();
            Object.DestroyImmediate(src);

            var rgb = new byte[px.Length * 3];
            for (int i = 0; i < px.Length; i++)
            {
                rgb[i * 3] = px[i].r;
                rgb[i * 3 + 1] = px[i].g;
                rgb[i * 3 + 2] = px[i].b;
            }
            byte[] mask = Wg3TrimMaskPattern.FromNormal(rgb);

            var outPx = new Color32[px.Length];
            for (int i = 0; i < outPx.Length; i++)
                outPx[i] = new Color32(mask[i * 4], mask[i * 4 + 1], mask[i * 4 + 2], mask[i * 4 + 3]);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            tex.SetPixels32(outPx);
            tex.Apply();
            File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), MaskPath), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);

            AssetDatabase.ImportAsset(MaskPath, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(MaskPath) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;
                importer.alphaIsTransparency = false;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.wrapMode = TextureWrapMode.Repeat;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = 8;
                importer.mipmapEnabled = true;
                importer.maxTextureSize = Mathf.Max(w, h);
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }
            var maskTex = AssetDatabase.LoadAssetAtPath<Texture2D>(MaskPath);

            Vector2 s = mat.GetTextureScale("_BaseMap");
            mat.SetTexture("_MetallicGlossMap", maskTex);
            mat.SetTextureScale("_MetallicGlossMap", s);
            mat.SetTexture("_OcclusionMap", maskTex);
            mat.SetTextureScale("_OcclusionMap", s);
            mat.SetFloat("_OcclusionStrength", 1f);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 1f);
            mat.SetFloat("_SmoothnessTextureChannel", 0f);
            mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            mat.EnableKeyword("_OCCLUSIONMAP");
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[wg3] máscara del remate generada en {MaskPath} desde {normalPath} y aplicada a {MaterialPath}.");
        }
    }
}
#endif
