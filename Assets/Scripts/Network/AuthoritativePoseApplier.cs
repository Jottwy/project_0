using System.Collections.Generic;
using PolymindGames;
using PolymindGames.MovementSystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-025 Slice A (Fix A) — event-armed consumer of the backend's authoritative movement
    /// delta (delta_update). Movement in this game is client-authoritative (apply_client_authoritative_move
    /// trusts the client pose, clamping only against collision), so the applier must NOT correct the
    /// local player from the continuous delta stream: doing so snapped the motor onto its own
    /// lagged / collision-clamped self-echo and produced a backward-movement feedback loop.
    ///
    /// Instead it snaps ONLY inside a short window ARMED by an authoritative-reposition event:
    /// "player_died" (the backend froze the pose at the death position — ADR-025: no auto-respawn)
    /// and "player_respawned" (the backend honored the client's respawn_request and repositioned
    /// onto the resolved safe spawn). Outside that window every delta is ignored. When the arming
    /// event carries the reposition target, the snap happens IMMEDIATELY to that position and the
    /// window only admits near-target deltas (the queued pre-reposition stream must never win —
    /// observed live 2026-07-03); a positionless (state-derived) arm waits for the first delta
    /// that DIFFERS from the frozen arm pose. Short enough to stay hidden under the death fade.
    /// Future reposition events (chunk teleport, PvP knockback) arm the same window.
    /// Because those events are one-shot on a lossy channel (the IPC broadcast buffer drops its
    /// oldest messages under lag), the death/respawn edges are ALSO derived from the resilient
    /// per-tick WorldState stream (server health 0 ↔ alive) — see OnWorldState.
    ///
    /// Deliberately does NOT touch STP: STP's own SetPlayerPosition (SoloSurvivalGameMode) still runs
    /// its local scene-spawn teleport on respawn, unmodified — this applier's snap simply overrides it
    /// a moment later with the authoritative pose once "player_died" arrives. This is the external-hook
    /// pattern for STP integration: correct AFTER STP acts, never edit STP's classes (PolymindGames.asmdef
    /// cannot reference Assembly-CSharp/_Migration anyway, so no STP class can consume an external signal
    /// without a companion class living inside PolymindGames' own asmdef-covered folder — avoided
    /// entirely by never needing STP to consume anything at all). See STATE.md / memory for the full
    /// rationale (an earlier attempt added a PlayerRespawnGate gate class + a guard inside
    /// SoloSurvivalGameMode.cs; reverted in favor of this purely-external approach).
    ///
    /// Self-bootstraps (no scene wiring, mirrors PhantomAttackHandler / PlayerPoseTransmitter) and is
    /// fully removable (delete the file → the server pose is ignored again, P2 returns). Event + delta
    /// callbacks run on the MAIN thread (IPCClient.Update drains the queues), so touching the motor here
    /// is safe.
    /// </summary>
    public sealed class AuthoritativePoseApplier : MonoBehaviour
    {
        // Seconds an authoritative-reposition event keeps the snap armed. Long enough to catch the
        // post-respawn delta(s) across a frame hitch, short enough to stay under the death fade.
        private const float SnapWindow = 0.35f;
        // Backend events that reposition/freeze the local player server-side. player_died freezes
        // the authoritative pose at the death position (ADR-025: the server no longer auto-respawns);
        // player_respawned carries the safe-spawn reposition once the client's respawn_request is
        // honored — the reposition that the death block used to apply in the same tick.
        private const string DiedEvent = "player_died";
        private const string RespawnedEvent = "player_respawned";
        // ADR-032: emitted once after the backend hydrates a world save (deferred to the first
        // PlayerInput so the IPC subscription exists). Same wire shape as player_respawned
        // ("position" key) but a DISTINCT type: RespawnRequester must NOT force the native
        // respawn chain for a session load — only this applier consumes it (position snap only).
        private const string RestoredEvent = "session_restored";

        private static AuthoritativePoseApplier _instance;

        private IPCClient _ipc;
        private CharacterControllerMotor _motor;
        private float _snapWindowRemaining;
        // Releases the SnapPending transmitter gate once the reposition has been applied.
        private bool _snappedThisWindow;
        // Point-4 mitigation: cap snaps to one per frame (a post-stall drain delivers the whole
        // queued delta backlog in a single frame — hundreds of same-frame snaps otherwise).
        private int _lastSnapFrame = -1;

        /// <summary>
        /// True while an authoritative reposition is pending. PlayerPoseTransmitter gates its
        /// sends on this — the server trusts client-authoritative positions, so a single stale
        /// pose report (sent between the server-side reposition and the client-side snap)
        /// re-overwrites the honored respawn spawn. Two shapes, both bounded by the window
        /// (0.35 s max, no retries):
        ///  • event arm (reposition target KNOWN): suppressed for the WHOLE window — the forced
        ///    native respawn chain teleports the motor to the STP scene spawn in the same event
        ///    drain (listener order unknowable), and one resumed send from there would overwrite
        ///    the honored spawn server-side (observed live 2026-07-03: honored (22.50,…) but
        ///    every delta stayed at the death pos).
        ///  • state-derived arm (target unknown): suppressed until the first snap — that snap is
        ///    by construction the repositioned pose (see the arm-pose filter in OnMovementDelta).
        /// </summary>
        internal static bool SnapPending
        {
            get
            {
                var i = _instance;
                if (i == null || i._snapWindowRemaining <= 0f)
                    return false;
                return i._hasExpectedPos || !i._snappedThisWindow;
            }
        }

        // Reposition target parsed from the arming event (player_died → "death_pos",
        // player_respawned → "position"). While set, ONLY deltas near it may snap — everything
        // farther is the stale pre-reposition stream still draining from the queue (the exact
        // failure observed live: the window's first snap consumed a queued pre-respawn delta,
        // released the TX gate at the OLD pose, and the honored spawn never reached any delta).
        private Vector3 _expectedPos;
        private bool _hasExpectedPos;

        // Pose the motor held when a positionless (state-derived) window was armed. Deltas
        // still equal to it are the frozen/stale stream; with pose TX suppressed nothing else
        // can move the authoritative pose, so the first delta that DIFFERS is the reposition.
        private Vector3 _armPose;
        private bool _hasArmPose;

        // Whether the last WorldState reported the local player dead (server health 0).
        // Drives the state-derived arming fallback — see OnWorldState.
        private bool _serverSawDead;

        // Deterministically reset persisted statics on every Play entry — mirrors IPCClient.
        // With "Enter Play Mode Options" disabling Domain Reload (EditorSettings
        // m_EnterPlayModeOptions includes DisableDomainReload), static fields are NOT cleared
        // between Play sessions; relying on OnDestroy alone leaves a stale `_instance` ghost (which
        // would make Bootstrap early-return → no applier → no snap) and a stale gate. SubsystemRegistration
        // runs earliest (before AfterSceneLoad/Bootstrap), so the world starts each session clean.
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

            var go = new GameObject("[AuthoritativePoseApplier]");
            _instance = go.AddComponent<AuthoritativePoseApplier>();
            DontDestroyOnLoad(go);
        }

        // Hard singleton: a second instance (duplicate scene load, etc.) self-destructs so the
        // listeners are never double-subscribed and the gate is owned by one instance only.
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
            // Count down the armed snap window (main-thread; unscaled so a death-time-scale change
            // never freezes it).
            if (_snapWindowRemaining > 0f)
            {
                _snapWindowRemaining -= Time.unscaledDeltaTime;
                // A window that expires WITHOUT any snap means a reposition event arrived but no
                // usable delta followed (no motor / origin pos / none in the window) → the
                // reposition silently did nothing. Loud so it can never hide again.
                if (_snapWindowRemaining <= 0f && !_snappedThisWindow)
                    Debug.LogWarning("[AuthPoseApplier] snap window EXPIRED without snapping — a reposition event arrived but no reposition was applied");
            }

            // Subscribe once the IPC client exists (mirrors PhantomAttackHandler). Known limit of
            // this pattern (shared with PhantomAttackHandler): if the IPCClient is destroyed and
            // recreated on reconnect, we keep the stale `_ipc` and don't re-subscribe. The client
            // is a long-lived DontDestroyOnLoad singleton, so this doesn't occur in practice.
            if (_ipc == null && IPCClient.TryGetInstance(out var ipc))
            {
                _ipc = ipc;
                _ipc.AddMovementDeltaListener(OnMovementDelta);
                _ipc.AddEventListener(OnGameEvent);
                _ipc.AddStateListener(OnWorldState);
            }
        }

        // Resilient fallback for the one-shot reposition events. The IPC broadcast buffer drops
        // its OLDEST messages under lag ("IPC write loop lagged", ipc/server.rs) and a respawn is
        // precisely a lag spike (Unity's main thread stalls rebuilding the spawn-area chunks) →
        // player_died / player_respawned can be lost exactly when they matter. The same fact is
        // DERIVABLE from the WorldState stream, which is re-sent every send tick: server health
        // is 0 the whole time the player is dead-awaiting (ADR-025) and >0 the tick the
        // respawn_request is honored. Either edge means the authoritative pose was frozen /
        // repositioned → arm the same snap window the events arm (double-arming is harmless).
        private void OnWorldState(WorldStateMsg state)
        {
            if (state?.localPlayer?.stats == null)
                return;

            bool dead = state.localPlayer.stats.health <= HealthExtensions.Threshold;
            if (dead == _serverSawDead)
                return;
            _serverSawDead = dead;

            // The fallback exists ONLY for a LOST event: if a window is already armed (the event
            // fast-path arrived), re-arming here would CLOBBER its expected-position filter —
            // observed live 2026-07-03: the dead→alive edge landed ~100 ms after
            // player_respawned, downgraded the window to positionless, and the stale in-flight
            // deltas re-snapped the player back to the old pose.
            if (_snapWindowRemaining > 0f)
                return;

            // Positionless arm: remember the CURRENT motor pose so the delta filter can tell
            // the stale stream (== this pose) from the actual reposition (differs).
            ResolveMotor();
            _hasExpectedPos = false;
            _hasArmPose = _motor != null;
            _armPose = _hasArmPose ? _motor.transform.position : Vector3.zero;

            _snapWindowRemaining = SnapWindow;
            _snappedThisWindow = false; // arms the SnapPending transmitter gate
        }

        // Fired on the main thread (IPCClient.Update drains the event queue before the delta queue).
        private void OnGameEvent(GameEventMsg ev)
        {
            if (ev == null ||
                (ev.eventType != DiedEvent && ev.eventType != RespawnedEvent && ev.eventType != RestoredEvent))
                return;

            // The backend froze (death) or repositioned (respawn) the local player's authoritative
            // pose this tick — and the event CARRIES that position. Snap to it immediately instead
            // of racing the (possibly stale) queued delta stream; the armed window then lets
            // near-target deltas refine while the _expectedPos filter discards the stale ones.
            var d = ev.data as Dictionary<string, object>;
            string posKey = ev.eventType == DiedEvent ? "death_pos" : "position";
            Vector3 eventPos = d != null && d.TryGetValue(posKey, out var rawPos)
                ? IPCParse.Vec3(rawPos)
                : Vector3.zero;
            _hasExpectedPos = eventPos != Vector3.zero; // origin = missing/unparsable → fall back
            _expectedPos = eventPos;
            _hasArmPose = false;

            _snapWindowRemaining = SnapWindow;
            _snappedThisWindow = false;

            if (_hasExpectedPos)
                SnapTo(eventPos);
        }

        // Fired on the main thread (IPCClient.Update drains the delta queue).
        private void OnMovementDelta(MovementDeltaMsg delta)
        {
            if (delta == null)
                return;

            // Outside an armed reposition window, ignore the continuous stream entirely — snapping to
            // the lagged / collision-clamped self-echo is what caused the backward-movement feedback
            // loop (Fix A). Movement stays client-authoritative here.
            if (_snapWindowRemaining <= 0f)
                return;

            // One snap per frame: after a stall, IPCClient drains its whole delta backlog in a
            // single frame (observed: 12 s stall → 240+ same-frame snaps). The next frame's
            // delta is strictly newer, so skipping the rest of the batch loses nothing.
            if (_lastSnapFrame == Time.frameCount)
                return;

            Vector3 authPos = delta.position;
            if (authPos == Vector3.zero)
            {
                // Defensive: never snap to the literal origin (default pose pre-first-input).
                Debug.LogWarning("[AuthPoseApplier] window ACTIVE but delta.position is origin — snap skipped");
                return;
            }

            if (_hasExpectedPos)
            {
                // Only the delta reflecting the reposition target may snap — anything farther is
                // the stale pre-reposition stream still draining from the queue.
                if ((authPos - _expectedPos).sqrMagnitude > 1f)
                    return;
            }
            else if (_hasArmPose && (authPos - _armPose).sqrMagnitude < 0.25f)
            {
                // Positionless (state-derived) arm: this delta still equals the frozen arm pose →
                // stale stream. With pose TX suppressed nothing else can move the authoritative
                // pose, so the first delta that differs IS the reposition — wait for it.
                return;
            }

            SnapTo(authPos);
        }

        // Snap the motor onto the authoritative pose (preserve the client's yaw — look is input,
        // ADR-009 §8) and clear residual velocity.
        // ORDER IS MANDATORY: Teleport does `_lastPosition += position` (accumulates, not
        // assigns — CharacterControllerMotor.cs:145), which makes the next Update compute a huge
        // phantom velocity; ResetVelocity re-bases `_lastPosition` to the current position
        // (CharacterControllerMotor.cs:173). Inverting these re-exports a velocity spike with no
        // obvious failure. Keep Teleport first, ResetVelocity second.
        private void SnapTo(Vector3 authPos)
        {
            ResolveMotor();
            if (_motor == null)
            {
                Debug.LogWarning("[AuthPoseApplier] window ACTIVE but no local motor found — snap skipped");
                return;
            }

            _motor.Teleport(authPos, _motor.transform.rotation);
            _motor.ResetVelocity();
            _snappedThisWindow = true; // releases the SnapPending gate (state-derived arm shape)
            _lastSnapFrame = Time.frameCount;
        }

        /// <summary>
        /// Finds the LOCAL motor live (mirror of PlayerPoseTransmitter.ResolveMotor). Unity's
        /// overloaded == reports a destroyed motor as null, so a rig rebuild forces a re-find;
        /// remote avatars (under a RemotePlayerManager) are excluded — shared scan in
        /// <see cref="LocalPlayerLocator.Find{T}"/>.
        /// </summary>
        private void ResolveMotor()
        {
            if (_motor != null)
                return;

            _motor = LocalPlayerLocator.Find<CharacterControllerMotor>();
        }

        private void OnDestroy()
        {
            if (_instance != this)
                return; // a duplicate self-destructing in Awake — never touch the survivor's state

            if (_ipc != null)
            {
                _ipc.RemoveMovementDeltaListener(OnMovementDelta);
                _ipc.RemoveEventListener(OnGameEvent);
                _ipc.RemoveStateListener(OnWorldState);
            }

            _instance = null;
        }
    }
}
