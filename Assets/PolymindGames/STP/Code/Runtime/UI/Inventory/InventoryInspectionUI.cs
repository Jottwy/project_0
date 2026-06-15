using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Manages the inventory UI during character inspection, handling attachment and detachment of the inventory UI when inspection starts and ends.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InventoryInspectionUI : CharacterUIBehaviour
    {
        [SerializeField, NotNull]
        [Tooltip("The inventory UI component to attach during inspection.")]
        private InventoryUI _inventoryUI;

        [SerializeField, Range(0f, 1f)]
        [Tooltip("Delay time before detaching the inventory UI after inspection ends.")]
        private float _detachInventoryDelay = 0.25f;

        [SerializeField, SpaceArea]
        [Tooltip("Event invoked when inventory inspection starts.")]
        private UnityEvent _inspectionStarted;

        [SerializeField]
        [Tooltip("Event invoked when inventory inspection ends.")]
        private UnityEvent _inspectionStopped;

        /// <summary>
        /// Handles actions when a character is attached to this UI, such as setting up inspection event listeners.
        /// </summary>
        /// <param name="character">The character being attached.</param>
        protected override void OnCharacterAttached(ICharacter character)
        {
            // Attach the inventory UI to the character's inventory
            _inventoryUI.AttachToInventory(character.Inventory);

            // Get the inspection manager and subscribe to events
            var inspectionManager = character.GetCC<IInventoryInspectionManagerCC>();
            inspectionManager.InspectionStarted += OnInspectionStarted;
            inspectionManager.InspectionEnded += OnInspectionEnded;

            // If inspection is already started, trigger the event
            if (inspectionManager.IsInspecting)
                OnInspectionStarted();
        }

        /// <summary>
        /// Handles actions when a character is detached from this UI, such as cleaning up event listeners.
        /// </summary>
        /// <param name="character">The character being detached.</param>
        protected override void OnCharacterDetached(ICharacter character)
        {
            // Detach the inventory UI from the character
            _inventoryUI.DetachFromInventory();

            // Get the inspection manager and unsubscribe from events
            var inspectionManager = character.GetCC<IInventoryInspectionManagerCC>();
            inspectionManager.InspectionStarted -= OnInspectionStarted;
            inspectionManager.InspectionEnded -= OnInspectionEnded;

            // If inspection is still active, end it
            if (inspectionManager.IsInspecting)
                OnInspectionEnded();
        }

        /// <summary>
        /// Invoked when the inventory inspection starts, attaches non-persistent containers and invokes the inspection started event.
        /// </summary>
        private void OnInspectionStarted()
        {
            _inventoryUI.AttachNonPersistentContainers();
            _inspectionStarted.Invoke();
        }

        /// <summary>
        /// Invoked when the inventory inspection ends, detaches non-persistent containers after a delay and invokes the inspection stopped event.
        /// </summary>
        private void OnInspectionEnded()
        {
            // Delay detachment of non-persistent containers
            CoroutineUtility.InvokeDelayedSafe(this, _inventoryUI.DetachNonPersistentContainers, _detachInventoryDelay);
            _inspectionStopped.Invoke();
        }

        #region Editor
#if UNITY_EDITOR
        /// <summary>
        /// Ensures the default container UI is set if available.
        /// </summary>
        private void OnValidate()
        {
            if (_inventoryUI == null)
                _inventoryUI = GetComponent<InventoryUI>();
        }
#endif
        #endregion
    }

}