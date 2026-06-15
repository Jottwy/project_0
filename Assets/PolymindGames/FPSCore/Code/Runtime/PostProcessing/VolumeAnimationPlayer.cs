using UnityEngine;

namespace PolymindGames.PostProcessing
{
    /// <summary>
    /// Plays and stops volume animations using a specified profile and duration settings.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VolumeAnimationPlayer : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Determines whether the animation should use unscaled time (ignoring Time.timeScale).")]
        private bool _useUnscaledTime = false;

        [SerializeField, Range(0f, 10f)]
        [Tooltip("Multiplier for the animation's duration.")]
        private float _durationMultiplier = 1f;

        [SerializeField, NotNull]
        [Tooltip("The profile that defines the volume animation settings.")]
        private VolumeAnimationProfile _volumeAnimation;

        /// <summary>
        /// Plays the configured volume animation.
        /// </summary>
        public void PlayAnimation()
        {
            PostProcessingManager.Instance.TryPlayAnimation(this, _volumeAnimation, _durationMultiplier, _useUnscaledTime);
        }

        /// <summary>
        /// Stops the currently playing volume animation.
        /// </summary>
        public void StopAnimation()
        {
            PostProcessingManager.Instance.CancelAnimation(this, _volumeAnimation);
        }
    }
}