using UnityEngine;

namespace PolymindGames.Options
{
    [CreateAssetMenu(menuName = CreateMenuPath + "Audio Options", fileName = nameof(AudioOptions))]
    public sealed partial class AudioOptions : UserOptions<AudioOptions>
    {
        [SerializeField, Title("Volume")]
        private Option<float> _masterVolume = new(1f);

        [SerializeField]
        private Option<float> _effectsVolume = new(1f);

        [SerializeField]
        private Option<float> _ambienceVolume = new(1f);
        
        [SerializeField]
        private Option<float> _musicVolume = new(1f);

        [SerializeField]
        private Option<float> _uiVolume = new(1f);

        public Option<float> MasterVolume => _masterVolume;
        public Option<float> EffectsVolume => _effectsVolume;
        public Option<float> AmbienceVolume => _ambienceVolume;
        public Option<float> MusicVolume => _musicVolume;
        public Option<float> UIVolume => _uiVolume;

#if UNITY_EDITOR
        private void OnValidate()
        {
            _masterVolume.SetValue(Mathf.Clamp01(_masterVolume.Value));
            _effectsVolume.SetValue(Mathf.Clamp01(_effectsVolume.Value));
            _ambienceVolume.SetValue(Mathf.Clamp01(_ambienceVolume.Value));
            _musicVolume.SetValue(Mathf.Clamp01(_musicVolume.Value));
            _uiVolume.SetValue(Mathf.Clamp01(_uiVolume.Value));
        }
#endif
    }
}