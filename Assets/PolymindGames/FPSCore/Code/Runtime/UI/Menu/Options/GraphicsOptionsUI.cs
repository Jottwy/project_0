using System.Collections.Generic;
using PolymindGames.Options;
using UnityEngine.UI;
using UnityEngine;
using System.Linq;
using System;
using TMPro;

namespace PolymindGames.UserInterface
{
    [RequireComponent(typeof(UIPanel))]
    public partial class GraphicsOptionsUI : UserOptionsUI<GraphicsOptions>
    {
        [SerializeField]
        private TMP_Dropdown _qualityDropdown; 
        
        [SerializeField]
        private TMP_Dropdown _fullscreenModeDropdown;

        [SerializeField]
        private TMP_Dropdown _resolutionDropdown;

        [SerializeField]
        private Slider _fieldOfView;
        
        [SerializeField]
        private Slider _frameRateCap;
        
        [SerializeField]
        private Toggle _vSyncToggle;

        private List<Resolution> _filteredResolutions;
        
        protected override void Start()
        {
            base.Start();
            InitializeFullscreenModeDropdown();
            InitializeFrameRateCapSlider();
            InitializeResolutionDropdown();
            InitializeFieldOfViewSlider();
            InitializeQualityDropdown();
            InitializeVSyncToggle();
        }

        protected override void ResetUIState()
        {
            _fullscreenModeDropdown.value = (int)UserOptions.FullscreenMode.Value.Value;
            _frameRateCap.value = UserOptions.FrameRateCap.Value;
            _resolutionDropdown.value = _filteredResolutions.FindIndex(resolution => resolution.width == UserOptions.Resolution.Value.x);
            _fieldOfView.value = UserOptions.FieldOfView.Value;
            _qualityDropdown.value = UserOptions.Quality.Value;
            _vSyncToggle.isOn = UserOptions.VSyncMode.Value > 0;
        }

        protected override void ApplyChanges()
        {
            UserOptions.FullscreenMode.SetValue((FullScreenMode)_fullscreenModeDropdown.value);
            UserOptions.FrameRateCap.SetValue((int)_frameRateCap.value);

            var resolution = _filteredResolutions[_resolutionDropdown.value];
            UserOptions.Resolution.SetValue(new Vector2Int(resolution.width, resolution.height));
            
            UserOptions.FieldOfView.SetValue(_fieldOfView.value);
            UserOptions.Quality.SetValue(_qualityDropdown.value);
            UserOptions.VSyncMode.SetValue(_vSyncToggle.isOn ? 1 : 0);
        }

        private void InitializeFullscreenModeDropdown()
        {
            var options = Enum.GetNames(typeof(FullScreenMode)).Select(str => str.AddSpaceBeforeCapitalLetters()).ToList();
            _fullscreenModeDropdown.ClearOptions();
            _fullscreenModeDropdown.AddOptions(options);
            _fullscreenModeDropdown.onValueChanged.AddListener(MarkDirty);
        }
        
        private void InitializeFrameRateCapSlider()
        {
            _frameRateCap.maxValue = GraphicsOptions.MaxFramerate;
            _frameRateCap.minValue = 0f;
            _frameRateCap.wholeNumbers = true;
            _frameRateCap.onValueChanged.AddListener(MarkDirty);
        }

        private void InitializeResolutionDropdown()
        {
            var resolutions = Screen.resolutions;
            var maxRefreshRate = resolutions.Max(resolution => resolution.refreshRateRatio).value;
            _filteredResolutions = resolutions.Where(resolution => Math.Abs(resolution.refreshRateRatio.value - maxRefreshRate) < 0.001f).ToList();
            
            var options = _filteredResolutions.Select(resolution => $"{resolution.width} x {resolution.height}").ToList();
            _resolutionDropdown.ClearOptions();
            _resolutionDropdown.AddOptions(options);
            _resolutionDropdown.onValueChanged.AddListener(MarkDirty);
        }

        private void InitializeFieldOfViewSlider()
        {
            _fieldOfView.minValue = GraphicsOptions.MinFOV;
            _fieldOfView.maxValue = GraphicsOptions.MaxFOV;
            _fieldOfView.wholeNumbers = true;
            _fieldOfView.onValueChanged.AddListener(MarkDirty);
        }

        private void InitializeQualityDropdown()
        {
            _qualityDropdown.ClearOptions();
            _qualityDropdown.AddOptions(QualitySettings.names.ToList());
            _qualityDropdown.onValueChanged.AddListener(MarkDirty);
        }

        private void InitializeVSyncToggle()
        {
            _vSyncToggle.onValueChanged.AddListener(MarkDirty);
        }
    }
}