using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using BackroomsSurvival.Gameplay;
using NUnit.Framework;
using UnityEngine;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// El menú de calidad gráfica: la tabla de los seis escalones y el contrato del desplegable.
    ///
    /// Lo que se prueba aquí no es decorado. El desplegable global es la ÚNICA fila que mueve a
    /// las demás, así que si la tabla y el reconocimiento del escalón no cuadran, el menú miente:
    /// dice "High" sobre ajustes que ya no son High, o se pone en "Custom" nada más elegir uno.
    ///
    /// Y el escalón High es un ESPEJO de <c>PC_RPAsset</c>, que hasta ahora es un espejo sin
    /// oráculo — el mismo problema anotado en STATE para <c>scale</c> y <c>density</c>. Aquí el
    /// oráculo es el propio asset: el test lo lee y lo compara.
    /// </summary>
    [TestFixture]
    public class GraphicsQualityPresetsTests
    {
        /// <summary>El asset de URP que sirve HOY al juego (ADR-065, Forward+).</summary>
        private const string PcRenderPipelineAssetPath = "Settings/PC_RPAsset.asset";

        [Test]
        public void EveryPresetRecognizesItself()
        {
            for (int i = 0; i < GraphicsQualityPresets.PresetCount; i++)
            {
                var values = GraphicsQualityPresets.Get(i);
                Assert.AreEqual((GraphicsPreset)i, GraphicsQualityPresets.Match(values),
                    $"El escalón {GraphicsQualityPresets.Labels[i]} no se reconoce a sí mismo.");
            }
        }

        [Test]
        public void LabelsCoverEveryPresetPlusCustom()
        {
            Assert.AreEqual(GraphicsQualityPresets.PresetCount + 1, GraphicsQualityPresets.Labels.Length,
                "El desplegable necesita una etiqueta por escalón más la de Custom.");
            Assert.AreEqual("Custom", GraphicsQualityPresets.Labels[GraphicsQualityPresets.PresetCount]);
        }

        /// <summary>
        /// Tocar UN detalle tiene que sacar del escalón. Si un cambio pasara desapercibido, el
        /// jugador vería "High" sobre unos ajustes que ya no lo son.
        /// </summary>
        [Test]
        public void ChangingOneDetailFallsToCustom()
        {
            var high = GraphicsQualityPresets.Get(GraphicsPreset.High);

            var withOtherShadows = WithShadowDistance(high, high.ShadowDistance + 25f);
            Assert.AreEqual(GraphicsPreset.Custom, GraphicsQualityPresets.Match(withOtherShadows));

            var withOtherAa = WithAntiAliasing(high, AntiAliasingMode.Off);
            Assert.AreEqual(GraphicsPreset.Custom, GraphicsQualityPresets.Match(withOtherAa));
        }

        /// <summary>Un slider devuelve 0,9999998: la comparación tiene que aguantarlo.</summary>
        [Test]
        public void FloatNoiseStillMatchesThePreset()
        {
            var high = GraphicsQualityPresets.Get(GraphicsPreset.High);
            var noisy = WithShadowDistance(high, high.ShadowDistance + 0.0001f);
            Assert.AreEqual(GraphicsPreset.High, GraphicsQualityPresets.Match(noisy));
        }

        /// <summary>
        /// Los escalones van de menos a más. No es cosmético: un preset "Low" que pidiera más que
        /// "Medium" en cualquier eje haría que bajar la calidad costara más fotogramas.
        /// </summary>
        [Test]
        public void PresetsNeverGetCheaperAsTheyGoUp()
        {
            for (int i = 1; i < GraphicsQualityPresets.PresetCount; i++)
            {
                var lower = GraphicsQualityPresets.Get(i - 1);
                var upper = GraphicsQualityPresets.Get(i);
                string what = $"{GraphicsQualityPresets.Labels[i - 1]} → {GraphicsQualityPresets.Labels[i]}";

                Assert.LessOrEqual(lower.RenderScale, upper.RenderScale, $"Render scale baja en {what}");
                Assert.LessOrEqual(lower.ShadowDistance, upper.ShadowDistance, $"Distancia de sombra baja en {what}");
                Assert.LessOrEqual(lower.ShadowCascades, upper.ShadowCascades, $"Cascadas bajan en {what}");
                Assert.LessOrEqual((int)lower.Msaa, (int)upper.Msaa, $"MSAA baja en {what}");
                Assert.LessOrEqual((int)lower.ShadowQuality, (int)upper.ShadowQuality, $"Calidad de sombra baja en {what}");
                Assert.LessOrEqual((int)lower.TextureQuality, (int)upper.TextureQuality, $"Calidad de textura baja en {what}");
                Assert.LessOrEqual(lower.LodBias, upper.LodBias, $"LOD bias baja en {what}");
                Assert.IsFalse(lower.Shadows && !upper.Shadows, $"Se pierden las sombras en {what}");
            }
        }

        /// <summary>
        /// EL ESPEJO: "High" es el defecto y tiene que describir lo que el juego renderiza HOY, o
        /// el día que llegue el aplicador el jugador que no toque nada verá cambiar el aspecto.
        /// </summary>
        [Test]
        public void HighMirrorsTheRenderPipelineAssetInUse()
        {
            string path = Path.Combine(Application.dataPath, PcRenderPipelineAssetPath);
            if (!File.Exists(path))
                Assert.Ignore($"No encuentro {PcRenderPipelineAssetPath}; sin asset no hay espejo que comparar.");

            string yaml = File.ReadAllText(path);
            var high = GraphicsQualityPresets.Get(GraphicsPreset.High);

            Assert.AreEqual(SampleCountOf(high.Msaa), ReadInt(yaml, "m_MSAA"),
                "El MSAA del escalón High ya no es el del asset.");
            Assert.AreEqual(high.ShadowDistance, ReadInt(yaml, "m_ShadowDistance"), 0.5f,
                "La distancia de sombra del escalón High ya no es la del asset.");
            Assert.AreEqual(high.ShadowCascades, ReadInt(yaml, "m_ShadowCascadeCount"),
                "Las cascadas del escalón High ya no son las del asset.");
            Assert.AreEqual(high.AdditionalLightShadows, ReadInt(yaml, "m_AdditionalLightShadowsSupported") == 1,
                "Las sombras de luces adicionales del escalón High ya no son las del asset.");
            Assert.AreEqual(high.Hdr, ReadInt(yaml, "m_SupportsHDR") == 1,
                "El HDR del escalón High ya no es el del asset.");
            Assert.AreEqual(high.RenderScale, ReadFloat(yaml, "m_RenderScale"), 0.005f,
                "La escala de render del escalón High ya no es la del asset.");
        }

        /// <summary>Guardar unos valores deja el escalón que les corresponde, no el que había.</summary>
        [Test]
        public void SetValuesStoresTheMatchingPreset()
        {
            var options = ScriptableObject.CreateInstance<BackroomsGraphicsOptions>();
            try
            {
                options.SetValues(GraphicsQualityPresets.Get(GraphicsPreset.Ultra));
                Assert.AreEqual((int)GraphicsPreset.Ultra, options.Preset.Value);
                Assert.AreEqual(GraphicsPreset.Ultra, GraphicsQualityPresets.Match(options.ToValues()));

                options.SetValues(WithShadowDistance(GraphicsQualityPresets.Get(GraphicsPreset.Ultra), 65f));
                Assert.AreEqual((int)GraphicsPreset.Custom, options.Preset.Value);
                Assert.AreEqual(65f, options.ShadowDistance.Value, 0.01f);
            }
            finally
            {
                Object.DestroyImmediate(options);
            }
        }

        /// <summary>Los valores guardados sobreviven al viaje de ida y vuelta por la estructura.</summary>
        [Test]
        public void ValuesSurviveTheRoundTrip()
        {
            var options = ScriptableObject.CreateInstance<BackroomsGraphicsOptions>();
            try
            {
                var source = GraphicsQualityPresets.Get(GraphicsPreset.VeryLow);
                options.SetValues(source);
                var back = options.ToValues();

                Assert.AreEqual(source.RenderScale, back.RenderScale, 0.001f);
                Assert.AreEqual(source.Upscaling, back.Upscaling);
                Assert.AreEqual(source.Shadows, back.Shadows);
                Assert.AreEqual(source.ShadowCascades, back.ShadowCascades);
                Assert.AreEqual(source.TextureQuality, back.TextureQuality);
                Assert.AreEqual(source.Anisotropic, back.Anisotropic);
                Assert.AreEqual(source.LodBias, back.LodBias, 0.001f);
            }
            finally
            {
                Object.DestroyImmediate(options);
            }
        }

        private static int SampleCountOf(MsaaMode msaa) => msaa switch
        {
            MsaaMode.X2 => 2,
            MsaaMode.X4 => 4,
            MsaaMode.X8 => 8,
            _ => 1
        };

        private static int ReadInt(string yaml, string field) => Mathf.RoundToInt(ReadFloat(yaml, field));

        private static float ReadFloat(string yaml, string field)
        {
            var match = Regex.Match(yaml, $@"^\s*{Regex.Escape(field)}:\s*(-?[\d.]+)\s*$", RegexOptions.Multiline);
            Assert.IsTrue(match.Success, $"El asset ya no tiene el campo '{field}'.");
            return float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        private static GraphicsPresetValues WithShadowDistance(in GraphicsPresetValues values, float distance) =>
            new GraphicsPresetValues(values.RenderScale, values.Upscaling, values.Hdr, values.AntiAliasing,
                values.Msaa, values.Shadows, values.ShadowQuality, distance, values.ShadowCascades,
                values.AdditionalLightShadows, values.Bloom, values.MotionBlur, values.DepthOfField,
                values.ChromaticAberration, values.TextureQuality, values.Anisotropic, values.LodBias);

        private static GraphicsPresetValues WithAntiAliasing(in GraphicsPresetValues values, AntiAliasingMode mode) =>
            new GraphicsPresetValues(values.RenderScale, values.Upscaling, values.Hdr, mode,
                values.Msaa, values.Shadows, values.ShadowQuality, values.ShadowDistance, values.ShadowCascades,
                values.AdditionalLightShadows, values.Bloom, values.MotionBlur, values.DepthOfField,
                values.ChromaticAberration, values.TextureQuality, values.Anisotropic, values.LodBias);
    }
}
