using System.Collections.Generic;
using BackroomsSurvival.Net;
using PolymindGames;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// L2 client-side stat interpolator (ADR-009 §6). The server owns the survival
    /// stats and ships them in the WorldState snapshot; this component stops the
    /// STP managers' local per-frame drain and instead displays each stat by
    /// interpolating toward the server value over the snapshot window, rendered one
    /// latency margin (~50 ms) behind so it always lerps between two known samples
    /// and never overshoots.
    ///
    /// Four per-stat binders, each with its own "set" hook (the managers are not
    /// uniform — Health has no setter/drain, Stamina is event+blocking driven and
    /// normalized 0..1, Hunger/Thirst are disabled outright). Sanity is handled
    /// elsewhere (SanityEffectsController, ADR-009 §9), not here.
    /// </summary>
    public sealed class StatInterpolator : MonoBehaviour
    {
        [Tooltip("Render this far behind real time (s) so interpolation stays between two received samples.")]
        public float latencyMarginSec = 0.05f;

        private IPCClient _ipc;
        private bool _controlled;

        private HungerBinder _hunger;
        private ThirstBinder _thirst;
        private StaminaBinder _stamina;
        private HealthBinder _health;
        private StatBinder[] _binders;

        private void Awake()
        {
            var hunger = GetComponentInChildren<HungerManager>(true);
            var thirst = GetComponentInChildren<ThirstManager>(true);
            var stamina = GetComponentInChildren<StaminaManager>(true);
            var health = GetComponentInChildren<HealthManager>(true);

            var binders = new List<StatBinder>(4);
            // Hunger/Thirst need the health manager to hook its Respawn event.
            if (health != null)
            {
                if (hunger != null) { _hunger = new HungerBinder(hunger, health); binders.Add(_hunger); }
                if (thirst != null) { _thirst = new ThirstBinder(thirst, health); binders.Add(_thirst); }
                _health = new HealthBinder(health); binders.Add(_health);
            }
            if (stamina != null) { _stamina = new StaminaBinder(stamina); binders.Add(_stamina); }
            _binders = binders.ToArray();
        }

        private void OnEnable()
        {
            if (IPCClient.TryGetInstance(out _ipc))
                _ipc.AddStateListener(OnWorldState);
        }

        private void OnDisable()
        {
            if (_ipc != null)
                _ipc.RemoveStateListener(OnWorldState);
            SetControlled(false); // release the managers back to local behaviour
        }

        private void Update()
        {
            if (_ipc == null && IPCClient.TryGetInstance(out _ipc))
                _ipc.AddStateListener(OnWorldState);

            // Take control while connected; release (resume local drain) when not.
            bool connected = _ipc != null && _ipc.IsConnected;
            if (connected != _controlled)
                SetControlled(connected);

            if (_binders == null)
                return;

            float now = Time.time;
            for (int i = 0; i < _binders.Length; i++)
                _binders[i].Tick(now, latencyMarginSec);
        }

        // 5 Hz (nested in the WorldState snapshot). Update all four targets at once.
        private void OnWorldState(WorldStateMsg state)
        {
            var st = state != null && state.localPlayer != null ? state.localPlayer.stats : null;
            if (st == null)
                return;

            float now = Time.time;
            _hunger?.SetTarget(st.hunger, now);
            _thirst?.SetTarget(st.thirst, now);
            _stamina?.SetTarget(st.stamina / 100f, now); // STP stamina is normalized 0..1
            _health?.SetTarget(st.health, now);
        }

        private void SetControlled(bool on)
        {
            _controlled = on;
            if (_binders == null)
                return;
            for (int i = 0; i < _binders.Length; i++)
                _binders[i].SetControlled(on);
        }

        // ── Binders ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Two-sample interpolation buffer shared by all stats. Holds the previous
        /// and current server samples with arrival times; Tick renders at
        /// (now - latency) interpolating between them (Clamp01 → never overshoot;
        /// holds the last value if the stream stalls).
        /// </summary>
        private abstract class StatBinder
        {
            private float _v0, _v1, _t0, _t1;
            private bool _hasSample;
            protected bool Controlled { get; private set; }

            public void SetTarget(float value, float now)
            {
                if (!_hasSample) { _v0 = value; _t0 = now; }
                else { _v0 = _v1; _t0 = _t1; }
                _v1 = value;
                _t1 = now;
                _hasSample = true;
            }

            // Hard reset: jump the display to value immediately (used on respawn).
            protected void SnapTo(float value, float now)
            {
                _v0 = _v1 = value;
                _t0 = _t1 = now;
                _hasSample = true;
            }

            public void Tick(float now, float latency)
            {
                if (!Controlled || !_hasSample)
                    return;

                float renderT = now - latency;
                float span = _t1 - _t0;
                float f = span > 1e-4f ? Mathf.Clamp01((renderT - _t0) / span) : 1f;
                WriteDisplay(Mathf.Lerp(_v0, _v1, f));
            }

            public void SetControlled(bool on)
            {
                if (Controlled == on)
                    return;
                Controlled = on;
                if (on) TakeControl();
                else ReleaseControl();
            }

            protected abstract void WriteDisplay(float value);
            protected abstract void TakeControl();
            protected abstract void ReleaseControl();
        }

        private sealed class HungerBinder : StatBinder
        {
            private readonly HungerManager _m;
            private readonly IHealthManager _health;

            public HungerBinder(HungerManager m, IHealthManager health)
            {
                _m = m;
                _health = health;
            }

            protected override void WriteDisplay(float value) => _m.Hunger = value;

            protected override void TakeControl()
            {
                _m.enabled = false; // stop the local deltaTime drain
                _health.Respawn += OnRespawn;
            }

            protected override void ReleaseControl()
            {
                _m.enabled = true;
                _health.Respawn -= OnRespawn;
            }

            // On respawn the manager is disabled, so its own OnRespawn (→ max) would
            // be overwritten by our stale target. Snap the buffer to full instead.
            private void OnRespawn() => SnapTo(_m.MaxHunger, Time.time);
        }

        private sealed class ThirstBinder : StatBinder
        {
            private readonly ThirstManager _m;
            private readonly IHealthManager _health;

            public ThirstBinder(ThirstManager m, IHealthManager health)
            {
                _m = m;
                _health = health;
            }

            protected override void WriteDisplay(float value) => _m.Thirst = value;

            protected override void TakeControl()
            {
                _m.enabled = false;
                _health.Respawn += OnRespawn;
            }

            protected override void ReleaseControl()
            {
                _m.enabled = true;
                _health.Respawn -= OnRespawn;
            }

            private void OnRespawn() => SnapTo(_m.MaxThirst, Time.time);
        }

        private sealed class StaminaBinder : StatBinder
        {
            private readonly StaminaManager _m;

            public StaminaBinder(StaminaManager m) => _m = m;

            // Setter fires StaminaChanged + runs movement-blocking/audio.
            protected override void WriteDisplay(float value) => _m.Stamina = value;
            protected override void TakeControl() => _m.SetServerControlled(true);
            protected override void ReleaseControl() => _m.SetServerControlled(false);
        }

        private sealed class HealthBinder : StatBinder
        {
            private readonly HealthManager _m;

            public HealthBinder(HealthManager m) => _m = m;

            protected override void WriteDisplay(float value) => _m.SetHealthSilent(value);
            protected override void TakeControl() { }   // health has no local drain
            protected override void ReleaseControl() { }
        }
    }
}
