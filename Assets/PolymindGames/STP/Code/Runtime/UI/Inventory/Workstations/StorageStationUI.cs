using PolymindGames.InventorySystem;
using System.Collections.Generic;
using UnityEngine.UI;
using UnityEngine;
using System;
using TMPro;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Manages the UI for the Storage Station, including the display and interaction with item containers.
    /// </summary>
    public sealed class StorageStationUI : WorkstationInspectorBaseUI<StorageStation>
    {
        [SerializeField, Range(1, MaxContainerCount)]
        [Tooltip("Initial number of containers displayed")]
        private int _initialContainerCount = 4;

        [SerializeField]
        [Tooltip("Template for creating new container UIs")]
        private StorageContainer _containerTemplate;

        private readonly List<StorageContainer> _storageContainers = new();
        private const int MaxContainerCount = 12;

        protected override void OnInspectionStarted(StorageStation workstation)
        {
            var containers = workstation.GetContainers();
            int count = containers.Count;

            if (count > MaxContainerCount)
            {
                Debug.LogError("Too many containers, consider lowering the count.", workstation);
                return;
            }

            EnsureVisibleContainersCount(count);
            for (int i = 0; i < count; i++)
            {
                _storageContainers[i].AttachToContainer(containers[i]);
            }
        }

        protected override void OnInspectionEnded(StorageStation workstation)
        {
            int count = workstation.GetContainers().Count;
            for (int i = 0; i < count; i++)
            {
                _storageContainers[i].DetachFromContainer();
            }
        }

        protected override void OnCharacterAttached(ICharacter character)
        {
            base.OnCharacterAttached(character);

            // Initialize storage containers with the template
            _storageContainers.Add(_containerTemplate);
            _containerTemplate.Initialize(Character.Inventory);
            for (int i = 0; i < _initialContainerCount; i++)
            {
                _storageContainers.Add(new StorageContainer(_containerTemplate));
            }
        }

        /// <summary>
        /// Ensures the UI has the required number of visible containers.
        /// </summary>
        /// <param name="count">The number of containers that need to be visible.</param>
        private void EnsureVisibleContainersCount(int count)
        {
            // Add containers if necessary
            while (_storageContainers.Count < count)
            {
                _storageContainers.Add(new StorageContainer(_containerTemplate));
            }

            // Set visibility for each container
            for (int i = 0; i < _storageContainers.Count; i++)
            {
                _storageContainers[i].SetVisibility(i < count);
            }
        }

        /// <summary>
        /// Represents a single storage container in the UI.
        /// </summary>
        [Serializable]
        private sealed class StorageContainer
        {
            [SerializeField, SceneObjectOnly]
            private ItemContainerUI _container;

            [SerializeField, SceneObjectOnly]
            private TextMeshProUGUI _headerText;

            [SerializeField, SceneObjectOnly]
            private SelectableButton _takeAllButton;

            private IInventory _characterInventory;
            private GridLayoutGroup _layoutGroup;
            private int _maxSlotsPerRow;

            public StorageContainer(ItemContainerUI container, TextMeshProUGUI headerText, SelectableButton takeAllButton, IInventory inventory)
            {
                _container = container;
                _headerText = headerText;
                _takeAllButton = takeAllButton;
                _characterInventory = inventory;
                Initialize(inventory);
            }

            public StorageContainer(StorageContainer template)
            {
                // Deeply clone the container to create a new instance
                _container = Instantiate(template._container, template._container.transform.parent);

                // Ensure components are retrieved from the new container, not from the template's references
                var newContainerTrs = _container.transform;
                _headerText = newContainerTrs.GetComponentInChildren<TextMeshProUGUI>();
                _takeAllButton = newContainerTrs.GetComponentInChildren<SelectableButton>();

                Initialize(template._characterInventory);
            }

            public void Initialize(IInventory inventory)
            {
                if (_characterInventory != null)
                    return;

                _characterInventory = inventory;
                _layoutGroup = _container.GetComponent<GridLayoutGroup>();
                _maxSlotsPerRow = _layoutGroup.constraintCount;

                _takeAllButton.Clicked += TakeAllItems;
                _takeAllButton.IsInteractable = false;
            }

            /// <summary>
            /// Sets the visibility of the container UI.
            /// </summary>
            /// <param name="isVisible">Whether the container should be visible.</param>
            public void SetVisibility(bool isVisible)
            {
                _container.gameObject.SetActive(isVisible);
            }

            /// <summary>
            /// Attaches the container to an item container from the workstation.
            /// </summary>
            /// <param name="itemContainer">The item container to attach to.</param>
            public void AttachToContainer(IItemContainer itemContainer)
            {
                _container.AttachToContainer(itemContainer);
                itemContainer.Changed += HandleContainerChange;

                _headerText.text = itemContainer.Name;
                HandleContainerChange();

                _layoutGroup.constraintCount = Mathf.Min(_maxSlotsPerRow, itemContainer.SlotsCount);
                _takeAllButton.gameObject.SetActive(itemContainer.SlotsCount > 1);
            }

            /// <summary>
            /// Detaches the container from the item container.
            /// </summary>
            public void DetachFromContainer()
            {
                _container.Container.Changed -= HandleContainerChange;
                _container.DetachFromContainer();
            }

            /// <summary>
            /// Handles the 'Take All' button functionality, moving all items to the character's inventory.
            /// </summary>
            private void TakeAllItems(SelectableButton buttonSelectable)
            {
                var container = _container.Container;
                foreach (var slot in container.GetSlots())
                    slot.TransferItemToInventory(_characterInventory);
            }

            /// <summary>
            /// Handles changes in the item container, such as when items are added or removed.
            /// </summary>
            private void HandleContainerChange()
            {
                _takeAllButton.IsInteractable = !_container.Container.IsEmpty();
            }
        }
    }
}