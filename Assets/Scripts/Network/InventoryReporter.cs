using System.Collections.Generic;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.MovementSystem;
using UnityEngine;

namespace BackroomsSurvival.Net
{
    /// <summary>
    /// ADR-032 amendment — debounced on-change reporter of the LOCAL player's REAL STP inventory,
    /// so world persistence can save it (the legacy Rust inventory is disconnected from the game;
    /// the backend's only other view of the real inventory is the death edge, ADR-028).
    ///
    /// Enumeration mirrors <see cref="DeathLootReporter.BuildSnapshot"/> exactly: every non-empty
    /// stack of EVERY container of the STP inventory (backpack, hotbar, equipment containers and
    /// the wieldable holster). Raw STP item ids (DataIdReference — may be negative).
    ///
    /// Trigger: <c>IItemContainer.SlotChanged</c> of every container (the same event ADR-022's
    /// clothing sync reads), DEBOUNCED — a burst of changes (crafting, looting a corpse, the
    /// respawn container-clear) coalesces into one report ~1 s after the burst quiets down, with
    /// a 3 s max-latency flush so a steady drip of changes can never starve the report forever.
    ///
    /// Rule #3: read-and-report only — never mutates the inventory. Trust-the-client, same level
    /// as report_death_loot/consume_item (no authoritative inventory exists to verify against);
    /// the backend applies the shared corpse hygiene (quantity>0, cap 64) on receipt.
    ///
    /// Death coherence is free by construction: DeathLootReporter.OnRespawn clears every
    /// container → those Clears fire SlotChanged → this reporter sends the now-empty snapshot →
    /// the next save persists "naked" while the corpse (already persisted via world.corpses)
    /// holds the loot. Resolve pattern mirrors DeathLootReporter (re-resolve on rig rebuild).
    /// Self-bootstraps; removable.
    /// </summary>
    public sealed class InventoryReporter : MonoBehaviour
    {
        // Quiet period after the LAST change before a report is sent.
        private const float DebounceSeconds = 1f;
        // Upper bound since the FIRST unreported change — a continuous stream of changes
        // (each resetting the quiet period) still flushes at this cadence.
        private const float MaxLatencySeconds = 3f;

        private static InventoryReporter _instance;

        private CharacterControllerMotor _motor;
        private ICharacter _character;
        private readonly List<IItemContainer> _subscribed = new List<IItemContainer>(8);

        private bool _dirty;
        private float _firstDirtyAt;
        private float _lastChangeAt;

        // Reusable buffer — snapshots are small (≤64 stacks server-side).
        private readonly List<CorpseLootStack> _items = new List<CorpseLootStack>(32);

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

            var go = new GameObject("[InventoryReporter]");
            _instance = go.AddComponent<InventoryReporter>();
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

            if (!_dirty)
                return;

            float now = Time.unscaledTime;
            if (now - _lastChangeAt < DebounceSeconds && now - _firstDirtyAt < MaxLatencySeconds)
                return;

            _dirty = false;
            SendReport();
        }

        private void OnSlotChanged(in SlotReference slot, SlotChangeType changeType)
        {
            float now = Time.unscaledTime;
            if (!_dirty)
                _firstDirtyAt = now;
            _dirty = true;
            _lastChangeAt = now;
        }

        private void SendReport()
        {
            if (!IPCClient.TryGetInstance(out var ipc) || !ipc.IsConnected)
                return; // nothing to do; the next change re-arms

            BuildSnapshot();
            ipc.SendReportInventory(_items);
        }

        /// <summary>Every non-empty stack of every container — DeathLootReporter's items pass.</summary>
        private void BuildSnapshot()
        {
            _items.Clear();

            var containers = _character?.Inventory?.Containers;
            if (containers == null)
                return;

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

        // Mirror of DeathLootReporter.ResolveAndSubscribe: destroyed motor reads as null →
        // rig rebuild forces a clean re-find + re-subscribe of every container's SlotChanged.
        private void ResolveAndSubscribe()
        {
            if (_motor != null)
                return;

            Unsubscribe();

            var m = LocalPlayerLocator.Find<CharacterControllerMotor>();
            if (m == null)
            {
                _motor = null;
                _character = null;
                return;
            }

            _motor = m;
            _character = m.GetComponentInParent<ICharacter>();

            var containers = _character?.Inventory?.Containers;
            if (containers == null)
            {
                Debug.LogWarning($"[InventoryReporter] motor {m.GetInstanceID()} found but inventory is NULL — inventory report inactive until rig rebuild");
                return;
            }

            for (int c = 0; c < containers.Count; c++)
            {
                var container = containers[c];
                if (container == null)
                    continue;
                container.SlotChanged += OnSlotChanged;
                _subscribed.Add(container);
            }

            // Baseline report so the backend knows the starting inventory without
            // waiting for the first change (arms the debounce like a change would).
            OnSlotChanged(default, default);
        }

        private void Unsubscribe()
        {
            for (int i = 0; i < _subscribed.Count; i++)
            {
                if (_subscribed[i] != null)
                    _subscribed[i].SlotChanged -= OnSlotChanged;
            }
            _subscribed.Clear();
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
