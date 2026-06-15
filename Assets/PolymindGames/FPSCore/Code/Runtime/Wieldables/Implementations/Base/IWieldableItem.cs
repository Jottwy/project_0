using PolymindGames.InventorySystem;
using UnityEngine.Events;

namespace PolymindGames.WieldableSystem
{
    public interface IWieldableItem
    {
        SlotReference Slot { get; }
        IWieldable Wieldable { get; }

        event UnityAction<SlotReference> AttachedSlotChanged;

        void AttachToSlot(SlotReference slot);
    }
}