using System.Collections;
using UnityEngine;

#if POLYMIND_GAMES_FPS_HDRP || POLYMIND_GAMES_FPS_URP
using VolProfile = UnityEngine.Rendering.VolumeProfile;
using Volume = UnityEngine.Rendering.Volume;
#else
using VolProfile = UnityEngine.Rendering.PostProcessing.PostProcessProfile;
using Volume = UnityEngine.Rendering.PostProcessing.PostProcessVolume;
#endif

namespace PolymindGames.PostProcessing
{
    public sealed partial class PostProcessingManager : Manager<PostProcessingManager>
    {
        private readonly VolumeAnimPlayer[] _animPlayers = new VolumeAnimPlayer[4];
        private Volume _activeVolume;

        #region Initialization
#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
#else
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
        private static void Init() => CreateInstance();

        protected override void OnInitialized()
        {
            for (int i = 0; i < _animPlayers.Length; i++)
                _animPlayers[i] = new VolumeAnimPlayer();

            ActiveVolume = null;
        }
        #endregion

        public Volume ActiveVolume
        {
            get => _activeVolume;
            set => _activeVolume = value;
        }

        public void TryPlayAnimation(MonoBehaviour behaviour, VolumeAnimationProfile animProfile, float durationMod = 1f, bool useUnscaledTime = false)
        {
            if (animProfile == null || durationMod < 0.01f)
                return;

            PlayAnimation_Internal(behaviour, animProfile, durationMod, useUnscaledTime);
        }

        public void PlayAnimation(MonoBehaviour behaviour, VolumeAnimationProfile animProfile, float durationMod = 1f, bool useUnscaledTime = false)
        {
#if DEBUG
            if (animProfile == null)
            {
                Debug.LogError($"The ''{nameof(VolumeAnimationProfile)}'' is null.", this);
                return;
            }
#endif

            PlayAnimation_Internal(behaviour, animProfile, durationMod, useUnscaledTime);
        }

        public void CancelAnimation(MonoBehaviour behaviour, VolumeAnimationProfile profile, bool immediate = false)
        {
            if (TryGetAnimPlayerFor(behaviour, profile, out var animPlayer))
            {
                animPlayer.Cancel(immediate);
            }
        }

        public void CancelAllAnimations()
        {
            foreach (var animPlayer in _animPlayers)
                animPlayer.Cancel(true);
        }

        private void PlayAnimation_Internal(MonoBehaviour behaviour, VolumeAnimationProfile animProfile, float durationMod = 1f, bool useUnscaledTime = false)
        {
            if (TryGetAnimPlayer(out var animPlayer))
            {
                if (ActiveVolume == null)
                {
                    Debug.LogWarning("No active volume active in the scene.");
                    return;
                }

                animPlayer.Play(behaviour, animProfile, ActiveVolume.profile, durationMod, useUnscaledTime);
            }
        }

        private bool TryGetAnimPlayerFor(MonoBehaviour behaviour, VolumeAnimationProfile profile, out VolumeAnimPlayer player)
        {
            foreach (var animPlayer in _animPlayers)
            {
                if (!animPlayer.IsActive)
                    continue;

                if (animPlayer.AnimationProfile == profile && animPlayer.AnimationHandler == behaviour)
                {
                    player = animPlayer;
                    return true;
                }
            }

            player = null;
            return false;
        }

        private bool TryGetAnimPlayer(out VolumeAnimPlayer volumeAnimPlayer)
        {
            foreach (var animPlayer in _animPlayers)
            {
                if (animPlayer.AnimationHandler == null)
                {
                    volumeAnimPlayer = animPlayer;
                    return true;
                }
            }

            volumeAnimPlayer = null;
            return false;
        }

        #region Internal Types
        private sealed class VolumeAnimPlayer
        {
            private enum VolumeAnimState
            {
                Idle,
                Playing,
                Cancelling
            }

            private VolumeAnimState _currentState = VolumeAnimState.Idle;
            private VolumeAnimationProfile _animationProfile;
            private MonoBehaviour _animationHandler;
            private VolProfile _volumeProfile;
            private Coroutine _currentCoroutine;
            
            public MonoBehaviour AnimationHandler => _animationHandler;
            public VolumeAnimationProfile AnimationProfile => _animationProfile;
            public VolProfile VolumeProfile => _volumeProfile;
            public bool IsActive => _currentState != VolumeAnimState.Idle;

