using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.MovementSystem;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// Freezes the LOCAL player's movement for the duration of the pickup gesture (ADR-011 Fase 1,
    /// fully client-side). Hooks the local pickup CONFIRMATION — the IPC "stp_pickup_granted" event,
    /// which the backend delivers only to the client that picked up — NOT rp.animation (that is the
    /// proxy channel). Uses STP's native, reusable movement blocker
    /// (<see cref="IMovementControllerCC.AddStateBlocker"/>): the motor stays alive so
    /// PlayerPoseTransmitter keeps sending the (now static) pose; the player just can't enter a
    /// locomotion state until the gesture ends.
    ///
    /// Duration is read from the proxy prefab's <see cref="ProxyPickupHook.GestureDuration"/>
    /// (pickup clip ÷ speed, baked by RemoteAvatarPrefabBuilder) — no hard-coded number; it follows
    /// a PickupSpeed change on the next rebuild. Self-bootstraps; fully removable (delete the file).
    /// </summary>
    public sealed class LocalPickupInputLock : MonoBehaviour
    {
        private const string PickupGrantedEvent = "stp_pickup_granted";
        private const string ProxyPrefabResource = "RemotePlayerAvatar";
        private const float FallbackDuration = 0.6f;

        // Locomotion states suspended during the gesture. Idle/Airborne are left intact (blocking
        // Airborne would break gravity; Idle is the not-moving state). These three always exist on a
        // player controller (Walk/Run/Jump), so AddStateBlocker never hits a null state.
        private static readonly MovementStateType[] BlockedStates =
        {
            MovementStateType.Walk, MovementStateType.Run, MovementStateType.Jump
        };

        private static LocalPickupInputLock _instance;

        private IPCClient _ipc;
        private IMovementControllerCC _movement;
        private bool _blocked;
        private float _releaseTime;
        private float _gestureDuration = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[LocalPickupInputLock]");
            _instance = go.AddComponent<LocalPickupInputLock>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            // Subscribe once the IPC client exists (mirrors StpPickupController).
            if (_ipc == null && IPCClient.TryGetInstance(out var ipc))
            {
                _ipc = ipc;
                _ipc.AddEventListener(OnGameEvent);
            }

            if (_blocked && Time.time >= _releaseTime)
                Unblock();
        }

        // Fired on the main thread (IPCClient.Update drains the event queue).
        private void OnGameEvent(GameEventMsg ev)
        {
            if (ev == null || ev.eventType != PickupGrantedEvent)
                return;

            // Extend the window on a re-grant; engage the blockers exactly once.
            _releaseTime = Time.time + GetGestureDuration();
            if (!_blocked)
                Block();
        }

        private void Block()
        {
            var movement = ResolveMovement();
            if (movement == null)
                return;

            for (int i = 0; i < BlockedStates.Length; i++)
                movement.AddStateBlocker(this, BlockedStates[i]);

            _movement = movement;
            _blocked = true;
        }

        private void Unblock()
        {
            _blocked = false;
            if (_movement == null)
                return;

            for (int i = 0; i < BlockedStates.Length; i++)
                _movement.RemoveStateBlocker(this, BlockedStates[i]);

            _movement = null;
        }

        // The LOCAL player's movement controller. Proxies (MTP_PlayerViewer) have no
        // PlayerMovementController, so this finds the local player; the RemotePlayerManager
        // exclusion is defensive belt-and-suspenders.
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

        private float GetGestureDuration()
        {
            if (_gestureDuration > 0f)
                return _gestureDuration;

            var prefab = Resources.Load<GameObject>(ProxyPrefabResource);
            var hook = prefab != null ? prefab.GetComponent<ProxyPickupHook>() : null;
            _gestureDuration = hook != null && hook.GestureDuration > 0f
                ? hook.GestureDuration
                : FallbackDuration;
            return _gestureDuration;
        }

        private void OnDestroy()
        {
            if (_ipc != null)
                _ipc.RemoveEventListener(OnGameEvent);

            Unblock();

            if (_instance == this)
                _instance = null;
        }
    }
}
