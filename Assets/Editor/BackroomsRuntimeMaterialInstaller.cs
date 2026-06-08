#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace BackroomsSurvival.EditorTools
{
    public sealed class BackroomsRuntimeMaterialInstaller : IPreprocessBuildWithReport
    {
        private const string ResourcesRoot = "Assets/Resources";
        private const string MaterialFolder = "Assets/Resources/BackroomsRuntimeMaterials";

        public int callbackOrder => -1000;

        [MenuItem("Tools/Backrooms/Fix Runtime Materials")]
        public static void FixRuntimeMaterials()
        {
            EnsureFolders();

            CreateOrUpdateMaterial("BR_Lit.mat", RuntimeMaterialKind.Lit, new Color(0.78f, 0.72f, 0.55f, 1f));
            CreateOrUpdateMaterial("BR_Unlit.mat", RuntimeMaterialKind.Unlit, Color.white);
            CreateOrUpdateMaterial("BR_Transparent.mat", RuntimeMaterialKind.Transparent, new Color(1f, 1f, 1f, 0.35f));
            CreateOrUpdateMaterial("BR_Emissive.mat", RuntimeMaterialKind.Lit, new Color(1f, 0.95f, 0.65f, 1f), true);
            CreateOrUpdateMaterial("BR_UI.mat", RuntimeMaterialKind.Ui, Color.white);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[BackroomsRuntimeMaterialInstaller] Runtime materials fixed for the current render pipeline.");
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            FixRuntimeMaterials();
        }

        private static void EnsureFolders()
        {
            if (!AssetDatabase.IsValidFolder(ResourcesRoot))
                AssetDatabase.CreateFolder("Assets", "Resources");

            if (!AssetDatabase.IsValidFolder(MaterialFolder))
                AssetDatabase.CreateFolder(ResourcesRoot, "BackroomsRuntimeMaterials");
        }

        private static void CreateOrUpdateMaterial(string fileName, RuntimeMaterialKind kind, Color color, bool emissive = false)
        {
            Shader shader = FindCompatibleShader(kind);

            if (shader == null)
            {
                Debug.LogError($"[BackroomsRuntimeMaterialInstaller] No compatible shader found for {fileName}.");
                return;
            }

            string path = $"{MaterialFolder}/{fileName}";
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);

            if (mat == null)
            {
                mat = new Material(shader)
                {
                    name = Path.GetFileNameWithoutExtension(fileName)
                };

                AssetDatabase.CreateAsset(mat, path);
            }
            else
            {
                mat.shader = shader;
            }

            ApplyColor(mat, color);

            if (kind == RuntimeMaterialKind.Transparent || color.a < 0.999f)
                SetTransparent(mat);
            else
                SetOpaque(mat);

            if (emissive)
                SetEmission(mat, color, 1.5f);

            EditorUtility.SetDirty(mat);

            Debug.Log($"[BackroomsRuntimeMaterialInstaller] {fileName} -> {shader.name}");
        }

        private static Shader FindCompatibleShader(RuntimeMaterialKind kind)
        {
            bool usingSrp = GraphicsSettings.currentRenderPipeline != null;

            if (usingSrp)
            {
                Shader srpShader = kind == RuntimeMaterialKind.Unlit || kind == RuntimeMaterialKind.Ui
                    ? FirstShader("Universal Render Pipeline/Unlit", "Sprites/Default", "UI/Default")
                    : FirstShader("Universal Render Pipeline/Lit", "Universal Render Pipeline/Simple Lit", "Universal Render Pipeline/Unlit");

                if (srpShader != null)
                    return srpShader;
            }

            switch (kind)
            {
                case RuntimeMaterialKind.Unlit:
                    return FirstShader("Unlit/Color", "Sprites/Default", "UI/Default", "Legacy Shaders/Diffuse", "Standard");

                case RuntimeMaterialKind.Ui:
                    return FirstShader("UI/Default", "Sprites/Default", "Unlit/Color", "Legacy Shaders/Diffuse", "Standard");

                case RuntimeMaterialKind.Transparent:
                    return FirstShader("Standard", "Legacy Shaders/Transparent/Diffuse", "Unlit/Transparent", "Unlit/Color");

                default:
                    return FirstShader("Standard", "Legacy Shaders/Diffuse", "Unlit/Color", "Sprites/Default");
            }
        }

        private static Shader FirstShader(params string[] names)
        {
            bool usingSrp = GraphicsSettings.currentRenderPipeline != null;

            foreach (string shaderName in names)
            {
                Shader shader = Shader.Find(shaderName);

                if (shader == null)
                    continue;

                if (!usingSrp && shader.name.StartsWith("Universal Render Pipeline/"))
                    continue;

                if (!usingSrp && shader.name.StartsWith("HDRP/"))
                    continue;

                return shader;
            }

            return null;
        }

        private static void ApplyColor(Material mat, Color color)
        {
            if (mat == null)
                return;

            if (mat.HasProperty("_Color"))
                mat.SetColor("_Color", color);

            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", color);

            if (mat.HasProperty("_TintColor"))
                mat.SetColor("_TintColor", color);

            mat.color = color;
        }

        private static void SetOpaque(Material mat)
        {
            if (mat == null)
                return;

            mat.SetOverrideTag("RenderType", "Opaque");

            if (mat.HasProperty("_Mode"))
                mat.SetFloat("_Mode", 0f);

            if (mat.HasProperty("_Surface"))
                mat.SetFloat("_Surface", 0f);

            if (mat.HasProperty("_ZWrite"))
                mat.SetInt("_ZWrite", 1);

            if (mat.HasProperty("_SrcBlend"))
                mat.SetInt("_SrcBlend", (int)BlendMode.One);

            if (mat.HasProperty("_DstBlend"))
                mat.SetInt("_DstBlend", (int)BlendMode.Zero);

            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");

            mat.renderQueue = -1;
        }

        private static void SetTransparent(Material mat)
        {
            if (mat == null)
                return;

            mat.SetOverrideTag("RenderType", "Transparent");

            if (mat.HasProperty("_Mode"))
                mat.SetFloat("_Mode", 3f);

            if (mat.HasProperty("_Surface"))
                mat.SetFloat("_Surface", 1f);

            if (mat.HasProperty("_Blend"))
                mat.SetFloat("_Blend", 0f);

            if (mat.HasProperty("_SrcBlend"))
                mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);

            if (mat.HasProperty("_DstBlend"))
                mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);

            if (mat.HasProperty("_ZWrite"))
                mat.SetInt("_ZWrite", 0);

            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");

            mat.renderQueue = (int)RenderQueue.Transparent;
        }

        private static void SetEmission(Material mat, Color color, float strength)
        {
            if (mat == null)
                return;

            Color emission = color * Mathf.Max(0f, strength);

            if (mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", emission);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            if (mat.HasProperty("_EmissiveColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissiveColor", emission);
            }
        }

        private enum RuntimeMaterialKind
        {
            Lit,
            Unlit,
            Transparent,
            Ui
        }
    }
}
#endif