using PolymindGames.UserInterface;
using UnityEngine.InputSystem;
using UnityEngine;

namespace PolymindGames.InputSystem.Behaviours
{
    [AddComponentMenu("Input/Item Drag Input")]
    public class ItemDragInput : PlayerUIInputBehaviour
    {
        [SerializeField, NotNull]
        private ItemDragger _dragHandler;

        [SerializeField, NotNull, Title("Actions")]
        private InputActionReference _leftClickAction;

        [SerializeField, NotNull]
        private InputActionReference _splitItemStackAction;
        
        private Vector2 _pointerPositionLastFrame;
        private ItemSlotUIBase _dragStartSlot;
        private bool _pointerMovedLastFrame;
        private bool _isDragging;

        #region Initialization
        private void OnEnable()
        {
            _leftClickAction.action.Enable();
            _splitItemStackAction.EnableAction();
        }

        private void OnDisable()
        {
            _splitItemStackAction.DisableAction();

            _dragHandler.CancelDrag(_dragStartSlot);
            
            _dragStartSlot = null;
            _isDragging = false;
        }

        #endregion

        #region Input Handling
        private void Update()
        {
            Vector2 pointerPosition = RaycastManagerUI.Instance.GetCursorPosition();
            bool pointerMovedThisFrame = (pointerPosition - _pointerPositionLastFrame).sqrMagnitude > 0.0001f;
            _pointerPositionLastFrame = pointerPosition;
            UpdateDragging(pointerPosition, pointerMovedThisFrame, _pointerMovedLastFrame);
            _pointerMovedLastFrame = pointerMovedThisFrame;
        }

        private void UpdateDragging(Vector2 pointerPosition, bool pointerMovedThisFrame, bool pointerMovedLastFrame)
        {
            if (!_isDragging)
            {
                if (_leftClickAction.action.ReadValue<float>() > 0.1f && pointerMovedThisFrame && pointerMovedLastFrame)
                {
                    _dragStartSlot = GetSlotRaycast(out _);
                    _isDragging = true;

                    if (_dragStartSlot != null)
                    {
                        bool splitItemStack = _splitItemStackAction.action.ReadValue<float>() > 0.01f;
                        _dragHandler.OnDragStart(_dragStartSlot, pointerPosition, splitItemStack);
                    }
                }
            }
            else
            {
                _dragHandler.OnDrag(pointerPosition);

                if (_leftClickAction.action.WasReleasedThisFrame())
                {
                    if (_dragStartSlot != null)
                    {
                        var raycastedSlot = GetSlotRaycast(out var raycastedObject);
                        _dragHandler.OnDragEnd(_dragStartSlot, raycastedSlot, raycastedObject);
                    }

                    _isDragging = false;
                }
            }
        }

        private static ItemSlotUIBase GetSlotRaycast(out GameObject raycastedObject)
        {
            raycastedObject = RaycastManagerUI.Instance.RaycastAtCursorPosition();
            return raycastedObject != null ? raycastedObject.GetComponent<ItemSlotUIBase>() : null;
        }
        #endregion

        #region Editor
#if UNITY_EDITOR
        private void Reset()
        {
            _dragHandler = GetComponent<ItemDragger>();
            if (_dragHandler == null)
                _dragHandler = gameObject.AddComponent<ItemDragHandler>();
        }
#endif
        #endregion
    }
}
