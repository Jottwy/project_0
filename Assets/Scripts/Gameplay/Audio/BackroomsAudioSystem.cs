using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Audio
{
    /// <summary>
    /// Global Backrooms audio director (singleton). On Start it consolidates the
    /// AudioListener onto the Player and adds a Hallway reverb zone. Each frame it
    /// keeps the lamp sources matched to the inspector knobs, muffles the mix
    /// toward an "oppressive silence" low-pass when the Player is far from every
    /// lamp, and periodically makes one nearby lamp flicker.
    /// </summary>
    public sealed class BackroomsAudioSystem : MonoBehaviour
    {
        public static BackroomsAudioSystem Instance { get; private set; }

        [SerializeField] private float humVolume       = 0.3f;
        [SerializeField] private float minDistance      = 0.5f;
        [SerializeField] private float maxDistance      = 5f;
        [SerializeField] private float flickerInterval = 30f;
        [SerializeField] private float silenceRadius    = 8f;

        private const float FlickerRadius      = 20f;
        private const float ListenerOpen       = 22000f; // unfiltered cutoff
        private const float ListenerMuffled    = 800f;   // oppressive-silence cutoff
        private const float SilenceDownSeconds = 2f;     // open → muffled
        private const float SilenceUpSeconds   = 1f;     // muffled → open

        private Transform _player;
        private AudioLowPassFilter _lowPass;
        private float _flickerTimer;
        private FluorescentAudio _flickeringLamp;
        private readonly List<FluorescentAudio> _nearby = new List<FluorescentAudio>();

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        private void Start()
        {
            var playerGo = GameObject.FindWithTag("Player");
            if (playerGo == null)
            {
                Debug.LogWarning("[BackroomsAudioSystem] No Player tag — audio director idle.");
                enabled = false;
                return;
            }
            _player = playerGo.transform;

            ConsolidateListener(playerGo);

            var zone = playerGo.GetComponent<AudioReverbZone>();
            if (zone == null) zone = playerGo.AddComponent<AudioReverbZone>();
            zone.minDistance  = 0.5f;
            zone.maxDistance  = 8f;
            zone.reverbPreset = AudioReverbPreset.Hallway; // open, empty-corridor space

            _lowPass = playerGo.GetComponent<AudioLowPassFilter>();
            if (_lowPass == null) _lowPass = playerGo.AddComponent<AudioLowPassFilter>();
            _lowPass.cutoffFrequency = ListenerOpen;

            _flickerTimer = flickerInterval;
        }

        // Exactly one AudioListener, on the Player root. SpawnPlayer puts one on
        // the camera — destroy that (and any other) so Unity doesn't warn about
        // multiple listeners.
        private static void ConsolidateListener(GameObject playerGo)
        {
            var keep = playerGo.GetComponent<AudioListener>();
            if (keep == null) keep = playerGo.AddComponent<AudioListener>();
            foreach (var l in FindObjectsByType<AudioListener>(FindObjectsSortMode.None))
                if (l != keep) Destroy(l);
        }

        private void Update()
        {
            if (_player == null) return;

            float nearest = TuneLampsAndFindNearest();
            UpdateSilenceFilter(nearest);
            UpdateFlickerTimer();
        }

        // Keep every lamp's source matched to the inspector knobs and return the
        // distance to the closest lamp. The currently-flickering lamp is skipped
        // so its volume fade isn't overwritten.
        private float TuneLampsAndFindNearest()
        {
            float nearestSqr = float.MaxValue;
            var list = FluorescentAudio.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var lamp = list[i];
                if (lamp == null || lamp.Source == null) continue;

                lamp.Source.minDistance = minDistance;
                lamp.Source.maxDistance = maxDistance;
                // Drive the lamp's base volume; FluorescentAudio applies it scaled
                // by its vertical fade. Skip the flickering lamp so its fade holds.
                if (lamp != _flickeringLamp) lamp.baseVolume = humVolume;

                float d = (lamp.transform.position - _player.position).sqrMagnitude;
                if (d < nearestSqr) nearestSqr = d;
            }
            return nearestSqr == float.MaxValue ? float.MaxValue : Mathf.Sqrt(nearestSqr);
        }

        // Far from every lamp → ramp the listener low-pass down to a muffled
        // cutoff over 2 s; near a lamp → open it back up over 1 s.
        private void UpdateSilenceFilter(float nearest)
        {
            bool  far     = nearest > silenceRadius;
            float target  = far ? ListenerMuffled : ListenerOpen;
            float seconds = far ? SilenceDownSeconds : SilenceUpSeconds;
            float speed   = (ListenerOpen - ListenerMuffled) / Mathf.Max(0.0001f, seconds);
            _lowPass.cutoffFrequency =
                Mathf.MoveTowards(_lowPass.cutoffFrequency, target, speed * Time.deltaTime);
        }

        private void UpdateFlickerTimer()
        {
            _flickerTimer -= Time.deltaTime;
            if (_flickerTimer > 0f) return;
            _flickerTimer = flickerInterval;
            TryStartFlicker();
        }

        // Pick a random lamp within FlickerRadius of the Player and drop it out.
        private void TryStartFlicker()
        {
            if (_flickeringLamp != null) return; // one flicker at a time

            _nearby.Clear();
            float r2 = FlickerRadius * FlickerRadius;
            var list = FluorescentAudio.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var lamp = list[i];
                if (lamp == null || lamp.Source == null) continue;
                if ((lamp.transform.position - _player.position).sqrMagnitude <= r2)
                    _nearby.Add(lamp);
            }
            if (_nearby.Count == 0) return;

            StartCoroutine(FlickerRoutine(_nearby[Random.Range(0, _nearby.Count)]));
        }

        // Fade to silence (0.1 s) → brief gap (0.05–0.2 s) → fade back (0.15 s):
        // one lamp sounds as if it briefly fails.
        private IEnumerator FlickerRoutine(FluorescentAudio lamp)
        {
            _flickeringLamp = lamp;

            yield return FadeBaseVolume(lamp, lamp.baseVolume, 0f, 0.1f);
            yield return new WaitForSeconds(Random.Range(0.05f, 0.2f));
            yield return FadeBaseVolume(lamp, 0f, humVolume, 0.15f);

            _flickeringLamp = null;
        }

        // Fade the lamp's base volume (not Source.volume directly) so the flicker
        // composes with FluorescentAudio's vertical-isolation fade.
        private static IEnumerator FadeBaseVolume(
            FluorescentAudio lamp, float from, float to, float dur)
        {
            float e = 0f;
            while (e < dur)
            {
                if (lamp == null) yield break; // lamp streamed out mid-flicker
                e += Time.deltaTime;
                lamp.baseVolume = Mathf.Lerp(from, to, e / dur);
                yield return null;
            }
            if (lamp != null) lamp.baseVolume = to;
        }
    }
}
