using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>Los seis escalones del desplegable global, más el estado que NO es un escalón.</summary>
    public enum GraphicsPreset
    {
        VeryLow = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        VeryHigh = 4,
        Ultra = 5,

        /// <summary>No es un preset: es "el jugador ha tocado un detalle a mano".</summary>
        Custom = 6
    }

    public enum AntiAliasingMode { Off = 0, Fxaa = 1, Smaa = 2, Taa = 3 }

    public enum MsaaMode { Off = 0, X2 = 1, X4 = 2, X8 = 3 }

    /// <summary>
    /// NO hay DLSS. DLSS es HDRP-only en Unity 6 y esto es URP 17 Forward+ (ADR-065): ofrecerlo
    /// sería una etiqueta que no puede hacer nada. Los dos escaladores que URP SÍ trae son
    /// FSR 1.0 (espacial) y STP (temporal, nuevo en Unity 6).
    /// </summary>
    public enum UpscalingMode { Off = 0, Fsr1 = 1, Stp = 2 }

    /// <summary>Nombre con sufijo: `ShadowQuality` a secas choca con el enum de UnityEngine.</summary>
    public enum ShadowQualityLevel { Low = 0, Medium = 1, High = 2, Ultra = 3 }

    public enum TextureQuality { Low = 0, Medium = 1, High = 2, Full = 3 }

    public enum AnisotropicMode { Disabled = 0, PerTexture = 1, ForcedOn = 2 }

    /// <summary>
    /// Los valores de UN escalón. Es el contrato entre el desplegable global y las filas de
    /// detalle: elegir preset escribe estos campos en los controles, y tocar cualquier control
    /// deja de coincidir con la fila de la tabla, lo que pone el desplegable en
    /// <see cref="GraphicsPreset.Custom"/>.
    /// </summary>
    public readonly struct GraphicsPresetValues
    {
        public readonly float RenderScale;
        public readonly UpscalingMode Upscaling;
        public readonly bool Hdr;
        public readonly AntiAliasingMode AntiAliasing;
        public readonly MsaaMode Msaa;

        public readonly bool Shadows;
        public readonly ShadowQualityLevel ShadowQuality;
        public readonly float ShadowDistance;
        public readonly int ShadowCascades;
        public readonly bool AdditionalLightShadows;

        public readonly bool Bloom;
        public readonly bool MotionBlur;
        public readonly bool DepthOfField;
        public readonly bool ChromaticAberration;

        public readonly TextureQuality TextureQuality;
        public readonly AnisotropicMode Anisotropic;
        public readonly float LodBias;

        public GraphicsPresetValues(float renderScale, UpscalingMode upscaling, bool hdr,
            AntiAliasingMode antiAliasing, MsaaMode msaa, bool shadows, ShadowQualityLevel shadowQuality,
            float shadowDistance, int shadowCascades, bool additionalLightShadows, bool bloom,
            bool motionBlur, bool depthOfField, bool chromaticAberration,
            TextureQuality textureQuality, AnisotropicMode anisotropic, float lodBias)
        {
            RenderScale = renderScale;
            Upscaling = upscaling;
            Hdr = hdr;
            AntiAliasing = antiAliasing;
            Msaa = msaa;
            Shadows = shadows;
            ShadowQuality = shadowQuality;
            ShadowDistance = shadowDistance;
            ShadowCascades = shadowCascades;
            AdditionalLightShadows = additionalLightShadows;
            Bloom = bloom;
            MotionBlur = motionBlur;
            DepthOfField = depthOfField;
            ChromaticAberration = chromaticAberration;
            TextureQuality = textureQuality;
            Anisotropic = anisotropic;
            LodBias = lodBias;
        }
    }

    /// <summary>
    /// La tabla de los seis escalones.
    ///
    /// AÚN NO APLICA NADA: esta tanda monta el menú (filas, desplegables, guardado) y deja el
    /// aplicador para la siguiente, que es donde vive la decisión abierta — un URP Asset por
    /// nivel, o uno solo escrito en runtime. La tabla se escribe YA porque es lo que el menú
    /// necesita para que el desplegable global mueva las filas de detalle.
    ///
    /// High es el ESPEJO de lo que hay hoy en Assets/Settings/PC_RPAsset.asset (MSAA 2x,
    /// distancia 50, 4 cascadas, sombras de luces adicionales encendidas): así el preset por
    /// defecto no promete un aspecto distinto del que el juego ya tiene, y cuando llegue el
    /// aplicador, quien no toque nada no verá ningún cambio.
    /// </summary>
    public static class GraphicsQualityPresets
    {
        /// <summary>Escalones reales. <see cref="GraphicsPreset.Custom"/> queda fuera a propósito.</summary>
        public const int PresetCount = 6;

        /// <summary>El escalón con el que arranca quien no ha tocado nunca el menú.</summary>
        public const GraphicsPreset Default = GraphicsPreset.High;

        /// <summary>Etiquetas del desplegable, en inglés y en orden. La séptima es Custom.</summary>
        public static readonly string[] Labels =
        {
            "Very Low", "Low", "Medium", "High", "Very High", "Ultra", "Custom"
        };

        private static readonly GraphicsPresetValues[] Table =
        {
            // Very Low - sacrifica todo menos poder jugar: escala baja + FSR, cero sombras.
            new GraphicsPresetValues(0.60f, UpscalingMode.Fsr1, false, AntiAliasingMode.Off, MsaaMode.Off,
                false, ShadowQualityLevel.Low, 30f, 1, false,
                false, false, false, false,
                TextureQuality.Low, AnisotropicMode.Disabled, 0.5f),

            // Low - vuelven las sombras, pero no las de las luces del techo, que son las caras.
            new GraphicsPresetValues(0.75f, UpscalingMode.Fsr1, false, AntiAliasingMode.Fxaa, MsaaMode.Off,
                true, ShadowQualityLevel.Low, 40f, 1, false,
                true, false, false, false,
                TextureQuality.Medium, AnisotropicMode.PerTexture, 0.7f),

            new GraphicsPresetValues(0.85f, UpscalingMode.Fsr1, true, AntiAliasingMode.Fxaa, MsaaMode.Off,
                true, ShadowQualityLevel.Medium, 50f, 2, true,
                true, false, true, false,
                TextureQuality.High, AnisotropicMode.PerTexture, 1.0f),

            // High - espejo de PC_RPAsset tal como está commiteado. Es el defecto.
            new GraphicsPresetValues(1.00f, UpscalingMode.Off, true, AntiAliasingMode.Smaa, MsaaMode.X2,
                true, ShadowQualityLevel.High, 50f, 4, true,
                true, false, true, true,
                TextureQuality.Full, AnisotropicMode.PerTexture, 1.0f),

            new GraphicsPresetValues(1.00f, UpscalingMode.Off, true, AntiAliasingMode.Smaa, MsaaMode.X4,
                true, ShadowQualityLevel.Ultra, 80f, 4, true,
                true, true, true, true,
                TextureQuality.Full, AnisotropicMode.ForcedOn, 1.5f),

            // Ultra - supersampling por encima de 1.0; el techo de sombras es el atlas de 4096.
            new GraphicsPresetValues(1.25f, UpscalingMode.Off, true, AntiAliasingMode.Taa, MsaaMode.X8,
                true, ShadowQualityLevel.Ultra, 120f, 4, true,
                true, true, true, true,
                TextureQuality.Full, AnisotropicMode.ForcedOn, 2.0f)
        };

        public static GraphicsPresetValues Get(GraphicsPreset preset) => Get((int)preset);

        public static GraphicsPresetValues Get(int index) =>
            Table[Mathf.Clamp(index, 0, PresetCount - 1)];

        /// <summary>
        /// Qué escalón describe estos valores, o <see cref="GraphicsPreset.Custom"/> si ninguno.
        /// Es la mitad del contrato del desplegable: sin esto, mover una fila de detalle dejaría
        /// el desplegable mintiendo.
        /// </summary>
        public static GraphicsPreset Match(in GraphicsPresetValues values)
        {
            for (int i = 0; i < PresetCount; i++)
            {
                if (SameValues(Table[i], values))
                    return (GraphicsPreset)i;
            }

            return GraphicsPreset.Custom;
        }

        /// <summary>Los flotantes se comparan con holgura: un slider devuelve 0,7499999.</summary>
        private static bool SameValues(in GraphicsPresetValues a, in GraphicsPresetValues b) =>
            Mathf.Abs(a.RenderScale - b.RenderScale) < 0.005f &&
            a.Upscaling == b.Upscaling &&
            a.Hdr == b.Hdr &&
            a.AntiAliasing == b.AntiAliasing &&
            a.Msaa == b.Msaa &&
            a.Shadows == b.Shadows &&
            a.ShadowQuality == b.ShadowQuality &&
            Mathf.Abs(a.ShadowDistance - b.ShadowDistance) < 0.5f &&
            a.ShadowCascades == b.ShadowCascades &&
            a.AdditionalLightShadows == b.AdditionalLightShadows &&
            a.Bloom == b.Bloom &&
            a.MotionBlur == b.MotionBlur &&
            a.DepthOfField == b.DepthOfField &&
            a.ChromaticAberration == b.ChromaticAberration &&
            a.TextureQuality == b.TextureQuality &&
            a.Anisotropic == b.Anisotropic &&
            Mathf.Abs(a.LodBias - b.LodBias) < 0.005f;
    }
}
