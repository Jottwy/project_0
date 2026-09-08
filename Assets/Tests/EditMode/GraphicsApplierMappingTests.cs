using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using BackroomsSurvival.Gameplay;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// ADR-134 — las conversiones del aplicador: lo que el menú dice («2x», «Ultra», «Full») y lo
    /// que URP entiende (muestras, píxeles de atlas, límite de mip).
    ///
    /// Se prueban las funciones PURAS a propósito. Escribir de verdad en el URP Asset en uso
    /// cambiaría el render del editor donde corre la suite, y la parte que sí importa —que un
    /// número no se traduzca al que no es— vive entera aquí.
    /// </summary>
    [TestFixture]
    public class GraphicsApplierMappingTests
    {
        private const string PcRenderPipelineAssetPath = "Settings/PC_RPAsset.asset";

        [Test]
        public void MsaaLabelsMapToSampleCounts()
        {
            Assert.AreEqual(1, BackroomsGraphicsApplier.SampleCountOf(MsaaMode.Off));
            Assert.AreEqual(2, BackroomsGraphicsApplier.SampleCountOf(MsaaMode.X2));
            Assert.AreEqual(4, BackroomsGraphicsApplier.SampleCountOf(MsaaMode.X4));
            Assert.AreEqual(8, BackroomsGraphicsApplier.SampleCountOf(MsaaMode.X8));
        }

        /// <summary>
        /// NO hay DLSS en URP: la fila ofrece FSR 1.0 y STP, y cada una tiene que caer en el filtro
        /// que le toca. Un mapeo cruzado aquí sería una etiqueta que hace otra cosa.
        /// </summary>
        [Test]
        public void UpscalingMapsToTheFilterItPromises()
        {
            Assert.AreEqual(UpscalingFilterSelection.Auto, BackroomsGraphicsApplier.FilterOf(UpscalingMode.Off));
            Assert.AreEqual(UpscalingFilterSelection.FSR, BackroomsGraphicsApplier.FilterOf(UpscalingMode.Fsr1));
            Assert.AreEqual(UpscalingFilterSelection.STP, BackroomsGraphicsApplier.FilterOf(UpscalingMode.Stp));
        }

        [Test]
        public void AntiAliasingMapsToTheCameraModes()
        {
            Assert.AreEqual(AntialiasingMode.None,
                BackroomsGraphicsApplier.CameraAntiAliasingOf(AntiAliasingMode.Off));
            Assert.AreEqual(AntialiasingMode.FastApproximateAntialiasing,
                BackroomsGraphicsApplier.CameraAntiAliasingOf(AntiAliasingMode.Fxaa));
            Assert.AreEqual(AntialiasingMode.SubpixelMorphologicalAntiAliasing,
                BackroomsGraphicsApplier.CameraAntiAliasingOf(AntiAliasingMode.Smaa));
            Assert.AreEqual(AntialiasingMode.TemporalAntiAliasing,
                BackroomsGraphicsApplier.CameraAntiAliasingOf(AntiAliasingMode.Taa));
        }

        /// <summary>
        /// «Sin sombras» se aplica por su EFECTO (distancia cero), porque la bandera del asset tiene
        /// setter interno. Si esto devolviera la distancia guardada, apagar las sombras no apagaría
        /// nada y el ajuste sería decorativo.
        /// </summary>
        [Test]
        public void TurningShadowsOffZeroesTheDistance()
        {
            var high = GraphicsQualityPresets.Get(GraphicsPreset.High);
            Assert.AreEqual(high.ShadowDistance, BackroomsGraphicsApplier.ShadowDistanceOf(high), 0.5f);

            var off = WithShadows(high, false);
            Assert.AreEqual(0f, BackroomsGraphicsApplier.ShadowDistanceOf(off), 0.001f,
                "Con las sombras apagadas la distancia tiene que ser cero.");
        }

        [Test]
        public void ShadowAtlasesNeverShrinkAsQualityRises()
        {
            for (int i = 1; i <= (int)ShadowQualityLevel.Ultra; i++)
            {
                var lower = (ShadowQualityLevel)(i - 1);
                var upper = (ShadowQualityLevel)i;

                Assert.LessOrEqual(BackroomsGraphicsApplier.MainShadowResolutionOf(lower),
                    BackroomsGraphicsApplier.MainShadowResolutionOf(upper), $"Atlas principal baja en {upper}.");
                Assert.LessOrEqual(BackroomsGraphicsApplier.AdditionalShadowResolutionOf(lower),
                    BackroomsGraphicsApplier.AdditionalShadowResolutionOf(upper), $"Atlas adicional baja en {upper}.");
            }
        }

        /// <summary>
        /// El atlas de las luces adicionales va por delante del principal: un Point son SEIS caras y
        /// URP reparte el atlas entre ellas. Igualarlos degradaría la sombra de las lámparas del
        /// techo, que son las caras del Nivel 0.
        /// </summary>
        [Test]
        public void AdditionalLightsGetAtLeastAsMuchAtlasAsTheSun()
        {
            for (int i = 0; i <= (int)ShadowQualityLevel.Ultra; i++)
            {
                var quality = (ShadowQualityLevel)i;
                Assert.GreaterOrEqual(BackroomsGraphicsApplier.AdditionalShadowResolutionOf(quality),
                    BackroomsGraphicsApplier.MainShadowResolutionOf(quality), $"En {quality}.");
            }
        }

        [Test]
        public void TextureQualityMapsToMipLimitsInTheRightDirection()
        {
            Assert.AreEqual(0, BackroomsGraphicsApplier.MipmapLimitOf(TextureQuality.Full),
                "«Full» es la textura entera: límite de mip cero.");
            Assert.Greater(BackroomsGraphicsApplier.MipmapLimitOf(TextureQuality.Low),
                BackroomsGraphicsApplier.MipmapLimitOf(TextureQuality.High),
                "Menos calidad tiene que ser MÁS límite de mip, no menos.");
        }

        [Test]
        public void AnisotropicMapsToTheEngineModes()
        {
            Assert.AreEqual(AnisotropicFiltering.Disable,
                BackroomsGraphicsApplier.AnisotropicOf(AnisotropicMode.Disabled));
            Assert.AreEqual(AnisotropicFiltering.Enable,
                BackroomsGraphicsApplier.AnisotropicOf(AnisotropicMode.PerTexture));
            Assert.AreEqual(AnisotropicFiltering.ForceEnable,
                BackroomsGraphicsApplier.AnisotropicOf(AnisotropicMode.ForcedOn));
        }

        /// <summary>
        /// El otro espejo de `PC_RPAsset`, hermano del de los presets: en el escalón High, los dos
        /// atlas tienen que salir con los píxeles que el asset lleva commiteados. Si alguien afina
        /// el asset y no toca la tabla, aplicar «High» degradaría el aspecto validado.
        /// </summary>
        [Test]
        public void HighAtlasesMirrorTheRenderPipelineAssetInUse()
        {
            string path = Path.Combine(Application.dataPath, PcRenderPipelineAssetPath);
            if (!File.Exists(path))
                Assert.Ignore($"No encuentro {PcRenderPipelineAssetPath}; sin asset no hay espejo.");

            string yaml = File.ReadAllText(path);
            var high = GraphicsQualityPresets.Get(GraphicsPreset.High);

            Assert.AreEqual(ReadInt(yaml, "m_MainLightShadowmapResolution"),
                BackroomsGraphicsApplier.MainShadowResolutionOf(high.ShadowQuality),
                "El atlas principal de High ya no es el del asset.");
            Assert.AreEqual(ReadInt(yaml, "m_AdditionalLightsShadowmapResolution"),
                BackroomsGraphicsApplier.AdditionalShadowResolutionOf(high.ShadowQuality),
                "El atlas de luces adicionales de High ya no es el del asset.");
        }

        /// <summary>
        /// Que las conversiones sean correctas no basta: hay que ver que CADA número acaba en su
        /// campo. Se escribe sobre un asset de usar y tirar, nunca sobre el que usa el editor.
        /// </summary>
        [Test]
        public void ApplyingUltraWritesEveryBudgetOntoTheAsset()
        {
            var pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            try
            {
                var ultra = GraphicsQualityPresets.Get(GraphicsPreset.Ultra);
                BackroomsGraphicsApplier.ApplyToPipeline(pipeline, ultra);

                Assert.AreEqual(ultra.RenderScale, pipeline.renderScale, 0.001f);
                Assert.AreEqual(BackroomsGraphicsApplier.SampleCountOf(ultra.Msaa), pipeline.msaaSampleCount);
                Assert.AreEqual(BackroomsGraphicsApplier.FilterOf(ultra.Upscaling), pipeline.upscalingFilter);
                Assert.AreEqual(ultra.Hdr, pipeline.supportsHDR);
                Assert.AreEqual(ultra.ShadowDistance, pipeline.shadowDistance, 0.5f);
                Assert.AreEqual(ultra.ShadowCascades, pipeline.shadowCascadeCount);
                Assert.AreEqual(BackroomsGraphicsApplier.MainShadowResolutionOf(ultra.ShadowQuality),
                    pipeline.mainLightShadowmapResolution);
                Assert.AreEqual(BackroomsGraphicsApplier.AdditionalShadowResolutionOf(ultra.ShadowQuality),
                    pipeline.additionalLightsShadowmapResolution);
            }
            finally
            {
                Object.DestroyImmediate(pipeline);
            }
        }

        /// <summary>Apagar las sombras tiene que llegar al asset como distancia CERO.</summary>
        [Test]
        public void ApplyingWithShadowsOffZeroesTheAssetDistance()
        {
            var pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            try
            {
                BackroomsGraphicsApplier.ApplyToPipeline(pipeline,
                    WithShadows(GraphicsQualityPresets.Get(GraphicsPreset.High), false));

                Assert.AreEqual(0f, pipeline.shadowDistance, 0.001f);
            }
            finally
            {
                Object.DestroyImmediate(pipeline);
            }
        }

        /// <summary>
        /// La escala de render se recorta al rango del deslizador: un valor fuera de él no puede
        /// llegar al asset, porque URP lo aceptaría y el juego se dibujaría a otra resolución.
        /// </summary>
        [Test]
        public void RenderScaleIsClampedToTheSliderRange()
        {
            var pipeline = ScriptableObject.CreateInstance<UniversalRenderPipelineAsset>();
            try
            {
                var absurd = WithRenderScale(GraphicsQualityPresets.Get(GraphicsPreset.High), 12f);
                BackroomsGraphicsApplier.ApplyToPipeline(pipeline, absurd);

                Assert.LessOrEqual(pipeline.renderScale, BackroomsGraphicsOptions.MaxRenderScale + 0.001f);
            }
            finally
            {
                Object.DestroyImmediate(pipeline);
            }
        }

        private static GraphicsPresetValues WithRenderScale(in GraphicsPresetValues values, float renderScale) =>
            new GraphicsPresetValues(renderScale, values.Upscaling, values.Hdr, values.AntiAliasing,
                values.Msaa, values.Shadows, values.ShadowQuality, values.ShadowDistance, values.ShadowCascades,
                values.AdditionalLightShadows, values.Bloom, values.MotionBlur, values.DepthOfField,
                values.ChromaticAberration, values.TextureQuality, values.Anisotropic, values.LodBias);

        private static int ReadInt(string yaml, string field)
        {
            var match = Regex.Match(yaml, $@"^\s*{Regex.Escape(field)}:\s*(-?[\d.]+)\s*$", RegexOptions.Multiline);
            Assert.IsTrue(match.Success, $"El asset ya no tiene el campo '{field}'.");
            return Mathf.RoundToInt(float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        private static GraphicsPresetValues WithShadows(in GraphicsPresetValues values, bool shadows) =>
            new GraphicsPresetValues(values.RenderScale, values.Upscaling, values.Hdr, values.AntiAliasing,
                values.Msaa, shadows, values.ShadowQuality, values.ShadowDistance, values.ShadowCascades,
                values.AdditionalLightShadows, values.Bloom, values.MotionBlur, values.DepthOfField,
                values.ChromaticAberration, values.TextureQuality, values.Anisotropic, values.LodBias);
    }
}
