using System.Reflection;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.MovementSystem;
using PolymindGames.UserInterface;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-009 design-gap bridge (mitad B, authorized 2026-07-03): the server-authoritative
    /// health reaches the local HealthManager via SetHealthSilent (StatInterpolator), which by
    /// design fires NO events — and STP's PlayerHealthUI only repaints on DamageReceived /
    /// HealthRestored. Server-side damage (starvation, dehydration, entities) therefore updated
    /// the value but never the bar: "la vida no baja nunca" while the backend logged deaths.
    ///
    /// This bridge keeps the bar's fill synced to Health/MaxHealth every frame (change-detected;
    /// idempotent with the native event-driven updates, which write the same value). Scope is
    /// deliberately ONLY the health bar: BloodScreenUI / DamageIndicatorUI stay event-driven so
    /// remote reconciliation never fakes hit feedback.
    ///
    /// The bar Image is read from PlayerHealthUI's private serialized `_healthBar` field —
    /// read-only reflection, same vendor-field idiom as StpBuildingReplicator / RespawnRequester
    /// (STATE.md ▸ never edit STP). Self-bootstraps, mirrors RespawnRequester; removable.
    /// </summary>
    public sealed class SilentHealthUIBridge : MonoBehaviour
    {
        private static SilentHealthUIBridge _instance;

        private static readonly FieldInfo _healthBarField =
            typeof(PlayerHealthUI).GetField("_healthBar", BindingFlags.Instance | BindingFlags.NonPublic);

        private CharacterControllerMotor _motor;
        private IHealthManager _health;
        private PlayerHealthUI _ui;
        private Image _bar;
        private float _lastFill = -1f;

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

            var go = new GameObject("[SilentHealthUIBridge]");
            _instance = go.AddComponent<SilentHealthUIBridge>();
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
            Resolve();
            if (_health == null || _bar == null)
                return;

            float max = _health.MaxHealth;
            if (max <= 0f)
                return;

            float fill = Mathf.Clamp01(_health.Health / max);
            if (Mathf.Abs(fill - _lastFill) < 0.0005f)
                return;

            _lastFill = fill;
            _bar.fillAmount = fill;
        }

        /// <summary>
        /// Resolve the LOCAL health manager (motor pattern shared with RespawnRequester — a
        /// destroyed motor reads as null and forces a clean re-find) and the health bar Image
        /// behind PlayerHealthUI (re-resolved if the UI is rebuilt).
        /// </summary>
        private void Resolve()
        {
            if (_motor == null)
            {
                _health = null;
                var motors = FindObjectsByType<CharacterControllerMotor>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                for (int i = 0; i < motors.Length; i++)
                {
                    var m = motors[i];
                    if (m.GetComponentInParent<RemotePlayerManager>() != null)
                        continue; // remote avatar, not the local player

                    _motor = m;
                    _health = m.GetComponentInParent<ICharacter>()?.HealthManager;
                    break;
                }
            }

            if (_ui == null)
            {
                _bar = null;
                _lastFill = -1f;
                var uis = FindObjectsByType<PlayerHealthUI>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (uis.Length > 0)
                {
                    _ui = uis[0];
                    _bar = _healthBarField?.GetValue(_ui) as Image;
                }
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
