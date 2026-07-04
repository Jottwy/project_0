using PolymindGames.InventorySystem;

namespace BackroomsSurvival.Migration.STPIntegration
{
    /// <summary>
    /// ADR-028 Fase D fix (2026-07-03) — a corpse is LOOT-ONLY by explicit scope decision: items
    /// come out, nothing goes back in. StorageStationUI's drag-and-drop is bidirectional by design
    /// (correct for real storage crates), so without a restriction a player could drag an item INTO
    /// a corpse's container. That silently corrupts CorpseLootSync's local-slot → server-Vec-index
    /// mirror (a deposit lands in a slot the mirror considers empty; a LATER take of that same local
    /// slot then sends the WRONG server index and the wrong item is removed server-side). Blocking
    /// the write at the container level (STP's own ContainerRestriction, checked by
    /// GetAllowedCount BEFORE any write lands) makes the drag visibly fail in the vendor UI itself
    /// — never applied-then-reverted.
    ///
    /// SEALABLE, not unconditional (bug found in play-test, 2026-07-03): ItemContainer routes its
    /// OWN SetItemAtIndex through GetAllowedCount too, so an always-reject restriction blocked
    /// CorpseSpawner's initial population — the loot panel opened EMPTY (every initial set silently
    /// placed nothing). The restriction starts UNSEALED (allow everything) while BuildLootContainer
    /// populates the slots, then seals. CorpseLootSync also unseals briefly during a rejection
    /// ROLLBACK (re-inserting the item the server refused to hand over is a legitimate write to the
    /// corpse container) and reseals right after. One instance PER CORPSE — sealing state is
    /// per-container, so the previous shared-singleton approach is gone.
    /// </summary>
    public sealed class RejectAllRestriction : ContainerRestriction
    {
        /// <summary>False during initial population and rollback re-insertion; true otherwise.</summary>
        public bool Sealed { get; set; }

        public static RejectAllRestriction CreateUnsealed()
        {
            var restriction = CreateInstance<RejectAllRestriction>();
            // RejectionReason's setter is `protected` — accessible here since this class
            // subclasses ContainerRestriction (the intended extension point).
            restriction.RejectionReason = "Corpses cannot be given items";
            restriction.Sealed = false;
            return restriction;
        }

        public override int GetAllowedCount(IItemContainer container, Item item, int requestedCount)
            => Sealed ? 0 : requestedCount;
    }
}
