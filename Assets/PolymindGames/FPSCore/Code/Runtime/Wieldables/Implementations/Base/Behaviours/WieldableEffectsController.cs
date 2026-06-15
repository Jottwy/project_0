using UnityEngine;
using System;

namespace PolymindGames.WieldableSystem
{
    [AddComponentMenu("Polymind Games/Wieldables/Behaviours/Wieldable Effects Controller")]
    public sealed class WieldableEffectsController : MonoBehaviour
    {
        [SerializeField]
        [Title("Toggle Effects")]
        [ReorderableList(HasLabels = false)]
        private LightEffect[] _lightEffects;

        [SerializeField, ReorderableList(HasLabels = false)]
        private AudioEffect[] _audioEffects;

        [SerializeField, ReorderableList(HasLabels = false)]
        private ParticleSystem[] _particleEffects;

        [SerializeField, Title("Enable Effects")]
        private DelayedAudioData _enableAudio;

        [SerializeField, ReorderableList, SpaceArea]
        private AnimatorParameterTrigger[] _enableAnimatorParameters = Array.Empty<AnimatorParameterTrigger>();

        [SerializeField, Title("Disable Effects")]
        private DelayedAudioData _disableAudio;

        [SerializeField, ReorderableList, SpaceArea]
        private AnimatorParameterTrigger[] _disableAnimatorParameters = Array.Empty<AnimatorParameterTrigger>();

        private IWieldable _wieldable;
        private bool _canEnableEffects = true;
        private bool _effectsEnabled;

        /// <summary>
        /// Allows or disallows enabling the effects.
        /// If the effects are currently enabled and this is set to false, the effects will be disabled.
        /// </summary>
        public void SetCanEnable(bool isEnabled)
        {
            _canEnableEffects = isEnabled;
            if (_effectsEnabled && !isEnabled)
                DisableEffects();
        }

        /// <summary>
        /// Toggles the enabled state of the effects.
        /// </summary>
        public void ToggleEffects()
        {
            if (_effectsEnabled)
            {
                DisableEffects();
            }
            else
            {
                EnableEffects();
            }
        }

        /// <summary>
        /// Enables all configured effects.
        /// </summary>
        public void EnableEffects()
        {
            if (_effectsEnabled || !_canEnableEffects)
                return;

            ActivateEffects(_lightEffects, _audioEffects, _particleEffects);
            PlayAudioClip(_enableAudio);
            TriggerAnimatorParameters(_enableAnimatorParameters);

            _effectsEnabled = true;
        }

        /// <summary>
        /// Disables all configured effects.
        /// </summary>
        public void DisableEffects()
        {
            if (!_effectsEnabled)
                return;

            DeactivateEffects(_lightEffects, _audioEffects, _particleEffects);
            PlayAudioClip(_disableAudio);
            TriggerAnimatorParameters(_disableAnimatorParameters);

            _effectsEnabled = false;
        }

        private void Awake()
        {
            _wieldable = GetComponentInParent<IWieldable>();
        }

        private static void ActivateEffects(LightEffect[] lights, AudioEffect[] audios, ParticleSystem[] particles)
        {
            foreach (var light in lights) light.Play();
            foreach (var audio in audios) audio.Play();
            foreach (var particle in particles) particle.Play(true);
        }

        private static void DeactivateEffects(LightEffect[] lights, AudioEffect[] audios, ParticleSystem[] particles)
        {
            foreach (var light in lights) light.Stop();
            foreach (var audio in audios) audio.Stop();
            foreach (var particle in particles) particle.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        private void PlayAudioClip(in DelayedAudioData audioData)
        {
            _wieldable.Audio.PlayClip(audioData.Clip, BodyPoint.Hands, audioData.Volume, audioData.Delay);
        }

        private void TriggerAnimatorParameters(AnimatorParameterTrigger[] parameters)
        {
            var animator = _wieldable.Animator;
            foreach (var param in parameters)
            {
                animator.SetParameter(param.Type, param.Hash, param.Value);
            }
        }
    }
}