using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Audio
{
    /// <summary>
    /// NOT INSTANTIATED TODAY: its only consumer is <see cref="BackroomsAudioSystem"/>, which is
    /// itself disabled on purpose (see the reason in that file's header). Neither GUID appears in
    /// any scene, prefab or asset. Both come back together or not at all.
    ///
    /// Per-lamp 3D fluorescent hum. Added to each "FluorescentLight" GameObject;
    /// in Awake it attaches a looping spatial AudioSource (plus a short reverb
    /// filter) that plays a procedurally-synthesised mains hum.
    ///
    /// The clip is identical for every lamp, so it is generated once and shared
    /// (<see cref="GenerateHum"/>); per-lamp character comes from a small random
    /// pitch offset. Instances register in <see cref="Active"/> so the global
    /// BackroomsAudioSystem can query nearby lamps without scanning the scene.
    /// </summary>
    public sealed class FluorescentAudio : MonoBehaviour
    {
        /// <summary>Every enabled lamp, for the global audio director to read.</summary>
        public static readonly List<FluorescentAudio> Active = new List<FluorescentAudio>();

        /// <summary>The looping spatial source this lamp drives.</summary>
        public AudioSource Source { get; private set; }

        // One clip shared by every lamp instead of one identical clip each.
        private static AudioClip _sharedHum;

        /// <summary>
        /// Nominal volume the global system drives (humVolume, and the flicker
        /// fade). The live <see cref="Source"/> volume is this scaled by the
        /// vertical-isolation fade — this component is the sole writer of
        /// Source.volume so the two effects compose instead of fighting.
        /// </summary>
        public float baseVolume = 0.3f;

        private Transform _listener;
        private float _vertTimer;
        private float _vertFade = 1f;
        private const float VerticalCutoff = 3f; // ~3/4 of LayerHeight → silent beyond

        private void Awake()
        {
            // AUDIO DESACTIVADO por petición — el hum 3D por lámpara molestaba (cientos de
            // AudioSource simultáneos, uno por lámpara × muchas lámparas × muchos chunks).
            // Se comenta TODA la creación de audio: AudioSource + AudioReverbFilter + el clip
            // sintetizado. Source queda null; los consumidores (BackroomsAudioSystem
            // .TuneLampsAndFindNearest / .TryStartFlicker y el Update de abajo) ya hacen guard
            // contra Source == null, así que no hay NRE ni hum. Para reactivar: descomenta.
            //
            // if (_sharedHum == null)
            //     _sharedHum = GenerateHum();
            //
            // var src = gameObject.AddComponent<AudioSource>();
            // src.clip         = _sharedHum;
            // src.loop         = true;
            // src.spatialBlend = 1f;                              // fully 3D
            // src.minDistance  = 0.5f;                            // full volume within
            // src.maxDistance  = 5f;                              // silent beyond
            // src.rolloffMode  = AudioRolloffMode.Custom;         // natural proximity gradient
            // src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, new AnimationCurve(
            //     new Keyframe(0f,   1f),    // 0 m   — full volume
            //     new Keyframe(0.3f, 0.8f),  // 1.5 m — still strong
            //     new Keyframe(0.7f, 0.2f),  // 3.5 m — almost gone
            //     new Keyframe(1f,   0f)));  // 5 m   — silent
            // src.volume       = baseVolume;                      // scaled by vertical fade in Update
            // src.pitch        = 1f + Random.Range(-0.02f, 0.02f); // slight per-lamp detune
            // src.dopplerLevel = 0f;                              // stationary lamp
            // src.playOnAwake  = false;
            // src.Play();
            // Source = src;
            //
            // var reverb = gameObject.AddComponent<AudioReverbFilter>();
            // reverb.reverbPreset     = AudioReverbPreset.Room;   // nearby-wall reflections
            // reverb.room             = -1000;
            // reverb.roomHF           = -100;
            // reverb.decayTime        = 0.8f;
            // reverb.reflectionsLevel = -300;
            // reverb.reverbLevel      = 100;
        }

        private void OnEnable()  => Active.Add(this);
        private void OnDisable() => Active.Remove(this);

        private void Update()
        {
            // Re-check the vertical gap to the listener every 0.1 s (cheap throttle);
            // apply the resulting volume every frame so the flicker fade stays smooth.
            _vertTimer -= Time.deltaTime;
            if (_vertTimer <= 0f)
            {
                _vertTimer = 0.1f;
                _vertFade  = ComputeVerticalFade();
            }
            if (Source != null) Source.volume = baseVolume * _vertFade;
        }

        // Lamps on other layers must not bleed through: fade out with vertical
        // distance to the listener, fully silent past VerticalCutoff.
        private float ComputeVerticalFade()
        {
            if (_listener == null)
            {
                var al = FindAnyObjectByType<AudioListener>();
                if (al != null) _listener = al.transform;
            }
            if (_listener == null) return 1f;

            float verticalDist = Mathf.Abs(transform.position.y - _listener.position.y);
            return verticalDist > VerticalCutoff ? 0f : 1f - verticalDist / VerticalCutoff;
        }

        /// <summary>
        /// Synthesises a seamless-looping fluorescent mains hum: a 60 Hz
        /// fundamental (flickering ±2% every 0.1 s) over 120/180/240 Hz harmonics,
        /// normalised to ±0.8, with a 128-sample head/tail crossfade so the loop
        /// wraps without a click.
        /// </summary>
        public static AudioClip GenerateHum(int sampleRate = 44100, float duration = 2f)
        {
            int sc    = Mathf.Max(256, Mathf.RoundToInt(sampleRate * duration));
            int fade  = Mathf.Min(128, sc / 2);
            int total = sc + fade;                  // extra tail to crossfade into the head

            var buf = new float[total];
            var rng = new System.Random(6060);      // deterministic ignition flicker
            int flickerSamples = Mathf.Max(1, Mathf.RoundToInt(sampleRate * 0.1f));
            const double TwoPi = 2.0 * System.Math.PI;

            double fundFreq  = 60.0;
            double fundPhase = 0.0;                  // accumulated so the freq can wobble click-free
            float  maxAbs    = 0f;

            for (int i = 0; i < total; i++)
            {
                if (i % flickerSamples == 0)         // re-roll the fundamental ±2% every 0.1 s
                    fundFreq = 60.0 * (1.0 + (rng.NextDouble() * 2.0 - 1.0) * 0.02);

                fundPhase += TwoPi * fundFreq / sampleRate;
                double t = (double)i / sampleRate;

                double s = 0.50 * System.Math.Sin(fundPhase)             // 60 Hz mains
                         + 0.25 * System.Math.Sin(TwoPi * 120.0 * t)     // 120 Hz transformer
                         + 0.15 * System.Math.Sin(TwoPi * 180.0 * t)     // 180 Hz aged tube
                         + 0.08 * System.Math.Sin(TwoPi * 240.0 * t);    // 240 Hz ballast

                buf[i] = (float)s;
                float a = buf[i] < 0f ? -buf[i] : buf[i];
                if (a > maxAbs) maxAbs = a;
            }

            float norm = maxAbs > 1e-6f ? 0.8f / maxAbs : 1f;   // normalise to ±0.8
            for (int i = 0; i < total; i++) buf[i] *= norm;

            for (int i = 0; i < fade; i++)           // crossfade the tail-continuation into the head
            {
                float w = (float)i / fade;
                buf[i] = buf[i] * w + buf[sc + i] * (1f - w);
            }

            var data = new float[sc];
            System.Array.Copy(buf, data, sc);

            var clip = AudioClip.Create("FluorescentHum", sc, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
