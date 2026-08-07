using System.Collections.Generic;
using PolymindGames;
using PolymindGames.MovementSystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-032 amendment — applies the backend's persisted REAL STP inventory to the local
    /// player after a world-save hydration. Counterpart of <see cref="InventoryReporter"/>.
    ///
    /// Trigger: the one-shot <c>inventory_restored {items:[{item_id,quantity}]}</c> event, which
    /// the backend emits in the SAME deferred first-input window as <c>session_restored</c>
    /// (Tarea 1's position snap). The two are independent consumers of independent events — the
    /// applier snaps the motor, this component fills containers — so ordering between them is
    /// cosmetic and they cannot step on each other.
    ///
    /// Apply = CLEAR first, then add: every container is cleared with the same STP-native
    /// <c>IItemContainer.Clear()</c> DeathLootReporter.OnRespawn uses (idempotence — a duplicate
    /// event or a pre-populated fresh-session inventory can never double up), then each persisted
    /// stack is added via the STP-native <c>IInventory.AddItemsById</c> (its own routing/stacking
    /// decides the slots). Anything that doesn't fit is logged and dropped (the container total
    /// is the same one the stacks came from, so overflow only happens on a config change).
    ///
    /// The event can arrive before the local character exists (first-input window fires during
    /// scene warm-up) — the payload is stashed and applied as soon as the character resolves.
    /// Self-bootstraps; removable.
    /// </summary>
    public sealed class InventoryRestorer : MonoBehaviour
    {
        private const string RestoredEvent = "inventory_restored";

        private static InventoryRestorer _instance;

        private IPCClient _ipc;
        private CharacterControllerMotor _motor;
        private ICharacter _character;

        // Stacks waiting for the character to exist. Null = nothing pending.
        private List<(int itemId, int quantity)> _pending;

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

            var go = new GameObject("[InventoryRestorer]");
            _instance = go.AddComponent<InventoryRestorer>();
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
            // Subscribe once the IPC client exists (same pattern/known-limit as
            // AuthoritativePoseApplier: the client is a long-lived singleton).
            if (_ipc == null && IPCClient.TryGetInstance(out var ipc))
            {
                _ipc = ipc;
                _ipc.AddEventListener(OnGameEvent);
            }

            if (_pending == null)
                return;

            ResolveCharacter();
            if (_character?.Inventory == null)
                return; // keep waiting — scene still warming up

            Apply(_pending);
            _pending = null;
        }

        private void OnGameEvent(GameEventMsg ev)
        {
            if (ev == null || ev.eventType != RestoredEvent)
                return;

            var stacks = ParseStacks(ev.data);
            if (stacks == null)
            {
                Debug.LogWarning("[InventoryRestorer] inventory_restored with unparsable payload — ignored");
                return;
            }

            _pending = stacks; // applied in Update once the character exists
            Debug.Log($"[InventoryRestorer] inventory_restored received: {stacks.Count} stacks (pending apply)");
        }

        /// ADR-045 fix: `MsgPackReader.ReadValue()` materializes a msgpack array as `object[]`
        /// (see its doc comment + `ReadArray`), NEVER `List<object>` — every other IPC event
        /// consumer in the project (`IPCParse.Vec3`/`IntArray`/`IntArray2`/`StringArray`) checks
        /// `is object[]` for exactly this reason. This method checked `List<object>`, a type the
        /// reader never produces, so the cast failed on every single payload and
        /// `inventory_restored` was unparsable 100% of the time — confirmed in a real playtest
        /// log, not intermittent. Rewritten to match the established `object[]` + `IPCParse`
        /// convention; `ToLong` never throws on a type mismatch (defaults to 0), same as every
        /// other `IPCParse` accessor, so the old try/catch is no longer needed.
        ///
        /// Public rather than internal so the EditMode suite can reach it directly — same reason
        /// documented on `IpcStreamReader`: `tools/dev/CompileCheckClient.sh` builds each
        /// assembly under a `_check` suffix, so an `InternalsVisibleTo` friend name never matches
        /// under the project's own compile-check gate.
        public static List<(int itemId, int quantity)> ParseStacks(object data)
        {
            if (data is not Dictionary<string, object> d ||
                !d.TryGetValue("items", out var rawItems) ||
                rawItems is not object[] list)
                return null;

            var stacks = new List<(int, int)>(list.Length);
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] is not Dictionary<string, object> m)
                    continue;

                int id = (int)IPCParse.ToLong(IPCParse.Get(m, "item_id"));
                int qty = (int)IPCParse.ToLong(IPCParse.Get(m, "quantity"));
                if (id != 0 && qty > 0)
                    stacks.Add((id, qty));
            }
            return stacks;
        }

        private void Apply(List<(int itemId, int quantity)> stacks)
        {
            var inventory = _character.Inventory;
            var containers = inventory.Containers;

            // Clear first (STP-native, mirrors DeathLootReporter.OnRespawn) — idempotent apply.
            for (int i = 0; i < containers.Count; i++)
                containers[i]?.Clear();

            int added = 0, rejected = 0;
            for (int i = 0; i < stacks.Count; i++)
            {
                var (addedCount, rejectReason) = inventory.AddItemsById(stacks[i].itemId, stacks[i].quantity);
                if (addedCount >= stacks[i].quantity)
                {
                    added++;
                }
                else
                {
                    rejected++;
                    Debug.LogWarning($"[InventoryRestorer] stack item_id={stacks[i].itemId} x{stacks[i].quantity} only added {addedCount} ({rejectReason ?? "no reason"})");
                }
            }

            Debug.Log($"[InventoryRestorer] inventory restored: {added} stacks added, {rejected} partial/rejected");
        }

        // Same local-character resolve as DeathLootReporter/InventoryReporter (skip remote
        // avatars; destroyed motor reads as null → re-resolve).
        private void ResolveCharacter()
        {
            if (_motor != null)
                return;

            var m = LocalPlayerLocator.Find<CharacterControllerMotor>();
            if (m == null)
            {
                _motor = null;
                _character = null;
                return;
            }

            _motor = m;
            _character = m.GetComponentInParent<ICharacter>();
        }

        private void OnDestroy()
        {
            if (_instance != this)
                return;

            if (_ipc != null)
                _ipc.RemoveEventListener(OnGameEvent);
            _instance = null;
        }
    }
}
