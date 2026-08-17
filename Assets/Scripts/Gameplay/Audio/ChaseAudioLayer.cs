using BackroomsSurvival.Net;
using PolymindGames; // AudioManager / AudioChannel
using UnityEngine;

namespace BackroomsSurvival.Gameplay.Audio
{
    /// <summary>
    /// ADR-080 point 7 — THE CHASE HAS A SOUND. While something is coming at you with its skin off,
    /// a low drone and a heartbeat rise underneath everything else, and they fall away slowly once it
    /// is gone.
    ///
    /// Why it exists: the reveal was a one-shot scream and then silence. A creature sprinting at you
    /// through a corridor sounded like a player jogging, so the loudest moment of the whole game was
    /// its first half-second. This is the sustained layer that carries the rest of it.
    ///
    /// IT READS ONLY <c>revealed</c> AND DISTANCE, which is the entire reason it is allowed to exist.
    /// `revealed` is public knowledge on the pose relay (ADR-038): every client already knows which
    /// proxies show a real form, so reacting to it tells an observer nothing they could not see with
    /// their eyes. Reading anything private — hunger, state, phantom ids — would make this an oracle
    /// and break the indistinguishability ADR-016 §1 protects. A disguised creature sounds like a
    /// player here, on purpose, forever.
    ///
    /// SYNTHESISED, NOT AUTHORED (the SpraySfx pattern): both voices are generated deterministically
    /// at startup, so this ships working with no assets to bake and nothing to lose on a re-import.
    ///
    /// Routed to the MUSIC bus, never left on Master. An AudioSource without
    /// <c>outputAudioMixerGroup</c> comes out of Master and NO volume slider touches it — that cost
    /// three commits once (see FluorescentHumDirector) and is not being paid again. Music rather than
    /// Sfx because this is score, not a thing in the world: it has no position, and a player who
    /// turns music down is asking for exactly this to be quieter.
    ///
    /// 2D on purpose (<c>spatialBlend = 0</c>): the drone is not emitted by the creature. Its own
    /// screams, breath and footsteps are already spatial and belong to the proxy.
    ///
    /// Removable: delete this file and the chase goes back to being silent between screams. Nothing
    /// else reads it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ChaseAudioLayer : MonoBehaviour
    {
        /// <summary>Beyond this, a revealed creature contributes nothing. Inside it, intensity ramps.</summary>
        private const float MaxDistance = 25f;

        /// <summary>Inside this, intensity is full — it is on top of you.</summary>
        private const float FullDistance = 6f;

        // Authored LOW. The lesson from the ambience pass: this project's ambient layer sits around
        // 0.05 and had to be turned down twice before it stopped competing with the sounds that
        // carry information. A chase layer that drowns the footsteps behind you actively removes
        // tension instead of adding it.
        private const float DroneVolume = 0.10f;
        private const float HeartVolume = 0.18f;

        // Fast in, slow out, and the asymmetry is the point: the arrival should land, and the relief
        // afterwards should not be instant — you keep listening for a few seconds after it is gone.
        private const float AttackSeconds = 0.5f;
        private const float ReleaseSeconds = 4f;

        /// <summary>Beats per minute at full intensity, and at the edge of the ramp.</summary>
        private const float HeartBpmNear = 132f;
        private const float HeartBpmFar = 74f;

        private const int SampleRate = 44100;

        private static ChaseAudioLayer _instance;
        private static bool _quitting;

        private AudioSource _drone;
        private AudioSource _heart;
        private AudioClip _heartClip;
        private bool _routed;
        private float _level;
        private float _beatTimer;
        private RemotePlayerManager _manager;
        private Transform _listener;

        // Statics survive "Enter Play Mode" without a domain reload, so a stale `_quitting` from the
        // previous session would leave the layer permanently unable to start. Same guard, same
        // reason, as FluorescentHumDirector.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _quitting = false;
            _instance = null;
        }

        /// <summary>
        /// Self-starting after the scene loads: there is nothing to wire in a prefab and no scene to
        /// edit, which also means no re-bake and no serialized reference that a future rename can
        /// silently break. It costs one idle component when nothing is revealed.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null || _quitting)
                return;

