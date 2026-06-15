using PolymindGames.UserInterface;
using UnityEngine.InputSystem;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [RequireComponent(typeof(ItemWheelUI))]
    [AddComponentMenu("Input/Item Wheel Input")]
    public sealed class ItemWheelUIInput : InputBehaviour
    {
        [SerializeField, NotNull, Title("Actions")]
        private InputActionReference _itemWheelAction;
        
        [SerializeField, NotNull]
        private InputActionReference _cursorDeltaAction;

        private ItemWheelUI _itemWheel;
        
        #region Initialization
        private void OnEnable()
        {
            _cursorDeltaAction.action.Enable();
            _itemWheelAction.RegisterPerformed(OnWheelOpen);
            _itemWheelAction.RegisterCanceled(OnWheelCloseInput);
            _itemWheel = GetComponent<ItemWheelUI>();
        }

        private void OnDisable()
        {
            _itemWheelAction.UnregisterPerformed(OnWheelOpen);
            _itemWheelAction.UnregisterCanceled(OnWheelCloseInput);
        }
        #endregion
        
        #region Input handling
        private void Update()
        {
            if (!_itemWheel.IsVisible)
                return;
            
            var cursorDelta = _cursorDeltaAction.action.ReadValue<Vector2>().normalized;
            _itemWheel.RadialMenu.UpdateHighlightedSelection(cursorDelta);
        }

        private void OnWheelOpen(InputAction.CallbackContext ctx) => _itemWheel.StartWheelInspection();
        private void OnWheelCloseInput(InputAction.CallbackContext ctx) => _itemWheel.StopWheelInspection();
        #endregion
    }
}