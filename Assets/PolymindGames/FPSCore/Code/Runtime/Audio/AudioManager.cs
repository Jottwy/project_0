using System.Runtime.CompilerServices;
using System.Collections.Generic;
using PolymindGames.Options;
using System.Collections;
using UnityEngine.Audio;
using UnityEngine;
using System;

namespace PolymindGames
{
    public enum AudioChannel
    {
        [Tooltip("Controls the overall volume of all audio.")]
        Master,

        [Tooltip("Handles sound effects like gunfire, explosions, etc.")]
        Sfx,

        [Tooltip("Controls ambient background sounds like wind or birds.")]
        Ambience,

        [Tooltip("Manages background music playback.")]
        Music,

        [Tooltip("Handles user interface sounds like button clicks.")]
        UI
    }

    /// <summary>
    /// Manages game audio including sound effects, ambience, music, and UI sounds.
    /// Handles audio mixer groups, 3D/2D audio source counts, and easing durations for looping audio.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrderConstants.Manager)]
    [CreateAssetMenu(menuName = CreateMenuPath + "Audio Manager", fileName = nameof(AudioManager))]
    public sealed partial class AudioManager : Manager<AudioManager>
    {
        [SerializeField]
        [Tooltip("The main audio mixer controlling all audio settings.")]
        private AudioMixer _audioMixer;

        [SerializeField]
        [Tooltip("The default audio mixer snapshot to revert to.")]
        private AudioMixerSnapshot _defaultSnapshot;

        [SerializeField, Title("Groups")]
        [Tooltip("The master audio mixer group.")]
        private AudioMixerGroup _masterGroup;

        [SerializeField]
        [Tooltip("The audio mixer group for sound effects.")]
        private AudioMixerGroup _soundEffectsGroup;

        [SerializeField]
        [Tooltip("The audio mixer group for ambience sounds.")]
        private AudioMixerGroup _ambienceGroup;

        [SerializeField]
        [Tooltip("The audio mixer group for music.")]
        private AudioMixerGroup _musicGroup;

        [SerializeField]
        [Tooltip("The audio mixer group for UI sounds.")]
        private AudioMixerGroup _uiGroup;

        [SerializeField, Range(1, 32), Title("Audio Sources")]
        [Tooltip("The default number of 3D audio sources.")]
        private int _initial3dSourcesCount = 8;

        [SerializeField, Range(1, 32)]
        [Tooltip("The maximum number of 3D audio sources.")]
        private int _max3dSourcesCount = 16;

        [SerializeField, Range(1, 32)]
        [Tooltip("The default number of 2D audio sources.")]
        private int _initial2dSourcesCount = 2;

        [SerializeField, Range(1, 32)]
        [Tooltip("The maximum number of 2D audio sources.")]
        private int _max2dSourcesCount = 4;

        private CoroutineHandler _coroutineHandler;
        private List<AudioSource> _2dAudioSources;
        private List<AudioSource> _3dAudioSources;
        private int _current2DAudioSourceIndex;
        private int _current3DAudioSourceIndex;
        private Transform _audioSourcesRoot;

        private const string MasterVolumeKey = "MasterVolume";
        private const string EffectsVolumeKey = "EffectsVolume";
        private const string AmbienceVolumeKey = "AmbienceVolume";
        private const string MusicVolumeKey = "MusicVolume";
        private const string UIVolumeKey = "UIVolume";

        private const int MaxRetryCount = 3;
        private const float Min3DRange = 2f;
        private const float MinEaseDuration = 0.25f;
        private const float MaxEaseDuration = 0.5f;
        private const string AudioSource3D = "3D Audio Source";
        private const string AudioSource2D = "2D Audio Source";

        #region Initialization
        private sealed class CoroutineHandler : MonoBehaviour
        { }

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
#else
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
#endif
        private static void Init() => LoadOrCreateInstance();

        protected override void OnInitialized()
        {
            _audioSourcesRoot = CreateChildTransformForManager("AudioManagerRuntimeObject");
            _coroutineHandler = _audioSourcesRoot.gameObject.AddComponent<CoroutineHandler>();
            
            CreateAudioSources();
            SetVolumeCallbacks();
            InitVolume();
        }

