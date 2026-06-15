using PolymindGames.Options;
using UnityEngine.UI;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    public partial class AudioOptionsUI : UserOptionsUI<AudioOptions>
    {
        [SerializeField]
        private Slider _masterVolumeSlider;
        
        [SerializeField]
        private Slider _effectsVolumeSlider;
        
        [SerializeField]
        private Slider _ambienceVolumeSlider;
        
        [SerializeField] 
        private Slider _musicVolumeSlider;
        
        [SerializeField]
        private Slider _uiVolumeSlider;

        protected override void Start()
        {
            base.Start();
            
            _masterVolumeSlider.onValueChanged.AddListener(value => UserOptions.MasterVolume.SetValue(value * 0.01f));
            _effectsVolumeSlider.onValueChanged.AddListener(value => UserOptions.EffectsVolume.SetValue(value * 0.01f));
            _ambienceVolumeSlider.onValueChanged.AddListener(value => UserOptions.AmbienceVolume.SetValue(value * 0.01f));
            _musicVolumeSlider.onValueChanged.AddListener(value => UserOptions.MusicVolume.SetValue(value * 0.01f));
            _uiVolumeSlider.onValueChanged.AddListener(value => UserOptions.UIVolume.SetValue(value * 0.01f));
        }

        protected override void ResetUIState()
        {
            _masterVolumeSlider.value = UserOptions.MasterVolume * 100f;
            _effectsVolumeSlider.value = UserOptions.EffectsVolume * 100f;
            _ambienceVolumeSlider.value = UserOptions.AmbienceVolume * 100f;
            _musicVolumeSlider.value = UserOptions.MusicVolume * 100f;
            _uiVolumeSlider.value = UserOptions.UIVolume * 100f;
        }
    }
}