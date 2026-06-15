using PolymindGames.InventorySystem;
using PolymindGames.PostProcessing;
using PolymindGames.InputSystem;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Manages the item wheel UI, allowing the player to inspect and select items using a radial menu.
    /// </summary>
    public sealed class ItemWheelUI : CharacterUIBehaviour
    {
        [SerializeField, NotNull]
        private InputContext _inputContext;

        [SerializeField, NotNull]
        private RadialMenuUI _radialMenu;

        [SerializeField, NotNull]
        private ItemInspectorBaseUI _itemInspector;

        [SerializeField, NotNull]
        private ItemContainerUI _itemContainer;

        [SerializeField]
        private VolumeAnimationProfile _inspectEffect;

        private IWieldableInventoryCC _wieldableInventory;
        private IItemContainer _holsterContainer;

        /// <summary>
        /// Gets the radial menu associated with this item wheel.
        /// </summary>
        public RadialMenuUI RadialMenu => _radialMenu;

        /// <summary>
        /// Gets a value indicating whether the item wheel is currently visible.
        /// </summary>
        public bool IsVisible { get; private set; }

        /// <summary>
        /// Starts the item wheel inspection process, displaying the radial menu and setting the selected item.
        /// </summary>
        public void StartWheelInspection()
        {
            if (IsVisible)
                return;

            // Set up input handling and post-processing effects
            InputManager.Instance.PushEscapeCallback(StopWheelInspection);
            InputManager.Instance.PushContext(_inputContext);
            PostProcessingManager.Instance.TryPlayAnimation(this, _inspectEffect);

            // Determine the starting index based on the current or previous selection
            int startingIndex = _wieldableInventory.SelectedIndex == -1
                ? Mathf.Max(_wieldableInventory.PreviousIndex, 0)
                : _wieldableInventory.SelectedIndex;

            _itemContainer.AttachToContainer(_holsterContainer);
            
            // Show the radial menu and select the starting item
            _radialMenu.ShowMenu();
            _radialMenu.SelectAtIndex(startingIndex);

            IsVisible = true;
        }

        /// <summary>
        /// Stops the item wheel inspection process, hiding the radial menu and applying the selected item.
        /// </summary>
        public void StopWheelInspection()
        {
            if (!IsVisible)
                return;

            // Clean up input handling and post-processing effects
            InputManager.Instance.PopEscapeCallback(StopWheelInspection);
            InputManager.Instance.PopContext(_inputContext);
            PostProcessingManager.Instance.CancelAnimation(this, _inspectEffect);

            _itemContainer.DetachFromContainer();
            
            // Apply the selected item and hide the radial menu
            _radialMenu.Highlighted.Select();
            _radialMenu.HideMenu();

            IsVisible = false;
        }

        protected override void OnCharacterAttached(ICharacter character)
        {
            base.OnCharacterAttached(character);
            
            _wieldableInventory = character.GetCC<IWieldableInventoryCC>();
            _radialMenu.SelectedChanged += OnSelectedChanged;
            _radialMenu.HighlightedChanged += OnHighlightedChanged;
            _holsterContainer = character.Inventory.FindContainer(ItemContainerFilters.WithName(_itemContainer.ContainerName));
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            base.OnCharacterDetached(character);
            
            _radialMenu.SelectedChanged -= OnSelectedChanged;
            _radialMenu.HighlightedChanged -= OnHighlightedChanged;
        }

        private void OnSelectedChanged(SelectableButton buttonSelectable)
        {
            int index = _radialMenu.Selectables.IndexOf(buttonSelectable);
            _wieldableInventory.SelectAtIndex(index);
        }

        private void OnHighlightedChanged(SelectableButton buttonSelectable)
        {
            _itemInspector.SetInspectedSlot(buttonSelectable != null ? buttonSelectable.GetComponent<ItemSlotUIBase>() : null);
        }
    }
}