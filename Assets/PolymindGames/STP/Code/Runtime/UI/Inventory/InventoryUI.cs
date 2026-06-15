using PolymindGames.InventorySystem;
using UnityEngine.Events;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Manages the inventory UI, attaching and detaching inventory containers based on the current inventory state.
    /// </summary>
    public sealed class InventoryUI : MonoBehaviour
    {
        [SerializeField, ReorderableList(HasLabels = false)]
        [Tooltip("Persistent containers that should always be attached to the inventory UI.")]
        private ItemContainerUI[] _persistentContainers;
    
        [SpaceArea]
        [SerializeField, ReorderableList(HasLabels = false)]
        [Tooltip("Non-persistent containers that are only attached during inspection or other temporary actions.")]
        private ItemContainerUI[] _nonPersistentContainers;
    
        private IInventory _inventory;
    
        /// <summary>
        /// The current inventory attached to this UI.
        /// </summary>
        public IInventory Inventory => _inventory;
    
        /// <summary>
        /// Event triggered when the attached inventory changes.
        /// </summary>
        public event UnityAction<IInventory> AttachedInventoryChanged;
    
        /// <summary>
        /// Attaches the UI to a given inventory, setting up persistent containers and triggering the inventory change event.
        /// </summary>
        /// <param name="newInventory">The inventory to attach.</param>
        public void AttachToInventory(IInventory newInventory)
        {
            if (_inventory == newInventory)
                return;
    
            DetachFromInventory();
    
            _inventory = newInventory;
    
            // Attach persistent containers immediately
            foreach (var containerUI in _persistentContainers)
            {
                var container = _inventory.FindContainer(ItemContainerFilters.WithName(containerUI.ContainerName));
                containerUI.AttachToContainer(container);
            }
    
            // Invoke the event to notify listeners
            AttachedInventoryChanged?.Invoke(_inventory);
        }
    
        /// <summary>
        /// Detaches the UI from the current inventory, clearing all attached containers.
        /// </summary>
        public void DetachFromInventory()
        {
            if (_inventory == null)
                return;
    
            // Detach all persistent containers
            foreach (var containerUI in _persistentContainers)
            {
                containerUI.DetachFromContainer();
            }
    
            // Detach all non-persistent containers
            foreach (var containerUI in _nonPersistentContainers)
            {
                containerUI.DetachFromContainer();
            }
    
            _inventory = null;
        }
    
        /// <summary>
        /// Attaches non-persistent containers to the current inventory.
        /// </summary>
        public void AttachNonPersistentContainers()
        {
            foreach (var containerUI in _nonPersistentContainers)
            {
                var container = _inventory.FindContainer(ItemContainerFilters.WithName(containerUI.ContainerName));
                containerUI.AttachToContainer(container);
            }
        }
    
        /// <summary>
        /// Detaches all non-persistent containers from the current inventory.
        /// </summary>
        public void DetachNonPersistentContainers()
        {
            foreach (var containerUI in _nonPersistentContainers)
            {
                containerUI.DetachFromContainer();
            }
        }
    }
}