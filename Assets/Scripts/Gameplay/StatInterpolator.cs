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
    /// elsewhere (SanityEffects, ADR-009 §9), not here.
    ///
    /// Manager references SELF-HEAL (2026-08-16): the rig rebuild destroys and recreates the
    /// STP managers, and every write this component does to a destroyed one is plain C# — no
    /// exception, no log, just a HUD frozen against a server that keeps draining. Update
    /// re-resolves whenever a bound manager reads as destroyed and re-asserts control over
    /// the replacements. See ResolveBinders.
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
        private float _nextResolveRetry;

        private void Awake()
        {
            ResolveBinders();
        }

        /// <summary>
        /// Finds the STP managers and builds one binder per stat found. Called from Awake AND
        /// from the self-heal in Update: the rig rebuild (ADR-024) destroys and recreates the
        /// managers, so an Awake-only cache goes stale and this component ends up writing into
        /// destroyed components — with no exception, because every write it does is plain C#
        /// (field setters, SetHealthSilent). The visible failure is a HUD that silently stops
        /// following the server while the NEW managers resume their local drain.
        /// </summary>
        private void ResolveBinders()
        {
            var hunger = GetComponentInChildren<HungerManager>(true);
            var thirst = GetComponentInChildren<ThirstManager>(true);
            var stamina = GetComponentInChildren<StaminaManager>(true);
            var health = GetComponentInChildren<HealthManager>(true);

            _hunger = null;
            _thirst = null;
            _stamina = null;
            _health = null;

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

            // Self-heal after a rig rebuild (ADR-024): detection is by STATE (a bound manager
            // reads as destroyed), not by event, so any current or future rebuild path is
            // covered without subscribing to it — the same re-resolve contract every networked
            // consumer of the rig already follows (LocalPlayerLocator's doc comment). Mid-
            // rebuild the new rig may not exist yet, hence the 0.5 s retry clock instead of a
            // per-frame GetComponentInChildren sweep while headless.
            bool lost = AnyBinderLost();
            if (lost || (_binders.Length == 0 && Time.unscaledTime >= _nextResolveRetry))
            {
                _nextResolveRetry = Time.unscaledTime + 0.5f;
                if (lost)
                    SetControlled(false); // guarded per-binder: skips destroyed managers
                ResolveBinders();
                // Re-assert control over the NEW managers right away: they are born with
                // enabled=true / _serverControlled=false and would drain locally this frame.
                if (connected && _binders.Length > 0)
                    SetControlled(true);
            }

            float now = Time.time;
            for (int i = 0; i < _binders.Length; i++)
                _binders[i].Tick(now, latencyMarginSec);
        }

        private bool AnyBinderLost()
        {
            for (int i = 0; i < _binders.Length; i++)
            {
                if (_binders[i].Lost)
                    return true;
            }
            return false;
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

            /// <summary>
            /// Reset duro que NO inventa un valor: colapsa la interpolación sobre la muestra más
            /// reciente del SERVIDOR. Es lo que necesita el respawn desde 2026-08-17, cuando
            /// hambre y sed dejaron de rellenarse a 100 al reaparecer: saltar al máximo pintaría
            /// una barra llena que acto seguido se desliza hasta el valor real, o sea una mentira
            /// visible. No-op si aún no ha llegado ninguna muestra.
            /// </summary>
            protected void SnapToLatestServerSample(float now)
            {
                if (!_hasSample)
                    return;
                SnapTo(_v1, now);
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

            /// <summary>True when the bound manager was destroyed (rig rebuild) — Unity's
            /// overloaded == on the CONCRETE component type is what detects the fake null.</summary>
            public abstract bool Lost { get; }

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

            public override bool Lost => _m == null;

            protected override void WriteDisplay(float value) => _m.Hunger = value;

            protected override void TakeControl()
            {
                _m.enabled = false; // stop the local deltaTime drain
                _health.Respawn += OnRespawn;
            }

            protected override void ReleaseControl()
            {
                // `.enabled` on a destroyed component throws (it IS a Unity API call, unlike
                // the plain-C# stat setters); the event unsubscribe is managed-only and safe.
                if (_m != null)
                    _m.enabled = true;
                _health.Respawn -= OnRespawn;
            }

            // On respawn the manager is disabled, so its own OnRespawn (→ max) would be
            // overwritten by our stale target. Antes se saltaba a MaxHunger para coincidir con el
            // vendor; desde 2026-08-17 el backend CONSERVA el hambre a través de la muerte
            // (`PlayerStats::on_respawn`), así que saltar al máximo pintaría una barra llena que
            // luego se desliza hasta el valor real. Se colapsa sobre la última muestra del
            // servidor en vez de inventar.
            private void OnRespawn() => SnapToLatestServerSample(Time.time);
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

            public override bool Lost => _m == null;

            protected override void WriteDisplay(float value) => _m.Thirst = value;

            protected override void TakeControl()
            {
                _m.enabled = false;
                _health.Respawn += OnRespawn;
            }

            protected override void ReleaseControl()
            {
                if (_m != null)
                    _m.enabled = true;
                _health.Respawn -= OnRespawn;
            }

            // Misma razón que en HungerBinder: la sed se conserva a través de la muerte.
            private void OnRespawn() => SnapToLatestServerSample(Time.time);
        }

        private sealed class StaminaBinder : StatBinder
        {
            private readonly StaminaManager _m;

            public StaminaBinder(StaminaManager m) => _m = m;

            public override bool Lost => _m == null;

            // Setter fires StaminaChanged + runs movement-blocking/audio.
            protected override void WriteDisplay(float value) => _m.Stamina = value;
            protected override void TakeControl() => _m.SetServerControlled(true);

            protected override void ReleaseControl()
            {
                if (_m != null)
                    _m.SetServerControlled(false);
            }
        }

        private sealed class HealthBinder : StatBinder
        {
            private readonly HealthManager _m;

            public HealthBinder(HealthManager m) => _m = m;

            public override bool Lost => _m == null;

            protected override void WriteDisplay(float value) => _m.SetHealthSilent(value);
            protected override void TakeControl() { }   // health has no local drain
            protected override void ReleaseControl() { }
        }
    }
}
