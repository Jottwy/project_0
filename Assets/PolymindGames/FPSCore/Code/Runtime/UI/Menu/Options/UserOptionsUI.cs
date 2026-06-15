using PolymindGames.Options;
using UnityEngine;

namespace PolymindGames.UserInterface
{
    /// <summary>
    /// Base class for managing user options in the UI, linked to a <see cref="UserOptions{T}"/> instance.
    /// Provides functionality for applying and restoring default settings through UI buttons.
    /// </summary>
    /// <typeparam name="T">The type of the user options being managed.</typeparam>
    [RequireComponent(typeof(UIPanel))]
    public abstract class UserOptionsUI<T> : MonoBehaviour where T : UserOptions<T>
    {
        [SerializeField]
        [Tooltip("Button to restore default settings.")]
        private SelectableButton _restoreDefaultsButton;

        [SerializeField, SpaceArea(0f, 5f)] 
        [Tooltip("Button to apply changes.")]
        private SelectableButton _applyChangesButton;

        private bool _hasPendingChanges;

        /// <summary>
        /// Gets the user options instance being managed.
        /// </summary>
        protected T UserOptions => UserOptions<T>.Instance;

        /// <summary>
        /// Applies changes to the user options.
        /// Override this method to implement custom behavior for applying changes.
        /// </summary>
        protected virtual void ApplyChanges() { }

        /// <summary>
        /// Resets the UI to reflect the current state of the user options.
        /// Override this method to implement custom behavior for resetting UI state.
        /// </summary>
        protected virtual void ResetUIState() { }

        /// <summary>
        /// Marks the user options as having unsaved changes (useful for hooking to events).
        /// </summary>
        protected void MarkDirty<V>(V value)
            => MarkDirty();
        
        /// <summary>
        /// Marks the user options as having unsaved changes.
        /// </summary>
        protected void MarkDirty()
        {
            if (_hasPendingChanges)
                return;

            _hasPendingChanges = true;
            if (_applyChangesButton != null)
                _applyChangesButton.IsInteractable = true;
            
            if (_restoreDefaultsButton != null)
                _restoreDefaultsButton.IsInteractable = true;
        }

        /// <summary>
        /// Initializes the component and subscribes to panel state changes.
        /// </summary>
        protected virtual void Start()
        {
            var panel = GetComponent<UIPanel>();
            panel.PanelStateChanged += HandlePanelStateChanged;
        }

        /// <summary>
        /// Handles panel state changes to reset UI or connect buttons when enabled/disabled.
        /// </summary>
        /// <param name="isPanelEnabled">True if the panel is enabled, otherwise false.</param>
        private void HandlePanelStateChanged(bool isPanelEnabled)
        {
            if (isPanelEnabled)
                ResetUIState();
            else if (_applyChangesButton == null)
                UserOptions.Save();

            _hasPendingChanges = false;
            ConfigureApplyChangesButton(isPanelEnabled);
            ConfigureRestoreDefaultsButton(isPanelEnabled);
        }

        /// <summary>
        /// Configures the apply changes button's visibility and interactions.
        /// </summary>
        /// <param name="isEnabled">True to enable the button, otherwise false.</param>
        private void ConfigureApplyChangesButton(bool isEnabled)
        {
            if (_applyChangesButton == null)
                return;

            _applyChangesButton.gameObject.SetActive(isEnabled);
            _applyChangesButton.IsInteractable = _hasPendingChanges;

            if (isEnabled)
                _applyChangesButton.Clicked += ApplyChangesAndSave;
            else
                _applyChangesButton.Clicked -= ApplyChangesAndSave;

            void ApplyChangesAndSave(SelectableButton button)
            {
                if (!_hasPendingChanges)
                    return;

                ApplyChanges();
                UserOptions.Save();
                _hasPendingChanges = false;
                _applyChangesButton.IsInteractable = false;
            }
        }

        /// <summary>
        /// Configures the restore defaults button's visibility and interactions.
        /// </summary>
        /// <param name="isEnabled">True to enable the button, otherwise false.</param>
        private void ConfigureRestoreDefaultsButton(bool isEnabled)
        {
            if (_restoreDefaultsButton == null)
                return;

            // _restoreDefaultsButton.IsInteractable = false;

            if (isEnabled)
                _restoreDefaultsButton.Clicked += RestoreDefaults;
            else
                _restoreDefaultsButton.Clicked -= RestoreDefaults;

            void RestoreDefaults(SelectableButton button)
            {
                _hasPendingChanges = false;

                if (_applyChangesButton != null)
                    _applyChangesButton.IsInteractable = false;

                if (_restoreDefaultsButton != null)
                    _restoreDefaultsButton.IsInteractable = false;

                UserOptions.RestoreDefaults();
                ResetUIState();
            }
        }
    }
}