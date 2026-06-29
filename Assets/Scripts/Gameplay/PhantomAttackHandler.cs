using System.Collections.Generic;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.MovementSystem;
using UnityEngine;
using UnityEngine.UI;

namespace BackroomsSurvival.Gameplay
{
    /// <summary>
    /// ADR-016 slice 2 (client) — reacts to the host-authoritative phantom-attack events the
    /// backend emits (slice 1), all via the generic <see cref="IPCClient.AddEventListener"/> channel
    /// (no new IPC cases, no schema change):
    ///   • "phantom_hit"       → screen shake + a red flash (non-lethal frontal strike).
    ///   • "phantom_kill"      → fade-to-black + "YOU DIED" while the LOCAL player's movement is
    ///                            locked (the backend already respawned + teleported; the fade hides
    ///                            the jump and the lock stops you "walking dead" under it).
    ///   • "phantom_knockback" → a shove applied CLIENT-side via the motor's SetVelocity (mutating
    ///                            the pose server-side would be overwritten by the next
    ///                            client-authoritative input — ADR-009).
    ///
    /// DEUDA: host-only. A joiner's health/death lives in its own backend (P2P multi-backend), so
    /// these events only fire for the host until cross-backend damage authority (Fase 7).
    ///
    /// Self-bootstraps (no scene wiring, mirrors LocalPickupInputLock / PlayerPoseTransmitter) and
    /// is fully removable (delete the file). Event callbacks run on the MAIN thread (IPCClient.Update
    /// drains the queue), so touching Unity objects here is safe.
    /// </summary>
    public sealed class PhantomAttackHandler : MonoBehaviour
    {
        private const string HitEvent = "phantom_hit";
        private const string KillEvent = "phantom_kill";
        private const string KnockbackEvent = "phantom_knockback";

        // Death fade timing (presentation only — the backend respawn is instant).
        private const float FadeInTime = 0.5f;
        private const float HoldTime = 2.0f;
        private const float FadeOutTime = 0.5f;

        // Hit feedback.
        private const float HitShakeTime = 0.3f;
        private const float HitShakeMag = 0.6f;    // degrees of additive camera jitter per frame
        private const float HitFlashTime = 0.35f;
        private const float HitFlashAlpha = 0.35f; // red flash peak alpha

        // Locomotion states suspended during the death fade (Idle/Airborne left intact — see
        // LocalPickupInputLock). These always exist on a player controller.
        private static readonly MovementStateType[] BlockedStates =
        {
            MovementStateType.Walk, MovementStateType.Run, MovementStateType.Jump
        };

        private static PhantomAttackHandler _instance;

        private IPCClient _ipc;
        private Camera _cam;

        // Lazily-built overlay UI.
        private Canvas _canvas;
        private Image _flashImage; // red hit flash
        private Image _fadeImage;  // black death fade
        private Text _diedText;

        // Death sequence.
        private bool _dying;
        private float _deathElapsed;
        private IMovementControllerCC _deathBlock;

        // Transient hit feedback timers.
        private float _shakeTimer;
        private float _flashTimer;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[PhantomAttackHandler]");
            _instance = go.AddComponent<PhantomAttackHandler>();
            DontDestroyOnLoad(go);
        }

        // Hard singleton: a second instance (duplicate scene load, etc.) self-destructs so the
        // listener is never double-subscribed.
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
            // Subscribe once the IPC client exists (mirrors LocalPickupInputLock).
            if (_ipc == null && IPCClient.TryGetInstance(out var ipc))
            {
                _ipc = ipc;
                _ipc.AddEventListener(OnGameEvent);
            }