        private void CreateAudioSources()
        {
            _current3DAudioSourceIndex = -1;
            _current2DAudioSourceIndex = -1;
            _3dAudioSources = CreateInitialAudioSources(_soundEffectsGroup, _initial3dSourcesCount, _max3dSourcesCount, true, AudioSource3D);
            _2dAudioSources = CreateInitialAudioSources(_soundEffectsGroup, _initial2dSourcesCount, _max2dSourcesCount, false, AudioSource2D);

            return;

            List<AudioSource> CreateInitialAudioSources(AudioMixerGroup mixerGroup, int count, int maxCount, bool is3D, string objectName)
            {
                var sources = new List<AudioSource>(count + maxCount);
                for (int i = 0; i < count; i++)
                    sources.Add(CreateAudioSource(mixerGroup, is3D, objectName));

                return sources;
            }
        }

        private AudioSource CreateAudioSource(AudioMixerGroup mixerGroup, bool is3D, string objectName)
        {
            AudioSource source;

            if (is3D)
            {
                GameObject audioObject = new(objectName, typeof(AudioSource));
                audioObject.transform.SetParent(_audioSourcesRoot.transform);
                source = audioObject.GetComponent<AudioSource>();
            }
            else
                source = _audioSourcesRoot.gameObject.AddComponent<AudioSource>();

            source.volume = 1f;
            source.outputAudioMixerGroup = mixerGroup;
            source.playOnAwake = false;
            source.spatialize = is3D;
            source.spatialBlend = is3D ? 1f : 0f;
            source.minDistance = Min3DRange;

            return source;
        }

        private void SetVolumeCallbacks()
        {
            var settings = AudioOptions.Instance;
            settings.MasterVolume.Changed += volume => _audioMixer.SetFloat(MasterVolumeKey, GetDBForVolume(volume));
            settings.EffectsVolume.Changed += volume => _audioMixer.SetFloat(EffectsVolumeKey, GetDBForVolume(volume));
            settings.AmbienceVolume.Changed += volume => _audioMixer.SetFloat(AmbienceVolumeKey, GetDBForVolume(volume));
            settings.MusicVolume.Changed += volume => _audioMixer.SetFloat(MusicVolumeKey, GetDBForVolume(volume));
            settings.UIVolume.Changed += volume => _audioMixer.SetFloat(UIVolumeKey, GetDBForVolume(volume));
        }

        private void InitVolume()
        {
            var settings = AudioOptions.Instance;
            _audioMixer.SetFloat(MasterVolumeKey, GetDBForVolume(settings.MasterVolume));
            _audioMixer.SetFloat(EffectsVolumeKey, GetDBForVolume(settings.EffectsVolume));
            _audioMixer.SetFloat(AmbienceVolumeKey, GetDBForVolume(settings.AmbienceVolume));
            _audioMixer.SetFloat(MusicVolumeKey, GetDBForVolume(settings.MusicVolume));
            _audioMixer.SetFloat(UIVolumeKey, GetDBForVolume(settings.UIVolume));
        }

        private static float GetDBForVolume(float volume) => Mathf.Log(Mathf.Clamp(volume, 0.001f, 1f)) * 20;
		#endregion

        /// <summary>
        /// Gets the AudioMixer used by the audio system.
        /// </summary>
        public AudioMixer AudioMixer => _audioMixer;

        /// <summary>
        /// Gets the default AudioMixerSnapshot.
        /// </summary>
        public AudioMixerSnapshot DefaultSnapshot => _defaultSnapshot;

        /// <summary>
        /// Retrieves the corresponding AudioMixerGroup for the given audio channel.
        /// </summary>
        /// <param name="channel">The AudioChannel to retrieve the mixer group for.</param>
        /// <returns>The AudioMixerGroup associated with the specified AudioChannel.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the provided channel is not a valid AudioChannel.</exception>
        public AudioMixerGroup GetMixerGroup(AudioChannel channel)
        {
            return channel switch
            {
                AudioChannel.Master => _masterGroup,
                AudioChannel.Sfx => _soundEffectsGroup,
                AudioChannel.Ambience => _ambienceGroup,
                AudioChannel.Music => _musicGroup,
                AudioChannel.UI => _uiGroup,
                _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
            };
        }

        /// <summary>
        /// Plays an audio clip at a specified position in 3D space after a delay.
        /// </summary>
        public AudioSource PlayClip3D(AudioResource clip, Vector3 position, float volume = 1f, float delay = 0f, AudioChannel channel = AudioChannel.Sfx)
        {
            var source = GetAvailable3DAudioSource();
            var sourceTransform = source.transform;
            sourceTransform.parent = _audioSourcesRoot;
            sourceTransform.position = position;

            source.loop = false;
            source.resource = clip;
            source.volume = volume;
            source.minDistance = Min3DRange;
            source.outputAudioMixerGroup = GetMixerGroup(channel);

            if (delay > 0.01f)
                source.PlayDelayed(delay);
            else
                source.Play();

            return source;
        }

