using PolymindGames.InventorySystem;
using PolymindGames.UserInterface;
using UnityEngine.InputSystem;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/Item Action Input")]
    public class ItemActionInput : PlayerUIInputBehaviour
    {
        [SerializeField, Title("Settings")]
        private InputActionReference _inputAction;

        [SerializeField]
        private ItemAction _itemAction;

        private void OnEnable() => _inputAction.RegisterPerformed(OnAction);
        private void OnDisable() => _inputAction.UnregisterPerformed(OnAction);

        private void OnAction(InputAction.CallbackContext context)
        {
            if (ItemDragger.Instance.IsDragging)
                return;

            var raycastObject = RaycastManagerUI.Instance.RaycastAtCursorPosition();
            if (raycastObject != null && raycastObject.TryGetComponent<ItemSlotUIBase>(out var slotUI) && slotUI.HasItem)
                _itemAction.Perform(Character, slotUI.Slot, slotUI.Slot.GetStack());
        }
    }
}
