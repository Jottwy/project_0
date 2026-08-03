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

        // ── The grab (kill only) ──────────────────────────────────────────────────────────────
        // How long the camera is held on the thing that killed you before the fade starts. Short:
        // this is a beat of recognition, not a cutscene, and the player has already lost control.
        private const float GrabTime = 0.9f;
        // How far the killer may be and still be credited with the grab (m). The backend strikes
        // inside PHANTOM_ATTACK_REACH (2.4 m); the slack absorbs the 10 Hz pose relay and the fact
        // that the local player is client-authoritative and has kept moving since.
        private const float GrabSearchRadius = 4.5f;
        // Distance the player is dragged to, so the creature is not visibly hugging thin air.
        private const float GrabHoldDistance = 1.3f;
        // Drag speed toward the killer (m/s), applied through the motor rather than by writing the
        // transform: position is client-authoritative (ADR-009) and a direct write would be fought
        // by the next motor step.
        private const float GrabPullSpeed = 4.0f;
        // How fast the view swings onto the killer. NOT a snap — an instant camera cut on death is
        // nauseating, and it also hides the one thing this sequence exists to show.
        private const float GrabLookLerp = 9.0f;
        // Struggle tremor at the END of the hold (degrees). Grows with t², because a CONSTANT shake
        // reads as a broken camera and an accelerating one reads as something winding up.
        private const float GrabShakeMax = 2.2f;
        // Slow camera roll while held (degrees) — the horizon tipping is what sells losing your feet.
        private const float GrabRollMax = 9f;
        // Fraction of the hold after which you are lifted off the floor.
        private const float GrabLiftStart = 0.55f;
        private const float GrabLiftSpeed = 3.2f;
        // Impulse handed to the corpse ragdoll, away from the killer.
        private const float CorpseThrowSpeed = 6.5f;
        // A corpse spawning further than this from the recorded death spot is somebody else's.
        private const float CorpseMatchRadius = 4.0f;
        // …and one arriving later than this is a different death entirely.
        private const float CorpseMatchWindow = 6.0f;

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

        // The grab: who took you, and how long is left of being held by it.
        private Transform _grabber;
        private float _grabTimer;

        /// <summary>
        /// The shove the corpse should be thrown with, handed to <c>CorpseSpawner</c> across the
        /// assembly boundary (it lives in Assembly-CSharp, which auto-references this one; the
        /// reverse is impossible, which is why the handoff is a static read and not a call).
        ///
        /// A STATIC and not an event because the two are not alive at the same time: the corpse is
        /// spawned by the backend and reconciled into the world some frames later, by which point
        /// this component is mid-fade. The consumer matches on position AND time so it can never
        /// apply your death's impulse to somebody else's body.
        /// </summary>
        /// <summary>
        /// The proxy currently holding the local player, or null. Read by <c>ProxyGrabHook</c>,
        /// which lives in Assembly-CSharp and therefore cannot be called from here (the reference
        /// only goes the other way) — so the creature-side animation PULLS this state rather than
        /// being pushed it. Same handoff shape as the corpse throw below, and for the same reason.
        /// </summary>
        public static Transform ActiveGrabber { get; private set; }

        /// <summary>0 → 1 across the grab, so the creature's reach can ramp with it.</summary>
        public static float GrabProgress01 { get; private set; }

        /// <summary>Where the creature's hands should converge: the victim's head/chest, in world
        /// space. Fed from the camera, which IS the local player's head.</summary>
        public static Vector3 GrabVictimPoint { get; private set; }

        public static Vector3 PendingCorpseThrow { get; private set; }
        public static Vector3 PendingCorpseThrowAt { get; private set; }
        public static float PendingCorpseThrowTime { get; private set; } = float.NegativeInfinity;

        /// <summary>
        /// Consume the pending throw if `where` is the body from that death. Returns false — and
        /// leaves the ragdoll to settle on its own — for any corpse that does not match, which is
        /// the correct behaviour for every corpse this client did not just produce.
        /// </summary>
        public static bool TryTakeCorpseThrow(Vector3 where, out Vector3 impulse)
        {
            impulse = Vector3.zero;
            if (Time.time - PendingCorpseThrowTime > CorpseMatchWindow)
                return false;
            if ((where - PendingCorpseThrowAt).sqrMagnitude > CorpseMatchRadius * CorpseMatchRadius)
                return false;

            impulse = PendingCorpseThrow;
            PendingCorpseThrowTime = float.NegativeInfinity; // one body per death
            return true;
        }

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

            if (_grabTimer > 0f)
                TickGrab();
            else if (_dying)
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

            // Who took you. Resolved CLIENT-SIDE from what is already on screen — the killer is a
            // rendered remote avatar whose `revealed` flag is up — so the grab costs NOTHING on the
            // wire. The backend would have to carry the phantom's id and pose to tell us the same
            // thing, and that is a protocol change (and an ADR) for information the client can see.
            _grabber = ResolveGrabber();
            if (_grabber != null)
            {
                _grabTimer = GrabTime;
                ActiveGrabber = _grabber;
                GrabProgress01 = 0f;
                RecordCorpseThrow(_grabber);
            }

            var movement = ResolveMovement();
            if (movement != null)
            {
                for (int i = 0; i < BlockedStates.Length; i++)
                    movement.AddStateBlocker(this, BlockedStates[i]);
                _deathBlock = movement;
            }
        }

        /// <summary>
        /// Held by the thing that killed you: the view swings onto it and you are dragged to arm's
        /// length, so the last second of the round is spent looking at what took you instead of at
        /// whatever direction you happened to be running.
        ///
        /// DECLARED LIMIT: there is no authored grab animation. The creature holds the strike pose
        /// the backend already puts it in and stays revealed through it (`PHANTOM_STRIKE_RECOVERY`),
        /// so what reads is "it has you", not "it is performing a grab". A real animation needs the
        /// creature and the player aligned by a shared clip, which is authoring work, not code.
        /// </summary>
        private void TickGrab()
        {
            _grabTimer -= Time.unscaledDeltaTime;

            // The grabber can vanish mid-hold (it despawns, the pool recycles it). Fall straight
            // through to the ordinary fade rather than freezing on nothing.
            if (_grabber == null)
            {
                _grabTimer = 0f;
                ActiveGrabber = null;
                return;
            }

            float t = 1f - Mathf.Clamp01(_grabTimer / GrabTime); // 0 → 1 across the hold
            GrabProgress01 = t;
            ActiveGrabber = _grabber;

            var cam = ResolveCam();
            if (cam != null)
            {
                // Aim at the upper chest rather than the pivot: the pivot is at the feet, and a
                // camera pointed at the floor is the opposite of the shot this exists to get.
                var focus = _grabber.position + Vector3.up * 1.6f;
                var to = focus - cam.transform.position;
                if (to.sqrMagnitude > 1e-4f)
                {
                    cam.transform.rotation = Quaternion.Slerp(
                        cam.transform.rotation,
                        Quaternion.LookRotation(to.normalized, Vector3.up),
                        1f - Mathf.Exp(-GrabLookLerp * Time.unscaledDeltaTime));
                }

                GrabVictimPoint = cam.transform.position;

                // Struggle: a tremor that GROWS as it holds you, plus a slow roll. Growing is the
                // whole trick — a constant shake reads as a broken camera, an accelerating one
                // reads as something winding up. It rides on top of the look-at above, so the
                // creature stays framed while the frame itself comes apart.
                float shake = GrabShakeMax * t * t;
                cam.transform.rotation *= Quaternion.Euler(
                    Random.Range(-shake, shake),
                    Random.Range(-shake, shake),
                    Mathf.Sin(t * 9f) * GrabRollMax * t);
            }

            var motor = ResolveMotor();
            if (motor != null)
            {
                // Dragged in to arm's length, through the motor (ADR-009: a transform write would
                // be overwritten by the next motor step).
                var flat = _grabber.position - motor.transform.position;
                flat.y = 0f;
                float d = flat.magnitude;
                var pull = d > GrabHoldDistance && d > 1e-3f
                    ? flat / d * GrabPullSpeed
                    : Vector3.zero;

                // …and LIFTED off the floor for the last stretch, so the throw that follows starts
                // from a body already off balance instead of from someone standing calmly.
                if (t > GrabLiftStart)
                {
                    float lift = (t - GrabLiftStart) / Mathf.Max(1e-3f, 1f - GrabLiftStart);
                    pull.y = GrabLiftSpeed * lift;
                }
                motor.SetVelocity(pull);
            }

            if (_grabTimer <= 0f)
            {
                _grabTimer = 0f;   // the fade takes over next frame
                ActiveGrabber = null;
                GrabProgress01 = 0f;
            }
        }

        /// <summary>
        /// The nearest REVEALED remote avatar within reach — i.e. the only thing on screen that can
        /// have killed you. A revealed proxy is a robapieles mid-lunge by construction (ADR-038),
        /// and a real player is never revealed, so this can never mistake a teammate for the killer.
        /// </summary>
        private Transform ResolveGrabber()
        {
            var managers = FindObjectsByType<RemotePlayerManager>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var cam = ResolveCam();
            var origin = cam != null ? cam.transform.position : transform.position;

            Transform best = null;
            float bestSq = GrabSearchRadius * GrabSearchRadius;
            for (int m = 0; m < managers.Length; m++)
            {
                foreach (var kvp in managers[m].ActivePlayers)
                {
                    var view = kvp.Value;
                    if (view == null || !view.revealed || view.root == null)
                        continue;
                    float sq = (view.root.position - origin).sqrMagnitude;
                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        best = view.root;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Stage the impulse the corpse will be thrown with: away from the killer, and upward, so
        /// the body leaves rather than crumpling on the spot.
        ///
        /// Consistent with ADR-028's existing design, NOT a new divergence: corpse ragdolls already
        /// settle independently on every client (each one runs its own physics against its own
        /// rendered geometry), so a locally-applied impulse adds no synchrony that was ever there.
        /// </summary>
        private void RecordCorpseThrow(Transform grabber)
        {
            var motor = ResolveMotor();
            var at = motor != null ? motor.transform.position : transform.position;
            var away = at - grabber.position;
            away.y = 0f;
            if (away.sqrMagnitude < 1e-4f)
                away = -grabber.forward; // on top of each other: thrown off its front

            PendingCorpseThrow = (away.normalized + Vector3.up * 0.55f).normalized * CorpseThrowSpeed;
            PendingCorpseThrowAt = at;
            PendingCorpseThrowTime = Time.time;
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
            _grabber = null;
            _grabTimer = 0f;
            ActiveGrabber = null;
            GrabProgress01 = 0f;
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
