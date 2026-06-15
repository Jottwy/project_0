using UnityEngine;
using System;

namespace PolymindGames
{
    [RequireComponent(typeof(AudioSource))]
    public sealed class AudioEffect : MonoBehaviour
    {
        [SerializeField, HideInInspector]
        private AudioSource _audioSource;

        [SerializeField, Range(0, 1f)]
        private float _volume;

        [SpaceArea]
        [SerializeField, Range(0f, 5f)]
        private float _fadeInDuration = 0.5f;

        [SerializeField, Range(0f, 5f)]
        private float _fadeOutDuration = 0.5f;

        [SerializeField, SpaceArea]
        private NoiseSettings _noise;

        private bool _isOn = true;
        private float _weight;
        
        public AudioSource AudioSource => _audioSource;
        public float VolumeMultiplier { get; set; } = 1f;

        public void Play(bool fadeIn = true)
        {
            enabled = true;
            _isOn = true;

            _audioSource.Play();

            if (!fadeIn)
                _weight = 1f;
        }

        public void Stop(bool fadeOut = true)
        {
            _isOn = false;

            if (!fadeOut)
                enabled = false;
        }

        private void FixedUpdate()
        {
            UpdateWeight(Time.fixedDeltaTime);
            UpdateAudio();

            if (_weight < 0.0001f)
                enabled = false;
        }

        private void UpdateWeight(float deltaTime)
        {
            float targetWeight = _isOn ? 1f : 0f;
            float fadeDelta = deltaTime * (1f / (_isOn ? _fadeInDuration : _fadeOutDuration));
            _weight = Mathf.MoveTowards(_weight, targetWeight, fadeDelta);
        }

        private void UpdateAudio()
        {
            float noise = _noise.Enabled ? Mathf.PerlinNoise(Time.time * _noise.Speed, 0f) * _noise.Intensity : 0f;
            float volume = (_volume + noise * _volume) * VolumeMultiplier * _weight;

            AudioSource.volume = volume;
        }

        private void OnEnable()
        {
            _audioSource.enabled = true;
            _audioSource.Play();
        }

        private void OnDisable()
        {
            _audioSource.Stop();
            _audioSource.enabled = false;
        }

        #region Internal Types
        [Serializable]
        private struct NoiseSettings
        {
            public bool Enabled;

            [Range(0f, 1f)]
            public float Intensity;

            [Range(0f, 1f)]
            public float Speed;
        }
        #endregion

        #region Editor
#if UNITY_EDITOR
        private void Reset()
        {
            _audioSource = gameObject.GetOrAddComponent<AudioSource>();
            _audioSource.spatialBlend = 1f;
            _audioSource.loop = true;
            _audioSource.playOnAwake = false;
            _audioSource.volume = 0f;
            _audioSource.minDistance = 1f;
            _audioSource.maxDistance = 10f;
        }

        private void OnValidate()
        {
            if (_audioSource == null)
                _audioSource = GetComponent<AudioSource>();
            _audioSource.enabled = enabled;
            _audioSource.volume = _volume;
        }
#endif
        #endregion
    }
}