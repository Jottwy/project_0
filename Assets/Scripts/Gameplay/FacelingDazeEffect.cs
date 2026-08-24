using PolymindGames;
using PolymindGames.MovementSystem;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-094 Enmienda 7 — the AFTERMATH of a seizure: your ears ring and your legs do not work
    /// properly for a while.
    ///
    /// Separate from <see cref="PhantomAttackHandler"/>'s cinematic on purpose, and not just for
    /// tidiness: the two have completely different lifetimes. The hold is under a second and a half
    /// and owns the camera; this outlives it by many seconds, has to survive the player walking
    /// away, respawning or being seized again mid-daze, and touches nothing the cinematic touches.
    /// Folding them together would mean one timer guarding two unrelated things.
    ///
    /// THE DEAFNESS is an <see cref="AudioLowPassFilter"/> added to the listener at runtime, NOT a
    /// mixer parameter. The mixer route would need a new exposed parameter on the asset, and this
    /// project has already paid for that once: a `SetFloat` against a name that does not exist logs
    /// an error WITH A STACK TRACE on every call (229k lines in one session), and renaming exposed
    /// parameters by hand in the YAML does not reach the runtime at all. A component added and
    /// removed in code has neither failure mode and leaves the mixer asset untouched.
    ///
    /// THE STAGGER reuses `AddStateBlocker`, the same lever the grab and the knockdown already use
    /// — it blocks RUN only. You keep walking and keep aiming; what you lose is the ability to
    /// sprint away from the four children still around you, which is precisely the cost the moment
    /// is supposed to have.
    ///
    /// Self-bootstraps; fully removable (delete the file and a seizure simply stops leaving marks).
    /// </summary>
    public sealed class FacelingDazeEffect : MonoBehaviour
    {
        /// <summary>How long the ringing and the stagger last.</summary>
        private const float DazeSeconds = 7.0f;
        /// <summary>
        /// Cutoff at the worst of it (Hz). 380 is muffled-underwater, not deaf: you must still be
        /// able to hear where the pack is, or the effect stops being frightening and starts being
        /// an accessibility problem — you would be blind AND deaf while surrounded.
        /// </summary>
        private const float DazeCutoffHz = 380f;
        /// <summary>Unfiltered. Unity's own default, restored explicitly rather than assumed.</summary>
        private const float NormalCutoffHz = 22000f;
        /// <summary>
        /// Fraction of the daze spent at full muffle before it starts clearing. The recovery is the
        /// long part: a hard cut back to normal audio would land as a bug rather than as relief.
        /// </summary>
        private const float DazeHoldFraction = 0.35f;

        private static FacelingDazeEffect _instance;

        private float _timer;
        private AudioLowPassFilter _filter;
        private IMovementControllerCC _block;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[FacelingDazeEffect]");
            _instance = go.AddComponent<FacelingDazeEffect>();
            DontDestroyOnLoad(go);
        }

        /// <summary>
        /// Starts (or refreshes) the daze. Called by the seizure cinematic at the moment it takes
        /// hold rather than when it lets go, so the ringing is already there when control returns.
        /// </summary>
        public static void Begin()
        {
            if (_instance != null)
                _instance.BeginLocal();
        }

        private void BeginLocal()
        {
            // Refresh, never stack: a second seizure inside the window restarts the clock, and the
            // blocker is added once. The grab in PhantomAttackHandler carries a comment about the
            // bug that shape prevents — two blockers under one key, one removal, a player that can
            // never run again.
            _timer = DazeSeconds;

            if (_block == null)
            {
                var movement = LocalPlayerLocator.Find<PlayerMovementController>();
                if (movement != null)
                {
                    movement.AddStateBlocker(this, MovementStateType.Run);
                    _block = movement;
                }
            }
        }

        private void Update()
        {
            if (_timer <= 0f)
                return;

            _timer -= Time.unscaledDeltaTime;
            if (_timer <= 0f)
            {
                Clear();
                return;
            }

            var filter = ResolveFilter();
            if (filter == null)
                return;

            // Full muffle for the first stretch, then an ease back up. `t` runs 0..1 across the
            // recovery only, so the hold is genuinely flat rather than a slope that starts at once.
            float elapsed01 = 1f - Mathf.Clamp01(_timer / DazeSeconds);
            float t = Mathf.InverseLerp(DazeHoldFraction, 1f, elapsed01);
            // Interpolated in LOG space: pitch and "muffledness" are both logarithmic, so a linear
            // ramp between 380 and 22000 spends almost all its time sounding already-recovered.
            filter.cutoffFrequency = Mathf.Exp(
                Mathf.Lerp(Mathf.Log(DazeCutoffHz), Mathf.Log(NormalCutoffHz), t));
        }

        private void Clear()
        {
            _timer = 0f;

            if (_filter != null)
            {
                _filter.cutoffFrequency = NormalCutoffHz;
                _filter.enabled = false;
            }

            if (_block != null)
            {
                _block.RemoveStateBlocker(this, MovementStateType.Run);
                _block = null;
            }
        }

        /// <summary>
        /// The low-pass on the active listener, added the first time it is needed.
        ///
        /// Re-resolved rather than cached across scenes: the listener lives on the player camera,
        /// which does not survive a scene load, and a filter component on a destroyed object is a
        /// null that only shows up mid-scare.
        /// </summary>
        private AudioLowPassFilter ResolveFilter()
        {
            if (_filter != null)
            {
                _filter.enabled = true;
                return _filter;
            }

            var listener = FindFirstObjectByType<AudioListener>();
            if (listener == null)
                return null;

            _filter = listener.GetComponent<AudioLowPassFilter>();
            if (_filter == null)
                _filter = listener.gameObject.AddComponent<AudioLowPassFilter>();
            _filter.enabled = true;
            return _filter;
        }

        private void OnDestroy()
        {
            // A domain reload or scene teardown mid-daze must not leave the player permanently
            // unable to run: the blocker lives on a component that outlives this one.
            Clear();
        }
    }
}
