using PolymindGames;
using PolymindGames.MovementSystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Single, motor-independent transmitter of the LOCAL player's pose over IPC (~30 Hz).
    /// Replaces the deleted PlayerPoseSender / LocalPoseSendGate / MovementReconciler stack.
    ///
    /// Three hard lessons baked into the design (do not regress):
    ///   1. Reads <c>_motor.transform.position</c>, NEVER the Player root. Only the
    ///      CharacterControllerMotor moves its own transform when walking; the Player root
    ///      stays at spawn / origin (see CharacterControllerMotor.cs:236,282).
    ///   2. Re-resolves the motor LIVE every frame, never caches it only once. The STP rig
    ///      is rebuilt at runtime (triggered by CharacterClothing.Start's NRE), which destroys
    ///      a cached motor; we revalidate and re-find on cache-miss.
    ///   3. ONE sender. No "yield to another component" handshake (that deadlocked the old
    ///      system). This component lives on its own DontDestroyOnLoad object, independent of
    ///      the player GameObject, so it is immune to the rig rebuild that was the root cause.
    /// </summary>
    public sealed class PlayerPoseTransmitter : MonoBehaviour
    {
        private static PlayerPoseTransmitter _instance;

        // Read-only diagnostics mirror for NetworkDebugHud (no behavioural coupling).
        public static bool IsSending { get; private set; }
        public static Vector3 LastSent { get; private set; }

        private const float SendHz = 30f;
        private const float SendInterval = 1f / SendHz; // ~33 ms (single sender; backend read loop handles this easily)
        private const float LogInterval = 1f;           // [POSE_TX] heartbeat @ 1 Hz

        private CharacterControllerMotor _motor;
        private float _sendAccum;
        private float _logAccum;
        private uint _inputSeq;
        private uint _clientTick;
        private Vector3 _prevPos;
        private bool _hasPrev;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[PlayerPoseTransmitter]");
            _instance = go.AddComponent<PlayerPoseTransmitter>();
            DontDestroyOnLoad(go);
        }

        // Hard singleton: even if a second instance is created by any path (a duplicate scene
        // load, a baked component, a manual AddComponent), only one survives → never doubles the
        // IPC send rate.
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
            // Lesson 2: re-resolve every frame (cheap cache-hit path, full re-find on miss).
            ResolveMotor();

            float frameDt = Time.unscaledDeltaTime;
            _logAccum += frameDt;
            _sendAccum += frameDt;
            if (_sendAccum < SendInterval)
                return;

            float dt = _sendAccum; // elapsed since last send → drives the finite-diff velocity
            _sendAccum = 0f;

            // Lesson 1 + SKIP: no motor → do NOT send. Never report the Player root or (0,1.8,0).
            if (_motor == null)
            {
                IsSending = false;
                _hasPrev = false; // restart the finite difference after a gap / rig rebuild
                MaybeLog(false, Vector3.zero);
                return;
            }

            Vector3 pos = _motor.transform.position;

            // Minimal inline gate: discard the literal origin only (defensive — with the
            // motor present pos is already valid). No separate LocalPoseSendGate.
            if (pos == Vector3.zero)
            {
                IsSending = false;
                _hasPrev = false;
                MaybeLog(false, pos);
                return;
            }

            float yaw = _motor.transform.eulerAngles.y;

            Vector3 vel = Vector3.zero;
            if (_hasPrev && dt > 0f)
                vel = (pos - _prevPos) / dt;
            _prevPos = pos;
            _hasPrev = true;

            // move_state only drives server-side run-stamina drain (the speed cap is always the
            // sprint cap), so a simple idle/walk classification is sufficient and harmless.
            Vector3 horiz = new Vector3(vel.x, 0f, vel.z);
            byte moveState = horiz.magnitude > 0.1f ? (byte)1 : (byte)0;

            // ADR-020: report the LOCAL crouch state, derived from the motor height. STP's native
            // CharacterCrouchState lowers Motor.Height when crouching; we only READ it (never apply,
            // never reimplement — rule #3). Relayed cosmetically to peers; not authoritative.
            var motorCC = (IMotorCC)_motor;
            bool crouch = motorCC.Height < motorCC.DefaultHeight - 0.05f;

            bool sent = false;
            if (IPCClient.TryGetInstance(out var ipc) && ipc.IsConnected)
            {
                ipc.SendPlayerInput(_inputSeq, _clientTick, pos, vel, moveState, 0f, yaw, 0, crouch);
                _inputSeq++;
                _clientTick++;
                LastSent = pos;
                sent = true;
            }

            IsSending = sent;
            MaybeLog(sent, pos);
        }

        /// <summary>
        /// Finds the LOCAL motor live. Revalidates the cache (Unity's overloaded == reports a
        /// destroyed motor as null, so a rig rebuild forces a re-find) and excludes remote
        /// avatars (anything under a RemotePlayerManager hierarchy).
        /// </summary>
        private void ResolveMotor()
        {
            if (_motor != null)
                return;

            var motors = FindObjectsByType<CharacterControllerMotor>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            for (int i = 0; i < motors.Length; i++)
            {
                var m = motors[i];
                if (m.GetComponentInParent<RemotePlayerManager>() != null)
                    continue; // remote avatar, not the local player

                _motor = m;
                return;
            }

            _motor = null;
        }

        // Temporary live-verification log (throttled to 1 Hz, removable).
        private void MaybeLog(bool sent, Vector3 pos)
        {
            if (_logAccum < LogInterval)
                return;

            _logAccum = 0f;
            Debug.Log($"[POSE_TX] seq={_inputSeq} motorFound={(_motor != null)} sending={sent} pos={pos}");
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
    }
}