        /// <summary>
        /// Plays an audio clip at the position of a specified transform after a delay.
        /// </summary>
        public AudioSource PlayClip3D(AudioResource clip, Transform transform, float volume = 1f, float delay = 0f, AudioChannel channel = AudioChannel.Sfx)
        {
            var source = GetAvailable3DAudioSource();
            var sourceTransform = source.transform;
            sourceTransform.SetParent(transform, false);
            sourceTransform.localPosition = Vector3.zero;

            source.resource = clip;
            source.volume = volume;
            source.loop = false;
            source.minDistance = Min3DRange;
            source.outputAudioMixerGroup = GetMixerGroup(channel);

            if (delay > 0.01f)
                source.PlayDelayed(delay);
            else
                source.Play();

            return source;
        }

        /// <summary>
        /// Starts looping an audio clip at the position of a specified transform.
        /// </summary>
        public AudioSource StartLoop3D(AudioResource clip, Vector3 position, float volume = 1f, float duration = float.PositiveInfinity, AudioChannel channel = AudioChannel.Sfx)
        {
            AudioSource source = GetAvailable3DAudioSource();
            var sourceTransform = source.transform;
            sourceTransform.parent = _audioSourcesRoot;
            sourceTransform.position = position;

            source.loop = true;
            source.resource = clip;
            source.playOnAwake = true;
            source.outputAudioMixerGroup = GetMixerGroup(channel);

            _coroutineHandler.StartCoroutine(PlayLoop(source, volume, duration));
            return source;
        }

        /// <summary>
        /// Starts looping an audio clip at the position of a specified transform.
        /// </summary>
        public AudioSource StartLoop3D(AudioResource clip, Transform transform, float volume = 1f, float duration = float.PositiveInfinity, AudioChannel channel = AudioChannel.Sfx)
        {
            AudioSource source = GetAvailable3DAudioSource();
            var sourceTransform = source.transform;
            sourceTransform.SetParent(transform, false);
            sourceTransform.localPosition = Vector3.zero;

            source.loop = true;
            source.resource = clip;
            source.playOnAwake = true;
            source.outputAudioMixerGroup = GetMixerGroup(channel);

            _coroutineHandler.StartCoroutine(PlayLoop(source, volume, duration, true));
            return source;
        }

        /// <summary>
        /// Plays an audio clip in a 2D audio environment after a delay.
        /// </summary>
        public AudioSource PlayClip2D(AudioResource clip, float volume = 1f, float delay = 0f, AudioChannel channel = AudioChannel.Sfx)
        {
            var source = GetAvailable2DAudioSource();

            source.resource = clip;
            source.volume = volume;
            source.loop = false;
            source.outputAudioMixerGroup = GetMixerGroup(channel);

            if (delay > 0.01f)
                source.PlayDelayed(delay);
            else
                source.Play();

            return source;
        }

        public AudioSource StartLoop2D(AudioResource clip, float volume = 1f, float duration = 1f, AudioChannel channel = AudioChannel.Sfx)
        {
            AudioSource source = GetAvailable2DAudioSource();
            
            source.loop = true;
            source.resource = clip;
            source.playOnAwake = true;
            source.outputAudioMixerGroup = GetMixerGroup(channel);

            _coroutineHandler.StartCoroutine(PlayLoop(source, volume, duration, true));
            return source;
        }

        /// <summary>
        /// Checks if the specified AudioSource is currently playing a loop.
        /// </summary>
        public bool IsLoopPlaying(AudioSource source) => source.isPlaying && source.loop && source.playOnAwake;

        /// <summary>
        /// Stops the specified looping AudioSource.
        /// </summary>
        public void StopLoop(AudioSource source)
        {
#if DEBUG
            if (source == null)
            {
                Debug.LogWarning("Audio source is null.");
                return;
            }
#endif
            source.playOnAwake = false;
        }

