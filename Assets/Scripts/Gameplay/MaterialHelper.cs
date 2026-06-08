using UnityEngine;
using UnityEngine.Rendering;

namespace BackroomsSurvival.Gameplay
{
    public static class MaterialHelper
    {
        private const string ResourcesFolder = "BackroomsRuntimeMaterials";
        private const string LitPath = ResourcesFolder + "/BR_Lit";
        private const string UnlitPath = ResourcesFolder + "/BR_Unlit";
        private const string TransparentPath = ResourcesFolder + "/BR_Transparent";
        private const string EmissivePath = ResourcesFolder + "/BR_Emissive";
        private const string UiPath = ResourcesFolder + "/BR_UI";

        private static Material _litBase;
        private static Material _unlitBase;
        private static Material _transparentBase;
        private static Material _emissiveBase;
        private static Material _uiBase;

        public static Material MakeLit(Color color)
        {
            Material mat = Clone(GetLitBase(), "Runtime_Lit");
            ApplyColor(mat, color);
            ForceOpaque(mat);
            return mat;
        }

        public static Material MakeUnlit(Color color)
        {
            Material mat = Clone(GetUnlitBase(), "Runtime_Unlit");
            ApplyColor(mat, color);
            ForceOpaque(mat);
            return mat;
        }

        public static Material MakeTransparent(Color color)
        {
            Material mat = Clone(GetTransparentBase(), "Runtime_Transparent");
            ApplyColor(mat, color);
            SetAlphaBlend(mat);
            return mat;
        }

        public static Material MakeEmissive(Color color, float emissionStrength = 1f)
        {
            Material mat = Clone(GetEmissiveBase(), "Runtime_Emissive");
            ApplyColor(mat, color);
            ForceOpaque(mat);

            Color emission = color * Mathf.Max(0f, emissionStrength);

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

            return mat;
        }

        public static Material MakeUi(Color color)
        {
            Material mat = Clone(GetUiBase(), "Runtime_UI");
            ApplyColor(mat, color);

            if (color.a < 0.999f)
                SetAlphaBlend(mat);
            else
                ForceOpaque(mat);

            return mat;
        }

        private static Material GetLitBase()
        {
            if (_litBase == null)
                _litBase = LoadBaseMaterial(LitPath, RuntimeMaterialKind.Lit);

            return _litBase;
        }

        private static Material GetUnlitBase()
        {
            if (_unlitBase == null)
                _unlitBase = LoadBaseMaterial(UnlitPath, RuntimeMaterialKind.Unlit);

            return _unlitBase;
        }

        private static Material GetTransparentBase()
        {
            if (_transparentBase == null)
            {
                _transparentBase = LoadBaseMaterial(TransparentPath, RuntimeMaterialKind.Transparent);
                SetAlphaBlend(_transparentBase);
            }

            return _transparentBase;
        }

        private static Material GetEmissiveBase()
        {
            if (_emissiveBase == null)
                _emissiveBase = LoadBaseMaterial(EmissivePath, RuntimeMaterialKind.Lit);

            return _emissiveBase;
        }

        private static Material GetUiBase()
        {
            if (_uiBase == null)
                _uiBase = LoadBaseMaterial(UiPath, RuntimeMaterialKind.Ui);

            return _uiBase;
        }

        private static Material LoadBaseMaterial(string path, RuntimeMaterialKind kind)
        {
            Material loaded = Resources.Load<Material>(path);

            if (loaded != null && IsShaderCompatible(loaded.shader))
                return loaded;

            if (loaded != null)
            {
                Debug.LogWarning(
                    $"[MaterialHelper] Ignoring incompatible material at Resources/{path}.mat. " +
                    $"Shader={loaded.shader?.name ?? "NULL"}."
                );
            }

            Shader shader = FindCompatibleShader(kind);

            if (shader != null)
            {
                Material mat = new Material(shader)
                {
                    name = "RuntimeFallback_" + kind
                };

                return mat;
            }

            Debug.LogError("[MaterialHelper] No compatible shader found. Creating primitive fallback.");
            return CreatePrimitiveFallbackMaterial();
        }

