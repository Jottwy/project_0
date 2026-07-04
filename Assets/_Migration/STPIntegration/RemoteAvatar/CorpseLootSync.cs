using System;
using System.Collections.Generic;
using BackroomsSurvival.Net;
using PolymindGames;
using PolymindGames.InventorySystem;
using PolymindGames.MovementSystem;
using UnityEngine;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-028 Fase D — reports item removals from a corpse's local <see cref="ItemContainer"/> to
    /// the authoritative backend (<c>take_corpse_item</c>, Fase A) and reconciles against the
    /// server's verdict (<c>corpse_item_taken</c> / <c>corpse_take_rejected</c>).
    ///
    /// TRUST MODEL (fix #1, 2026-07-03 — found in Fase D play-test): the actual item MOVE happens
    /// via STP's native <c>ItemTransfering.TransferItemToInventory</c> (StorageStationUI's drag /
    /// "Take All"), which does <c>inventory.AddItem(stack)</c> on the DESTINATION FIRST, then
    /// <c>slot.AdjustStack(-addedCount)</c> on the SOURCE (this corpse's container) — see
    /// ItemTransfering.cs. That ADD-before-REMOVE order means a request-gate that blocks the local
    /// move until server confirmation (the ADR-011/014 world-item pickup pattern — ask first,
    /// apply on grant) is NOT reachable without either forking STP's transfer call or vetoing the
    /// UI gesture before it starts (deeper vendor UI surgery, out of scope for this pass). Instead
    /// this mirrors ADR-009's OTHER established pattern — client predicts, server reconciles: the
    /// local move is applied immediately (as STP already does natively, unavoidably), reported to
    /// the server, and on a `corpse_take_rejected` verdict this component ROLLS IT BACK exactly —
    /// re-inserts the item into the corpse container AND removes the erroneously-added copy from
    /// the looter's own inventory. The "server manda, cliente refleja" invariant holds on the
    /// SETTLED state (a rejection never leaves a permanent duplicate or a permanently-vanished
    /// corpse item), at the cost of a brief (same-process IPC, sub-frame in practice) optimistic
    /// window instead of true zero-flicker.
    ///
    /// INDEX MIRRORING: the backend stores a corpse's loot as <c>Vec&lt;CorpseStack&gt;</c> and does
    /// <c>Vec::remove(item_index)</c> when a stack fully depletes — every LATER entry's index shifts
    /// down by one. The local <see cref="ItemContainer"/> is a FIXED-SIZE slot array that never
    /// shifts. This component keeps a parallel <c>_serverIndex</c> map (local slot → current server
    /// Vec index). Because of the rollback requirement above, the shift is no longer applied eagerly
    /// at the moment of local removal (a rejected take must NOT have shifted anything — the server
    /// never actually removed that Vec entry) — it is deferred until `corpse_item_taken` CONFIRMS
    /// the removal really happened, tracked per-request in `_pending`. A PARTIAL removal (count
    /// reduced but not to zero) never reindexes on the server either way.
    ///
    /// KNOWN LIMITATION (v1, accepted): depositing an item INTO the corpse container (StorageStationUI
    /// allows dragging either direction) is NOT reported to the server — the corpse is a one-way loot
    /// source by design intent, and the server has no code path for "add to corpse" regardless.
    ///
    /// ADR-028 Fase E2 — CONTENT RECONCILE (remote loot): when ANOTHER peer loots this corpse, the
    /// change arrives only through the mirrored roster (CorpseView.items in each WorldState) — no
    /// event fires here, and worse, the server Vec SHIFTED without this client's knowledge, so the
    /// positional `_serverIndex` mirror is stale. <see cref="ReconcileFromServer"/> (called by
    /// CorpseSpawner each LateUpdate with the fresh roster view) therefore re-derives BOTH from
    /// ground truth by ORDER-PRESERVING item_id matching (valid because Vec::remove keeps relative
    /// order and local slots never reorder): each local slot claims the first unclaimed server entry
    /// with its item_id. A local slot with no claimable entry was fully looted remotely → cleared; a
    /// claimed entry with a lower quantity was partially looted remotely → reduced. Slots with OWN
    /// takes in flight are skipped entirely (the optimistic local state is ahead of the roster by
    /// design — reconciling them would resurrect the just-taken item), though they still claim their
    /// server entry so a duplicate-item_id slot can't steal it. Server-applied writes are made with
    /// reporting suppressed (they must never echo back as take requests) and never ADD items
    /// (a server entry with MORE than local means the roster is just stale after our own confirmed
    /// take — re-adding would duplicate).
    /// </summary>
    public sealed class CorpseLootSync : MonoBehaviour
    {
        private readonly struct PendingTake
        {
            public readonly int LocalSlot;
            public readonly int ItemId;
            public readonly int Quantity;
            public readonly bool WasFullDepletion;

            public PendingTake(int localSlot, int itemId, int quantity, bool wasFullDepletion)
            {
                LocalSlot = localSlot;
                ItemId = itemId;
                Quantity = quantity;
                WasFullDepletion = wasFullDepletion;
            }
        }

        private ItemContainer _container;
        private RejectAllRestriction _restriction;
        private uint _corpseId;
        private int[] _serverIndex;
        private int[] _lastKnownCount;
        private int[] _lastKnownItemId;

        /// <summary>Fires (itemId, quantity) once the server CONFIRMS a take — not on the
        /// optimistic local removal. CorpseSpawner.WireLoot subscribes to undress the ragdoll in
        /// real time when a looted item matches a worn equipment slot or the held item.</summary>
        public event Action<int, int> ItemTakenConfirmed;

        // Keyed by the server Vec index the request targeted (== _serverIndex[localSlot] AT THE
        // MOMENT the request was sent, before any shift). Case where multiple takes are in flight
        // at once (e.g. "Take All" fires every slot in one frame) is handled correctly because each
        // shift is deferred to ITS OWN confirmation, applied in confirmation-arrival order.
        private readonly Dictionary<int, PendingTake> _pending = new Dictionary<int, PendingTake>();

        private IPCClient _ipc;
        private ICharacter _looterCharacter;

        // E2: true while ReconcileFromServer applies server-side reductions — OnSlotChanged must
        // record the bookkeeping but NOT report them back as take requests (they'd echo forever).
        private bool _reconciling;
        // E2: scratch for the order-preserving match (sized on first reconcile, reused after).
        private bool[] _serverEntryClaimed;

        public void Initialize(uint corpseId, ItemContainer container, RejectAllRestriction restriction)
        {
            _corpseId = corpseId;
            _container = container;
            _restriction = restriction;

            int n = container.SlotsCount;
            _serverIndex = new int[n];
            _lastKnownCount = new int[n];
            _lastKnownItemId = new int[n];
            for (int i = 0; i < n; i++)
            {
                var stack = container.GetItemAtIndex(i);
                _serverIndex[i] = i;
                _lastKnownCount[i] = stack.Count;
                _lastKnownItemId[i] = stack.Item?.Id ?? 0;
            }

            container.SlotChanged += OnSlotChanged;
        }

        private void Update()
        {
            if (_ipc == null && IPCClient.TryGetInstance(out var ipc))
            {
                _ipc = ipc;
                _ipc.AddEventListener(OnGameEvent);
            }
        }

        private void OnSlotChanged(in SlotReference slot, SlotChangeType changeType)
        {
            int i = slot.Index;
            if (i < 0 || i >= _lastKnownCount.Length)
                return; // defensive: container resized unexpectedly, ignore

            int newCount = slot.GetCount();
            int newItemId = slot.HasItem() ? slot.GetItem().Id : 0;
            int oldCount = _lastKnownCount[i];
            int oldItemId = _lastKnownItemId[i];
            _lastKnownCount[i] = newCount;
            _lastKnownItemId[i] = newItemId;

            int delta = newCount - oldCount;
            if (delta >= 0)
                return; // deposit, no-op, or a rollback re-insertion — never reported

            if (_reconciling)
                return; // E2: server-applied reduction (remote loot) — bookkeeping only, no echo

            int removed = -delta;
            int serverIdx = _serverIndex[i];
            bool wasFullDepletion = newCount == 0;

            _pending[serverIdx] = new PendingTake(i, oldItemId, removed, wasFullDepletion);

            if (_ipc != null && _ipc.IsConnected)
                _ipc.SendTakeCorpseItem(_corpseId, serverIdx, removed);
            else
                Debug.LogWarning($"[CorpseLootSync] corpse {_corpseId}: IPC disconnected — take not reported, will never confirm (item stays optimistically removed).");

            // NOTE: the server-index shift is deferred to OnGameEvent (corpse_item_taken) — see
            // class doc. Applying it here would be wrong for a request that later gets rejected.
        }

        private void OnGameEvent(GameEventMsg ev)
        {
            if (ev == null)
                return;

            bool taken = ev.eventType == "corpse_item_taken";
            bool rejected = ev.eventType == "corpse_take_rejected";
            if (!taken && !rejected)
                return;

            var d = ev.data as Dictionary<string, object>;
            if (d == null)
                return;

            uint eventCorpseId = (uint)IPCParse.L(d, "corpse_id");
            if (eventCorpseId != _corpseId)
                return;

            int itemIndex = (int)IPCParse.L(d, "item_index");
            if (!_pending.TryGetValue(itemIndex, out var pending))
                return; // not ours (e.g. a stale/duplicate event) — ignore
            _pending.Remove(itemIndex);

            if (taken)
            {
                // Confirmed for real — NOW mirror the server's Vec::remove shift, if this request
                // was the one that fully depleted its slot.
                if (pending.WasFullDepletion)
                {
                    for (int j = 0; j < _serverIndex.Length; j++)
                    {
                        if (_serverIndex[j] > itemIndex)
                            _serverIndex[j]--;
                    }
                }
                // Fix (2026-07-03): let the appearance layer (CorpseSpawner.WireLoot) know a
                // CONFIRMED take happened, so a looted clothing/held item can undress the ragdoll
                // in real time instead of only at spawn.
                ItemTakenConfirmed?.Invoke(pending.ItemId, pending.Quantity);
                return;
            }

            // Rejected — roll back exactly: restore the corpse's local copy, undo the erroneous
            // add to the looter's own inventory. No server-index shift needed (nothing shifted).
            string reason = IPCParse.S(d, "reason");
            Debug.Log($"[CorpseLootSync] corpse {_corpseId}: take rejected (reason={reason}) — rolling back item_id={pending.ItemId} x{pending.Quantity}.");
            RollBack(pending);
        }

        /// <summary>
        /// ADR-028 Fase E2: apply the host's roster view of this corpse (remote peers' loot) to
        /// the local container, and re-derive the local-slot → server-Vec-index mirror from
        /// ground truth. Called by CorpseSpawner each LateUpdate. See class doc for the full
        /// rationale (order-preserving item_id matching, pending-slot guard, never-add rule).
        /// </summary>
        public void ReconcileFromServer(List<CorpseLootStack> serverItems)
        {
            if (_container == null || serverItems == null)
                return;

            int serverCount = serverItems.Count;
            if (_serverEntryClaimed == null || _serverEntryClaimed.Length < serverCount)
                _serverEntryClaimed = new bool[Mathf.Max(serverCount, 8)];
            for (int j = 0; j < _serverEntryClaimed.Length; j++)
                _serverEntryClaimed[j] = false;

            for (int i = 0; i < _serverIndex.Length; i++)
            {
                // OWN take in flight for this slot: the optimistic local state is deliberately
                // ahead of the (stale) roster — but still claim one matching server entry so a
                // later duplicate-item_id slot can't be matched against it.
                if (TryGetPendingForSlot(i, out var pendingItemId))
                {
                    ClaimFirst(serverItems, pendingItemId);
                    continue;
                }

                var localStack = _container.GetItemAtIndex(i);
                int localId = localStack.Item?.Id ?? 0;
                int localCount = localStack.Count;
                if (localId == 0 || localCount <= 0)
                {
                    _serverIndex[i] = -1; // emptied and settled — no server entry to track
                    continue;
                }

                int claimed = ClaimFirst(serverItems, localId);
                if (claimed < 0)
                {
                    // Fully looted by another peer → clear the slot (suppressed: never echo back).
                    _reconciling = true;
                    try { _container.SetItemAtIndex(i, ItemStack.Null); }
                    finally { _reconciling = false; }
                    _serverIndex[i] = -1;
                    ItemTakenConfirmed?.Invoke(localId, localCount);
                    Debug.Log($"[CorpseLootSync] corpse {_corpseId}: slot {i} (item_id={localId} x{localCount}) looted remotely — cleared.");
                    continue;
                }

                _serverIndex[i] = claimed;
                int serverQty = serverItems[claimed].quantity;
                if (serverQty < localCount)
                {
                    int removed = localCount - serverQty;
                    _reconciling = true;
                    try { _container.AdjustStackAtIndex(i, -removed); }
                    finally { _reconciling = false; }
                    ItemTakenConfirmed?.Invoke(localId, removed);
                    Debug.Log($"[CorpseLootSync] corpse {_corpseId}: slot {i} (item_id={localId}) reduced remotely by {removed}.");
                }
                // serverQty >= localCount: stale roster after our own confirmed take — never add.
            }
        }

        private bool TryGetPendingForSlot(int localSlot, out int itemId)
        {
            foreach (var kv in _pending)
            {
                if (kv.Value.LocalSlot == localSlot)
                {
                    itemId = kv.Value.ItemId;
                    return true;
                }
            }
            itemId = 0;
            return false;
        }

        /// <summary>First unclaimed server entry with this item_id, claimed; −1 when none.</summary>
        private int ClaimFirst(List<CorpseLootStack> serverItems, int itemId)
        {
            for (int j = 0; j < serverItems.Count; j++)
            {
                if (!_serverEntryClaimed[j] && serverItems[j].itemId == itemId)
                {
                    _serverEntryClaimed[j] = true;
                    return j;
                }
            }
            return -1;
        }

        private void RollBack(in PendingTake pending)
        {
            var def = DataDefinition<ItemDefinition>.GetWithId(pending.ItemId);
            if (def == null)
            {
                Debug.LogError($"[CorpseLootSync] corpse {_corpseId}: rollback failed, unknown item_id={pending.ItemId} — " +
                    "the item is now gone from BOTH the corpse and (potentially) the looter's inventory.");
                return;
            }

            // The loot-only seal also blocks OUR restore write — lift it just for this insert.
            if (_restriction != null)
                _restriction.Sealed = false;
            try
            {
                if (pending.WasFullDepletion)
                    _container.SetItemAtIndex(pending.LocalSlot, new ItemStack(new Item(def), pending.Quantity));
                else
                    _container.AdjustStackAtIndex(pending.LocalSlot, pending.Quantity);
            }
            finally
            {
                if (_restriction != null)
                    _restriction.Sealed = true;
            }

            var character = ResolveLooterCharacter();
            if (character != null)
                character.Inventory.RemoveItemsById(pending.ItemId, pending.Quantity);
            else
                Debug.LogError($"[CorpseLootSync] corpse {_corpseId}: rollback restored the corpse copy but could not resolve the " +
                    "local character to undo the inventory add — the item is now DUPLICATED.");
        }

        // Same resolve pattern as DeathLootReporter/RespawnRequester (exclude remote avatars).
        private ICharacter ResolveLooterCharacter()
        {
            if (_looterCharacter != null)
                return _looterCharacter;

            var motors = FindObjectsByType<CharacterControllerMotor>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < motors.Length; i++)
            {
                var m = motors[i];
                if (m.GetComponentInParent<BackroomsSurvival.Net.RemotePlayerManager>() != null)
                    continue;

                _looterCharacter = m.GetComponentInParent<ICharacter>();
                break;
            }
            return _looterCharacter;
        }

        private void OnDestroy()
        {
            if (_container != null)
                _container.SlotChanged -= OnSlotChanged;
            if (_ipc != null)
                _ipc.RemoveEventListener(OnGameEvent);
        }
    }
}