            var go = new GameObject("ChaseAudioLayer");
            _instance = go.AddComponent<ChaseAudioLayer>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(this); return; }
            _instance = this;

            _drone = BuildSource(BuildDroneClip(), loop: true);
            _heartClip = BuildHeartClip();
            _heart = BuildSource(null, loop: false);
        }

        private void OnApplicationQuit() => _quitting = true;

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void Update()
        {
            float target = ResolveIntensity();

            // Ramp measured against the working volume rather than against 1, or "0.5 s" would be a
            // fifth of that in practice (the SpraySfx lesson).
            float rate = target > _level ? AttackSeconds : ReleaseSeconds;
            _level = Mathf.MoveTowards(_level, target, Time.unscaledDeltaTime / Mathf.Max(1e-4f, rate));

            if (_level <= 0.001f)
            {
                // Silence means STOPPED, not muted: a playing AudioSource costs a voice either way.
                if (_drone != null && _drone.isPlaying) _drone.Stop();
                _beatTimer = 0f;
                return;
            }

            Route();

            if (_drone != null)
            {
                _drone.volume = DroneVolume * _level;
                if (!_drone.isPlaying) _drone.Play();
            }

            // The heartbeat is a one-shot per beat rather than a loop, so its RATE can follow the
            // intensity. A looping clip would need a pitch shift to speed up, and pitching a heart
            // sound up turns it into a different, smaller animal.
            if (_heart != null && _heartClip != null)
            {
                float bpm = Mathf.Lerp(HeartBpmFar, HeartBpmNear, _level);
                _beatTimer -= Time.unscaledDeltaTime;
                if (_beatTimer <= 0f)
                {
                    _beatTimer = 60f / Mathf.Max(1f, bpm);
                    _heart.PlayOneShot(_heartClip, HeartVolume * _level);
                }
            }
        }

        /// <summary>
        /// 0..1 from the NEAREST revealed proxy. Nothing revealed (the overwhelming majority of the
        /// time, and always in a session with no creatures) costs one dictionary walk.
        /// </summary>
        private float ResolveIntensity()
        {
            // NOT LocalPlayerLocator: that helper skips anything under a RemotePlayerManager, and
            // `GetComponentInParent` includes the object itself — so it would never find the manager.
            if (_manager == null)
                _manager = FindFirstObjectByType<RemotePlayerManager>();
            if (_manager == null)
                return 0f;

            // The listener is the local camera. Re-resolved when missing rather than cached forever:
            // the player rig is rebuilt on respawn (the StatInterpolator lesson — caching across a
            // rebuild means writing to a corpse in silence).
            if (_listener == null)
            {
                var cam = Camera.main;
                _listener = cam != null ? cam.transform : null;
            }
            if (_listener == null)
                return 0f;

            float nearest = float.MaxValue;
            foreach (var kvp in _manager.ActivePlayers)
            {
                var view = kvp.Value;
                // `RemotePlayerView` is a plain class, not a component: its transform is `root`, and
                // a pooled view that has been released has none.
                if (view == null || !view.revealed || view.root == null)
                    continue;

                float d = Vector3.Distance(_listener.position, view.root.position);
                if (d < nearest)
                    nearest = d;
            }

            if (nearest >= MaxDistance)
                return 0f;

            return Mathf.Clamp01(Mathf.InverseLerp(MaxDistance, FullDistance, nearest));
        }

        private AudioSource BuildSource(AudioClip clip, bool loop)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.clip = clip;
            src.loop = loop;
            src.playOnAwake = false;
            src.volume = 0f;
            src.spatialBlend = 0f; // score, not a thing in the room
            src.dopplerLevel = 0f;
            return src;
        }

        /// <summary>
        /// To the MUSIC bus. Lazy and retryable because AudioManager may not exist yet — or at all,
        /// in a bare test scene, where this then plays unmixed rather than throwing.
        /// </summary>
        private void Route()
        {
            if (_routed)
                return;

            var mgr = AudioManager.Instance;
            if (mgr == null)
                return;

            var group = mgr.GetMixerGroup(AudioChannel.Music);
            if (group == null)
                return;

            if (_drone != null) _drone.outputAudioMixerGroup = group;
            if (_heart != null) _heart.outputAudioMixerGroup = group;
            _routed = true;
        }

        /// <summary>
        /// The drone: two detuned low sines plus a slow tremolo. Deliberately not noise — noise
        /// masks the footsteps and screams that carry information, whereas a low tone sits UNDER
        /// them. The two frequencies beat against each other so the tone never sits still.
        /// </summary>
        private static AudioClip BuildDroneClip()
        {
            const float seconds = 4f; // loops seamlessly: both partials complete whole cycles
            int samples = (int)(SampleRate * seconds);
            var data = new float[samples];

            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)SampleRate;
                // 41 Hz and 43.5 Hz: a 2.5 Hz beat, slow enough to read as unease rather than as a
                // wobble effect. Both are whole numbers of cycles over 4 s (164 and 174).
                float a = Mathf.Sin(2f * Mathf.PI * 41f * t);
                float b = Mathf.Sin(2f * Mathf.PI * 43.5f * t) * 0.7f;
                // Slow swell, one full cycle across the loop, so the seam is at matching amplitude.
                float swell = 0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * t / seconds);
                data[i] = (a + b) * 0.45f * swell;
            }

            var clip = AudioClip.Create("ChaseDrone", samples, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>
        /// One heartbeat: two thumps (lub-dub), each a fast-decaying low sine with a pitch drop.
        /// Short by design — the RATE is what the player reads, not the timbre.
        /// </summary>
        private static AudioClip BuildHeartClip()
        {
            const float seconds = 0.42f;
            int samples = (int)(SampleRate * seconds);
            var data = new float[samples];

            // Second thump is quieter and lands a beat after the first: that asymmetry is what makes
            // two thumps read as one heart instead of as two hits.
            AddThump(data, startSeconds: 0f, amplitude: 1f);
            AddThump(data, startSeconds: 0.16f, amplitude: 0.62f);

            var clip = AudioClip.Create("ChaseHeartbeat", samples, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static void AddThump(float[] data, float startSeconds, float amplitude)
        {
            int start = (int)(startSeconds * SampleRate);
            int length = (int)(0.13f * SampleRate);
            float phase = 0f;

            for (int i = 0; i < length && start + i < data.Length; i++)
            {
                float t = i / (float)SampleRate;
                // 62 Hz falling to ~34 Hz over the thump: a chest, not a beep. Integrating the
                // frequency (rather than sampling sin(2πft) with a moving f) keeps the phase
                // continuous — otherwise the sweep clicks.
                float freq = Mathf.Lerp(62f, 34f, t / 0.13f);
                phase += 2f * Mathf.PI * freq / SampleRate;
                float env = Mathf.Exp(-t * 26f);
                data[start + i] += Mathf.Sin(phase) * env * amplitude * 0.8f;
            }
        }
    }
}