        private static bool IsShaderCompatible(Shader shader)
        {
            if (shader == null)
                return false;

            string shaderName = shader.name;
            bool usingSrp = GraphicsSettings.currentRenderPipeline != null;

            if (!usingSrp)
            {
                if (shaderName.StartsWith("Universal Render Pipeline/"))
                    return false;

                if (shaderName.StartsWith("HDRP/") || shaderName.StartsWith("High Definition Render Pipeline/"))
                    return false;
            }

            return true;
        }

        private static Shader FindCompatibleShader(RuntimeMaterialKind kind)
        {
            bool usingSrp = GraphicsSettings.currentRenderPipeline != null;

            if (usingSrp)
            {
                Shader srpShader;

                if (kind == RuntimeMaterialKind.Unlit || kind == RuntimeMaterialKind.Ui)
                {
                    srpShader = FindFirstExistingShader(
                        "Universal Render Pipeline/Unlit",
                        "Sprites/Default",
                        "UI/Default"
                    );
                }
                else
                {
                    srpShader = FindFirstExistingShader(
                        "Universal Render Pipeline/Lit",
                        "Universal Render Pipeline/Simple Lit",
                        "Universal Render Pipeline/Unlit"
                    );
                }

                if (srpShader != null)
                    return srpShader;
            }

            switch (kind)
            {
                case RuntimeMaterialKind.Unlit:
                    return FindFirstExistingShader(
                        "Unlit/Color",
                        "Sprites/Default",
                        "UI/Default",
                        "Legacy Shaders/Diffuse",
                        "Standard"
                    );

                case RuntimeMaterialKind.Ui:
                    return FindFirstExistingShader(
                        "UI/Default",
                        "Sprites/Default",
                        "Unlit/Color",
                        "Legacy Shaders/Diffuse",
                        "Standard"
                    );

                case RuntimeMaterialKind.Transparent:
                    return FindFirstExistingShader(
                        "Standard",
                        "Legacy Shaders/Transparent/Diffuse",
                        "Unlit/Transparent",
                        "Unlit/Color",
                        "Sprites/Default"
                    );

                default:
                    return FindFirstExistingShader(
                        "Standard",
                        "Legacy Shaders/Diffuse",
                        "Unlit/Color",
                        "Sprites/Default"
                    );
            }
        }

        private static Shader FindFirstExistingShader(params string[] shaderNames)
        {
            foreach (string shaderName in shaderNames)
            {
                Shader shader = Shader.Find(shaderName);

                if (shader != null && IsShaderCompatible(shader))
                    return shader;
            }

            return null;
        }

        private static Material Clone(Material source, string name)
        {
            Material mat;

            if (source != null)
                mat = new Material(source);
            else
                mat = CreatePrimitiveFallbackMaterial();

            if (mat != null)
                mat.name = name;

            return mat;
        }

        private static Material CreatePrimitiveFallbackMaterial()
        {
            GameObject tmp = null;

            try
            {
                tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                tmp.hideFlags = HideFlags.HideAndDontSave;
                tmp.SetActive(false);

                Renderer renderer = tmp.GetComponent<Renderer>();

                if (renderer != null && renderer.sharedMaterial != null)
                    return new Material(renderer.sharedMaterial);
            }
            finally
            {
                if (tmp != null)
                {
                    if (Application.isPlaying)
                        Object.Destroy(tmp);
                    else
                        Object.DestroyImmediate(tmp);
                }
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

        public static void ForceOpaque(Material mat)
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

        public static void SetAlphaBlend(Material mat)
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

        public static void ClearCache()
        {
            _litBase = null;
            _unlitBase = null;
            _transparentBase = null;
            _emissiveBase = null;
            _uiBase = null;
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