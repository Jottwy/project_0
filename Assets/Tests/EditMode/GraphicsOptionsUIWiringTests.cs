using System.Collections.Generic;
using System.Reflection;
using BackroomsSurvival.Gameplay;
using BackroomsSurvival.UI;
using NUnit.Framework;
using PolymindGames.Options;
using PolymindGames.UserInterface;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.Tests
{
    /// <summary>
    /// La red que faltaba: el bug del 2026-09-08 (los ajustes gráficos se volcaban solos a Ultra,
    /// o a los máximos de cada rango) no lo cazó ningún test — hizo falta una partida instrumentada
    /// con traza a fichero. Costó una tarde de editor compartido, así que aquí queda cubierto.
    ///
    /// LO QUE SE PRUEBA ES EL CONTRATO ENTRE EL CÓDIGO Y LOS CONTROLES, no el aspecto:
    /// escribir en las filas desde código NO puede disparar los avisos de uGUI, porque ese aviso
    /// era el principio del bucle — recalculaba el preset a Custom, el desplegable global rebotaba
    /// al último escalón y el rebote volcaba Ultra sobre las diecisiete filas.
    ///
    /// El singleton de opciones se sustituye a mano: <see cref="UserOptions{T}.Instance"/> lee y
    /// ESCRIBE un JSON en la carpeta de datos del usuario, y un test no tiene por qué tocar los
    /// ajustes de quien lo corre.
    /// </summary>
    [TestFixture]
    public class GraphicsOptionsUIWiringTests
    {
        private GameObject _root;
        private BackroomsGraphicsOptionsUI _ui;
        private BackroomsGraphicsOptions _options;
        private BackroomsGraphicsOptions _previousInstance;

        private readonly Dictionary<string, TMP_Dropdown> _dropdowns = new();
        private readonly Dictionary<string, Toggle> _toggles = new();
        private readonly Dictionary<string, Slider> _sliders = new();

        /// <summary>Cuántos avisos han emitido los controles desde el último <see cref="ResetNotifications"/>.</summary>
        private int _notifications;

        private static readonly string[] DropdownFields =
        {
            "_presetDropdown", "_upscalingDropdown", "_antiAliasingDropdown", "_msaaDropdown",
            "_shadowQualityDropdown", "_shadowCascadesDropdown", "_textureQualityDropdown",
            "_anisotropicDropdown"
        };

        private static readonly string[] ToggleFields =
        {
            "_hdrToggle", "_shadowsToggle", "_additionalLightShadowsToggle", "_bloomToggle",
            "_motionBlurToggle", "_depthOfFieldToggle", "_chromaticAberrationToggle"
        };

        private static readonly string[] SliderFields =
        {
            "_renderScaleSlider", "_shadowDistanceSlider", "_lodBiasSlider"
        };

        [SetUp]
        public void SetUp()
        {
            _options = ScriptableObject.CreateInstance<BackroomsGraphicsOptions>();
            _options.SetValues(GraphicsQualityPresets.Get(GraphicsPreset.High));
            _previousInstance = SwapSingleton(_options);

            _root = new GameObject("GfxOptionsUI", typeof(TestPanel));
            _ui = _root.AddComponent<BackroomsGraphicsOptionsUI>();

            foreach (string field in DropdownFields)
                _dropdowns[field] = Wire<TMP_Dropdown>(field);

            foreach (string field in ToggleFields)
                _toggles[field] = Wire<Toggle>(field);

            foreach (string field in SliderFields)
                _sliders[field] = Wire<Slider>(field);

            Invoke("Start");
            ResetNotifications();
        }

        [TearDown]
        public void TearDown()
        {
            SwapSingleton(_previousInstance);

            if (_root != null)
                Object.DestroyImmediate(_root);

            if (_options != null)
                Object.DestroyImmediate(_options);

            _dropdowns.Clear();
            _toggles.Clear();
            _sliders.Clear();
        }

        /// <summary>
        /// EL TEST DEL BUG. Volcar un preset escribe las diecisiete filas; si alguna de esas
        /// escrituras avisa, el aviso vuelve por <c>OnDetailChanged</c>, el preset pasa a Custom y
        /// el desplegable global rebota. Cero avisos es la única cifra que corta el bucle.
        /// </summary>
        [Test]
        public void WritingWidgetsFromCodeNotifiesNobody()
        {
            Invoke("ResetUIState");
            Assert.AreEqual(0, _notifications, "Escribir los valores guardados disparó avisos de uGUI.");

            _dropdowns["_presetDropdown"].onValueChanged.Invoke((int)GraphicsPreset.VeryLow);
            ResetNotifications();
            Invoke("ResetUIState");
            Assert.AreEqual(0, _notifications, "Volcar un preset disparó avisos de uGUI.");
        }

        /// <summary>
        /// La otra huella del mismo fallo: todos los campos en el MÁXIMO de su rango, que es lo que
        /// queda si se leen los controles antes de escribirles lo guardado.
        /// </summary>
        [Test]
        public void StartLeavesTheWidgetsOnTheSavedValues()
        {
            var high = GraphicsQualityPresets.Get(GraphicsPreset.High);

            Assert.AreEqual(high.RenderScale, _sliders["_renderScaleSlider"].value, 0.001f);
            Assert.AreEqual(high.ShadowDistance, _sliders["_shadowDistanceSlider"].value, 0.5f);
            Assert.AreEqual(high.LodBias, _sliders["_lodBiasSlider"].value, 0.001f);
            Assert.AreEqual((int)high.Msaa, _dropdowns["_msaaDropdown"].value);
            Assert.AreEqual((int)high.AntiAliasing, _dropdowns["_antiAliasingDropdown"].value);
            Assert.AreEqual((int)GraphicsPreset.High, _dropdowns["_presetDropdown"].value);
            Assert.IsFalse(_toggles["_motionBlurToggle"].isOn, "High no lleva desenfoque de movimiento.");

            Assert.AreNotEqual(_sliders["_renderScaleSlider"].maxValue, _sliders["_renderScaleSlider"].value,
                "Los controles se quedaron en el máximo de su rango: es el síntoma del bug de 2026-09-08.");
        }

        /// <summary>Elegir un escalón escribe sus valores en todas las filas.</summary>
        [Test]
        public void PickingAPresetWritesItsValues()
        {
            _dropdowns["_presetDropdown"].onValueChanged.Invoke((int)GraphicsPreset.VeryLow);

            var veryLow = GraphicsQualityPresets.Get(GraphicsPreset.VeryLow);
            Assert.AreEqual(veryLow.RenderScale, _sliders["_renderScaleSlider"].value, 0.001f);
            Assert.AreEqual((int)veryLow.Upscaling, _dropdowns["_upscalingDropdown"].value);
            Assert.IsFalse(_toggles["_shadowsToggle"].isOn, "Very Low va sin sombras.");
        }

        /// <summary>
        /// Tocar UNA fila deja el desplegable en Custom y NO toca las demás. Antes del arreglo, ese
        /// recálculo rebotaba y reescribía las dieciséis restantes con otro escalón entero.
        /// </summary>
        [Test]
        public void ChangingOneRowFallsToCustomAndLeavesTheOthersAlone()
        {
            float renderScale = _sliders["_renderScaleSlider"].value;
            float shadowDistance = _sliders["_shadowDistanceSlider"].value;
            float lodBias = _sliders["_lodBiasSlider"].value;
            int antiAliasing = _dropdowns["_antiAliasingDropdown"].value;
            bool depthOfField = _toggles["_depthOfFieldToggle"].isOn;

            // Como lo haría el jugador: el control avisa, y el aviso entra por el mismo camino.
            var msaa = _dropdowns["_msaaDropdown"];
            msaa.value = (int)MsaaMode.X8;
            msaa.onValueChanged.Invoke(msaa.value);

            Assert.AreEqual((int)GraphicsPreset.Custom, _dropdowns["_presetDropdown"].value,
                "Tocar una fila tiene que dejar el desplegable global en Custom.");

            Assert.AreEqual(renderScale, _sliders["_renderScaleSlider"].value, 0.001f, "Rebote sobre Render Scale.");
            Assert.AreEqual(shadowDistance, _sliders["_shadowDistanceSlider"].value, 0.001f, "Rebote sobre Shadow Distance.");
            Assert.AreEqual(lodBias, _sliders["_lodBiasSlider"].value, 0.001f, "Rebote sobre LOD Bias.");
            Assert.AreEqual(antiAliasing, _dropdowns["_antiAliasingDropdown"].value, "Rebote sobre Anti-Aliasing.");
            Assert.AreEqual(depthOfField, _toggles["_depthOfFieldToggle"].isOn, "Rebote sobre Depth of Field.");
        }

        /// <summary>
        /// El desplegable global tiene siete entradas y la séptima es Custom: si tuviera seis, el
        /// intento de mostrar Custom se recortaría al último escalón — que es exactamente cómo el
        /// bug acababa en Ultra.
        /// </summary>
        [Test]
        public void ThePresetDropdownCanHoldCustom()
        {
            Assert.AreEqual(GraphicsQualityPresets.Labels.Length, _dropdowns["_presetDropdown"].options.Count);
            Assert.Greater(_dropdowns["_presetDropdown"].options.Count, (int)GraphicsPreset.Custom,
                "Sin hueco para Custom, el desplegable se recorta a Ultra y arrastra las demás filas.");
        }

        /// <summary>Aplicar guarda lo que enseñan los controles, ni más ni menos.</summary>
        [Test]
        public void ApplyChangesSavesWhatTheWidgetsShow()
        {
            _sliders["_shadowDistanceSlider"].value = 90f;
            _toggles["_bloomToggle"].isOn = false;

            Invoke("ApplyChanges");

            Assert.AreEqual(90f, _options.ShadowDistance.Value, 0.5f);
            Assert.IsFalse(_options.Bloom.Value);
            Assert.AreEqual((int)GraphicsPreset.Custom, _options.Preset.Value);
        }

        private T Wire<T>(string field) where T : Component
        {
            var go = new GameObject(field, typeof(RectTransform));
            go.transform.SetParent(_root.transform, false);
            var control = go.AddComponent<T>();

            switch (control)
            {
                case TMP_Dropdown dropdown: dropdown.onValueChanged.AddListener(_ => _notifications++); break;
                case Toggle toggle: toggle.onValueChanged.AddListener(_ => _notifications++); break;
                case Slider slider: slider.onValueChanged.AddListener(_ => _notifications++); break;
            }

            var info = typeof(BackroomsGraphicsOptionsUI)
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(info, $"El campo '{field}' ya no existe en la UI.");
            info.SetValue(_ui, control);

            return control;
        }

        private void ResetNotifications() => _notifications = 0;

        private void Invoke(string method)
        {
            var info = typeof(BackroomsGraphicsOptionsUI).GetMethod(method,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            Assert.IsNotNull(info, $"El método '{method}' ya no existe en la UI.");
            info.Invoke(_ui, null);
        }

        /// <summary>
        /// Cambia el singleton de opciones y devuelve el que había. Sin esto, el test leería y
        /// escribiría el fichero de opciones de la máquina donde corre.
        /// </summary>
        private static BackroomsGraphicsOptions SwapSingleton(BackroomsGraphicsOptions instance)
        {
            var field = typeof(UserOptions<BackroomsGraphicsOptions>)
                .GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "El vendor cambió el nombre del campo del singleton.");

            var previous = (BackroomsGraphicsOptions)field.GetValue(null);
            field.SetValue(null, instance);
            return previous;
        }

        /// <summary>
        /// <see cref="UIPanel"/> es abstracta y <see cref="UserOptionsUI{T}"/> la exige. Esta es la
        /// mínima que compila: el test no muestra ni oculta nada, sólo necesita que el componente
        /// exista para que el <c>Start</c> del vendor pueda suscribirse.
        /// </summary>
        private sealed class TestPanel : UIPanel
        {
            protected override void OnVisibilityChanged(bool show) { }
        }
    }
}
