using System.Collections.Generic;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-029 Fase 2: cosmetic feedback for the three PvP outcome events. Self-bootstraps;
    /// fully removable. NEVER applies damage — the real health mutation (or lack of it)
    /// already happened on the backend before any of these events is emitted; this only logs
    /// today. Fase 5 (ADR-029) is where hitmarker/damage-indicator UI gets built.
    /// </summary>
    public sealed class PvpFeedbackController : MonoBehaviour
    {
        private static PvpFeedbackController _instance;
        private IPCClient _ipc;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[PvpFeedbackController]");
            _instance = go.AddComponent<PvpFeedbackController>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            if (_ipc == null && IPCClient.TryGetInstance(out var ipc))
            {
                _ipc = ipc;
                _ipc.AddEventListener(OnGameEvent);
            }
        }

        // Fired on the main thread (IPCClient.Update drains the event queue).
        private void OnGameEvent(GameEventMsg ev)
        {
            if (ev == null)
                return;

            switch (ev.eventType)
            {
                case "pvp_hit_confirmed":
                    OnHitConfirmed(ev.data as Dictionary<string, object>);
                    break;
                case "pvp_hit_rejected":
                    OnHitRejected(ev.data as Dictionary<string, object>);
                    break;
                case "pvp_damage_taken":
                    OnDamageTaken(ev.data as Dictionary<string, object>);
                    break;
            }
        }

        /// <summary>
        /// Only ever emitted by OUR OWN backend when it is ALSO the host that decided the
        /// grant — ADR-029's wire list defines no P2P "confirmed" packet for a remote shooter,
        /// so a joiner shooter never receives this in V0 (see backend
        /// process_pvp_hit_candidate_host's NOTE comment). Cosmetic hook for Fase 5
        /// (hitmarker); no gameplay effect today.
        /// </summary>
        private void OnHitConfirmed(Dictionary<string, object> d)
        {
            if (d == null)
                return;

            long requestId = IPCParse.L(d, "request_id");
            long victimId = IPCParse.L(d, "victim_id");
            float damage = IPCParse.F(d, "damage");
            Debug.Log(
                $"MPTRACE step=PVP event=pvp_hit_confirmed_received request_id={requestId} " +
                $"victim_id={victimId} damage={damage:F1}");
        }

        /// <summary>
        /// Always emitted to the shooter's own Unity, whether the shot was local or forwarded.
        /// `reason` is one of the stable strings from game_loop.rs's validate_pvp_hit
        /// (duplicate/self_hit/too_far/invalid_weapon/victim_dead/victim_invulnerable/etc.).
        /// No UI yet (ADR-029 Fase 2 gate: "no hace falta UI todavia, solo no fallar
        /// silenciosamente") — logged so a rejection is never silent.
        /// </summary>
        private void OnHitRejected(Dictionary<string, object> d)
        {
            if (d == null)
                return;

            long requestId = IPCParse.L(d, "request_id");
            string reason = IPCParse.S(d, "reason");
            Debug.Log($"MPTRACE step=PVP event=pvp_hit_rejected_received request_id={requestId} reason={reason}");
        }

        /// <summary>
        /// Emitted to the VICTIM's own Unity when their own backend applies a validated grant.
        /// VERIFIED (this session): this is currently the ONLY local signal of being hit by
        /// PvP. ADR-024's hit_seq/flinch is driven exclusively by the LOCAL HealthManager's
        /// DamageReceived event (PlayerPoseTransmitter.OnDamageReceived), which never fires
        /// here — the health change is server-authoritative and applied SILENTLY via
        /// StatInterpolator.HealthBinder.SetHealthSilent (ADR-025/027, by design, so
        /// reconciliation never produces a false flinch). So this event does NOT duplicate any
        /// existing feedback — it also doesn't produce any yet (log-only). Wiring a real
        /// flinch/blood-screen response is explicitly Fase 5 scope (ADR-029); left as a TODO.
        /// </summary>
        private void OnDamageTaken(Dictionary<string, object> d)
        {
            if (d == null)
                return;

            long attackerId = IPCParse.L(d, "attacker_id");
            long weaponId = IPCParse.L(d, "weapon_id");
            float damage = IPCParse.F(d, "damage");
            float health = IPCParse.F(d, "health");
            Debug.Log(
                $"MPTRACE step=PVP event=pvp_damage_taken_received attacker_id={attackerId} " +
                $"weapon_id={weaponId} damage={damage:F1} health={health:F1}");
            // TODO (Fase 5, ADR-029): trigger cosmetic feedback here (reuse the hit_seq flinch
            // pipeline or a dedicated damage indicator) — intentionally NOT wired in Fase 2.
        }

        private void OnDestroy()
        {
            if (_ipc != null)
                _ipc.RemoveEventListener(OnGameEvent);
            if (_instance == this)
                _instance = null;
        }
    }
}
