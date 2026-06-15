using System.Runtime.CompilerServices;
using UnityEngine.Audio;
using UnityEngine;

namespace PolymindGames
{
    /// <summary>
    /// Defines methods for playing audio clips associated with character body points.
    /// Provides functionality for playing single clips, looping clips, and managing audio sequences.
    /// </summary>
    public interface ICharacterAudioPlayer
    {
        /// <summary>
        /// Plays an audio clip at a specified body point with volume and delay.
        /// </summary>
        /// <returns>The AudioSource playing the clip.</returns>
        AudioSource PlayClip(AudioResource clip, BodyPoint point, float volume = 1f, float delay = 0f);

        /// <summary>
        /// Plays a sequence of audio clips at the specified body point.
        /// </summary>
        /// <returns>The AudioSource playing the sequence.</returns>
        AudioSource PlayClips(AudioSequence sequence, BodyPoint point, float speed = 1f);

        /// <summary>
        /// Starts playing a looping audio clip at the specified body point.
        /// </summary>
        /// <returns>The AudioSource playing the loop.</returns>
        AudioSource StartLoop(AudioResource clip, BodyPoint point, float volume = 1f, float duration = float.PositiveInfinity);

        /// <summary>
        /// Stops the looping audio for the specified audio source.
        /// </summary>
        void StopLoop(AudioSource audioSource);
    }

    public sealed class DefaultCharacterAudioPlayer : ICharacterAudioPlayer
    {
        public AudioSource PlayClip(AudioResource clip, BodyPoint point, float volume, float delay)
            => AudioManager.Instance.PlayClip2D(clip, volume, delay);

        public AudioSource PlayClips(AudioSequence sequence, BodyPoint point, float speed)
            => AudioManager.Instance.PlayClips2D(sequence, speed);

        public AudioSource StartLoop(AudioResource clip, BodyPoint point, float volume, float duration)
            => AudioManager.Instance.StartLoop2D(clip, volume, duration);

        public void StopLoop(AudioSource audioSource)
            => AudioManager.Instance.StopLoop(audioSource);
    }

    public static class CharacterAudioPlayerExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AudioSource PlayClip(this ICharacterAudioPlayer audioPlayer, in AudioData audioData, BodyPoint point)
        {
            return audioData.IsPlayable
                ? audioPlayer.PlayClip(audioData.Clip, point, audioData.Volume)
                : null;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AudioSource PlayClip(this ICharacterAudioPlayer audioPlayer, in AudioData audioData, BodyPoint point, float volumeMultiplier)
        {
            return audioData.IsPlayable
                ? audioPlayer.PlayClip(audioData.Clip, point, audioData.Volume * volumeMultiplier)
                : null;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AudioSource StartLoop(this ICharacterAudioPlayer audioPlayer, in AudioData audioData, BodyPoint point, float duration = float.PositiveInfinity)
        {
            return audioData.IsPlayable
                ? audioPlayer.StartLoop(audioData.Clip, point, audioData.Volume, duration)
                : null;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AudioSource PlayClip(this ICharacterAudioPlayer audioPlayer, in DelayedAudioData audioData, BodyPoint point)
        {
            return audioData.IsPlayable
                ? audioPlayer.PlayClip(audioData.Clip, point, audioData.Volume, audioData.Delay)
                : null;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AudioSource PlayClip(this ICharacterAudioPlayer audioPlayer, in DelayedAudioData audioData, BodyPoint point, float volumeMultiplier)
        {
            return audioData.IsPlayable
                ? audioPlayer.PlayClip(audioData.Clip, point, audioData.Volume * volumeMultiplier, audioData.Delay)
                : null;
        }
        
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AudioSource StartLoop(this ICharacterAudioPlayer audioPlayer, in DelayedAudioData audioData, BodyPoint point, float duration = float.PositiveInfinity)
        {
            return audioData.IsPlayable
                ? audioPlayer.StartLoop(audioData.Clip, point, audioData.Volume, duration)
                : null;
        }
    }
}