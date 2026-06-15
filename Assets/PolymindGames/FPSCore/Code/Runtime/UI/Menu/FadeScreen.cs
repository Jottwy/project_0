using PolymindGames.ProceduralMotion;
using System.Collections;
using UnityEngine.Audio;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Handles fade-in and fade-out animations for a UI canvas group, allowing for smooth transitions in visibility.
    /// </summary>
    public sealed class FadeScreen : MonoBehaviour
    {
        [SerializeField, NotNull]
        [Tooltip("The canvas group used to control visibility and opacity.")]
        private CanvasGroup _canvasGroup;

        [SerializeField]
        [Tooltip("The default audio snapshot applied after loading is complete.")]
        private AudioMixerSnapshot _defaultAudioSnapshot;
        
        [SerializeField]
        [Tooltip("The audio snapshot applied during the loading screen.")]
        private AudioMixerSnapshot _loadingAudioSnapshot;

        [SerializeField, Range(0f, 5f), Title("Fade Settings")]
        [Tooltip("The delay before starting the fade-in animation.")]
        private float _fadeInDelay = 0.25f;

        [SerializeField, Range(0f, 5f)]
        [Tooltip("The duration of the fade-in animation.")]
        private float _fadeInDuration = 0.5f;

        [SerializeField, Range(0f, 5f)]
        [Tooltip("The delay before starting the fade-out animation.")]
        private float _fadeOutDelay = 0.25f;

        [SerializeField, Range(0f, 5f)]
        [Tooltip("The duration of the fade-out animation.")]
        private float _fadeOutDuration = 0.5f;

        private Tween<float> _canvasTween;

        /// <summary>
        /// Starts the fade-in animation with the configured delay and duration.
        /// </summary>
        public IEnumerator FadeIn(float durationMod = 1f)
        {
            _loadingAudioSnapshot.TransitionTo(_fadeInDuration * durationMod);
            yield return AnimateCanvasAlpha(true, _fadeInDelay * durationMod, _fadeInDuration * durationMod);
        }

        /// <summary>
        /// Starts the fade-out animation with the configured delay and duration.
        /// </summary>
        public IEnumerator FadeOut(float durationMod = 1f)
        {
            _defaultAudioSnapshot.TransitionTo(_fadeOutDelay * durationMod);
            yield return AnimateCanvasAlpha(false, _fadeOutDelay * durationMod, _fadeOutDuration * durationMod);
        }

        /// <summary>
        /// Animates the canvas group's alpha to fade in or fade out.
        /// </summary>
        /// <param name="fadeIn">If true, fades in; otherwise, fades out.</param>
        /// <param name="fadeDelay">The delay before starting the fade animation.</param>
        /// <param name="duration">The duration of the fade animation.</param>
        private IEnumerator AnimateCanvasAlpha(bool fadeIn, float fadeDelay, float duration)
        {
            yield return null;
            float targetAlpha = fadeIn ? 1f : 0f;
            var easeType = fadeIn ? EaseType.SineOut : EaseType.SineIn;
            yield return _canvasGroup.TweenCanvasGroupAlpha(targetAlpha, duration)
                .SetUnscaledTime(true)
                .SetDelay(fadeDelay)
                .SetEasing(easeType)
                .WaitForCompletion();
        }

        private void Awake()
        {
            _loadingAudioSnapshot.TransitionTo(0f);
            _canvasGroup.alpha = 1f;
        }
    }
}