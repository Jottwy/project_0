using System.Collections.Generic;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.MovementSystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-028 Fase B — reports the local player's death-loot snapshot to the authoritative
    /// backend, which spawns the corpse at the server-frozen death position.
    ///
    /// Trigger: IHealthManager.Death of the LOCAL character. This single event covers BOTH
    /// death paths — a real local death (fall: ReceiveDamage fires DamageReceived, whose
    /// report_damage kills the server-side health FIRST on the same ordered TCP stream, then
    /// Death) and a server-side death (starvation/entities: RespawnRequester.ForceNativeDeathEdge
    /// forces the native alive→0 crossing, which fires this same Death event; the server is
    /// already dead by then). Either way the report arrives while the server's is_dead gate is
    /// open. The server dedupes per death (death_loot_reported flag), so the client sends
    /// unconditionally on every Death edge — no local latch needed beyond the event itself.
    ///
    /// Snapshot contents (decision 2026-07-03: EVERYTHING falls to the corpse):
    ///   - items: every stack of every container of the STP inventory (backpack, hotbar,
    ///     equipment containers AND the wieldable holster — clothing and weapons are lootable
    ///     stacks like everything else). Raw STP item ids (DataIdReference — may be negative).
    ///   - equipment[4] + heldItem: the cosmetic snapshot (same read patterns as
    ///     PlayerPoseTransmitter, ADR-022/023) that will dress the ragdoll in Fase C. These
    ///     DUPLICATE information already inside items — intentionally: items is the loot
    ///     ledger, equipment/heldItem is presentation.
    ///
    /// Rule #3: the death side only READS and REPORTS — it never clears anything there (a
    /// cadaver-emptying fix, 2026-07-03, closes the loop on the RESPAWN side instead — see
    /// OnRespawn below). Resolve patterns mirror RespawnRequester/PlayerPoseTransmitter (live
    /// re-resolve on rig rebuild; destroyed motor reads as null). Self-bootstraps; removable.
    ///
    /// Contract-integrity fix (2026-07-03): ADR-028 decision 2 says the player "respawnea
    /// desnudo y sin nada" — Fase B only reported the snapshot; nothing actually emptied the
    /// local inventory, so a looter (including the owner looting their own corpse) would find
    /// duplicated items. Fixed by clearing every container on IHealthManager.Respawn (the SAME
    /// native event ADR-025/027's pipeline already uses for the health/hunger/thirst reset —
    /// not a new hook). Ordering is safe by construction, not by timing: Respawn cannot fire
    /// before Death for the same life (STP's own state machine — Death always precedes
    /// Respawn), and the snapshot report happens synchronously inside OnDeath, so by the time
    /// ANY Respawn subscriber runs the server has already recorded the corpse. Clearing here
    /// can never race ahead of the report.
    /// </summary>
    public sealed class DeathLootReporter : MonoBehaviour
    {
        private static DeathLootReporter _instance;

        private CharacterControllerMotor _motor;
        private ICharacter _character;
        private IHealthManager _health;

        // Reusable buffers — the snapshot is built at most once per death.
        private readonly int[] _equipment = new int[4];
        private readonly List<CorpseLootStack> _items = new List<CorpseLootStack>(32);

        // Contract-integrity fix (2026-07-03, found in Fase D play-test): held_item always
        // reported 0 even with a wieldable visibly equipped. Root cause: WieldableInventory.OnDeath
        // ALSO subscribes to IHealthManager.Death and unconditionally calls SelectAtIndex(-1) —
        // C# multicast events invoke subscribers in subscription order, and WieldableInventory
        // subscribes during the character's own OnBehaviourStart (early), while this component
        // subscribes only once its polling Update() finds the character (later) — so by the time
        // OnDeath below runs, SelectedIndex is already -1. Fix: read the held item EVERY frame
        // (mirrors PlayerPoseTransmitter's per-pose read, ADR-023) and use the last value cached
        // BEFORE death instead of a fresh read racing WieldableInventory's reset.
        private int _cachedHeldItem;

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

            var go = new GameObject("[DeathLootReporter]");
            _instance = go.AddComponent<DeathLootReporter>();
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
            ResolveAndSubscribe();
            if (_character != null)
                _cachedHeldItem = ReadHeldItem();
        }

        // Fired on the native Death edge (local damage or the forced server-side bridge). Death
        // subscribers run synchronously in SUBSCRIPTION order — WieldableInventory's OnDeath (a
        // DIFFERENT subscriber to this same event) unconditionally resets SelectedIndex to -1, and
        // typically runs BEFORE this one (see _cachedHeldItem doc) — inventory CONTAINER contents
        // are untouched by any known Death subscriber, so BuildSnapshot's fresh container read is
        // still safe here, but held_item is NOT (hence the cache).
        private void OnDeath(in DamageArgs args)
        {
            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
            {
                Debug.LogWarning("[DeathLootReporter] Death edge but IPC missing/disconnected → report_death_loot NOT sent (corpse will be empty)");
                return;
            }

            BuildSnapshot();
            ipc.SendReportDeathLoot(_equipment, _cachedHeldItem, _items);
            Debug.Log($"[DeathLootReporter] report_death_loot sent: {_items.Count} stacks, equipment=[{_equipment[0]},{_equipment[1]},{_equipment[2]},{_equipment[3]}]");
        }

        // Contract-integrity fix (2026-07-03): the corpse now holds the snapshot (reported
        // synchronously above, well before this can fire — see class doc) — empty every
        // container so the player actually "respawnea desnudo y sin nada" (ADR-028 decision 2).
        // Clear() is STP-native (IItemContainer.Clear, no game_loop-derived removal logic).
        private void OnRespawn()
        {
            var containers = _character?.Inventory?.Containers;
            if (containers == null)
                return;

            for (int i = 0; i < containers.Count; i++)
                containers[i]?.Clear();

            Debug.Log($"[DeathLootReporter] local inventory cleared on respawn ({containers.Count} containers)");
        }

        /// <summary>
        /// Fill _equipment (protocol order [Head, Torso, Legs, Feet], ADR-022) and _items
        /// (every non-empty slot of every inventory container). Containers are enumerated
        /// fresh at the death edge — no caching needed for a once-per-death read.
        /// </summary>
        private void BuildSnapshot()
        {
            System.Array.Clear(_equipment, 0, 4);
            _items.Clear();

            var inventory = _character?.Inventory;
            if (inventory == null)
                return;

            _equipment[0] = FirstSlotItemId(inventory, ItemConstants.HeadEquipmentTag);
            _equipment[1] = FirstSlotItemId(inventory, ItemConstants.TorsoEquipmentTag);
            _equipment[2] = FirstSlotItemId(inventory, ItemConstants.LegsEquipmentTag);
            _equipment[3] = FirstSlotItemId(inventory, ItemConstants.FeetEquipmentTag);

            var containers = inventory.Containers;
            for (int c = 0; c < containers.Count; c++)
            {
                var container = containers[c];
                if (container == null)
                    continue;

                for (int s = 0; s < container.SlotsCount; s++)
                {
                    var stack = container.GetItemAtIndex(s);
                    int id = stack.Item?.Id ?? 0;
                    if (id == 0 || stack.Count <= 0)
                        continue;

                    _items.Add(new CorpseLootStack { itemId = id, quantity = stack.Count });
                }
            }
        }

        private static int FirstSlotItemId(IInventory inventory, DataIdReference<ItemTagDefinition> tag)
        {
            var container = inventory.FindContainer(ItemContainerFilters.WithTag(tag));
            if (container == null || container.SlotsCount == 0)
                return 0;
            return container.GetItemAtIndex(0).Item?.Id ?? 0;
        }

        // ADR-023 read pattern: holster container + selected wieldable index (0 = empty hands).
        private int ReadHeldItem()
        {
            var wieldableInv = _character?.GetCC<IWieldableInventoryCC>();
            var holster = _character?.Inventory?.FindContainer(
                ItemContainerFilters.WithTag(ItemConstants.WieldableTag));
            if (wieldableInv == null || holster == null)
                return 0;

            int index = wieldableInv.SelectedIndex;
            if (index < 0 || index >= holster.SlotsCount)
                return 0;
            return holster.GetItemAtIndex(index).Item?.Id ?? 0;
        }

        /// <summary>
        /// Resolve the LOCAL character's health manager and subscribe to Death (report) and
        /// Respawn (clear). Unity's overloaded == makes a destroyed motor read as null → rig
        /// rebuild forces a clean re-find + re-subscribe (same pattern as RespawnRequester).
        /// </summary>
        private void ResolveAndSubscribe()
        {
            if (_motor != null)
                return;

            Unsubscribe();

            var motors = FindObjectsByType<CharacterControllerMotor>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            for (int i = 0; i < motors.Length; i++)
            {
                var m = motors[i];
                if (m.GetComponentInParent<RemotePlayerManager>() != null)
                    continue; // remote avatar, not the local player

                _motor = m;
                _character = m.GetComponentInParent<ICharacter>();
                _health = _character?.HealthManager;
                if (_health != null)
                {
                    _health.Death += OnDeath;
                    _health.Respawn += OnRespawn;
                }
                else
                    Debug.LogWarning($"[DeathLootReporter] motor {m.GetInstanceID()} found but character/HealthManager is NULL — death-loot report inactive until rig rebuild");
                return;
            }

            _motor = null;
            _character = null;
        }

        private void Unsubscribe()
        {
            if (_health != null)
            {
                _health.Death -= OnDeath;
                _health.Respawn -= OnRespawn;
            }
            _health = null;
        }

        private void OnDestroy()
        {
            if (_instance != this)
                return;

            Unsubscribe();
            _instance = null;
        }
    }
}
