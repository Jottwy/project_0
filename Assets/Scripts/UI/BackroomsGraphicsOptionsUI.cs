using System.Collections.Generic;
using BackroomsSurvival.Gameplay;
using PolymindGames.UserInterface;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// Las filas visuales que faltaban, DENTRO de la pestaña Graphics — mismo montaje que la voz
    /// en la de Audio (ADR-046): segundo componente del mismo panel que <c>GraphicsOptionsUI</c>,
    /// compartiendo sus botones "Apply" y "Restore", así que los dos se aplican juntos.
    ///
    /// EL CONTRATO DEL DESPLEGABLE GLOBAL, que es lo único con lógica real aquí: elegir un escalón
    /// ESCRIBE las filas de detalle; tocar cualquier detalle recalcula el escalón y, si ya no
    /// coincide con ninguno, el desplegable pasa a "Custom". Un menú donde el global y los
    /// detalles pueden contradecirse es peor que no tener global.
    ///
    /// <b>NADA DE ESTO CAMBIA TODAVÍA UN PÍXEL.</b> <see cref="BackroomsGraphicsOptions"/> guarda
    /// y no aplica: el aplicador (URP Asset, cámara y Volume) es la tanda siguiente, con ADR.
    ///
    /// ETIQUETAS EN INGLÉS por decisión de Joel. Las filas de voz siguen en español: la pestaña
    /// Audio queda mezclada hasta que se traduzcan.
    ///
    /// TODOS LOS CAMPOS SON OPCIONALES: el panel lo monta un script de editor y un montaje a
    /// medias no puede tirar excepciones por cada control que falte.
    /// </summary>
    public sealed class BackroomsGraphicsOptionsUI : UserOptionsUI<BackroomsGraphicsOptions>
    {
        [Header("Preset")]
        [SerializeField] private TMP_Dropdown _presetDropdown;

        [Header("Rendering")]
        [SerializeField] private Slider _renderScaleSlider;
        [SerializeField] private TMP_Dropdown _upscalingDropdown;
        [SerializeField] private Toggle _hdrToggle;
        [SerializeField] private TMP_Dropdown _antiAliasingDropdown;
        [SerializeField] private TMP_Dropdown _msaaDropdown;

        [Header("Shadows")]
        [SerializeField] private Toggle _shadowsToggle;
        [SerializeField] private TMP_Dropdown _shadowQualityDropdown;
        [SerializeField] private Slider _shadowDistanceSlider;
        [SerializeField] private TMP_Dropdown _shadowCascadesDropdown;
        [SerializeField] private Toggle _additionalLightShadowsToggle;

        [Header("Post-processing")]
        [SerializeField] private Toggle _bloomToggle;
        [SerializeField] private Toggle _motionBlurToggle;
        [SerializeField] private Toggle _depthOfFieldToggle;
        [SerializeField] private Toggle _chromaticAberrationToggle;

        [Header("Textures & detail")]
        [SerializeField] private TMP_Dropdown _textureQualityDropdown;
        [SerializeField] private TMP_Dropdown _anisotropicDropdown;
        [SerializeField] private Slider _lodBiasSlider;

        private static readonly string[] UpscalingLabels = { "Off", "FSR 1.0", "STP" };
        private static readonly string[] AntiAliasingLabels = { "Off", "FXAA", "SMAA", "TAA" };
        private static readonly string[] MsaaLabels = { "Off", "2x", "4x", "8x" };
        private static readonly string[] ShadowQualityLabels = { "Low", "Medium", "High", "Ultra" };
        private static readonly string[] CascadeLabels = { "1", "2", "3", "4" };
        private static readonly string[] TextureQualityLabels = { "Low", "Medium", "High", "Full" };
        private static readonly string[] AnisotropicLabels = { "Disabled", "Per Texture", "Forced On" };

        /// <summary>
        /// Mientras esto vale true, escribir en un control NO cuenta como que el jugador lo tocó.
        ///
        /// SEGUNDA LÍNEA DE DEFENSA, no la primera: la primera es que todo se escribe con
        /// <c>SetValueWithoutNotify</c>. Esta bandera sola NO bastaba — en Play se cazó un aviso
        /// de desplegable con la bandera ya bajada, y de ahí salía el vuelco a Ultra.
        /// </summary>
        private bool _writingWidgets;

        protected override void Start()
        {
            base.Start();

            FillDropdown(_presetDropdown, GraphicsQualityPresets.Labels);
            FillDropdown(_upscalingDropdown, UpscalingLabels);
            FillDropdown(_antiAliasingDropdown, AntiAliasingLabels);
            FillDropdown(_msaaDropdown, MsaaLabels);
            FillDropdown(_shadowQualityDropdown, ShadowQualityLabels);
            FillDropdown(_shadowCascadesDropdown, CascadeLabels);
            FillDropdown(_textureQualityDropdown, TextureQualityLabels);
            FillDropdown(_anisotropicDropdown, AnisotropicLabels);

            SetupSlider(_renderScaleSlider, BackroomsGraphicsOptions.MinRenderScale,
                BackroomsGraphicsOptions.MaxRenderScale, false);
            SetupSlider(_shadowDistanceSlider, BackroomsGraphicsOptions.MinShadowDistance,
                BackroomsGraphicsOptions.MaxShadowDistance, true);
            SetupSlider(_lodBiasSlider, BackroomsGraphicsOptions.MinLodBias,
                BackroomsGraphicsOptions.MaxLodBias, false);

            if (_presetDropdown != null)
                _presetDropdown.onValueChanged.AddListener(OnPresetPicked);

            HookDetail(_upscalingDropdown);
            HookDetail(_antiAliasingDropdown);
            HookDetail(_msaaDropdown);
            HookDetail(_shadowQualityDropdown);
            HookDetail(_shadowCascadesDropdown);
            HookDetail(_textureQualityDropdown);
            HookDetail(_anisotropicDropdown);

            HookDetail(_hdrToggle);
            HookDetail(_shadowsToggle);
            HookDetail(_additionalLightShadowsToggle);
            HookDetail(_bloomToggle);
            HookDetail(_motionBlurToggle);
            HookDetail(_depthOfFieldToggle);
            HookDetail(_chromaticAberrationToggle);

            HookDetail(_renderScaleSlider);
            HookDetail(_shadowDistanceSlider);
            HookDetail(_lodBiasSlider);

            ResetUIState();
        }

        /// <inheritdoc/>
        protected override void ResetUIState()
        {
            WriteWidgets(UserOptions.ToValues());

            SetDropdown(_presetDropdown, UserOptions.Preset.Value);
        }

        /// <inheritdoc/>
        protected override void ApplyChanges() => UserOptions.SetValues(ReadWidgets());

        /// <summary>
        /// Elegir un escalón vuelca su fila entera sobre los controles. "Custom" no es un escalón:
        /// elegirlo a mano no cambia ningún detalle, sólo deja de decir que son los de un preset.
        /// </summary>
        private void OnPresetPicked(int index)
        {
            if (_writingWidgets)
                return;

            if (index < GraphicsQualityPresets.PresetCount)
                WriteWidgets(GraphicsQualityPresets.Get(index));

            MarkDirty();
        }

        /// <summary>Tocar un detalle recalcula el escalón; casi siempre acaba en "Custom".</summary>
        private void OnDetailChanged()
        {
            if (_writingWidgets)
                return;

            SetDropdown(_presetDropdown, (int)GraphicsQualityPresets.Match(ReadWidgets()));
            MarkDirty();
        }

        private void WriteWidgets(in GraphicsPresetValues values)
        {
            _writingWidgets = true;

            SetSlider(_renderScaleSlider, values.RenderScale);
            SetDropdown(_upscalingDropdown, (int)values.Upscaling);
            SetToggle(_hdrToggle, values.Hdr);
            SetDropdown(_antiAliasingDropdown, (int)values.AntiAliasing);
            SetDropdown(_msaaDropdown, (int)values.Msaa);

            SetToggle(_shadowsToggle, values.Shadows);
            SetDropdown(_shadowQualityDropdown, (int)values.ShadowQuality);
            SetSlider(_shadowDistanceSlider, values.ShadowDistance);
            SetDropdown(_shadowCascadesDropdown, values.ShadowCascades - 1);
            SetToggle(_additionalLightShadowsToggle, values.AdditionalLightShadows);

            SetToggle(_bloomToggle, values.Bloom);
            SetToggle(_motionBlurToggle, values.MotionBlur);
            SetToggle(_depthOfFieldToggle, values.DepthOfField);
            SetToggle(_chromaticAberrationToggle, values.ChromaticAberration);

            SetDropdown(_textureQualityDropdown, (int)values.TextureQuality);
            SetDropdown(_anisotropicDropdown, (int)values.Anisotropic);
            SetSlider(_lodBiasSlider, values.LodBias);

            _writingWidgets = false;
        }

        /// <summary>
        /// Lo que dicen los controles. Un control que no exista NO inventa un valor: devuelve el
        /// que ya está guardado, para que un panel montado a medias no baje ajustes en silencio.
        /// </summary>
        private GraphicsPresetValues ReadWidgets()
        {
            var saved = UserOptions.ToValues();

            return new GraphicsPresetValues(
                Read(_renderScaleSlider, saved.RenderScale),
                (UpscalingMode)Read(_upscalingDropdown, (int)saved.Upscaling),
                Read(_hdrToggle, saved.Hdr),
                (AntiAliasingMode)Read(_antiAliasingDropdown, (int)saved.AntiAliasing),
                (MsaaMode)Read(_msaaDropdown, (int)saved.Msaa),
                Read(_shadowsToggle, saved.Shadows),
                (ShadowQualityLevel)Read(_shadowQualityDropdown, (int)saved.ShadowQuality),
                Read(_shadowDistanceSlider, saved.ShadowDistance),
                Read(_shadowCascadesDropdown, saved.ShadowCascades - 1) + 1,
                Read(_additionalLightShadowsToggle, saved.AdditionalLightShadows),
                Read(_bloomToggle, saved.Bloom),
                Read(_motionBlurToggle, saved.MotionBlur),
                Read(_depthOfFieldToggle, saved.DepthOfField),
                Read(_chromaticAberrationToggle, saved.ChromaticAberration),
                (TextureQuality)Read(_textureQualityDropdown, (int)saved.TextureQuality),
                (AnisotropicMode)Read(_anisotropicDropdown, (int)saved.Anisotropic),
                Read(_lodBiasSlider, saved.LodBias));
        }

        private static void FillDropdown(TMP_Dropdown dropdown, IReadOnlyList<string> labels)
        {
            if (dropdown == null)
                return;

            var options = new List<string>(labels.Count);
            for (int i = 0; i < labels.Count; i++)
                options.Add(labels[i]);

            dropdown.ClearOptions();
            dropdown.AddOptions(options);
        }

        private static void SetupSlider(Slider slider, float min, float max, bool wholeNumbers)
        {
            if (slider == null)
                return;

            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = wholeNumbers;
        }

        private void HookDetail(TMP_Dropdown dropdown)
        {
            if (dropdown != null)
                dropdown.onValueChanged.AddListener(_ => OnDetailChanged());
        }

        private void HookDetail(Toggle toggle)
        {
            if (toggle != null)
                toggle.onValueChanged.AddListener(_ => OnDetailChanged());
        }

        private void HookDetail(Slider slider)
        {
            if (slider != null)
                slider.onValueChanged.AddListener(_ => OnDetailChanged());
        }

        // TODO ESTO ESCRIBE *WithoutNotify*, y no es cosmetico: es lo unico que corta el bucle
        // que se cazo en Play. Un desplegable puede avisar de un cambio DESPUES de que la bandera
        // `_writingWidgets` se haya bajado -- se vio con `writing=False` sin que nadie tocara nada.
        // Ese aviso recalculaba el preset a Custom, el desplegable global rebotaba al ultimo
        // escalon (Ultra) y ese rebote volcaba Ultra sobre las diecisiete filas; un Apply despues,
        // quedaba guardado. Escribiendo sin avisar, el bucle no puede empezar.
        private static void SetDropdown(TMP_Dropdown dropdown, int value)
        {
            if (dropdown == null)
                return;

            dropdown.SetValueWithoutNotify(Mathf.Clamp(value, 0, Mathf.Max(0, dropdown.options.Count - 1)));
            dropdown.RefreshShownValue();
        }

        private static void SetToggle(Toggle toggle, bool value)
        {
            if (toggle != null)
                toggle.SetIsOnWithoutNotify(value);
        }

        private static void SetSlider(Slider slider, float value)
        {
            if (slider != null)
                slider.SetValueWithoutNotify(Mathf.Clamp(value, slider.minValue, slider.maxValue));
        }

        private static int Read(TMP_Dropdown dropdown, int fallback) => dropdown != null ? dropdown.value : fallback;

        private static bool Read(Toggle toggle, bool fallback) => toggle != null ? toggle.isOn : fallback;

        private static float Read(Slider slider, float fallback) => slider != null ? slider.value : fallback;
    }
}