            if (_dying)
                TickDeath();
            if (_shakeTimer > 0f)
                TickShake();
            if (_flashTimer > 0f)
                TickFlash();
        }

        // Fired on the main thread (IPCClient.Update drains the event queue).
        private void OnGameEvent(GameEventMsg ev)
        {
            if (ev == null)
                return;

            switch (ev.eventType)
            {
                case HitEvent:
                    EnsureUi();
                    _shakeTimer = HitShakeTime;
                    _flashTimer = HitFlashTime;
                    break;

                case KillEvent:
                    StartDeath();
                    break;

                case KnockbackEvent:
                    var d = ev.data as Dictionary<string, object>;
                    ApplyKnockback(IPCParse.F(d, "dx"), IPCParse.F(d, "dz"));
                    break;
            }
        }

        // ── Death (fade + "YOU DIED" + movement lock) ──────────────────────────────────────────

        private void StartDeath()
        {
            EnsureUi();
            if (_dying)
            {
                _deathElapsed = 0f; // re-kill during the fade → restart the sequence
                return;
            }

            _dying = true;
            _deathElapsed = 0f;

            var movement = ResolveMovement();
            if (movement != null)
            {
                for (int i = 0; i < BlockedStates.Length; i++)
                    movement.AddStateBlocker(this, BlockedStates[i]);
                _deathBlock = movement;
            }
        }

        private void TickDeath()
        {
            _deathElapsed += Time.unscaledDeltaTime;
            float total = FadeInTime + HoldTime + FadeOutTime;

            float a;
            if (_deathElapsed < FadeInTime)
                a = _deathElapsed / FadeInTime;
            else if (_deathElapsed < FadeInTime + HoldTime)
                a = 1f;
            else if (_deathElapsed < total)
                a = 1f - (_deathElapsed - FadeInTime - HoldTime) / FadeOutTime;
            else
            {
                EndDeath();
                return;
            }

            if (_fadeImage != null)
                _fadeImage.color = new Color(0f, 0f, 0f, a);
            if (_diedText != null)
                _diedText.color = new Color(0.85f, 0.85f, 0.80f, a);
        }

        private void EndDeath()
        {
            _dying = false;
            if (_fadeImage != null)
                _fadeImage.color = new Color(0f, 0f, 0f, 0f);
            if (_diedText != null)
                _diedText.color = new Color(0.85f, 0.85f, 0.80f, 0f);

            if (_deathBlock != null)
            {
                for (int i = 0; i < BlockedStates.Length; i++)
                    _deathBlock.RemoveStateBlocker(this, BlockedStates[i]);
                _deathBlock = null;
            }
        }

        // ── Hit feedback ─────────────────────────────────────────────────────────────────────────

        private void TickShake()
        {
            _shakeTimer -= Time.unscaledDeltaTime;
            var cam = ResolveCam();
            if (cam != null)
            {
                float m = HitShakeMag * Mathf.Clamp01(_shakeTimer / HitShakeTime);
                cam.transform.localRotation *= Quaternion.Euler(
                    Random.Range(-m, m), Random.Range(-m, m), 0f);
            }
            if (_shakeTimer < 0f)
                _shakeTimer = 0f;
        }

        private void TickFlash()
        {
            _flashTimer -= Time.unscaledDeltaTime;
            float a = HitFlashAlpha * Mathf.Clamp01(_flashTimer / HitFlashTime);
            if (_flashImage != null)
                _flashImage.color = new Color(0.6f, 0f, 0f, a);
            if (_flashTimer < 0f)
                _flashTimer = 0f;
        }

        // ── Knockback ────────────────────────────────────────────────────────────────────────────

        private void ApplyKnockback(float dx, float dz)
        {
            var motor = ResolveMotor();
            if (motor == null)
                return;

            // One-shot horizontal shove; gravity reasserts the next frame. The backend already
            // pre-scaled (dx, dz) by the knockback force.
            motor.SetVelocity(new Vector3(dx, 0f, dz));
        }

        // ── Resolvers (local player only; remote avatars are excluded) ─────────────────────────────

        private Camera ResolveCam()
        {
            if (_cam == null)
                _cam = Camera.main;
            return _cam;
        }

        private IMovementControllerCC ResolveMovement()
        {
            var controllers = FindObjectsByType<PlayerMovementController>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            for (int i = 0; i < controllers.Length; i++)
            {
                if (controllers[i].GetComponentInParent<RemotePlayerManager>() != null)
                    continue; // remote avatar, not the local player
                return controllers[i];
            }

            return null;
        }

        private CharacterControllerMotor ResolveMotor()
        {
            var motors = FindObjectsByType<CharacterControllerMotor>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            for (int i = 0; i < motors.Length; i++)
            {
                if (motors[i].GetComponentInParent<RemotePlayerManager>() != null)
                    continue; // remote avatar, not the local player
                return motors[i];
            }

            return null;
        }

        // ── Lazily-built overlay UI (mirrors SanityEffects) ────────────────────────────────────────

        private void EnsureUi()
        {
            if (_canvas != null)
                return;

            _canvas = new GameObject("PhantomAttackCanvas").AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100; // above SanityCanvas (90)
            DontDestroyOnLoad(_canvas.gameObject);

            // Child order = render order: flash (bottom), fade, text (top).
            _flashImage = CreateOverlay("HitFlash", new Color(0.6f, 0f, 0f, 0f));
            _fadeImage = CreateOverlay("DeathFade", new Color(0f, 0f, 0f, 0f));
            _diedText = CreateText("YouDied", "YOU DIED");
        }

        private Image CreateOverlay(string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_canvas.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        private Text CreateText(string name, string content)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_canvas.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(800f, 200f);

            var txt = go.AddComponent<Text>();
            txt.text = content;
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            txt.fontSize = 72;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = new Color(0.85f, 0.85f, 0.80f, 0f);
            txt.raycastTarget = false;
            return txt;
        }

        private void OnDestroy()
        {
            if (_ipc != null)
                _ipc.RemoveEventListener(OnGameEvent);

            EndDeath(); // releases the movement block if mid-fade

            if (_canvas != null)
                Destroy(_canvas.gameObject);

            if (_instance == this)
                _instance = null;
        }
    }
}
