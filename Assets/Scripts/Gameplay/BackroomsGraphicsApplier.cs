using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-134 — lo que convierte las filas del menú en píxeles.
    ///
    /// UN SOLO URP Asset, escrito en caliente (D1). La vía que Unity documenta es un asset por
    /// nivel de <c>QualitySettings</c>; se descartó porque los seis serían copias de
    /// `PC_RPAsset` y en cuanto alguien afine el de verdad, los otros cinco mienten.
    ///
    /// LO QUE NO SE TOCA, y es la regla que sostiene todo lo demás (D2): las CAPACIDADES del asset
    /// (<c>supportsMainLightShadows</c>, <c>supportsAdditionalLightShadows</c>,
    /// <c>supportsSoftShadows</c>). URP recorta keywords en el build a partir de ellas, así que una
    /// que venga apagada NO se puede encender desde código — y además su setter es <c>internal</c>.
    /// Los presupuestos numéricos (escala, muestras, distancia, cascadas, atlas) sí se escriben.
    ///
    /// «Sin sombras» se aplica por su EFECTO, no por su bandera: distancia de sombra a cero, que es
    /// lo que de verdad se salta el pase (D3).
    /// </summary>
    public static class BackroomsGraphicsApplier
    {
        /// <summary>Ya se avisó de que no hay pipeline; no se repite por cada Apply.</summary>
        private static bool _warnedAboutPipeline;

        /// <summary>
        /// Vuelca los ajustes sobre el URP Asset, la cámara, los Volume y QualitySettings.
        /// Lo llama <see cref="BackroomsGraphicsOptions.Apply"/>, que es el punto que el vendor ya
        /// invoca desde Save y RestoreDefaults (D7).
        /// </summary>
        public static void Apply(BackroomsGraphicsOptions options)
        {
            if (options == null)
                return;

            var values = options.ToValues();

            ApplyToPipeline(values);
            ApplyToQualitySettings(values);
            ApplyToCamera(values);
            ApplyToVolumes(values);
            ApplyToLights(values);
        }

        /// <summary>
        /// El asset EN USO, no el que esté en `Assets/Settings`: la prueba válida de que hay URP es
        /// <see cref="GraphicsSettings.currentRenderPipeline"/>, no que exista el shader (ADR-065 y
        /// la trampa del magenta que costó una sesión entera).
        /// </summary>
        private static UniversalRenderPipelineAsset CurrentPipeline()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null && !_warnedAboutPipeline)
            {
                _warnedAboutPipeline = true;
                Debug.LogWarning("[Gráficos] No hay URP Asset en uso: los ajustes de render no se aplican.");
            }

            return pipeline;
        }

        private static void ApplyToPipeline(in GraphicsPresetValues values)
        {
            var pipeline = CurrentPipeline();
            if (pipeline == null)
                return;

            pipeline.renderScale = Mathf.Clamp(values.RenderScale,
                BackroomsGraphicsOptions.MinRenderScale, BackroomsGraphicsOptions.MaxRenderScale);
            pipeline.upscalingFilter = FilterOf(values.Upscaling);
            pipeline.msaaSampleCount = SampleCountOf(values.Msaa);
            pipeline.supportsHDR = values.Hdr;

            pipeline.shadowDistance = ShadowDistanceOf(values);
            pipeline.shadowCascadeCount = Mathf.Clamp(values.ShadowCascades, 1, 4);
            pipeline.mainLightShadowmapResolution = MainShadowResolutionOf(values.ShadowQuality);
            pipeline.additionalLightsShadowmapResolution = AdditionalShadowResolutionOf(values.ShadowQuality);
        }

        private static void ApplyToQualitySettings(in GraphicsPresetValues values)
        {
            QualitySettings.globalTextureMipmapLimit = MipmapLimitOf(values.TextureQuality);
            QualitySettings.anisotropicFiltering = AnisotropicOf(values.Anisotropic);
            QualitySettings.lodBias = Mathf.Clamp(values.LodBias,
                BackroomsGraphicsOptions.MinLodBias, BackroomsGraphicsOptions.MaxLodBias);
        }

        /// <summary>
        /// FXAA, SMAA y TAA son de la CÁMARA; sólo MSAA vive en el asset (D4). Que no haya cámara es
        /// un caso NORMAL, no un error: el jugador aparece tarde y estos ajustes se guardan antes.
        /// </summary>
        private static void ApplyToCamera(in GraphicsPresetValues values)
        {
            var camera = Camera.main;
            if (camera == null)
                return;

            var data = camera.GetUniversalAdditionalCameraData();
            if (data == null)
                return;

            data.antialiasing = CameraAntiAliasingOf(values.AntiAliasing);
            data.antialiasingQuality = AntialiasingQuality.High;
        }

        /// <summary>
        /// Los efectos se encienden y apagan en <c>volume.profile</c>, que es una COPIA de runtime.
        /// Escribir en <c>sharedProfile</c> guardaría el cambio en el asset del vendor (D5).
        /// </summary>
        private static void ApplyToVolumes(in GraphicsPresetValues values)
        {
            var volumes = Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var volume in volumes)
            {
                if (volume == null || volume.sharedProfile == null)
                    continue;

                var profile = volume.profile;
                SetOverride<Bloom>(profile, values.Bloom);
                SetOverride<MotionBlur>(profile, values.MotionBlur);
                SetOverride<DepthOfField>(profile, values.DepthOfField);
                SetOverride<ChromaticAberration>(profile, values.ChromaticAberration);
            }
        }

        private static void SetOverride<T>(VolumeProfile profile, bool enabled) where T : VolumeComponent
        {
            if (profile != null && profile.TryGet(out T component))
                component.active = enabled;
        }

        /// <summary>
        /// Las sombras de las luces del techo se apagan LUZ A LUZ, porque la bandera del asset tiene
        /// setter interno (D3). Y sólo se APAGAN: encenderlas obligaría a inventar una sombra en
        /// luces que nunca la tuvieron. Alcance real limitado, y está declarado en el ADR: lo que
        /// crea WG3 en runtime lleva `DontSave` y no aparece en la búsqueda.
        /// </summary>
        private static void ApplyToLights(in GraphicsPresetValues values)
        {
            if (values.AdditionalLightShadows && values.Shadows)
                return;

            var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var light in lights)
            {
                if (light == null || light.type == LightType.Directional)
                    continue;

                light.shadows = LightShadows.None;
            }
        }

        // --- Conversiones puras: aquí es donde muerden los tests -------------------------------

        /// <summary>Muestras de MSAA. El desplegable dice 2x/4x/8x; URP quiere el número.</summary>
        public static int SampleCountOf(MsaaMode msaa) => msaa switch
        {
            MsaaMode.X2 => 2,
            MsaaMode.X4 => 4,
            MsaaMode.X8 => 8,
            _ => 1
        };

        public static UpscalingFilterSelection FilterOf(UpscalingMode upscaling) => upscaling switch
        {
            UpscalingMode.Fsr1 => UpscalingFilterSelection.FSR,
            UpscalingMode.Stp => UpscalingFilterSelection.STP,
            _ => UpscalingFilterSelection.Auto
        };

        public static AntialiasingMode CameraAntiAliasingOf(AntiAliasingMode antiAliasing) => antiAliasing switch
        {
            AntiAliasingMode.Fxaa => AntialiasingMode.FastApproximateAntialiasing,
            AntiAliasingMode.Smaa => AntialiasingMode.SubpixelMorphologicalAntiAliasing,
            AntiAliasingMode.Taa => AntialiasingMode.TemporalAntiAliasing,
            _ => AntialiasingMode.None
        };

        /// <summary>Cero cuando el jugador apaga las sombras: es lo que se salta el pase.</summary>
        public static float ShadowDistanceOf(in GraphicsPresetValues values) =>
            values.Shadows ? Mathf.Max(0f, values.ShadowDistance) : 0f;

        /// <summary>2048 en «High» no es capricho: es lo que `PC_RPAsset` lleva commiteado.</summary>
        public static int MainShadowResolutionOf(ShadowQualityLevel quality) => quality switch
        {
            ShadowQualityLevel.Low => 1024,
            ShadowQualityLevel.Medium => 2048,
            ShadowQualityLevel.High => 2048,
            _ => 4096
        };

        /// <summary>
        /// El atlas de las luces adicionales va un escalón por delante: un Point son SEIS caras y
        /// URP reparte el atlas entre ellas — con 2048 el techo real por cara son ~682 px.
        /// </summary>
        public static int AdditionalShadowResolutionOf(ShadowQualityLevel quality) => quality switch
        {
            ShadowQualityLevel.Low => 1024,
            ShadowQualityLevel.Medium => 2048,
            _ => 4096
        };

        /// <summary>Límite de mip: 0 es la textura entera, y cada escalón divide el lado por dos.</summary>
        public static int MipmapLimitOf(TextureQuality quality) => quality switch
        {
            TextureQuality.Low => 3,
            TextureQuality.Medium => 2,
            TextureQuality.High => 1,
            _ => 0
        };

        public static AnisotropicFiltering AnisotropicOf(AnisotropicMode anisotropic) => anisotropic switch
        {
            AnisotropicMode.Disabled => AnisotropicFiltering.Disable,
            AnisotropicMode.ForcedOn => AnisotropicFiltering.ForceEnable,
            _ => AnisotropicFiltering.Enable
        };
    }
}
