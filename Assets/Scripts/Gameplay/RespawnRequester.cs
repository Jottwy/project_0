using System.Reflection;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.MovementSystem;
using PolymindGames.UserInterface;
using UnityEngine;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-025 respawn-on-demand — bridges STP's NATIVE Respawn button to the authoritative
    /// backend, without editing any STP class (STATE.md ▸ NO tocar).
    ///
    /// Chain: the DeathUI respawn button's PUBLIC SelectableButton.Clicked event (the button
    /// instance is read from the private DeathUI._respawnButton field — read-only reflection,
    /// same vendor-field idiom as StpBuildingReplicator/StpHarvestableSyncManager) → this
    /// component → IPCClient.SendRespawnRequest, UNCONDITIONALLY (no local-health gate: the
    /// server ignores the request unless its own authoritative state says dead —
    /// respawn_request_ignored reason=not_dead). On honor it resolves a safe spawn and emits
    /// "player_respawned", which arms the AuthoritativePoseApplier snap → the motor lands on
    /// the authoritative spawn.
    ///
    /// NOT driven by HealthManager.Respawn (the original design): that event is gated by
    /// !wasAlive &amp;&amp; IsAlive, and the StatInterpolator's SetHealthSilent reconciliation
    /// revives the local health value right after death (P1 "se cura al máximo", STATE.md) →
    /// the gate never passes while connected, so the event never fires (confirmed by diag log
    /// 2026-07-02).
    ///
    /// Same dead gate strands EVERY native Respawn consumer (PlayerDeathHandler never pops the
    /// death InputContext nor cancels the death post-processing, SoloSurvivalGameMode never
    /// teleports, stats/wieldables never reset) → without help the player stays input-locked
    /// on a faded screen forever. So on the server's "player_respawned" event this component
    /// FORCES the native 0→alive edge exactly once: SetHealthSilent(0) (the ADR-009 hook, no
    /// events) + RestoreHealth(max) → the full native respawn chain fires on the server's own
    /// confirmation. SoloSurvivalGameMode's scene-spawn teleport fires inside that call and is
    /// immediately corrected by the AuthoritativePoseApplier snap (armed by this same event —
    /// correct-after pattern). A flag skips the force when the native edge already fired at
    /// the click (health really 0, e.g. reconciliation off), avoiding a double InputContext pop.
    /// Because player_respawned is one-shot on a lossy channel, the same trigger is ALSO
    /// derived from the resilient per-tick WorldState stream (server health dead→alive edge,
    /// see OnWorldState) — either path fires the chain, the flag makes them idempotent.
    ///
    /// The DEATH side needs the mirror bridge (diagnosed 2026-07-03): server-side deaths
    /// (starvation, dehydration, entities) arrive through the same silent reconciliation write,
    /// so STP's native Death never fires → no DeathUI, no button, no way to request a respawn —
    /// the player was stranded dead-awaiting. On the server's dead edge (player_died event, or
    /// the derived health alive→dead edge as the loss fallback) this component forces the native
    /// alive→0 crossing exactly once — SetHealthSilent(1) + ReceiveDamage — so DamageReceived +
    /// Death fire through the REAL damage path and the whole native death chain (DeathUI,
    /// effects) engages the validated on-demand pipeline. Idempotent via the same
    /// _nativeRespawnFired flag: a death that already fired locally (fall) skips the force.
    ///
    /// DeathUI's native 15-s auto-respawn (RespawnRoutine → RespawnPlayer(null)) is NEUTRALIZED
    /// (on-demand decision, 2026-07-03): it reset the hunger/thirst displays, got its health
    /// restore instantly re-pinned by reconciliation and sent NO respawn_request — a ghost
    /// half-respawn. Once the countdown ends (button interactable) the only live leg inside
    /// RespawnRoutine is that auto-respawn, so StopAllCoroutines() on the DeathUI kills it while
    /// the button stays clickable (Clicked subscriptions are coroutine-independent). The player
    /// waits for the button indefinitely.
    ///
    /// Offline the StatInterpolator releases control, health stays 0, the native Respawn path
    /// works untouched, and this component sends/forces nothing (no IPC event source).
    ///
    /// Self-bootstraps, mirrors PhantomAttackHandler; removable.
    /// </summary>
    public sealed class RespawnRequester : MonoBehaviour
    {
        private static RespawnRequester _instance;

        // The exact button instance DeathUI itself listens to ([SerializeField, NotNull]).
        private static readonly FieldInfo _respawnButtonField =
            typeof(DeathUI).GetField("_respawnButton", BindingFlags.Instance | BindingFlags.NonPublic);

        private SelectableButton _respawnButton;
        private DeathUI _deathUi;

        // True once this death's auto-respawn leg has been killed (see class doc); re-armed on
        // each native Death.
        private bool _autoRespawnNeutralized;

        // Backend events driving the two forced edges (see class doc).
        private const string RespawnedEvent = "player_respawned";
        private const string DiedEvent = "player_died";

        private IPCClient _ipc;

        // True while no death is pending a native respawn. Set false on Death, true on Respawn →
        // the player_respawned handler only forces the edge when the native one never fired.
        private bool _nativeRespawnFired = true;

        // Whether the last WorldState reported the local player dead (server health 0).
        // Drives the state-derived fallback trigger — see OnWorldState.
        private bool _serverSawDead;

        // Local health resolution: the send trigger is the DeathUI button (above), but the
        // player_respawned handler needs the concrete HealthManager to force the respawn edge.
        private CharacterControllerMotor _motor;
        private IHealthManager _health;

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

            var go = new GameObject("[RespawnRequester]");
            _instance = go.AddComponent<RespawnRequester>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
        }

        private void Update()
        {
            ResolveButtonAndSubscribe();
            ResolveAndSubscribe();

            // Subscribe once the IPC client exists (mirrors AuthoritativePoseApplier; same known
            // limit: a destroyed+recreated IPCClient is not re-subscribed — doesn't occur, the
            // client is a long-lived DontDestroyOnLoad singleton).
            if (_ipc == null && IPCClient.TryGetInstance(out var ipc))
            {
                _ipc = ipc;
                _ipc.AddEventListener(OnGameEvent);
                _ipc.AddStateListener(OnWorldState);
            }

            // On-demand decision: kill DeathUI's native 15-s auto-respawn. Once the countdown
            // ends (button interactable) the only live leg inside RespawnRoutine is that
            // auto-respawn — stopping the coroutine removes it while the button stays clickable.
            if (!_autoRespawnNeutralized && _deathUi != null &&
                _respawnButton != null && _respawnButton.IsInteractable)
            {
                _deathUi.StopAllCoroutines();
                _autoRespawnNeutralized = true;
            }
        }

        // Fired on the main thread (IPCClient.Update drains the event queue). The server honored
        // our respawn_request → force STP's native respawn chain if its own edge never fired
        // (see class doc). Runs BEFORE the same frame's delta drain, so the scene-spawn teleport
        // triggered inside RestoreHealth is corrected by the applier's snap immediately after.
        private void OnGameEvent(GameEventMsg ev)
        {
            if (ev == null)
                return;

            if (ev.eventType == RespawnedEvent)
                ForceNativeRespawnChain("player_respawned event");
            else if (ev.eventType == DiedEvent)
                ForceNativeDeathEdge("player_died event");
        }

        // Resilient fallback (mirrors AuthoritativePoseApplier.OnWorldState): player_respawned is
        // one-shot on a lossy channel — the IPC broadcast buffer drops its oldest messages under
        // lag, and a respawn IS a lag spike (Unity's main thread stalls rebuilding the spawn-area
        // chunks). The same fact is derivable from the re-sent-every-tick WorldState: server
        // health is 0 the whole time the player is dead-awaiting (ADR-025) and >0 the tick the
        // respawn_request is honored → the dead→alive edge triggers the same forced chain
        // (idempotent via _nativeRespawnFired).
        private void OnWorldState(WorldStateMsg state)
        {
            if (state?.localPlayer?.stats == null)
                return;

            bool dead = state.localPlayer.stats.health <= HealthExtensions.Threshold;
            if (dead == _serverSawDead)
                return;
            _serverSawDead = dead;

            if (dead)
                ForceNativeDeathEdge("state-derived death edge (server health alive→0)");
            else
                ForceNativeRespawnChain("state-derived respawn edge (server health 0→alive)");
        }

        // Mirror of ForceNativeRespawnChain for the DEATH side: server-side deaths (starvation,
        // dehydration, entities) reach the client only through SetHealthSilent (no events), so
        // the native Death never fires and no DeathUI/button/respawn_request is possible. Force
        // the alive→0 crossing through the REAL damage path so DamageReceived + Death fire and
        // the validated on-demand pipeline engages. Runs at most once per death: a locally-fired
        // death (fall) sets _nativeRespawnFired=false first and skips the force.
        private void ForceNativeDeathEdge(string trigger)
        {
            if (!_nativeRespawnFired)
                return; // a native Death already fired for this death — nothing to bridge

            if (_health is not HealthManager concreteHealth)
            {
                Debug.LogWarning($"[RespawnRequester] {trigger} but no local HealthManager resolved — native Death NOT forced");
                return;
            }

            // Re-arm the crossing even if reconciliation already wrote 0 silently, then cross it
            // via ReceiveDamage (fires DamageReceived + Death natively; the ~1 hp delta gets
            // reported back to the already-dead server, a no-op).
            concreteHealth.SetHealthSilent(1f);
            _health.ReceiveDamage(_health.MaxHealth + 1f);
        }

        private void ForceNativeRespawnChain(string trigger)
        {
            if (_nativeRespawnFired)
                return; // the native chain already ran (health really 0 at the click, or the
                        // event fast-path beat this fallback) — never double-fire Respawn

            if (_health is not HealthManager concreteHealth)
            {
                Debug.LogWarning($"[RespawnRequester] {trigger} but no local HealthManager resolved — native respawn chain NOT forced");
                return;
            }

            concreteHealth.SetHealthSilent(0f);
            _health.RestoreHealth(_health.MaxHealth);
        }

        // Fired by the native DeathUI respawn button click. Unconditional: no local-health
        // gate (the reconciled local value is untrustworthy near death — that is the very bug
        // that broke the Respawn-event design); the server enforces its own not_dead gate.
        private void OnRespawnButtonClicked(SelectableButton button)
        {
            if (IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
                ipc.SendRespawnRequest();
            else
                Debug.LogWarning("[RespawnRequester] respawn button clicked but IPC missing/disconnected → respawn_request NOT sent");
        }

        /// <summary>
        /// Resolve the DeathUI respawn button and subscribe to its public Clicked event.
        /// Unity's overloaded == makes a destroyed button read as null → UI rebuild forces a
        /// clean re-find + re-subscribe (same retry pattern as ResolveAndSubscribe below).
        /// </summary>
        private void ResolveButtonAndSubscribe()
        {
            if (_respawnButton != null)
                return;

            UnsubscribeButton();

            // Include inactive: the button GameObject stays SetActive(false) until death.
            var deathUis = FindObjectsByType<DeathUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < deathUis.Length; i++)
            {
                var button = _respawnButtonField?.GetValue(deathUis[i]) as SelectableButton;
                if (button == null)
                    continue;

                _respawnButton = button;
                _deathUi = deathUis[i]; // needed to neutralize its auto-respawn coroutine
                button.Clicked += OnRespawnButtonClicked;
                return;
            }
        }

        private void UnsubscribeButton()
        {
            // C#-level null check on purpose: unsubscribing from a destroyed-but-referenced
            // button is safe and avoids leaking the delegate.
            if (!ReferenceEquals(_respawnButton, null))
                _respawnButton.Clicked -= OnRespawnButtonClicked;
            _respawnButton = null;
            _deathUi = null;
        }

        // Not the send trigger (the button hook above is): tracks whether the native respawn
        // edge fired on its own, so the player_respawned handler skips the forced edge.
        // While connected this is normally reached only BY that forced edge.
        private void OnRespawn() => _nativeRespawnFired = true;

        // A native respawn (or our forced one) is now pending; the fresh DeathUI cycle re-arms
        // its auto-respawn leg, so re-arm the neutralizer too.
        private void OnDeath(in DamageArgs args)
        {
            _nativeRespawnFired = false;
            _autoRespawnNeutralized = false;
        }

        /// <summary>
        /// Resolve the LOCAL health manager and (re)subscribe. Unity's overloaded == makes a
        /// destroyed motor read as null → rig rebuild forces a clean re-find + re-subscribe
        /// (same pattern as PlayerPoseTransmitter).
        /// </summary>
        private void ResolveAndSubscribe()
        {
            if (_motor != null)
                return;

            Unsubscribe();

            var m = LocalPlayerLocator.Find<CharacterControllerMotor>();
            if (m == null)
            {
                _motor = null;
                return;
            }

            _motor = m;
            var character = m.GetComponentInParent<ICharacter>();
            _health = character?.HealthManager;
            if (_health != null)
            {
                _health.Respawn += OnRespawn;
                _health.Death += OnDeath;
            }
            else
            {
                // With _motor set and _health null this component never retries (Update
                // early-outs) → a silent no-subscribe. Loud so it can never hide again.
                Debug.LogWarning($"[RespawnRequester] motor {m.GetInstanceID()} found but character/HealthManager is NULL — respawn bridge inactive until rig rebuild");
            }
        }

        private void Unsubscribe()
        {
            if (_health != null)
            {
                _health.Respawn -= OnRespawn;
                _health.Death -= OnDeath;
            }
            _health = null;
        }

        private void OnDestroy()
        {
            if (_instance != this)
                return;

            if (_ipc != null)
            {
                _ipc.RemoveEventListener(OnGameEvent);
                _ipc.RemoveStateListener(OnWorldState);
            }

            UnsubscribeButton();
            Unsubscribe();
            _instance = null;
        }
    }
}
