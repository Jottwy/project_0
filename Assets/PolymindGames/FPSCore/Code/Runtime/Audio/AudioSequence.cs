using System.Collections;
using UnityEngine;
using System;

namespace PolymindGames
{
    /// <summary>
    /// Represents a sequence of audio clips to be played in a specific order, with configurable volume and delay settings for each clip.
    /// </summary>
    [Serializable]
    public sealed class AudioSequence : ISerializationCallbackReceiver
    {
        [BeginHorizontal, Header("Volume"), HideLabel]
        [SerializeField, Range(0f, 1f), SpaceArea]
        [Tooltip("The base volume of the audio clips in the range of 0 to 1.")]
        private float _volume = 1f;

        [Header("Pitch"), HideLabel]
        [SerializeField, Range(0f, 2f), SpaceArea]
        [Tooltip("The base pitch of the audio clips in the range of 0 to 2.")]
        private float _pitch = 1f;

        [EndHorizontal, Header("Randomness"), HideLabel]
        [SerializeField, Range(0f, 1f), SpaceArea(5f, 5f)]
        [Tooltip("The amount of randomness to apply to the volume and pitch of the audio clips.")]
        private float _randomness = 0.05f;

        [SerializeField]
        [ReorderableList(ListStyle.Lined, HasLabels = false), IgnoreParent]
        [Tooltip("The sequence of audio clips with associated volume and delay settings.")]
        private DelayedAudioData[] _clips = Array.Empty<DelayedAudioData>();

        private const float PitchRandomnessMultiplier = 0.3f;

        public AudioSequence()
        { }

        public AudioSequence(DelayedAudioData[] clips, float volume, float pitch, float randomness)
        {
            _clips = clips;
            _volume = volume;
            _pitch = pitch;
            _randomness = randomness;
        }
        
        /// <summary>
        /// Returns true if the audio sequence is worth playing (i.e., has a volume greater than 0.01 and contains clips).
        /// </summary>
        public bool IsPlayable => _volume > 0.01f && _clips.Length > 0;

        /// <summary>
        /// Gets the jittered volume multiplier to use for the audio clips.
        /// </summary>
        public float Volume => _volume.Jitter(_randomness);

        /// <summary>
        /// Gets the jittered pitch multiplier to use for the audio clips.
        /// </summary>
        public float Pitch => _pitch.Jitter(_randomness * PitchRandomnessMultiplier);

        public DelayedAudioData[] Clips => _clips; 

        /// <summary>
        /// Plays the audio sequence using the provided AudioSource and MonoBehaviour owner.
        /// </summary>
        public void PlaySequence(AudioSource audioSource, MonoBehaviour owner, float speed = 1f)
        {
            if (!IsPlayable || UnityUtility.IsQuitting)
                return;
            
#if DEBUG
            if (audioSource == null || owner == null)
            {
                Debug.LogError("AudioSource or MonoBehaviour owner is not assigned.");
                return;
            }
#endif
            
            if (_clips.Length == 1 && _clips[0].Delay < 0.001f)
            {
                audioSource.volume = 1f;
                audioSource.pitch = _pitch;
                audioSource.PlayOneShot((AudioClip)_clips[0].Clip, _clips[0].Volume * _volume);
            }
            else
            {
                owner.StartCoroutine(PlaySequence(audioSource, speed));
            }
        }

        public IEnumerator PlaySequence(AudioSource audioSource, float speed)
        {
            audioSource.playOnAwake = true;
            audioSource.volume = 1f;
            audioSource.pitch = _pitch;

            float startTime = Time.time;
            float delayMultiplier = 1 / speed;
            foreach (var data in _clips)
            {
                // Calculate the time to wait until the clip should start playing
                float timeToWait = (data.Delay - (Time.time - startTime)) * delayMultiplier;
                yield return new WaitForTime(timeToWait);

                // Play the audio clip with the specified volume
                audioSource.PlayOneShot((AudioClip)data.Clip, data.Volume * _volume);
            }

            audioSource.playOnAwake = false;
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                ClampClipDelays();

                for (int i = 0; i < Clips.Length; i++)
                {
                    if (Clips[i].Volume < 0.001f)
                        Clips[i].Volume = 1f;
                }
            }
#endif
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        { }

#if UNITY_EDITOR
        private void ClampClipDelays()
        {
            if (_clips == null)
                return;

            float minDelay = 0f;
            for (int i = 0; i < _clips.Length; ++i)
            {
                if (_clips[i].Clip != null && _clips[i].Clip is not AudioClip)
                {
                    Debug.LogWarning($"Audio Sequence does not support Audio Random Containers. {_clips[i].Clip}", _clips[i].Clip);
                    UnityEditor.ArrayUtility.RemoveAt(ref _clips, i);
                    continue;
                }

                _clips[i].Delay = Mathf.Max(minDelay, _clips[i].Delay);
                minDelay = _clips[i].Delay;
            }
        }
#endif
    }
}