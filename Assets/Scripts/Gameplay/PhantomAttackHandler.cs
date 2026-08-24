using System.Collections.Generic;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.MovementSystem;
using UnityEngine;
using UnityEngine.InputSystem;
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
        // ADR-050 point 9: the grab is now a LIVE window rather than a death epilogue.
        private const string GrabStartEvent = "phantom_grab_start";
        private const string GrabReleaseEvent = "phantom_grab_release";
        // ADR-076: the disguised ambush connecting.
        private const string KnockdownEvent = "phantom_knockdown";
        // ADR-094 Enmienda 7: a faceling child has you from behind.
        private const string SeizeEvent = "faceling_seize";

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
        // ── The LIVE grab (ADR-050 point 9) ───────────────────────────────────────────────────
        // How many presses break you out. Enough that it is an effort and few enough that it can be
        // done inside the window with room to spare — the tension is meant to come from the seconds
        // draining, not from a wrist test.
        private const int StruggleTarget = 8;
        // Struggle decays while you are not pressing, so pacing yourself does not work.
        private const float StruggleDecayPerSecond = 2.2f;
        // What counts as struggling: space or the left mouse button, both, because in the two
        // seconds after something grabs you nobody reads a prompt.
        //
        // READ THROUGH THE INPUT SYSTEM PACKAGE, never `UnityEngine.Input`. This project has active
        // input handling set to the package, so the legacy class THROWS on every read
        // (`InvalidOperationException: You are trying to read Input using the UnityEngine.Input
        // class…`). The first version of this shipped with `Input.GetKeyDown` and the play-test log
        // holds 1427 of those: the exception aborted the struggle tick before it could count a
        // press, so all eight grabs in that session ended in `grab_expired` and the escape hatch
        // silently did not exist. `BackroomsGraphicsSettings` two files over already did it right.
        private static bool StrugglePressedThisFrame()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.spaceKey.wasPressedThisFrame)
                return true;
            var mouse = Mouse.current;
            return mouse != null && mouse.leftButton.wasPressedThisFrame;
        }

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

        // ── Knockdown (ADR-076) ─────────────────────────────────────────────────────────────────
        // A SEPARATE array from `BlockedStates` on purpose: that one is shared by grab and death,
        // and a knockdown adds `Crouch` to the set (a derailed player should not be able to duck
        // out of the fall) without changing what those two other sequences block.
        private static readonly MovementStateType[] KnockdownBlockedStates =
        {
            MovementStateType.Walk, MovementStateType.Run, MovementStateType.Jump,
            MovementStateType.Crouch
        };
        // How low the collider drops (m) — the same lever CharacterCrouchState uses, borrowed
        // procedurally: there is no authored knockdown/rise animation (same declared limit as the
        // grab above).
        private const float KnockdownHeight = 0.6f;
        private const float KnockdownFallTime = 0.25f;

        // ── The seizure (ADR-094 Enmienda 7) ────────────────────────────────────────────────────
        // A child caught you from behind: it spins you round, holds your face against its own and
        // screams, then throws you off.
        //
        // SHORT — under a second and a half all in. The grab above is a death and can afford 0.9 s
        // of recognition; this one you SURVIVE, and a scare you survive has to give control back
        // before it stops being a scare and starts being a cutscene you are waiting out. What is
        // meant to linger is the daze afterwards, not the hold itself.
        private const float SeizeTurnTime = 0.18f;  // the wrench round — near-instant, deliberately
        private const float SeizeHoldTime = 0.85f;  // face to face
        private const float SeizeTotalTime = SeizeTurnTime + SeizeHoldTime;
        // How fast the view is wrenched onto it. MUCH harder than `GrabLookLerp` (9): that one is a
        // dying man's head lolling round, this is being physically turned by something behind you.
        private const float SeizeTurnLerp = 26f;
        // How close it gets pulled to your face. Tighter than the grab's 1.3 — the whole point Joel
        // asked for is "muy cerca de su cara".
        private const float SeizeFaceDistance = 0.85f;
        private const float SeizePullSpeed = 6.0f;
        // Aim higher up the body than the grab's 1.6: a child is short, and a camera pointed at
        // chest height on a 1.95 m model is pointed at its chest — on this one it is the face.
        private const float SeizeFaceHeight = 1.5f;
        // The tremor while it screams at you. Bigger than the grab's, and it does NOT grow: this
        // is a shorter, louder moment, so it opens at full intensity instead of winding up.
        private const float SeizeShake = 3.1f;
        // The throw-off at the end.
        private const float SeizeShoveSpeed = 7.5f;

        // Locomotion suspended while it has you. Crouch included, same reasoning as the knockdown:
        // a player being held by the shoulders is not ducking out of it.
        private static readonly MovementStateType[] SeizeBlockedStates =
        {
            MovementStateType.Walk, MovementStateType.Run, MovementStateType.Jump,
            MovementStateType.Crouch
        };
        private const float KnockdownRiseTime = 0.35f;
        // Camera tilt while down (degrees). Applied relative to the rotation cached at the start
        // of the knockdown, never accumulated — see EndKnockdown for why an un-restored roll is a
        // standing bug in this file already (the grab's own camera-roll fix, above).
        private const float KnockdownRollMax = 40f;

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
        // ADR-050 point 9 — the LIVE grab: held and still alive, with a way out. Distinct from
        // `_dying` on purpose; the death grab keeps working exactly as it did for anything that
        // still kills outright.
        private bool _heldAlive;
        private float _heldWindow;
        private float _struggle;
        private bool _struggleSent;
        private IMovementControllerCC _heldBlock;
        private Text _struggleText;
        // The camera transform the grab borrowed, and the rotation to hand back. Cached at the
        // start rather than reconstructed at the end: by then the rig may have moved and there
        // would be nothing correct left to restore to.
        private Transform _camRestore;
        private Quaternion _camRotationAtGrab;

        // Knockdown (ADR-076).
        private bool _knockedDown;
        private float _knockdownTimer;
        private float _knockdownTotal;
        private float _kdHeightRestore;
        private IMovementControllerCC _kdBlock;
        private Transform _kdCam;
        private Quaternion _kdCamRestore;

        // Seizure (ADR-094 Enmienda 7).
        private bool _seized;
        private float _seizeTimer;
        private Transform _seizer;
        private IMovementControllerCC _seizeBlock;
        private Transform _seizeCam;
        private Quaternion _seizeCamRestore;

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

            // ADR-050: the live grab is checked FIRST and is mutually exclusive with the death one
            // — while you are held and alive there is no fade to run, and once the grab becomes a
            // kill `StartDeath` has already cleared this flag.
            if (_heldAlive)
                TickLiveGrab();
            else if (_grabTimer > 0f)
                TickGrab();
            else if (_dying)
                TickDeath();
            // ADR-094 Enmienda 7 — above the knockdown and below the three above, for the same
            // reason each of those sits where it does: it owns the camera while it runs, and
            // anything that outranks it calls EndSeizure() on the way in.
            else if (_seized)
                TickSeizure();
            // ADR-076: lowest priority of the four — death and the grab both own the camera and
            // both call EndKnockdown() on entry (below), so by the time either is active a
            // knockdown in progress has already been unwound.
            else if (_knockedDown)
                TickKnockdown();
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

                case GrabStartEvent:
                    // The window comes from the backend rather than being a second copy of the
                    // number here. `PHANTOM_GRAB_SECONDS` has one owner now.
                    var g = ev.data as Dictionary<string, object>;
                    StartLiveGrab(IPCParse.F(g, "window"));
                    break;

                case GrabReleaseEvent:
                    EndLiveGrab();
                    break;

                case KnockdownEvent:
                    var k = ev.data as Dictionary<string, object>;
                    StartKnockdown(IPCParse.F(k, "seconds"), IPCParse.F(k, "dx"), IPCParse.F(k, "dz"));
                    break;

                // ADR-094 Enmienda 7. Carries no payload at all: every duration and distance in
                // the seizure is presentation this side owns, and WHICH child has you is resolved
                // here too (ADR-016 §1 keeps its id off the wire).
                case SeizeEvent:
                    StartSeizure();
                    break;
            }
        }

        // ── The live grab (ADR-050 point 9) ────────────────────────────────────────────────────

        /// <summary>
        /// Something has hold of you and you are STILL ALIVE. This is the window that did not exist
        /// before: the old grab began inside <see cref="StartDeath"/>, i.e. after the backend had
        /// already applied 100 damage, ran for a fixed 0.9 s and read no input at all. There was no
        /// instant at which a player was held and could act, so there was nothing to escape.
        ///
        /// Reuses the whole presentation the death grab already built — camera possession, the drag
        /// to arm's length, the locomotion block, the handoff to `ProxyGrabHook` — and adds the one
        /// thing it never had: a way out.
        /// </summary>
        private void StartLiveGrab(float window)
        {
            if (_dying)
                return; // already gone; a grab means nothing now

            // ADR-076: a knockdown owns the collider height and (if still falling) the camera
            // tilt. A grab landing on a downed player has to unwind that first, or the two
            // sequences fight over the same camera transform and the height never gets restored.
            EndKnockdown();

            // TWO CREATURES CAN GRAB YOU IN THE SAME TICK — seen in the play-test log at 23:11:53,
            // phantom 61440 and 61441 both opening on victim 1. Without this guard the second call
            // added a SECOND set of locomotion blockers under the same key, and the single
            // `EndLiveGrab` that follows only removes one set: the player respawns unable to walk.
            // Re-grabbing while already held just refreshes the window; the backend is the one
            // tracking who actually has you.
            if (_heldAlive)
            {
                _heldWindow = window > 0.05f ? window : _heldWindow;
                _grabTimer = _heldWindow;
                return;
            }

            EnsureUi();
            _heldAlive = true;
            // Guard the window: a malformed or missing field must not produce an instant death or an
            // eternal hold. The backend's own value is the contract, this is only the floor.
            _heldWindow = window > 0.05f ? window : 2.5f;
            _grabTimer = _heldWindow;
            _struggle = 0f;
            _struggleSent = false;

            _grabber = ResolveGrabber();
            ActiveGrabber = _grabber;
            GrabProgress01 = 0f;

            var grabCam = ResolveCam();
            if (grabCam != null)
            {
                _camRestore = grabCam.transform;
                _camRotationAtGrab = _camRestore.localRotation;
            }

            // Held means held: the same locomotion block the death fade uses. Tracked in its own
            // field so releasing it can never race the death one.
            var movement = ResolveMovement();
            if (movement != null)
            {
                for (int i = 0; i < BlockedStates.Length; i++)
                    movement.AddStateBlocker(this, BlockedStates[i]);
                _heldBlock = movement;
            }
        }

        /// <summary>
        /// Drives the live grab: the same camera work as the death hold, plus reading input and
        /// reporting a successful struggle to the backend, which is the authority on whether you
        /// actually got free.
        ///
        /// The client NEVER decides the outcome on its own. It reports; the backend releases (or
        /// does not) and says so with `phantom_grab_release`. Deciding locally would mean a client
        /// reverting a death the server already owns, which is exactly the split ADR-025 exists to
        /// keep straight.
        /// </summary>
        private void TickLiveGrab()
        {
            _grabTimer -= Time.unscaledDeltaTime;

            // It let go, died, or despawned. The backend is still the authority on what happens
            // next; locally there is nothing left to hold on to.
            if (_grabber == null)
            {
                EndLiveGrab();
                return;
            }

            float t = 1f - Mathf.Clamp01(_grabTimer / Mathf.Max(0.01f, _heldWindow));
            GrabProgress01 = t;
            ActiveGrabber = _grabber;
            DriveGrabCamera(t);

            // Struggle. Decays while idle, so pacing yourself does not work.
            _struggle = Mathf.Max(0f, _struggle - StruggleDecayPerSecond * Time.unscaledDeltaTime);
            if (StrugglePressedThisFrame())
                _struggle += 1f;

            if (_struggleText != null)
            {
                int left = Mathf.Max(0, StruggleTarget - Mathf.FloorToInt(_struggle));
                _struggleText.text = left > 0 ? $"MASH  [SPACE]   {left}" : "…";
                _struggleText.enabled = true;
            }

            // Report ONCE. Mashing past the threshold must not spam the reliable channel, and the
            // release arrives as its own event whenever the backend gets to it.
            if (!_struggleSent && _struggle >= StruggleTarget)
            {
                _struggleSent = true;
                if (_ipc != null)
                    _ipc.SendAction(ProtocolActionTypes.ReportStruggle);
            }
        }

        /// <summary>
        /// Hand everything back. Called on release, on death (the grab became a kill), and when the
        /// grabber vanishes — so it must be safe to run twice.
        /// </summary>
        private void EndLiveGrab()
        {
            if (!_heldAlive)
                return;
            _heldAlive = false;
            _struggle = 0f;
            _struggleSent = false;

            if (_struggleText != null)
                _struggleText.enabled = false;

            if (_heldBlock != null)
            {
                for (int i = 0; i < BlockedStates.Length; i++)
                    _heldBlock.RemoveStateBlocker(this, BlockedStates[i]);
                _heldBlock = null;
            }

            // Only give the camera back if the death sequence has not taken it over: if the grab
            // became a kill, `StartDeath` is now driving it and restoring here would fight it.
            if (!_dying)
                EndGrab();
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

            // ADR-050: the grab ran out and became this. Tear the live hold down FIRST — it owns a
            // locomotion blocker and the struggle prompt, and the death sequence is about to take
            // the camera. Note this runs before `_dying` is set, so `EndLiveGrab` correctly hands
            // the camera back to be re-borrowed below rather than leaving it half-owned.
            EndLiveGrab();
            // ADR-076: same reasoning — a downed player can still be killed by something else
            // while on the ground, and the death sequence needs the collider height and camera
            // back to normal before it takes them over.
            EndKnockdown();

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

                // Borrow the camera, and remember what to give back (see EndGrab).
                var grabCam = ResolveCam();
                if (grabCam != null)
                {
                    _camRestore = grabCam.transform;
                    _camRotationAtGrab = _camRestore.localRotation;
                }

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
                EndGrab();
                return;
            }

            float t = 1f - Mathf.Clamp01(_grabTimer / GrabTime); // 0 → 1 across the hold
            GrabProgress01 = t;
            ActiveGrabber = _grabber;
            DriveGrabCamera(t);

            if (_grabTimer <= 0f)
                EndGrab(); // the fade takes over next frame
        }

        /// <summary>
        /// The presentation of being held, shared by the death grab and the live one (ADR-050): the
        /// view swings onto the creature, the frame comes apart as it holds you, and the body is
        /// dragged to arm's length and lifted off its feet.
        ///
        /// Extracted rather than duplicated because these two grabs must not drift apart — the live
        /// one IS the death one with an exit, and a second copy of this would be a second place for
        /// the camera-roll bug below to come back in.
        ///
        /// DECLARED LIMIT: there is no authored grab animation. The creature holds the strike pose
        /// the backend already puts it in and stays revealed through it, so what reads is "it has
        /// you", not "it is performing a grab". A real animation needs the creature and the player
        /// aligned by a shared clip, which is authoring work, not code.
        /// </summary>
        private void DriveGrabCamera(float t)
        {
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
        }

        /// <summary>
        /// Put the camera back exactly as it was and stop everything the grab was driving.
        ///
        /// THE BUG THIS EXISTS FOR, reported after a play-test death: "respawneo y la camara esta
        /// mal posicionada o deformada". The grab rolls the camera on its Z axis to tip the horizon,
        /// and NOTHING in the game ever writes camera roll — STP's look handler owns yaw and pitch
        /// only. So the last tilt of the struggle simply stayed on, through the fade, through the
        /// respawn, for the rest of the session. A borrowed transform has to be given back.
        ///
        /// The motor is zeroed for the same reason: the lift that gets the body off its feet would
        /// otherwise still be in flight when the respawn teleport lands, and shove the fresh player.
        /// </summary>
        private void EndGrab()
        {
            _grabTimer = 0f;
            ActiveGrabber = null;
            GrabProgress01 = 0f;

            if (_camRestore != null)
            {
                _camRestore.localRotation = _camRotationAtGrab;
                _camRestore = null;
            }

            var motor = ResolveMotor();
            if (motor != null)
                motor.SetVelocity(Vector3.zero);
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
            // Belt and braces: the death can end through paths the grab never saw (a re-kill during
            // the fade, the component being destroyed), and a camera left rolled is forever.
            EndGrab();
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

        // ── Knockdown (ADR-076) ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// The disguised ambush connected: no health lost, just a fall and a scare. Zero-daño by
        /// design (ADR-076 point 1, Joel's call) — the derived clip is the collider dropping and
        /// the camera tilting, both procedural, same declared-limit reasoning as the grab above:
        /// there is no authored knockdown/rise animation to align the creature to.
        /// </summary>
        private void StartKnockdown(float seconds, float dx, float dz)
        {
            // Death and the live grab both outrank a knockdown outright — see the doc comment on
            // the Update() tick order.
            if (_dying || _heldAlive)
                return;

            // Same reentrancy shape as StartLiveGrab's two-creatures-same-tick guard: a second
            // knockdown must extend the hold, never stack a second set of blockers under the same
            // key (the guard above it exists BECAUSE that bug already happened once, in the grab).
            if (_knockedDown)
            {
                _knockdownTimer = Mathf.Max(_knockdownTimer, seconds > 0.05f ? seconds : _knockdownTimer);
                return;
            }

            _knockedDown = true;
            _knockdownTotal = seconds > 0.05f ? seconds : 2.0f; // floor, same reasoning as the grab window
            _knockdownTimer = _knockdownTotal;

            var motor = ResolveMotor();
            if (motor != null)
            {
                // Cache BEFORE touching anything — the lesson the grab's camera-roll bug already
                // paid for in this file. Height first: if `CanSetHeight` refuses, nothing else
                // about this knockdown should apply either.
                _kdHeightRestore = motor.Height;
                if (motor.CanSetHeight(KnockdownHeight))
                    motor.SetVelocity(new Vector3(dx, 0f, dz));
            }

            var cam = ResolveCam();
            if (cam != null)
            {
                _kdCam = cam.transform;
                _kdCamRestore = _kdCam.localRotation;
            }

            var movement = ResolveMovement();
            if (movement != null)
            {
                for (int i = 0; i < KnockdownBlockedStates.Length; i++)
                    movement.AddStateBlocker(this, KnockdownBlockedStates[i]);
                _kdBlock = movement;
            }
        }

        /// <summary>
        /// No QTE, deliberately (Joel's call): the scare IS watching it close the distance while
        /// you cannot get up. Looking around stays free — only locomotion is blocked — so the
        /// player can at least see it coming.
        /// </summary>
        private void TickKnockdown()
        {
            _knockdownTimer -= Time.unscaledDeltaTime;
            float elapsed = _knockdownTotal - _knockdownTimer;

            // Multiplicative blend, not two independent clamps: `fallT` ramps 0→1 over the first
            // `KnockdownFallTime` seconds, `riseT` ramps 0→1 over the LAST `KnockdownRiseTime`
            // seconds (counting down). Their product is 0 at both ends and 1 through the middle,
            // so even a very short `seconds` still falls and rises instead of snapping to down.
            float fallT = Mathf.Clamp01(elapsed / KnockdownFallTime);
            float riseT = Mathf.Clamp01(1f - _knockdownTimer / KnockdownRiseTime);
            float down = fallT * (1f - riseT);

            var motor = ResolveMotor();
            if (motor != null && motor.CanSetHeight(KnockdownHeight))
                motor.Height = Mathf.Lerp(_kdHeightRestore, KnockdownHeight, down);

            if (_kdCam != null)
                _kdCam.localRotation = _kdCamRestore * Quaternion.Euler(KnockdownRollMax * down, 0f, 0f);

            if (_knockdownTimer <= 0f)
                EndKnockdown();
        }

        /// <summary>
        /// Restores EXACTLY what was cached at the start — height and camera rotation — and drops
        /// the locomotion blockers. Safe to call when no knockdown is active (the death and grab
        /// entry points both call it unconditionally) and safe to call twice.
        /// </summary>
        private void EndKnockdown()
        {
            if (!_knockedDown)
                return;
            _knockedDown = false;
            _knockdownTimer = 0f;

            var motor = ResolveMotor();
            if (motor != null && motor.CanSetHeight(_kdHeightRestore))
                motor.Height = _kdHeightRestore;

            if (_kdCam != null)
            {
                _kdCam.localRotation = _kdCamRestore;
                _kdCam = null;
            }

            if (_kdBlock != null)
            {
                for (int i = 0; i < KnockdownBlockedStates.Length; i++)
                    _kdBlock.RemoveStateBlocker(this, KnockdownBlockedStates[i]);
                _kdBlock = null;
            }
        }

        // ── The seizure (ADR-094 Enmienda 7) ────────────────────────────────────────────────────

        /// <summary>
        /// A faceling child has you from behind. Wrenches the view onto it, drags it to your face
        /// while it screams, then throws you off dazed.
        ///
        /// WHICH child is decided HERE, not by the backend, and that is deliberate: ADR-016 §1
        /// keeps a creature's id off the wire entirely, so the cue arrives anonymous. The nearest
        /// one is the right answer anyway — it is the same geometry the backend used to pick the
        /// attacker in the first place.
        /// </summary>
        private void StartSeizure()
        {
            // Death and the live grab both outrank this outright, same order the knockdown obeys:
            // two sequences driving one camera is the bug this file's own history already paid for.
            if (_dying || _heldAlive)
                return;

            // Re-entrancy: refresh rather than stack a second set of blockers under the same key.
            // The grab learned this the hard way (a player who respawned unable to walk).
            if (_seized)
            {
                _seizeTimer = SeizeTotalTime;
                return;
            }

            // A knockdown underneath would be fighting for the same camera and collider height.
            EndKnockdown();

            _seizer = ResolveGrabber(); // same nearest-creature search the grab uses
            if (_seizer == null)
                return; // nothing to hold your face against; the Hit that rode along still landed

            _seized = true;
            _seizeTimer = SeizeTotalTime;

            var cam = ResolveCam();
            if (cam != null)
            {
                // Cache BEFORE touching anything — the lesson the grab's camera-roll bug paid for.
                _seizeCam = cam.transform;
                _seizeCamRestore = _seizeCam.localRotation;
            }

            var movement = ResolveMovement();
            if (movement != null)
            {
                for (int i = 0; i < SeizeBlockedStates.Length; i++)
                    movement.AddStateBlocker(this, SeizeBlockedStates[i]);
                _seizeBlock = movement;
            }

            FacelingDazeEffect.Begin();
        }

        private void TickSeizure()
        {
            _seizeTimer -= Time.unscaledDeltaTime;

            // It died or despawned mid-hold. Give control back immediately rather than holding a
            // camera on nothing.
            if (_seizer == null || _seizeTimer <= 0f)
            {
                EndSeizure();
                return;
            }

            var cam = ResolveCam();
            if (cam != null)
            {
                var focus = _seizer.position + Vector3.up * SeizeFaceHeight;
                var to = focus - cam.transform.position;
                if (to.sqrMagnitude > 1e-4f)
                {
                    cam.transform.rotation = Quaternion.Slerp(
                        cam.transform.rotation,
                        Quaternion.LookRotation(to.normalized, Vector3.up),
                        1f - Mathf.Exp(-SeizeTurnLerp * Time.unscaledDeltaTime));
                }

                // Full-intensity tremor from the first frame — see `SeizeShake`.
                cam.transform.rotation *= Quaternion.Euler(
                    Random.Range(-SeizeShake, SeizeShake),
                    Random.Range(-SeizeShake, SeizeShake),
                    Random.Range(-SeizeShake, SeizeShake) * 0.5f);
            }

            // Pulled in to face distance, through the motor (ADR-009 again: a transform write is
            // overwritten by the next motor step).
            var motor = ResolveMotor();
            if (motor != null)
            {
                var flat = _seizer.position - motor.transform.position;
                flat.y = 0f;
                float d = flat.magnitude;
                motor.SetVelocity(d > SeizeFaceDistance && d > 1e-3f
                    ? flat / d * SeizePullSpeed
                    : Vector3.zero);
            }
        }

        /// <summary>Throws you off and gives control back. Idempotent.</summary>
        private void EndSeizure()
        {
            if (!_seized)
                return;
            _seized = false;

            // THE SHOVE. Away from it, through the motor. This is the "luego te empujan" half —
            // the backend sends no impulse for the seizure precisely so that the throw lines up
            // with the end of the animation rather than with the packet that started it.
            var motor = ResolveMotor();
            if (motor != null && _seizer != null)
            {
                var away = motor.transform.position - _seizer.position;
                away.y = 0f;
                if (away.sqrMagnitude > 1e-4f)
                    motor.SetVelocity(away.normalized * SeizeShoveSpeed);
            }

            // Give the camera back exactly as it was. The grab's own bug comment explains why this
            // is not optional: nothing else in the game ever writes camera roll, so a tilt left on
            // survives the respawn and the rest of the session.
            if (_seizeCam != null)
            {
                _seizeCam.localRotation = _seizeCamRestore;
                _seizeCam = null;
            }

            if (_seizeBlock != null)
            {
                for (int i = 0; i < SeizeBlockedStates.Length; i++)
                    _seizeBlock.RemoveStateBlocker(this, SeizeBlockedStates[i]);
                _seizeBlock = null;
            }

            _seizer = null;
        }

        // ── Resolvers (local player only; remote avatars are excluded) ─────────────────────────────

        private Camera ResolveCam()
        {
            if (_cam == null)
                _cam = Camera.main;
            return _cam;
        }

        private IMovementControllerCC ResolveMovement()
            => LocalPlayerLocator.Find<PlayerMovementController>();

        private CharacterControllerMotor ResolveMotor()
            => LocalPlayerLocator.Find<CharacterControllerMotor>();

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

            // ADR-050: the struggle prompt. Smaller than YOU DIED and sat low, because it competes
            // with the creature's face for the same two seconds and the face has to win.
            _struggleText = CreateText("Struggle", string.Empty);
            _struggleText.fontSize = 34;
            _struggleText.color = new Color(0.92f, 0.88f, 0.82f, 0.95f);
            _struggleText.rectTransform.anchoredPosition = new Vector2(0f, -180f);
            _struggleText.enabled = false;
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

            EndLiveGrab(); // releases the live-grab block if it is holding one
            EndDeath(); // releases the movement block if mid-fade

            if (_canvas != null)
                Destroy(_canvas.gameObject);

            if (_instance == this)
                _instance = null;
        }
    }
}
