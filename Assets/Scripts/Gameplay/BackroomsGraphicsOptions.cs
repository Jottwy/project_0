using PolymindGames.Options;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// Los ajustes visuales del juego, DENTRO del sistema de opciones del vendor — mismo patrón
    /// que <see cref="VoiceOptions"/> (ADR-046): heredar de <see cref="UserOptions{T}"/> da gratis
    /// el guardado en JSON y los botones "Apply" y "Restore".
    ///
    /// NO es un <c>partial</c> de <c>GraphicsOptions</c>: esa clase es <c>sealed partial</c> dentro
    /// del asmdef <c>PolymindGames</c>, así que una parcial nuestra tendría que vivir en territorio
    /// del vendor, y eso es regla dura del proyecto. Se hereda, no se toca.
    ///
    /// LO QUE EL VENDOR YA CUBRE NO SE REPITE AQUÍ: resolución, pantalla completa, VSync, tope de
    /// fotogramas y campo de visión siguen siendo de <c>GraphicsOptions</c>, y su desplegable de
    /// calidad sigue siendo el de <c>QualitySettings</c>. Este objeto añade lo que faltaba.
    ///
    /// APLICA desde ADR-134: <see cref="Apply"/> vuelca los valores sobre el URP Asset en uso, la
    /// cámara, los Volume y QualitySettings a través de <see cref="BackroomsGraphicsApplier"/>. Un
    /// solo asset escrito en caliente, nunca seis copias de <c>PC_RPAsset</c>.
    /// </summary>
    [CreateAssetMenu(menuName = "Backrooms/Options/Graphics Quality Options",
        fileName = nameof(BackroomsGraphicsOptions))]
    public sealed class BackroomsGraphicsOptions : UserOptions<BackroomsGraphicsOptions>
    {
        [SerializeField, Tooltip("Escalón global, 0..5. El 6 es Custom: lo pone tocar un detalle.")]
        private Option<int> _preset = new Option<int>((int)GraphicsQualityPresets.Default);

        [Header("Rendering")]
        [SerializeField, Tooltip("Resolución interna respecto de la de pantalla.")]
        private Option<float> _renderScale = new Option<float>(1f);

        [SerializeField, Tooltip("Escalador: 0 ninguno, 1 FSR 1.0, 2 STP. NO hay DLSS en URP.")]
        private Option<int> _upscaling = new Option<int>((int)UpscalingMode.Off);

        [SerializeField]
        private Option<bool> _hdr = new Option<bool>(true);

        [SerializeField, Tooltip("0 ninguno, 1 FXAA, 2 SMAA, 3 TAA. Va por cámara, no por asset.")]
        private Option<int> _antiAliasing = new Option<int>((int)AntiAliasingMode.Smaa);

        [SerializeField, Tooltip("0 ninguno, 1 2x, 2 4x, 3 8x.")]
        private Option<int> _msaa = new Option<int>((int)MsaaMode.X2);

        [Header("Shadows")]
        [SerializeField]
        private Option<bool> _shadows = new Option<bool>(true);

        [SerializeField, Tooltip("0 baja, 1 media, 2 alta, 3 ultra.")]
        private Option<int> _shadowQuality = new Option<int>((int)ShadowQualityLevel.High);

        [SerializeField, Tooltip("Metros de mundo. En interiores manda más que la resolución.")]
        private Option<float> _shadowDistance = new Option<float>(50f);

        [SerializeField, Tooltip("Número de cascadas, 1..4 (el valor, no el índice del desplegable).")]
        private Option<int> _shadowCascades = new Option<int>(4);

        [SerializeField, Tooltip("Sombras de las luces del techo. Son las caras del Nivel 0.")]
        private Option<bool> _additionalLightShadows = new Option<bool>(true);

        [Header("Post-processing")]
        [SerializeField]
        private Option<bool> _bloom = new Option<bool>(true);

        [SerializeField]
        private Option<bool> _motionBlur = new Option<bool>(false);

        [SerializeField]
        // Apagada por defecto (13-09): el perfil enfoca a 10 m y difumina lo que se lleva en la mano.
        private Option<bool> _depthOfField = new Option<bool>(false);

        [SerializeField]
        private Option<bool> _chromaticAberration = new Option<bool>(true);

        [Header("Textures & detail")]
        [SerializeField, Tooltip("0 baja, 1 media, 2 alta, 3 completa (límite de mip).")]
        private Option<int> _textureQuality = new Option<int>((int)TextureQuality.Full);

        [SerializeField, Tooltip("0 desactivado, 1 por textura, 2 forzado.")]
        private Option<int> _anisotropic = new Option<int>((int)AnisotropicMode.PerTexture);

        [SerializeField]
        private Option<float> _lodBias = new Option<float>(1f);

        public const float MinRenderScale = 0.5f;
        public const float MaxRenderScale = 2f;
        public const float MinShadowDistance = 20f;
        public const float MaxShadowDistance = 150f;
        public const float MinLodBias = 0.4f;
        public const float MaxLodBias = 2f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Init() => EnsureInstanceInitialized();

        public Option<int> Preset => _preset;
        public Option<float> RenderScale => _renderScale;
        public Option<int> Upscaling => _upscaling;
        public Option<bool> Hdr => _hdr;
        public Option<int> AntiAliasing => _antiAliasing;
        public Option<int> Msaa => _msaa;
        public Option<bool> Shadows => _shadows;

        // Nombres distintos de los enums: una propiedad llamada igual que un tipo del mismo
        // espacio de nombres tapa al tipo, y los casts de `ToValues` dejan de compilar.
        public Option<int> ShadowLevel => _shadowQuality;
        public Option<float> ShadowDistance => _shadowDistance;
        public Option<int> ShadowCascades => _shadowCascades;
        public Option<bool> AdditionalLightShadows => _additionalLightShadows;
        public Option<bool> Bloom => _bloom;
        public Option<bool> MotionBlur => _motionBlur;
        public Option<bool> DepthOfField => _depthOfField;
        public Option<bool> ChromaticAberration => _chromaticAberration;
        public Option<int> TextureLevel => _textureQuality;
        public Option<int> Anisotropic => _anisotropic;
        public Option<float> LodBias => _lodBias;

        /// <summary>Los detalles guardados, en la forma que entiende la tabla de presets.</summary>
        public GraphicsPresetValues ToValues() => new GraphicsPresetValues(
            _renderScale.Value,
            (UpscalingMode)_upscaling.Value,
            _hdr.Value,
            (AntiAliasingMode)_antiAliasing.Value,
            (MsaaMode)_msaa.Value,
            _shadows.Value,
            (ShadowQualityLevel)_shadowQuality.Value,
            _shadowDistance.Value,
            _shadowCascades.Value,
            _additionalLightShadows.Value,
            _bloom.Value,
            _motionBlur.Value,
            _depthOfField.Value,
            _chromaticAberration.Value,
            (TextureQuality)_textureQuality.Value,
            (AnisotropicMode)_anisotropic.Value,
            _lodBias.Value);

        /// <summary>
        /// Vuelca unos valores sobre los campos y RECALCULA el escalón: si coinciden con una fila
        /// de la tabla se guarda esa, y si no, Custom. Así el desplegable nunca puede quedarse
        /// diciendo "High" sobre unos ajustes que ya no son High.
        /// </summary>
        public void SetValues(in GraphicsPresetValues values)
        {
            _renderScale.SetValue(values.RenderScale);
            _upscaling.SetValue((int)values.Upscaling);
            _hdr.SetValue(values.Hdr);
            _antiAliasing.SetValue((int)values.AntiAliasing);
            _msaa.SetValue((int)values.Msaa);
            _shadows.SetValue(values.Shadows);
            _shadowQuality.SetValue((int)values.ShadowQuality);
            _shadowDistance.SetValue(values.ShadowDistance);
            _shadowCascades.SetValue(values.ShadowCascades);
            _additionalLightShadows.SetValue(values.AdditionalLightShadows);
            _bloom.SetValue(values.Bloom);
            _motionBlur.SetValue(values.MotionBlur);
            _depthOfField.SetValue(values.DepthOfField);
            _chromaticAberration.SetValue(values.ChromaticAberration);
            _textureQuality.SetValue((int)values.TextureQuality);
            _anisotropic.SetValue((int)values.Anisotropic);
            _lodBias.SetValue(values.LodBias);
            _preset.SetValue((int)GraphicsQualityPresets.Match(values));
        }

        /// <summary>
        /// ADR-134. El vendor llama a esto desde <c>Save()</c> y <c>RestoreDefaults()</c>, así que
        /// el botón «Apply» del menú aplica de verdad sin que la UI tenga que saber nada del render.
        /// </summary>
        protected override void Apply() => BackroomsGraphicsApplier.Apply(this);

        /// <inheritdoc/>
        protected override void Reset() => SetValues(GraphicsQualityPresets.Get(GraphicsQualityPresets.Default));
    }
}