        private IEnumerator PlayLoop(AudioSource source, float volume, float duration, bool reparent = false)
        {
            source.Play();

            float easeDuration = Mathf.Clamp(duration * 0.25f, MinEaseDuration, MaxEaseDuration);

            //  Ease in volume...
            float from = 0f;
            float to = volume;
            float time = 0f;
            while (time < 1f)
            {
                source.volume = Mathf.Lerp(from, to, time);
                time += Time.deltaTime / easeDuration;
                yield return null;
            }

            // Loop audio...
            while (source.playOnAwake)
            {
                if (duration <= 0f)
                    source.playOnAwake = false;
                duration -= Time.deltaTime;

                yield return null;
            }

            // Ease out volume...
            from = source.volume;
            to = 0f;
            time = 0f;
            while (time < 1f)
            {
                source.volume = Mathf.Lerp(from, to, time);
                time += Time.deltaTime / easeDuration;
                yield return null;
            }

            source.Stop();

            if (reparent)
                source.transform.SetParent(_audioSourcesRoot.transform);
        }
        
        private AudioSource GetAvailable3DAudioSource() => 
            GetAvailableAudioSource(_3dAudioSources, ref _current3DAudioSourceIndex, _max3dSourcesCount, is3D: true, AudioSource3D);

        private AudioSource GetAvailable2DAudioSource() => 
            GetAvailableAudioSource(_2dAudioSources, ref _current2DAudioSourceIndex, _max2dSourcesCount, is3D: false, AudioSource2D);

        private AudioSource GetAvailableAudioSource(List<AudioSource> audioSources, ref int currentIndex, int maxSourcesCount, bool is3D, string sourceName)
        {
            int attemptCount = 0;

            while (true)
            {
                // Increment and wrap around the index for circular array behavior
                currentIndex = (currentIndex + 1) % audioSources.Count;

                var audioSource = audioSources[currentIndex];

                // Check if the audio source is null and recreate it
                if (audioSource == null)
                {
                    audioSource = CreateAudioSource(_soundEffectsGroup, is3D, sourceName);
                    audioSources[currentIndex] = audioSource;
                }
                else if (!audioSource.isPlaying)
                {
                    return audioSource;
                }
                // Create a new audio source if the current one is in use and within max source count
                else if (audioSources.Count < maxSourcesCount)
                {
                    audioSource = CreateAudioSource(_soundEffectsGroup, is3D, sourceName);
                    currentIndex = audioSources.Count;
                    audioSources.Add(audioSource);
                    return audioSource;
                }
                // Retry until limit if sources are all playing
                else if (attemptCount < MaxRetryCount)
                {
                    attemptCount++;
                }
                else
                {
                    // Stop the current source and return it
                    audioSource.Stop();
                    return audioSource;
                }
            }
        }

        #region Editor
#if UNITY_EDITOR
        private void OnValidate()
        {
            _max3dSourcesCount = Mathf.Max(_initial3dSourcesCount, _max3dSourcesCount);
            _max2dSourcesCount = Mathf.Max(_initial2dSourcesCount, _max2dSourcesCount);
        }
#endif
        #endregion
    }

    public sealed partial class AudioManager
    {
        /// <summary>
        /// Plays an audio clip at a specified position in 3D space.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AudioSource PlayClip3D(in AudioData data, Vector3 position, AudioChannel channel = AudioChannel.Sfx)
        {
            return data.IsPlayable
                ? PlayClip3D(data.Clip, position, data.Volume, 0f, channel)
                : null;
        }

        /// <summary>
        /// Plays an audio clip at a specified position in 3D space.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AudioSource PlayClip3D(in AudioData data, Vector3 position, float volumeMultiplier, AudioChannel channel = AudioChannel.Sfx)
        {
            return data.IsPlayable
                ? PlayClip3D(data.Clip, position, data.Volume * volumeMultiplier, 0f, channel)
                : null;
        }

        /// <summary>
        /// Plays an audio clip at the position of a specified transform after a delay.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AudioSource PlayClip3D(in AudioData data, Transform transform, AudioChannel channel = AudioChannel.Sfx)
        {
            return data.IsPlayable
                ? PlayClip3D(data.Clip, transform, data.Volume, 0f, channel)
                : null;
        }

        /// <summary>
        /// Plays an audio clip at the position of a specified transform after a delay.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AudioSource PlayClip3D(in AudioData data, Transform transform, float volumeMultiplier, AudioChannel channel = AudioChannel.Sfx)
        {
            return data.IsPlayable
                ? PlayClip3D(data.Clip, transform, data.Volume * volumeMultiplier, 0f, channel)
                : null;
        }

