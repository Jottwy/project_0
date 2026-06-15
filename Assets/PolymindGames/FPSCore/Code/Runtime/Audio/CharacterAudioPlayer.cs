using System.Collections.Generic;
using UnityEngine.Audio;
using UnityEngine;
using System;

namespace PolymindGames
{
    /// <summary>
    /// Handles audio playback for character body points, providing 2D and 3D spatialized audio playback.
    /// Implements <see cref="ICharacterAudioPlayer"/> for functionality like playing, looping, and managing audio.
    /// </summary>
    [HelpURL("https://polymindgames.gitbook.io/welcome-to-gitbook/qgUktTCVlUDA7CAODZfe/player/modules-and-behaviours/audio#audio-player-module")]
    public sealed class CharacterAudioPlayer : CharacterBehaviour, ICharacterAudioPlayer
    {
        [SerializeField]
        [ReorderableList(FixedSize = true), LabelByChild(nameof(BodyPointAudioConfiguration.BodyPoint))]
        [Tooltip("Configuration array for audio playback at various character body points.")]
        private BodyPointAudioConfiguration[] _audioConfigurations = Array.Empty<BodyPointAudioConfiguration>();

        private readonly Transform[] _bodyPointTransforms = new Transform[BodyPointUtility.TotalBodyPoints];

        /// <inheritdoc/>
        public AudioSource PlayClip(AudioResource clip, BodyPoint bodyPoint, float volume, float delay)
        {
            int bodyPointIndex = (int)bodyPoint;
            var playMode = _audioConfigurations[bodyPointIndex].PlaybackMode;
            
            return playMode switch
            {
                AudioPlaybackMode.TwoDimensional => AudioManager.Instance.PlayClip2D(clip, volume, delay),
                AudioPlaybackMode.ThreeDimensionalStatic => AudioManager.Instance.PlayClip3D(clip, _bodyPointTransforms[bodyPointIndex].position, volume, delay),
                AudioPlaybackMode.ThreeDimensionalFollow => AudioManager.Instance.PlayClip3D(clip, _bodyPointTransforms[bodyPointIndex], volume, delay),
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        /// <inheritdoc/>
        public AudioSource PlayClips(AudioSequence sequence, BodyPoint bodyPoint, float speed = 1f) 
        {
            if (!sequence.IsPlayable || UnityUtility.IsQuitting)
                return null;

            int bodyPointIndex = (int)bodyPoint;
            var playMode = _audioConfigurations[bodyPointIndex].PlaybackMode;

            return playMode switch
            {
                AudioPlaybackMode.TwoDimensional => AudioManager.Instance.PlayClips2D(sequence, speed),
                AudioPlaybackMode.ThreeDimensionalStatic => AudioManager.Instance.PlayClips3D(sequence, _bodyPointTransforms[bodyPointIndex].position, speed),
                AudioPlaybackMode.ThreeDimensionalFollow => AudioManager.Instance.PlayClips3D(sequence, _bodyPointTransforms[bodyPointIndex], speed),
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        /// <inheritdoc/>
        public AudioSource StartLoop(AudioResource clip, BodyPoint bodyPoint, float volume, float duration)
        {
            int bodyPointIndex = (int)bodyPoint;
            var playMode = _audioConfigurations[bodyPointIndex].PlaybackMode;

            return playMode switch
            {
                AudioPlaybackMode.TwoDimensional => AudioManager.Instance.StartLoop2D(clip, volume, duration),
                AudioPlaybackMode.ThreeDimensionalStatic => AudioManager.Instance.StartLoop3D(clip, _bodyPointTransforms[bodyPointIndex], volume, duration),
                AudioPlaybackMode.ThreeDimensionalFollow => AudioManager.Instance.StartLoop3D(clip, _bodyPointTransforms[bodyPointIndex], volume, duration),
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        /// <inheritdoc/>
        public void StopLoop(AudioSource source)
        {
            AudioManager.Instance.StopLoop(source);
        }

        protected override void OnBehaviourStart(ICharacter character)
        {
            for (int i = 0; i < _bodyPointTransforms.Length; i++)
                _bodyPointTransforms[i] = character.GetTransformOfBodyPoint((BodyPoint)i);
        }

        #region Internal Types
        private enum AudioPlaybackMode
        {
            [Tooltip("Non-spatial 2D audio playback")]
            TwoDimensional,

            [Tooltip("Spatialized 3D audio at a fixed position")]
            ThreeDimensionalStatic,

            [Tooltip("Spatialized 3D audio that follows a transform")]
            ThreeDimensionalFollow
        }

        [Serializable]
        private struct BodyPointAudioConfiguration
        {
            [SerializeField, Hide]
            public BodyPoint BodyPoint;

            [SerializeField]
            public AudioPlaybackMode PlaybackMode;

            public BodyPointAudioConfiguration(BodyPoint bodyPoint)
            {
                BodyPoint = bodyPoint;
                PlaybackMode = AudioPlaybackMode.TwoDimensional;
            }
        }
        #endregion

        #region Editor
#if UNITY_EDITOR
        private void Reset() => ValidateCharacterAudioSourceArray();
        private void OnValidate() => ValidateCharacterAudioSourceArray();

        private void ValidateCharacterAudioSourceArray()
        {
            int currentBodyPointCount = BodyPointUtility.TotalBodyPoints;

            if (_audioConfigurations.Length != currentBodyPointCount)
            {
                var newSources = new List<BodyPointAudioConfiguration>(currentBodyPointCount);

                // Copy existing data to the new list while preserving serialized data
                for (int i = 0; i < currentBodyPointCount; i++)
                {
                    // If we have an existing source, keep it; otherwise, create a new one
                    // Create a new CharacterAudioSource for any new BodyPoints
                    newSources.Add(i < _audioConfigurations.Length ? _audioConfigurations[i] : new BodyPointAudioConfiguration((BodyPoint)i));
                }

                // Update the audio source points array
                _audioConfigurations = newSources.ToArray();

                // Mark the object as dirty to save changes
                UnityEditor.EditorUtility.SetDirty(this);
            }
        }
#endif
    #endregion
    }
}