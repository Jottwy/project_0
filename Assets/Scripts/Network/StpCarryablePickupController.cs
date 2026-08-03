using System.Collections.Generic;
using PolymindGames;
using PolymindGames.WieldableSystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase B2.5: routes STP carryable pickups through the host and puts the carryable IN THE
    /// HAND of the local player ONLY on host confirmation. Unlike the item pickup (which credits
    /// an inventory count), the grant instantiates a fresh <see cref="CarryablePickup"/> and
    /// carries it via <see cref="ICarryableControllerCC.TryCarryObject"/>. The replicated world
    /// carryable was already removed (host removed it from stp_carryables → replicator destroyed
    /// it). Self-bootstraps; fully removable. Mirrors <see cref="StpPickupController"/>.
    /// </summary>
    public sealed class StpCarryablePickupController : MonoBehaviour
    {
        private static StpCarryablePickupController _instance;

        // carryable_id → the local character that requested it (granted the in-hand carryable).
        private readonly Dictionary<uint, ICharacter> _pending = new Dictionary<uint, ICharacter>();
        private IPCClient _ipc;

        // Tracks the local player's current carry type/count via CarryableController's own public
        // events, since ICarryableControllerCC doesn't expose ActiveDefinition. Used to refuse a
        // pickup request locally instead of letting the host grant (and remove from world) an item
        // the client can't actually carry, which used to strand an orphaned pickup and lose it.
        private ICarryableControllerCC _carryCc;
        private CarryableDefinition _activeCarryDef;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpCarryablePickupController]");
            _instance = go.AddComponent<StpCarryablePickupController>();
            DontDestroyOnLoad(go);
        }

        /// <summary>Called by a StpCarryablePickupGate when the local player interacts.</summary>
        public static void RequestPickup(uint carryableId, int defId, ICharacter recoger)
        {
            if (_instance != null)
                _instance.Request(carryableId, defId, recoger);
        }

        private void Request(uint carryableId, int defId, ICharacter recoger)
        {
            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            if (recoger.TryGetCC(out ICarryableControllerCC carry))
            {
                TrackCarryState(carry);

                var def = CarryableDefinition.GetWithId(defId);
                bool atCapacity = carry.CarryCount > 0 && (_activeCarryDef != def || carry.CarryCount >= def.MaxCarryCount);
                if (def != null && atCapacity)
                {
                    Debug.Log($"[StpCarryablePickupController] pickup refused locally: already carrying {carry.CarryCount}/{_activeCarryDef?.MaxCarryCount} of '{_activeCarryDef?.Name}'.");
                    return;
                }
            }

            _pending[carryableId] = recoger;
            ipc.SendStpCarryablePickup(carryableId);
            Debug.Log($"[StpCarryablePickupController] requested pickup carryable_id={carryableId}");
        }

        private void TrackCarryState(ICarryableControllerCC carry)
        {
            if (_carryCc == carry)
                return;

            if (_carryCc != null)
            {
                _carryCc.ObjectCarryStarted -= OnCarryStarted;
                _carryCc.ObjectCarryStopped -= OnCarryStopped;
            }

            _carryCc = carry;
            _activeCarryDef = null;
            carry.ObjectCarryStarted += OnCarryStarted;
            carry.ObjectCarryStopped += OnCarryStopped;
        }

        private void OnCarryStarted(CarryableDefinition def) => _activeCarryDef = def;
        private void OnCarryStopped() => _activeCarryDef = null;

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
            if (ev == null || ev.eventType != "stp_carryable_pickup_granted")
                return;

            var d = ev.data as Dictionary<string, object>;
            if (d == null)
                return;

            uint carryableId = (uint)IPCParse.L(d, "carryable_id");
            int defId = (int)IPCParse.L(d, "def_id");

            // Only carry if THIS client requested it (host grants to the winner only).
            if (!_pending.TryGetValue(carryableId, out var recoger))
                return;
            _pending.Remove(carryableId);

            if (recoger == null)
                return;

            var def = CarryableDefinition.GetWithId(defId);
            if (def == null || def.Pickup == null)
            {
                Debug.LogWarning($"[StpCarryablePickupController] grant for unknown/empty def_id={defId} (carryable_id={carryableId}).");
                return;
            }

            if (!recoger.TryGetCC(out ICarryableControllerCC carry))
                return;

            // Fresh in-hand instance (snaps to the hand socket on carry). It has NO
            // NetworkCarryableInstance, so when later dropped the drop watcher will adopt it.
            var pickup = Instantiate(def.Pickup);
            if (!carry.TryCarryObject(pickup))
            {
                // Local capacity changed between the request and the grant (race). The host
                // already removed the world object, so this is unrecoverable either way — at
                // least don't leak an orphaned, un-carried pickup instance in the scene.
                Debug.LogWarning($"[StpCarryablePickupController] grant for carryable_id={carryableId} couldn't be carried locally (capacity race); destroying orphaned instance.");
                Destroy(pickup.gameObject);
                return;
            }
            Debug.Log($"[StpCarryablePickupController] carried '{def.Name}' in hand (carryable_id={carryableId}).");
        }

        private void OnDestroy()
        {
            if (_ipc != null)
                _ipc.RemoveEventListener(OnGameEvent);
            if (_carryCc != null)
            {
                _carryCc.ObjectCarryStarted -= OnCarryStarted;
                _carryCc.ObjectCarryStopped -= OnCarryStopped;
            }
            if (_instance == this)
                _instance = null;
        }
    }
}
