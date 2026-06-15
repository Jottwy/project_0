using UnityEngine.Audio;
using UnityEngine;
using System;

namespace PolymindGames
{
    /// <summary>
    /// Struct that's used to store an audio resource and a volume multiplier.
    /// </summary>
    [Serializable]
    public struct AudioData
    {
        [Tooltip("The audio resource containing the clip to be played.")]
        public AudioResource Clip;

        [Tooltip("The volume of the audio clip in the range of 0 to 1.")]
        public float Volume;

        /// <summary>
        /// Initializes a new instance of the <see cref="AudioData"/> struct.
        /// </summary>
        /// <param name="clip">The audio clip to associate with this data.</param>
        /// <param name="volume">The volume multiplier, ranging from 0 to 1. Defaults to 1.</param>
        public AudioData(AudioClip clip, float volume = 1f)
        {
            Clip = clip;
            Volume = volume;
        }

        /// <summary>
        /// Determines whether the audio data is valid and worth playing.
        /// </summary>
        /// <returns>True if the audio is playable (audible volume and a valid clip), otherwise false.</returns>
        public readonly bool IsPlayable => Volume > 0.01f;
    }

    /// <summary>
    /// Struct that's used to store an audio clip, its volume, and a delay before playing.
    /// </summary>
    [Serializable]
    public struct DelayedAudioData
    {
        [Tooltip("The audio clip to be played in this sequence.")]
        public AudioResource Clip;

        [Tooltip("The volume of the audio clip, ranging from 0 to 1.")]
        public float Volume;

        [Tooltip("The delay time in seconds before the audio clip starts playing.")]
        public float Delay;

        /// <summary>
        /// Initializes a new instance of the <see cref="DelayedAudioData"/> struct.
        /// </summary>
        /// <param name="clip">The audio clip to be played.</param>
        /// <param name="volume">The volume multiplier, ranging from 0 to 1. Defaults to 1.</param>
        /// <param name="delay">The delay in seconds before playback begins. Defaults to 0.</param>
        public DelayedAudioData(AudioClip clip, float volume = 1f, float delay = 0f)
        {
            Clip = clip;
            Volume = volume;
            Delay = delay;
        }

        /// <summary>
        /// Determines whether the audio data is valid and worth playing.
        /// </summary>
        /// <returns>True if the audio is playable (audible volume and a valid clip), otherwise false.</returns>
        public readonly bool IsPlayable => Volume > 0.01f;
    }
}