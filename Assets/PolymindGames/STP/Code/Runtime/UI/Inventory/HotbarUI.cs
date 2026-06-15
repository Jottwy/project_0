using PolymindGames.InputSystem;
using UnityEngine.EventSystems;
using System.Collections;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    [DefaultExecutionOrder(ExecutionOrderConstants.AfterDefault1)]
    public sealed class HotbarUI : CharacterUIBehaviour
    {
        private enum HotbarVisibilityMode
        {
            Default,
            Inventory,
            Hidden
        }

        [SerializeField, Range(0f, 100f)]
        [Help("Set the duration (in seconds) for which the holster UI remains visible. A value close to 0 will keep the holster UI permanently active.")]
        private float _holsterVisibleDuration = 5f;

        [SerializeField, NotNull, Title("References")]
        private UIPanel _panel;

        [SerializeField, NotNull]
        private ItemContainerUI _holsterContainer;

        [SerializeField, NotNull]
        private InputContext _defaultInputContext;

        [SerializeField, NotNull]
        private InputContext _inventoryInputContext;

        private IWieldableInventoryCC _wieldableInventory;
        private Coroutine _coroutine;
        private float _disableTime;

        protected override void OnCharacterAttached(ICharacter character)
        {
            base.OnCharacterAttached(character);

            var container = character.Inventory.FindContainer(ItemContainerFilters.WithName(_holsterContainer.ContainerName));
            _holsterContainer.AttachToContainer(container);
            container.Changed += OnContainerChanged;

            _wieldableInventory = character.GetCC<IWieldableInventoryCC>();
            _wieldableInventory.SelectedIndexChanged += OnSelectedWieldableChanged;

            InputManager.Instance.ContextChanged += UpdateVisibilityBasedOnInputContext;

            UpdateVisibilityBasedOnInputContext(InputManager.Instance.ActiveContext);
        }

        protected override void OnCharacterDetached(ICharacter character)
        {
            base.OnCharacterDetached(character);

            if (_holsterContainer.Container != null)
            {
                _holsterContainer.Container.Changed -= OnContainerChanged;
                _holsterContainer.DetachFromContainer();
            }

            _wieldableInventory.SelectedIndexChanged -= OnSelectedWieldableChanged;

            InputManager.Instance.ContextChanged -= UpdateVisibilityBasedOnInputContext;
        }

        /// <summary>
        /// Updates the visibility of the hotbar UI based on the current input context.
        /// </summary>
        /// <param name="context">The current input context.</param>
        private void UpdateVisibilityBasedOnInputContext(InputContext context)
        {
            var visibilityMode = DetermineVisibilityMode(context);
            UpdateHotbarVisibility(visibilityMode);
            
            if (visibilityMode == HotbarVisibilityMode.Default)
            {
                if (EventSystem.current.alreadySelecting)
                    CoroutineUtility.InvokeNextFrame(this, HighlightSelectedSlot);
                else
                    HighlightSelectedSlot();
            }
        }

        /// <summary>
        /// Determines the visibility mode of the hotbar UI based on the provided input context.
        /// </summary>
        /// <param name="context">The current input context.</param>
        /// <returns>The appropriate <see cref="HotbarVisibilityMode"/> for the given context.</returns>
        private HotbarVisibilityMode DetermineVisibilityMode(InputContext context)
        {
            if (context == _defaultInputContext)
            {
                return HotbarVisibilityMode.Default;
            }
            
            if (context == _inventoryInputContext || InputManager.Instance.ContextStack.IndexOf(_inventoryInputContext) != -1)
            {
                return HotbarVisibilityMode.Inventory;
            }

            return HotbarVisibilityMode.Hidden;
        }

        /// <summary>
        /// Highlights the currently selected slot in the hotbar UI based on the selected or previously selected item.
        /// </summary>
        private void HighlightSelectedSlot()
        {
            int selectedIndex = _wieldableInventory.SelectedIndex != -1
                ? _wieldableInventory.SelectedIndex
                : _wieldableInventory.PreviousIndex;

            var selectedSlot = _holsterContainer.ItemSlotsUI[selectedIndex].gameObject;
            EventSystem.current.SetSelectedGameObject(selectedSlot);
        }

        /// <summary>
        /// Handles changes in the selected wieldable item by updating the hotbar visibility based on the active input context.
        /// </summary>
        /// <param name="index">The index of the selected wieldable item.</param>
        private void OnSelectedWieldableChanged(int index) => UpdateVisibilityBasedOnInputContext(InputManager.Instance.ActiveContext);

        /// <summary>
        /// Handles changes in the container by updating the hotbar visibility based on the active input context.
        /// </summary>
        private void OnContainerChanged() => UpdateVisibilityBasedOnInputContext(InputManager.Instance.ActiveContext);

        /// <summary>
        /// Updates the visibility of the hotbar UI based on the provided visibility mode.
        /// </summary>
        /// <param name="mode">The desired <see cref="HotbarVisibilityMode"/> for the hotbar UI.</param>
        private void UpdateHotbarVisibility(HotbarVisibilityMode mode)
        {
            float duration = mode switch
            {
                HotbarVisibilityMode.Default => _holsterVisibleDuration > 0.01f ? _holsterVisibleDuration : float.MaxValue,
                HotbarVisibilityMode.Inventory => float.MaxValue,
                HotbarVisibilityMode.Hidden => 0f,
                _ => 0f
            };

            ShowPanelForDuration(duration);
        }

        /// <summary>
        /// Shows the panel for a specified duration.
        /// </summary>
        /// <param name="time">The duration to show the panel.</param>
        private void ShowPanelForDuration(float time)
        {
            _disableTime = Time.time + time;

            if (_coroutine == null && time > 0.001f)
            {
                _panel.Show();
                _coroutine = StartCoroutine(DisableHolster());
            }
        }

        /// <summary>
        /// Coroutine to disable the holster panel after the specified time.
        /// </summary>
        /// <returns>IEnumerator for the coroutine.</returns>
        private IEnumerator DisableHolster()
        {
            while (Time.time < _disableTime)
                yield return null;

            _coroutine = null;
            _panel.Hide();
        }
    }
}