            /// <summary>
            /// Starts playing the volume animation with the specified parameters.
            /// </summary>
            /// <param name="animHandler">The MonoBehaviour that will handle the coroutine.</param>
            /// <param name="animProfile">The profile containing the animation settings.</param>
            /// <param name="volumeProfile">The profile to apply animations to.</param>
            /// <param name="durationMod">A modifier to adjust the duration of the animation.</param>
            /// <param name="useUnscaledTime">Whether to use unscaled time for the animation.</param>
            public void Play(MonoBehaviour animHandler, VolumeAnimationProfile animProfile, VolProfile volumeProfile, float durationMod, bool useUnscaledTime)
            {
                // If the animation is already playing or cancelling, return early.
                if (_currentState != VolumeAnimState.Idle)
                    return;

                // Initialize the animation state and start the coroutine to play the animation.
                _currentState = VolumeAnimState.Playing;
                _animationHandler = animHandler;
                _animationProfile = animProfile;
                _volumeProfile = volumeProfile;
                _currentCoroutine = animHandler.StartCoroutine(PlayAnimation(animProfile, volumeProfile, durationMod, useUnscaledTime));
            }

            /// <summary>
            /// Cancels the current volume animation.
            /// </summary>
            /// <param name="immediate">Determines whether to cancel the animation immediately or allow it to complete gracefully.</param>
            /// <returns>Returns true if the animation was in progress and was cancelled; otherwise, false.</returns>
            public void Cancel(bool immediate)
            {
                // If the animation is not active, return false as there's nothing to cancel.
                if (_currentState == VolumeAnimState.Idle)
                    return;
                
                // Handle the cancellation based on the state of the animation and the immediate flag.
                if (_animationHandler == null)
                {
                    Cleanup();
                }
                else if (immediate)
                {
                    _animationHandler.StopCoroutine(_currentCoroutine);
                    Cleanup();
                }
                else
                {
                    _currentState = VolumeAnimState.Cancelling;
                }

                // Cleans up the resources and resets the state to idle.
                void Cleanup()
                {
                    // Clear references to the animation handler and coroutine.
                    _animationHandler = null;
                    _currentCoroutine = null;

                    // Reset the state to idle.
                    _currentState = VolumeAnimState.Idle;

                    // Dispose of all animations in the current profile.
                    foreach (var animation in _animationProfile.Animations)
                        animation.Dispose(_volumeProfile);
                }
            }

            /// <summary>
            /// Coroutine to play animations from a VolumeAnimationProfile.
            /// </summary>
            private IEnumerator PlayAnimation(VolumeAnimationProfile animProfile, VolProfile profile, float durationMod, bool useUnscaledTime)
            {
                // Get animations from the profile and store their count.
                var animations = animProfile.Animations;
                int animCount = animations.Length;

                // Set the profile for each animation.
                for (int i = 0; i < animCount; i++)
                    animations[i].SetProfile(profile);

                // Initialize time variables.
                float elapsedTime = 0f;
                float totalDuration = animProfile.PlayDuration * durationMod;
                float inverseDuration = 1 / totalDuration;

                // Continue animating while time is less than 1 and state is Playing.
                while (elapsedTime < 1f && _currentState == VolumeAnimState.Playing)
                {
                    // Animate each animation at time 'elapsedTime'.
                    for (int i = 0; i < animCount; i++)
                        animations[i].Animate(elapsedTime);

                    // Increment time based on frame time and unscaled or scaled time.
                    elapsedTime += inverseDuration * (useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime);
                    yield return null;
                }

                // If time exceeds 1, set time to 1 and finalize animations.
                if (elapsedTime > 1f)
                {
                    elapsedTime = 1f;
                    for (int i = 0; i < animCount; i++)
                        animations[i].Animate(1f);
                }

                // Continue playing until manual stop or if state is Playing and mode is PlayUntilManualStop.
                while (_currentState == VolumeAnimState.Playing && animProfile.Mode == AnimateMode.PlayUntilManualStop)
                {
#if UNITY_EDITOR

                    // Force animations to their final state if in Unity Editor.
                    for (int i = 0; i < animCount; i++)
                        animations[i].Animate(1f);
#endif
                    yield return null;
                }

                // If cancelling or mode is PlayOnceAndReverse, reverse animation.
                if (_currentState == VolumeAnimState.Cancelling || animProfile.Mode == AnimateMode.PlayOnceAndReverse)
                {
                    totalDuration = elapsedTime * animProfile.CancelDuration * durationMod;
                    inverseDuration = 1 / totalDuration;
                    while (elapsedTime > 0f)
                    {
                        // Animate each animation at time 'elapsedTime' in reverse.
                        for (int i = 0; i < animCount; i++)
                            animations[i].Animate(elapsedTime);

                        // Decrement time based on frame time and unscaled or scaled time.
                        elapsedTime -= inverseDuration * (useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime);
                        yield return null;
                    }
                }

                // Dispose animations from the profile.
                for (int i = 0; i < animCount; i++)
                    animations[i].Dispose(profile);

                // Reset state and coroutine.
                _currentState = VolumeAnimState.Idle;
                _animationHandler = null;
                _currentCoroutine = null;
            }
        }
        #endregion
    }
}