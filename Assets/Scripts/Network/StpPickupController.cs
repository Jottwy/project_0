using System.Collections.Generic;
using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// Phase 2: persistent controller that routes STP pickups through the host and credits the
    /// local inventory ONLY on host confirmation. The item GameObject (and its
    /// <see cref="NetworkItemPickupGate"/>) is destroyed when the host removes the item, so the
    /// pending state and the credit live here, not on the item. Self-bootstraps; fully removable.
    /// </summary>
    public sealed class StpPickupController : MonoBehaviour
    {
        private static StpPickupController _instance;

        // item_id → the local character that requested it (credited when the host grants).
        private readonly Dictionary<uint, ICharacter> _pending = new Dictionary<uint, ICharacter>();
        private IPCClient _ipc;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null)
                return;

            var go = new GameObject("[StpPickupController]");
            _instance = go.AddComponent<StpPickupController>();
            DontDestroyOnLoad(go);
        }

        /// <summary>Called by a NetworkItemPickupGate when the local player interacts.</summary>
        public static void RequestPickup(uint itemId, ICharacter recoger)
        {
            if (_instance != null)
                _instance.Request(itemId, recoger);
        }

        private void Request(uint itemId, ICharacter recoger)
        {
            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return;

            _pending[itemId] = recoger;
            ipc.SendStpPickup(itemId);
            Debug.Log($"[StpPickupController] requested pickup item_id={itemId}");
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
            if (ev == null || ev.eventType != "stp_pickup_granted")
                return;

            var d = ev.data as Dictionary<string, object>;
            if (d == null)
                return;

            uint itemId = (uint)IPCParse.L(d, "item_id");
            int defId = (int)IPCParse.L(d, "def_id");
            int count = Mathf.Max(1, (int)IPCParse.L(d, "count"));

            // Only credit if THIS client requested it (host grants to the winner only).
            if (!_pending.TryGetValue(itemId, out var recoger))
                return;
            _pending.Remove(itemId);

            if (recoger == null)
                return;

            var def = ItemDefinition.GetWithId(defId);
            if (def == null)
            {
                Debug.LogWarning($"[StpPickupController] grant for unknown def_id={defId} (item_id={itemId}).");
                return;
            }

            recoger.Inventory.AddItem(new ItemStack(new Item(def), count));
            Debug.Log($"[StpPickupController] credited '{def.Name}' x{count} (item_id={itemId}) to local inventory.");
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
