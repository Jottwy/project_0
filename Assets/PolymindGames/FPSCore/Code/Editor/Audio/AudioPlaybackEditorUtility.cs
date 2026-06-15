using Unity.EditorCoroutines.Editor;
using UnityEngine.Audio;
using UnityEditor;
using UnityEngine;

namespace PolymindGames.Editor
{
    public static class AudioPlaybackEditorUtility
    {
        private static EditorCoroutine _activeSequence;
        private static AudioSource _audioSource;
        
        public static readonly GUIContent PlayButtonStyle = EditorGUIUtility.IconContent("d_Play");
        
        public static void PlayClip(AudioResource clip, float volume, float pitch = 1f)
        {
            if (clip == null)
                return;

            if (_activeSequence != null)
                EditorCoroutineUtility.StopCoroutine(_activeSequence);

            EnsureAudioSource();
            ResetAudioSource();

            _audioSource.resource = clip;
            _audioSource.volume = volume;
            _audioSource.pitch = pitch;
            _audioSource.Play();
        }

        public static void PlaySequence(AudioSequence sequence)
        {
            if (sequence == null)
                return;

            EnsureAudioSource();
            ResetAudioSource();

            _activeSequence = EditorCoroutineUtility.StartCoroutineOwnerless(sequence.PlaySequence(_audioSource, 1f));
        }

        private static void EnsureAudioSource()
        {
            if (_audioSource == null)
            {
                _audioSource = EditorUtility.CreateGameObjectWithHideFlags(
                    "Audio Preview", HideFlags.HideAndDontSave, typeof(AudioSource)
                ).GetComponent<AudioSource>();
            }
        }

        private static void ResetAudioSource()
        {
            if (_activeSequence != null)
            {
                EditorCoroutineUtility.StopCoroutine(_activeSequence);
                _activeSequence = null;
            }

            _audioSource.Stop();
        }

        public static void Cleanup()
        {
            if (_audioSource != null && !_audioSource.isPlaying && !_audioSource.playOnAwake)
            {
                ResetAudioSource();
                Object.DestroyImmediate(_audioSource.gameObject);
                _audioSource = null;
            }
        }

        // public static AudioClip CombineDelayedAudioClips(DelayedAudioData[] delayedClips)
        // {
        //     if (delayedClips == null || delayedClips.Length == 0)
        //         return null;
        //
        //     float totalDuration = 0f;
        //     var clip = (AudioClip)delayedClips[0].Clip;
        //     int channels = clip.channels;
        //     int frequency = clip.frequency;
        //
        //     foreach (var delayedClip in delayedClips)
        //     {
        //         float clipEndTime = delayedClip.Delay + delayedClip.Clip.length;
        //         totalDuration = Mathf.Max(totalDuration, clipEndTime);
        //     }
        //
        //     int totalSamples = Mathf.CeilToInt(totalDuration * frequency) * channels;
        //     float[] combinedSamples = new float[totalSamples];
        //
        //     foreach (var delayedClip in delayedClips)
        //     {
        //         int startSample = Mathf.FloorToInt(delayedClip.Delay * frequency) * channels;
        //         float[] clipSamples = new float[delayedClip.Clip.samples * channels];
        //         delayedClip.Clip.GetData(clipSamples, 0);
        //
        //         for (int i = 0; i < clipSamples.Length; i++)
        //         {
        //             int sampleIndex = startSample + i;
        //             if (sampleIndex < combinedSamples.Length)
        //             {
        //                 combinedSamples[sampleIndex] += clipSamples[i] * delayedClip.Volume;
        //             }
        //         }
        //     }
        //
        //     var combinedClip = AudioClip.Create("CombinedAudioWithDelays", totalSamples / channels, channels, frequency, false);
        //     combinedClip.SetData(combinedSamples, 0);
        //     return combinedClip;
        // }

        // public static void SaveAudioClipAsWavUsingDialog(AudioClip clip)
        // {
        //     if (clip == null)
        //     {
        //         Debug.LogWarning("No AudioClip provided to save.");
        //         return;
        //     }
        //
        //     // Open the save file panel
        //     string path = EditorUtility.SaveFilePanel(
        //         "Save Audio Clip as WAV",
        //         Application.dataPath,
        //         clip.name + ".wav",
        //         "wav"
        //     );
        //
        //     if (string.IsNullOrEmpty(path))
        //     {
        //         Debug.Log("Save operation cancelled.");
        //         return;
        //     }
        //
        //     SaveAudioClipAsWav(clip, path);
        //     Debug.Log($"Audio clip saved to {path}");
        // }

        // private static void SaveAudioClipAsWav(AudioClip clip, string path)
        // {
        //     var samples = new float[clip.samples * clip.channels];
        //     clip.GetData(samples, 0);
        //
        //     byte[] wavFile = ConvertToWav(samples, clip.channels, clip.frequency);
        //     File.WriteAllBytes(path, wavFile);
        //     Debug.Log($"Saved WAV to {path}");
        // }
        //
        // private static byte[] ConvertToWav(float[] samples, int channels, int frequency)
        // {
        //     int byteRate = frequency * channels * 2; // 16-bit audio
        //     int totalLength = 44 + samples.Length * 2;
        //
        //     using (var memoryStream = new MemoryStream(totalLength))
        //     using (var writer = new BinaryWriter(memoryStream))
        //     {
        //         // WAV Header
        //         writer.Write(new[]
        //         {
        //             'R', 'I', 'F', 'F'
        //         });
        //         writer.Write(totalLength - 8);
        //         writer.Write(new[]
        //         {
        //             'W', 'A', 'V', 'E'
        //         });
        //         writer.Write(new[]
        //         {
        //             'f', 'm', 't', ' '
        //         });
        //         writer.Write(16);       // PCM
        //         writer.Write((short)1); // Audio format
        //         writer.Write((short)channels);
        //         writer.Write(frequency);
        //         writer.Write(byteRate);
        //         writer.Write((short)(channels * 2)); // Block align
        //         writer.Write((short)16);             // Bits per sample
        //
        //         // Data chunk
        //         writer.Write(new[]
        //         {
        //             'd', 'a', 't', 'a'
        //         });
        //         writer.Write(samples.Length * 2);
        //
        //         foreach (var sample in samples)
        //         {
        //             short intSample = (short)(Mathf.Clamp(sample, -1f, 1f) * short.MaxValue);
        //             writer.Write(intSample);
        //         }
        //
        //         return memoryStream.ToArray();
        //     }
        // }
    }
}