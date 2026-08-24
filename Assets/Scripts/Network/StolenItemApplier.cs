using PolymindGames;
using PolymindGames.InventorySystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-094 punto 4 — takes the stolen stack out of the local player's REAL inventory when a
    /// faceling child robs them.
    ///
    /// WHY THIS HAS TO EXIST AT ALL (Enmienda 1, D2). The backend's `inventory_v2` is a MIRROR the
    /// client reports up, not the inventory itself — the real one is STP's, here. A backend that
    /// only removed the stack from its own mirror would have the theft undone by the very next
    /// `report_inventory`. The theft is only real once it lands in this component.
    ///
    /// WHY NOT REUSE `InventoryRestorer`. That one consumes `inventory_restored`, and it applies it
    /// by CLEARING every container and refilling — a whole-inventory transplant, correct for
    /// hydration and completely wrong for taking one stack. This is the surgical counterpart.
    ///
    /// The removal itself is STP-native (<c>IInventory.RemoveItemsById</c>, the same call the
    /// vendor's own code uses), so no container routing is reimplemented here: which slots it comes
    /// out of is STP's decision, exactly as it is when the player drops something by hand.
    ///
    /// Self-bootstraps; fully removable — delete the file and the client simply stops honouring
    /// thefts (the backend keeps asking, nothing crashes).
    /// </summary>
    public sealed class StolenItemApplier : MonoBehaviour
    {
        private const string StolenEvent = "item_stolen";

        private static StolenItemApplier _instance;

        private IPCClient _ipc;
        private ICharacter _character;

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

            var go = new GameObject("[StolenItemApplier]");
            _instance = go.AddComponent<StolenItemApplier>();
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
            // Same late-subscribe shape as InventoryRestorer: the IPC client is a long-lived
            // singleton that may not exist yet during scene warm-up.
            if (_ipc == null && IPCClient.TryGetInstance(out var ipc))
            {
                _ipc = ipc;
                _ipc.AddEventListener(OnGameEvent);
            }
        }

        private void OnGameEvent(GameEventMsg ev)
        {
            if (ev == null || ev.eventType != StolenEvent)
                return;

            if (!TryParse(ev.data, out int defId, out int count) || count <= 0)
            {
                Debug.LogWarning("[StolenItemApplier] item_stolen with unusable payload — ignored");
                return;
            }

            // Applied IMMEDIATELY, never stashed for a later frame like InventoryRestorer does.
            // The theft is already committed on the backend by the time this arrives, so a
            // deferred apply would leave a window where the mirror and the real inventory disagree
            // — and `report_inventory` runs on its own clock, inside that window.
            ResolveCharacter();
            var inventory = _character?.Inventory;
            if (inventory == null)
            {
                Debug.LogWarning(
                    $"[StolenItemApplier] item_stolen (def={defId} x{count}) arrived with no local " +
                    "character — the backend has already taken it, so the mirrors will disagree " +
                    "until the next report_inventory reconciles them.");
                return;
            }

            int removed = inventory.RemoveItemsById(defId, count);
            if (removed < count)
            {
                // Not an error: the backend picked from a mirror that can lag a frame or two, so
                // it can name a stack the player just spent. Logged because a persistent gap here
                // means the mirror is drifting, which is worth seeing.
                Debug.Log(
                    $"[StolenItemApplier] asked to remove {count} of def={defId}, removed {removed} " +
                    "(mirror lag — the rest was already gone).");
            }
        }

        private void ResolveCharacter()
        {
            if (_character != null)
                return;

            var motor = FindFirstObjectByType<PolymindGames.MovementSystem.CharacterControllerMotor>();
            if (motor != null)
                _character = motor.GetComponentInParent<ICharacter>();
        }

        /// <summary>
        /// `MsgPackReader.ReadValue()` materializes maps as <c>Dictionary&lt;string, object&gt;</c>
        /// and numbers as boxed integer types whose exact width is not guaranteed — hence the
        /// Convert calls rather than direct casts, matching every other IPC consumer here.
        /// </summary>
        private static bool TryParse(object data, out int defId, out int count)
        {
            defId = 0;
            count = 0;

            if (data is not System.Collections.Generic.Dictionary<string, object> map)
                return false;

            if (!map.TryGetValue("def_id", out var rawDef) || rawDef == null)
                return false;
            if (!map.TryGetValue("count", out var rawCount) || rawCount == null)
                return false;

            try
            {
                defId = System.Convert.ToInt32(rawDef);
                count = System.Convert.ToInt32(rawCount);
            }
            catch (System.Exception)
            {
                return false;
            }

            return defId != 0;
        }

        private void OnDestroy()
        {
            if (_ipc != null)
                _ipc.RemoveEventListener(OnGameEvent);
        }
    }
}
