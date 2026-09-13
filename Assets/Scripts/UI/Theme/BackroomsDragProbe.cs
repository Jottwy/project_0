using System.Collections.Generic;
using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;

namespace BackroomsSurvival.UI
{
    /// <summary>
    /// La pila que se está arrastrando. El vendor la guarda en privado, pero su copia visual es un hueco con un
    /// contenedor propio de un hueco, sin inventario, colgado del mismo padre que <see cref="ItemDragger"/>. Lo usan las
    /// secciones (abrirse al arrastrar) y la respuesta al soltar.
    /// </summary>
    public static class BackroomsDragProbe
    {
        public static bool TryGetDraggedStack(List<ItemSlotUIBase> scan, out ItemStack stack)
        {
            stack = ItemStack.Null;
            if (!ItemDragger.HasInstance || !ItemDragger.Instance.IsDragging) return false;
            var parent = ItemDragger.Instance.transform.parent;
            if (parent == null) return false;
            parent.GetComponentsInChildren(false, scan);
            foreach (var slot in scan)
            {
                if (!slot.HasItem || slot.Slot.Container == null || slot.Slot.Container.Inventory != null) continue;
                stack = slot.Slot.GetStack();
                return true;
            }
            return false;
        }
    }
}
