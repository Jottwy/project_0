using UnityEngine;
using System;

namespace PolymindGames
{
    [RequireComponent(typeof(Light))]
    public sealed class LightEffect : MonoBehaviour
    {
        private enum PlayMode
        {
            Once, Loop
        }
        
        [SerializeField, Range(0f, 5f), Delayed]
        [Tooltip("Intensity of the light effect.")]
        private float _intensity = 1f;

        [SerializeField, Range(0f, 100f), Delayed]
        [Tooltip("Range of the light effect.")]
        private float _range = 1f;

        [SerializeField]
        [Tooltip("Color of the light effect.")]
        private Color _color = Color.yellow;

        [SerializeField, Range(0f, 2f), Title("Fade")]
        [Tooltip("Duration of the fade-in effect.")]
        private float _fadeInDuration = 0.5f;

        [SerializeField, Range(0f, 2f)]
        [Tooltip("Duration of the fade-out effect.")]
        private float _fadeOutDuration = 0.1f;

        [SerializeField, Title("Effects")]
        [Tooltip("Settings for pulsing effect.")]
        private PulseSettings _pulse;

        [SerializeField]
        [Tooltip("Settings for noise effect.")]
        private NoiseSettings _noise;

        private bool _isOn = true;
        private bool _pulseActive = true;
        private float _multiplier = 1f;
        private float _pulseTimer;
        private float _weight;
        
        [NonSerialized]
        private Light _light;
        
        public float Multiplier
        {
            get => _multiplier;
            set => _multiplier = value;
        }

        public void Play(bool fadeIn = true)
        {
            enabled = true;

            _isOn = true;
            _pulseActive = true;
            _pulseTimer = 0f;

            if (!fadeIn)
                _weight = 1f;
        }

        public void Stop(bool fadeOut = true)
        {
            _isOn = false;
            _pulseActive = false;

            if (!fadeOut)
                enabled = false;
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;
            UpdateWeight(deltaTime);
            UpdateLightEffect(deltaTime);

            if (_weight < 0.0001f)
                enabled = false;
        }

        private void UpdateWeight(float deltaTime)
        {
            float targetWeight = _isOn ? 1f : 0f;
            float fadeDelta = deltaTime * (1f / (_isOn ? _fadeInDuration : _fadeOutDuration));
            _weight = Mathf.MoveTowards(_weight, targetWeight, fadeDelta);
        }

        private void UpdateLightEffect(float deltaTime)
        {
            float intensity = _intensity * _weight * _multiplier;
            float range = _range * _weight * _multiplier;
            Color color = _color;

            ApplyPulseEffect(ref intensity, ref range, ref color, deltaTime);
            ApplyNoiseEffect(ref intensity, ref range);

            ApplyLightProperties(intensity, range, color);
        }

        private void ApplyPulseEffect(ref float intensity, ref float range, ref Color color, float deltaTime)
        {
            if (!_pulse.Enabled || !_pulseActive)
                return;

            float time = _pulseTimer / Mathf.Max(_pulse.Duration, 0.001f);
            float t = (Mathf.Sin(Mathf.PI * (2f * time - 0.5f)) + 1f) * 0.5f;

            intensity += _intensity * t * _pulse.IntensityAmplitude;
            range += _range * t * _pulse.RangeAmplitude;
            color = Color.Lerp(color, _pulse.Color, t * _pulse.ColorWeight);

            _pulseTimer += deltaTime;

            if (_pulseTimer > _pulse.Duration)
            {
                if (_pulse.Mode == PlayMode.Once)
                {
                    _pulseActive = false;
                }

                _pulseTimer = 0f;
            }

            if (!_pulseActive)
            {
                _isOn = false;
            }
        }

        private void ApplyNoiseEffect(ref float intensity, ref float range)
        {
            if (!_noise.Enabled)
                return;

            float noise = Mathf.PerlinNoise(Time.time * _noise.Speed, 0f) * _noise.Intensity;
            intensity += _intensity * noise;
            range += _range * noise;
        }

        private void ApplyLightProperties(float intensity, float range, Color color)
        {
#if POLYMIND_GAMES_FPS_HDRP
            intensity *= 500f;
#endif
            _light.intensity = intensity;
            _light.range = range;
            _light.color = color;
        }

        private void Awake()
        {
            _light = GetComponent<Light>();
        }

        private void OnEnable()
        {
            _light.enabled = true;
            ApplyLightProperties(0f, _light.range, _light.color);
        }

        private void OnDisable()
        {
            _light.enabled = false;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_light == null)
            {
                _light = GetComponent<Light>();
                if (_light == null)
                    return;
            }

            if (_light.enabled != enabled)
                _light.enabled = enabled;
            
            float intensity = _intensity;
            
#if POLYMIND_GAMES_FPS_HDRP
            intensity *= 500f;
#endif
            
            if (!Mathf.Approximately(_light.intensity, intensity))
                _light.intensity = intensity;

            if (!Mathf.Approximately(_light.range, _range))
                _light.range = _range;

            if (_light.color != _color)
                _light.color = _color;
        }
#endif
        
        #region Internal Types
        [Serializable]
        private struct PulseSettings
        {
            public bool Enabled;

            public PlayMode Mode;

            [Range(0f, 100f), SpaceArea]
            public float Duration;
            
            public Color Color;

            [Range(0f, 1f), SpaceArea]
            public float IntensityAmplitude;

            [Range(0f, 1f)]
            public float RangeAmplitude;

            [Range(0f, 1f)]
            public float ColorWeight;
        }

        [Serializable]
        private struct NoiseSettings
        {
            public bool Enabled;

            [Range(0f, 1f)]
            public float Intensity;

            [Range(0f, 10f)]
            public float Speed;
        }
        #endregion
    }
}