        /// <summary>
        /// Plays an audio clip in a 2D audio environment.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AudioSource PlayClip2D(in AudioData data, AudioChannel channel = AudioChannel.Sfx)
        {
            return data.IsPlayable
                ? PlayClip2D(data.Clip, data.Volume, 0f, channel)
                : null;
        }

        /// <summary>
        /// Plays an audio clip in a 2D audio environment.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AudioSource PlayClip2D(in AudioData data, float volumeMultiplier, AudioChannel channel = AudioChannel.Sfx)
        {
            return data.IsPlayable
                ? PlayClip2D(data.Clip, data.Volume * volumeMultiplier, 0f, channel)
                : null;
        }

        /// <summary>
        /// Plays an audio clip at a specified position in 3D space.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AudioSource PlayClip3D(in DelayedAudioData data, Vector3 position, AudioChannel channel = AudioChannel.Sfx)
        {
            return data.IsPlayable
                ? PlayClip3D(data.Clip, position, data.Volume, data.Delay, channel)
                : null;
        }

        /// <summary>
        /// Plays an audio clip at a specified position in 3D space.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AudioSource PlayClip3D(in DelayedAudioData data, Vector3 position, float volumeMultiplier, AudioChannel channel = AudioChannel.Sfx)
        {
            return data.IsPlayable
                ? PlayClip3D(data.Clip, position, data.Volume * volumeMultiplier, data.Delay, channel)
                : null;
        }

        /// <summary>
        /// Plays an audio clip at the position of a specified transform after a delay.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AudioSource PlayClip3D(in DelayedAudioData data, Transform transform, AudioChannel channel = AudioChannel.Sfx)
        {
            return data.IsPlayable
                ? PlayClip3D(data.Clip, transform, data.Volume, data.Delay, channel)
                : null;
        }

        /// <summary>
        /// Plays an audio clip at the position of a specified transform after a delay.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AudioSource PlayClip3D(in DelayedAudioData data, Transform transform, float volumeMultiplier, AudioChannel channel = AudioChannel.Sfx)
        {
            return data.IsPlayable
                ? PlayClip3D(data.Clip, transform, data.Volume * volumeMultiplier, data.Delay, channel)
                : null;
        }

        /// <summary>
        /// Plays an audio clip in a 2D audio environment.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AudioSource PlayClip2D(in DelayedAudioData data, AudioChannel channel = AudioChannel.Sfx)
        {
            return data.IsPlayable
                ? PlayClip2D(data.Clip, data.Volume, data.Delay, channel)
                : null;
        }

        /// <summary>
        /// Plays an audio clip in a 2D audio environment.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AudioSource PlayClip2D(in DelayedAudioData data, float volumeMultiplier, AudioChannel channel = AudioChannel.Sfx)
        {
            return data.IsPlayable
                ? PlayClip2D(data.Clip, data.Volume * volumeMultiplier, data.Delay, channel)
                : null;
        }

        /// <summary>
        /// Plays an audio clip at a specified position in 3D space.
        /// </summary>
        public AudioSource PlayClips3D(AudioSequence sequence, Vector3 position, float speed = 1f, AudioChannel channel = AudioChannel.Sfx)
        {
            if (!sequence.IsPlayable)
                return null;

            AudioSource source = GetAvailable3DAudioSource();
            source.transform.position = position;
            source.outputAudioMixerGroup = GetMixerGroup(channel);
            sequence.PlaySequence(source, _coroutineHandler, speed);
            return null;
        }

        /// <summary>
        /// Plays an audio clip at the position of a specified transform after a delay.
        /// </summary>
        public AudioSource PlayClips3D(AudioSequence sequence, Transform transform, float speed = 1f, AudioChannel channel = AudioChannel.Sfx)
        {
            if (!sequence.IsPlayable)
                return null;

            AudioSource source = GetAvailable3DAudioSource();
            source.transform.SetParent(transform);
            source.outputAudioMixerGroup = GetMixerGroup(channel);
            sequence.PlaySequence(source, _coroutineHandler, speed);
            return source;
        }

        /// <summary>
        /// Plays an audio clip in a 2D audio environment.
        /// </summary>
        public AudioSource PlayClips2D(AudioSequence sequence, float speed = 1f, AudioChannel channel = AudioChannel.Sfx)
        {
            if (!sequence.IsPlayable)
                return null;

            AudioSource source = GetAvailable2DAudioSource();
            source.outputAudioMixerGroup = GetMixerGroup(channel);
            sequence.PlaySequence(source, _coroutineHandler, speed);
            return source;
        }
    }
}